using AYellowpaper.SerializedCollections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(fileName = "WfcModuleSet", menuName = "Wave Function Collapse/WfcTemplate")]
    public class WfcTemplate : ScriptableObject
    {
        

        public TileDatabase TileDatabase;
        public WfcTileRules TileRules;
        public WfcWeights Weights;
        public int DefaultTileKey = 0;

        public void ScanTilemapAndOverwrite(Tilemap tilemap)
        {
            TileDatabase tileDatabase = TileDatabase;

            if (tileDatabase == null)
            {
                Debug.LogError($"[{nameof(WfcTemplate)}] Tile Database is null. Aborting.");
            }

            SerializedDictionary<int, WfcTileRules.AllowedNeighbors> newModules =
                new SerializedDictionary<int, WfcTileRules.AllowedNeighbors>();

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
                    module = new WfcTileRules.AllowedNeighbors()
                    {
                        Neighbors = new SerializedDictionary<Direction, SerializedHashSet<int>>()
                    };
                    module.Neighbors[Direction.Up] = new SerializedHashSet<int>();
                    module.Neighbors[Direction.Down] = new SerializedHashSet<int>();
                    module.Neighbors[Direction.Left] = new SerializedHashSet<int>();
                    module.Neighbors[Direction.Right] = new SerializedHashSet<int>();
                    newModules.Add(tileKey, module);
                }

                //update its compatible neighbors
                foreach (Direction direction in DirectionExtension.EnumerateAll())
                {
                    TileBase neighborTileBase = tilemap.GetTile(pos + direction.ToVector3Int());
                    if (neighborTileBase == null) continue;
                    if (!tileDatabase.TryGetKeyFromMapTile(neighborTileBase, out int neighborTileKey))
                    {
                        Debug.LogError($"Tile \"{neighborTileBase.name}\" not found in the database. Aborting.");
                        return;
                    }

                    newModules[tileKey].Neighbors[direction].Add(neighborTileKey);
                }

                newModules[tileKey] = module;
            }

            TileRules.Modules = newModules;
        }

        public void ScanTilemapForNewTiles(Tilemap tilemap)
        {
            TileDatabase tileDatabase = TileDatabase;

            if (tileDatabase == null)
            {
                Debug.LogError($"[{nameof(WfcTemplate)}] Tile Database is null. Aborting.");
            }

            SerializedDictionary<int, WfcTileRules.AllowedNeighbors> newModules = new SerializedDictionary<int, WfcTileRules.AllowedNeighbors>();

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
                    module = new WfcTileRules.AllowedNeighbors
                    {
                        Neighbors = new SerializedDictionary<Direction, SerializedHashSet<int>>()
                    };
                    module.Neighbors[Direction.Up] = new SerializedHashSet<int>();
                    module.Neighbors[Direction.Down] = new SerializedHashSet<int>();
                    module.Neighbors[Direction.Left] = new SerializedHashSet<int>();
                    module.Neighbors[Direction.Right] = new SerializedHashSet<int>();
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

                    newModules[tileKey].Neighbors[direction].Add(neighborTileKey);
                }

                newModules[tileKey] = module;
            }

            TileRules.Modules = newModules;
        }
    }
}