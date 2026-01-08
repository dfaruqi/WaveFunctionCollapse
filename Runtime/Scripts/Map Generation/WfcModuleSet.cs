using AYellowpaper.SerializedCollections;
using MagusStudios.Arcanist.Utils;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(fileName = "WfcModuleSet", menuName = "Wave Function Collapse/WfcModuleSet")]
    public class WfcModuleSet : ScriptableObject
    {
        /// <summary>
        /// One module refers to one tile asset and its domain of compatible adjacent tiles for each direction up down left right
        /// </summary>
        [System.Serializable]
        public struct TileModule
        {
            public float weight;
            public SerializedDictionary<Direction, SerializedHashSet<int>> compatibleNeighbors;
        }

        public TileDatabase TileDatabase;
        public SerializedDictionary<int, TileModule> Modules;

        public void ScanTilemapAndOverwrite(Tilemap tilemap)
        {
            TileDatabase tileDatabase = TileDatabase;

            if (tileDatabase == null)
            {
                Debug.LogError($"[{nameof(WfcModuleSet)}] Tile Database is null. Aborting.");
            }

            //var tileDict = new SerializedDictionary<int, WfcModuleSet.TileModule>();
            Dictionary<int, float> weights = Modules.ToDictionary(m => m.Key, m => m.Value.weight);

            SerializedDictionary<int, TileModule> newModules = new SerializedDictionary<int, TileModule>();

            //for each position in the tilemap
            foreach (var pos in tilemap.cellBounds.allPositionsWithin)
            {
                //check if the tile found at this position is in the database
                //abort if it is not
                TileBase tileBase = tilemap.GetTile(pos);
                if (tileBase == null) continue;

                //int tileKey = 0;
                if (!tileDatabase.TryGetKeyFromMapTile(tileBase, out int tileKey))
                {
                    Debug.LogError($"Tile \"{tileBase.name}\" not found in the database. Aborting.");
                    return;
                }

                //store a new constraint module for this tile if we have not encountered it yet
                if (!newModules.TryGetValue(tileKey, out var module))
                {
                    module = new TileModule
                    {
                        weight = weights.TryGetValue(tileKey, out float value) ? value : 1,
                        compatibleNeighbors = new SerializedDictionary<Direction, SerializedHashSet<int>>()
                    };
                    module.compatibleNeighbors[Direction.Up] = new SerializedHashSet<int>();
                    module.compatibleNeighbors[Direction.Down] = new SerializedHashSet<int>();
                    module.compatibleNeighbors[Direction.Left] = new SerializedHashSet<int>();
                    module.compatibleNeighbors[Direction.Right] = new SerializedHashSet<int>();
                    newModules.Add(tileKey, module);
                }

                //update its compatible neighbors
                //TODO, for each adjacent position in the tilemap, if there is a tile there, add it to the corresponding domain (up, down, left, or right)
                foreach (Direction direction in DirectionExtension.EnumerateAll())
                {
                    TileBase neighborTileBase = tilemap.GetTile(pos + direction.ToVector3Int());
                    if (neighborTileBase == null) continue;
                    if (!tileDatabase.TryGetKeyFromMapTile(neighborTileBase, out int neighborTileKey))
                    {
                        Debug.LogError($"Tile \"{neighborTileBase.name}\" not found in the database. Aborting.");
                        return;
                    }
                    newModules[tileKey].compatibleNeighbors[direction].Add(neighborTileKey);
                }

                newModules[tileKey] = module;
            }

            Modules = newModules;
        }

        public void ScanTilemapForNewTiles(Tilemap tilemap)
        {
            TileDatabase tileDatabase = TileDatabase;

            if (tileDatabase == null)
            {
                Debug.LogError($"[{nameof(WfcModuleSet)}] Tile Database is null. Aborting.");
            }

            Dictionary<int, float> weights = Modules.ToDictionary(m => m.Key, m => m.Value.weight);

            SerializedDictionary<int, TileModule> newModules = new SerializedDictionary<int, TileModule>();

            //for each position in the tilemap
            foreach (var pos in tilemap.cellBounds.allPositionsWithin)
            {
                //check if the tile found at this position is in the database
                //abort if it is not
                TileBase tileBase = tilemap.GetTile(pos);
                if (tileBase == null) continue;

                //int tileKey = 0;
                if (!tileDatabase.TryGetKeyFromMapTile(tileBase, out int tileKey))
                {
                    Debug.LogError($"Tile \"{tileBase.name}\" not found in the database. Aborting.");
                    return;
                }

                //store a new constraint module for this tile if we have not encountered it yet
                if (!newModules.TryGetValue(tileKey, out var module))
                {
                    module = new TileModule
                    {
                        weight = weights.TryGetValue(tileKey, out float value) ? value : 1,
                        compatibleNeighbors = new SerializedDictionary<Direction, SerializedHashSet<int>>()
                    };
                    module.compatibleNeighbors[Direction.Up] = new SerializedHashSet<int>();
                    module.compatibleNeighbors[Direction.Down] = new SerializedHashSet<int>();
                    module.compatibleNeighbors[Direction.Left] = new SerializedHashSet<int>();
                    module.compatibleNeighbors[Direction.Right] = new SerializedHashSet<int>();
                    newModules.Add(tileKey, module);
                }

                //update its compatible neighbors
                //TODO, for each adjacent position in the tilemap, if there is a tile there, add it to the corresponding domain (up, down, left, or right)
                foreach (Direction direction in DirectionExtension.EnumerateAll())
                {
                    TileBase neighborTileBase = tilemap.GetTile(pos + direction.ToVector3Int());
                    if (neighborTileBase == null) continue;
                    if (!tileDatabase.TryGetKeyFromMapTile(neighborTileBase, out int neighborTileKey))
                    {
                        Debug.LogError($"Tile \"{neighborTileBase.name}\" not found in the database. Aborting.");
                        return;
                    }
                    newModules[tileKey].compatibleNeighbors[direction].Add(neighborTileKey);
                }

                newModules[tileKey] = module;
            }

            Modules = newModules;
        }
    }
}
