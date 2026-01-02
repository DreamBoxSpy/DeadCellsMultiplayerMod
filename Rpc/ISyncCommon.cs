using DeadCellsMultiplayerMod.Rpc.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace DeadCellsMultiplayerMod.Rpc
{
    public interface ISyncCommon
    {
        public Task Print(string msg);
        Task UpdateHero(HeroUpdateInfo info);
    }
}
