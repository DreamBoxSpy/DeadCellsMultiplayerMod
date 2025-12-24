using Hashlink.Proxy.DynamicAccess;
using System;
using ModCore.Events.Interfaces.Game;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Mods;
using ModCore.Events.Interfaces.Game.Save;
using Serilog;
using System.Net;
using System.Reflection;
using System.Collections;
using dc.en;
using dc.pr;
using dc.cine;
using ModCore.Utitities;
using ModCore.Events;
using ModCore.Serialization;

using dc.en.inter;
using dc.level;
using dc.hl.types;
using HaxeProxy.Runtime;
using dc.libs.heaps.slib;

namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry(ModInfo info) : ModBase(info),
        IOnGameEndInit,
        IOnHeroInit,
        IOnHeroUpdate,
        IOnFrameUpdate,
        IOnAfterLoadingSave
    {
        public static ModEntry? Instance { get; private set; }
        private static GameDataSync? _cachedGameDataSync;
        private bool _ready;

        private object? _lastLevelRef;
        private object? _lastGameRef;
        private string? _remoteLevelText;

        private NetRole _netRole = NetRole.None;
        private NetNode? _net;

        private static int _beheadedWakeCounter;

        private bool _initialGhostSpawned;

        public dc.pr.Game? game;

        public Hero _companion = null;
        Hero me = null;

        int players_count = 2;

        private GhostHero? _ghost;

        private ZDoor zDoor;

        public Hero[] heroes = Array.Empty<Hero>();

        dc.hxbit.Serializer ser;




        public void OnGameEndInit()
        {
            _ready = true;
            _beheadedWakeCounter = 0;
            GameMenu.AllowGameDataHooks = false;
            GameMenu.SetRole(NetRole.None);
            var seed = GameMenu.ForceGenerateServerSeed("OnGameEndInit");
            Logger.Information("[NetMod] GameEndInit - ready (use menu) seed={Seed}", seed);
        }

        public override void Initialize()
        {
            Instance = this;
            GameMenu.Initialize(Logger);
            Hook_Game.init += Hook_mygameinit;
            Logger.Debug("[NetMod] Hook_mygameinit attached");
            Hook_Hero.wakeup += hook_hero_wakeup;
            Logger.Debug("[NetMod] Hook_Hero.wakeup attached");

            Hook_Hero.onLevelChanged += hook_level_changed;
            Logger.Debug("[NetMod] Hook_Hero.onLevelChanged attached");
            Hook__LevelTransition.gotoSub += hook_gotosub;
            Logger.Debug("[NetMod] Hook__LevelTransition.gotoSub attached");
            Hook_ZDoor.enter += Hook_ZDoor_enter;
            Hook_LevelTransition.loadNewLevel += Hook__LevelTransition_loadnewlevel;
            Hook_Game.initHero += Hook_Game_inithero;
            Hook_Game.activateSubLevel += hook_game_activateSubLevel;
            Hook__AfterZDoor.__constructor__ += Hook__AfterZDoor_intcompanion;

        }


        private void Hook__AfterZDoor_intcompanion(Hook__AfterZDoor.orig___constructor__ orig, AfterZDoor arg1, Hero hero)
        {
            hero = heroes[0];
            Level level = _companion.set_level(hero._level);
            _companion.awake = false;
            me.awake = false;
            _companion.wakeup(level, hero.cx, hero.cy);
            me.wakeup(level, hero.cx, hero.cy);
            _ghost.ReinitGFX(_companion);
            _ghost.DisableHero(_companion);
            _ghost.ReinitGFX(me);
            orig(arg1, hero);
        }

        private void hook_game_activateSubLevel(Hook_Game.orig_activateSubLevel orig, Game self, LevelMap linkId, int? shouldSave, Ref<bool> outAnim, Ref<bool> shouldSave2)
        {

            orig(self, linkId, shouldSave, outAnim, shouldSave2);
        }

        private Hero Hook_Game_inithero(Hook_Game.orig_initHero orig, Game self, Level cx, int cy, int from, UsableBody fromDeadBody, bool oldLevel, Level e)
        {
            return orig(self, cx, cy, from, fromDeadBody, oldLevel, e);
        }

        private void Hook__LevelTransition_loadnewlevel(Hook_LevelTransition.orig_loadNewLevel orig, LevelTransition self)
        {
            orig(self);
        }


        private void Hook_ZDoor_enter(Hook_ZDoor.orig_enter orig, ZDoor self, Hero h)
        {
            HlAction onComplete = new HlAction(() =>
            {
                var game = Game.Class.ME;
                game.subLevels = me._level.game.subLevels = _companion._level.game.subLevels;
                _LevelTransition levelTransition = LevelTransition.Class;
                if (me._level.map == _companion._level.map)
                {
                    self.destMap = self.destMap;
                }
                levelTransition.gotoSub(self.destMap, self.linkId);
            });
            UseZDoor useZDoor = new UseZDoor(_companion, self, onComplete);
            UseZDoor useZDoor2 = new UseZDoor(me, self, onComplete);
        }




        public LevelTransition hook_gotosub(Hook__LevelTransition.orig_gotoSub orig, dc.level.LevelMap map, int? linkId)
        {
            return orig(map, linkId);
        }





        public void hook_level_changed(Hook_Hero.orig_onLevelChanged orig, Hero self, Level oldLevel)
        {
            me = heroes[0];
            self = heroes[0];
            orig(self, oldLevel);

            if (_netRole == NetRole.None) return;
            SendLevel();
            var remoteCurrentLevelId = _remoteLevelText;
            if (oldLevel == null) return;
            if (zDoor == null) return;
        }


        public void hook_hero_wakeup(Hook_Hero.orig_wakeup orig, Hero self, Level lvl, int cx, int cy)
        {
            if (Array.IndexOf(heroes, self) < 0)
            {
                var newLen = heroes.Length + 1;
                Array.Resize(ref heroes, newLen);
                heroes[newLen - 1] = self;
            }
            me = heroes[0];
            Logger.Warning($"self: {self}");
            Logger.Warning($"heroes[0].lastRoomId: {self.lastRoomId}");
            orig(self, lvl, cx, cy);
            if (_netRole == NetRole.None) return;


            if (heroes.Length < players_count && game != null && me != null)
            {
                _ghost ??= new GhostHero(game, me);
                _companion = _ghost.CreateGhost();
                Logger.Debug($"[NetMod] Hook_Hero.wakeup created ghost = {_companion}");
            }

        }


        public void Hook_mygameinit(Hook_Game.orig_init orig, dc.pr.Game self)
        {
            game = self;
            orig(self);
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

        public static void OnClientReceiveGameData(byte[] bytes)
        {
            ApplyGameDataBytes(bytes);
        }


        public void OnFrameUpdate(double dt)
        {
            if (!_ready) return;
            GameMenu.TickMenu(dt);
        }


        public int last_cx, last_cy;
        private bool mapswitch = false;

        void IOnHeroUpdate.OnHeroUpdate(double dt)
        {
            if (_companion == null) return;
            _ghost.Teleport(me.cx + 5, me.cy, me.xr, me.yr);
            SendHeroCoords();
            ReceiveGhostCoords();
            ReceiveGhostLevel();
        }


        private void SendLevel()
        {
            if (_netRole == NetRole.None) return;
            var net = _net;
            var hero = me;

            if (net == null || hero == null || _companion == null) return;
            net.LevelSend(hero._level.uniqId.ToString());

        }


        private void ReceiveGhostLevel()
        {
            var net = _net;
            var ghost = _ghost;
            if (net == null || ghost == null || _companion == null) return;

            if (!net.TryGetRemoteLevelString(out var remoteLevel) || string.IsNullOrWhiteSpace(remoteLevel))
                return;

            if (string.Equals(_remoteLevelText, remoteLevel, StringComparison.Ordinal))
                return;

            _remoteLevelText = remoteLevel;
        }


        private void SendHeroCoords()
        {
            if (_netRole == NetRole.None) return;

            var net = _net;
            var hero = me;

            if (net == null || hero == null || _companion == null) return;
            if (hero.cx == last_cx && hero.cy == last_cy) return;

            net.TickSend(hero.cx, hero.cy, hero.xr, hero.yr);
            last_cx = hero.cx;
            last_cy = hero.cy;

        }

        private void ReceiveGhostCoords()
        {
            var net = _net;
            var ghost = _ghost;
            if (net == null || ghost == null) return;

            if (net.TryGetRemote(out var rcx, out var rcy, out var rxr, out var ryr))
            {
                ghost.Teleport(rcx, rcy, rxr, ryr);
            }
        }



        private IPEndPoint BuildEndpoint(string ipText, int port)
        {
            if (port <= 0 || port > 65535) port = 1234;
            if (!IPAddress.TryParse(ipText, out var ip))
            {
                ip = IPAddress.Loopback;
            }
            return new IPEndPoint(ip, port);
        }

        public void StartHostFromMenu(string ipText, int port)
        {
            var ep = BuildEndpoint(ipText, port);
            StartHostWithEndpoint(ep);
        }

        public void StartClientFromMenu(string ipText, int port)
        {
            var ep = BuildEndpoint(ipText, port);
            StartClientWithEndpoint(ep);
        }

        private void StartHostWithEndpoint(IPEndPoint ep)
        {
            try
            {
                _net?.Dispose();

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
                GameMenu.SetRole(_netRole);
            }
        }

        private void StartClientWithEndpoint(IPEndPoint ep)
        {
            try
            {
                _net?.Dispose();

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
                GameMenu.SetRole(_netRole);
            }
        }

        public void StopNetworkFromMenu()
        {
            try
            {
                _net?.Dispose();
            }
            catch { }
            _net = null;
            _netRole = NetRole.None;
            GameMenu.NetRef = null;
            GameMenu.SetRole(_netRole);
        }

        private static object? ExtractGameFromLevel(object? levelObj)
        {
            if (levelObj == null) return null;
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = levelObj.GetType();
            return type.GetProperty("game", Flags)?.GetValue(levelObj) ??
                   type.GetField("game", Flags)?.GetValue(levelObj);
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
