using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace DeadCellsMultiplayerMod.Rpc.Data
{
    public class EntityUpdateInfo
    {
        public long Tick { get; set; } = DateTime.UtcNow.Ticks;
        public string GUID { get; set; } = "";

        public Vector2? NewSpriteScale { get; set; }
        public Vector2? NewSpriteXY { get; set; }
        public Vector2? NewPosition { get; set; }
        public Vector2? NewRXY { get; set; }
        public int? NewDir { get; set; }
        public Vector2? NewVelocity { get; set; }
        public string? NewAnimId { get; set; }
        public int NewAnimFrame { get; set; }
    }
}
