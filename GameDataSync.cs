using System;
using dc;
using dc.hl.types;
using Hashlink.Virtuals;
using dc.level;
using HaxeProxy.Runtime;
using dc.tool;
using System.Text.Json.Nodes;
using dc.en;


namespace DeadCellsMultiplayerMod
{
    internal class GameDataSync
    {
        static Serilog.ILogger _log;


        static public int Seed;

        public GameDataSync(Serilog.ILogger log)
        {
            _log = log;
        }



        public static void user_hook_new_game(Hook_User.orig_newGame orig, 
        User self, 
        int lvl, 
        virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ isTwitch, 
        bool isCustom, 
        bool mode, 
        LaunchMode gdata)
        {
            isCustom = false;
            mode = false;
            Seed = lvl;
            var net = GameMenu.NetRef;
            if (net != null && net.IsHost)
            {
                Seed = GameMenu.ForceGenerateServerSeed("NewGame_hook");
                net.SendSeed(Seed);
            }
            else if (net != null)
            {
                if (GameMenu.TryGetRemoteSeed(out var remoteSeed))
                {
                    Seed = remoteSeed;
                }
            }
            lvl = Seed;
            orig(self, lvl, isTwitch, isCustom, mode, gdata);
        }

        public static ArrayObj hook_generate(Hook_LevelGen.orig_generate orig, 
        LevelGen self, 
        User seed, 
        int ldat, 
        virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ resetCount, 
        Ref<bool> resetCount2)
        {
            ldat = Seed;
            _log.Debug($"hook_generate _companionKing = {ModEntry._companionKing}");
            ModEntry._companionKing = null;
            _log.Debug($"hook_generate _companionKing = {ModEntry._companionKing}");
            return orig(self, seed, ldat, resetCount, resetCount2);
        }
    }
}
