using AYellowpaper.SerializedCollections;
using Codice.CM.Client.Differences.Graphic;
using MagusStudios.Arcanist.Tilemaps;
using MagusStudios.Arcanist.Utils;
using MagusStudios.Collections;
using MagusStudios.WaveFunctionCollapse;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Tilemaps;
using static MagusStudios.Arcanist.WaveFunctionCollapse.WfcModuleSet;

namespace MagusStudios.Arcanist.WaveFunctionCollapse
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
            //start a stopwatch for efficiency analysis later
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
            Debug.Log($"[{nameof(WaveFunctionCollapse)}] Starting WFC map generation with module set {moduleSet.name} and seed {seed}");

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


        /// <summary>
        /// Generates a map of tile IDs according to the WFC algorithm. Optimized and burst-compilable.
        /// </summary>
        /// <returns></returns>
        /// <exception cref="System.Exception">Throws an exception if the tile set has more than the 128-tile maximum. </exception>
        private int[,] GenerateMap()
        {
            SerializedDictionary<int, TileModule> moduleDict = moduleSet.Modules;

            // First, check that the module set does not have too many tiles
            if (moduleDict.Count >= MAXIMUM_TILES)
            {
                Debug.LogError($"[{nameof(WaveFunctionCollapse)}] Module set has too many tiles. WFC only supports up to {MAXIMUM_TILES} tiles.");
                throw new System.Exception($"[{nameof(WaveFunctionCollapse)}] Module set has too many tiles. WFC only supports up to {MAXIMUM_TILES} tiles.");
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

            // modules (stored as an array of index information alongside a flattened array of all neighbor data)
            NativeParallelHashMap<int, WfcJob.TileConstraints> modules = new NativeParallelHashMap<int, WfcJob.TileConstraints>(moduleDict.Count, Allocator.Persistent);
            NativeList<int> allNeighbors = new NativeList<int>(Allocator.Persistent);

            // weights
            NativeParallelHashMap<int, float> weights = new NativeParallelHashMap<int, float>(moduleDict.Count, Allocator.Persistent);

            // - initialize modules and weights -

            // The module set is a dictionary for more flexibility in the editor. Native data does not support dictionaries however, so we create an
            // ordered array of all modules instead. The indices of this array will be used as the tile ids in the algorithm. We create a mapping
            // of tile id (in the module set) -> index in the ordered array so that the output can be converted back to the ids
            // used in the module set after generation. 

            // First, create the mapping
            Dictionary<int, int> moduleKeyToIndex = new Dictionary<int, int>();
            Dictionary<int, int> moduleIndexToKey = new Dictionary<int, int>();
            int mappingCount = 0;
            foreach (KeyValuePair<int, TileModule> kvp in moduleDict)
            {
                moduleKeyToIndex[kvp.Key] = mappingCount;
                moduleIndexToKey[mappingCount] = kvp.Key;
                mappingCount++;
            }

            // Fill modules, allNeighbors, and weights
            int moduleCount = 0;
            int neighborCount = 0;
            foreach (KeyValuePair<int, TileModule> kvp in moduleDict)
            {
                TileModule module = kvp.Value;
                WfcJob.TileConstraints nativeModule = new WfcJob.TileConstraints();

                // up
                int neighborCountUp = module.compatibleNeighbors[Direction.Up].Count;

                nativeModule.upCount = neighborCountUp;
                nativeModule.upStart = neighborCount;
                neighborCount += neighborCountUp;

                foreach (int neighbor in module.compatibleNeighbors[Direction.Up])
                {
                    allNeighbors.Add(moduleKeyToIndex[neighbor]);
                }

                // down
                int neighborCountDown = module.compatibleNeighbors[Direction.Down].Count;

                nativeModule.downCount = neighborCountDown;
                nativeModule.downStart = neighborCount;
                neighborCount += neighborCountDown;

                foreach (int neighbor in module.compatibleNeighbors[Direction.Down])
                {
                    allNeighbors.Add(moduleKeyToIndex[neighbor]);
                }

                // left
                int neighborCountLeft = module.compatibleNeighbors[Direction.Left].Count;

                nativeModule.leftCount = neighborCountLeft;
                nativeModule.leftStart = neighborCount;
                neighborCount += neighborCountLeft;

                foreach (int neighbor in module.compatibleNeighbors[Direction.Left])
                {
                    allNeighbors.Add(moduleKeyToIndex[neighbor]);
                }

                // right
                int neighborCountRight = module.compatibleNeighbors[Direction.Right].Count;

                nativeModule.rightCount = neighborCountRight;
                nativeModule.rightStart = neighborCount;
                neighborCount += neighborCountRight;

                foreach (int neighbor in module.compatibleNeighbors[Direction.Right])
                {
                    allNeighbors.Add(moduleKeyToIndex[neighbor]);
                }

                modules.Add(moduleCount, nativeModule);
                weights.Add(moduleCount, module.weight);
                moduleCount++;
            }

            // === Initialization of Algorithm State ===

            // entropy 
            NativeArray<float> cellEntropy = new NativeArray<float>(cellCount, Allocator.Persistent);

            // the starting entropy will be applied to all cells at the start of generation

            // domains
            NativeArray<WfcJob.Cell> cells = new NativeArray<WfcJob.Cell>(cellCount, Allocator.Persistent);

            // fill domains with all tiles to start
            for (int i = 0; i < cellCount; i++)
            {
                cells[i] = WfcJob.Cell.CreateWithAllTiles(cellCount);
            }

            // === Create random generator ===
            var rng = new Unity.Mathematics.Random(seed);

            // === Initialize Stack for Propagation Step
            NativeList<int> propagationStack = new NativeList<int>(cellCount, Allocator.Persistent);

            // === Initialize Output Structure ===
            NativeArray<int> output = new NativeArray<int>(cellCount, Allocator.Persistent);

            // === Create and schedule the job ===
            WfcJob wfc = new WfcJob
            {
                Modules = modules,
                AllNeighbors = allNeighbors.AsArray(),
                Weights = weights,
                Cells = cells,
                CellEntropy = cellEntropy,
                random = rng,
                PropagationStack = propagationStack,
                PropagationStackTop = 0,
                CellCount = cellCount,
                Width = width,
                Height = height,
                Output = output,
                Flag = WfcJob.State.OK
            };

            wfc.Execute();

            // === Convert and cleanup ===
            int[,] unconvertedMap = wfc.Output.ToSquare2DArray();
            int[,] finalOutput = new  int[width, height];

            for (int i = 0; i < width; i++)
            {
                for (int j = 0; j < height; j++)
                {
                    if(moduleIndexToKey[unconvertedMap[i, j]] != -1)
                        finalOutput[i, j] = moduleIndexToKey[unconvertedMap[i, j]];
                    else
                    {
                        finalOutput[i, j] = -1;
                    }
                }
            }

            modules.Dispose();
            allNeighbors.Dispose();
            weights.Dispose();
            cells.Dispose();
            cellEntropy.Dispose();
            propagationStack.Dispose();
            output.Dispose();

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


    struct WfcJob// : IJob
    {
        // Lookup structures - immutable, for reference only, and accessible in parallel

        // module constraints
        [ReadOnly][NativeDisableParallelForRestriction] public NativeParallelHashMap<int, TileConstraints> Modules;
        [ReadOnly][NativeDisableParallelForRestriction] public NativeArray<int> AllNeighbors;

        // weights
        [ReadOnly][NativeDisableParallelForRestriction] public NativeParallelHashMap<int, float> Weights;

        // Algorithm State

        // domains
        public NativeArray<Cell> Cells;

        // entropy
        public NativeArray<float> CellEntropy;

        // map size and cell count
        public int CellCount;
        public int Width;
        public int Height;

        // rng
        public Unity.Mathematics.Random random;

        // stack for propagation step
        public NativeList<int> PropagationStack;
        public int PropagationStackTop;

        // output
        public NativeArray<int> Output;

        // state of operation (error, ok)
        public State Flag;

        public enum State { OK, WARNING, ERROR }

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
                    int highBits = size - 64;    // 1..63
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

        /// <summary>
        /// Query allNeighbors with the indices and counts here to find all the neighbor data
        /// </summary>
        public struct TileConstraints
        {
            public int upStart;
            public int upCount;
            public int downStart;
            public int downCount;
            public int leftStart;
            public int leftCount;
            public int rightStart;
            public int rightCount;
        }

        public void Execute()
        {
            // Initialize all cells to the starting entropy
            for (int i = 0; i < CellEntropy.Length; i++)
            {
                UpdateEntropy(i);
            }

            // Execute the algorithm until complete
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

        private bool WaveFunctionCollapse()
        {
            // Collapse a random lowest-entropy cell
            int selectedCell = GetRandomLowestEntropyCell();
            if (selectedCell == -1)
                return true; // Algorithm finished

            CollapseCell(selectedCell);
            
            return true;

            // Push the initial collapsed cell
            PushToPropagationStack(selectedCell);

            // Propagation loop
            while (PropagationStackTop > 0)
            {
                int cell = PopFromPropagationStack();

                // If a cell has entropy 0 it has no possibilities → no need to propagate
                if (Cells[cell].domainCount == 0)
                    continue;

                int x = cell % Width;
                int y = cell / Width;

                // ---- UP ----
                if (y + 1 < Height)
                {
                    int neighborUp = cell + Width;
                    if (ConstrainCell(cell, neighborUp))
                        PushToPropagationStack(neighborUp);
                }

                // ---- DOWN ----
                if (y - 1 >= 0)
                {
                    int neighborDown = cell - Width;
                    if (ConstrainCell(cell, neighborDown))
                        PushToPropagationStack(neighborDown);
                }

                // ---- LEFT ----
                if (x - 1 >= 0)
                {
                    int neighborLeft = cell - 1;
                    if (ConstrainCell(cell, neighborLeft))
                        PushToPropagationStack(neighborLeft);
                }

                // ---- RIGHT ----
                if (x + 1 < Width)
                {
                    int neighborRight = cell + 1;
                    if (ConstrainCell(cell, neighborRight))
                        PushToPropagationStack(neighborRight);
                }
            }

            // Reset the stack for the next collapse cycle
            PropagationStackTop = 0;

            return false; // Not done yet
        }


        void PushToPropagationStack(int v) => PropagationStack[PropagationStackTop++] = v;
        int PopFromPropagationStack() => PropagationStack[--PropagationStackTop];


        private int GetRandomLowestEntropyCell()
        {
            // Find minimum entropy
            // Entropy is 0 for a domain of 1 or less, meaning:
            // error cells (no domain) and correctly collapsed cells (domain of 1)
            // are both ignored
            float minEntropy = float.MaxValue;
            for (int i = 0; i < Cells.Length; i++)
            {
                float entropy = CellEntropy[i];
                if (entropy < minEntropy)
                {
                    minEntropy = entropy;
                }
            }

            // Check if we're done (no cells left to collapse)
            if (minEntropy == float.MaxValue)
            {
                return -1; // Algorithm complete
            }

            // Count cells tied for minimum entropy (reservoir sampling)
            int selectedCell = -1;
            int tieCount = 0;
            for (int i = 0; i < Cells.Length; i++)
            {
                if (CellEntropy[i] == minEntropy) // possibly replace this with a threshold of tolerance instead of strict equality?
                {
                    tieCount++;
                    // Reservoir sampling: select with probability 1/tieCount
                    if (random.NextFloat() < 1.0f / tieCount)
                    {
                        selectedCell = i;
                    }
                }
            }

            return selectedCell;
        }

        private void UpdateEntropy(int cellId)
        {
            var cell = Cells[cellId];

            // If domain is empty or has only one element, entropy is 0
            if (cell.domainCount <= 1)
            {
                CellEntropy[cellId] = 0f;
                return;
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
                CellEntropy[cellId] = 0f;
                return;
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

            CellEntropy[cellId] = entropy;
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

        private bool ConstrainCell(int cellId, int neighbor)
        {
            Cell cell = Cells[cellId];
            Cell neighborCell = Cells[neighbor];

            // If neighbor is already collapsed to 0 possibilities, it cannot be constrained further
            if (neighborCell.domainCount == 0)
                return false;

            // Compute the allowed mask for the neighbor based on the cell's domain
            ulong allow0 = 0UL;
            ulong allow1 = 0UL;

            // For every tile still possible in "cell", OR-in the allowed neighbor tiles
            // This is a fast bit iteration pattern: extract bits one at a time.

            ulong mask0 = cell.domainMask0;
            while (mask0 != 0)
            {
                ulong bit = mask0 & (ulong)-(long)mask0;  // lowest set bit
                int tile = Tzcnt(mask0);
                mask0 ^= bit;

                TileConstraints tc = Modules[tile];
                int start, count;

                // use the correct direction based on the relationship
                if (neighbor == cellId + Width)          // UP
                {
                    start = tc.upStart;
                    count = tc.upCount;
                }
                else if (neighbor == cellId - Width)     // DOWN
                {
                    start = tc.downStart;
                    count = tc.downCount;
                }
                else if (neighbor == cellId - 1)         // LEFT
                {
                    start = tc.leftStart;
                    count = tc.leftCount;
                }
                else                                     // RIGHT
                {
                    start = tc.rightStart;
                    count = tc.rightCount;
                }

                // OR-in all allowed neighbors for this tile
                for (int i = 0; i < count; i++)
                {
                    int nbrTile = AllNeighbors[start + i];
                    if (nbrTile < 64)
                        allow0 |= 1UL << nbrTile;
                    else
                        allow1 |= 1UL << (nbrTile - 64);
                }
            }

            ulong mask1 = cell.domainMask1;
            while (mask1 != 0)
            {
                ulong bit = mask1 & (ulong)-(long)mask1;
                int tile = Tzcnt(mask1) + 64;
                mask1 ^= bit;

                TileConstraints tc = Modules[tile];
                int start, count;

                if (neighbor == cellId + Width)          // UP
                {
                    start = tc.upStart;
                    count = tc.upCount;
                }
                else if (neighbor == cellId - Width)     // DOWN
                {
                    start = tc.downStart;
                    count = tc.downCount;
                }
                else if (neighbor == cellId - 1)         // LEFT
                {
                    start = tc.leftStart;
                    count = tc.leftCount;
                }
                else                                     // RIGHT
                {
                    start = tc.rightStart;
                    count = tc.rightCount;
                }

                for (int i = 0; i < count; i++)
                {
                    int nbrTile = AllNeighbors[start + i];
                    if (nbrTile < 64)
                        allow0 |= 1UL << nbrTile;
                    else
                        allow1 |= 1UL << (nbrTile - 64);
                }
            }


            // Compute: neighbor.domainMask &= allowedMask
            ulong new0 = neighborCell.domainMask0 & allow0;
            ulong new1 = neighborCell.domainMask1 & allow1;

            // If no change: no propagation needed
            if (new0 == neighborCell.domainMask0 && new1 == neighborCell.domainMask1)
                return false;

            // Update domain
            neighborCell.domainMask0 = new0;
            neighborCell.domainMask1 = new1;

            // Count bits
            int count0 = PopCount(new0);
            int count1 = PopCount(new1);
            neighborCell.domainCount = count0 + count1;

            Cells[neighbor] = neighborCell;

            return true; // domain changed → must propagate

            //todo update entropy??
            
            //todo update selected if collapsed from this
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Tzcnt(ulong value)
        {
            if (value == 0)
                return 64;

            int count = 0;
            while ((value & 1UL) == 0UL)
            {
                count++;
                value >>= 1;
            }
            return count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int PopCount(ulong value)
        {
            // classic hack: "Hamming Weight" algorithm
            value -= (value >> 1) & 0x5555555555555555UL;
            value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
            value = (value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((value * 0x0101010101010101UL) >> 56);
        }

    }

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
            foreach (int i in domain) { this.domain.Add(i); }
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
        public bool Constrain(IReadOnlyList<int> neighborDomain,
                              Direction direction,
                              SerializedDictionary<int, TileModule> modules,
                              Dictionary<int, float> weights)
        {
            bool constrained = false;

            if (Collapsed) return false;

            HashSet<int> valid = new HashSet<int>();
            foreach (int id in neighborDomain)
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
                                         SerializedDictionary<int, TileModule> modules,
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
