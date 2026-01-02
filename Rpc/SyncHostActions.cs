using DeadCellsMultiplayerMod.Rpc.Data;
using DeadCellsMultiplayerMod.Server;
using System;
using System.Collections.Generic;
using System.Text;

namespace DeadCellsMultiplayerMod.Rpc
{
    internal class SyncHostActions(NetNode node) : SyncCommonActions(node), ISyncHostActions
    {
        public override Task UpdateHero(HeroUpdateInfo info)
        {
            info.GUID = Node.GUID;
            Server!.OnUpdateHeroInfo(info);
            return Task.CompletedTask;
        }
    }
}
