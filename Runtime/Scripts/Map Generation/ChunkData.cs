using System.Collections.Generic;
using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    public class ChunkData
    {
        public int[] Tiles; // CHUNK_SIZE * CHUNK_SIZE
        public List<WorldObjectSpawn> WorldObjects; 

        [System.Serializable]
        public struct WorldObjectSpawn
        {
            public Vector2 localPosition; // pos in the chunk
            public int prefabId;
        }
        
        /// <summary>
        /// A spawn to instantiate inside a chunk, with its prefab already resolved. The streamer
        /// builds these at draw time from both GameObjectTile cells and stored <see cref="WorldObjectSpawn"/>
        /// entries (whose prefabIds it resolves against its WorldObjectDatabase), so subscribers
        /// don't need a database of their own.
        /// </summary>
        public struct ChunkSpawn
        {
            public Vector2 localPosition; // local to the chunk, like ChunkData.WorldObjectData
            public GameObject prefab;
        }
    }
}