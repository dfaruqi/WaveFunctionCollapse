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
    }
}
