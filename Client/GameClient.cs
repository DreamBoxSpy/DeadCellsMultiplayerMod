using DeadCellsMultiplayerMod.Rpc.Data;
using DeadCellsMultiplayerMod.Utils;
using ModCore.Events;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Modules;
using System;
using System.Collections.Generic;
using System.Text;
using static dc.hxsl.Prec;

namespace DeadCellsMultiplayerMod.Client
{
    internal class GameClient(NetNode node, dc.pr.Game game) : IEventReceiver,
        IOnHeroUpdate
    {
        public int Seed { get; set;  }
        public NetNode Node => node;
        public readonly Dictionary<string, GhostHero> heroes = [];

        public void OnHeroUpdate(double dt)
        {
            var ui = new HeroUpdateInfo();
            ui.FillHeroUpdateInfo(game.hero);
            Node.SyncHost.UpdateHero(ui);
        }

        public void OnChangeLevel(string newLevel)
        {
            Node.SyncHost.UpdateHero(new()
            {
                NewLevel = newLevel,
            });
        }

        public virtual void RecvHeroUpdate(HeroUpdateInfo info)
        {
            if(!heroes.TryGetValue(info.GUID, out var gh))
            {
                if(info.NewName is string name)
                {
                    gh = new(game, game.hero)
                    {
                        name = name
                    };
                    heroes.Add(info.GUID, gh);
                }
            }
            if(gh == null)
            {
                return;
            }
            if(info.NewLevel is string newLevel)
            {
                gh.level = newLevel;
            }
            if(info.NewSkin is string newSkin)
            {
                gh.SetSkin(newSkin);
            }
            if(gh.level != game.curLevel.name.ToString())
            {
                gh.king?.dispose();
                gh.king = null;
                return;
            }
            var ks = gh.ReInitKing(game.curLevel);
            ks.ApplyEntityUpdateInfo(info);
        }
    }
}
