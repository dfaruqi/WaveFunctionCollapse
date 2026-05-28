using System.Collections.Generic;
using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    /// <summary>
    /// Read-only view of a single loaded chunk. Bundles the chunk's tile array and stored
    /// world-object spawns together with the WfcTemplate that produced them, so consumers
    /// (e.g. <see cref="WorldObjectSpawner"/>) can interpret tile keys via the template's
    /// <see cref="TileDatabase"/> without holding a separate reference.
    ///
    /// The snapshot wraps the streamer's internal buffers by reference — it is cheap to
    /// construct, but only valid synchronously. Once control returns to the streamer (next
    /// frame), the underlying chunk may be unloaded or rewritten by ongoing generation.
    /// </summary>
    public readonly struct ChunkSnapshot
    {
        public Vector2Int ChunkPos { get; }
        public int ChunkSize { get; }
        public WfcTemplate Template { get; }

        private readonly int[] _tiles;
        private readonly List<ChunkData.WorldObjectSpawn> _worldObjects;

        public ChunkSnapshot(
            Vector2Int chunkPos, int chunkSize, WfcTemplate template,
            int[] tiles, List<ChunkData.WorldObjectSpawn> worldObjects)
        {
            ChunkPos = chunkPos;
            ChunkSize = chunkSize;
            Template = template;
            _tiles = tiles;
            _worldObjects = worldObjects;
        }

        /// <summary>Tile key at the given chunk-local cell.</summary>
        public int GetTileKey(int localX, int localY) => _tiles[localY * ChunkSize + localX];

        /// <summary>Stored world-object spawns for the chunk (prefab-id form).</summary>
        public IReadOnlyList<ChunkData.WorldObjectSpawn> WorldObjects => _worldObjects;
    }
}
