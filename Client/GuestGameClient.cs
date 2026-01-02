using dc.pr;
using System;
using System.Collections.Generic;
using System.Text;

namespace DeadCellsMultiplayerMod.Client
{
    internal class GuestGameClient(Game game, NetNode node) : GameClient(node, game)
    {
    }
}
