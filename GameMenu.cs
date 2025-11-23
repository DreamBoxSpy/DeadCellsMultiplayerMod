using System;
using System.Reflection;
using Hashlink.Proxy.DynamicAccess;
using MonoMod.RuntimeDetour;
using ModCore.Modules;
using Serilog;
using dc;

namespace DeadCellsMultiplayerMod
{
    internal static class GameMenu
    {
        private static readonly object Sync = new();
        private static Hook? _newGameHook;
        private static ILogger? _log;
        private static NetRole _netRole;
        private static int? _remoteHostSeed;
        private static int? _localSeed;
        private static Action<int>? _hostSeedBroadcaster;

        private delegate void UserNewGameOrig(User self, int seed, object level, bool useTwitch, bool custom, LaunchMode mode);

        public static void Initialize(ILogger logger)
        {
            lock (Sync)
            {
                _log ??= logger;
                if (_newGameHook != null)
                    return;

                var userType = typeof(User);
                var methods = userType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var m in methods)
                {
                    if (m.Name == "newGame")
                        _log?.Information("[NetMod] newGame candidate: {Signature}", DescribeMethod(m));
                }

                var target = userType.GetMethod(
                    "newGame",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(int), typeof(object), typeof(bool), typeof(bool), typeof(LaunchMode) },
                    modifiers: null)
                    ?? userType.GetMethod(
                        "newGame",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var detour = typeof(GameMenu).GetMethod(
                    nameof(NewGameRedirect),
                    BindingFlags.Static | BindingFlags.NonPublic);

                if (target == null || detour == null)
                {
                    _log?.Warning("[NetMod] Failed to install newGame hook (missing target or detour)");
                    return;
                }

                try
                {
                    _newGameHook = new Hook(target, detour);
                    _newGameHook.Apply();
                    _log?.Information("[NetMod] Hooked User.newGame -> GameMenu.NewGameRedirect");
                }
                catch (Exception ex)
                {
                    _log?.Error("[NetMod] Failed to hook newGame: {Message}", ex.Message);
                    _newGameHook = null;
                }
            }
        }

        public static void SetNetRole(NetRole role)
        {
            lock (Sync)
            {
                _netRole = role;
                if (role != NetRole.Client)
                    _remoteHostSeed = null;
                if (role != NetRole.Host)
                    _hostSeedBroadcaster = null;
            }
        }

        public static void SetHostSeedBroadcaster(Action<int>? broadcaster)
        {
            lock (Sync)
            {
                _hostSeedBroadcaster = broadcaster;
            }
        }

        public static void SetHostSeedFromRemote(int seed)
        {
            lock (Sync)
            {
                _remoteHostSeed = seed;
                _localSeed ??= seed;
            }
            _log?.Information("[NetMod] Received host seed {Seed}", seed);
        }

        public static int GetWelcomeSeed()
        {
            int? seed;
            lock (Sync)
            {
                seed = _localSeed;
            }

            seed ??= TryReadSeedFromGame();
            if (seed.HasValue)
            {
                lock (Sync)
                {
                    _localSeed = seed;
                }
                return seed.Value;
            }

            var fallback = Environment.TickCount;
            lock (Sync)
            {
                _localSeed = fallback;
            }
            return fallback;
        }

        public static bool TryGetKnownSeed(out int seed)
        {
            lock (Sync)
            {
                if (_localSeed.HasValue)
                {
                    seed = _localSeed.Value;
                    return true;
                }
            }

            var current = TryReadSeedFromGame();
            if (current.HasValue)
            {
                lock (Sync)
                {
                    _localSeed ??= current.Value;
                }
                seed = current.Value;
                return true;
            }

            seed = default;
            return false;
        }

        private static void NewGameRedirect(UserNewGameOrig orig, User self, int seed, object level, bool useTwitch, bool custom, LaunchMode mode)
        {
            _log?.Information("[NetMod] NewGameRedirect entered (seed={Seed}, levelType={LevelType}, role={Role}, hostSeedKnown={HasSeed})",
                seed, level?.GetType().FullName ?? "null", _netRole, _remoteHostSeed.HasValue);
            lock (Sync)
            {
                if (_netRole == NetRole.Client && !_remoteHostSeed.HasValue)
                {
                    _log?.Warning("[NetMod] Host seed not received yet; blocking NewGame on client");
                    return;
                }
            }

            var finalSeed = seed;
            lock (Sync)
            {
                if (_netRole == NetRole.Client && _remoteHostSeed.HasValue)
                    finalSeed = _remoteHostSeed.Value;
                _localSeed = finalSeed;
            }

            _log?.Information("[NetMod] newGame seed => {Seed} (orig {Original}, role {Role}, hostSeedKnown={HostSeed})",
                finalSeed, seed, _netRole, _remoteHostSeed.HasValue);

            Action<int>? broadcaster = null;
            lock (Sync)
            {
                if (_netRole == NetRole.Host)
                    broadcaster = _hostSeedBroadcaster;
            }

            broadcaster?.Invoke(finalSeed);
            orig(self, finalSeed, level, useTwitch, custom, mode);
        }

        private static int? TryReadSeedFromGame()
        {
            try
            {
                var gm = Game.Instance;
                if (gm == null)
                    return null;

                dynamic dynGame = DynamicAccessUtils.AsDynamic(gm);
                object? data = null;
                try { data = (object?)dynGame.data; } catch { }
                if (data == null)
                    return null;

                try
                {
                    dynamic dynData = DynamicAccessUtils.AsDynamic(data);
                    return (int)dynData.gameSeed;
                }
                catch { }

                const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var prop = data.GetType().GetProperty("gameSeed", Flags);
                if (prop?.CanRead == true && prop.GetValue(data) is int propSeed)
                    return propSeed;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to read current seed: {Message}", ex.Message);
            }

            return null;
        }

        private static string DescribeMethod(MethodInfo m)
        {
            var p = m.GetParameters();
            var ps = string.Join(", ", p.Select(x => $"{x.ParameterType.FullName} {x.Name}"));
            return $"{m.DeclaringType?.FullName}.{m.Name}({ps})";
        }
    }
}
