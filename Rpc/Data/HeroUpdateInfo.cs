using System;
using System.Collections.Generic;
using System.Text;

namespace DeadCellsMultiplayerMod.Rpc.Data
{
    public class HeroUpdateInfo : EntityUpdateInfo
    {
        public string? NewLevel { get; set; }
        public string? NewName { get; set; }
        public string? NewSkin { get; set; }
    }
}
