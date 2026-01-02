using dc.en;
using dc.pr;
using HaxeProxy.Runtime;
using ModCore.Utitities;
using Serilog;
using dc.h3d.mat;
using dc.libs.heaps.slib;
using dc;
using Hashlink.Virtuals;
using dc.hl.types;



namespace DeadCellsMultiplayerMod.Client
{
    internal class GhostHero(Game game, Hero me)
    {

        public KingSkin? king;

        public string? lastSkin;
        public string name = "";
        public string level = "";
        public KingSkin CreateGhostKing(Level level)
        {
            king = new KingSkin(level, (int)me.spr.x, (int)me.spr.y);
            king.init();
            king.set_level(level);
            king.set_team(me._team);
            king.setPosCase(me.cx, me.cy, me.xr, me.yr);
            king.visible = true;
            king.initGfx();
            SetSkin(null);
            var miniMap = game.hud.minimap;
            if (miniMap != null && me._level.map == king._level.map)
            {
                miniMap.track(king, 14888237, "minimapHero".AsHaxeString(), null, true, null, null, null);
            }
            SetLabel(king, GameMenu.RemoteUsername);

            return king;
        }


        public KingSkin ReInitKing(Level level)
        {
            if(king == null)
            {
                return CreateGhostKing(level);
            }
            king.set_level(level);
            king.initGfx();
            SetSkin(null);
            king.visible = true;
            var miniMap = game.hud.minimap;
            if (miniMap != null && me._level.map == king._level.map)
            {
                miniMap.track(king, 14888237, "minimapHero".AsHaxeString(), null, true, null, null, null);
            }
            SetLabel(king, name);
            
            return king;
        }

        public void SetSkin(string? id)
        {
            if(id == lastSkin)
            {
                return;
            }
            lastSkin = id ?? "PrisonerDefault";

            if(king == null)
            {
                return;
            }

            dc.String group = "idle".AsHaxeString();
            SpriteLib heroLib = Assets.Class.getHeroLib(Cdb.Class.getSkinInfo(lastSkin.AsHaxeString()));
            king.spr.lib = heroLib;
            Texture normalMapFromGroup = heroLib.getNormalMapFromGroup(group);
            int? dp_ROOM_MAIN_HERO = Const.Class.DP_ROOM_MAIN_HERO;
            king.initSprite(heroLib, group, 0.5, 0.5, dp_ROOM_MAIN_HERO, true, null, normalMapFromGroup);
            king.initColorMap(Cdb.Class.getSkinInfo(lastSkin.AsHaxeString()));
            king.createLight(10, 10, 0, 1);
        }
        
        public void TeleportByPixels(double x, double y)
        {
            king?.setPosPixel(x, y - 0.2d);
        }

        public void SetLabel(Entity entity, string? text)
        {
            if (entity == null) return;
            _Assets _Assets = Assets.Class;
            dc.h2d.Text text_h2d = _Assets.makeText(text?.AsHaxeString(), dc.ui.Text.Class.COLORS.get("ST".AsHaxeString()), true, entity.spr);
            text_h2d.y -= 80;
            text_h2d.x -= 15;
            text_h2d.font.size = 18;
            text_h2d.alpha = 0.8;
            text_h2d.scaleX = 0.6d;
            text_h2d.scaleY = 0.6d;
            text_h2d.textColor = 0;
        }
    }
}
