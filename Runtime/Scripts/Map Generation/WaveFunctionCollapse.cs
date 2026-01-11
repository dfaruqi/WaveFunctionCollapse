using AYellowpaper.SerializedCollections;
using MagusStudios.Arcanist.Utils;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Tilemaps;
using Random = UnityEngine.Random;

namespace MagusStudios.WaveFunctionCollapse
{
    /// <summary>
    /// Performs a simplified Wave Function Collapse using adjacency constraints defined in a WfcModuleSet.
    /// </summary>
    public class WaveFunctionCollapse : MonoBehaviour
    {
        //modules
        [SerializeField] WfcModuleSet moduleSet;

        //dimensions
        [SerializeField] Vector2Int mapSize = new(10, 10);

        //animation
        [SerializeField] int animatedPassesPerSecond = 16;
        [SerializeField] bool animate;
        [SerializeField] bool showDomains;

        //randomization
        [SerializeField] uint seed;

        public delegate void CellConstrainedHandler(Vector2Int pos, int domainSize);

        private Tilemap tilemap;
        private System.Random random;
        private TilemapNumberOverlay _debugOverlay;

        private const int MAXIMUM_TILES = 128;

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
            //cache first tilemap in scene
            GetFirstTilemapInScene();

            //cache domain overlay
            _debugOverlay = GetComponent<TilemapNumberOverlay>();
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

        //called from button in editor
        public void Generate()
        {
            StopAllCoroutines();

            if (animate) GenerateMapAnimated(tilemap);
            else GenerateMapAndApplyToTilemap(tilemap);
        }

        private void GenerateMapAndApplyToTilemap(Tilemap tilemap)
        {
            //start a stopwatch for efficiency analysis
            System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();

            int[,] map = GenerateMap();

            stopwatch.Stop();

            Debug.Log($"[{nameof(WaveFunctionCollapse)}] Finished generation in {stopwatch.Elapsed} seconds.");

            //load the map into the first tilemap found in the scene
            TileUtils.LoadMapData(tilemap, map, moduleSet.TileDatabase);
        }

        private void GenerateMapAnimated(Tilemap tilemap)
        {
            StartCoroutine(GenerateMapAnimatedCoroutine(tilemap));
        }

        public IEnumerator GenerateMapAnimatedCoroutine(Tilemap tilemap)
        {
            Debug.Log(
                $"[{nameof(WaveFunctionCollapse)}] Starting WFC map generation with module set {moduleSet.name} and seed {seed}");

            //prepare data needed for algorithm
            var modules = moduleSet.Modules;
            int[] allTileIDs = modules.Keys.ToArray();
            var weights = modules.ToDictionary(m => m.Key, m => m.Value.weight);
            int width = mapSize.x;
            int height = mapSize.y;

            // initialize random
            random = new System.Random((int)seed);

            //clear the tilemap
            tilemap?.ClearAllTiles();

            //prepare the debug overlay or hide it
            if (ShouldShowDebugOverlay())
            {
                _debugOverlay.enabled = true;
                _debugOverlay.CreateNumberOverlay(tilemap, mapSize, modules.Count);
            }
            else
            {
                _debugOverlay.enabled = false;
            }

            // the map that will be used during generation (not the output)
            var world = new Grid(mapSize.x, mapSize.y);

            // Initialize all cells in the grid and link them to their neighbors for algorithmic reasons

            //First pass: create all cells without neighbors
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    world.map[x, y] = new Cell(allTileIDs, x, y);
                }
            }

            //Second pass: assign neighbors
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    Cell neighborUp = (y < height - 1) ? world.map[x, y + 1] : null;
                    Cell neighborDown = (y > 0) ? world.map[x, y - 1] : null;
                    Cell neighborLeft = (x > 0) ? world.map[x - 1, y] : null;
                    Cell neighborRight = (x < width - 1) ? world.map[x + 1, y] : null;

                    // Update the cell with its neighbors
                    world.map[x, y].SetNeighbors(neighborUp, neighborDown, neighborLeft, neighborRight);
                }
            }

            //if showDomains is true, pass a callback into the wave function collapse algorithm
            //to run every time a cell is constrained so that we can update the ui
            CellConstrainedHandler updateDebugOverlay = ShouldShowDebugOverlay() ? UpdateTileDomain : null;

            //create a list to store the cells that were collapsed for each pass of the algorithm (for animating the tilemap during generation)
            List<Cell> cellsCollapsed = new List<Cell>();

            //main algorithm loop
            bool done = false;
            while (!done)
            {
                cellsCollapsed.Clear();
                done = world.WaveFunctionCollapse(weights, modules, ref cellsCollapsed, random, updateDebugOverlay);
                foreach (Cell cell in cellsCollapsed)
                {
                    if (cell.Domain.Count == 0) continue;
                    //update the tiles after each pass of the algorithm so you can watch the map generate
                    Vector3Int pos = cell.pos.ToVector3Int();
                    int tileId = cell.Domain[0];
                    TileBase tile = moduleSet.TileDatabase.Tiles[tileId];
                    tilemap.SetTileDynamic(pos, tile);
                }

                yield return new WaitForSeconds(1f / animatedPassesPerSecond);
            }

            // 4. Build result
            var map = new int[mapSize.x, mapSize.y];
            Debug.Log($"[{nameof(WaveFunctionCollapse)}] Building map...");

            for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
            {
                map[x, y] = world.map[x, y].GetCollapsedTile();
            }

            //once finished, load the whole map
            //TileUtils.LoadMapData(tilemap, map, moduleSet.TileDatabase);

            yield break;
        }

        private struct Borders
        {
            public List<int> BorderUp;
            public List<int> BorderDown;
            public List<int> BorderLeft;
            public List<int> BorderRight;
        }

        private void GenerateMapInChunks(int widthInChunks, int heightInChunks, int chunkWidth, int chunkHeight)
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

            // === Initialization of Readonly Lookup Structures (accessible in parallel) ===

            // modules (stored as an array of module structs, which contain masks defining allowed tiles in each direction)
            NativeParallelHashMap<int, WfcJob.AllowedNeighborModule> modules =
                new NativeParallelHashMap<int, WfcJob.AllowedNeighborModule>(moduleDict.Count, Allocator.Persistent);

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
            NativeArray<Direction> directions = new NativeArray<Direction>(AllDirectionOrders, Allocator.Persistent);

            for (int y = 0; y < heightInChunks; y++)
            {
                for (int x = 0; x < widthInChunks; x++)
                {
                }
            }
        }

        /// <summary>
        /// [Fast] Generates a map of tile IDs according to the WFC algorithm. Optimized with jobs and the burst compiler.
        /// </summary>
        /// <param name="borders">Optional borders to enforce adjacency along the edges of this map, useful for creating
        /// larger maps in chunks. </param>
        /// <returns></returns>
        /// <exception cref="System.Exception">Throws an exception if the tile set has more than the 128-tile maximum. </exception>
        private int[,] GenerateMap(Borders borders = default)
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
                new NativeParallelHashMap<int, WfcJob.AllowedNeighborModule>(moduleDict.Count, Allocator.Persistent);

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
            NativeArray<Direction> directions = new NativeArray<Direction>(AllDirectionOrders, Allocator.Persistent);

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

            NativeArray<int> upBorder = new NativeArray<int>(Mathf.Min(width, bordersUpCount), Allocator.Persistent);
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
            var rng = new Unity.Mathematics.Random(seed);

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
                CellCount = cellCount,
                Width = width,
                Height = height,
                Output = output,
                Flag = WfcJob.State.OK
            };

            //wfc.Schedule().Complete();
            wfc.Execute();

            // === Convert and cleanup ===

            // Now that generation is complete, we use the mapping to convert the finished map back into the tile ids
            // used in the module set.
            int[,] unconvertedMap = wfc.Output.ToSquare2DArray();
            int[,] finalOutput = new int[width, height];

            for (int i = 0; i < width; i++)
            {
                for (int j = 0; j < height; j++)
                {
                    if (unconvertedMap[i, j] != -1)
                        finalOutput[i, j] = moduleIndexToKey[unconvertedMap[i, j]];
                    else
                    {
                        finalOutput[i, j] = -1;
                    }
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

        private void UpdateTileDomain(Vector2Int pos, int domainSize)
        {
            _debugOverlay.SetNumber(pos.ToVector3Int(), domainSize);
        }


        private bool ShouldShowDebugOverlay()
        {
            return showDomains && _debugOverlay != null;
        }
    }

    /// <summary>
    /// A burst-compilable, preallocated, fast implementation of Wave Function Collapse.
    /// </summary>
    struct WfcJob
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
        public int CellCount;
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
                int enforcerTile = LeftBorder[i];
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

            Propagate();

            // Execute passes of the algorithm until complete
            bool done = false;
            while (!done)
            {
                done = WaveFunctionCollapse();
            }

            // Prepare output
            for (int i = 0; i < CellCount; i++)
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

            // If already collapsed, exit (this should not happen...)
            if (cell.domainCount <= 1)
            {
                if (Flag == State.OK)
                    Flag = State.WARNING;
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
        private bool ConstrainCell(int cellToConstrain, ulong enforcerDomain0, ulong enforcerDomain1, Direction direction)
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

    /// <summary>
    /// Used by the naive implementation of Wave Function Collapse. Has callbacks to animate the tiles changing. 
    /// </summary>
    public class Cell
    {
        public Dictionary<Direction, Cell> Neighbors;
        private List<int> domain;
        public Vector2Int pos;

        public bool Collapsed => domain.Count <= 1;
        public IReadOnlyList<int> Domain => domain;

        public Cell(int[] domain, int x, int y)
        {
            this.domain = new List<int>();
            foreach (int i in domain)
            {
                this.domain.Add(i);
            }

            Neighbors = new Dictionary<Direction, Cell>();
            pos.x = x;
            pos.y = y;
        }

        public void SetNeighbors(Cell up, Cell down, Cell left, Cell right)
        {
            // Clear existing neighbors and add the new ones
            Neighbors.Clear();
            if (up != null) Neighbors.Add(Direction.Up, up);
            if (down != null) Neighbors.Add(Direction.Down, down);
            if (left != null) Neighbors.Add(Direction.Left, left);
            if (right != null) Neighbors.Add(Direction.Right, right);
        }

        public float GetEntropy(Dictionary<int, float> weights)
        {
            if (domain.Count <= 1) return 0;

            float totalWeight = 0f;
            float entropy = 0f;

            // Calculate total weight of all possible states in the domain
            foreach (int state in domain)
            {
                if (weights.TryGetValue(state, out float weight))
                {
                    totalWeight += weight;
                }
                else
                {
                    Debug.LogError($"Error: no weight found for tile id: {state}");
                }
            }

            // If total weight is 0, return maximum entropy
            if (totalWeight <= 0f)
                return float.MaxValue;

            // Calculate entropy using Shannon entropy formula: -Σ(p_i * log2(p_i))
            foreach (int state in domain)
            {
                if (weights.TryGetValue(state, out float weight))
                {
                    float probability = weight / totalWeight;
                    if (probability > 0f)
                    {
                        entropy -= probability * (float)Mathf.Log(probability, 2);
                    }
                }
                else
                {
                    Debug.LogError($"Error: no weight found for tile id: {state}");
                }
            }

            return entropy;
        }

        /// <summary>
        /// Collapse this cell to one tile ID, chosen randomly with weight.
        /// </summary>
        public void Collapse(Dictionary<int, float> weights, System.Random random)
        {
            if (Collapsed) return;

            float totalWeight = domain.Sum(id => weights[id]);
            float choice = (float)(random.NextDouble() * totalWeight);

            float cumulative = 0;
            int selected = domain[0];

            foreach (var id in domain)
            {
                cumulative += weights[id];
                if (choice < cumulative)
                {
                    selected = id;
                    break;
                }
            }

            domain.Clear();
            domain.Add(selected);
        }

        /// <summary>
        /// Constrains this cell’s domain based on the neighbor’s possible tiles.
        /// </summary>
        public bool Constrain(IReadOnlyList<int> enforcerDomain,
            Direction direction,
            SerializedDictionary<int, WfcModuleSet.TileModule> modules,
            Dictionary<int, float> weights)
        {
            bool constrained = false;

            if (Collapsed) return false;

            HashSet<int> valid = new HashSet<int>();
            foreach (int id in enforcerDomain)
            {
                foreach (int key in modules[id].compatibleNeighbors[direction])
                {
                    valid.Add(key);
                }
            }

            List<int> domainCopy = new List<int>(domain);
            foreach (int id in domainCopy)
            {
                if (!valid.Contains(id))
                {
                    domain.Remove(id);

                    // This case represents a cell with no solution where the domain of legal tiles has been reduced to 0.
                    // We return false to not propagate from this tile, as doing so would fill the grid with more error cells
                    if (domain.Count == 0)
                    {
                        return false;
                    }

                    // This cell was constrained to a valid domain, so we return true to propagate this constraint to other tiles
                    constrained = true;
                }
            }

            return constrained;
        }


        public int GetCollapsedTile() => domain.Count > 0 ? domain[0] : -1;
    }

    /// <summary>
    /// Naive implementation of Wave Function Collapse for demonstration purposes. Has callbacks to animate the
    /// tiles changing.
    /// </summary>
    public class Grid
    {
        public Cell[,] map;

        public Grid(int x, int y)
        {
            map = new Cell[x, y];
        }

        //cached for efficiency
        List<Cell> lowestEntropyCells = new List<Cell>();

        /// <summary>
        /// 
        /// </summary>
        /// <param name="weights"></param>
        /// <param name="modules"></param>
        /// <returns>Is the algorithm done, What cells were collapsed</returns>
        public bool WaveFunctionCollapse(Dictionary<int, float> weights,
            SerializedDictionary<int, WfcModuleSet.TileModule> modules,
            ref List<Cell> cellsCollapsed,
            System.Random random,
            WaveFunctionCollapse.CellConstrainedHandler cellConstrainedHandler = null)
        {
            //empty the passed-in list, which will be filled with the cells collapsed in this pass of the algorithm, useful for animating the pass later
            cellsCollapsed.Clear();

            //collapse a random cell of the lowest entropy cells in the grid
            GetCellsLowestEntropy(weights, ref lowestEntropyCells);
            if (lowestEntropyCells.Count == 0) return true;

            //Cell cellToCollapse = lowestEntropyCells[(int)(random.NextFloat() * lowestEntropyCells.Count)];
            Cell cellToCollapse = lowestEntropyCells[random.Next(0, lowestEntropyCells.Count)];
            cellToCollapse.Collapse(weights, random);
            cellConstrainedHandler?.Invoke(cellToCollapse.pos, cellToCollapse.Domain.Count);

            cellsCollapsed.Add(cellToCollapse);

            //Propagation
            Stack<Cell> stack = new Stack<Cell>();
            stack.Push(cellToCollapse);

            while (stack.Count > 0)
            {
                Cell cell = stack.Pop();
                IReadOnlyList<int> domain = cell.Domain;

                foreach (Direction direction in DirectionExtension.EnumerateAll().Shuffle(random))
                {
                    if (!cell.Neighbors.TryGetValue(direction, out Cell neighbor))
                    {
                        continue; //normal for edge tiles not to have neighbors and ignore them
                    }

                    if (neighbor.GetEntropy(weights) != 0)
                    {
                        bool propagate = neighbor.Constrain(domain, direction, modules, weights);
                        if (propagate)
                        {
                            cellConstrainedHandler?.Invoke(neighbor.pos, neighbor.Domain.Count);
                            stack.Push(neighbor);
                            if (neighbor.Collapsed) cellsCollapsed.Add(neighbor);
                        }
                        else if (neighbor.Domain.Count == 0)
                        {
                            cellConstrainedHandler?.Invoke(neighbor.pos, neighbor.Domain.Count);
                        }
                    }
                }
            }

            return false;
        }

        public void GetCellsLowestEntropy(Dictionary<int, float> weights, ref List<Cell> lowestEntropyCells)
        {
            lowestEntropyCells.Clear();

            int width = map.GetLength(0);
            int height = map.GetLength(1);

            float lowestEntropy = float.MaxValue;

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    float entropy = map[x, y].GetEntropy(weights);

                    if (entropy <= 0)
                        continue;

                    if (entropy < lowestEntropy)
                    {
                        lowestEntropy = entropy;
                        lowestEntropyCells.Clear();
                        lowestEntropyCells.Add(map[x, y]);
                    }
                    else if (entropy == lowestEntropy)
                    {
                        lowestEntropyCells.Add(map[x, y]);
                    }
                }
            }
        }
    }
}