using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MagusStudios.WaveFunctionCollapse
{
    public static class TileUtils
    {
        public static int TILE_SIZE = 1; // In Unity Coordinate System units

        /// <summary>
        /// Deterministically hashes a Vector2Int into a uniform integer in [0, n).
        /// </summary>
        public static int HashPosition(Vector2Int pos, int n)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(bytes,       pos.x);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[4..],  pos.y);

            var ro = (ReadOnlySpan<byte>)bytes;
            uint h = MurmurHash3.Hash32(ref ro, seed: 0);

            return (int)(h % (uint)n);
        }
        
        public static float HashPositionFloat(Vector2Int pos)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, pos.x);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], pos.y);

            ReadOnlySpan<byte> readOnly = bytes;
            uint h = MurmurHash3.Hash32(ref readOnly, seed: 0);

            return h * (1f / 4294967296f);
        }

        public static uint HashWorldBlock(uint seed, Vector2Int chunk, byte block)
        {
            Span<byte> buffer = stackalloc byte[9];

            BinaryPrimitives.WriteInt32LittleEndian(buffer[0..4], chunk.x);
            BinaryPrimitives.WriteInt32LittleEndian(buffer[4..8], chunk.y);
            buffer[8] = block;

            ReadOnlySpan<byte> bytes = buffer;

            return MurmurHash3.Hash32(ref bytes, seed);
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

        public static Vector2Int GetWorldPosition(Vector2Int chunkPos, Vector2Int localTilePosition, int chunkSize)
        {
            return chunkPos * chunkSize + localTilePosition;
        }

        public static Vector2 GetTileCenterPosition(Vector2Int tilePosition)
        {
            return tilePosition + Vector2.one * 0.5f * TILE_SIZE;
        }

        public static int Flatten(Vector2Int position, int width)
        {
            return position.y * width + position.x;
        }
        
        public static Vector2Int Unflatten(int index, int width)
        {
            return new Vector2Int(index % width, index / width);
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
        public static void SetTile(this Tilemap tilemap, Vector3Int position, RandomGameObjectTile tile)
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