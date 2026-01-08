using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MagusStudios.WaveFunctionCollapse
{
    public static class TileUtils
    {
        /// <summary>
        /// Deterministically hashes a Vector3Int into a uniform integer in [1, n].
        /// Produces consistent results for the same input, ideal for deterministic procedural tiling.
        /// </summary>
        public static int HashPosition(Vector3Int pos, int n)
        {
            unchecked
            {
                int hash = pos.x * 374761393 + pos.y * 668265263 + pos.z * 2147483647;
                hash = (hash ^ (hash >> 13)) * 1274126177;
                hash ^= (hash >> 16);

                return (Mathf.Abs(hash) % n) + 1;
            }
        }

        public static void LoadMapData(Tilemap tilemap, int[,] map, TileDatabase tileDatabase)
        {
            // Clear the tilemap first
            tilemap.ClearAllTiles();

            int width = map.GetLength(0);
            int height = map.GetLength(1);

            // Iterate over the 2D map array
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    int tileId = map[x, y];

                    // Skip if the tile ID is invalid (optional)
                    if (!tileDatabase.TryGetTile(tileId, out Tile tile))
                    {
                        Debug.LogWarning($"[{nameof(TileUtils)}] Tried to load a tile with invalid id {tileId}");
                        continue;
                    }

                    // Set the tile at the corresponding position
                    tilemap.SetTile(new Vector3Int(x, y, 0), tile);
                }
            }

            // Refresh the tilemap so it updates visually
            tilemap.RefreshAllTiles();
        }
    }

    public static class TilemapExtension
    {
        /// <summary>
        /// Must be used for tiles with custom spawning logic, i.e. GameObjectTile
        /// </summary>
        /// <param name="tilemap"></param>
        /// <param name="position"></param>
        /// <param name="tile"></param>
        public static void SetTileDynamic(this Tilemap tilemap, Vector3Int position, TileBase tile)
        {
            //TODO spawn gameObjects for tiles of type GameObjectTile in overloaded function
            tilemap.SetTile(position, tile);
        }

        /// <summary>
        /// Must be used for tiles with custom spawning logic, i.e. GameObjectTile
        /// </summary>
        /// <param name="tilemap"></param>
        /// <param name="position"></param>
        /// <param name="tile"></param>
        public static void SetTile(this Tilemap tilemap, Vector3Int position, GameObjectTile tile)
        {
            // tilemap.SetTile(position, tile);
            // if (tile.Prefab == null)
            // {
            //     Debug.LogError($"[{nameof(TilemapExtension)}] Tried to spawn a null prefab from GameObjectTile {tile.name} at position {position}");
            //     return;
            // }
            //
            // TilemapController tilemapController = tilemap.GetComponent<TilemapController>();
            // if(tilemapController == null)
            // {
            //     Debug.LogError($"[{nameof(TilemapExtension)}] Tried to spawn a prefab from GameObjectTile {tile.name} at position {position}, but there was no TilemapController attached to the tilemap to manage it.");
            // }
            // GameObject go = GameObject.Instantiate(tile.Prefab, tilemap.GetCellCenterWorld(position), Quaternion.identity);
        }
    }
}
