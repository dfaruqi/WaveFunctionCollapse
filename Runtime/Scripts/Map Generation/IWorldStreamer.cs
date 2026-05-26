using System.Collections.Generic;
using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    public interface IWorldStreamer
    {
        /// <summary>
        /// Fired after a chunk has been drawn to the tilemap. The two spawn lists are exposed
        /// separately because they resolve their prefabs differently:
        ///   - <paramref name="tilePrefabSpawns"/> comes from GameObjectTiles in the chunk's
        ///     tile array; the prefab is already known, so no database lookup is needed.
        ///   - <paramref name="storedWorldObjects"/> are standalone world objects persisted
        ///     alongside the chunk; they reference prefabs by integer id and are resolved by the
        ///     subscriber's own WorldObjectDatabase.
        /// </summary>
        public delegate void ChunkDrawnHandler(
            Vector2Int chunkPos,
            IReadOnlyList<ChunkData.TilePrefabSpawn> tilePrefabSpawns,
            IReadOnlyList<ChunkData.WorldObjectSpawn> storedWorldObjects,
            Biome biome);

        public delegate void ChunkUndrawnHandler(Vector2Int chunkPos);

        public event ChunkDrawnHandler OnChunkDrawn;
        public event ChunkUndrawnHandler OnChunkUndrawn;
    }
}
