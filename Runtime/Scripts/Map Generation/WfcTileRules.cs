using AYellowpaper.SerializedCollections;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(fileName = "WfcModuleSet", menuName = "Wave Function Collapse/WfcTileRules")]
    public class WfcTileRules : ScriptableObject
    {
        /// <summary>
        /// One module refers to one tile asset and its domain of compatible adjacent tiles for each direction up down left right
        /// </summary>
        [System.Serializable]
        public struct AllowedNeighbors
        {
            public SerializedDictionary<Direction, SerializedHashSet<int>> Neighbors;
        }
        
        public SerializedDictionary<int, AllowedNeighbors> Modules;
    }
}