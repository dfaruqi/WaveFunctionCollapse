using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(fileName = "Biome", menuName = "Wave Function Collapse/Biome")]

    public class Biome : ScriptableObject
    {
        [System.Serializable]
        public struct BiomeEntry
        {
            [Range(0f, 1f)] public float threshold;
            public WfcTemplate template;
        }

        [SerializeField] private BiomeEntry[] entries;
        [SerializeField] private float noiseScale = 0.15f;
        
        public WfcTemplate GetTemplate(int chunkX, int chunkY)
        {
            float noise = Mathf.PerlinNoise(chunkX * noiseScale, chunkY * noiseScale);
            return GetTemplateFromNoise(noise);
        }
        
        public WfcTemplate GetTemplate(Vector2Int pos)
        {
            return GetTemplate(pos.x, pos.y);
        }
        
        public WfcTemplate GetTemplateFromNoise(float noise)
        {
            for (int i = 0; i < entries.Length - 1; i++)
            {
                if (noise < entries[i].threshold)
                    return entries[i].template;
            }
            return entries[^1].template;
        }
    }
}