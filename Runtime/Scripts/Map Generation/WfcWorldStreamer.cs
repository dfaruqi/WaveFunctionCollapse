using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AYellowpaper.SerializedCollections;
using MagusStudios.WaveFunctionCollapse.Utils;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Tilemaps;
using Random = Unity.Mathematics.Random;
using Vector3 = UnityEngine.Vector3;

namespace MagusStudios.WaveFunctionCollapse
{
    public class WfcWorldStreamer : MonoBehaviour
    {
        public Transform Target; // The target transform to generate the world around (the player)
        public Tilemap TargetTilemap; // The target tilemap to generate the world upon
        public uint Seed;

        [SerializeField] int generateDistance = 1;
        [SerializeField] int drawDistance = 1;
        [SerializeField] bool clearOnStart = true;
        [SerializeField] Biome biome;

        // ~ Constants ~

        // directory where chunks are saved
        private string _chunkDirectory;

        // size of loaded/saved chunks, must be even
        // suggestions: 16,32,48,64
        public const int CHUNK_SIZE = 24;

        // size of generated blocks, which are later converted to chunks. Must satisfy the following:
        // BLOCKSIZE is even and BLOCK_SIZE < CHUNK_SIZE and BLOCK_SIZE > CHUNK_SIZE / 2
        // suggestions: 12,24,36,48
        private const int BLOCK_SIZE = 18;

        // Tile draw is split into sub-blocks of this size to spread SetTilesBlock cost across frames.
        // Computed from CHUNK_SIZE so it always divides the chunk evenly. Targets ~16 per side, capped
        // at CHUNK_SIZE/2 so we always get at least 4 sub-blocks per chunk.
        private static readonly int SUB_BLOCK_SIZE = ComputeSubBlockSize(CHUNK_SIZE);

        private static int ComputeSubBlockSize(int chunkSize)
        {
            int target = Math.Min(16, Math.Max(1, chunkSize / 2));
            for (int s = target; s >= 1; s--)
            {
                if (chunkSize % s == 0) return s;
            }
            return 1;
        }

        // ~ State ~

        // Initialization (load from file)
        private bool _initialized = false;

        // Chunks currently loaded and their data
        private readonly Dictionary<Vector2Int, int[]> _loadedChunks = new();

        // Chunks currently drawn and on the tilemap
        private readonly HashSet<Vector2Int> _drawnChunks = new();

        // List of job handles for blocks currently generating
        private readonly List<JobHandle> _jobHandles = new();

        // record of all blocks generated and the layer they have been generated through
        // (0=pregenerated, 1-4=layers 1-4)
        private Dictionary<Vector2Int, byte> _allGeneratedBlocks = new();

        // Set whenever _allGeneratedBlocks is mutated; cleared after a successful save. Lets us skip
        // re-serializing+writing the (potentially large, growing) layer-progression file when nothing
        // has actually changed since the last save cycle.
        private bool _allGeneratedBlocksDirty = false;

        // the last chunk the player was in, used to determine when to update chunks
        private Vector2Int _lastPlayerChunk = new(int.MaxValue, int.MaxValue);

        // cached containers used in chunks updates to avoid reallocation. 
        private HashSet<Vector2Int> _unloadedChunksInLoadDistance = new();
        private readonly HashSet<Vector2Int> _chunksPregenerated = new();
        private readonly HashSet<Vector2Int> _chunksUnloaded = new();
        private HashSet<Vector2Int> _chunksInGenerateDistance = new();
        private readonly HashSet<Vector2Int> _chunksAffectedByGeneration = new();
        private readonly HashSet<Vector2Int> _chunksInDrawDistance = new();
        private readonly HashSet<Vector2Int> _chunksToDraw = new();
        private HashSet<Vector2Int> _chunksToUndraw = new();
        private HashSet<Vector2Int> _chunksToUnload = new();
        private HashSet<Vector2Int>[] _blocksToGenerate = new HashSet<Vector2Int>[4];
        private readonly List<Task> _saveTasks = new();
        private readonly TileBase[] _subTileDrawBuffer = new TileBase[SUB_BLOCK_SIZE * SUB_BLOCK_SIZE];
        private readonly TileBase[] _subNullTileBuffer = new TileBase[SUB_BLOCK_SIZE * SUB_BLOCK_SIZE];
        private readonly Stack<WfcBlockState> _blockStatePool = new();

        // Per-template adjacency lookup cache for IsOutputValid.
        // Outer dict: template -> inner dict. Inner dict: tileKey -> array indexed by (int)Direction
        // of the HashSet of allowed neighbor tile keys. Built once per template.
        private readonly Dictionary<WfcTemplate, Dictionary<int, HashSet<int>[]>> _adjacencyLookupCache = new();

        // Per-template WfcBiomeData cache. WfcBiomeData is identical for a given template (it just
        // converts the template's modules into the native lookup structures the WFC job uses), so
        // we build it once and reuse — the previous code allocated a fresh one (plus two managed
        // dictionaries inside) per affected chunk per generation cycle. Disposed in OnDestroy.
        private readonly Dictionary<WfcTemplate, WfcBiomeData> _biomeDataCache = new();

        // Per-cycle scratch for GenerateBlocks; lifted to a field so the dictionary itself isn't
        // re-allocated each cycle. Cleared between layers.
        private readonly Dictionary<Vector2Int, WfcBlockState> _stateDict = new();

        // Reusable border buffers for GetBordersOfBlock — previously 4 fresh List<int> per call.
        private readonly List<int> _bordersUp = new(BLOCK_SIZE);
        private readonly List<int> _bordersDown = new(BLOCK_SIZE);
        private readonly List<int> _bordersLeft = new(BLOCK_SIZE);
        private readonly List<int> _bordersRight = new(BLOCK_SIZE);

        // Reusable list of in-flight chunk-load tasks for UpdateChunks — replaces a LINQ Select
        // that was allocating a closure per chunk plus an iterator object.
        private readonly List<Task> _loadTasks = new();

        // Pre-computed border lookup tables. For each (layer, t) where t is the position along
        // a block edge, stores the chunk-offset (relative to the current chunk) and flat tile
        // index that the border tile maps to. Replaces 4 * BLOCK_SIZE calls to
        // GetChunkAndLocalTilePositionFromTile per call to GetBordersOfBlock.
        private struct BorderOffset
        {
            public Vector2Int chunkOffset;
            public int localIndex;
        }

        private BorderOffset[,] _borderOffsetsTop;     // [layer, t]
        private BorderOffset[,] _borderOffsetsBottom;
        private BorderOffset[,] _borderOffsetsLeft;
        private BorderOffset[,] _borderOffsetsRight;

        // Reusable scratch for parallelized validation-and-apply phase in GenerateBlocks.
        private struct ValidationEntry
        {
            public Vector2Int ChunkPos;
            public WfcBlockState BlockState;
            public WfcBiomeData BiomeData;
            public Task<bool> Task;
        }

        private readonly List<ValidationEntry> _validationEntries = new();

        // ~ Events ~

        public delegate void ChunkDrawnHandler(Vector2Int chunkPos, IReadOnlyList<int> chunkData, Biome biome);

        public event ChunkDrawnHandler OnChunkDrawn;

        public delegate void ChunkUndrawnHandler(Vector2Int
            chunkPos);

        public event ChunkUndrawnHandler OnChunkUndrawn;

        // ~ Data structs ~

        private static readonly Vector2Int[] NeighborOffsets =
        {
            new(-1, 1), // UpLeft
            new(0, 1), // Up
            new(1, 1), // UpRight
            new(-1, 0), // Left
            new(1, 0), // Right
            new(-1, -1), // DownLeft
            new(0, -1), // Down
            new(1, -1), // DownRight
        };

        private Vector2Int[] BlockOffsets = new Vector2Int[4];

        private void Awake()
        {
            _chunkDirectory = Path.Combine(Application.persistentDataPath, "tile_chunks");

            // create chunk directory if it does not exist
            // todo add save files
            if (!Directory.Exists(_chunkDirectory))
            {
                Directory.CreateDirectory(_chunkDirectory);
                // (previously returned here, which skipped BlockOffsets init and everything below;
                // continuing now so the rest of Awake runs on a fresh install too)
            }

            // initialize offsets for blocks (just some reference data that we won't have to recompute)
            int blockGap = CHUNK_SIZE - BLOCK_SIZE;
            BlockOffsets[0] = new Vector2Int(blockGap / 2, -CHUNK_SIZE / 2 + blockGap / 2);
            BlockOffsets[1] = new Vector2Int(CHUNK_SIZE - BLOCK_SIZE / 2, -CHUNK_SIZE / 2 + blockGap / 2);
            BlockOffsets[2] = new Vector2Int(CHUNK_SIZE - BLOCK_SIZE / 2, blockGap / 2);
            BlockOffsets[3] = new Vector2Int(blockGap / 2, blockGap / 2);

            // Pre-size _jobHandles to the maximum number of blocks we will schedule per layer
            // (one job per chunk in generateDistance). Avoids List growth reallocations.
            int chunksInGenerateDistance = (2 * generateDistance + 1) * (2 * generateDistance + 1);
            _jobHandles.Capacity = chunksInGenerateDistance;
            _validationEntries.Capacity = chunksInGenerateDistance;

            PrecomputeBorderOffsets();

            if (clearOnStart)
            {
                TargetTilemap.RefreshAllTiles();
                TargetTilemap.ClearAllTiles();
            }

            // initialize blocks to generate array
            for (int i = 0; i < _blocksToGenerate.Length; i++)
            {
                _blocksToGenerate[i] = new HashSet<Vector2Int>();
            }

            // load all generated chunk coords
            _ = InitializeAsync();
        }

        private bool AllJobsCompleted()
        {
            foreach (JobHandle h in _jobHandles)
                if (!h.IsCompleted)
                    return false;
            return true;
        }

        private void OnEnable()
        {
            StartCoroutine(StreamWorld());
        }

        private void OnDisable()
        {
            StopAllCoroutines();
        }

        async Task InitializeAsync()
        {
            _allGeneratedBlocks = await LoadChunkLayersAsync(GetAllGeneratedBlocksPath());
            _initialized = true;
        }

        private IEnumerator StreamWorld()
        {
            while (!_initialized)
            {
                yield return null;
            }

            while (true)
            {
                Vector2Int currentChunk = GetPlayerChunk(Target.position);

                if (currentChunk != _lastPlayerChunk)
                {
                    yield return StartCoroutine(UpdateChunks(currentChunk));
                    _lastPlayerChunk = currentChunk;
                }

                yield return new WaitForSeconds(0.25f); // throttle
            }
        }

        private IEnumerator UpdateChunks(Vector2Int playerChunkPosition)
        {
            // - Load Chunks -

            _unloadedChunksInLoadDistance.Clear();
            GetUnloadedChunksInLoadDistance(playerChunkPosition, ref _unloadedChunksInLoadDistance);

            // keep track of chunks that get generated, loaded, or unloaded
            _chunksPregenerated.Clear();
            _chunksUnloaded.Clear();

            // load or pre-generate
            _loadTasks.Clear();
            foreach (Vector2Int coord in _unloadedChunksInLoadDistance)
            {
                _loadTasks.Add(LoadOrPregenerateChunkAsync(coord, _chunksPregenerated));
            }
            Task loadTask = Task.WhenAll(_loadTasks);

            while (!loadTask.IsCompleted)
                yield return null;
            if (loadTask.IsFaulted)
                throw loadTask.Exception;

            // - Generate Blocks -

            // generate blocks
            // container to get all blocks that should be generated and to what layer they should be generated to
            foreach (HashSet<Vector2Int> hashSet in _blocksToGenerate)
            {
                hashSet?.Clear();
            }

            _chunksInGenerateDistance.Clear();
            GetChunksInDistance(playerChunkPosition, generateDistance, _chunksInGenerateDistance);

            foreach (Vector2Int chunk in _chunksInGenerateDistance)
            {
                if (_allGeneratedBlocks[chunk] < 4) _blocksToGenerate[3].Add(chunk);
            }

            CascadeBlockDependencies(ref _blocksToGenerate);

            if (_blocksToGenerate.Length != 0)
            {
                Task generateBlocksTask = GenerateBlocks(_blocksToGenerate);
                while (!generateBlocksTask.IsCompleted)
                    yield return null;
                if (loadTask.IsFaulted)
                    throw loadTask.Exception;

                // update all generated blocks dictionary
                for (byte i = 0; i < 4; i++)
                {
                    foreach (Vector2Int block in _blocksToGenerate[i])
                    {
                        byte oldBlockGeneratedTo = _allGeneratedBlocks[block];
                        byte newBlockGeneratedTo = (byte)(i + 1);
                        if (oldBlockGeneratedTo < newBlockGeneratedTo)
                        {
                            _allGeneratedBlocks[block] = newBlockGeneratedTo;
                            _allGeneratedBlocksDirty = true;
                        }
                    }
                }
            }

            yield return null;

            // - Unload Chunks -

            // unload chunks
            _chunksToUnload.Clear();
            GetChunksOutsideDistance(playerChunkPosition, generateDistance + 2, _loadedChunks.Keys,
                ref _chunksToUnload);

            foreach (Vector2Int chunkPos in _chunksToUnload)
            {
                if (_loadedChunks.Remove(chunkPos)) _chunksUnloaded.Add(chunkPos);
            }

            // - Update Files -

            // get chunks affected by generation from block dependencies
            _chunksAffectedByGeneration.Clear();

            foreach (Vector2Int block in _blocksToGenerate[0])
            {
                _chunksAffectedByGeneration.Add(block);
                _chunksAffectedByGeneration.Add(block + Vector2Int.down);
            }

            foreach (Vector2Int block in _blocksToGenerate[1])
            {
                _chunksAffectedByGeneration.Add(block);
                _chunksAffectedByGeneration.Add(block + Vector2Int.right);
                _chunksAffectedByGeneration.Add(block + Vector2Int.down);
                _chunksAffectedByGeneration.Add(block + Vector2Int.right + Vector2Int.down);
            }

            foreach (Vector2Int block in _blocksToGenerate[2])
            {
                _chunksAffectedByGeneration.Add(block);
                _chunksAffectedByGeneration.Add(block + Vector2Int.right);
            }

            foreach (Vector2Int block in _blocksToGenerate[3])
            {
                _chunksAffectedByGeneration.Add(block);
            }

            // add chunks that were pregenerated (if not already added)
            foreach (Vector2Int chunk in _chunksPregenerated)
            {
                _chunksAffectedByGeneration.Add(chunk);
            }

            // write all chunks that were changed to file and the all generated chunk positions dict
            _saveTasks.Clear();
            foreach (Vector2Int chunkPos in _chunksAffectedByGeneration)
            {
                _saveTasks.Add(SaveChunkAsync(chunkPos, _loadedChunks[chunkPos]));
            }
            // Only re-save the layer-progression file when something actually changed. This dictionary
            // grows unboundedly with exploration, so the serialize+write cost grows over the session;
            // skipping no-op saves keeps the cost off most cycles entirely.
            if (_allGeneratedBlocksDirty)
            {
                _saveTasks.Add(SaveAllGeneratedBlocksDictAsync(_allGeneratedBlocks, GetAllGeneratedBlocksPath()));
                _allGeneratedBlocksDirty = false;
            }

            Task saveAll = Task.WhenAll(_saveTasks);
            bool allChunksSaved;
            do
            {
                allChunksSaved = true;
                for (int i = 0; i < _jobHandles.Count; i++)
                {
                    if (!_jobHandles[i].IsCompleted)
                    {
                        allChunksSaved = false;
                        break;
                    }
                }

                if (!allChunksSaved) yield return null;
            } while (!allChunksSaved);

            if (saveAll.IsFaulted)
                throw saveAll.Exception;

            // - Update Tilemap - 

            // Walk only chunks within draw distance — that's the visibility filter for generation
            // effects too. A chunk is queued for (re)draw if it isn't drawn yet, OR it's already drawn
            // but its tiles changed this cycle (generation writes into neighbor chunks via block
            // dependencies, so "affected" can include chunks where only a small region actually
            // changed). The previous `(drawn && affected)` clause was redundant — if !drawn was
            // false we know drawn is true, so just check `affected`.
            _chunksToDraw.Clear();
            _chunksInDrawDistance.Clear();
            GetChunksInDistance(playerChunkPosition, drawDistance, _chunksInDrawDistance);
            foreach (Vector2Int c in _chunksInDrawDistance)
            {
                if (!_drawnChunks.Contains(c) || _chunksAffectedByGeneration.Contains(c))
                    _chunksToDraw.Add(c);
            }

            yield return StartCoroutine(DrawChunks(_chunksToDraw));

            // un-draw chunks that are drawn and outside draw distance
            _chunksToUndraw.Clear();
            GetChunksOutsideDistance(playerChunkPosition, drawDistance + 1, _drawnChunks,
                ref _chunksToUndraw);
            foreach (Vector2Int chunkPos in _chunksToUndraw)
            {
                Vector3Int chunkOrigin = (chunkPos * CHUNK_SIZE).ToVector3Int();
                Vector3Int subSize = new Vector3Int(SUB_BLOCK_SIZE, SUB_BLOCK_SIZE, 1);

                // Same sub-block split as DrawChunks — clear the chunk in pieces, yielding between
                // each SetTilesBlock so undraw doesn't spike a frame either.
                for (int subY = 0; subY < CHUNK_SIZE; subY += SUB_BLOCK_SIZE)
                {
                    for (int subX = 0; subX < CHUNK_SIZE; subX += SUB_BLOCK_SIZE)
                    {
                        BoundsInt subBounds = new BoundsInt(
                            chunkOrigin + new Vector3Int(subX, subY, 0), subSize);
                        TargetTilemap.SetTilesBlock(subBounds, _subNullTileBuffer);

                        yield return null;
                    }
                }

                _drawnChunks.Remove(chunkPos);

                OnChunkUndrawn?.Invoke(chunkPos);
            }

            // - Log -

            Debug.Log(
                $"{nameof(WfcWorldStreamer)} Chunk Updates - \n" +
                $"   loaded/generated: {_unloadedChunksInLoadDistance.Count}]\n" +
                $"   unloaded: {_chunksUnloaded.Count})\n" +
                $"   drawn: {_chunksToDraw.Count}");
        }

        /// <summary>
        /// Assumes all chunks passed in are loaded.
        /// </summary>
        /// <param name="chunks"></param>
        /// <returns></returns>
        private IEnumerator DrawChunks(HashSet<Vector2Int> chunks)
        {
            // draw chunks that are within the draw distance and were affected by generation or are not drawn
            foreach (Vector2Int chunkPos in chunks)
            {
                // Hoist per-chunk lookups out of the inner loop — GetTemplate does a Perlin sample
                // and would otherwise run once per tile.
                int[] chunkData = _loadedChunks[chunkPos];
                TileDatabase tileDatabase = biome.GetTemplate(chunkPos).TileDatabase;
                Vector3Int chunkOrigin = (chunkPos * CHUNK_SIZE).ToVector3Int();
                Vector3Int subSize = new Vector3Int(SUB_BLOCK_SIZE, SUB_BLOCK_SIZE, 1);

                // Stream the chunk to the tilemap one sub-block at a time, yielding between each
                // SetTilesBlock call so a single large chunk doesn't spike a frame.
                for (int subY = 0; subY < CHUNK_SIZE; subY += SUB_BLOCK_SIZE)
                {
                    for (int subX = 0; subX < CHUNK_SIZE; subX += SUB_BLOCK_SIZE)
                    {
                        for (int dy = 0; dy < SUB_BLOCK_SIZE; dy++)
                        {
                            int chunkRowStart = (subY + dy) * CHUNK_SIZE + subX;
                            int subRowStart = dy * SUB_BLOCK_SIZE;
                            for (int dx = 0; dx < SUB_BLOCK_SIZE; dx++)
                            {
                                int tileKey = chunkData[chunkRowStart + dx];
                                _subTileDrawBuffer[subRowStart + dx] =
                                    tileKey < 0 ? null : tileDatabase[tileKey];
                            }
                        }

                        BoundsInt subBounds = new BoundsInt(
                            chunkOrigin + new Vector3Int(subX, subY, 0), subSize);
                        TargetTilemap.SetTilesBlock(subBounds, _subTileDrawBuffer);

                        yield return null;
                    }
                }

                _drawnChunks.Add(chunkPos);
                OnChunkDrawn?.Invoke(chunkPos, chunkData, biome);
            }
        }
        
        private async Task GenerateBlocks(HashSet<Vector2Int>[] blocksToGenerate)
        {
            _stateDict.Clear();

            for (byte layer = 0; layer < 4; layer++)
            {
                foreach (Vector2Int chunk in blocksToGenerate[layer])
                {
                    WfcBiomeData wfcBiomeData = GetOrBuildBiomeData(biome.GetTemplate(chunk));
                    WfcTemplate template = wfcBiomeData.Template;

                    Random rng = new Random(TileUtils.HashWorldBlock(Seed, chunk, layer));
                    WfcUtils.Borders borders = GetBordersOfBlock(chunk, layer, wfcBiomeData.moduleKeyToIndex);

                    // Rent from pool instead of allocating a new one, as these are relatively expensive to reallocate.
                    WfcBlockState wfcBlockState = RentBlockState(
                        new Vector2Int(BLOCK_SIZE, BLOCK_SIZE),
                        template.TileRules.Modules.Count,
                        template, borders);

                    _stateDict.Add(chunk, wfcBlockState);

                    WfcJob wfc = new WfcJob
                    {
                        Modules = wfcBiomeData.Modules,
                        Weights = wfcBlockState.Weights,
                        Cells = wfcBlockState.Cells,
                        AllDirectionPermutations = wfcBiomeData.directions,
                        UpBorder = wfcBlockState.UpBorder,
                        DownBorder = wfcBlockState.DownBorder,
                        LeftBorder = wfcBlockState.LeftBorder,
                        RightBorder = wfcBlockState.RightBorder,
                        EntropyHeap = wfcBlockState.EntropyHeap,
                        EntropyIndices = wfcBlockState.EntropyIndices,
                        random = rng,
                        PropagationStack = wfcBlockState.PropagationStack,
                        PropagationStackTop = 0,
                        Width = BLOCK_SIZE,
                        Height = BLOCK_SIZE,
                        Output = wfcBlockState.Output,
                        Flag = WfcJob.State.OK
                    };

                    _jobHandles.Add(wfc.Schedule());
                }

                // Kick off all batched jobs immediately rather than waiting for an implicit flush —
                // this maximizes job-system parallelism while we're still doing main-thread setup.
                JobHandle.ScheduleBatchedJobs();

                while (!AllJobsCompleted())
                {
                    await Task.Yield();
                }

                foreach (JobHandle jobHandle in _jobHandles)
                    jobHandle.Complete();

                _jobHandles.Clear();

                // Launch all chunk validations up front. IsOutputValid does its heavy lifting on the
                // thread pool, so launching all of them before awaiting any lets them run in parallel.
                _validationEntries.Clear();
                foreach (KeyValuePair<Vector2Int, WfcBlockState> kvp in _stateDict)
                {
                    WfcBiomeData wfcBiomeData = GetOrBuildBiomeData(biome.GetTemplate(kvp.Key));
                    _validationEntries.Add(new ValidationEntry
                    {
                        ChunkPos = kvp.Key,
                        BlockState = kvp.Value,
                        BiomeData = wfcBiomeData,
                        Task = IsOutputValid(
                            kvp.Value.Output, kvp.Key, layer,
                            wfcBiomeData.moduleIndexToKey),
                    });
                }

                // Now drain results in order. Any task that finished while we were awaiting an earlier
                // one returns immediately. 
                foreach (ValidationEntry entry in _validationEntries)
                {
                    bool valid = await entry.Task;

                    if (!valid)
                        Debug.LogWarning(
                            $"[{nameof(WfcWorldStreamer)}] Error in chunk {entry.ChunkPos} on layer {layer}");
                    else
                        UpdateChunksFromBlock(
                            entry.ChunkPos, layer, entry.BlockState.Output,
                            entry.BiomeData.moduleIndexToKey,
                            entry.BiomeData.Template.DefaultTileKey);

                    // Return to pool instead of disposing.
                    ReturnBlockState(entry.BlockState);
                }

                _stateDict.Clear();
            }
        }

        private WfcBiomeData GetOrBuildBiomeData(WfcTemplate template)
        {
            if (!_biomeDataCache.TryGetValue(template, out WfcBiomeData data))
            {
                data = new WfcBiomeData(template);
                _biomeDataCache[template] = data;
            }
            return data;
        }

        // Pool helpers for generation
        private WfcBlockState RentBlockState(
            Vector2Int size, int moduleCount, WfcTemplate template,
            WfcUtils.Borders borders)
        {
            if (_blockStatePool.TryPop(out WfcBlockState pooled))
            {
                pooled.Reset(size, moduleCount, template, borders);
                return pooled;
            }

            // Pooled state was wrong size (or pool was empty) — make a fresh one.
            if (pooled != null) pooled.Dispose(); // discard the incompatible one
            return new WfcBlockState(size, moduleCount, template, borders);
        }

        private void ReturnBlockState(WfcBlockState state) => _blockStatePool.Push(state);

        // On teardown, drain the pool and dispose cached biome data (which owns native containers).
        private void OnDestroy()
        {
            while (_blockStatePool.TryPop(out WfcBlockState s))
                s.Dispose();

            foreach (WfcBiomeData data in _biomeDataCache.Values)
                data.Dispose();
            _biomeDataCache.Clear();
        }

        private void GetUnloadedChunksInLoadDistance(Vector2Int playerChunkPosition,
            ref HashSet<Vector2Int> unloadedChunksInLoadDistance)
        {
            unloadedChunksInLoadDistance.Clear();

            int chunkCeilX = generateDistance + 1;
            int chunkCeilY = generateDistance + 1;

            for (int y = -chunkCeilY; y <= chunkCeilY; y++)
            {
                for (int x = -chunkCeilX; x <= chunkCeilX; x++)
                {
                    Vector2Int chunkPos = playerChunkPosition + new Vector2Int(x, y);
                    if (!_loadedChunks.ContainsKey(chunkPos)) unloadedChunksInLoadDistance.Add(chunkPos);
                }
            }
        }

        private void GetChunksOutsideDistance(Vector2Int position, int distance, ICollection<Vector2Int> chunks,
            ref HashSet<Vector2Int> chunksOutsideDistance)
        {
            chunksOutsideDistance.Clear();
            foreach (Vector2Int chunkPos in chunks)
            {
                if (Mathf.Abs(position.y - chunkPos.y) > distance ||
                    Mathf.Abs(position.x - chunkPos.x) > distance)
                {
                    chunksOutsideDistance.Add(chunkPos);
                }
            }
        }

        private void GetChunksInDistance(Vector2Int position, int distance, HashSet<Vector2Int> chunksInDistance)
        {
            chunksInDistance.Clear();

            for (int y = -distance; y <= distance; y++)
            {
                for (int x = -distance; x <= distance; x++)
                {
                    Vector2Int chunkPos = position + new Vector2Int(x, y);
                    chunksInDistance.Add(chunkPos);
                }
            }
        }

        private async Task LoadOrPregenerateChunkAsync(Vector2Int chunkPos, HashSet<Vector2Int> chunksPregenerated)
        {
            if (!_allGeneratedBlocks.ContainsKey(chunkPos))
            {
                int size = CHUNK_SIZE * CHUNK_SIZE;
                int[] grass = new int[size];
                for (int i = 0; i < size; i++)
                {
                    grass[i] = biome.GetTemplate(chunkPos).DefaultTileKey;
                }

                _loadedChunks.Add(chunkPos, grass);
                chunksPregenerated.Add(chunkPos);
                _allGeneratedBlocks.Add(chunkPos, 0);
                _allGeneratedBlocksDirty = true;
                return;
            }

            if (!_loadedChunks.ContainsKey(chunkPos))
            {
                _loadedChunks.Add(chunkPos, await LoadChunkAsync(chunkPos));
            }
        }

        private void CascadeBlockDependencies(ref HashSet<Vector2Int>[] blocksToGenerate)
        {
            // The layer 4 block at x, y depends on layer 3 blocks at x, y and x - 1, y
            foreach (Vector2Int block in blocksToGenerate[3])
            {
                for (int x = 0; x >= -1; x--)
                {
                    Vector2Int dependent = new Vector2Int(block.x + x, block.y);
                    if (_allGeneratedBlocks[dependent] < 3)
                    {
                        if (!blocksToGenerate[2].Contains(dependent))
                            blocksToGenerate[2].Add(dependent);
                    }
                }
            }

            // The layer 3 block at x, y depends on layer 2 blocks at x, y and x, y+1
            foreach (Vector2Int block in blocksToGenerate[2])
            {
                for (int y = 0; y <= 1; y++)
                {
                    Vector2Int dependent = new Vector2Int(block.x, block.y + y);
                    if (_allGeneratedBlocks[dependent] < 2)
                    {
                        if (!blocksToGenerate[1].Contains(dependent))
                            blocksToGenerate[1].Add(dependent);
                    }
                }
            }

            // The layer 2 block at x, y depends  on layer 1 blocks x, y and x+1, y
            foreach (Vector2Int block in blocksToGenerate[1])
            {
                for (int x = 0; x <= 1; x++)
                {
                    Vector2Int dependent = new Vector2Int(block.x + x, block.y);
                    if (_allGeneratedBlocks[dependent] < 1)
                    {
                        blocksToGenerate[0].Add(dependent);
                    }
                }
            }
        }

        private Dictionary<int, HashSet<int>[]> GetOrBuildAdjacencyLookup(WfcTemplate template)
        {
            if (_adjacencyLookupCache.TryGetValue(template, out var cached))
                return cached;

            SerializedDictionary<int, WfcTileRules.AllowedNeighbors> modules = template.TileRules.Modules;
            var lookup = new Dictionary<int, HashSet<int>[]>(modules.Count);
            foreach (var kvp in modules)
            {
                var perDirection = new HashSet<int>[4];
                perDirection[(int)Direction.Up] = new HashSet<int>(kvp.Value.Neighbors[Direction.Up]);
                perDirection[(int)Direction.Down] = new HashSet<int>(kvp.Value.Neighbors[Direction.Down]);
                perDirection[(int)Direction.Left] = new HashSet<int>(kvp.Value.Neighbors[Direction.Left]);
                perDirection[(int)Direction.Right] = new HashSet<int>(kvp.Value.Neighbors[Direction.Right]);
                lookup[kvp.Key] = perDirection;
            }

            _adjacencyLookupCache[template] = lookup;
            return lookup;
        }

        private Task<bool> IsOutputValid(NativeArray<int> output, Vector2Int chunkPos, int layer,
            int[] moduleIndexToKey)
        {
            Dictionary<int, HashSet<int>[]> adjacency =
                GetOrBuildAdjacencyLookup(biome.GetTemplate(chunkPos));

            Vector2Int blockStartTilePosGlobal = chunkPos * CHUNK_SIZE + BlockOffsets[layer];

            // Snapshot main-thread-only state so the validation loop can run on a thread pool thread:
            // copy the NativeArray to managed memory and pre-fetch the four block-edge neighbor strips
            // from _loadedChunks. moduleIndexToKey and the adjacency lookup are not mutated after
            // construction, so they're safe to read concurrently.
            int[] outputCopy = new int[output.Length];
            output.CopyTo(outputCopy);

            int[] leftBorder = new int[BLOCK_SIZE];
            int[] rightBorder = new int[BLOCK_SIZE];
            int[] upBorder = new int[BLOCK_SIZE];
            int[] downBorder = new int[BLOCK_SIZE];

            for (int n = 0; n < BLOCK_SIZE; n++)
            {
                (Vector2Int lc, Vector2Int lp) = GetChunkAndLocalTilePositionFromTile(
                    blockStartTilePosGlobal + Vector2Int.up * n + Vector2Int.left);
                leftBorder[n] = _loadedChunks[lc][TileUtils.Flatten(lp, CHUNK_SIZE)];

                (Vector2Int rc, Vector2Int rp) = GetChunkAndLocalTilePositionFromTile(
                    blockStartTilePosGlobal + Vector2Int.up * n + Vector2Int.right * BLOCK_SIZE);
                rightBorder[n] = _loadedChunks[rc][TileUtils.Flatten(rp, CHUNK_SIZE)];

                (Vector2Int uc, Vector2Int up) = GetChunkAndLocalTilePositionFromTile(
                    blockStartTilePosGlobal + Vector2Int.up * BLOCK_SIZE + Vector2Int.right * n);
                upBorder[n] = _loadedChunks[uc][TileUtils.Flatten(up, CHUNK_SIZE)];

                (Vector2Int dc, Vector2Int dp) = GetChunkAndLocalTilePositionFromTile(
                    blockStartTilePosGlobal + Vector2Int.down + Vector2Int.right * n);
                downBorder[n] = _loadedChunks[dc][TileUtils.Flatten(dp, CHUNK_SIZE)];
            }

            return Task.Run(() => ValidateBlockOutput(
                outputCopy, adjacency, moduleIndexToKey,
                leftBorder, rightBorder, upBorder, downBorder, chunkPos));
        }

        private static bool ValidateBlockOutput(
            int[] output,
            Dictionary<int, HashSet<int>[]> adjacency,
            int[] moduleIndexToKey,
            int[] leftBorder, int[] rightBorder, int[] upBorder, int[] downBorder,
            Vector2Int chunkPos)
        {
            for (int i = 0; i < output.Length; i++)
            {
                int localX = i % BLOCK_SIZE;
                int localY = i / BLOCK_SIZE;

                if (output[i] < 0)
                {
                    Debug.Log($"Output in chunk {chunkPos} invalid at output index {i}");
                    return false;
                }

                // output[] is in index space; adjacency sets and borders are in key space.
                int currentTileKey = moduleIndexToKey[output[i]];

                // tile left
                int leftNeighborTileKey;
                if (localX == 0)
                {
                    leftNeighborTileKey = leftBorder[localY];
                }
                else
                {
                    int leftTileNeighborIndex = output[i - 1];
                    if (leftTileNeighborIndex < 0)
                    {
                        Debug.Log($"Output in chunk {chunkPos} invalid at output index {i - 1}");
                        return false;
                    }

                    leftNeighborTileKey = moduleIndexToKey[leftTileNeighborIndex];
                }

                if (!adjacency[leftNeighborTileKey][(int)Direction.Right].Contains(currentTileKey))
                {
                    Debug.Log($"{leftNeighborTileKey} does not contain {currentTileKey} in its right neighbors");
                    return false;
                }

                // tile right
                int rightNeighborTileKey;
                if (localX == BLOCK_SIZE - 1)
                {
                    rightNeighborTileKey = rightBorder[localY];
                }
                else
                {
                    int rightTileNeighborIndex = output[i + 1];
                    if (rightTileNeighborIndex < 0)
                    {
                        Debug.Log($"Output in chunk {chunkPos} invalid at output index {i + 1}");
                        return false;
                    }

                    rightNeighborTileKey = moduleIndexToKey[rightTileNeighborIndex];
                }

                if (!adjacency[rightNeighborTileKey][(int)Direction.Left].Contains(currentTileKey))
                {
                    Debug.Log($"{rightNeighborTileKey} does not contain {currentTileKey} in its left neighbors");
                    return false;
                }

                // tile up
                int upNeighborTileKey;
                if (localY == BLOCK_SIZE - 1)
                {
                    upNeighborTileKey = upBorder[localX];
                }
                else
                {
                    int upNeighborTileIndex = output[i + BLOCK_SIZE];
                    if (upNeighborTileIndex < 0)
                    {
                        Debug.Log($"Output in chunk {chunkPos} invalid at output index {i + BLOCK_SIZE}");
                        return false;
                    }

                    upNeighborTileKey = moduleIndexToKey[output[i + BLOCK_SIZE]];
                }

                if (!adjacency[upNeighborTileKey][(int)Direction.Down].Contains(currentTileKey))
                {
                    Debug.Log($"{upNeighborTileKey} does not contain {currentTileKey} in its down neighbors");
                    return false;
                }

                // tile down
                int downNeighborTileKey;
                if (localY == 0)
                {
                    downNeighborTileKey = downBorder[localX];
                }
                else
                {
                    int downNeighborTileIndex = output[i - BLOCK_SIZE];
                    if (downNeighborTileIndex < 0)
                    {
                        Debug.Log($"Output in chunk {chunkPos} invalid at output index {i - BLOCK_SIZE}");
                        return false;
                    }

                    downNeighborTileKey = moduleIndexToKey[output[i - BLOCK_SIZE]];
                }

                if (!adjacency[downNeighborTileKey][(int)Direction.Up].Contains(currentTileKey))
                {
                    Debug.Log($"{downNeighborTileKey} does not contain {currentTileKey} in its up neighbors");
                    return false;
                }
            }

            return true;
        }

        private int GetNeighborChunkTile(Vector2Int neighborBlockPos, int localX, int localY)
        {
            if (!_loadedChunks.TryGetValue(neighborBlockPos, out var chunk))
                return -1;

            return chunk[localX + localY * BLOCK_SIZE];
        }

        /// <summary>
        /// Get Borders of this block in index-space
        /// </summary>
        /// <param name="chunk"></param>
        /// <param name="blockLayer"></param>
        /// <param name="moduleKeyToIndex"></param>
        /// <returns></returns>
        // Build the lookup tables once per session — the chunk-offset/local-index pattern of each
        // border tile depends only on (layer, t) and is identical for every chunk.
        private void PrecomputeBorderOffsets()
        {
            _borderOffsetsTop = new BorderOffset[4, BLOCK_SIZE];
            _borderOffsetsBottom = new BorderOffset[4, BLOCK_SIZE];
            _borderOffsetsLeft = new BorderOffset[4, BLOCK_SIZE];
            _borderOffsetsRight = new BorderOffset[4, BLOCK_SIZE];

            for (int layer = 0; layer < 4; layer++)
            {
                Vector2Int blockStartPos = BlockOffsets[layer];
                for (int t = 0; t < BLOCK_SIZE; t++)
                {
                    _borderOffsetsTop[layer, t] = MakeOffset(blockStartPos + new Vector2Int(t, BLOCK_SIZE));
                    _borderOffsetsBottom[layer, t] = MakeOffset(blockStartPos + new Vector2Int(t, -1));
                    _borderOffsetsLeft[layer, t] = MakeOffset(blockStartPos + new Vector2Int(-1, t));
                    _borderOffsetsRight[layer, t] = MakeOffset(blockStartPos + new Vector2Int(BLOCK_SIZE, t));
                }
            }
        }

        private BorderOffset MakeOffset(Vector2Int relativePos)
        {
            (Vector2Int chunkOffset, Vector2Int localTile) = GetChunkAndLocalTilePositionFromTile(relativePos);
            return new BorderOffset
            {
                chunkOffset = chunkOffset,
                localIndex = localTile.y * CHUNK_SIZE + localTile.x,
            };
        }

        private WfcUtils.Borders GetBordersOfBlock(Vector2Int chunk, int blockLayer,
            Dictionary<int, int> moduleKeyToIndex)
        {
            // Reuse instance-level lists; WfcBlockState.Reset copies their contents into NativeArrays
            // synchronously, so the lists are safe to reuse across calls.
            _bordersUp.Clear();
            _bordersDown.Clear();
            _bordersLeft.Clear();
            _bordersRight.Clear();

            for (int t = 0; t < BLOCK_SIZE; t++)
            {
                BorderOffset top = _borderOffsetsTop[blockLayer, t];
                _bordersUp.Add(moduleKeyToIndex[_loadedChunks[chunk + top.chunkOffset][top.localIndex]]);

                BorderOffset bottom = _borderOffsetsBottom[blockLayer, t];
                _bordersDown.Add(moduleKeyToIndex[_loadedChunks[chunk + bottom.chunkOffset][bottom.localIndex]]);

                BorderOffset left = _borderOffsetsLeft[blockLayer, t];
                _bordersLeft.Add(moduleKeyToIndex[_loadedChunks[chunk + left.chunkOffset][left.localIndex]]);

                BorderOffset right = _borderOffsetsRight[blockLayer, t];
                _bordersRight.Add(moduleKeyToIndex[_loadedChunks[chunk + right.chunkOffset][right.localIndex]]);
            }

            return new WfcUtils.Borders
            {
                BorderDown = _bordersDown,
                BorderUp = _bordersUp,
                BorderLeft = _bordersLeft,
                BorderRight = _bordersRight,
            };
        }

        private void UpdateChunksFromBlock(Vector2Int chunkPos, int layer, NativeArray<int> wfcOutput,
            int[] moduleIndexToKey, int defaultTileKey)
        {
            int offsetX = BlockOffsets[layer].x;
            int offsetY = BlockOffsets[layer].y;

            for (int x = 0; x < BLOCK_SIZE; x++)
            {
                for (int y = 0; y < BLOCK_SIZE; y++)
                {
                    int localX = x + offsetX;
                    int localY = y + offsetY;

                    int neighborDX = 0, neighborDY = 0;

                    if (localX < 0)
                    {
                        neighborDX = -1;
                        localX += CHUNK_SIZE;
                    }
                    else if (localX >= CHUNK_SIZE)
                    {
                        neighborDX = 1;
                        localX -= CHUNK_SIZE;
                    }

                    if (localY < 0)
                    {
                        neighborDY = -1;
                        localY += CHUNK_SIZE;
                    }
                    else if (localY >= CHUNK_SIZE)
                    {
                        neighborDY = 1;
                        localY -= CHUNK_SIZE;
                    }

                    Vector2Int targetChunk = new Vector2Int(chunkPos.x + neighborDX, chunkPos.y + neighborDY);
                    int localPosition = localX + localY * CHUNK_SIZE;
                    int output = wfcOutput[x + y * BLOCK_SIZE];

                    _loadedChunks[targetChunk][localPosition] =
                        output >= 0 ? moduleIndexToKey[output] : defaultTileKey;
                }
            }
        }

        public (Vector2Int chunk, Vector2Int localTile) GetChunkAndLocalTilePositionFromTile(Vector2Int tilePos)
        {
            int chunkX = (int)Math.Floor((double)tilePos.x / CHUNK_SIZE);
            int chunkY = (int)Math.Floor((double)tilePos.y / CHUNK_SIZE);

            int localX = ((tilePos.x % CHUNK_SIZE) + CHUNK_SIZE) % CHUNK_SIZE;
            int localY = ((tilePos.y % CHUNK_SIZE) + CHUNK_SIZE) % CHUNK_SIZE;

            return (new Vector2Int(chunkX, chunkY), new Vector2Int(localX, localY));
        }

        private Task SaveChunkAsync(Vector2Int chunkCoord, int[] tiles)
        {
            // chunkCoord is a struct (captured by value); _chunkDirectory is an immutable string.
            // Everything — path construction, FileStream syscall, the write — runs on the thread pool.
            // Using a non-async delegate avoids the async-state-machine box, and writing the int[]
            // directly via MemoryMarshal removes the byte[] copy and the ArrayPool round-trip.
            string chunkDirectory = _chunkDirectory;
            return Task.Run(() =>
            {
                string path = Path.Combine(chunkDirectory, $"chunk_{chunkCoord.x}_{chunkCoord.y}.bin");
                using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write,
                    FileShare.None, bufferSize: 4096, useAsync: false);
                fs.Write(MemoryMarshal.AsBytes(tiles.AsSpan()));
            });
        }

        private async Task<int[]> LoadChunkAsync(Vector2Int chunkCoord)
        {
            string path = Path.Combine(_chunkDirectory, $"chunk_{chunkCoord.x}_{chunkCoord.y}.bin");
            // ConfigureAwait(false): continuations resume on the thread pool instead of being posted
            // back to Unity's main thread (where they'd contend for frame time).
            await using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true);

            byte[] buffer = new byte[CHUNK_SIZE * CHUNK_SIZE * sizeof(int)];
            await fs.ReadAsync(buffer).ConfigureAwait(false);

            int[] tiles = new int[CHUNK_SIZE * CHUNK_SIZE];
            Buffer.BlockCopy(buffer, 0, tiles, 0, buffer.Length);
            return tiles;
        }


        private Vector2Int GetPlayerChunk(Vector3 playerWorldPos)
        {
            var tilePosition = TargetTilemap.WorldToCell(playerWorldPos);
            Vector2Int playerChunk = new Vector2Int(Mathf.FloorToInt((float)tilePosition.x / CHUNK_SIZE),
                Mathf.FloorToInt((float)tilePosition.y / CHUNK_SIZE));
            return playerChunk;
        }

        /// <summary>
        /// Get the path of the file that stores the coordinates of all chunks that have ever been generated and their
        /// current stage of generation (1 through 4)
        /// </summary>
        /// <returns></returns>
        private string GetAllGeneratedBlocksPath()
        {
            string fileName = "chunk_layers.dat";
            return Path.Combine(_chunkDirectory, fileName);
        }

        public static Task SaveAllGeneratedBlocksDictAsync(Dictionary<Vector2Int, byte> chunkLayers, string path)
        {
            // Serialize on the main thread because the dictionary may be mutated as soon as we yield —
            // we need a snapshot before going async. Use a pooled byte buffer so the allocation itself
            // doesn't grow GC pressure as the world expands.
            int count = chunkLayers.Count;
            int byteCount = sizeof(int) + count * (sizeof(int) * 2 + sizeof(byte));
            byte[] buffer = ArrayPool<byte>.Shared.Rent(byteCount);

            int offset = 0;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), count);
            offset += sizeof(int);

            foreach (var pair in chunkLayers)
            {
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), pair.Key.x);
                offset += sizeof(int);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), pair.Key.y);
                offset += sizeof(int);
                buffer[offset++] = pair.Value;
            }

            // FileStream construction is a synchronous OS syscall; push it (and the write) to a thread
            // pool thread so the main thread never blocks on filesystem I/O.
            return Task.Run(() =>
            {
                try
                {
                    using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write,
                        FileShare.None, bufferSize: 4096, useAsync: false);
                    fs.Write(buffer, 0, byteCount);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            });
        }

        public static async Task<Dictionary<Vector2Int, byte>> LoadChunkLayersAsync(string path)
        {
            Dictionary<Vector2Int, byte> chunkLayers = new Dictionary<Vector2Int, byte>();

            if (!File.Exists(path))
                return chunkLayers;

            byte[] buffer = await File.ReadAllBytesAsync(path).ConfigureAwait(false);

            using MemoryStream ms = new MemoryStream(buffer);
            using BinaryReader reader = new BinaryReader(ms);

            int count = reader.ReadInt32();

            for (int i = 0; i < count; i++)
            {
                int x = reader.ReadInt32();
                int y = reader.ReadInt32();
                byte layersGenerated = reader.ReadByte();

                chunkLayers[new Vector2Int(x, y)] = layersGenerated;
            }

            return chunkLayers;
        }

        // used for testing
        void PrintHashSetArray(HashSet<Vector2Int>[] array)
        {
            if (array == null)
            {
                Debug.Log("Array is null");
                return;
            }

            for (int i = 0; i < array.Length; i++)
            {
                if (array[i] == null)
                {
                    Debug.Log($"[{i}]: null");
                    continue;
                }

                if (array[i].Count == 0)
                {
                    Debug.Log($"[{i}]: (empty)");
                    continue;
                }

                string entries = string.Join(", ", array[i]);
                Debug.Log($"[{i}]: {{ {entries} }}");
            }
        }
    }
}