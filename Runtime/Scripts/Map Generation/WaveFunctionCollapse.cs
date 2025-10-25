using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using AYellowpaper.SerializedCollections;
using static MagusStudios.Arcanist.WaveFunctionCollapse.WfcModuleSet;
using UnityEngine.Tilemaps;
using MagusStudios.Arcanist.Tilemaps;
using MagusStudios.Collections;
using System.Collections;
using MagusStudios.Arcanist.Utils;

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
        [SerializeField] int seed;

        public delegate void CellConstrainedHandler(Vector2Int pos, int domainSize);

        private Tilemap tilemap;
        private System.Random random;
        private TilemapNumberOverlay _debugOverlay;

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

        /// <summary>
        /// Generates a map of tile IDs according to the WFC algorithm.
        /// </summary>
        private int[,] GenerateMap()
        {
            throw new System.NotImplementedException();
            //Debug.Log($"[{nameof(WaveFunctionCollapse)}] Starting WFC map generation with module set {moduleSet.name}");

            //var modules = moduleSet.Modules;
            //int[] allTileIDs = modules.Keys.ToArray();
            //var weights = modules.ToDictionary(m => m.Key, m => m.Value.weight);

            //int width = mapSize.x;
            //int height = mapSize.y;

            ////the map that will be output, which is just a 2d array of ints (tile ids)
            //var world = new Grid(mapSize.x, mapSize.y);

            //// Initialize all cells in the grid and link them to their neighbors for algorithmic reasons

            //// first pass of initialization: create all cells
            //for (int x = 0; x < width; x++)
            //{
            //    for (int y = 0; y < height; y++)
            //    {
            //        world.map[x, y] = new Cell(allTileIDs, x, y);
            //    }
            //}

            //// second pass of initialization assign neighbor references to each cell
            //for (int x = 0; x < width; x++)
            //{
            //    for (int y = 0; y < height; y++)
            //    {
            //        Cell neighborUp = (y < height - 1) ? world.map[x, y + 1] : null;
            //        Cell neighborDown = (y > 0) ? world.map[x, y - 1] : null;
            //        Cell neighborLeft = (x > 0) ? world.map[x - 1, y] : null;
            //        Cell neighborRight = (x < width - 1) ? world.map[x + 1, y] : null;

            //        // Update the cell with its neighbors
            //        world.map[x, y].SetNeighbors(neighborUp, neighborDown, neighborLeft, neighborRight);
            //    }
            //}

            //// main generation loop
            //List<Cell> cellsCollapsed = new List<Cell>();
            //bool done = false;
            //while (!done) done = world.WaveFunctionCollapse(weights, modules, ref cellsCollapsed, random);

            //// 4. Build result
            //var map = new int[mapSize.x, mapSize.y];

            //Debug.Log($"[{nameof(WaveFunctionCollapse)}] Building map...");
            //for (int x = 0; x < width; x++)
            //    for (int y = 0; y < height; y++)
            //    {
            //        map[x, y] = world.map[x, y].GetCollapsedTile();
            //        //Debug.Log($"[{nameof(WaveFunctionCollapse)}] cell with domain size of {world.map[x, y].Domain.Count} was added to the map");
            //    }

            //return map;
        }

        private void UpdateTileDomain(Vector2Int pos, int domainSize)
        {
            _debugOverlay.SetNumber(pos.ToVector3Int(), domainSize);
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
            random = new System.Random(seed);

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

        private bool ShouldShowDebugOverlay()
        {
            return showDomains && _debugOverlay != null;
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
