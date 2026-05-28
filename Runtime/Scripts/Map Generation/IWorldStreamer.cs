using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    public interface IWorldStreamer
    {
        /// <summary>
        /// Fired after a chunk has been drawn to the tilemap. <paramref name="worldObjectDatabase"/>
        /// is the database the active biome resolves its stored world-object spawns against —
        /// passed through so subscribers don't need their own reference.
        /// </summary>
        public delegate void ChunkDrawnHandler(Vector2Int chunkPos, WorldObjectDatabase worldObjectDatabase);

        public delegate void ChunkUndrawnHandler(Vector2Int chunkPos);

        public event ChunkDrawnHandler OnChunkDrawn;
        public event ChunkUndrawnHandler OnChunkUndrawn;

        /// <summary>
        /// Attempts to fetch a read-only view of a currently-loaded chunk. Returns false if the
        /// chunk is not loaded. The returned snapshot wraps the streamer's internal buffers and
        /// is only valid synchronously — once control returns to the streamer it may be unloaded
        /// or overwritten by ongoing generation.
        /// </summary>
        bool TryGetChunk(Vector2Int chunkPos, out ChunkSnapshot snapshot);
    }
}
