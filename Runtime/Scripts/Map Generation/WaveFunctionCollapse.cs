using System;
using AYellowpaper.SerializedCollections;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MagusStudios.WaveFunctionCollapse.Utils;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Tilemaps;
using Random = Unity.Mathematics.Random;

namespace MagusStudios.WaveFunctionCollapse
{
    /// <summary>
    /// Performs a simplified Wave Function Collapse using adjacency constraints defined in a WfcModuleSet.
    /// </summary>
    public class WaveFunctionCollapse : MonoBehaviour
    {
        //modules
        public WfcModuleSet ModuleSet;

        //dimensions (simple)
        public Vector2Int MapSize = new(16, 16);

        //dimensions (in blocks)
        public Vector2Int Blocks = new(4, 4);
        public Vector2Int BlockSize = new(32, 32);

        //randomization
        public uint Seed;

        [SerializeField] MapGenerationMode GenerationMode;

        private Tilemap tilemap;

        private const int MAXIMUM_TILES = 128;

        public enum MapGenerationMode
        {
            Simple,
            Chunked
        }

        private void Start()
        {
            // cache first tilemap in scene
            GetFirstTilemapInScene();
        }

        private void GetFirstTilemapInScene()
        {
            //cache tilemap
            Tilemap[] tilemaps = FindObjectsByType<Tilemap>(FindObjectsSortMode.None);
            if (tilemaps.Length == 0)
            {
                Debug.LogError($"[{nameof(WaveFunctionCollapse)}]No tilemaps found in the scene");
                return;
            }

            tilemap = tilemaps[0];
        }

        // called from button in editor script (see WaveFunctionCollapseEditor)
        public void GenerateFromEditor()
        {
            if (!Application.IsPlaying(this)) return; // don't generate when not playing
            // (it would overwrite the tilemap in your scene in edit mode, which is
            // not desirable if using it to create module sets)

            GetFirstTilemapInScene(); // update the tilemap

            GenerateMapAndApplyToTilemap(tilemap);
        }

        private void GenerateMapAndApplyToTilemap(Tilemap tilemap)
        {
            //start a stopwatch for efficiency analysis
            System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();

            int[,] map;
            switch (GenerationMode)
            {
                case MapGenerationMode.Simple:
                    map = GenerateMap(ModuleSet, MapSize);
                    break;
                case MapGenerationMode.Chunked:
                    map = GenerateMapInBlocks(ModuleSet, Blocks, BlockSize);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            // int[,] map = GenerateMap(borders);

            stopwatch.Stop();

            Debug.Log($"[{nameof(WaveFunctionCollapse)}] Finished generation in {stopwatch.Elapsed} seconds.");

            stopwatch.Reset();
            stopwatch.Start();

            //load the map into the first tilemap found in the scene
            TileUtils.LoadMapData(tilemap, map, ModuleSet.TileDatabase);

            stopwatch.Stop();

            Debug.Log($"[{nameof(WaveFunctionCollapse)}] Finished setting tiles on tilemap in {stopwatch.Elapsed} seconds.");
        }

        /// <summary>
        /// [Fast] Generates a map of tile IDs according to the WFC algorithm in blocks. Use for large maps.
        /// </summary>
        /// <param name="moduleSet">Module set containing allowed neighbor information.</param>
        /// <param name="size">Size of the map to generate in blocks.</param>
        /// <param name="blockSize">Size of each block in tiles.</param>
        /// <param name="defaultTileKey">The key of the default tile that should initially fill the map. (grass, sand, dirt, etc.)</param>
        /// <returns></returns>
        public int[,] GenerateMapInBlocks(WfcModuleSet moduleSet, Vector2Int size, Vector2Int blockSize)
        {
            // create algorithm lookup data and state
            WfcGlobals wfcGlobals = new WfcGlobals(moduleSet);
            WfcState[,] stateGrid = new WfcState[size.x, size.y];

            //create rng
            Random blockSeedGenerator = new Unity.Mathematics.Random(Seed);

            // sizing
            int blockWidth = blockSize.x;
            int blockHeight = blockSize.y;

            int totalMapWidth = blockWidth * size.x;
            int totalMapHeight = blockHeight * size.y;

            // Generate the map in blocks:
            // To take advantage of multiple worker threads (for large maps) while avoiding violating neighbor
            // constraints along borders of neighboring blocks generating at the same time, the map is
            // generated in 4 passes:
            // 1. All blocks in even columns, even rows (0-indexed)
            // 2. All blocks in even columns, odd rows (0-indexed)
            // 3. All blocks in odd columns, even rows (0-indexed)
            // 4. All blocks in odd columns, odd rows (0-indexed)
            //
            // Example (4x4, showing which chunks are generated in passes 1,2,3,4)
            // 2 4 2 4
            // 1 3 1 3
            // 2 4 2 4
            // 1 3 1 3
            //
            // In this example, 4 blocks can be generated at a time. The number of blocks that can be generated in
            // parallel scales with the size of the map so that generation of large maps can take advantage
            // of many worker threads.
            //
            // When generating blocks, we employ the modifying-in-blocks approach. The output is initiated to
            // a trivial solution. This means filling the map completely with one default tile, like grass, for
            // example. Then, when a block is generated, a wave function collapse job is scheduled that has a map
            // size slightly bigger than the block size, typically +1 tile on every side (so blocks are 2 tiles larger
            // overall in each dimension). The tiles that overlap other blocks are regenerated so adjacent blocks
            // can get enough constraint information from neighboring blocks, thus preventing errors at borders
            // or corners. 

            // This running output is in index-space rather than key-space. See WfcGlobals
            int[,] output = new int[totalMapWidth, totalMapHeight];

            for (int j = 0; j < output.GetLength(1); j++)
            {
                for (int i = 0; i < output.GetLength(0); i++)
                {
                    output[i, j] = wfcGlobals.moduleKeyToIndex[moduleSet.DefaultTileKey];
                }
            }

            for (int pass = 0; pass < 4; pass++)
            {
                // Binary-derived offsets
                int passStartBlockX = pass >> 1;
                int passStartBlockY = pass & 1;

                List<JobHandle> jobHandles = new List<JobHandle>();
                for (int blockY = passStartBlockY; blockY < size.y; blockY += 2)
                {
                    for (int blockX = passStartBlockX; blockX < size.x; blockX += 2)
                    {
                        WfcState wfcState = new WfcState(new Vector2Int(blockSize.x + 2, blockSize.y + 2),
                            ModuleSet.Modules.Count,
                            GetBorders(output, blockX, blockY, size, blockSize,
                                wfcGlobals.moduleKeyToIndex[moduleSet.DefaultTileKey]));

                        // WfcState wfcState = new WfcState(new Vector2Int(blockSize.x + 2, blockSize.y + 2),
                        //     moduleSet.Modules.Count);
                        stateGrid[blockX, blockY] = wfcState;

                        Random rng = new Random(blockSeedGenerator.NextUInt());

                        // === Create and schedule the job ===
                        WfcJob wfc = new WfcJob
                        {
                            Modules = wfcGlobals.Modules,
                            Weights = wfcGlobals.Weights,
                            Cells = wfcState.Cells,
                            AllDirectionPermutations = wfcGlobals.directions,
                            UpBorder = wfcState.UpBorder,
                            DownBorder = wfcState.DownBorder,
                            LeftBorder = wfcState.LeftBorder,
                            RightBorder = wfcState.RightBorder,
                            EntropyHeap = wfcState.EntropyHeap,
                            EntropyIndices = wfcState.EntropyIndices,
                            random = rng,
                            PropagationStack = wfcState.PropagationStack,
                            PropagationStackTop = 0,
                            Width = blockSize.x + 2,
                            Height = blockSize.y + 2,
                            Output = wfcState.Output,
                            Flag = WfcJob.State.OK
                        };

                        jobHandles.Add(wfc.Schedule());
                    }
                }

                // Complete all jobs
                NativeArray<JobHandle> jobHandlesNative =
                    new NativeArray<JobHandle>(WfcUtils.AllDirectionOrders.Length, Allocator.Persistent);

                jobHandlesNative = new NativeArray<JobHandle>(jobHandles.Count, Allocator.Persistent);
                for (int i = 0; i < jobHandles.Count; i++)
                {
                    jobHandlesNative[i] = jobHandles[i];
                }

                JobHandle.CompleteAll(jobHandlesNative);

                jobHandlesNative.Dispose();
                jobHandles.Clear();

                int trueBlockWidth = blockWidth + 2;
                int trueBlockHeight = blockHeight + 2;
                for (int blockY = passStartBlockY; blockY < size.y; blockY += 2)
                {
                    for (int blockX = passStartBlockX; blockX < size.x; blockX += 2)
                    {
                        int startX = blockX * blockSize.x - 1;
                        int startY = blockY * blockSize.y - 1;

                        WfcState wfcState = stateGrid[blockX, blockY];

                        for (int i = 0; i < (trueBlockWidth) * (trueBlockHeight); i++)
                        {
                            int y = i / trueBlockWidth;
                            int x = i % trueBlockWidth;

                            int worldX = x + startX;
                            int worldY = y + startY;
                            if (worldX < 0 || worldY < 0 || worldX >= totalMapWidth || worldY >= totalMapHeight)
                            {
                                continue;
                            }

                            int unconverted = wfcState.Output[i];

                            output[x + startX, y + startY] = unconverted;
                        }

                        wfcState.Dispose();
                    }
                }
            }

            wfcGlobals.Dispose();

            // convert output back to keys

            for (int j = 0; j < output.GetLength(1); j++)
            {
                for (int i = 0; i < output.GetLength(0); i++)
                {
                    int tileId = output[i, j];
                    output[i, j] = tileId < 0 ? -1 : wfcGlobals.moduleIndexToKey[output[i, j]];
                }
            }

            return output;
        }

        /// <summary>
        /// Helper function for GenerateMapInBlocks
        /// </summary>
        private WfcUtils.Borders GetBorders(
            int[,] output,
            int x,
            int y,
            Vector2Int size,
            Vector2Int blockSize,
            int defaultTileId)
        {
            WfcUtils.Borders borders = new WfcUtils.Borders();

            int blockStartX = x * blockSize.x;
            int blockStartY = y * blockSize.y;

            int blockEndX = blockStartX + blockSize.x - 1;
            int blockEndY = blockStartY + blockSize.y - 1;

            // ========= LEFT =========
            if (x > 0)
            {
                int borderX = blockStartX - 2;
                borders.BorderLeft = new List<int>();

                for (int yTile = blockStartY - 1; yTile <= blockEndY + 1; yTile++)
                {
                    if (IsInBounds(borderX, yTile, output))
                        borders.BorderLeft.Add(output[borderX, yTile]);
                    else
                        borders.BorderLeft.Add(defaultTileId);
                }
            }
            else
            {
                borders.BorderLeft = Enumerable.Repeat(defaultTileId, blockSize.y + 2).ToList();
            }

            // ========= RIGHT =========
            if (x < size.x - 1)
            {
                int borderX = blockEndX + 2;
                borders.BorderRight = new List<int>();

                for (int yTile = blockStartY - 1; yTile <= blockEndY + 1; yTile++)
                {
                    if (IsInBounds(borderX, yTile, output))
                        borders.BorderRight.Add(output[borderX, yTile]);
                    else
                        borders.BorderRight.Add(defaultTileId);
                }
            }
            else
            {
                borders.BorderRight = Enumerable.Repeat(defaultTileId, blockSize.y + 2).ToList();
            }

            // ========= DOWN =========
            if (y > 0)
            {
                int borderY = blockStartY - 2;
                borders.BorderDown = new List<int>();

                for (int xTile = blockStartX - 1; xTile <= blockEndX + 1; xTile++)
                {
                    if (IsInBounds(xTile, borderY, output))
                        borders.BorderDown.Add(output[xTile, borderY]);
                    else
                        borders.BorderDown.Add(defaultTileId);
                }
            }
            else
            {
                borders.BorderDown = Enumerable.Repeat(defaultTileId, blockSize.x + 2).ToList();
            }

            // ========= UP =========
            if (y < size.y - 1)
            {
                int borderY = blockEndY + 2;
                borders.BorderUp = new List<int>();

                for (int xTile = blockStartX - 1; xTile <= blockEndX + 1; xTile++)
                {
                    if (IsInBounds(xTile, borderY, output))
                        borders.BorderUp.Add(output[xTile, borderY]);
                    else
                        borders.BorderUp.Add(defaultTileId);
                }
            }
            else
            {
                borders.BorderUp = Enumerable.Repeat(defaultTileId, blockSize.x + 2).ToList();
            }

            return borders;
        }


        private bool IsInBounds(int x, int y, int[,] grid)
        {
            return x >= 0 &&
                   y >= 0 &&
                   x < grid.GetLength(0) &&
                   y < grid.GetLength(1);
        }

        /// <summary>
        /// [Fast] Generates a map of tile IDs according to the WFC algorithm.
        /// </summary>
        /// <param name="moduleSet">Module set containing allowed neighbor information.</param>
        /// <param name="mapSize">Size of the map to generate in tiles.</param>
        /// <param name="borders">Optional borders to enforce adjacency along the edges of this map, useful for creating
        ///     larger maps in chunks. </param>
        /// <returns></returns>
        /// <exception cref="System.Exception">Throws an exception if the tile set has more than the 128-tile maximum or no tiles. </exception>
        public int[,] GenerateMap(WfcModuleSet moduleSet, Vector2Int mapSize, WfcUtils.Borders borders = default)
        {
            SerializedDictionary<int, WfcModuleSet.TileModule> moduleDict = moduleSet.Modules;

            // First, check that the module set does not have too many tiles
            if (moduleDict.Count >= MAXIMUM_TILES)
            {
                Debug.LogError(
                    $"[{nameof(WaveFunctionCollapse)}] Module set has too many tiles. WFC only supports up to {MAXIMUM_TILES} tiles.");
                throw new System.Exception(
                    $"[{nameof(WaveFunctionCollapse)}] Module set has too many tiles. WFC only supports up to {MAXIMUM_TILES} tiles.");
            }

            // check for 0 tiles
            if (moduleDict.Count == 0)
            {
                throw new System.Exception($"[{nameof(WaveFunctionCollapse)}] Module set has no tiles.");
            }

            int width = mapSize.x;
            int height = mapSize.y;
            int cellCount = width * height;

            // === Initialization of Readonly Lookup Structures ===

            // modules (stored as an array of module structs, which contain masks defining allowed tiles in each direction)
            NativeParallelHashMap<int, WfcJob.AllowedNeighborModule> modules =
                new NativeParallelHashMap<int, WfcJob.AllowedNeighborModule>(moduleDict.Count,
                    Allocator.Persistent);

            // weights
            NativeParallelHashMap<int, float> weights =
                new NativeParallelHashMap<int, float>(moduleDict.Count, Allocator.Persistent);

            // - initialize modules and weights -

            // The module set is a dictionary for more flexibility in the editor. Native data does not support dictionaries, however, so we create an
            // ordered array of all modules instead. The indices of this array will be used as the tile ids in the algorithm. We create a mapping
            // of tile id (in the module set) -> index in the ordered array so that the output can be converted back to the module set's editor ids
            // after generation returns the map

            // First, create the mapping
            Dictionary<int, int> moduleKeyToIndex = new Dictionary<int, int>();
            Dictionary<int, int> moduleIndexToKey = new Dictionary<int, int>();
            int mappingCount = 0;
            foreach (KeyValuePair<int, WfcModuleSet.TileModule> kvp in moduleDict)
            {
                moduleKeyToIndex[kvp.Key] = mappingCount;
                moduleIndexToKey[mappingCount] = kvp.Key;
                mappingCount++;
            }

            // Fill modules and weights
            int moduleCount = 0;
            foreach (KeyValuePair<int, WfcModuleSet.TileModule> kvp in moduleDict)
            {
                WfcModuleSet.TileModule module = kvp.Value;
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

                weights.Add(moduleCount, module.weight);
                modules.Add(moduleCount, nativeModule);
                moduleCount++;
            }

            // A flattened area of all permutations of four directions, precomputed and for use in generation for when
            // the algorithm constrains neighbor cells, it does each direction in a random order
            NativeArray<Direction> directions =
                new NativeArray<Direction>(WfcUtils.AllDirectionOrders, Allocator.Persistent);

            // === Initialization of Algorithm State ===

            // entropy 
            NativeHeap<WfcJob.CellEntropy, WfcJob.EntropyComparer> entropyHeap =
                new NativeHeap<WfcJob.CellEntropy, WfcJob.EntropyComparer>(Allocator.Persistent, cellCount);
            NativeArray<NativeHeapIndex> entropyIndices =
                new NativeArray<NativeHeapIndex>(cellCount, Allocator.Persistent);

            // the starting entropy will be applied to all cells at the start of generation

            // domains
            NativeArray<WfcJob.Cell> cells = new NativeArray<WfcJob.Cell>(cellCount, Allocator.Persistent);

            // fill domains with all tiles to start
            for (int i = 0; i < cellCount; i++)
            {
                cells[i] = WfcJob.Cell.CreateWithAllTiles(moduleDict.Count);
            }

            // Fill the optional borders
            int bordersUpCount = borders.BorderUp?.Count ?? 0;
            int bordersDownCount = borders.BorderDown?.Count ?? 0;
            int bordersRightCount = borders.BorderRight?.Count ?? 0;
            int bordersLeftCount = borders.BorderLeft?.Count ?? 0;

            NativeArray<int> upBorder =
                new NativeArray<int>(Mathf.Min(width, bordersUpCount), Allocator.Persistent);
            NativeArray<int> downBorder =
                new NativeArray<int>(Mathf.Min(width, bordersDownCount), Allocator.Persistent);
            NativeArray<int> leftBorder =
                new NativeArray<int>(Mathf.Min(height, bordersLeftCount), Allocator.Persistent);
            NativeArray<int> rightBorder =
                new NativeArray<int>(Mathf.Min(height, bordersRightCount), Allocator.Persistent);

            // ───── UP ─────
            List<int> bordersUp = borders.BorderUp;
            for (int i = 0; i < upBorder.Length; i++)
            {
                upBorder[i] = moduleKeyToIndex[bordersUp[i]];
            }

            // ───── DOWN ─────
            List<int> bordersDown = borders.BorderDown;
            for (int i = 0; i < downBorder.Length; i++)
            {
                downBorder[i] = moduleKeyToIndex[bordersDown[i]];
            }

            // ───── LEFT ─────
            List<int> bordersLeft = borders.BorderLeft;
            for (int i = 0; i < leftBorder.Length; i++)
            {
                leftBorder[i] = moduleKeyToIndex[bordersLeft[i]];
            }

            // ───── RIGHT ─────
            List<int> bordersRight = borders.BorderRight;
            for (int i = 0; i < rightBorder.Length; i++)
            {
                rightBorder[i] = moduleKeyToIndex[bordersRight[i]];
            }

            // === Create random generator ===
            Random rng = new Unity.Mathematics.Random(Seed);

            // === Initialize Stack for Propagation Step
            NativeArray<int> propagationStack = new NativeArray<int>(cellCount, Allocator.Persistent);

            // === Initialize Output Structure ===
            NativeArray<int> output = new NativeArray<int>(cellCount, Allocator.Persistent);

            // === Create and schedule the job ===
            WfcJob wfc = new WfcJob
            {
                Modules = modules,
                Weights = weights,
                Cells = cells,
                AllDirectionPermutations = directions,
                UpBorder = upBorder,
                DownBorder = downBorder,
                LeftBorder = leftBorder,
                RightBorder = rightBorder,
                EntropyHeap = entropyHeap,
                EntropyIndices = entropyIndices,
                random = rng,
                PropagationStack = propagationStack,
                PropagationStackTop = 0,
                Width = width,
                Height = height,
                Output = output,
                Flag = WfcJob.State.OK
            };

            wfc.Schedule().Complete();

            // === Convert and cleanup ===


            // Now that generation is complete, we use the mapping to convert the finished map back into the tile ids
            // used in the module set.

            int[,] finalOutput = new int[width, height];

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    int unconverted = output[rowOffset + x];
                    finalOutput[x, y] = unconverted >= 0 ? moduleIndexToKey[unconverted] : -1;
                }
            }

            modules.Dispose();
            weights.Dispose();
            cells.Dispose();
            entropyHeap.Dispose();
            propagationStack.Dispose();
            directions.Dispose();
            output.Dispose();
            upBorder.Dispose();
            downBorder.Dispose();
            leftBorder.Dispose();
            rightBorder.Dispose();
            entropyIndices.Dispose();

            return finalOutput;
        }
    }
}