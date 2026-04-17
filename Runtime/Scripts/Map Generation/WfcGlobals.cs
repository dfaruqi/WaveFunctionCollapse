using System.Collections.Generic;
using AYellowpaper.SerializedCollections;
using MagusStudios.WaveFunctionCollapse.Utils;
using Unity.Collections;

namespace MagusStudios.WaveFunctionCollapse
{
    /// <summary>
    /// Contains state needed for Wave Function Collapse. WfcGlobals are read-only and can be shared by
    /// multiple parallel runs.
    /// </summary>
    public class WfcGlobals
    {
        public NativeParallelHashMap<int, WfcJob.AllowedNeighborModule> Modules;
        public NativeParallelHashMap<int, float> Weights;
        public Dictionary<int, int> moduleKeyToIndex;
        public Dictionary<int, int> moduleIndexToKey;
        public NativeArray<Direction> directions;

        public WfcGlobals(WfcTemplate template)
        {
            SerializedDictionary<int, WfcTemplate.TileModule> moduleDict = template.TileRules.Modules;

            // === Initialization of Readonly Lookup Structures (immutable, accessible in parallel by multiple worker threads) ===

            // modules (stored as an array of module structs, which contain masks defining allowed tiles in each direction)
            Modules = new NativeParallelHashMap<int, WfcJob.AllowedNeighborModule>(moduleDict.Count,
                Allocator.Persistent);

            // weights
            Weights = new NativeParallelHashMap<int, float>(moduleDict.Count, Allocator.Persistent);

            // - initialize modules and weights -

            // The module set is a dictionary for more flexibility in the editor. Native data does not support dictionaries, however, so we create an
            // ordered array of all modules instead. The indices of this array will be used as the tile ids in the algorithm. We create a mapping
            // of tile id (in the module set) -> index in the ordered array so that the output can be converted back to the module set's editor ids

            // First, create the mapping
            moduleKeyToIndex = new Dictionary<int, int>();
            moduleIndexToKey = new Dictionary<int, int>();
            int mappingCount = 0;
            foreach (KeyValuePair<int, WfcTemplate.TileModule> kvp in moduleDict)
            {
                moduleKeyToIndex[kvp.Key] = mappingCount;
                moduleIndexToKey[mappingCount] = kvp.Key;
                mappingCount++;
            }

            // Fill modules and weights
            int moduleCount = 0;
            foreach (KeyValuePair<int, WfcTemplate.TileModule> kvp in moduleDict)
            {
                WfcTemplate.TileModule module = kvp.Value;
                WfcJob.AllowedNeighborModule nativeModule = new WfcJob.AllowedNeighborModule();

                // initialize the module's allowed neighbors to nothing at first
                nativeModule.allowedUp0 = 0;
                nativeModule.allowedUp1 = 0;
                nativeModule.allowedDown0 = 0;
                nativeModule.allowedDown1 = 0;
                nativeModule.allowedLeft0 = 0;
                nativeModule.allowedLeft1 = 0;
                nativeModule.allowedRight0 = 0;
                nativeModule.allowedRight1 = 0;

                // UP
                foreach (int v in module.compatibleNeighbors[Direction.Up])
                {
                    int compatibleNeighborIndex = moduleKeyToIndex[v];

                    if (compatibleNeighborIndex < 64)
                    {
                        nativeModule.allowedUp0 |= 1UL << compatibleNeighborIndex;
                    }
                    else
                    {
                        nativeModule.allowedUp1 |= 1UL << (compatibleNeighborIndex - 64);
                    }
                }

                // DOWN
                foreach (int v in module.compatibleNeighbors[Direction.Down])
                {
                    int compatibleNeighborIndex = moduleKeyToIndex[v];

                    if (compatibleNeighborIndex < 64)
                    {
                        nativeModule.allowedDown0 |= 1UL << compatibleNeighborIndex;
                    }
                    else
                    {
                        nativeModule.allowedDown1 |= 1UL << (compatibleNeighborIndex - 64);
                    }
                }

                // LEFT
                foreach (int v in module.compatibleNeighbors[Direction.Left])
                {
                    int compatibleNeighborIndex = moduleKeyToIndex[v];

                    if (compatibleNeighborIndex < 64)
                    {
                        nativeModule.allowedLeft0 |= 1UL << compatibleNeighborIndex;
                    }
                    else
                    {
                        nativeModule.allowedLeft1 |= 1UL << (compatibleNeighborIndex - 64);
                    }
                }

                // RIGHT
                foreach (int v in module.compatibleNeighbors[Direction.Right])
                {
                    int compatibleNeighborIndex = moduleKeyToIndex[v];

                    if (compatibleNeighborIndex < 64)
                    {
                        nativeModule.allowedRight0 |= 1UL << compatibleNeighborIndex;
                    }
                    else
                    {
                        nativeModule.allowedRight1 |= 1UL << (compatibleNeighborIndex - 64);
                    }
                }

                template.Weights.TryGetWeight(kvp.Key, out float weight);
                
                Weights.Add(moduleCount, weight);
                Modules.Add(moduleCount, nativeModule);
                moduleCount++;
            }

            // A flattened area of all permutations of four directions, precomputed and for use in generation for when
            // the algorithm constrains neighbor cells, it does each direction in a random order
            directions = new NativeArray<Direction>(WfcUtils.AllDirectionOrders, Allocator.Persistent);
        }

        public void Dispose()
        {
            Modules.Dispose();
            Weights.Dispose();
            directions.Dispose();
        }
    }
}