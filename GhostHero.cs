using System;
using System.Linq;
using System.Reflection;
using System.Linq.Expressions;
using Hashlink.Proxy.DynamicAccess;
using System.Runtime.CompilerServices;
using Serilog;

namespace DeadCellsMultiplayerMod
{
    public sealed class HeroGhost
    {
        private readonly ILogger _log;

        private object? _ghost;
        private object? _levelRef;
        private object? _gameRef;

        private string? _forcedHeroType;
        private MethodInfo? _setPosCase;
        private MethodInfo? _setPos;
        private MethodInfo? _safeTpTo;
        private bool _teleportDisabled;
        private bool _teleportWarningLogged;
        private bool _registeredInLevel;
        private DateTime _lastCoordLog = DateTime.MinValue;
        private static readonly HashSet<string> LoggedLevelMembers = new();
        private bool _spriteOffsetApplied;

        public HeroGhost(ILogger log) => _log = log;

        public bool IsSpawned => _ghost != null;
        public bool HasEntityType => true;

        private static void LogCatch(Exception ex, [CallerMemberName] string? member = null)
        {
            Log.Warning("[HeroGhost] {Member} exception: {Message} ({Detail})", member ?? "unknown", ex.Message, ex.ToString());
        }

        public bool TrySetEntityType(string? typeName)
        {
            _forcedHeroType = string.IsNullOrWhiteSpace(typeName) ? null : typeName;
            return true;
        }

        public void FindSuitableEntityType(object? heroRef)
        {
            // Compatibility shim: we already force a neutral entity type for the ghost.
        }

        public bool Spawn(object heroRef, object? levelHint, object? gameHint, int spawnCx, int spawnCy)
        {
            if (_ghost != null)
                return true;
            if (heroRef == null)
                return false;

            if (!TryResolveContext(heroRef, levelHint, gameHint, out var levelObj, out var gameObj))
            {
                _log.Warning("[HeroGhost] Spawn failed - unable to capture level/game");
                return false;
            }

            // Force base Entity (dc.Entity) to keep things simple/neutral.
            var heroTypeString = "dc.Entity";
            var coords = ExtractCoordsFromHero(heroRef);
            // Always prefer live hero coords so we don't end up at -1/-1
            spawnCx = coords.cx;
            // Horizontal offset for ghost spawn
            spawnCx += 3;
            spawnCy = coords.cy;

            var preferredNames = new[]
            {
                "dc.Entity",
                "en.Entity"
            };

            var heroClass = ResolveType(preferredNames, new[]
                {
                    gameObj.GetType().Assembly,
                    levelObj.GetType().Assembly,
                    heroRef.GetType().Assembly
                }) ??
                ResolveType(preferredNames, AppDomain.CurrentDomain.GetAssemblies()) ??
                FindEntityClassByName(heroTypeString, gameObj.GetType().Assembly) ??
                FindEntityClassByName(heroTypeString, levelObj.GetType().Assembly) ??
                FindEntityClassByName(heroTypeString, heroRef.GetType().Assembly) ??
                FindEntityClass(gameObj.GetType().Assembly) ??
                FindEntityClass(levelObj.GetType().Assembly) ??
                FindEntityClass(heroRef.GetType().Assembly) ??
                FindEntityClass(AppDomain.CurrentDomain.GetAssemblies());

            if (heroClass == null)
            {
                _log.Error("[HeroGhost] No suitable entity type found (tried Homunculus/Entity/Hero variants)");
                return false;
            }

            try
            {
                _ghost = CreateGhostInstance(heroClass, heroRef, gameObj, levelObj, spawnCx, spawnCy, heroTypeString);
                if (_ghost == null)
                {
                    _log.Error("[HeroGhost] Failed to construct ghost (even via fallbacks)");
                    return false;
                }

                _levelRef = levelObj;
                _gameRef = gameObj;
                SetHeroType(_ghost, heroTypeString);
                EnsureContextFields(_ghost, levelObj, gameObj);
                if (_ghost != null) LogPointerInfo(_ghost, warnOnNull: true);
                TryCopyController(heroRef, _ghost!);
                TryApplyEntitySetup(heroRef, _ghost!, levelObj);
                TryInvokeLifecycle(_ghost!);
                var registered = TryRegisterInLevel(levelObj, _ghost!);
                var placed = TryPlaceGhost(_ghost!, spawnCx, spawnCy, coords.xr, coords.yr, suppressWarnings: true);
                if (!placed)
                    placed = ForceSetCoords(_ghost!, spawnCx, spawnCy, coords.xr, coords.yr, suppressWarnings: false);
                if (placed)
                    TryRefreshSpritePos(_ghost!);

                var haveCoords = ExtractCoordsFromObject(_ghost!, out var gcx, out var gcy, out var gxr, out var gyr);
                _log.Information("[HeroGhost] Spawned ghost via {HeroClass} (type={Type}) at {Cx},{Cy} registered={Registered} (ghost now at {PX},{PY},{PXR},{PYR})", heroClass.FullName, heroTypeString, spawnCx, spawnCy, registered, haveCoords ? gcx : -1, haveCoords ? gcy : -1, haveCoords ? gxr : -1, haveCoords ? gyr : -1);
                return true;
            }
            catch (TargetInvocationException tex)
            {
                _log.Error("[HeroGhost] Spawn failed: {Message}", tex.InnerException?.Message ?? tex.Message);
                Reset();
                return false;
            }
            catch (Exception ex)
            {
                _log.Error("[HeroGhost] Spawn failed: {Message}", ex.Message);
                Reset();
                return false;
            }
        }

        public void TeleportTo(int cx, int cy, double xr, double yr)
        {
            var ghost = _ghost;
            if (ghost == null || _teleportDisabled) return;

            try
            {
                if (TryPlaceGhost(ghost, cx, cy, xr, yr, suppressWarnings: false))
                {
                    TryRefreshSpritePos(ghost);
                    return;
                }
            }
            catch (Exception ex)
            {
                if (!_teleportWarningLogged)
                {
                    _log.Warning("[HeroGhost] Teleport failed: {Message}", ex.Message);
                    _teleportWarningLogged = true;
                }
            }
            _teleportDisabled = true;
        }

        private bool TryPlaceGhost(object ghost, int cx, int cy, double xr, double yr, bool suppressWarnings)
        {
            const BindingFlags AllFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            _setPosCase = ghost.GetType().GetMethod(
                "setPosCase",
                AllFlags,
                binder: null,
                types: new[] { typeof(int), typeof(int), typeof(double?), typeof(double?) },
                modifiers: null);

            if (_setPosCase != null)
            {
                _setPosCase.Invoke(ghost, new object?[] { cx, cy, xr, yr });
                TryRefreshSpritePos(ghost);
                return true;
            }
            

            try
            {
                dynamic dyn = DynamicAccessUtils.AsDynamic(ghost);
                dyn.cx = cx;
                dyn.cy = cy;
                dyn.xr = xr;
                dyn.yr = yr;
                return true;
            }
            catch (Exception ex)
            {
                if (!suppressWarnings && !_teleportWarningLogged)
                {
                    _log.Warning("[HeroGhost] direct coord set failed: {Message}", ex.Message);
                    _teleportWarningLogged = true;
                }
            }

            return false;
        }

       private void TryRefreshSpritePos(object ghost)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            try
            {
                var t = ghost.GetType();

                // ---------- updateLastSprPos() ----------
                try
                {
                    var updateLast = t.GetMethods(Flags)
                        .Where(m => m.Name == "updateLastSprPos")
                        .Where(m => m.GetParameters().Length == 0)
                        .FirstOrDefault();

                    if (updateLast != null)
                        updateLast.Invoke(ghost, Array.Empty<object?>());
                }
                catch (Exception ex)
                {
                    _log.Warning("[HeroGhost] updateLastSprPos failed: {Message}", ex.Message);
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[HeroGhost] TryRefreshSpritePos exception: {Message}", ex.Message);
            }
        }


        public void Reset()
        {
            _ghost = null;
            _levelRef = null;
            _gameRef = null;
            _setPosCase = null;
            _setPos = null;
            _safeTpTo = null;
            _teleportDisabled = false;
            _teleportWarningLogged = false;
            _registeredInLevel = false;
            _lastCoordLog = DateTime.MinValue;
            _spriteOffsetApplied = false;
        }

        private bool TryResolveContext(object heroRef, object? levelHint, object? gameHint, out object levelObj, out object gameObj)
        {
            levelObj = levelHint ?? _levelRef ?? null!;
            gameObj = gameHint ?? _gameRef ?? null!;

            try
            {
                dynamic hero = DynamicAccessUtils.AsDynamic(heroRef);
                object? level = levelObj;
                try { level = (object?)hero._level ?? levelObj; } catch (Exception ex) { LogCatch(ex); }
                if (level == null) return false;

                object? game = ExtractGameFromLevel(level) ?? gameObj;
                if (game == null) return false;

                levelObj = level;
                gameObj = game;
                return true;
            }
            catch (Exception ex)
            {
                _log.Warning("[HeroGhost] TryResolveContext exception: {Message}", ex.Message);
                return levelObj != null && gameObj != null;
            }
        }

        private static object? ExtractGameFromLevel(object levelObj)
        {
            var levelType = levelObj.GetType();
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return levelType.GetProperty("game", Flags)?.GetValue(levelObj) ??
                   levelType.GetField("game", Flags)?.GetValue(levelObj);
        }

        private static Type? FindEntityClass(params Assembly[] assemblies)
        {
            return FindEntityClass((IEnumerable<Assembly>)assemblies);
        }

        private static Type? FindEntityClassByName(string? fullName, params Assembly[] assemblies)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return null;
            return FindEntityClassByName(fullName, (IEnumerable<Assembly>)assemblies);
        }

        private static Type? FindEntityClassByName(string fullName, IEnumerable<Assembly> assemblies)
        {
            foreach (var asm in assemblies)
            {
                if (asm == null) continue;
                try
                {
                    var t = asm.GetType(fullName);
                    if (t != null) return t;
                }
                catch (Exception ex) { LogCatch(ex); }
            }
            return null;
        }

        private static Type? ResolveType(IEnumerable<string> names, IEnumerable<Assembly> assemblies)
        {
            foreach (var name in names)
            {
                var t = FindEntityClassByName(name, assemblies);
                if (t != null) return t;
            }
            return null;
        }

        private object? TryCreateHaxeProxyInstance(Type heroClass)
        {
            try
            {
                var attr = heroClass.GetCustomAttributes(inherit: false)
                    .FirstOrDefault(a => string.Equals(a.GetType().FullName, "HaxeProxy.Runtime.Internals.HaxeProxyBindingAttribute", StringComparison.Ordinal));
                if (attr == null) return null;

                var typeIndexProp = attr.GetType().GetProperty("TypeIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (typeIndexProp == null) return null;
                var idxObj = typeIndexProp.GetValue(attr);
                if (idxObj == null) return null;
                var typeIndex = Convert.ToInt32(idxObj);
                if (typeIndex < 0) return null;

                var helperType = Type.GetType("HaxeProxy.Runtime.Internals.HaxeProxyHelper, HaxeProxy");
                var managerType = Type.GetType("HaxeProxy.Runtime.Internals.HaxeProxyManager, HaxeProxy");
                if (helperType == null || managerType == null) return null;

                var createInstance = helperType.GetMethod("CreateInstance", BindingFlags.Public | BindingFlags.Static);
                var createProxy = managerType.GetMethod("CreateProxy", BindingFlags.NonPublic | BindingFlags.Static);
                if (createInstance == null || createProxy == null) return null;

                var hlObj = createInstance.Invoke(null, new object?[] { typeIndex });
                if (hlObj == null) return null;

                var proxy = createProxy.Invoke(null, new[] { hlObj });
                if (proxy != null)
                {
                    _log.Information("[HeroGhost] Created Haxe proxy instance via CreateInstance (typeIndex={TypeIndex})", typeIndex);
                }
                return proxy;
            }
            catch (Exception ex)
            {
                _log.Warning("[HeroGhost] TryCreateHaxeProxyInstance failed: {Message}", ex.Message);
                return null;
            }
        }

        private static Type? FindEntityClass(IEnumerable<Assembly> assemblies)
        {
            string[] candidates =
            {
                "dc.en.Entity",
                "dc.Entity",
                "en.Entity"
            };

            foreach (var asm in assemblies)
            {
                if (asm == null) continue;
                foreach (var name in candidates)
                {
                    try
                    {
                        var t = asm.GetType(name);
                        if (t != null) return t;
                    }
                    catch (Exception ex) { LogCatch(ex); }
                }
            }
            return null;
        }

        private object? CreateGhostInstance(Type heroClass, object heroRef, object gameObj, object levelObj, int cx, int cy, string heroTypeString, HashSet<Type>? visited = null)
        {
            const BindingFlags AllFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            visited ??= new HashSet<Type>();
            if (!visited.Add(heroClass))
                return null;

            try
            {
                var ctors = heroClass.GetConstructors(AllFlags);

                // Prefer level-based constructors first so _level is set before registration.
                foreach (var ctor in ctors)
                {
                    var ps = ctor.GetParameters();
                    try
                    {
                        if (ps.Length == 3 &&
                            ps[0].ParameterType.IsInstanceOfType(levelObj) &&
                            ps[1].ParameterType == typeof(int) &&
                            ps[2].ParameterType == typeof(int))
                        {
                            return ctor.Invoke(new object?[] { levelObj, cx, cy });
                        }
                        if (ps.Length >= 5 &&
                            ps[0].ParameterType.IsInstanceOfType(levelObj) &&
                            ps[1].ParameterType == typeof(int) &&
                            ps[2].ParameterType == typeof(int))
                        {
                            // Homunculus(Level,int,int,bool,bool,Homunculus sourceSkill)
                            var args = new object?[ps.Length];
                            args[0] = levelObj;
                            args[1] = cx;
                            args[2] = cy;
                            for (int i = 3; i < ps.Length; i++)
                            {
                                args[i] = ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
                            }
                            return ctor.Invoke(args);
                        }

                        // Homunculus(Level,int,int,bool,bool,Homunculus sourceSkill) specific ctor for dc.en.Homunculus
                        if (ps.Length == 6 &&
                            ps[0].ParameterType.IsInstanceOfType(levelObj) &&
                            ps[1].ParameterType == typeof(int) &&
                            ps[2].ParameterType == typeof(int))
                        {
                            var args = new object?[ps.Length];
                            args[0] = levelObj;
                            args[1] = cx;
                            args[2] = cy;
                            args[3] = false; // forCinematic
                            args[4] = false; // attachedToHero
                            args[5] = null; // sourceSkill
                            return ctor.Invoke(args);
                        }

                        // HeroDeadCorpse(GameCinematic, Hero)
                        if (ps.Length == 2 &&
                            (ps[1].ParameterType.Name.Contains("Hero", StringComparison.OrdinalIgnoreCase) || ps[1].ParameterType.IsInstanceOfType(heroRef)))
                        {
                            var args = new object?[2];
                            args[0] = null; // GameCinematic not available
                            args[1] = heroRef;
                            try { return ctor.Invoke(args); } catch (Exception ex) { LogCatch(ex); }
                        }

                        // Entity(Level,int,int) ctor
                        if (ps.Length == 3 &&
                            ps[0].ParameterType.IsInstanceOfType(levelObj) &&
                            ps[1].ParameterType == typeof(int) &&
                            ps[2].ParameterType == typeof(int) &&
                            string.Equals(heroClass.FullName, "dc.Entity", StringComparison.Ordinal))
                        {
                            var args = new object?[3];
                            args[0] = levelObj;
                            args[1] = cx; // use cx as second arg
                            args[2] = 0;  // diminishingFactorsCount
                            try { return ctor.Invoke(args); } catch (Exception ex) { LogCatch(ex); }
                        }
                    }
                    catch (Exception ex) { LogCatch(ex); }
                }

                // Fallback: game + type string constructors after level attempts
                foreach (var ctor in ctors)
                {
                    var ps = ctor.GetParameters();
                    try
                    {
                        if (ps.Length == 2 && ps[1].ParameterType == typeof(string))
                        {
                            var p0 = ps[0];
                            if (p0.ParameterType.IsInstanceOfType(gameObj) ||
                                p0.ParameterType.Name.Contains("Game", StringComparison.OrdinalIgnoreCase) ||
                                p0.ParameterType.FullName?.Contains(".Game") == true)
                                return ctor.Invoke(new[] { gameObj, heroTypeString });

                            // Loosen matching: try invoke even if type assignability check fails
                            try
                            {
                                return ctor.Invoke(new[] { gameObj, heroTypeString });
                            }
                            catch (Exception ex) { LogCatch(ex); }
                        }
                    }
                    catch (Exception ex) { LogCatch(ex); }
                }
            }
            catch (Exception ex) { LogCatch(ex); }

            // Fallback: Activator with raw (gameObj, string)
            try
            {
                return Activator.CreateInstance(heroClass, new object?[] { gameObj, heroTypeString });
            }
            catch (Exception ex) { LogCatch(ex); }

            // Last resort: create Haxe proxy instance (no constructor) — may miss update hooks
            var hlGhost = TryCreateHaxeProxyInstance(heroClass);
            if (hlGhost != null)
                return hlGhost;


            // Fallback to alternate entity classes if the current one failed
            var fallbackNames = new[]
            {
                "dc.Entity",
                "en.Entity"
            };
            foreach (var name in fallbackNames)
            {
                try
                {
                    var fb = heroClass.Assembly.GetType(name) ??
                             levelObj.GetType().Assembly.GetType(name) ??
                             gameObj.GetType().Assembly.GetType(name) ??
                             AppDomain.CurrentDomain.GetAssemblies().Select(a => a?.GetType(name)).FirstOrDefault(t => t != null);
                    if (fb != null)
                    {
                        _log.Warning("[HeroGhost] Falling back to {FallbackClass}", fb.FullName);
                        var res = CreateGhostInstance(fb, heroRef, gameObj, levelObj, cx, cy, heroTypeString, visited);
                        if (res != null) return res;
                    }
                }
                catch (Exception ex) { LogCatch(ex); }
            }

            // Give up if we could not find a working constructor.
            _log.Error("[HeroGhost] No usable constructor found for {HeroClass}", heroClass.FullName);
            return null;
        }


        private static (int cx, int cy, double xr, double yr) ExtractCoordsFromHero(object heroRef)
        {
            int cx = -1, cy = -1;
            double xr = 0.5, yr = 1;
            try
            {
                dynamic h = DynamicAccessUtils.AsDynamic(heroRef);
                try { cx = (int)h.cx; } catch (Exception ex) { LogCatch(ex); }
                try { cy = (int)h.cy; } catch (Exception ex) { LogCatch(ex); }
                try { xr = (double)h.xr; } catch (Exception ex) { LogCatch(ex); }
                try { yr = (double)h.yr; } catch (Exception ex) { LogCatch(ex); }
            }
            catch (Exception ex) { LogCatch(ex); }
            return (cx, cy, xr, yr);
        }

        private static bool ExtractCoordsFromObject(object obj, out int cx, out int cy, out double xr, out double yr)
        {
            cx = cy = -1;
            xr = 0.5;
            yr = 1;
            try
            {
                dynamic h = DynamicAccessUtils.AsDynamic(obj);
                try { cx = (int)h.cx; } catch (Exception ex) { LogCatch(ex); }
                try { cy = (int)h.cy; } catch (Exception ex) { LogCatch(ex); }
                try { xr = (double)h.xr; } catch (Exception ex) { LogCatch(ex); }
                try { yr = (double)h.yr; } catch (Exception ex) { LogCatch(ex); }

                // fallback to x/y if cx/cy not present
                if (cx < 0 || cy < 0)
                {
                    try { cx = (int)h.x; } catch (Exception ex) { LogCatch(ex); }
                    try { cy = (int)h.y; } catch (Exception ex) { LogCatch(ex); }
                }

                return cx >= 0 && cy >= 0;
            }
            catch (Exception ex)
            {
                LogCatch(ex);
                return false;
            }
        }

        private bool ForceSetCoords(object ghost, int cx, int cy, double xr, double yr, bool suppressWarnings)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var t = ghost.GetType();
            var targets = new (string name, object value)[]
            {
                ("cx", cx), ("cy", cy), ("x", (double)cx), ("y", (double)cy), ("xr", xr), ("yr", yr)
            };

            var success = false;
            foreach (var (name, value) in targets)
            {
                try
                {
                    var f = t.GetField(name, Flags);
                    if (f != null && IsNumericAssignable(f.FieldType, value))
                    {
                        f.SetValue(ghost, Convert.ChangeType(value, f.FieldType));
                        success = true;
                        continue;
                    }

                    var p = t.GetProperty(name, Flags);
                    if (p?.CanWrite == true && IsNumericAssignable(p.PropertyType, value))
                    {
                        p.SetValue(ghost, Convert.ChangeType(value, p.PropertyType));
                        success = true;
                    }
                }
                catch (Exception ex)
                {
                    if (!suppressWarnings && !_teleportWarningLogged)
                    {
                        _log.Warning("[HeroGhost] Force set {Name} failed: {Message}", name, ex.Message);
                        _teleportWarningLogged = true;
                    }
                }
            }

            return success;
        }

        private void EnsureContextFields(object ghost, object levelObj, object gameObj)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var t = ghost.GetType();

            try
            {
                var levelFields = new[] { "level", "_level" };
                var gameFields = new[] { "game", "_game" };

                foreach (var name in levelFields)
                {
                    var f = t.GetField(name, Flags);
                    if (f != null && f.FieldType.IsInstanceOfType(levelObj))
                        try { f.SetValue(ghost, levelObj); } catch (Exception ex) { LogCatch(ex); }

                    var p = t.GetProperty(name, Flags);
                    if (p?.CanWrite == true && p.PropertyType.IsInstanceOfType(levelObj))
                        try { p.SetValue(ghost, levelObj); } catch (Exception ex) { LogCatch(ex); }
                }

                foreach (var name in gameFields)
                {
                    var f = t.GetField(name, Flags);
                    if (f != null && f.FieldType.IsInstanceOfType(gameObj))
                        try { f.SetValue(ghost, gameObj); } catch (Exception ex) { LogCatch(ex); }

                    var p = t.GetProperty(name, Flags);
                    if (p?.CanWrite == true && p.PropertyType.IsInstanceOfType(gameObj))
                        try { p.SetValue(ghost, gameObj); } catch (Exception ex) { LogCatch(ex); }
                }

                // Best-effort alive/body flags
                var aliveField = t.GetField("alive", Flags);
                if (aliveField != null && aliveField.FieldType == typeof(bool))
                    try { aliveField.SetValue(ghost, true); } catch (Exception ex) { LogCatch(ex); }
                var aliveProp = t.GetProperty("alive", Flags);
                if (aliveProp?.CanWrite == true && aliveProp.PropertyType == typeof(bool))
                    try { aliveProp.SetValue(ghost, true); } catch (Exception ex) { LogCatch(ex); }

                LogPointerInfo(ghost, t);
            }
            catch (Exception ex) { LogCatch(ex); }
        }

        private void SetHeroType(object ghost, string heroTypeString)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var t = ghost.GetType();
            var names = new[] { "heroType", "_heroType", "type", "_type" };

            foreach (var name in names)
            {
                try
                {
                    var f = t.GetField(name, Flags);
                    if (f != null && f.FieldType == typeof(string))
                    {
                        f.SetValue(ghost, heroTypeString);
                        continue;
                    }

                    var p = t.GetProperty(name, Flags);
                    if (p?.CanWrite == true && p.PropertyType == typeof(string))
                    {
                        p.SetValue(ghost, heroTypeString);
                    }
                }
                catch (Exception ex) { LogCatch(ex); }
            }
        }

        private void LogLevelIntrospection(object levelObj)
        {
            try
            {
                var type = levelObj.GetType();
                if (!LoggedLevelMembers.Add(type.FullName ?? type.Name))
                    return;

                const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var fields = type.GetFields(Flags)
                    .Select(f => $"{f.Name}:{f.FieldType.Name}")
                    .Where(n => n.Contains("ent", StringComparison.OrdinalIgnoreCase) || n.Contains("list", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var props = type.GetProperties(Flags)
                    .Select(p => $"{p.Name}:{p.PropertyType.Name}")
                    .Where(n => n.Contains("ent", StringComparison.OrdinalIgnoreCase) || n.Contains("list", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var methods = type.GetMethods(Flags)
                    .Select(m => $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})")
                    .Where(n =>
                        n.Contains("register", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("spawn", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("create", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("add", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                _log.Information("[HeroGhost] Level members (entity-ish) fields={Fields} props={Props} methods={Methods}", string.Join(",", fields), string.Join(",", props), string.Join(",", methods));
            }
            catch (Exception ex) { LogCatch(ex); }
        }

        private void LogPointerInfo(object ghost, Type? typeOverride = null, bool warnOnNull = false)
        {
            try
            {
                const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
                var t = typeOverride ?? ghost.GetType();
                var ptrField = t.GetField("_hxPtr", Flags) ?? t.GetField("HashlinkPointer", Flags);
                var ptrProp = t.GetProperty("HashlinkPointer", Flags) ?? t.GetProperty("HashlinkObj", Flags);

                if (ptrField != null)
                {
                    try { _log.Information("[HeroGhost] Ghost pointer field {Field}={Value}", ptrField.Name, ptrField.GetValue(ghost)); } catch (Exception ex) { LogCatch(ex); }
                }
                if (ptrProp != null)
                {
                    try { _log.Information("[HeroGhost] Ghost pointer prop {Prop}={Value}", ptrProp.Name, ptrProp.GetValue(ghost)); } catch (Exception ex) { LogCatch(ex); }
                }

                if (warnOnNull)
                {
                    try
                    {
                        var v1 = ptrField?.GetValue(ghost);
                        var v2 = ptrProp?.GetValue(ghost);
                        if (v1 == null && v2 == null)
                            _log.Warning("[HeroGhost] Ghost Hashlink pointer is null");
                    }
                    catch (Exception ex) { LogCatch(ex); }
                }
            }
            catch (Exception ex) { LogCatch(ex); }
        }

        private static bool IsNumericAssignable(Type targetType, object value)
        {
            if (!targetType.IsValueType) return false;
            var code = Type.GetTypeCode(targetType);
            return code is TypeCode.Int32 or TypeCode.Double or TypeCode.Single or TypeCode.Int64 or TypeCode.Int16;
        }

        public bool TryLogCoords(TimeSpan? minInterval = null)
        {
            var ghost = _ghost;
            if (ghost == null) return false;

            var now = DateTime.UtcNow;
            var interval = minInterval ?? TimeSpan.FromSeconds(5);
            if (now - _lastCoordLog < interval)
                return false;

            if (!ExtractCoordsFromObject(ghost, out var cx, out var cy, out var xr, out var yr))
                return false;

            _lastCoordLog = now;
            _log.Information("[HeroGhost] Position {Cx},{Cy} ({Xr},{Yr})", cx, cy, xr, yr);
            return true;
        }

        private static Delegate CreateNoOpDelegate(Type delegateType)
        {
            var invoke = delegateType.GetMethod("Invoke");
            if (invoke == null) throw new InvalidOperationException("Delegate Invoke missing");

            var parameters = invoke.GetParameters()
                .Select(p => Expression.Parameter(p.ParameterType, p.Name ?? "p"))
                .ToArray();

            Expression body = invoke.ReturnType == typeof(void)
                ? Expression.Empty()
                : Expression.Default(invoke.ReturnType);

            var lambda = Expression.Lambda(delegateType, body, parameters);
            return lambda.Compile();
        }

        private void TryCopyController(object heroRef, object ghost)
        {
            try
            {
                const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                // source controller
                object? srcController = null;
                var hType = heroRef.GetType();
                var hCtrlField = hType.GetField("controller", Flags);
                var hCtrlProp = hType.GetProperty("controller", Flags);
                if (hCtrlField != null) srcController = hCtrlField.GetValue(heroRef);
                else if (hCtrlProp?.CanRead == true) srcController = hCtrlProp.GetValue(heroRef);

                if (srcController == null) return;

                // assign to ghost
                var gType = ghost.GetType();
                var gCtrlField = gType.GetField("controller", Flags);
                var gCtrlProp = gType.GetProperty("controller", Flags);
                var assigned = false;

                if (gCtrlField != null && gCtrlField.FieldType.IsInstanceOfType(srcController))
                {
                    try { gCtrlField.SetValue(ghost, srcController); assigned = true; } catch (Exception ex) { LogCatch(ex); }
                }
                if (!assigned && gCtrlProp?.CanWrite == true && gCtrlProp.PropertyType.IsInstanceOfType(srcController))
                {
                    try { gCtrlProp.SetValue(ghost, srcController); assigned = true; } catch (Exception ex) { LogCatch(ex); }
                }

                // lock inputs on controller
                if (assigned)
                {
                    try
                    {
                        TrySetBool(gCtrlField, ghost, "manualLock", true, Flags);
                        TrySetBool(gCtrlProp, ghost, "manualLock", true, Flags);
                    }
                    catch (Exception ex) { LogCatch(ex); }
                }
            }
            catch (Exception ex) { LogCatch(ex); }
        }

        private bool TryRegisterInLevel(object levelObj, object ghost)
        {
            if (_registeredInLevel || levelObj == null || ghost == null) return _registeredInLevel;

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                var levelType = levelObj.GetType();
                _log.Information("[HeroGhost] Registering ghost into level {LevelType} via lists/addChild", levelType.FullName);

                // Prefer the official registry API if present.
                if (TryInvokeOne(levelObj, "registerEntity", ghost, Flags)) return true;

                // addChild(Process) as a light-weight hook
                var processType = levelType.Assembly.GetType("dc.libs.Process") ?? levelType.BaseType?.Assembly?.GetType("dc.libs.Process");
                if (processType != null && processType.IsInstanceOfType(ghost))
                {
                    if (TryInvokeOne(levelObj, "addChild", ghost, Flags)) return true;
                }

                // push into list field/properties named entities/_entities/qTreeEntities/savedEntities/entitiesGC
                var listNames = new[] { "entities", "_entities", "qTreeEntities", "savedEntities", "entitiesGC" };
                foreach (var name in listNames)
                {
                    var listField = levelType.GetField(name, Flags);
                    if (listField != null && listField.GetValue(levelObj) is System.Collections.IList list)
                    {
                        try
                        {
                            list.Add(ghost);
                            _registeredInLevel = true;
                            _log.Information("[HeroGhost] Registered ghost via list field {Field} (count now {Count})", listField.Name, list.Count);
                            return true;
                        }
                        catch (Exception ex)
                        {
                            _log.Warning("[HeroGhost] Add to list field {Field} failed: {Message}", listField.Name, ex.Message);
                        }
                    }
                    var listProp = levelType.GetProperty(name, Flags);
                    if (listProp?.CanRead == true && listProp.GetValue(levelObj) is System.Collections.IList listPropVal)
                    {
                        try
                        {
                            listPropVal.Add(ghost);
                            _registeredInLevel = true;
                            _log.Information("[HeroGhost] Registered ghost via list property {Property} (count now {Count})", listProp.Name, listPropVal.Count);
                            return true;
                        }
                        catch (Exception ex)
                        {
                            _log.Warning("[HeroGhost] Add to list property {Property} failed: {Message}", listProp.Name, ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[HeroGhost] TryRegisterInLevel error: {Message}", ex.Message);
                LogLevelIntrospection(levelObj);
            }

            return _registeredInLevel;
        }


        private bool TryInvokeOne(object target, string methodName, object arg, BindingFlags flags)
        {
            if (_registeredInLevel) return true;
            var t = target.GetType();
            try
            {
                var methods = t.GetMethods(flags).Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal)).ToArray();
                MethodInfo? m = null;
                if (methods.Length > 0)
                {
                    m = methods.FirstOrDefault(mi =>
                    {
                        var ps = mi.GetParameters();
                        return ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(arg.GetType());
                    }) ??
                        methods.FirstOrDefault(mi =>
                        {
                            var ps = mi.GetParameters();
                            return ps.Length == 1 && ps[0].ParameterType == typeof(object);
                        }) ??
                        methods.FirstOrDefault(mi => mi.GetParameters().Length == 0);
                }
                m ??= t.GetMethod(methodName, flags);
                if (m == null)
                {
                    _log.Information("[HeroGhost] Method {Method} not found on level type {LevelType}", methodName, t.FullName);
                    return false;
                }

                var parameters = m.GetParameters();
                object?[] args = parameters.Length switch
                {
                    0 => Array.Empty<object?>(),
                    1 => new object?[] { arg },
                    _ => Enumerable.Repeat<object?>(null, parameters.Length).ToArray()
                };
                _log.Information("[HeroGhost] Attempting {Method} on level type {LevelType} with {ParamCount} params", methodName, t.FullName, parameters.Length);
                m.Invoke(target, args);
                _registeredInLevel = true;
                _log.Information("[HeroGhost] Ghost registered via {Method}", methodName);
                return true;
            }
            catch (Exception ex)
            {
                var innerMsg = ex.InnerException?.Message ?? string.Empty;
                if (methodName == "registerEntity" && innerMsg.Contains("Entity already registered", StringComparison.OrdinalIgnoreCase))
                {
                    _registeredInLevel = true;
                    _log.Information("[HeroGhost] registerEntity reported already registered; marking as registered");
                    return true;
                }
                _log.Warning("[HeroGhost] {Method} failed: {Message} (inner={Inner}) stack={Stack}", methodName, ex.Message, innerMsg.Length == 0 ? "none" : innerMsg, ex.ToString());
                LogLevelIntrospection(target);
            }

            return false;
        }


        private void TryInvokeLifecycle(object ghost)
        {
            try
            {
                const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var t = ghost.GetType();
                foreach (var name in new[] { "postCreate", "postInit", "init" })
                {
                    var m = t.GetMethod(name, Flags, binder: null, types: Type.EmptyTypes, modifiers: null);
                    if (m != null)
                    {
                        try { m.Invoke(ghost, Array.Empty<object?>()); } catch (Exception ex) { LogCatch(ex); }
                    }
                }
            }
            catch (Exception ex) { LogCatch(ex); }
        }

        private void TryApplyEntitySetup(object heroRef, object ghost, object levelObj)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            try
            {
                // set_level(Level)
                var setLevel = ghost.GetType().GetMethod("set_level", Flags, binder: null, types: new[] { levelObj.GetType() }, modifiers: null) ??
                               ghost.GetType().GetMethod("set_level", Flags);
                if (setLevel != null)
                {
                    var ps = setLevel.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(levelObj))
                    {
                        try { setLevel.Invoke(ghost, new[] { levelObj }); } catch (Exception ex) { LogCatch(ex); }
                    }
                }

                var initOk = TryInitSpriteFromHero(heroRef, ghost);
                if (initOk)
                {
                    TrySyncSpriteFrame(heroRef, ghost);
                }
                TryApplyNormal(heroRef, ghost);

                // spriteUpdate only if sprite exists
                var hasSprite = TryGetFieldOrProp(ghost, "sprite", Flags) ?? TryGetFieldOrProp(ghost, "spr", Flags);
                if (hasSprite != null)
                {
                    var spriteUpdate = ghost.GetType().GetMethod("spriteUpdate", Flags, binder: null, types: Type.EmptyTypes, modifiers: null);
                    if (spriteUpdate != null)
                    {
                        try { spriteUpdate.Invoke(ghost, Array.Empty<object?>()); } catch (Exception ex) { LogCatch(ex); }
                    }
                }
            }
            catch (Exception ex) { LogCatch(ex); }
        }

        private void TrySyncSpriteFrame(object heroRef, object ghost)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            try
            {
                var heroSpr = TryGetFieldOrProp(heroRef, "sprite", Flags) ?? TryGetFieldOrProp(heroRef, "spr", Flags);
                var ghostSpr = TryGetFieldOrProp(ghost, "sprite", Flags) ?? TryGetFieldOrProp(ghost, "spr", Flags);

                if (heroSpr == null || ghostSpr == null)
                    return;

                try
                {
                    var frameObj = TryGetFieldOrProp(heroSpr, "frame", Flags);
                    if (frameObj is int frame)
                    {
                        var setFrame = ghostSpr.GetType().GetMethod("set_frame", Flags, binder: null, types: new[] { typeof(int) }, modifiers: null);
                        if (setFrame != null)
                        {
                            setFrame.Invoke(ghostSpr, new object[] { frame });
                        }
                        else
                        {
                            var frameProp = ghostSpr.GetType().GetProperty("frame", Flags);
                            if (frameProp?.CanWrite == true && frameProp.PropertyType == typeof(int))
                                frameProp.SetValue(ghostSpr, frame);
                        }
                    }
                }
                catch (Exception ex) { LogCatch(ex, "TrySyncSpriteFrame"); }

                var heroLayer = TryGetFieldOrProp(heroSpr, "layer", Flags);
                var heroDepth = TryGetFieldOrProp(heroSpr, "depth", Flags);
                var heroXr = TryGetFieldOrProp(heroSpr, "xr", Flags);
                var heroYr = TryGetFieldOrProp(heroSpr, "yr", Flags);

                void TrySet(string name, object? val)
                {
                    if (val == null) return;
                    var f = ghostSpr.GetType().GetField(name, Flags);
                    if (f != null && f.FieldType.IsInstanceOfType(val))
                    {
                        try { f.SetValue(ghostSpr, val); } catch (Exception ex) { LogCatch(ex, "TrySyncSpriteFrame"); }
                        return;
                    }
                    var p = ghostSpr.GetType().GetProperty(name, Flags);
                    if (p != null && p.CanWrite && p.PropertyType.IsInstanceOfType(val))
                    {
                        try { p.SetValue(ghostSpr, val); } catch (Exception ex) { LogCatch(ex, "TrySyncSpriteFrame"); }
                    }
                }

                TrySet("layer", heroLayer);
                TrySet("depth", heroDepth);
                TrySet("xr", heroXr);
                TrySet("yr", heroYr);
            }
            catch (Exception ex) { LogCatch(ex, "TrySyncSpriteFrame"); }
        }

        private static object? TryGetFieldOrProp(object? target, string name, BindingFlags flags)
        {
            if (target == null) return null;
            try
            {
                var t = target.GetType();
                var f = t.GetField(name, flags);
                if (f != null) return f.GetValue(target);
                var p = t.GetProperty(name, flags);
                if (p?.CanRead == true) return p.GetValue(target);
            }
            catch (Exception ex) { LogCatch(ex); }
            return null;
        }

        private bool TryInitSpriteFromHero(object heroRef, object ghost)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            try
            {
                var init = ghost.GetType().GetMethod("initSprite", Flags);
                if (init == null) return false;

                object? heroSprite = TryGetFieldOrProp(heroRef, "sprite", Flags) ?? TryGetFieldOrProp(heroRef, "spr", Flags);
                if (heroSprite == null) return false;

                var lib = TryGetFieldOrProp(heroSprite, "lib", Flags);
                if (lib == null) return false;

                var groupObj = TryGetFieldOrProp(heroSprite, "groupName", Flags)
                               ?? TryGetFieldOrProp(heroSprite, "group", Flags)
                               ?? TryGetFieldOrProp(heroRef, "name", Flags)
                               ?? "hero";
                var xr = TryGetFieldOrProp(heroSprite, "xr", Flags) ?? TryGetFieldOrProp(heroRef, "xr", Flags);
                var yr = TryGetFieldOrProp(heroSprite, "yr", Flags) ?? TryGetFieldOrProp(heroRef, "yr", Flags);
                var layer = TryGetFieldOrProp(heroSprite, "layer", Flags);
                var lighted = TryGetFieldOrProp(heroSprite, "lighted", Flags);
                var depth = TryGetFieldOrProp(heroSprite, "depth", Flags);
                var nrmTex = TryGetFieldOrProp(heroSprite, "nrmTex", Flags)
                             ?? TryGetFieldOrProp(heroSprite, "normalTex", Flags)
                             ?? TryGetFieldOrProp(heroSprite, "nrm", Flags);

                object? ConvertGroup(Type targetType)
                {
                    if (groupObj != null && targetType.IsInstanceOfType(groupObj)) return groupObj;
                    var strVal = groupObj?.ToString() ?? "hero";
                    if (targetType == typeof(string)) return strVal;
                    try
                    {
                        var ctorStr = targetType.GetConstructor(new[] { typeof(string) });
                        if (ctorStr != null) return ctorStr.Invoke(new object?[] { strVal });
                        if (targetType.IsEnum) return Enum.Parse(targetType, strVal, ignoreCase: true);
                        return Convert.ChangeType(strVal, targetType);
                    }
                    catch (Exception ex) { LogCatch(ex); }
                    return null;
                }

                object? ConvDouble(object? v)
                {
                    try { return v == null ? null : Convert.ToDouble(v); } catch (Exception ex) { LogCatch(ex); return null; }
                }
                object? ConvInt(object? v)
                {
                    try { return v == null ? null : Convert.ToInt32(v); } catch (Exception ex) { LogCatch(ex); return null; }
                }
                object? ConvBool(object? v)
                {
                    try { return v == null ? null : Convert.ToBoolean(v); } catch (Exception ex) { LogCatch(ex); return null; }
                }

                var ps = init.GetParameters();
                var args = new object?[ps.Length];

                if (ps.Length > 0)
                {
                    if (!ps[0].ParameterType.IsInstanceOfType(lib)) return false;
                    args[0] = lib;
                }
                if (ps.Length > 1) args[1] = ConvertGroup(ps[1].ParameterType);
                if (ps.Length > 2) args[2] = ConvDouble(xr);
                if (ps.Length > 3) args[3] = ConvDouble(yr);
                if (ps.Length > 4) args[4] = ps[4].ParameterType.IsInstanceOfType(layer) ? layer : ConvInt(layer);
                if (ps.Length > 5) args[5] = ps[5].ParameterType.IsInstanceOfType(lighted) ? lighted : ConvBool(lighted);
                if (ps.Length > 6) args[6] = ConvDouble(depth);
                if (ps.Length > 7)
                {
                    args[7] = (nrmTex != null && ps[7].ParameterType.IsInstanceOfType(nrmTex)) ? nrmTex : null;
                }

                try
                {
                    init.Invoke(ghost, args);
                    _log.Information("[HeroGhost] initSprite applied from hero");
                    return true;
                }
                catch (Exception ex) { LogCatch(ex); }
            }
            catch (Exception ex) { LogCatch(ex); }

            return false;
        }


        private void TryApplyNormal(object heroRef, object ghost)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            try
            {
                var setNormal = ghost.GetType().GetMethod("setNormal", Flags);
                if (setNormal == null) return;

                var ghostSprite = TryGetFieldOrProp(ghost, "sprite", Flags) ?? TryGetFieldOrProp(ghost, "spr", Flags);
                if (ghostSprite == null) return;

                object? heroSprite = TryGetFieldOrProp(heroRef, "sprite", Flags) ?? TryGetFieldOrProp(heroRef, "spr", Flags);
                object? nrmTex = null;
                object? depth = null;
                object? layerConf = null;
                if (heroSprite != null)
                {
                    nrmTex = TryGetFieldOrProp(heroSprite, "nrmTex", Flags)
                             ?? TryGetFieldOrProp(heroSprite, "normalTex", Flags)
                             ?? TryGetFieldOrProp(heroSprite, "nrm", Flags);
                    depth = TryGetFieldOrProp(heroSprite, "depth", Flags);
                    layerConf = TryGetFieldOrProp(heroSprite, "layerConf", Flags) ?? TryGetFieldOrProp(heroSprite, "layer", Flags);
                }

                var ps = setNormal.GetParameters();
                if (ps.Length != 4) return;

                if (nrmTex != null && !ps[1].ParameterType.IsInstanceOfType(nrmTex)) nrmTex = null;
                if (depth != null && ps[2].ParameterType == typeof(double?))
                {
                    try { depth = Convert.ChangeType(depth, typeof(double)); }
                    catch (Exception ex) { LogCatch(ex); depth = null; }
                }
                if (layerConf != null && ps[3].ParameterType == typeof(string) && layerConf is not string)
                {
                    try { layerConf = layerConf.ToString(); } catch (Exception ex) { LogCatch(ex); layerConf = null; }
                }

                try
                {
                    setNormal.Invoke(ghost, new[] { ghostSprite, nrmTex, depth, layerConf });
                    _log.Information("[HeroGhost] Applied normal via setNormal");
                }
                catch (Exception ex) { LogCatch(ex); }
            }
            catch (Exception ex) { LogCatch(ex); }
        }


        private void TrySetBool(FieldInfo? maybeField, object target, string name, bool value, BindingFlags flags)
        {
            var field = maybeField;
            if (field == null || field.FieldType != typeof(bool))
            {
                var t = target.GetType();
                field = t.GetField(name, flags);
                if (field == null || field.FieldType != typeof(bool)) return;
            }
            try { field.SetValue(target, value); } catch (Exception ex) { LogCatch(ex); }
        }

        private void TrySetBool(PropertyInfo? maybeProp, object target, string name, bool value, BindingFlags flags)
        {
            var prop = maybeProp;
            if (prop == null || !prop.CanWrite || prop.PropertyType != typeof(bool))
            {
                var t = target.GetType();
                prop = t.GetProperty(name, flags);
                if (prop == null || !prop.CanWrite || prop.PropertyType != typeof(bool)) return;
            }
            try { prop.SetValue(target, value); } catch (Exception ex) { LogCatch(ex); }
        }
    }
}
