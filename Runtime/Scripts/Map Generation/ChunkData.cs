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
        /// A spawn derived from a GameObjectTile placed in a chunk's tile array. The prefab is
        /// taken straight from the tile, so consumers don't need a database lookup.
        /// </summary>
        public struct TilePrefabSpawn
        {
            public Vector2 localPosition; // local to the chunk, like ChunkData.WorldObjectData
            public GameObject prefab;
        }
    }
}