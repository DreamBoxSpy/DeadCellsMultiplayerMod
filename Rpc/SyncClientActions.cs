using DeadCellsMultiplayerMod.Rpc.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace DeadCellsMultiplayerMod.Rpc
{
    internal class SyncClientActions(NetNode node) : SyncCommonActions(node), ISyncClientActions
    {
        public Task SetSeed(int seed)
        {
            Node.Client?.Seed = seed;
            return Task.CompletedTask;
        }

        public override Task UpdateHero(HeroUpdateInfo info)
        {
            Node.Client?.RecvHeroUpdate(info);
            return Task.CompletedTask;
        }

        public Task SetGUID(string guid)
        {
            Node.GUID = guid;
            return Task.CompletedTask;
        }
    }
}
