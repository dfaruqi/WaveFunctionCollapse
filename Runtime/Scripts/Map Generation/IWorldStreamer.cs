using System.Collections.Generic;
using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    public interface IWorldStreamer
    {
        /// <summary>
        /// Fired after a chunk has been drawn to the tilemap. <paramref name="spawns"/> is a
        /// unified, prefab-resolved list combining both GameObjectTile-derived spawns and stored
        /// world objects (whose prefab ids the streamer has already resolved against its
        /// WorldObjectDatabase). Subscribers can just instantiate each entry — no database lookup
        /// required on their side.
        /// </summary>
        public delegate void ChunkDrawnHandler(
            Vector2Int chunkPos,
            IReadOnlyList<ChunkData.ChunkSpawn> spawns,
            Biome biome);

        public delegate void ChunkUndrawnHandler(Vector2Int chunkPos);

        public event ChunkDrawnHandler OnChunkDrawn;
        public event ChunkUndrawnHandler OnChunkUndrawn;
    }
}
