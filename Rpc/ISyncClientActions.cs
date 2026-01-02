using DeadCellsMultiplayerMod.Rpc.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace DeadCellsMultiplayerMod.Rpc
{
    public interface ISyncClientActions : ISyncCommon
    {
        Task SetGUID(string guid);
        Task SetSeed(int seed);
    }
}
