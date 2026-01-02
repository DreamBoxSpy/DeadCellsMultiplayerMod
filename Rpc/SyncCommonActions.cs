using DeadCellsMultiplayerMod.Rpc.Data;
using DeadCellsMultiplayerMod.Server;
using System;
using System.Collections.Generic;
using System.Text;

namespace DeadCellsMultiplayerMod.Rpc
{
    internal abstract class SyncCommonActions(NetNode node) : ISyncCommon
    {
        public NetNode Node => node;
        public GameServer? Server => node.Server;
        public Task Print(string msg)
        {
            node.Logger.Information(msg);
            return Task.CompletedTask;
        }

        public abstract Task UpdateHero(HeroUpdateInfo info);
    }
}
