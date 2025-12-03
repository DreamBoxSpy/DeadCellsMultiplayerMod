using System;
using System.Linq;
using System.Reflection;
using System.Linq.Expressions;
using System.IO;
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
        private object? _heroSourceRef;
        private object? _heroShaderSnapshot;
        private object? _heroShaderListSnapshot;

        private const string DefaultEntityType = "dc.Entity";
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
        public bool HasEntityType => !string.IsNullOrWhiteSpace(_forcedHeroType);

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
            _forcedHeroType ??= DefaultEntityType;
        }

        public bool Spawn(object heroRef, object? levelHint, object? gameHint, int spawnCx, int spawnCy)
        {
            if (_ghost != null)
                return true;
            if (heroRef == null)
                return false;

            _heroSourceRef = heroRef;

            if (!TryResolveContext(heroRef, levelHint, gameHint, out var levelObj, out var gameObj))
            {
                _log.Warning("[HeroGhost] Spawn failed - unable to capture level/game");
                return false;
            }

            var requestedType = _forcedHeroType ?? DefaultEntityType;
            _forcedHeroType ??= requestedType;
            var heroTypeString = requestedType;
            var coords = ExtractCoordsFromHero(heroRef);
            // Always prefer live hero coords so we don't end up at -1/-1
            spawnCx = coords.cx;
            // Horizontal offset for ghost spawn
            spawnCx += 3;
            spawnCy = coords.cy;

            var preferredNames = new[]
            {
                requestedType,
                DefaultEntityType,
                "dc.en.Entity",
                "dc.en.Mob",
                "dc.Entity",
                "en.Entity"
            }.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray();

            var searchAssemblies = new[]
            {
                gameObj.GetType().Assembly,
                levelObj.GetType().Assembly,
                heroRef.GetType().Assembly
            };

            var heroClass = ResolveType(preferredNames, searchAssemblies) ??
                ResolveType(preferredNames, AppDomain.CurrentDomain.GetAssemblies()) ??
                FindEntityClassByName(requestedType, gameObj.GetType().Assembly) ??
                FindEntityClassByName(requestedType, levelObj.GetType().Assembly) ??
                FindEntityClassByName(requestedType, heroRef.GetType().Assembly) ??
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
                heroTypeString = heroClass.FullName ?? heroTypeString;
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
                TryCenterSprite(heroRef, _ghost!);
                TryInvokeLifecycle(_ghost!);
                var registered = TryRegisterInLevel(levelObj, _ghost!);
                var placed = TryPlaceGhost(_ghost!, spawnCx, spawnCy, coords.xr, coords.yr, suppressWarnings: true);
                if (!placed)
                    placed = ForceSetCoords(_ghost!, spawnCx, spawnCy, coords.xr, coords.yr, suppressWarnings: false);
                if (placed)
                {
                    TryCenterSprite(heroRef, _ghost!);
                    TryRefreshSpritePos(_ghost!);
                }

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
                    TryCenterSprite(_heroSourceRef ?? ghost, ghost);
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

            if (TryTeleportLike(ghost, cx, cy, xr, yr))
            {
                TryRefreshSpritePos(ghost);
                return true;
            }

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
                    {
                        updateLast.Invoke(ghost, Array.Empty<object?>());
                        _log.Information("[HeroGhost] updateLastSprPos applied");
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning("[HeroGhost] updateLastSprPos failed: {Message}", ex.Message);
                }

                // ---------- spriteUpdate() ----------
                try
                {
                    var spriteUpdate = t.GetMethods(Flags)
                        .FirstOrDefault(m => m.Name == "spriteUpdate" && m.GetParameters().Length == 0);
                    if (spriteUpdate != null)
                    {
                        spriteUpdate.Invoke(ghost, Array.Empty<object?>());
                        _log.Information("[HeroGhost] spriteUpdate applied");

                        // After spriteUpdate, try to pull hero shader if it appeared.
                        if (_heroSourceRef != null)
                        {
                            TryLateCopyShader(_heroSourceRef, ghost);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning("[HeroGhost] spriteUpdate failed: {Message}", ex.Message);
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[HeroGhost] TryRefreshSpritePos exception: {Message}", ex.Message);
            }
        }

        private void TryCenterSprite(object heroRef, object ghost)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            try
            {
                var ghostSpr = TryGetFieldOrProp(ghost, "sprite", Flags) ?? TryGetFieldOrProp(ghost, "spr", Flags);
                if (ghostSpr == null) return;

                var heroSpr = TryGetFieldOrProp(heroRef, "sprite", Flags) ?? TryGetFieldOrProp(heroRef, "spr", Flags);

                double xr = 0.5, yr = 1.0;
                TryReadDouble(heroSpr, "xr", Flags, ref xr);
                TryReadDouble(heroSpr, "yr", Flags, ref yr);

                TrySetNumeric(ghostSpr, "xr", xr, Flags);
                TrySetNumeric(ghostSpr, "yr", yr, Flags);

                CopySpriteOffsets(heroSpr, ghostSpr, Flags);

                _spriteOffsetApplied = true;
                _log.Information("[HeroGhost] Centered sprite xr={Xr} yr={Yr}", xr, yr);
            }
            catch (Exception ex) { LogCatch(ex, "TryCenterSprite"); }
        }

        private static void TryReadDouble(object? target, string name, BindingFlags flags, ref double value)
        {
            if (target == null) return;
            try
            {
                var val = TryGetFieldOrProp(target, name, flags);
                if (val != null)
                    value = Convert.ToDouble(val);
            }
            catch (Exception ex) { LogCatch(ex); }
        }

        private void TrySetNumeric(object target, string name, double value, BindingFlags flags)
        {
            var t = target.GetType();
            try
            {
                var f = t.GetField(name, flags);
                if (f != null && IsNumericAssignable(f.FieldType, value))
                    f.SetValue(target, Convert.ChangeType(value, f.FieldType));
            }
            catch (Exception ex) { LogCatch(ex); }

            try
            {
                var p = t.GetProperty(name, flags);
                if (p?.CanWrite == true && IsNumericAssignable(p.PropertyType, value))
                    p.SetValue(target, Convert.ChangeType(value, p.PropertyType));
            }
            catch (Exception ex) { LogCatch(ex); }
        }

        private static void TrySetFieldOrProp(object target, string name, object? value, BindingFlags flags)
        {
            if (value == null) return;
            var t = target.GetType();
            try
            {
                var f = t.GetField(name, flags);
                if (f != null && f.FieldType.IsInstanceOfType(value))
                {
                    f.SetValue(target, value);
                    return;
                }
                if (f != null && IsNumericAssignable(f.FieldType, value))
                {
                    f.SetValue(target, Convert.ChangeType(value, f.FieldType));
                    return;
                }
            }
            catch (Exception ex) { LogCatch(ex); }

            try
            {
                var p = t.GetProperty(name, flags);
                if (p?.CanWrite == true && p.PropertyType.IsInstanceOfType(value))
                {
                    p.SetValue(target, value);
                    return;
                }
                if (p?.CanWrite == true && IsNumericAssignable(p.PropertyType, value))
                {
                    p.SetValue(target, Convert.ChangeType(value, p.PropertyType));
                }
            }
            catch (Exception ex) { LogCatch(ex); }
        }

        private void CopySpriteOffsets(object? srcSpr, object dstSpr, BindingFlags flags)
        {
            if (dstSpr == null) return;
            var numericNames = new[]
            {
                "ox","oy","offsetX","offsetY","pivotX","pivotY",
                "centerX","centerY","anchorX","anchorY","baseY"
            };

            foreach (var name in numericNames)
            {
                var val = TryGetFieldOrProp(srcSpr, name, flags);
                if (val == null) continue;
                try
                {
                    var d = Convert.ToDouble(val);
                    TrySetNumeric(dstSpr, name, d, flags);
                    _log.Information("[HeroGhost] Copy sprite offset {Name}={Value}", name, d);
                }
                catch (Exception ex) { LogCatch(ex); }
            }
        }

        private void TryCopySpriteAppearance(object heroSpr, object ghostSpr)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            var copyList = new[]
            {
                "lib","group","groupName","name","frameData","anim","animManager","material","mat",
                "layerConf","layer","depth","lighted","shader","texture","nrmTex","normalTex","normalMap","tile"
            };

            foreach (var name in copyList)
            {
                var val = TryGetFieldOrProp(heroSpr, name, Flags);
                if (val != null)
                {
                    TrySetFieldOrProp(ghostSpr, name, val, Flags);
                    if (name is "lib" or "group" or "groupName" or "material" or "shader" or "texture")
                        _log.Information("[HeroGhost] Copied sprite field {Field}", name);
                }
            }

            // Frame handled by TrySyncSpriteFrame, but ensure sprite lib/group is set before frame
            var frameVal = TryGetFieldOrProp(heroSpr, "frame", Flags);
            if (frameVal != null)
            {
                TrySetFieldOrProp(ghostSpr, "frame", frameVal, Flags);
                _log.Information("[HeroGhost] Copied sprite frame={Frame}", frameVal);
            }

            CopySpriteOffsets(heroSpr, ghostSpr, Flags);
        }

        private void TryCopyShaderAndMaterial(object heroSpr, object ghostSpr)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            try
            {
                // wipe any cached shader ids/queue to avoid engine retry on missing shaderLinker key
                ClearShaderKeys(ghostSpr, Flags);
                var heroShaderList = _heroShaderListSnapshot ?? TryGetFieldOrProp(heroSpr, "shaders", Flags);
                var heroShaderKey = TryGetShaderKey(heroSpr, Flags);
                var heroShaderListCount = heroShaderList != null ? CountShaderList(heroShaderList, Flags) : 0;
                object? preferredListShader = heroShaderList != null ? TryExtractShaderFromList(heroShaderList, Flags, preferColorMap: true) : null;
                if (heroShaderList != null)
                {
                    var clonedList = CloneShaderList(heroShaderList, Flags);
                    if (clonedList != null)
                    {
                        var singleList = preferredListShader != null ? CreateSingleShaderList(clonedList, preferredListShader, Flags) : null;
                        var toApply = singleList ?? clonedList;
                        var len = CountShaderList(toApply, Flags);
                        _log.Information("[HeroGhost] Copied shaders list from hero sprite (count={Count}, applied=True)", len, true);
                        _heroShaderListSnapshot = null;
                        DumpShaderListTypes("Hero shader list", heroShaderList, Flags);
                        DumpShaderListTypes("Ghost shader list", toApply, Flags);
                        TrySetFieldOrProp(ghostSpr, "shaders", toApply, Flags);
                        ApplyShaderListSetter(ghostSpr, toApply, Flags);
                    }
                }

                if (heroShaderKey == null && heroShaderList != null)
                {
                    heroShaderKey = TryExtractShaderKeyFromList(heroShaderList, Flags);
                    if (heroShaderKey != null)
                        _log.Information("[HeroGhost] Extracted shader key from list ({Key})", heroShaderKey);
                }
                if (heroShaderKey == null)
                {
                    var colorMap = TryGetFieldOrProp(heroSpr, "colorMap", Flags);
                    if (colorMap != null)
                    {
                        heroShaderKey = colorMap;
                        _log.Information("[HeroGhost] Using colorMap as shader key ({Key})", heroShaderKey);
                    }
                }

                if (heroShaderKey != null)
                {
                    TrySetShaderKey(ghostSpr, heroShaderKey, Flags);
                    ApplyShaderKeySetter(ghostSpr, heroShaderKey, Flags);
                }
                else
                {
                    ClearShaderKeys(ghostSpr, Flags);
                    TrySetFieldOrProp(ghostSpr, "shaderQueue", null, Flags);
                }
                // Disable asset fetch path to avoid shaderLinker cache lookups.
                heroShaderKey = null;

                var shader = _heroShaderSnapshot
                             ?? TryGetFieldOrProp(heroSpr, "shader", Flags)
                             ?? TryGetFieldOrProp(heroSpr, "mat", Flags)
                             ?? TryGetFieldOrProp(heroSpr, "material", Flags);
                if (preferredListShader != null) shader = preferredListShader;
                if (shader == null && heroShaderList != null)
                {
                    shader = TryExtractShaderFromList(heroShaderList, Flags, preferColorMap: true);
                    if (shader != null)
                        _log.Information("[HeroGhost] Extracted shader from hero shader list ({Type})", shader.GetType().FullName);
                }
                var normal = TryGetFieldOrProp(heroSpr, "nrmTex", Flags)
                             ?? TryGetFieldOrProp(heroSpr, "normalTex", Flags)
                             ?? TryGetFieldOrProp(heroSpr, "normalMap", Flags);
                var colorMapValue = TryGetFieldOrProp(heroSpr, "colorMap", Flags);

                if (shader != null)
                {
                    TrySetFieldOrProp(ghostSpr, "shader", shader, Flags);
                    TrySetFieldOrProp(ghostSpr, "mat", shader, Flags);
                    TrySetFieldOrProp(ghostSpr, "material", shader, Flags);
                    ApplyShaderTargets(ghostSpr, shader, Flags);
                    if (TryGetFieldOrProp(ghostSpr, "shader", Flags) == null)
                    {
                        ApplyShaderSetter(ghostSpr, shader, Flags);
                    }
                    _log.Information("[HeroGhost] Copied shader/material from hero sprite ({ShaderType})", shader.GetType().FullName);
                    _heroShaderSnapshot = null;
                }
                if (normal != null)
                {
                    TrySetFieldOrProp(ghostSpr, "nrmTex", normal, Flags);
                    TrySetFieldOrProp(ghostSpr, "normalTex", normal, Flags);
                    TrySetFieldOrProp(ghostSpr, "normalMap", normal, Flags);
                    _log.Information("[HeroGhost] Copied normal map from hero sprite");
                }
                if (colorMapValue != null)
                {
                    TrySetFieldOrProp(ghostSpr, "colorMap", colorMapValue, Flags);
                    _log.Information("[HeroGhost] Copied colorMap from hero sprite");
                }

                InvokeShaderInit(heroSpr, ghostSpr, shader);

                if (TryGetFieldOrProp(ghostSpr, "shader", Flags) == null && heroShaderKey != null)
                {
                    var fromAssets = TryFetchShaderFromAssets(heroShaderKey);
                    if (fromAssets != null)
                    {
                        ApplyShaderSetter(ghostSpr, fromAssets, Flags);
                        TrySetShaderKey(ghostSpr, heroShaderKey, Flags);
                        ApplyShaderKeySetter(ghostSpr, heroShaderKey, Flags);
                        _log.Information("[HeroGhost] Applied shader from Assets by key");
                    }
                    else
                    {
                        _log.Warning("[HeroGhost] Failed to fetch shader from Assets for key {Key}", heroShaderKey);
                    }
                }
                var finalShader = TryGetFieldOrProp(ghostSpr, "shader", Flags)
                                  ?? TryGetFieldOrProp(ghostSpr, "mat", Flags)
                                  ?? TryGetFieldOrProp(ghostSpr, "material", Flags);
                var ghostShaderList = TryGetFieldOrProp(ghostSpr, "shaders", Flags);
                if (finalShader == null && ghostShaderList != null)
                {
                    var preferred = TryExtractShaderFromList(ghostShaderList, Flags, preferColorMap: true);
                    if (preferred != null)
                    {
                        ApplyShaderTargets(ghostSpr, preferred, Flags);
                        ApplyShaderSetter(ghostSpr, preferred, Flags);
                        finalShader = TryGetFieldOrProp(ghostSpr, "shader", Flags)
                                      ?? TryGetFieldOrProp(ghostSpr, "mat", Flags)
                                      ?? TryGetFieldOrProp(ghostSpr, "material", Flags);
                        if (finalShader != null)
                            _log.Information("[HeroGhost] Applied preferred shader from list ({Type})", finalShader.GetType().FullName);
                    }
                }
                if (finalShader == null && shader != null)
                {
                    ApplyShaderSetter(ghostSpr, shader, Flags);
                    ApplyShaderTargets(ghostSpr, shader, Flags);
                    finalShader = TryGetFieldOrProp(ghostSpr, "shader", Flags)
                                  ?? TryGetFieldOrProp(ghostSpr, "mat", Flags)
                                  ?? TryGetFieldOrProp(ghostSpr, "material", Flags);
                    if (finalShader != null)
                        _log.Information("[HeroGhost] Shader applied after init via setter ({Type})", finalShader.GetType().FullName);
                }
                if (finalShader == null && ghostShaderList != null)
                {
                    var count = CountShaderList(ghostShaderList, Flags);
                    _log.Information("[HeroGhost] Using shader list only; shader field null (list count={Count})", count);
                }
                if (finalShader == null && ghostShaderList == null)
                {
                    ClearShaderKeys(ghostSpr, Flags);
                    DumpShaderTargets(ghostSpr, Flags);
                    var heroShaderType = shader?.GetType().FullName ?? "null";
                    _log.Error("[HeroGhost] Ghost shader still null after copy/fetch; fallback disabled (key={Key}, heroShader={HeroShader}, heroShaderListCount={Count})", heroShaderKey ?? "null", heroShaderType, heroShaderListCount);
                }
                if (finalShader == null)
                {
                    var fallbackApplied = TryApplyStandaloneMaterial(heroSpr, ghostSpr, Flags);
                    finalShader = TryGetFieldOrProp(ghostSpr, "shader", Flags)
                                  ?? TryGetFieldOrProp(ghostSpr, "mat", Flags)
                                  ?? TryGetFieldOrProp(ghostSpr, "material", Flags);
                    if (fallbackApplied && finalShader != null)
                    {
                        _log.Information("[HeroGhost] Applied standalone fallback material ({Type})", finalShader.GetType().FullName);
                    }
                }
                // ensure no leftover cache ids that would trigger shaderLinker lookups
                ClearShaderKeys(ghostSpr, Flags);
            }
            catch (Exception ex) { LogCatch(ex, "TryCopyShaderAndMaterial"); }
        }

        private void InvokeShaderInit(object heroSpr, object ghostSpr, object? shaderInstance)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                var ghostType = ghostSpr.GetType();
                foreach (var name in new[] { "initShaders", "initShader", "ensureShaders", "buildShader" })
                {
                    var m = ghostType.GetMethod(name, Flags, binder: null, types: Type.EmptyTypes, modifiers: null);
                    if (m != null)
                    {
                        m.Invoke(ghostSpr, Array.Empty<object?>());
                        _log.Information("[HeroGhost] Invoked {Method} on ghost sprite", name);
                        break;
                    }
                }

                // Try applying hero shader through setter if exists
                var heroShader = shaderInstance ?? TryGetFieldOrProp(heroSpr, "shader", Flags) ?? TryGetFieldOrProp(heroSpr, "mat", Flags);
                if (heroShader != null)
                {
                    ApplyShaderSetter(heroSpr, heroShader, Flags);
                    var setter = ghostType.GetMethods(Flags)
                        .FirstOrDefault(x => (x.Name == "set_shader" || x.Name == "set_mat" || x.Name == "set_material") &&
                                             x.GetParameters().Length == 1 &&
                                             x.GetParameters()[0].ParameterType.IsInstanceOfType(heroShader));
                    if (setter != null)
                    {
                        setter.Invoke(ghostSpr, new[] { heroShader });
                        _log.Information("[HeroGhost] Applied shader via setter {Setter}", setter.Name);
                    }
                }
            }
            catch (Exception ex) { LogCatch(ex, "InvokeShaderInit"); }
        }

        private void ApplyShaderSetter(object targetSpr, object shader, BindingFlags flags)
        {
            try
            {
                var t = targetSpr.GetType();
                var setter = t.GetMethods(flags)
                    .FirstOrDefault(x => (x.Name == "set_shader" || x.Name == "set_mat" || x.Name == "set_material") &&
                                         x.GetParameters().Length == 1 &&
                                         x.GetParameters()[0].ParameterType.IsInstanceOfType(shader));
                setter?.Invoke(targetSpr, new[] { shader });
            }
            catch (Exception ex) { LogCatch(ex, "ApplyShaderSetter"); }
        }

        private void TryLateCopyShader(object heroRef, object ghost)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            try
            {
                var heroSpr = TryGetFieldOrProp(heroRef, "sprite", Flags) ?? TryGetFieldOrProp(heroRef, "spr", Flags);
                var ghostSpr = TryGetFieldOrProp(ghost, "sprite", Flags) ?? TryGetFieldOrProp(ghost, "spr", Flags);
                if (heroSpr == null || ghostSpr == null)
                    return;

                var heroShader = TryGetFieldOrProp(heroSpr, "shader", Flags) ?? TryGetFieldOrProp(heroSpr, "mat", Flags) ?? TryGetFieldOrProp(heroSpr, "material", Flags);
                if (heroShader == null)
                {
                    _log.Information("[HeroGhost] Late shader copy: hero shader still null, applying standalone");
                    if (TryApplyStandaloneMaterial(heroSpr, ghostSpr, Flags))
                        _log.Information("[HeroGhost] Standalone shader applied in late copy");
                    return;
                }

                ApplyShaderSetter(ghostSpr, heroShader, Flags);
                _log.Information("[HeroGhost] Late shader copy applied ({Type})", heroShader.GetType().FullName);
            }
            catch (Exception ex) { LogCatch(ex, "TryLateCopyShader"); }
        }

        private void ClearShaderKeys(object targetSpr, BindingFlags flags)
        {
            TrySetFieldOrProp(targetSpr, "shaderKey", null, flags);
            TrySetFieldOrProp(targetSpr, "shaderCacheId", null, flags);
            TrySetFieldOrProp(targetSpr, "shaderId", null, flags);
            TrySetFieldOrProp(targetSpr, "shaderQueue", null, flags);
        }

        private object? TryGetShaderKey(object spr, BindingFlags flags)
        {
            return TryGetFieldOrProp(spr, "shaderKey", flags)
                   ?? TryGetFieldOrProp(spr, "shaderCacheId", flags)
                   ?? TryGetFieldOrProp(spr, "shaderId", flags);
        }

        private void TrySetShaderKey(object spr, object key, BindingFlags flags)
        {
            TrySetFieldOrProp(spr, "shaderKey", key, flags);
            TrySetFieldOrProp(spr, "shaderCacheId", key, flags);
            TrySetFieldOrProp(spr, "shaderId", key, flags);
        }

        private object? TryFetchShaderFromAssets(object key)
        {
            try
            {
                var assetsType =
                    Type.GetType("dc.Assets, GameProxy") ??
                    Type.GetType("dc.Assets, GamePseudocode") ??
                    AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("dc.Assets"))
                        .FirstOrDefault(t => t != null);
                if (assetsType == null)
                {
                    try
                    {
                        var pseudoPath = @"C:\SteamLibrary\steamapps\common\Dead Cells\coremod\cache\GamePseudocode.dll";
                        if (File.Exists(pseudoPath))
                        {
                            var asm = Assembly.LoadFile(pseudoPath);
                            assetsType = asm.GetType("dc.Assets");
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("[HeroGhost] Failed to load GamePseudocode for Assets: {Message}", ex.Message);
                    }
                }
                if (assetsType == null)
                {
                    _log.Warning("[HeroGhost] Assets type not found for shader fetch");
                    return null;
                }

                var classProp = assetsType.GetProperty("Class", BindingFlags.Public | BindingFlags.Static);
                var assetsObj = classProp?.GetValue(null);
                if (assetsObj == null) return null;

                const BindingFlags LinkerFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
                var linker = assetsObj.GetType().GetField("shaderLinker", LinkerFlags)?.GetValue(assetsObj)
                             ?? assetsObj.GetType().GetProperty("shaderLinker", LinkerFlags)?.GetValue(assetsObj);
                if (linker == null)
                {
                    linker = assetsObj.GetType().GetFields(LinkerFlags)
                        .Where(f => f.Name.Contains("shader", StringComparison.OrdinalIgnoreCase) && f.Name.Contains("link", StringComparison.OrdinalIgnoreCase))
                        .Select(f => f.GetValue(assetsObj))
                        .FirstOrDefault(v => v != null)
                             ?? assetsObj.GetType().GetProperties(LinkerFlags)
                        .Where(p => p.CanRead && p.Name.Contains("shader", StringComparison.OrdinalIgnoreCase) && p.Name.Contains("link", StringComparison.OrdinalIgnoreCase))
                        .Select(p => p.GetValue(assetsObj))
                        .FirstOrDefault(v => v != null);
                }
                if (linker == null)
                {
                    try
                    {
                        var fields = assetsObj.GetType().GetFields(LinkerFlags)
                            .Select(f => $"{f.Name}:{f.FieldType.Name}")
                            .ToArray();
                        var props = assetsObj.GetType().GetProperties(LinkerFlags)
                            .Select(p => $"{p.Name}:{p.PropertyType.Name}")
                            .ToArray();
                        _log.Warning("[HeroGhost] Assets shaderLinker not found; fields={Fields} props={Props}", string.Join(";", fields), string.Join(";", props));
                    }
                    catch (Exception ex) { LogCatch(ex, "TryFetchShaderFromAssets.FieldsDump"); }
                    _log.Warning("[HeroGhost] Assets shaderLinker not found");
                    return null;
                }

                DumpLinkerMethods(linker);

                var getter = linker.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                        (m.Name.Contains("getShader", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("fetch", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("get", StringComparison.OrdinalIgnoreCase)) &&
                        m.GetParameters().Length == 1);
                if (getter == null)
                {
                    _log.Warning("[HeroGhost] shaderLinker getter not found");
                    return null;
                }

                var argType = getter.GetParameters()[0].ParameterType;
                var arg = argType == typeof(string) ? key.ToString() : key;
                var shader = getter.Invoke(linker, new[] { arg });
                if (shader != null)
                    _log.Information("[HeroGhost] Fetched shader from Assets shaderLinker");
                return shader;
            }
            catch (Exception ex) { LogCatch(ex, "TryFetchShaderFromAssets"); return null; }
        }

        private void DumpLinkerMethods(object linker)
        {
            try
            {
                var methods = linker.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(m => $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})")
                    .ToArray();
                _log.Warning("[HeroGhost] shaderLinker methods: {Methods}", string.Join("; ", methods));
            }
            catch (Exception ex) { LogCatch(ex, "DumpLinkerMethods"); }
        }

        private object? CloneShaderList(object? listObj, BindingFlags flags)
        {
            if (listObj == null) return null;
            try
            {
                var t = listObj.GetType();
                var shader = TryGetFieldOrProp(listObj, "s", flags);
                var next = TryGetFieldOrProp(listObj, "next", flags);
                var clonedNext = CloneShaderList(next, flags);

                object? cloned = null;
                var ctor = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(c =>
                    {
                        var ps = c.GetParameters();
                        if (ps.Length != 2) return false;
                        var ok0 = shader == null || ps[0].ParameterType.IsInstanceOfType(shader) || ps[0].ParameterType.IsAssignableFrom(shader.GetType());
                        var ok1 = clonedNext == null || ps[1].ParameterType.IsInstanceOfType(clonedNext) || ps[1].ParameterType.IsAssignableFrom(t);
                        return ok0 && ok1;
                    });
                if (ctor != null)
                {
                    cloned = ctor.Invoke(new[] { shader, clonedNext });
                }
                else
                {
                    cloned = Activator.CreateInstance(t, nonPublic: true);
                    if (cloned != null)
                    {
                        TrySetFieldOrProp(cloned, "s", shader, flags);
                        TrySetFieldOrProp(cloned, "next", clonedNext, flags);
                    }
                }
                return cloned;
            }
            catch (Exception ex) { LogCatch(ex, "CloneShaderList"); return null; }
        }

        private int CountShaderList(object? listObj, BindingFlags flags, int depth = 0)
        {
            if (listObj == null || depth > 64) return 0;
            try
            {
                var next = TryGetFieldOrProp(listObj, "next", flags);
                return 1 + CountShaderList(next, flags, depth + 1);
            }
            catch (Exception ex) { LogCatch(ex, "CountShaderList"); return 0; }
        }

        private object? TryExtractShaderFromList(object? listObj, BindingFlags flags, bool preferBase = false, bool preferColorMap = false)
        {
            if (listObj == null) return null;
            try
            {
                object? best = null;
                var cursor = listObj;
                for (var depth = 0; depth < 64 && cursor != null; depth++)
                {
                    var shader = TryGetFieldOrProp(cursor, "s", flags);
                    if (shader != null)
                    {
                        var name = shader.GetType().FullName ?? string.Empty;
                        if (preferColorMap && name.Contains("colormap", StringComparison.OrdinalIgnoreCase))
                            return shader;
                        if (!preferBase || name.Contains("Base2d", StringComparison.OrdinalIgnoreCase))
                            return shader;
                        best ??= shader;
                    }
                    cursor = TryGetFieldOrProp(cursor, "next", flags);
                }
                return best;
            }
            catch (Exception ex) { LogCatch(ex, "TryExtractShaderFromList"); return null; }
        }

        private object? TryExtractShaderKeyFromList(object? listObj, BindingFlags flags, int depth = 0)
        {
            if (listObj == null || depth > 64) return null;
            try
            {
                var shader = TryGetFieldOrProp(listObj, "s", flags);
                var key = TryGetShaderKey(shader!, flags);
                if (key != null) return key;
                var next = TryGetFieldOrProp(listObj, "next", flags);
                return TryExtractShaderKeyFromList(next, flags, depth + 1);
            }
            catch (Exception ex) { LogCatch(ex, "TryExtractShaderKeyFromList"); return null; }
        }

        private void DumpShaderListTypes(string label, object? listObj, BindingFlags flags, int depth = 0)
        {
            if (listObj == null || depth > 32) return;
            try
            {
                var shader = TryGetFieldOrProp(listObj, "s", flags);
                var name = shader?.GetType().FullName ?? "null";
                _log.Information("[HeroGhost] {Label} node{Depth}: {Type}", label, depth, name);
                var next = TryGetFieldOrProp(listObj, "next", flags);
                if (next != null)
                    DumpShaderListTypes(label, next, flags, depth + 1);
            }
            catch (Exception ex) { LogCatch(ex, "DumpShaderListTypes"); }
        }

        private object? CreateSingleShaderList(object? listPrototype, object shader, BindingFlags flags)
        {
            if (listPrototype == null || shader == null) return null;
            try
            {
                var t = listPrototype.GetType();
                var ctor = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(c =>
                    {
                        var ps = c.GetParameters();
                        return ps.Length == 2 && (ps[0].ParameterType.IsInstanceOfType(shader) || ps[0].ParameterType.IsAssignableFrom(shader.GetType()));
                    });
                if (ctor != null)
                {
                    return ctor.Invoke(new[] { shader, null });
                }
                var inst = Activator.CreateInstance(t, nonPublic: true);
                if (inst != null)
                {
                    TrySetFieldOrProp(inst, "s", shader, flags);
                    TrySetFieldOrProp(inst, "next", null, flags);
                }
                return inst;
            }
            catch (Exception ex) { LogCatch(ex, "CreateSingleShaderList"); return null; }
        }

        private bool TryApplyStandaloneMaterial(object heroSpr, object ghostSpr, BindingFlags flags)
        {
            try
            {
                var fallback = TryCreateBaseShader(heroSpr.GetType().Assembly) ?? TryCreateBaseShader(ghostSpr.GetType().Assembly);
                if (fallback == null) return false;

                var tex = TryGetFieldOrProp(heroSpr, "texture", flags)
                          ?? TryGetFieldOrProp(heroSpr, "tex", flags)
                          ?? TryGetFieldOrProp(heroSpr, "tile", flags);
                var nrm = TryGetFieldOrProp(heroSpr, "nrmTex", flags)
                          ?? TryGetFieldOrProp(heroSpr, "normalTex", flags)
                          ?? TryGetFieldOrProp(heroSpr, "normalMap", flags);
                var colorMap = TryGetFieldOrProp(heroSpr, "colorMap", flags);

                TrySetFieldOrProp(ghostSpr, "shader", fallback, flags);
                TrySetFieldOrProp(ghostSpr, "mat", fallback, flags);
                TrySetFieldOrProp(ghostSpr, "material", fallback, flags);
                ApplyShaderTargets(ghostSpr, fallback, flags);

                if (tex != null)
                {
                    TrySetFieldOrProp(ghostSpr, "texture", tex, flags);
                    TrySetFieldOrProp(ghostSpr, "tex", tex, flags);
                    TrySetFieldOrProp(ghostSpr, "tile", tex, flags);
                }
                if (nrm != null)
                {
                    TrySetFieldOrProp(ghostSpr, "nrmTex", nrm, flags);
                    TrySetFieldOrProp(ghostSpr, "normalTex", nrm, flags);
                    TrySetFieldOrProp(ghostSpr, "normalMap", nrm, flags);
                }
                if (colorMap != null)
                {
                    TrySetFieldOrProp(ghostSpr, "colorMap", colorMap, flags);
                }

                TrySetFieldOrProp(ghostSpr, "shaders", null, flags);
                ClearShaderKeys(ghostSpr, flags);
                TrySetFieldOrProp(ghostSpr, "shaderQueue", null, flags);
                _log.Information("[HeroGhost] Standalone material applied with Base shader (no shaderLinker)");
                return true;
            }
            catch (Exception ex) { LogCatch(ex, "TryApplyStandaloneMaterial"); return false; }
        }

        private void ApplyShaderListSetter(object targetSpr, object listObj, BindingFlags flags)
        {
            try
            {
                var t = targetSpr.GetType();
                var setter = t.GetMethods(flags)
                    .FirstOrDefault(m =>
                        (m.Name.Equals("set_shaders", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Equals("setShaders", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Equals("applyShaders", StringComparison.OrdinalIgnoreCase)) &&
                        m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType.IsInstanceOfType(listObj));
                setter?.Invoke(targetSpr, new[] { listObj });
                if (setter != null)
                    _log.Information("[HeroGhost] Applied shader list via {Setter}", setter.Name);
            }
            catch (Exception ex) { LogCatch(ex, "ApplyShaderListSetter"); }
        }

        private void ApplyShaderKeySetter(object targetSpr, object key, BindingFlags flags)
        {
            try
            {
                var t = targetSpr.GetType();
                var setter = t.GetMethods(flags)
                    .FirstOrDefault(m =>
                        (m.Name.Equals("set_shaderKey", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Equals("setShaderKey", StringComparison.OrdinalIgnoreCase)) &&
                        m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType.IsInstanceOfType(key));
                setter?.Invoke(targetSpr, new[] { key });
                if (setter != null)
                    _log.Information("[HeroGhost] Applied shader key via {Setter}", setter.Name);
            }
            catch (Exception ex) { LogCatch(ex, "ApplyShaderKeySetter"); }
        }

        private void ApplyShaderTargets(object targetSpr, object shader, BindingFlags flags)
        {
            try
            {
                var t = targetSpr.GetType();
                var fields = t.GetFields(flags)
                    .Where(f => f.Name.Contains("shader", StringComparison.OrdinalIgnoreCase) && !f.Name.Equals("shaders", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                foreach (var f in fields)
                {
                    if (f.FieldType.IsInstanceOfType(shader) || f.FieldType.IsAssignableFrom(shader.GetType()))
                    {
                        try { f.SetValue(targetSpr, shader); _log.Information("[HeroGhost] Applied shader to field {Field}", f.Name); } catch (Exception ex) { LogCatch(ex, "ApplyShaderTargets(field)"); }
                    }
                }

                var props = t.GetProperties(flags)
                    .Where(p => p.CanWrite && p.Name.Contains("shader", StringComparison.OrdinalIgnoreCase) && !p.Name.Equals("shaders", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                foreach (var p in props)
                {
                    if (p.PropertyType.IsInstanceOfType(shader) || p.PropertyType.IsAssignableFrom(shader.GetType()))
                    {
                        try { p.SetValue(targetSpr, shader); _log.Information("[HeroGhost] Applied shader to prop {Prop}", p.Name); } catch (Exception ex) { LogCatch(ex, "ApplyShaderTargets(prop)"); }
                    }
                }
            }
            catch (Exception ex) { LogCatch(ex, "ApplyShaderTargets"); }
        }

        private void DumpShaderTargets(object targetSpr, BindingFlags flags)
        {
            try
            {
                var t = targetSpr.GetType();
                var fields = t.GetFields(flags)
                    .Where(f => f.Name.Contains("shader", StringComparison.OrdinalIgnoreCase))
                    .Select(f => $"{f.Name}:{f.FieldType.Name}={ValueSafe(() => f.GetValue(targetSpr))}")
                    .ToArray();
                var props = t.GetProperties(flags)
                    .Where(p => p.Name.Contains("shader", StringComparison.OrdinalIgnoreCase))
                    .Select(p => $"{p.Name}:{p.PropertyType.Name}={ValueSafe(() => p.GetValue(targetSpr))}")
                    .ToArray();
                _log.Warning("[HeroGhost] Sprite shader targets fields={Fields} props={Props}", string.Join(";", fields), string.Join(";", props));
            }
            catch (Exception ex) { LogCatch(ex, "DumpShaderTargets"); }
        }

        private static string ValueSafe(Func<object?> getter)
        {
            try
            {
                var v = getter();
                return v == null ? "null" : v.GetType().Name;
            }
            catch (Exception ex) { return $"err:{ex.Message}"; }
        }

        private object? TryCreateBaseShader(Assembly asm)
        {
            try
            {
                var t = asm.GetType("dc.shader.Base2d") ?? asm.GetType("dc.h3d.shader.Base2d");
                if (t != null) return Activator.CreateInstance(t);
                // fallback to ghost shader (colored) to get anything visible
                var ghost = asm.GetType("dc.shader.Ghost");
                if (ghost != null) return Activator.CreateInstance(ghost, args: new object?[] { null, null });
            }
            catch (Exception ex) { LogCatch(ex, "TryCreateBaseShader"); }
            return null;
        }


        private void CopySpriteAllFields(object heroSpr, object ghostSpr)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "x","y","z","cx","cy","xr","yr","parent","children","_hxPtr","HashlinkPointer","HashlinkObj"
            };

            try
            {
                var heroType = heroSpr.GetType();
                foreach (var hf in heroType.GetFields(Flags))
                {
                    if (skip.Contains(hf.Name)) continue;
                    var value = hf.GetValue(heroSpr);
                    if (value == null) continue;
                    TrySetFieldOrProp(ghostSpr, hf.Name, value, Flags);
                }

                foreach (var hp in heroType.GetProperties(Flags))
                {
                    if (!hp.CanRead || skip.Contains(hp.Name)) continue;
                    object? value;
                    try { value = hp.GetValue(heroSpr); } catch { continue; }
                    if (value == null) continue;
                    TrySetFieldOrProp(ghostSpr, hp.Name, value, Flags);
                }

                _log.Information("[HeroGhost] Copied generic sprite fields");
            }
            catch (Exception ex) { LogCatch(ex, "CopySpriteAllFields"); }
        }

        private object? TryCloneSprite(object heroSpr)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            try
            {
                var t = heroSpr.GetType();
                var cloneMethod = t.GetMethods(Flags)
                    .FirstOrDefault(m => m.GetParameters().Length == 0 &&
                                         (string.Equals(m.Name, "clone", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(m.Name, "copy", StringComparison.OrdinalIgnoreCase)));
                if (cloneMethod != null)
                {
                    var res = cloneMethod.Invoke(heroSpr, Array.Empty<object?>());
                    if (res != null)
                    {
                        _log.Information("[HeroGhost] Sprite cloned via {Method}", cloneMethod.Name);
                        return res;
                    }
                }

                var copyFrom = t.GetMethods(Flags)
                    .FirstOrDefault(m => string.Equals(m.Name, "copyFrom", StringComparison.OrdinalIgnoreCase) &&
                                         m.GetParameters().Length == 1 &&
                                         m.GetParameters()[0].ParameterType.IsInstanceOfType(heroSpr));
                if (copyFrom != null)
                {
                    var target = Activator.CreateInstance(t);
                    if (target != null)
                    {
                        copyFrom.Invoke(target, new[] { heroSpr });
                        _log.Information("[HeroGhost] Sprite cloned via copyFrom");
                        return target;
                    }
                }

                var memberwise = t.GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
                if (memberwise != null)
                {
                    var res = memberwise.Invoke(heroSpr, null);
                    if (res != null)
                    {
                        _log.Information("[HeroGhost] Sprite cloned via MemberwiseClone");
                        return res;
                    }
                }
            }
            catch (Exception ex) { LogCatch(ex, "TryCloneSprite"); }

            return null;
        }

        private object? TryCreateSpriteLikeHero(object heroSpr)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            try
            {
                var sprType = heroSpr.GetType();
                var lib = TryGetFieldOrProp(heroSpr, "lib", Flags);
                var groupObj = TryGetFieldOrProp(heroSpr, "groupName", Flags) ?? TryGetFieldOrProp(heroSpr, "group", Flags) ?? TryGetFieldOrProp(heroSpr, "name", Flags);
                var ctorMatch = sprType.GetConstructors(Flags)
                    .OrderBy(c => c.GetParameters().Length)
                    .FirstOrDefault(c =>
                    {
                        var ps = c.GetParameters();
                        if (ps.Length == 0) return true;
                        if (ps.Length == 2 && lib != null && ps[0].ParameterType.IsInstanceOfType(lib) && ps[1].ParameterType == typeof(string)) return true;
                        if (ps.Length == 1 && lib != null && ps[0].ParameterType.IsInstanceOfType(lib)) return true;
                        return false;
                    });

                object? sprInstance = null;
                if (ctorMatch != null)
                {
                    var ps = ctorMatch.GetParameters();
                    var args = new object?[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (lib != null && ps[i].ParameterType.IsInstanceOfType(lib))
                            args[i] = lib;
                        else if (ps[i].ParameterType == typeof(string))
                            args[i] = groupObj?.ToString() ?? "hero";
                        else
                            args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                    }
                    sprInstance = ctorMatch.Invoke(args);
                    if (sprInstance != null)
                        _log.Information("[HeroGhost] Sprite created via ctor {Ctor}", ctorMatch.ToString());
                }
                else
                {
                    sprInstance = Activator.CreateInstance(sprType);
                    if (sprInstance != null)
                        _log.Information("[HeroGhost] Sprite created via default ctor");
                }

                if (sprInstance != null)
                {
                    if (lib != null)
                        TrySetFieldOrProp(sprInstance, "lib", lib, Flags);
                    if (groupObj != null)
                        TrySetFieldOrProp(sprInstance, "groupName", groupObj, Flags);
                }

                return sprInstance;
            }
            catch (Exception ex) { LogCatch(ex, "TryCreateSpriteLikeHero"); return null; }
        }

        private void AssignSpriteToGhost(object ghost, object sprite)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var t = ghost.GetType();
            var applied = false;

            foreach (var name in new[] { "sprite", "spr" })
            {
                try
                {
                    var f = t.GetField(name, Flags);
                    if (f != null && f.FieldType.IsInstanceOfType(sprite))
                    {
                        f.SetValue(ghost, sprite);
                        applied = true;
                        _log.Information("[HeroGhost] Assigned sprite via field {Field}", name);
                        break;
                    }
                    var p = t.GetProperty(name, Flags);
                    if (p?.CanWrite == true && p.PropertyType.IsInstanceOfType(sprite))
                    {
                        p.SetValue(ghost, sprite);
                        applied = true;
                        _log.Information("[HeroGhost] Assigned sprite via property {Property}", name);
                        break;
                    }
                }
                catch (Exception ex) { LogCatch(ex, "AssignSpriteToGhost"); }
            }

            if (!applied)
            {
                try
                {
                    var setter = t.GetMethods(Flags)
                        .FirstOrDefault(m => (m.Name == "set_sprite" || m.Name == "set_spr") &&
                                             m.GetParameters().Length == 1 &&
                                             m.GetParameters()[0].ParameterType.IsInstanceOfType(sprite));
                    if (setter != null)
                    {
                        setter.Invoke(ghost, new[] { sprite });
                        applied = true;
                        _log.Information("[HeroGhost] Assigned sprite via setter {Setter}", setter.Name);
                    }
                }
                catch (Exception ex) { LogCatch(ex, "AssignSpriteToGhost"); }
            }

            if (!applied)
                _log.Warning("[HeroGhost] Failed to assign sprite to ghost type {Type}", t.FullName);
        }



        public void Reset()
        {
            _ghost = null;
            _levelRef = null;
            _gameRef = null;
            _heroSourceRef = null;
            _heroShaderSnapshot = null;
            _heroShaderListSnapshot = null;
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
                "dc.en.Mob",
                "dc.en.Active",
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
                DefaultEntityType,
                "dc.en.Mob",
                "dc.en.Entity",
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

        private bool TryTeleportLike(object ghost, int cx, int cy, double xr, double yr)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var names = new[] { "safeTpTo", "teleportTo", "teleport", "tpTo" };

            foreach (var name in names)
            {
                var methods = ghost.GetType().GetMethods(Flags)
                    .Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

                foreach (var m in methods.OrderBy(m => m.GetParameters().Length))
                {
                    var args = BuildTeleportArgs(m.GetParameters(), cx, cy, xr, yr);
                    if (args == null) continue;

                    try
                    {
                        m.Invoke(ghost, args);
                        return true;
                    }
                    catch (Exception ex) { LogCatch(ex, "TryTeleportLike"); }
                }
            }

            return false;
        }

        private object?[]? BuildTeleportArgs(ParameterInfo[] ps, int cx, int cy, double xr, double yr)
        {
            try
            {
                if (ps.Length == 2 &&
                    IsNumericAssignable(ps[0].ParameterType, cx) &&
                    IsNumericAssignable(ps[1].ParameterType, cy))
                {
                    return new object?[]
                    {
                        Convert.ChangeType(cx, ps[0].ParameterType),
                        Convert.ChangeType(cy, ps[1].ParameterType)
                    };
                }

                if (ps.Length == 3 &&
                    IsNumericAssignable(ps[0].ParameterType, cx) &&
                    IsNumericAssignable(ps[1].ParameterType, cy))
                {
                    if (IsNumericAssignable(ps[2].ParameterType, xr))
                        return new object?[]
                        {
                            Convert.ChangeType(cx, ps[0].ParameterType),
                            Convert.ChangeType(cy, ps[1].ParameterType),
                            Convert.ChangeType(xr, ps[2].ParameterType)
                        };

                    if (IsNumericAssignable(ps[2].ParameterType, yr))
                        return new object?[]
                        {
                            Convert.ChangeType(cx, ps[0].ParameterType),
                            Convert.ChangeType(cy, ps[1].ParameterType),
                            Convert.ChangeType(yr, ps[2].ParameterType)
                        };
                }

                if (ps.Length >= 4 &&
                    IsNumericAssignable(ps[0].ParameterType, cx) &&
                    IsNumericAssignable(ps[1].ParameterType, cy) &&
                    IsNumericAssignable(ps[2].ParameterType, xr) &&
                    IsNumericAssignable(ps[3].ParameterType, yr))
                {
                    var args = new object?[ps.Length];
                    args[0] = Convert.ChangeType(cx, ps[0].ParameterType);
                    args[1] = Convert.ChangeType(cy, ps[1].ParameterType);
                    args[2] = Convert.ChangeType(xr, ps[2].ParameterType);
                    args[3] = Convert.ChangeType(yr, ps[3].ParameterType);
                    for (int i = 4; i < ps.Length; i++)
                    {
                        args[i] = ps[i].HasDefaultValue
                            ? ps[i].DefaultValue
                            : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                    }
                    return args;
                }
            }
            catch (Exception ex) { LogCatch(ex, "BuildTeleportArgs"); }

            return null;
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

                var heroSpr = TryGetFieldOrProp(heroRef, "sprite", Flags) ?? TryGetFieldOrProp(heroRef, "spr", Flags);
                if (heroSpr != null)
                {
                    _heroShaderListSnapshot = CloneShaderList(TryGetFieldOrProp(heroSpr, "shaders", Flags), Flags);
                    if (_heroShaderListSnapshot != null)
                    {
                        var count = CountShaderList(_heroShaderListSnapshot, Flags);
                        _log.Information("[HeroGhost] Captured hero shader list snapshot (count={Count})", count);
                    }
                }
                _heroShaderSnapshot = TryGetFieldOrProp(heroSpr, "shader", Flags)
                                       ?? TryGetFieldOrProp(heroSpr, "mat", Flags)
                                       ?? TryGetFieldOrProp(heroSpr, "material", Flags);
                if (_heroShaderSnapshot != null)
                    _log.Information("[HeroGhost] Captured hero shader snapshot ({Type})", _heroShaderSnapshot.GetType().FullName);

                var initOk = TryInitSpriteFromHero(heroRef, ghost);

                object? ghostSpr = TryGetFieldOrProp(ghost, "sprite", Flags) ?? TryGetFieldOrProp(ghost, "spr", Flags);

                if (heroSpr != null && ghostSpr == null)
                {
                    // Prefer cloning to keep shader/material info; fall back to construct.
                    var cloned = TryCloneSprite(heroSpr);
                    if (cloned != null)
                    {
                        AssignSpriteToGhost(ghost, cloned);
                        _log.Information("[HeroGhost] Cloned hero sprite onto ghost");
                    }
                    else
                    {
                        var created = TryCreateSpriteLikeHero(heroSpr);
                        if (created != null)
                        {
                            AssignSpriteToGhost(ghost, created);
                            _log.Information("[HeroGhost] Created new sprite like hero");
                        }
                    }

                    ghostSpr = TryGetFieldOrProp(ghost, "sprite", Flags) ?? TryGetFieldOrProp(ghost, "spr", Flags);
                }

                if (initOk && heroSpr != null && ghostSpr != null)
                {
                    if (_heroShaderSnapshot != null && TryGetFieldOrProp(ghostSpr, "shader", Flags) == null)
                    {
                        ApplyShaderSetter(ghostSpr, _heroShaderSnapshot, Flags);
                        _log.Information("[HeroGhost] Applied shader snapshot pre-init ({Type})", _heroShaderSnapshot.GetType().FullName);
                        _heroShaderSnapshot = null;
                    }

                    TryCopyShaderAndMaterial(heroSpr, ghostSpr);
                    TryCopySpriteAppearance(heroSpr, ghostSpr);
                    CopySpriteAllFields(heroSpr, ghostSpr);
                    TrySyncSpriteFrame(heroRef, ghost);
                }
                TryApplyNormal(heroRef, ghost);

                // spriteUpdate only if sprite exists
                if (ghostSpr != null)
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
