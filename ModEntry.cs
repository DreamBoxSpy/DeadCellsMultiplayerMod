using Hashlink.Proxy.DynamicAccess;
using HaxeProxy.Runtime;
using ModCore.Events.Interfaces.Game;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Mods;
using ModCore.Modules;
using ModCore.Events.Interfaces.Game.Save;
using Serilog;
using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Linq;
using Newtonsoft.Json;

namespace DeadCellsMultiplayerMod
{
    public class ModEntry(ModInfo info) : ModBase(info),
        IOnGameEndInit,
        IOnHeroInit,
        IOnHeroUpdate,
        IOnFrameUpdate,
        IOnAfterLoadingSave
    {
        private static byte[]? _pendingGameData;
        private static GameDataSync? _cachedGameDataSync;
        private const int VK_F5 = 0x74; // Host
        private const int VK_F6 = 0x75; // Client
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

        private bool _ready;
        private bool _hotkeysEnabled = true;

        private HaxeObject? _heroRef;
        private object? _lastLevelRef;
        private object? _lastGameRef;

        private CompanionController? _companion;

        private NetRole _netRole = NetRole.None;
        private NetNode? _net;

        private bool _prevF5, _prevF6;
        private double _hotkeyCd;
        private const double HotkeyCooldown = 0.25;

        private int _lastSentCx = int.MinValue;
        private int _lastSentCy = int.MinValue;
        private double _lastSentXr = double.NaN;
        private double _lastSentYr = double.NaN;

        private double _accum;
        private bool _initialGhostSpawned;
        private double _ghostLogAccum;
        private const double GhostLogInterval = 5.0;
        public override void Initialize()
        {
            Logger.Information("[NetMod] Initialized");
            GameMenu.Initialize(Logger);
        }

        public void OnGameEndInit()
        {
            _ready = true;
            GameMenu.AllowGameDataHooks = false;
            GameMenu.SetRole(NetRole.None);
            var seed = GameMenu.ForceGenerateServerSeed("OnGameEndInit");
            Logger.Information("[NetMod] GameEndInit - ready (F5 host / F6 client) seed={Seed}", seed);
        }


        public void OnAfterLoadingSave(dc.User data)
        {
            try
            {
                var gd = TryGetGameData(data);
                if (gd == null) return;
                var dto = GameMenu.BuildGameDataSync(gd);
                _cachedGameDataSync = dto;
                GameMenu.CacheGameDataSync(dto);
                Logger.Information("[NetMod] Full GameData captured after save load");
            }
            catch (Exception ex)
            {
                Logger.Error("OnAfterLoadingSave failed: " + ex);
            }
        }

        public void OnHeroInit()
        {
            GameMenu.AllowGameDataHooks = true;
            GameMenu.MarkInRun();
            try
            {
                var gm = Game.Instance;
                _heroRef = gm?.HeroInstance;
                _lastGameRef = gm;
                _initialGhostSpawned = false;
                _companion = new CompanionController(Logger);

                if (_heroRef != null)
                {
                    string? heroTypeStr = null;
                    try
                    {
                        dynamic h = DynamicAccessUtils.AsDynamic(_heroRef);
                        try { heroTypeStr = (string?)h.type; } catch { }
                        // У героя есть только _level, не level
                        try { _lastLevelRef = (object?)h._level; } catch { }
                        
                        // Получаем game из уровня или используем Game.Instance
                        _lastGameRef = ExtractGameFromLevel(_lastLevelRef) ?? Game.Instance ?? _lastGameRef;
                    }
                    catch { }

                    heroTypeStr ??= _heroRef.GetType().FullName;
                    Logger.Information("[NetMod] Hero captured (type={Type})", heroTypeStr ?? "unknown");
                    TryInitialGhostSpawn();
                }
                else
                {
                    Logger.Information("[NetMod] Hero is null");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[NetMod] OnHeroInit error: {ex.Message}");
            }
        }

        public void OnHeroUpdate(double dt)
        {
            if (!_ready || _heroRef == null) return;

            var gm = Game.Instance;
            if (gm != null)
                _lastGameRef = gm;

            // шлём ≈10 Гц
            _accum += dt;
            if (_accum < 0.1) return;
            _accum = 0;

            int cx = 0, cy = 0;
            double xr = 0, yr = 0;
            object? levelObj = null;

            try
            {
                dynamic hero = DynamicAccessUtils.AsDynamic(_heroRef);
                cx = (int)hero.cx;
                cy = (int)hero.cy;
                xr = hero.xr;
                yr = hero.yr;

                levelObj = (object?)hero._level;
                if (!ReferenceEquals(levelObj, _lastLevelRef))
                {
                    _lastLevelRef = levelObj;
                    _companion?.ResetSpawnState();
                    _initialGhostSpawned = false;
                    Logger.Information("[NetMod] Level changed -> reset ghost");
                }

                // Получаем game из уровня или используем Game.Instance
                var gameObj = ExtractGameFromLevel(levelObj) ?? Game.Instance;
                if (gameObj != null)
                    _lastGameRef = gameObj;
            }
            catch (Exception ex)
            {
                // Логируем ошибку для отладки, но не падаем
                Logger.Warning($"[NetMod] OnHeroUpdate error: {ex.Message}");
                return;
            }

            TryInitialGhostSpawn();
            _ghostLogAccum += dt;
            if (_companion != null && _ghostLogAccum >= GhostLogInterval)
            {
                _ghostLogAccum = 0;
                _companion.TryLogGhostPosition();
            }

            // сеть: отправка только при изменениях
            if (_net != null && _net.IsAlive && _netRole != NetRole.None)
            {
                if (cx != _lastSentCx || cy != _lastSentCy || xr != _lastSentXr || yr != _lastSentYr)
                {
                    _lastSentCx = cx;
                    _lastSentCy = cy;
                    _lastSentXr = xr;
                    _lastSentYr = yr;
                    _net.TickSend(cx, cy, xr, yr);
                }
            }

            // появление удалённых координат — это триггер спавна призрака
            if (_net == null || !_net.IsAlive || _netRole == NetRole.None) return;
            if (!_net.TryGetRemote(out var rcx, out var rcy, out var rxr, out var ryr)) return;
            if (rcx < 0 || rcy < 0) return;

            _companion ??= new CompanionController(Logger);

            // спавним РАЗ на текущем уровне
            if (!_companion.IsSpawned)
            {
                if (_lastLevelRef != null && _lastGameRef != null && _heroRef != null)
                    _companion.EnsureSpawned(_heroRef, _lastLevelRef, _lastGameRef, cx, cy);
            }

            // и обновляем позицию
            _companion?.TeleportTo(rcx, rcy, rxr, ryr);
        }

        public static void OnClientReceiveGameData(byte[] bytes)
        {
            _pendingGameData = bytes;
            Log.Information("[NetMod] Client received GameData blob ({0} bytes)", bytes.Length);
            ApplyGameDataBytes(bytes);
        }

        public static void ApplyGameDataBytes(byte[] data)
        {
            try
            {
                var packet = new
                {
                    payload = string.Empty,
                    hx = Convert.ToBase64String(data),
                    extra = (object?)null,
                    hxbit = true
                };

                var json = Newtonsoft.Json.JsonConvert.SerializeObject(packet);
                var obj = HaxeProxySerializer.Deserialize<dc.tool.GameData>(json);
                if (obj != null)
                {
                    GameMenu.ApplyFullGameData(obj);
                    Log.Information("[NetMod] Applied GameData binary sync.");
                }
            }
            catch (Exception ex)
            {
                Log.Error("[NetMod] ApplyGameDataBytes failed: {Ex}", ex);
            }
        }


        // GameDataSync helpers removed (using full hxbit path)

        public void OnFrameUpdate(double dt)
        {
            if (!_ready) return;

            if (_hotkeysEnabled)
            {
                _hotkeyCd -= dt;
                if (_hotkeyCd < 0) _hotkeyCd = 0;

                bool f5Now = (GetAsyncKeyState(VK_F5) & 0x8000) != 0;
                bool f6Now = (GetAsyncKeyState(VK_F6) & 0x8000) != 0;
                bool f5Edge = f5Now && !_prevF5 && _hotkeyCd <= 0;
                bool f6Edge = f6Now && !_prevF6 && _hotkeyCd <= 0;

                if (f5Edge)
                {
                    Logger.Information("[NetMod] F5 (Host) pressed");
                    TryStartHost();
                    _hotkeyCd = HotkeyCooldown;
                    _hotkeysEnabled = false;
                }
                if (f6Edge)
                {
                    Logger.Information("[NetMod] F6 (Client) pressed");
                    TryStartClient();
                    _hotkeyCd = HotkeyCooldown;
                    _hotkeysEnabled = false;
                }

                _prevF5 = f5Now;
                _prevF6 = f6Now;
            }
        }

        private IPEndPoint GetHostFromFile()
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var root = Directory.GetParent(baseDir)?.Parent?.Parent?.Parent?.FullName ?? baseDir;
                var path = Path.Combine(root, "mods", "DeadCellsMultiplayerMod", "server.txt");
                Log.Information(path);
                if (!File.Exists(path)) return new IPEndPoint(IPAddress.Loopback, 1234);

                var parts = File.ReadAllText(path).Trim().Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) return new IPEndPoint(IPAddress.Loopback, 1234);

                if (!IPAddress.TryParse(parts[0], out var ip)) ip = IPAddress.Loopback;
                if (!int.TryParse(parts[1], out var port)) port = 1234;

                return new IPEndPoint(ip, port);
            }
            catch
            {
                return new IPEndPoint(IPAddress.Loopback, 1234);
            }
        }

        private void TryStartHost()
        {
            try
            {
                _net?.Dispose();
                var ep = GetHostFromFile();

                _net = NetNode.CreateHost(Logger, ep);
                _netRole = NetRole.Host;
                GameMenu.SetRole(_netRole);
                GameMenu.NetRef = _net;

                var lep = _net.ListenerEndpoint;
                if (lep != null)
                    Logger.Information($"[NetMod] Host listening at {lep.Address}:{lep.Port}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[NetMod] Host start failed: {ex.Message}");
                _netRole = NetRole.None;
                _net = null;
                _hotkeysEnabled = true;
                GameMenu.SetRole(_netRole);
            }
        }

        private void TryStartClient()
        {
            try
            {
                _net?.Dispose();
                var ep = GetHostFromFile();

                _net = NetNode.CreateClient(Logger, ep);
                _netRole = NetRole.Client;
                GameMenu.SetRole(_netRole);
                GameMenu.NetRef = _net;

                Logger.Information($"[NetMod] Client connecting to {ep.Address}:{ep.Port}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[NetMod] Client start failed: {ex.Message}");
                _netRole = NetRole.None;
                _net = null;
                _hotkeysEnabled = true;
                GameMenu.SetRole(_netRole);
            }
        }

        private static object? ExtractGameFromLevel(object? levelObj)
        {
            if (levelObj == null) return null;
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = levelObj.GetType();
            return type.GetProperty("game", Flags)?.GetValue(levelObj) ??
                   type.GetField("game", Flags)?.GetValue(levelObj);
        }

        private void TryInitialGhostSpawn()
        {
            if (_initialGhostSpawned) return;
            if (_heroRef == null || _lastLevelRef == null || _lastGameRef == null) return;

            try
            {
                dynamic h = DynamicAccessUtils.AsDynamic(_heroRef);
                int cx = 0, cy = 0;
                double xr = 0.5, yr = 1;
                try { cx = (int)h.cx; } catch { }
                try { cy = (int)h.cy; } catch { }
                try { xr = (double)h.xr; } catch { }
                try { yr = (double)h.yr; } catch { }
                if (cx < 0 || cy < 0) return;

                _companion ??= new CompanionController(Logger);
                _companion.EnsureSpawned(_heroRef, _lastLevelRef, _lastGameRef, cx, cy);
                if (_companion.IsSpawned)
                {
                    _initialGhostSpawned = true;
                    Logger.Information("[NetMod] Initial ghost spawned at {Cx},{Cy}", cx, cy);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("[NetMod] Initial ghost spawn failed: {Message}", ex.Message);
            }
        }

        private static dc.tool.GameData? TryGetGameData(dc.User data)
        {
            try
            {
                dynamic d = DynamicAccessUtils.AsDynamic(data);
                var gd = d?.gameData as dc.tool.GameData;
                if (gd != null) return gd;
            }
            catch { }

            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var type = data.GetType();
                var prop = type.GetProperty("gameData", flags);
                if (prop != null)
                {
                    var gd = prop.GetValue(data) as dc.tool.GameData;
                    if (gd != null) return gd;
                }

                var field = type.GetField("gameData", flags);
                if (field != null)
                {
                    var gd = field.GetValue(data) as dc.tool.GameData;
                    if (gd != null) return gd;
                }
            }
            catch { }

            return null;
        }
    }
}
