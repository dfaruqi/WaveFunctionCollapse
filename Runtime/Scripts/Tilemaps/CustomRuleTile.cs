using UnityEngine;
using UnityEngine.Tilemaps;

namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(fileName = "CustomRuleTile", menuName = "Tiles/CustomRuleTile")]
    public class CustomRuleTile : RuleTile<CustomRuleTile.Neighbor>
    {
        public TileBase matchTile;

        public class Neighbor : RuleTile.TilingRuleOutput.Neighbor
        {
            public const int SpecifiedTile = 3;
            public const int NotSpecifiedTile = 4;
        }

        public override bool RuleMatch(int neighbor, TileBase other)
        {
            if (other is RuleOverrideTile ot)
                other = ot.m_InstanceTile;

            switch (neighbor)
            {
                case Neighbor.SpecifiedTile:
                    return other == matchTile;
                case Neighbor.NotSpecifiedTile:
                    return other != matchTile;
            }

            return base.RuleMatch(neighbor, other);
        }
    }
}
