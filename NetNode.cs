using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using DeadCellsMultiplayerMod;
using HaxeProxy.Runtime;
using Serilog;
using dc;
using System.Collections.Immutable;
using StreamJsonRpc;
using DeadCellsMultiplayerMod.Rpc;
using DeadCellsMultiplayerMod.Server;
using DeadCellsMultiplayerMod.Client;

public enum NetRole { None, Host, Client }

class NetNode : IDisposable
{
    private readonly ILogger _log;
    private readonly NetRole _role;

    private TcpListener? _listener;   // host
    private TcpClient?   _client;     // client OR accepted

    private readonly IPEndPoint _bindEp;   // host bind
    private readonly IPEndPoint _destEp;   // client connect

    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    private JsonRpc? _rpc;
    public GameClient? Client { get; set; }
    public GameServer? Server { get; set; }
    public string GUID { get; set; } = Guid.NewGuid().ToString();
    public ISyncHostActions SyncHost { get; private set; } = null!;
    public ISyncClientActions SyncClient { get; private set; } = null!;
    public ILogger Logger => _log;
    public bool HasRemote { get; set; }
    public bool IsAlive =>
        (_role == NetRole.Host && _listener != null) ||
        (_role == NetRole.Client && _client   != null);
    public bool IsHost => _role == NetRole.Host;

    // Новое свойство для реального адреса хоста
    public IPEndPoint? ListenerEndpoint =>
        _listener != null ? (IPEndPoint?)_listener.LocalEndpoint : null;

    public static NetNode CreateHost(ILogger log, IPEndPoint ep)  => new(log, NetRole.Host,  ep);
    public static NetNode CreateClient(ILogger log, IPEndPoint ep)=> new(log, NetRole.Client, ep);

    private NetNode(ILogger log, NetRole role, IPEndPoint ep)
    {
        _log  = log;
        _role = role;

        if (role == NetRole.Host)
        {
            // только loopback
            _bindEp = ep;
            _destEp = new IPEndPoint(IPAddress.None, 0);
            StartHost();
        }
        else
        {
            _destEp = ep; // 127.0.0.1:XXXX из server.txt
            _bindEp = new IPEndPoint(IPAddress.None, 0);
            StartClient();
        }
    }

    // ================= HOST =================
    private void StartHost()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(_bindEp);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();

            var lep = (IPEndPoint)_listener.LocalEndpoint;

            // ВАЖНО: логируем реальный адрес слушателя
            _log.Information("[NetNode] Host started OK. Bound to {0}:{1}", lep.Address, lep.Port);

            _acceptTask = Task.Run(() => AcceptLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            _log.Error("[NetNode] Host start failed: {msg}", ex.Message);
            Dispose();
            throw;
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                var tcp = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                tcp.NoDelay = true;
                _client = tcp;

                SyncClient = JsonRpc.Attach<ISyncClientActions>(tcp.GetStream());
                SyncHost = new SyncHostActions(this);
                _rpc = JsonRpc.Attach(tcp.GetStream(), SyncHost);
                _rpc.SynchronizationContext = ModCore.Modules.Game.SynchronizationContext;

                _log.Information("[NetNode] Host accepted {ep}", tcp.Client.RemoteEndPoint);

                await SyncClient.Print("Welcome");

                if (_role == NetRole.Host && GameMenu.TryGetHostRunSeed(out var hostSeed))
                {
                    await SyncClient.SetSeed(hostSeed);
                }

                HasRemote = true;
                GameMenu.NotifyRemoteConnected(_role);

                break;
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] AcceptLoop error: {msg}", ex.Message);
        }
    }

    // ================= CLIENT =================
    private void StartClient()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectWithRetryAsync(_cts.Token));
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _log.Information("[NetNode] Client connecting to {dest}", _destEp);

                var tcp = new TcpClient(AddressFamily.InterNetwork);
                tcp.NoDelay = true;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

                await tcp.ConnectAsync(_destEp.Address, _destEp.Port, timeoutCts.Token).ConfigureAwait(false);
                _client = tcp;

                SyncHost = JsonRpc.Attach<ISyncHostActions>(tcp.GetStream());
                SyncClient = new SyncClientActions(this);
                _rpc = JsonRpc.Attach(tcp.GetStream(), SyncClient);
                _rpc.SynchronizationContext = ModCore.Modules.Game.SynchronizationContext;

                _log.Information("[NetNode] Client connected to {dest}", _destEp);

                await SyncHost.Print("Hello, World!");

                HasRemote = true;
                GameMenu.NotifyRemoteConnected(_role);
                return;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Warning("[NetNode] Client connect error: {msg}", ex.Message);
                await Task.Delay(3000, ct).ConfigureAwait(false);
            }
        }
    }

    // ============== COMMON IO ==============
    private async Task RecvLoop(CancellationToken ct)
    {
        var buf = new byte[2048];
        var sb  = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var stream = _stream;
                if (stream == null) break;

                int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
                if (n <= 0) break;

                sb.Append(Encoding.UTF8.GetString(buf, 0, n));

                while (true)
                {
                    var text = sb.ToString();
                    int idx = text.IndexOf('\n');
                    if (idx < 0) break;

                    var line = text[..idx].Trim();
                    sb.Remove(0, idx + 1);
                    if (line.Length == 0) continue;


                    if (line.StartsWith("WELCOME"))
                    {
                        lock (_sync) _hasRemote = true;
                        continue;
                    }

                    if (line.StartsWith("HELLO"))
                    {
                        lock (_sync) _hasRemote = true;
                        continue;
                    }

                    if (line.StartsWith("BRDATA|", StringComparison.OrdinalIgnoreCase))
                    {
                        var payload = line["BRDATA|".Length..];
                        lock (_sync) _hasRemote = true;
                        try
                        {
                            GameDataSync.ReceiveBrData(payload);
                        }
                        catch (Exception ex)
                        {
                            _log.Warning("[NetNode] Failed to handle BRDATA: {msg}", ex.Message);
                        }
                        continue;
                    }

                    if (line.StartsWith("SEED|"))
                    {
                        var partsSeed = line.Split('|');
                        if (partsSeed.Length >= 2 && int.TryParse(partsSeed[1], out var hostSeed))
                        {
                            lock (_sync) _hasRemote = true;
                            GameMenu.ReceiveHostRunSeed(hostSeed);
                            _log.Information("[NetNode] Received host run seed {Seed}", hostSeed);
                        }
                        else
                        {
                            _log.Warning("[NetNode] Malformed SEED line: \"{line}\"");
                        }
                        continue;
                    }

                    if (line.StartsWith("RUNPARAMS|"))
                    {
                        var payload = line["RUNPARAMS|".Length..];
                        lock (_sync) _hasRemote = true;
                        GameMenu.ReceiveRunParams(payload);
                        continue;
                    }

                    if (line.StartsWith("USER|"))
                    {
                        var payload = line["USER|".Length..];
                        lock (_sync) _hasRemote = true;
                        GameMenu.ReceiveRemoteUsername(payload);
                        continue;
                    }

                    if (line.StartsWith("LDESC|"))
                    {
                        var payload = line["LDESC|".Length..];
                        lock (_sync) _hasRemote = true;
                        GameMenu.ReceiveLevelDesc(payload);
                        continue;
                    }

                    if (line.StartsWith("GEN|"))
                    {
                        var payload = line["GEN|".Length..];
                        lock (_sync) _hasRemote = true;
                        GameMenu.ReceiveGeneratePayload(payload);
                        continue;
                    }

                    if (line.StartsWith("LEVEL|", StringComparison.OrdinalIgnoreCase))
                    {
                        var payload = line[(line.IndexOf('|') + 1)..];
                        lock (_sync)
                        {
                            _hasRemote = true;
                            _remoteLevelId = payload;
                        }
                        continue;
                    }

                    if (line.StartsWith("ANIM|", StringComparison.OrdinalIgnoreCase))
                    {
                        var payload = line[(line.IndexOf('|') + 1)..];
                        var partsAnim = payload.Split('|');
                        string animName = partsAnim.Length >= 1 ? partsAnim[0] : string.Empty;
                        int? q = null;
                        bool? gFlag = null;
                        if (partsAnim.Length >= 2 && int.TryParse(partsAnim[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedQ))
                            q = parsedQ;
                        if (partsAnim.Length >= 3 && TryParseBool(partsAnim[2], out var parsedBool))
                            gFlag = parsedBool;
                        lock (_sync)
                        {
                            _hasRemote = true;
                            _remoteAnim = animName;
                            _remoteAnimQueue = q;
                            _remoteAnimG = gFlag;
                            _hasRemoteAnim = true;
                        }
                        continue;
                    }

                    if (line.StartsWith("KICK"))
                    {
                        GameMenu.NotifyRemoteDisconnected(_role);
                        break;
                    }

                    var parts = line.Split('|');
                    if (parts.Length == 2 &&
                        double.TryParse(parts[0], out var cx) &&
                        double.TryParse(parts[1], out var cy))
                    {
                        lock (_sync)
                        {
                            _rx = cx; _ry = cy; _hasRemote = true;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
                _log.Warning("[NetNode] RecvLoop error: {msg}", ex.Message);
        }
        finally
        {
            lock (_sync)
            {
                _hasRemote = false;
                _remoteLevelId = null;
                _remoteAnim = null;
                _remoteAnimQueue = null;
                _remoteAnimG = null;
                _hasRemoteAnim = false;
            }
            GameMenu.NotifyRemoteDisconnected(_role);
        }
    }

    public void LevelSend(string lvl) => SendLevelId(lvl);


    public void SendUsername(string username)
    {
        if (_stream == null || _client == null || !_client.Connected)
        {
            _log.Information("[NetNode] Skip sending username: no connected client");
            return;
        }

        var safe = (username ?? "guest").Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (safe.Length == 0) safe = "guest";

        SendRaw("USER|" + safe);
        _log.Information("[NetNode] Sent username {Username}", safe);
    }

    public void SendRunParams(string json)
    {
        if (_stream == null || _client == null || !_client.Connected)
        {
            _log.Information("[NetNode] Skip sending run params: no connected client");
            return;
        }

        SendRaw("RUNPARAMS|" + json);
        _log.Information("[NetNode] Sent run params payload");
    }

    public void SendLevelDesc(string json)
    {
        if (_stream == null || _client == null || !_client.Connected)
        {
            _log.Information("[NetNode] Skip sending level desc: no connected client");
            return;
        }

        SendRaw("LDESC|" + json);
        _log.Information("[NetNode] Sent LevelDesc payload");
    }

    public void SendGeneratePayload(string json)
    {
        if (_stream == null || _client == null || !_client.Connected)
        {
            _log.Information("[NetNode] Skip sending generate payload: no connected client");
            return;
        }

        SendRaw("GEN|" + json);
        _log.Information("[NetNode] Sent Generate payload ({Length} bytes)", json.Length);
    }

    public void SendLevelId(string levelId)
    {
        if (_stream == null || _client == null || !_client.Connected)
        {
            _log.Information("[NetNode] Skip sending level id: no connected client");
            return;
        }

        var safe = levelId.Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        SendRaw("LEVEL|" + safe);
    }
   
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _client?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _sendLock.Dispose(); } catch { }
    }
}
