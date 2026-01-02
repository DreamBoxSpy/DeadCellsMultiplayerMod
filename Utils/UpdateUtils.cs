using dc;
using dc.en;
using DeadCellsMultiplayerMod.Rpc.Data;
using ModCore.Utitities;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace DeadCellsMultiplayerMod.Utils
{
    internal static class UpdateUtils
    {
        public static void FillEntityUpdateInfo(this EntityUpdateInfo info, Entity entity)
        {
            info.NewPosition = new(entity.cx, entity.cy);
            info.NewSpriteXY = new((float)entity.spr.x, (float)entity.spr.y);
            info.NewSpriteScale = new((float)entity.spr.scaleX, (float)entity.spr.scaleY);
            info.NewAnimId = entity.spr.get_anim().spr.groupName.ToString();
            info.NewAnimFrame = entity.spr.get_anim().spr.frame;
            info.NewDir = entity.dir;
            info.NewRXY = new((float)entity.xr, (float)entity.yr);
        }
        public static void FillHeroUpdateInfo(this HeroUpdateInfo info, Hero hero)
        {
            FillEntityUpdateInfo(info, hero);
        }

        public static void ApplyEntityUpdateInfo(this Entity entity, EntityUpdateInfo info)
        {
            if(info.NewPosition is Vector2 newPos)
            {
                entity.cx = (int)newPos.X;
                entity.cy = (int)newPos.Y;
            }
            if (info.NewSpriteXY is Vector2 spriteXY)
            {
                entity.spr.x = spriteXY.X;
                entity.spr.y = spriteXY.Y;
            }
            if(info.NewDir is int dir)
            {
                entity.dir = dir;
            }
            if(info.NewRXY is Vector2 rxy)
            {
                entity.xr = rxy.X;
                entity.yr = rxy.Y;
            }
            if(info.NewSpriteScale is Vector2 spriteScale)
            {
                entity.spr.scaleX = spriteScale.X;
                entity.spr.scaleY = spriteScale.Y;
            }
            if(info.NewAnimId is string animId)
            {
                entity.spr.get_anim().play(animId.AsHaxeString(), null, null);
            }
            if(info.NewAnimFrame is int animFrame)
            {
                entity.spr.get_anim().spr.setFrame(animFrame);
            }
        }
        
    }
}
