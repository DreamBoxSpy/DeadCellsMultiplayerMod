using dc.en;
using dc.pr;
using HaxeProxy.Runtime;
using ModCore.Utitities;
using Serilog;
using dc.h3d.mat;
using dc.libs.heaps.slib;
using dc;



namespace DeadCellsMultiplayerMod
{
    internal class GhostHero
    {
        private readonly dc.pr.Game _game;
        private readonly Hero _me;

        private Texture hero_nrmTex;

        private SpriteLib hero_lib;

        private dc.String hero_group;
        private static ILogger? _log;

        private KingSkin king;

        
        public GhostHero(dc.pr.Game game, Hero me)
        {
            _game = game;
            _me = me;
        }


        public KingSkin CreateGhostKing(Level level)
        {
            king = new KingSkin(level, (int)_me.spr.x, (int)_me.spr.y);
            king.init();
            king.set_level(level);
            king.set_team(_me._team);
            king.setPosCase(_me.cx, _me.cy, _me.xr, _me.yr);
            king.visible = true;
            king.initGfx();
            SetLabel("TEST");
            return king;
        }

        public void Teleport(int x, int y, double? xr, double? yr)
        {
            if (king == null) return;
            king?.setPosCase(x, y, xr, yr);
        }

        public void TeleportByPixels(double x, double y)
        {
            king?.setPosPixel(x, y);
        }

        public void SetLabel(string? text)
        {
            if (king == null || _me == null) return;
            _Assets _Assets = Assets.Class;
            dc.h2d.Text text_h2d = _Assets.makeText(text.AsHaxeString(), dc.ui.Text.Class.COLORS.get("ST".AsHaxeString()), true, king.spr);
            text_h2d.y -= 20;
            // text_h2d.x = (double)king.spr.x / 2;
            // text_h2d.scaleX = 1d;
            // text_h2d.scaleY = 1d;

            
        }
    }
}
