using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(fileName = "Biome", menuName = "World Generation/Biome")]

    public class Biome : ScriptableObject
    {
        [System.Serializable]
        public struct BiomeEntry
        {
            [Min(0f)] public float weight;
            public WfcTemplate template;
        }

        [SerializeField] private BiomeEntry[] templates;
        [SerializeField] private float noiseScale = 0.15f;

        // Cached cumulative thresholds, built once on first use.
        private float[] _thresholds;

        private float[] GetThresholds()
        {
            if (_thresholds != null && _thresholds.Length == templates.Length)
                return _thresholds;

            _thresholds = new float[templates.Length];
            float total = 0f;
            foreach (var e in templates) total += e.weight;

            float cumulative = 0f;
            for (int i = 0; i < templates.Length; i++)
            {
                cumulative += templates[i].weight;
                _thresholds[i] = total > 0f ? cumulative / total : (float)(i + 1) / templates.Length;
            }

            return _thresholds;
        }

        public WfcTemplate GetTemplate(int chunkX, int chunkY)
        {
            float noise = Mathf.PerlinNoise(chunkX * noiseScale, chunkY * noiseScale);
            return GetTemplateFromNoise(noise);
        }

        public WfcTemplate GetTemplate(Vector2Int pos) => GetTemplate(pos.x, pos.y);

        public WfcTemplate GetTemplateFromNoise(float noise)
        {
            float[] thresholds = GetThresholds();
            for (int i = 0; i < templates.Length - 1; i++)
            {
                if (noise < thresholds[i])
                    return templates[i].template;
            }
            return templates[^1].template;
        }

#if UNITY_EDITOR
        private void OnValidate() => _thresholds = null; // Invalidate cache on inspector change.
#endif
    }
}