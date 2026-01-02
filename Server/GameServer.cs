using DeadCellsMultiplayerMod.Rpc.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace DeadCellsMultiplayerMod.Server
{
    internal class GameServer
    {
        public readonly List<NetNode> nodes = [];

        public void OnUpdateHeroInfo(HeroUpdateInfo info)
        {
            foreach(var v in nodes)
            {
                v.SyncClient.UpdateHero(info);
            }
        }
    }
}
