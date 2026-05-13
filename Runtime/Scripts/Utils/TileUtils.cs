using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int HashPosition(Vector2Int pos, int n) => HashPosition(pos.x, pos.y, n);

        /// <summary>
        /// Deterministically hashes (x, y) into a uniform integer in [0, n).
        /// Specialized inline MurmurHash3 over exactly two uints — no buffer, no loop, no modulo.
        /// Output differs from the previous Span+modulo path; the mapping is still uniform and
        /// deterministic, but a given (x,y,n) will pick a different sprite variant than before.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int HashPosition(int x, int y, int n)
        {
            const uint c1 = 0xcc9e2d51;
            const uint c2 = 0x1b873593;

            uint h = 0; // seed

            uint k = (uint)x * c1;
            k = (k << 15) | (k >> 17);
            k *= c2;
            h ^= k;
            h = (h << 13) | (h >> 19);
            h = h * 5 + 0xe6546b64;

            k = (uint)y * c1;
            k = (k << 15) | (k >> 17);
            k *= c2;
            h ^= k;
            h = (h << 13) | (h >> 19);
            h = h * 5 + 0xe6546b64;

            // Length finalizer (8 bytes consumed, no tail).
            h ^= 8;
            h ^= h >> 16;
            h *= 0x85ebca6b;
            h ^= h >> 13;
            h *= 0xc2b2ae35;
            h ^= h >> 16;

            // Lemire's multiplicative reduction: avoids `% n`.
            return (int)(((ulong)h * (ulong)(uint)n) >> 32);
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

        /// <summary>
        /// Specialized inline MurmurHash3 over a fixed 9-byte input (chunk.x, chunk.y, block).
        /// Bit-for-bit identical to the previous Span+MurmurHash3 path — same seeds for the same
        /// inputs, so previously generated chunks regenerate identically.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint HashWorldBlock(uint seed, Vector2Int chunk, byte block)
        {
            const uint c1 = 0xcc9e2d51;
            const uint c2 = 0x1b873593;

            uint h = seed;

            // Word 0: chunk.x
            uint k = (uint)chunk.x * c1;
            k = (k << 15) | (k >> 17);
            k *= c2;
            h ^= k;
            h = (h << 13) | (h >> 19);
            h = h * 5 + 0xe6546b64;

            // Word 1: chunk.y
            k = (uint)chunk.y * c1;
            k = (k << 15) | (k >> 17);
            k *= c2;
            h ^= k;
            h = (h << 13) | (h >> 19);
            h = h * 5 + 0xe6546b64;

            // Tail: 1 byte (`block`).
            k = (uint)block * c1;
            k = (k << 15) | (k >> 17);
            k *= c2;
            h ^= k;

            // Length finalizer (9 bytes).
            h ^= 9;
            h ^= h >> 16;
            h *= 0x85ebca6b;
            h ^= h >> 13;
            h *= 0xc2b2ae35;
            h ^= h >> 16;

            return h;
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