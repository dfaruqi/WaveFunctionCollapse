using System;
using AYellowpaper.SerializedCollections;
using MagusStudios.Arcanist.Utils;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
        public int DefaultTileId = 0;
        
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
        
        private static readonly Direction[] AllDirectionOrders =
            new Direction[24 * 4] // Don't ask. It's for efficiency. 
            {
                // 0
                Direction.Up, Direction.Down, Direction.Left, Direction.Right,
                // 1
                Direction.Up, Direction.Down, Direction.Right, Direction.Left,
                // 2
                Direction.Up, Direction.Left, Direction.Down, Direction.Right,
                // 3
                Direction.Up, Direction.Left, Direction.Right, Direction.Down,
                // 4
                Direction.Up, Direction.Right, Direction.Down, Direction.Left,
                // 5
                Direction.Up, Direction.Right, Direction.Left, Direction.Down,

                // 6
                Direction.Down, Direction.Up, Direction.Left, Direction.Right,
                // 7
                Direction.Down, Direction.Up, Direction.Right, Direction.Left,
                // 8
                Direction.Down, Direction.Left, Direction.Up, Direction.Right,
                // 9
                Direction.Down, Direction.Left, Direction.Right, Direction.Up,
                // 10
                Direction.Down, Direction.Right, Direction.Up, Direction.Left,
                // 11
                Direction.Down, Direction.Right, Direction.Left, Direction.Up,

                // 12
                Direction.Left, Direction.Up, Direction.Down, Direction.Right,
                // 13
                Direction.Left, Direction.Up, Direction.Right, Direction.Down,
                // 14
                Direction.Left, Direction.Down, Direction.Up, Direction.Right,
                // 15
                Direction.Left, Direction.Down, Direction.Right, Direction.Up,
                // 16
                Direction.Left, Direction.Right, Direction.Up, Direction.Down,
                // 17
                Direction.Left, Direction.Right, Direction.Down, Direction.Up,

                // 18
                Direction.Right, Direction.Up, Direction.Down, Direction.Left,
                // 19
                Direction.Right, Direction.Up, Direction.Left, Direction.Down,
                // 20
                Direction.Right, Direction.Down, Direction.Up, Direction.Left,
                // 21
                Direction.Right, Direction.Down, Direction.Left, Direction.Up,
                // 22
                Direction.Right, Direction.Left, Direction.Up, Direction.Down,
                // 23
                Direction.Right, Direction.Left, Direction.Down, Direction.Up,
            };
        
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
                    map = GenerateMapInBlocks(ModuleSet, Blocks, BlockSize, DefaultTileId);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            // int[,] map = GenerateMap(borders);

            stopwatch.Stop();

            Debug.Log($"[{nameof(WaveFunctionCollapse)}] Finished generation in {stopwatch.Elapsed} seconds.");

            //load the map into the first tilemap found in the scene
            TileUtils.LoadMapData(tilemap, map, ModuleSet.TileDatabase);
        }

        // Contains border information
        public struct Borders
        {
            public List<int> BorderUp;
            public List<int> BorderDown;
            public List<int> BorderLeft;
            public List<int> BorderRight;
        }

        private class WfcState
        {
            public readonly NativeHeap<WfcJob.CellEntropy, WfcJob.EntropyComparer> EntropyHeap;
            public readonly NativeArray<NativeHeapIndex> EntropyIndices;
            public readonly NativeArray<WfcJob.Cell> Cells;
            public readonly NativeArray<int> PropagationStack;
            public readonly NativeArray<int> Output;
            public readonly NativeArray<int> UpBorder;
            public readonly NativeArray<int> DownBorder;
            public readonly NativeArray<int> LeftBorder;
            public readonly NativeArray<int> RightBorder;

            public WfcJob Job;
            
            public WfcState(Vector2Int size, int moduleCount, Borders borders = default)
            {
                int cellCount = size.x * size.y;

                // entropy 
                EntropyHeap =
                    new NativeHeap<WfcJob.CellEntropy, WfcJob.EntropyComparer>(Allocator.Persistent, cellCount);

                EntropyIndices =
                    new NativeArray<NativeHeapIndex>(cellCount, Allocator.Persistent);

                // the starting entropy will be applied to all cells at the start of generation

                Cells = new NativeArray<WfcJob.Cell>(cellCount, Allocator.Persistent);

                // fill domains with all tiles to start
                for (int i = 0; i < cellCount; i++)
                {
                    Cells[i] = WfcJob.Cell.CreateWithAllTiles(moduleCount);
                }

                // domains
                NativeArray<WfcJob.Cell> cells = new NativeArray<WfcJob.Cell>(cellCount, Allocator.Persistent);

                // fill domains with all tiles to start
                for (int i = 0;
                     i < cellCount;
                     i++)
                {
                    cells[i] = WfcJob.Cell.CreateWithAllTiles(moduleCount);
                }

                // === Initialize Stack for Propagation Step
                PropagationStack = new NativeArray<int>(cellCount, Allocator.Persistent);

                // === Initialize Output Structure ===
                Output = new NativeArray<int>(cellCount, Allocator.Persistent);

                // Fill the optional borders
                int bordersUpCount = borders.BorderUp?.Count ?? 0;
                int bordersDownCount = borders.BorderDown?.Count ?? 0;
                int bordersRightCount = borders.BorderRight?.Count ?? 0;
                int bordersLeftCount = borders.BorderLeft?.Count ?? 0;

                UpBorder = new NativeArray<int>(Mathf.Min(size.x, bordersUpCount), Allocator.Persistent);
                DownBorder = new NativeArray<int>(Mathf.Min(size.x, bordersDownCount), Allocator.Persistent);
                LeftBorder = new NativeArray<int>(Mathf.Min(size.y, bordersLeftCount), Allocator.Persistent);
                RightBorder = new NativeArray<int>(Mathf.Min(size.y, bordersRightCount), Allocator.Persistent);

                // ───── UP ─────
                List<int> bordersUp = borders.BorderUp;
                if (bordersUp != null)
                {
                    for (int i = 0; i < UpBorder.Length; i++)
                    {
                        UpBorder[i] = bordersUp[i];
                    }
                }

                // ───── DOWN ─────
                List<int> bordersDown = borders.BorderDown;
                if (bordersDown != null)
                {
                    for (int i = 0; i < DownBorder.Length; i++)
                    {
                        DownBorder[i] = bordersDown[i];
                    }
                }

                // ───── LEFT ─────
                List<int> bordersLeft = borders.BorderLeft;
                if (bordersLeft != null)
                {
                    for (int i = 0; i < LeftBorder.Length; i++)
                    {
                        LeftBorder[i] = bordersLeft[i];
                    }
                }

                // ───── RIGHT ─────
                List<int> bordersRight = borders.BorderRight;
                if (bordersRight != null)
                {
                    for (int i = 0; i < RightBorder.Length; i++)
                    {
                        RightBorder[i] = bordersRight[i];
                    }
                }
            }

            public void Dispose()
            {
                EntropyHeap.Dispose();
                EntropyIndices.Dispose();
                Cells.Dispose();
                PropagationStack.Dispose();
                Output.Dispose();
                UpBorder.Dispose();
                DownBorder.Dispose();
                LeftBorder.Dispose();
                RightBorder.Dispose();
            }
        }

        private class WfcGlobals
        {
            public NativeParallelHashMap<int, WfcJob.AllowedNeighborModule> Modules;
            public NativeParallelHashMap<int, float> Weights;
            public Dictionary<int, int> moduleKeyToIndex;
            public Dictionary<int, int> moduleIndexToKey;
            public NativeArray<Direction> directions;

            public WfcGlobals(WfcModuleSet moduleSet)
            {
                SerializedDictionary<int, WfcModuleSet.TileModule> moduleDict = moduleSet.Modules;

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
                // after generation returns the map

                // First, create the mapping
                moduleKeyToIndex = new Dictionary<int, int>();
                moduleIndexToKey = new Dictionary<int, int>();
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

                    Weights.Add(moduleCount, module.weight);
                    Modules.Add(moduleCount, nativeModule);
                    moduleCount++;
                }

                // A flattened area of all permutations of four directions, precomputed and for use in generation for when
                // the algorithm constrains neighbor cells, it does each direction in a random order
                directions = new NativeArray<Direction>(AllDirectionOrders, Allocator.Persistent);
            }

            public void Dispose()
            {
                Modules.Dispose();
                Weights.Dispose();
                directions.Dispose();
            }
        }

        /// <summary>
        /// [Fast] Generates a map of tile IDs according to the WFC algorithm in blocks. Use for large maps.
        /// </summary>
        /// <param name="moduleSet">Module set containing allowed neighbor information.</param>
        /// <param name="size">Size of the map to generate in blocks.</param>
        /// <param name="blockSize">Size of each block in tiles.</param>
        /// <param name="defaultTileKey">The key of the default tile that should initially fill the map. (grass, sand, dirt, etc.)</param>
        /// <returns></returns>
        public int[,] GenerateMapInBlocks(WfcModuleSet moduleSet, Vector2Int size, Vector2Int blockSize, int defaultTileKey)
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
            // 1. All blocks in odd columns, odd rows (1-indexed)
            // 2. All blocks in odd columns, even rows (1-indexed)
            // 3. All blocks in even columns, odd rows (1-indexed)
            // 4. All blocks in even columns, even rows (1-indexed)
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
            // size slightly bigger than the block size, typically +1 tile on every side (so 2 tiles larger overall
            // in each dimension). The tiles that overlap other blocks are regenerated so adjacent blocks
            // can get enough constraint information from neighboring blocks, thus preventing errors at borders
            // or corners. 

            int[,] output = new int[totalMapWidth, totalMapHeight];

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
                            GetBorders(output, blockX, blockY, size, blockSize, defaultTileKey));

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

                        wfcState.Job = wfc;

                        jobHandles.Add(wfc.Schedule());
                    }
                }

                // Complete all jobs
                NativeArray<JobHandle> jobHandlesNative =
                    new NativeArray<JobHandle>(AllDirectionOrders.Length, Allocator.Persistent);

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

                        if (wfcState.Job.Flag == WfcJob.State.ERROR)
                        {
                            Debug.Log($"[{nameof(WaveFunctionCollapse)}] Error in generation for block {blockX},{blockY}");
                        }

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

                            output[x + startX, y + startY] =
                                unconverted > -1 ? wfcState.Output[i] : defaultTileKey;
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
                    output[i, j] = wfcGlobals.moduleIndexToKey[output[i, j]];
                }
            }

            return output;
        }

        /// <summary>
        /// Helper function for GenerateMapInBlocks
        /// </summary>
        private Borders GetBorders(
            int[,] output,
            int x,
            int y,
            Vector2Int size,
            Vector2Int blockSize,
            int defaultTileId)
        {
            Borders borders = new Borders();

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
        public int[,] GenerateMap(WfcModuleSet moduleSet, Vector2Int mapSize, Borders borders = default)
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
                new NativeArray<Direction>(AllDirectionOrders, Allocator.Persistent);

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

    /// <summary>
    /// A burst-compilable, preallocated, fast implementation of Wave Function Collapse.
    /// </summary>
    [BurstCompile]
    struct WfcJob : IJob
    {
        // Lookup structures - immutable, for reference only, and accessible in parallel

        // module constraints
        [ReadOnly] [NativeDisableParallelForRestriction]
        public NativeParallelHashMap<int, AllowedNeighborModule> Modules;

        // weights
        [ReadOnly] [NativeDisableParallelForRestriction]
        public NativeParallelHashMap<int, float> Weights;

        [ReadOnly] [NativeDisableParallelForRestriction]
        public NativeArray<Direction> AllDirectionPermutations;

        [ReadOnly] public NativeArray<int> UpBorder;
        [ReadOnly] public NativeArray<int> DownBorder;
        [ReadOnly] public NativeArray<int> LeftBorder;
        [ReadOnly] public NativeArray<int> RightBorder;

        // Algorithm State

        // domains
        public NativeArray<Cell> Cells; // this represents the grid of cells being operated upon
        // each cell has a domain and a "selected" field (for efficiency) , which
        // is -1 until the cell has collapsed to one outcome, in which case it is 
        // set as the id of the selected tile for this cell

        // entropy
        public NativeHeap<CellEntropy, EntropyComparer> EntropyHeap;
        public NativeArray<NativeHeapIndex> EntropyIndices;

        // map size and cell count
        public int Width;
        public int Height;

        // rng
        public Unity.Mathematics.Random random;

        // stack for propagation step
        public NativeArray<int> PropagationStack;
        public int PropagationStackTop;

        // output
        public NativeArray<int> Output;

        // state of operation (error, ok)
        public State Flag;

        public enum State
        {
            OK,
            WARNING,
            ERROR
        }

        /// <summary>
        /// Represents one cell in the (flattened) grid that Wave Function Collapse operates upon. 
        /// Its domain of tiles is hard capped at 128. 
        /// </summary>
        public struct Cell
        {
            public ulong domainMask0;
            public ulong domainMask1;
            public int domainCount;
            public int selected;

            public static Cell CreateWithAllTiles(int size)
            {
                Cell cell = default;

                if (size <= 0)
                {
                    cell.domainMask0 = 0UL;
                    cell.domainMask1 = 0UL;
                    cell.domainCount = 0;
                    return cell;
                }

                if (size >= 128)
                {
                    // all 128 bits set
                    cell.domainMask0 = ulong.MaxValue;
                    cell.domainMask1 = ulong.MaxValue;
                    cell.domainCount = 128;
                    return cell;
                }

                cell.domainCount = size;

                if (size <= 64)
                {
                    // lower bits only
                    cell.domainMask0 = (size == 64) ? ulong.MaxValue : ((1UL << size) - 1UL);
                    cell.domainMask1 = 0UL;
                }
                else
                {
                    // fill lower 64 bits, then set upper (n-64) bits
                    cell.domainMask0 = ulong.MaxValue;
                    int highBits = size - 64; // 1..63
                    cell.domainMask1 = ((1UL << highBits) - 1UL);
                }

                cell.selected = -1;

                return cell;
            }

            public void Collapse(int tileId)
            {
                // Clear everything first
                domainMask0 = 0UL;
                domainMask1 = 0UL;

                if ((uint)tileId >= 128)
                {
                    // Out-of-range tile: collapsed to nothing
                    domainCount = 0;
                    selected = -1;
                    return;
                }

                domainCount = 1;

                if (tileId < 64)
                {
                    domainMask0 = 1UL << tileId;
                }
                else
                {
                    domainMask1 = 1UL << (tileId - 64);
                }

                selected = tileId;
            }
        }

        public struct CellEntropy
        {
            public int Id;
            public float Entropy;
        }

        public struct EntropyComparer : IComparer<CellEntropy>
        {
            public int Compare(CellEntropy x, CellEntropy y)
            {
                return x.Entropy.CompareTo(y.Entropy);
            }
        }

        /// <summary>
        /// Query allNeighbors with the indices and counts here to find all the neighbor data
        /// </summary>
        public struct AllowedNeighborModule
        {
            public ulong allowedUp0;
            public ulong allowedUp1;
            public ulong allowedDown0;
            public ulong allowedDown1;
            public ulong allowedLeft0;
            public ulong allowedLeft1;
            public ulong allowedRight0;
            public ulong allowedRight1;
        }

        public void Execute()
        {
            // Initialize all cells to the starting entropy
            for (int i = 0; i < Cells.Length; i++)
            {
                NativeHeapIndex nativeHeapIndex = EntropyHeap.Insert(new CellEntropy()
                {
                    Entropy = GetEntropy(i),
                    Id = i
                });
                EntropyIndices[i] = nativeHeapIndex;
            }

            // Enforce any constraints from optional borders of other maps

            // top border
            int index = (Height - 1) * Width;
            for (int i = 0; i < UpBorder.Length; i++)
            {
                int enforcerTile = UpBorder[i];
                if (enforcerTile < 0)
                {
                    index++;
                    continue;
                }

                ulong enforcerDomain0 = 0;
                ulong enforcerDomain1 = 0;
                if (enforcerTile < 64)
                {
                    enforcerDomain0 = (1UL << (enforcerTile));
                }
                else
                {
                    enforcerDomain1 = (1UL << (enforcerTile - 64));
                }

                if (ConstrainCell(index, enforcerDomain0, enforcerDomain1, Direction.Down))
                    PushToPropagationStack(index);

                index++;
            }

            // bottom border
            index = 0;
            for (int i = 0; i < DownBorder.Length; i++)
            {
                int enforcerTile = DownBorder[i];
                if (enforcerTile < 0)
                {
                    index++;
                    continue;
                }

                ulong enforcerDomain0 = 0;
                ulong enforcerDomain1 = 0;
                if (enforcerTile < 64)
                {
                    enforcerDomain0 = (1UL << (enforcerTile));
                }
                else
                {
                    enforcerDomain1 = (1UL << (enforcerTile - 64));
                }

                if (ConstrainCell(index, enforcerDomain0, enforcerDomain1, Direction.Up))
                    PushToPropagationStack(index);

                index++;
            }

            // left border
            index = 0;
            for (int i = 0; i < LeftBorder.Length; i++)
            {
                int enforcerTile = LeftBorder[i];
                if (enforcerTile < 0)
                {
                    index++;
                    continue;
                }

                ulong enforcerDomain0 = 0;
                ulong enforcerDomain1 = 0;
                if (enforcerTile < 64)
                {
                    enforcerDomain0 = (1UL << (enforcerTile));
                }
                else
                {
                    enforcerDomain1 = (1UL << (enforcerTile - 64));
                }

                if (ConstrainCell(index, enforcerDomain0, enforcerDomain1, Direction.Right))
                    PushToPropagationStack(index);

                index += Width;
            }

            // right border
            index = Width - 1;
            for (int i = 0; i < RightBorder.Length; i++)
            {
                int enforcerTile = RightBorder[i];
                if (enforcerTile < 0)
                {
                    index++;
                    continue;
                }

                ulong enforcerDomain0 = 0;
                ulong enforcerDomain1 = 0;
                if (enforcerTile < 64)
                {
                    enforcerDomain0 = (1UL << (enforcerTile));
                }
                else
                {
                    enforcerDomain1 = (1UL << (enforcerTile - 64));
                }

                if (ConstrainCell(index, enforcerDomain0, enforcerDomain1, Direction.Left))
                    PushToPropagationStack(index);

                index += Width;
            }

            // propagate any constraints from borders that were pushed to the propagation stack
            Propagate();

            // Execute passes of the algorithm until complete
            bool done = false;
            while (!done)
            {
                done = WaveFunctionCollapse();
            }

            // Prepare output
            for (int i = 0; i < Width * Height; i++)
            {
                Output[i] = Cells[i].selected;
            }
        }

        // One pass of the main algorithm, which collapses one lowest-entropy cell then recursively propagates constraints
        // to neighboring tiles until no more constraint propagation is possible.
        private bool WaveFunctionCollapse()
        {
            // Collapse a random lowest-entropy cell
            int selectedCell = GetRandomLowestEntropyCell();
            if (selectedCell == -1)
                return true; // Algorithm finished

            CollapseCell(selectedCell);

            // Push the initial collapsed cell
            PushToPropagationStack(selectedCell);

            Propagate();

            return false;
        }

        private void Propagate()
        {
            // Propagation loop
            while (PropagationStackTop > 0)
            {
                int enforcerCellIndex = PopFromPropagationStack();

                // If a cell has entropy 0 it has no possibilities → no need to propagate
                if (Cells[enforcerCellIndex].domainCount == 0)
                    continue;

                int x = enforcerCellIndex % Width;
                int y = enforcerCellIndex / Width;

                int perm = random.NextInt(24);
                int baseIdx = perm * 4;

                Cell enforcerCell = Cells[enforcerCellIndex];
                ulong enforcerDomain0 = enforcerCell.domainMask0;
                ulong enforcerDomain1 = enforcerCell.domainMask1;

                for (int i = 0; i < 4; i++)
                {
                    switch (AllDirectionPermutations[baseIdx + i])
                    {
                        case Direction.Up:
                            if (y + 1 < Height)
                            {
                                int neighborIndex = enforcerCellIndex + Width;
                                if (ConstrainCell(neighborIndex, enforcerDomain0, enforcerDomain1, Direction.Up))
                                    PushToPropagationStack(neighborIndex);
                            }

                            break;

                        case Direction.Down:
                            if (y - 1 >= 0)
                            {
                                int neighborIndex = enforcerCellIndex - Width;
                                if (ConstrainCell(neighborIndex, enforcerDomain0, enforcerDomain1, Direction.Down))
                                    PushToPropagationStack(neighborIndex);
                            }

                            break;

                        case Direction.Left:
                            if (x - 1 >= 0)
                            {
                                int neighborIndex = enforcerCellIndex - 1;
                                if (ConstrainCell(neighborIndex, enforcerDomain0, enforcerDomain1, Direction.Left))
                                    PushToPropagationStack(neighborIndex);
                            }

                            break;

                        case Direction.Right:
                            if (x + 1 < Width)
                            {
                                int neighborIndex = enforcerCellIndex + 1;
                                if (ConstrainCell(neighborIndex, enforcerDomain0, enforcerDomain1, Direction.Right))
                                    PushToPropagationStack(neighborIndex);
                            }

                            break;
                    }
                }
            }

            // Reset the stack for the next collapse cycle
            PropagationStackTop = 0;
        }


        void PushToPropagationStack(int v) => PropagationStack[PropagationStackTop++] = v;
        int PopFromPropagationStack() => PropagationStack[--PropagationStackTop];


        private int GetRandomLowestEntropyCell()
        {
            if (EntropyHeap.Count == 0) return -1; // Algorithm complete.

            return EntropyHeap.Peek().Id;
        }

        private void UpdateEntropy(int cellId)
        {
            float newEntropy = GetEntropy(cellId);

            // When we retrieve the lowest entropy cell, we want to ignore any already collapsed cells, so we remove 
            // them from the entropy heap
            if (newEntropy <= 0)
            {
                NativeHeapIndex index = EntropyIndices[cellId];
                if (!EntropyHeap.IsValidIndex(index)) return;
                EntropyHeap.Remove(index);
                return;
            }

            EntropyHeap.Remove(EntropyIndices[cellId]);
            NativeHeapIndex nativeHeapIndex =
                EntropyHeap.Insert(new CellEntropy() { Entropy = newEntropy, Id = cellId });
            EntropyIndices[cellId] = nativeHeapIndex;
        }

        // Calculate entropy based on a cell's domain 
        private float GetEntropy(int cellId)
        {
            var cell = Cells[cellId];

            // If domain is empty or has only one element, entropy is 0
            if (cell.domainCount <= 1)
            {
                return 0f;
            }

            // Calculate sum of weights for normalization
            float sumWeights = 0f;

            // Check domainMask0 (tiles 0-63)
            ulong mask0 = cell.domainMask0;
            for (int bitIndex = 0; bitIndex < 64; bitIndex++)
            {
                if ((mask0 & (1UL << bitIndex)) != 0)
                {
                    if (!Weights.TryGetValue(bitIndex, out float weight))
                    {
                        continue;
                    }

                    sumWeights += weight;
                }
            }

            // Check domainMask1 (tiles 64-127)
            ulong mask1 = cell.domainMask1;
            for (int bitIndex = 0; bitIndex < 64; bitIndex++)
            {
                if ((mask1 & (1UL << bitIndex)) != 0)
                {
                    if (Weights.TryGetValue(64 + bitIndex, out float weight))
                    {
                        sumWeights += weight;
                    }
                }
            }

            // Avoid division by zero
            if (sumWeights <= 0f)
            {
                return 0f;
            }

            // Calculate Shannon entropy: H = -Σ(p_i * log(p_i))
            float entropy = 0f;

            // Check domainMask0
            mask0 = cell.domainMask0;
            for (int bitIndex = 0; bitIndex < 64; bitIndex++)
            {
                if ((mask0 & (1UL << bitIndex)) != 0)
                {
                    if (Weights.TryGetValue(bitIndex, out float weight))
                    {
                        float probability = weight / sumWeights;
                        if (probability > 0f)
                        {
                            entropy -= probability * math.log2(probability);
                        }
                    }
                }
            }

            // Check domainMask1
            mask1 = cell.domainMask1;
            for (int bitIndex = 0; bitIndex < 64; bitIndex++)
            {
                if ((mask1 & (1UL << bitIndex)) != 0)
                {
                    if (Weights.TryGetValue(64 + bitIndex, out float weight))
                    {
                        float probability = weight / sumWeights;
                        if (probability > 0f)
                        {
                            entropy -= probability * math.log2(probability);
                        }
                    }
                }
            }

            return entropy;
        }

        private void CollapseCell(int cellId)
        {
            var cell = Cells[cellId];

            // If already collapsed, exit (this represents a standard constraint error in generation)
            if (cell.domainCount <= 1)
            {
                if (Flag == State.OK)
                    Flag = State.ERROR;
                return;
            }

            // Calculate total weight by iterating through set bits
            float totalWeight = 0;

            int tileCount = Weights.Count();

            // Check domainMask0 (tiles 0-63)
            ulong mask0 = cell.domainMask0;
            for (int bitIndex = 0; bitIndex < tileCount && bitIndex < 64; bitIndex++)
            {
                if ((mask0 & (1UL << bitIndex)) != 0)
                {
                    totalWeight += Weights[bitIndex];
                }
            }

            // Check domainMask1 (tiles 64-127)
            ulong mask1 = cell.domainMask1;
            for (int bitIndex = 0; bitIndex + 64 < tileCount && bitIndex < 64; bitIndex++)
            {
                if ((mask1 & (1UL << bitIndex)) != 0)
                {
                    totalWeight += Weights[64 + bitIndex];
                }
            }

            // Make weighted random choice
            float choice = random.NextFloat() * totalWeight;
            float cumulative = 0;
            int selected = -1;

            // Check domainMask0
            mask0 = cell.domainMask0;
            for (int bitIndex = 0; bitIndex < tileCount && bitIndex < 64; bitIndex++)
            {
                if ((mask0 & (1UL << bitIndex)) != 0)
                {
                    cumulative += Weights[bitIndex];
                    if (choice < cumulative)
                    {
                        selected = bitIndex;
                        break;
                    }
                }
            }

            // If not found in mask0, check domainMask1
            if (selected == -1)
            {
                mask1 = cell.domainMask1;
                for (int bitIndex = 0; bitIndex + 64 < tileCount && bitIndex < 64; bitIndex++)
                {
                    if ((mask1 & (1UL << bitIndex)) != 0)
                    {
                        cumulative += Weights[64 + bitIndex];
                        if (choice < cumulative)
                        {
                            selected = 64 + bitIndex;
                            break;
                        }
                    }
                }
            }

            // Collapse domain to single selected value
            cell.Collapse(selected);
            Cells[cellId] = cell;

            // Update entropy
            UpdateEntropy(cellId);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="cellToConstrain">Cell that will be constrained</param>
        /// <param name="enforcerDomain0">Mask representing the domain of tile ids (0-63) enforcing neighbor constraints.</param>
        /// <param name="enforcerDomain1">Mask representing the domain of tile ids (64-127) enforcing neighbor constraints.</param>
        /// <param name="direction">Direction from enforcer cell to the cell to constrain</param>
        /// <returns></returns>
        private bool ConstrainCell(int cellToConstrain, ulong enforcerDomain0, ulong enforcerDomain1,
            Direction direction)
        {
            Cell cell = Cells[cellToConstrain];

            // In the domain of the enforcer cell, which tiles are allowed in [direction] for each remaining possibility?
            // We will OR-in all the possibilities declared in the module set. 
            ulong allowedMask0 = 0;
            ulong allowedMask1 = 0;

            // iterate through each possibility in the domain of the enforcer cell
            while (enforcerDomain0 != 0)
            {
                ulong lowestBit = enforcerDomain0 & (~enforcerDomain0 + 1); // isolate lowest set bit
                int index = math.tzcnt(lowestBit);

                switch (direction)
                {
                    case Direction.Up:
                        allowedMask0 |= Modules[index].allowedUp0;
                        allowedMask1 |= Modules[index].allowedUp1;
                        break;
                    case Direction.Down:
                        allowedMask0 |= Modules[index].allowedDown0;
                        allowedMask1 |= Modules[index].allowedDown1;
                        break;
                    case Direction.Left:
                        allowedMask0 |= Modules[index].allowedLeft0;
                        allowedMask1 |= Modules[index].allowedLeft1;
                        break;
                    case Direction.Right:
                        allowedMask0 |= Modules[index].allowedRight0;
                        allowedMask1 |= Modules[index].allowedRight1;
                        break;
                }

                enforcerDomain0 &= enforcerDomain0 - 1; // clear lowest set bit
            }

            while (enforcerDomain1 != 0)
            {
                ulong lowestBit = enforcerDomain1 & (~enforcerDomain1 + 1); // isolate lowest set bit
                int index = math.tzcnt(lowestBit) + 64; // offset into modules 64–127

                switch (direction)
                {
                    case Direction.Up:
                        allowedMask0 |= Modules[index].allowedUp0;
                        allowedMask1 |= Modules[index].allowedUp1;
                        break;
                    case Direction.Down:
                        allowedMask0 |= Modules[index].allowedDown0;
                        allowedMask1 |= Modules[index].allowedDown1;
                        break;
                    case Direction.Left:
                        allowedMask0 |= Modules[index].allowedLeft0;
                        allowedMask1 |= Modules[index].allowedLeft1;
                        break;
                    case Direction.Right:
                        allowedMask0 |= Modules[index].allowedRight0;
                        allowedMask1 |= Modules[index].allowedRight1;
                        break;
                }

                enforcerDomain1 &= enforcerDomain1 - 1; // clear lowest set bit
            }

            ulong constrainedMask0 = cell.domainMask0;
            ulong constrainedMask1 = cell.domainMask1;

            constrainedMask0 &= allowedMask0;
            constrainedMask1 &= allowedMask1;

            // No change → no propagation needed
            if (constrainedMask0 == cell.domainMask0 && constrainedMask1 == cell.domainMask1)
                return false;

            // Update cell
            cell.domainMask0 = constrainedMask0;
            cell.domainMask1 = constrainedMask1;

            // Recompute domain count
            cell.domainCount =
                math.countbits(constrainedMask0) +
                math.countbits(constrainedMask1);

            if (cell.domainCount == 0)
            {
                Cells[cellToConstrain] = cell;
                UpdateEntropy(cellToConstrain);
                
                Flag = State.ERROR; // A standard error where a cell's domain is constrained to nothing. 
                                    // Errors are expected with large module sets.
                
                return false; //don't propagate error cells
            }

            // Collapse cell if its domain is 1 element
            if (cell.domainCount == 1)
            {
                if (cell.domainMask0 != 0)
                {
                    // Bit is in [0..63]
                    cell.selected = math.tzcnt(cell.domainMask0);
                }
                else
                {
                    // Bit is in [64..127]
                    cell.selected = 64 + math.tzcnt(cell.domainMask1);
                }
            }

            Cells[cellToConstrain] = cell;

            UpdateEntropy(cellToConstrain);

            return true; // we constrained this cell to a smaller domain, so return true
        }
    }

    
}