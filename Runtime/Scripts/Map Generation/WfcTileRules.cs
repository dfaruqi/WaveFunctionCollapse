using AYellowpaper.SerializedCollections;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(fileName = "WfcModuleSet", menuName = "Wave Function Collapse/WfcTileRules")]
    public class WfcTileRules : ScriptableObject
    {
        public SerializedDictionary<int, WfcTemplate.TileModule> Modules;
    }
}