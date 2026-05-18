using MagusStudios.WaveFunctionCollapse;
using UnityEditor;
using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    [CustomEditor(typeof(CustomRuleTile), true)]
    [CanEditMultipleObjects]
    public class CustomRuleTileEditor : RuleTileEditor
    {
        public override void RuleOnGUI(Rect rect, Vector3Int position, int neighbor)
        {
            switch (neighbor)
            {
                case CustomRuleTile.Neighbor.SpecifiedTile:
                    GUI.DrawTexture(rect, arrows[GetArrowIndex(position)]);
                    DrawTintOverlay(rect, new Color(1f, .4f, 1f, 0.28f));
                    break;
                case CustomRuleTile.Neighbor.NotSpecifiedTile:
                    GUI.DrawTexture(rect, arrows[9]);
                    DrawTintOverlay(rect, new Color(1f, .4f, 1f, 0.28f));
                    break;
                default:
                    base.RuleOnGUI(rect, position, neighbor);
                    break;
            }
        }

        private static void DrawTintOverlay(Rect rect, Color color)
        {
            var prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, EditorGUIUtility.whiteTexture);
            GUI.color = prev;
        }
    }
}
