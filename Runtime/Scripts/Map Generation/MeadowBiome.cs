using System.Collections.Generic;
using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(fileName = "MeadowBiome", menuName = "World Generation/Meadow Biome")]
    public class MeadowBiome : Biome
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

        public override WfcTemplate GetTemplate(int chunkX, int chunkY)
        {
            float noise = Mathf.PerlinNoise(chunkX * noiseScale, chunkY * noiseScale);
            return GetTemplateFromNoise(noise);
        }

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

        // Scratch list for the oak-cell search in PostGenerate. Reused across chunks so the per-
        // chunk cluster check doesn't allocate. PostGenerate is called on the main thread one
        // chunk at a time, so a single shared instance is safe.
        private readonly List<Vector2Int> _oakCellsScratch = new();

        public override void PostGenerate(in BiomePostGenContext ctx)
        {
            // Per-chunk deterministic RNG so reloading a chunk produces the same cluster.
            System.Random rng = new System.Random(
                unchecked(ctx.ChunkPos.x * 73856093 ^ ctx.ChunkPos.y * 19349663));

            // 50% chance per chunk → ~0.5 clusters/chunk on average.
            if (rng.NextDouble() >= 0.5) return;

            _oakCellsScratch.Clear();
            for (int y = 0; y < ctx.ChunkSize; y++)
            {
                for (int x = 0; x < ctx.ChunkSize; x++)
                {
                    if (ctx.TryGetTileName(x, y, out string name) && name == "tree_oak")
                        _oakCellsScratch.Add(new Vector2Int(x, y));
                }
            }
            if (_oakCellsScratch.Count == 0) return;

            Vector2Int center = _oakCellsScratch[rng.Next(_oakCellsScratch.Count)];

            // Drop a mushroom on each free grass tile in a 3x3 around the oak (excluding the
            // oak itself, which obviously isn't grass).
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int x = center.x + dx;
                    int y = center.y + dy;
                    if (x < 0 || y < 0 || x >= ctx.ChunkSize || y >= ctx.ChunkSize) continue;

                    if (ctx.TryGetTileName(x, y, out string name) && name == "grass")
                        ctx.TryAddSpawn(new Vector2(x + 0.5f, y + 0.5f), "mushroom");
                }
            }
        }

#if UNITY_EDITOR
        private void OnValidate() => _thresholds = null; // Invalidate cache on inspector change.
#endif
    }
}
