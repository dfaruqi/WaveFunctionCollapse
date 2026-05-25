using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
        // ~ Constants ~

        // Size of loaded/saved chunks. Must be even. Suggestions: 16, 32, 48, 64.
        public const int CHUNK_SIZE = 24;

        // Size of generated blocks, which are later converted to chunks. Must be even and satisfy
        // CHUNK_SIZE / 2 < BLOCK_SIZE < CHUNK_SIZE. Suggestions: 12, 24, 36, 48.
        private const int BLOCK_SIZE = 18;

        // Region files bundle a REGION_SIZE x REGION_SIZE grid of chunks into one file on disk —
        // one file per 256 chunks instead of one-per-chunk. All chunk payloads are fixed-size, so
        // the file layout is just slot N at byte offset (N * CHUNK_PAYLOAD_BYTES); no header.
        private const int REGION_SIZE = 16;
        private const int CHUNK_PAYLOAD_BYTES = CHUNK_SIZE * CHUNK_SIZE * sizeof(int);

        // Tile draw is split into sub-blocks of this size to spread SetTilesBlock cost across
        // frames. Computed from CHUNK_SIZE so it always divides the chunk evenly. Targets ~16 per
        // side, capped at CHUNK_SIZE / 2 so we always get at least 4 sub-blocks per chunk.
        private static readonly int SUB_BLOCK_SIZE = ComputeSubBlockSize(CHUNK_SIZE);

        private static readonly Vector2Int[] NeighborOffsets =
        {
            new(-1,  1), // UpLeft
            new( 0,  1), // Up
            new( 1,  1), // UpRight
            new(-1,  0), // Left
            new( 1,  0), // Right
            new(-1, -1), // DownLeft
            new( 0, -1), // Down
            new( 1, -1), // DownRight
        };

        private static int ComputeSubBlockSize(int chunkSize)
        {
            int target = Math.Min(16, Math.Max(1, chunkSize / 2));
            for (int s = target; s >= 1; s--)
            {
                if (chunkSize % s == 0) return s;
            }
            return 1;
        }

        // ~ Nested types ~

        // Pre-computed border lookup entry: chunk-offset (relative to the current chunk) and flat
        // tile index that a border tile maps to.
        private struct BorderOffset
        {
            public Vector2Int chunkOffset;
            public int localIndex;
        }

        // Scratch entry for the parallelized validation-and-apply phase in GenerateBlocks.
        private struct ValidationEntry
        {
            public Vector2Int ChunkPos;
            public WfcBlockState BlockState;
            public WfcBiomeData BiomeData;
            public Task<bool> Task;
        }

        public delegate void ChunkDrawnHandler(Vector2Int chunkPos, IReadOnlyList<int> chunkData, Biome biome);
        public delegate void ChunkUndrawnHandler(Vector2Int chunkPos);

        // ~ Inspector fields ~

        public Transform Target;       // Target transform we generate the world around (typically the player).
        public Tilemap TargetTilemap;  // Tilemap we generate the world onto.
        public uint Seed;

        [SerializeField] int generateDistance = 1;
        [SerializeField] int drawDistance = 1;
        [SerializeField] bool clearOnStart = true;
        [SerializeField] Biome biome;

        [SerializeField, Tooltip(
            "Directory where chunk region files are saved and loaded. " +
            "Absolute paths are used as-is; relative paths are resolved under " +
            "Application.persistentDataPath. Leave empty for the default ('tile_chunks' " +
            "under persistentDataPath). Can also be assigned from code before this " +
            "component's Awake runs.")]
        private string chunkDirectory = "tile_chunks";

        // ~ Events ~

        public event ChunkDrawnHandler OnChunkDrawn;
        public event ChunkUndrawnHandler OnChunkUndrawn;

        // ~ Public properties ~

        /// <summary>
        /// Directory where chunk region files are read and written.
        /// Before Awake: returns the configured value (assignable by a caller or inspector).
        /// After Awake: returns the resolved absolute path; assignments are ignored with a warning,
        /// since chunks would already be loaded from the previous location.
        /// </summary>
        public string ChunkDirectory
        {
            get => _chunkDirectory ?? chunkDirectory;
            set
            {
                if (_chunkDirectory != null)
                {
                    Debug.LogWarning(
                        $"[{nameof(WfcWorldStreamer)}] ChunkDirectory assignment after Awake is ignored; " +
                        "set it before Awake or via the inspector.");
                    return;
                }
                chunkDirectory = value;
            }
        }

        // ~ State: initialization ~

        private bool _initialized = false;

        // Resolved absolute directory where chunks are saved. Populated in Awake from
        // `chunkDirectory` — until then it is null, which the ChunkDirectory setter uses as the
        // "not yet initialized" signal.
        private string _chunkDirectory;

        // Pre-computed origin (within a chunk) for each layer's block. Built in Awake.
        private readonly Vector2Int[] _blockOffsets = new Vector2Int[4];

        // Pre-computed border lookup tables. For each (layer, t) where t is the position along a
        // block edge, stores the chunk-offset and flat tile index of the bordering tile. Replaces
        // 4 * BLOCK_SIZE calls to GetChunkAndLocalTilePositionFromTile per GetBordersOfBlock call.
        private BorderOffset[,] _borderOffsetsTop;
        private BorderOffset[,] _borderOffsetsBottom;
        private BorderOffset[,] _borderOffsetsLeft;
        private BorderOffset[,] _borderOffsetsRight;

        // ~ State: chunks ~

        // Chunks currently loaded and their tile data.
        private readonly Dictionary<Vector2Int, int[]> _loadedChunks = new();

        // Chunks currently drawn on the tilemap.
        private readonly HashSet<Vector2Int> _drawnChunks = new();

        // Record of every block ever generated and the layer it has reached (0 = pregenerated,
        // 1-4 = layers 1-4). _allGeneratedBlocksDirty is set whenever this is mutated and cleared
        // after a successful save. Lets us skip re-serializing the (potentially large, growing)
        // file when nothing changed since the last save cycle.
        private Dictionary<Vector2Int, byte> _allGeneratedBlocks = new();
        private bool _allGeneratedBlocksDirty = false;

        // The last chunk the player was in, used to decide when to update chunks.
        private Vector2Int _lastPlayerChunk = new(int.MaxValue, int.MaxValue);

        // ~ State: per-template caches ~

        // WfcBiomeData converts a template's modules into the native lookup structures the WFC
        // job needs. Disposed in OnDestroy.
        private readonly Dictionary<WfcTemplate, WfcBiomeData> _biomeDataCache = new();

        // Per-template adjacency lookup for IsOutputValid: tileKey -> array indexed by
        // (int)Direction of the HashSet of allowed neighbor tile keys.
        private readonly Dictionary<WfcTemplate, Dictionary<int, HashSet<int>[]>> _adjacencyLookupCache = new();

        // ~ State: generation scratch ~

        private readonly List<JobHandle> _jobHandles = new();
        private readonly HashSet<Vector2Int>[] _blocksToGenerate = new HashSet<Vector2Int>[4];
        private readonly Stack<WfcBlockState> _blockStatePool = new();
        private readonly Dictionary<Vector2Int, WfcBlockState> _stateDict = new();
        private readonly List<ValidationEntry> _validationEntries = new();

        // Reusable border buffers for GetBordersOfBlock. WfcBlockState.Reset copies their contents
        // into NativeArrays synchronously, so the lists are safe to reuse across calls.
        private readonly List<int> _bordersUp = new(BLOCK_SIZE);
        private readonly List<int> _bordersDown = new(BLOCK_SIZE);
        private readonly List<int> _bordersLeft = new(BLOCK_SIZE);
        private readonly List<int> _bordersRight = new(BLOCK_SIZE);

        // ~ State: UpdateChunks scratch ~

        // Sets and buffers reused across cycles to avoid reallocation.
        private readonly HashSet<Vector2Int> _unloadedChunksInLoadDistance = new();
        private readonly HashSet<Vector2Int> _chunksPregenerated = new();
        private readonly HashSet<Vector2Int> _chunksUnloaded = new();
        private readonly HashSet<Vector2Int> _chunksInGenerateDistance = new();
        private readonly HashSet<Vector2Int> _chunksAffectedByGeneration = new();
        private readonly HashSet<Vector2Int> _chunksInDrawDistance = new();
        private readonly HashSet<Vector2Int> _chunksToDraw = new();
        private readonly HashSet<Vector2Int> _chunksToUndraw = new();
        private readonly HashSet<Vector2Int> _chunksToUnload = new();
        private readonly List<Task> _saveTasks = new();
        private readonly List<Task> _loadTasks = new();
        private readonly TileBase[] _subTileDrawBuffer = new TileBase[SUB_BLOCK_SIZE * SUB_BLOCK_SIZE];
        private readonly TileBase[] _subNullTileBuffer = new TileBase[SUB_BLOCK_SIZE * SUB_BLOCK_SIZE];

        // ~ State: I/O ~

        // Per-region lock so concurrent SaveChunkAsync/LoadChunkAsync calls hitting the same
        // region file serialize (two chunks in one region are saved in parallel during a normal
        // cycle).
        private readonly ConcurrentDictionary<Vector2Int, object> _regionLocks = new();

        // ~ Unity lifecycle ~

        private void Awake()
        {
            _chunkDirectory = ResolveChunkDirectory(chunkDirectory);

            // Create chunk directory if it does not exist.
            // TODO: add save slots instead of just this one.
            if (!Directory.Exists(_chunkDirectory))
            {
                Directory.CreateDirectory(_chunkDirectory);
            }

            // Pre-compute the in-chunk origin of each layer's block.
            int blockGap = CHUNK_SIZE - BLOCK_SIZE;
            _blockOffsets[0] = new Vector2Int(blockGap / 2,               -CHUNK_SIZE / 2 + blockGap / 2);
            _blockOffsets[1] = new Vector2Int(CHUNK_SIZE - BLOCK_SIZE / 2, -CHUNK_SIZE / 2 + blockGap / 2);
            _blockOffsets[2] = new Vector2Int(CHUNK_SIZE - BLOCK_SIZE / 2, blockGap / 2);
            _blockOffsets[3] = new Vector2Int(blockGap / 2,                blockGap / 2);

            // Pre-size _jobHandles and _validationEntries to the maximum number of blocks we will
            // schedule per layer (one job per chunk in generateDistance). Avoids List growth
            // reallocations.
            int chunksInGenerateDistance = (2 * generateDistance + 1) * (2 * generateDistance + 1);
            _jobHandles.Capacity = chunksInGenerateDistance;
            _validationEntries.Capacity = chunksInGenerateDistance;

            PrecomputeBorderOffsets();

            if (clearOnStart)
            {
                TargetTilemap.RefreshAllTiles();
                TargetTilemap.ClearAllTiles();
            }

            for (int i = 0; i < _blocksToGenerate.Length; i++)
            {
                _blocksToGenerate[i] = new HashSet<Vector2Int>();
            }

            _ = InitializeAsync();
        }

        private void OnEnable() => StartCoroutine(StreamWorld());

        private void OnDisable() => StopAllCoroutines();

        // Drain the block-state pool and dispose cached biome data (which owns native containers).
        private void OnDestroy()
        {
            while (_blockStatePool.TryPop(out WfcBlockState s))
                s.Dispose();

            foreach (WfcBiomeData data in _biomeDataCache.Values)
                data.Dispose();
            _biomeDataCache.Clear();
        }

        // ~ Initialization ~

        private async Task InitializeAsync()
        {
            await MigrateLegacyChunkFilesAsync();
            _allGeneratedBlocks = await LoadChunkLayersAsync(GetAllGeneratedBlocksPath());
            _initialized = true;
        }

        // Build the border lookup tables once per session — the chunk-offset/local-index pattern
        // of each border tile depends only on (layer, t) and is identical for every chunk.
        private void PrecomputeBorderOffsets()
        {
            _borderOffsetsTop    = new BorderOffset[4, BLOCK_SIZE];
            _borderOffsetsBottom = new BorderOffset[4, BLOCK_SIZE];
            _borderOffsetsLeft   = new BorderOffset[4, BLOCK_SIZE];
            _borderOffsetsRight  = new BorderOffset[4, BLOCK_SIZE];

            for (int layer = 0; layer < 4; layer++)
            {
                Vector2Int blockStartPos = _blockOffsets[layer];
                for (int t = 0; t < BLOCK_SIZE; t++)
                {
                    _borderOffsetsTop[layer, t]    = MakeOffset(blockStartPos + new Vector2Int(t, BLOCK_SIZE));
                    _borderOffsetsBottom[layer, t] = MakeOffset(blockStartPos + new Vector2Int(t, -1));
                    _borderOffsetsLeft[layer, t]   = MakeOffset(blockStartPos + new Vector2Int(-1, t));
                    _borderOffsetsRight[layer, t]  = MakeOffset(blockStartPos + new Vector2Int(BLOCK_SIZE, t));
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

        // ~ Main loop ~

        private IEnumerator StreamWorld()
        {
            while (!_initialized)
                yield return null;

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
            // - Load chunks -

            _unloadedChunksInLoadDistance.Clear();
            GetUnloadedChunksInLoadDistance(playerChunkPosition, _unloadedChunksInLoadDistance);

            _chunksPregenerated.Clear();
            _chunksUnloaded.Clear();

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

            // - Generate blocks -

            foreach (HashSet<Vector2Int> hashSet in _blocksToGenerate)
                hashSet?.Clear();

            _chunksInGenerateDistance.Clear();
            GetChunksInDistance(playerChunkPosition, generateDistance, _chunksInGenerateDistance);

            foreach (Vector2Int chunk in _chunksInGenerateDistance)
            {
                if (_allGeneratedBlocks[chunk] < 4) _blocksToGenerate[3].Add(chunk);
            }

            CascadeBlockDependencies(_blocksToGenerate);

            if (_blocksToGenerate.Length != 0)
            {
                Task generateBlocksTask = GenerateBlocks(_blocksToGenerate);
                while (!generateBlocksTask.IsCompleted)
                    yield return null;
                if (loadTask.IsFaulted)
                    throw loadTask.Exception;

                // Update the generated-layer record.
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

            // - Unload chunks -

            _chunksToUnload.Clear();
            GetChunksOutsideDistance(playerChunkPosition, generateDistance + 2, _loadedChunks.Keys,
                _chunksToUnload);

            foreach (Vector2Int chunkPos in _chunksToUnload)
            {
                if (_loadedChunks.Remove(chunkPos)) _chunksUnloaded.Add(chunkPos);
            }

            // - Update files -

            // Collect chunks affected by generation via block dependencies.
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

            foreach (Vector2Int chunk in _chunksPregenerated)
            {
                _chunksAffectedByGeneration.Add(chunk);
            }

            _saveTasks.Clear();
            foreach (Vector2Int chunkPos in _chunksAffectedByGeneration)
            {
                _saveTasks.Add(SaveChunkAsync(chunkPos, _loadedChunks[chunkPos]));
            }
            // Only re-save the layer-progression file when something actually changed. This
            // dictionary grows unboundedly with exploration, so the serialize+write cost grows
            // over the session; skipping no-op saves keeps the cost off most cycles entirely.
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

            // - Update tilemap -

            // Walk only chunks within draw distance — that's the visibility filter for generation
            // effects too. A chunk is queued for (re)draw if it isn't drawn yet OR it's already
            // drawn but its tiles changed this cycle (generation writes into neighbor chunks via
            // block dependencies, so "affected" can include chunks where only a small region
            // actually changed).
            _chunksToDraw.Clear();
            _chunksInDrawDistance.Clear();
            GetChunksInDistance(playerChunkPosition, drawDistance, _chunksInDrawDistance);
            foreach (Vector2Int c in _chunksInDrawDistance)
            {
                if (!_drawnChunks.Contains(c) || _chunksAffectedByGeneration.Contains(c))
                    _chunksToDraw.Add(c);
            }

            yield return StartCoroutine(DrawChunks(_chunksToDraw));

            // Un-draw chunks that are drawn and outside draw distance.
            _chunksToUndraw.Clear();
            GetChunksOutsideDistance(playerChunkPosition, drawDistance + 1, _drawnChunks,
                _chunksToUndraw);
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

        // ~ Generation ~

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

                    // Rent from pool — these are expensive to reallocate.
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

                // Kick off all batched jobs immediately rather than waiting for an implicit
                // flush — this maximizes job-system parallelism while we're still doing
                // main-thread setup.
                JobHandle.ScheduleBatchedJobs();

                while (!AllJobsCompleted())
                    await Task.Yield();

                foreach (JobHandle jobHandle in _jobHandles)
                    jobHandle.Complete();

                _jobHandles.Clear();

                // Launch all chunk validations up front. IsOutputValid does its heavy lifting on
                // the thread pool, so launching them all before awaiting any lets them run in
                // parallel.
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

                // Drain results in order. Any task that finished while we were awaiting an
                // earlier one returns immediately.
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

                    ReturnBlockState(entry.BlockState);
                }

                _stateDict.Clear();
            }
        }

        private void CascadeBlockDependencies(HashSet<Vector2Int>[] blocksToGenerate)
        {
            // Layer 4 block at (x, y) depends on layer 3 blocks at (x, y) and (x - 1, y).
            foreach (Vector2Int block in blocksToGenerate[3])
            {
                for (int x = 0; x >= -1; x--)
                {
                    Vector2Int dependent = new Vector2Int(block.x + x, block.y);
                    if (_allGeneratedBlocks[dependent] < 3 && !blocksToGenerate[2].Contains(dependent))
                        blocksToGenerate[2].Add(dependent);
                }
            }

            // Layer 3 block at (x, y) depends on layer 2 blocks at (x, y) and (x, y + 1).
            foreach (Vector2Int block in blocksToGenerate[2])
            {
                for (int y = 0; y <= 1; y++)
                {
                    Vector2Int dependent = new Vector2Int(block.x, block.y + y);
                    if (_allGeneratedBlocks[dependent] < 2 && !blocksToGenerate[1].Contains(dependent))
                        blocksToGenerate[1].Add(dependent);
                }
            }

            // Layer 2 block at (x, y) depends on layer 1 blocks at (x, y) and (x + 1, y).
            foreach (Vector2Int block in blocksToGenerate[1])
            {
                for (int x = 0; x <= 1; x++)
                {
                    Vector2Int dependent = new Vector2Int(block.x + x, block.y);
                    if (_allGeneratedBlocks[dependent] < 1)
                        blocksToGenerate[0].Add(dependent);
                }
            }
        }

        /// <summary>
        /// Returns the borders of this block in index-space.
        /// </summary>
        private WfcUtils.Borders GetBordersOfBlock(Vector2Int chunk, int blockLayer,
            Dictionary<int, int> moduleKeyToIndex)
        {
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
            int offsetX = _blockOffsets[layer].x;
            int offsetY = _blockOffsets[layer].y;

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

        private WfcBiomeData GetOrBuildBiomeData(WfcTemplate template)
        {
            if (!_biomeDataCache.TryGetValue(template, out WfcBiomeData data))
            {
                data = new WfcBiomeData(template);
                _biomeDataCache[template] = data;
            }
            return data;
        }

        private WfcBlockState RentBlockState(
            Vector2Int size, int moduleCount, WfcTemplate template,
            WfcUtils.Borders borders)
        {
            if (_blockStatePool.TryPop(out WfcBlockState pooled))
            {
                pooled.Reset(size, moduleCount, template, borders);
                return pooled;
            }

            return new WfcBlockState(size, moduleCount, template, borders);
        }

        private void ReturnBlockState(WfcBlockState state) => _blockStatePool.Push(state);

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

        private bool AllJobsCompleted()
        {
            foreach (JobHandle h in _jobHandles)
                if (!h.IsCompleted)
                    return false;
            return true;
        }

        // ~ Validation ~

        private Task<bool> IsOutputValid(NativeArray<int> output, Vector2Int chunkPos, int layer,
            int[] moduleIndexToKey)
        {
            Dictionary<int, HashSet<int>[]> adjacency =
                GetOrBuildAdjacencyLookup(biome.GetTemplate(chunkPos));

            Vector2Int blockStartTilePosGlobal = chunkPos * CHUNK_SIZE + _blockOffsets[layer];

            // Snapshot main-thread-only state so the validation loop can run on a thread-pool
            // thread: copy the NativeArray to managed memory and pre-fetch the four block-edge
            // neighbor strips from _loadedChunks. moduleIndexToKey and the adjacency lookup are
            // not mutated after construction, so they're safe to read concurrently.
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

                // Left neighbor.
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

                // Right neighbor.
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

                // Up neighbor.
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

                // Down neighbor.
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

        private Dictionary<int, HashSet<int>[]> GetOrBuildAdjacencyLookup(WfcTemplate template)
        {
            if (_adjacencyLookupCache.TryGetValue(template, out var cached))
                return cached;

            SerializedDictionary<int, WfcTileRules.AllowedNeighbors> modules = template.TileRules.Modules;
            var lookup = new Dictionary<int, HashSet<int>[]>(modules.Count);
            foreach (var kvp in modules)
            {
                var perDirection = new HashSet<int>[4];
                perDirection[(int)Direction.Up]    = new HashSet<int>(kvp.Value.Neighbors[Direction.Up]);
                perDirection[(int)Direction.Down]  = new HashSet<int>(kvp.Value.Neighbors[Direction.Down]);
                perDirection[(int)Direction.Left]  = new HashSet<int>(kvp.Value.Neighbors[Direction.Left]);
                perDirection[(int)Direction.Right] = new HashSet<int>(kvp.Value.Neighbors[Direction.Right]);
                lookup[kvp.Key] = perDirection;
            }

            _adjacencyLookupCache[template] = lookup;
            return lookup;
        }

        // ~ Drawing ~

        /// <summary>
        /// Draws the given chunks onto the tilemap. Assumes every chunk passed in is loaded.
        /// </summary>
        private IEnumerator DrawChunks(HashSet<Vector2Int> chunks)
        {
            foreach (Vector2Int chunkPos in chunks)
            {
                // Hoist per-chunk lookups out of the inner loop — GetTemplate does a Perlin
                // sample and would otherwise run once per tile.
                int[] chunkData = _loadedChunks[chunkPos];
                TileDatabase tileDatabase = biome.GetTemplate(chunkPos).TileDatabase;
                Vector3Int chunkOrigin = (chunkPos * CHUNK_SIZE).ToVector3Int();
                Vector3Int subSize = new Vector3Int(SUB_BLOCK_SIZE, SUB_BLOCK_SIZE, 1);

                // Stream the chunk to the tilemap one sub-block at a time, yielding between
                // each SetTilesBlock call so a single large chunk doesn't spike a frame.
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

        // ~ Distance and coordinate helpers ~

        private Vector2Int GetPlayerChunk(Vector3 playerWorldPos)
        {
            var tilePosition = TargetTilemap.WorldToCell(playerWorldPos);
            return new Vector2Int(
                Mathf.FloorToInt((float)tilePosition.x / CHUNK_SIZE),
                Mathf.FloorToInt((float)tilePosition.y / CHUNK_SIZE));
        }

        private void GetUnloadedChunksInLoadDistance(Vector2Int playerChunkPosition,
            HashSet<Vector2Int> unloadedChunksInLoadDistance)
        {
            unloadedChunksInLoadDistance.Clear();

            int chunkCeil = generateDistance + 1;
            for (int y = -chunkCeil; y <= chunkCeil; y++)
            {
                for (int x = -chunkCeil; x <= chunkCeil; x++)
                {
                    Vector2Int chunkPos = playerChunkPosition + new Vector2Int(x, y);
                    if (!_loadedChunks.ContainsKey(chunkPos)) unloadedChunksInLoadDistance.Add(chunkPos);
                }
            }
        }

        private void GetChunksInDistance(Vector2Int position, int distance,
            HashSet<Vector2Int> chunksInDistance)
        {
            chunksInDistance.Clear();
            for (int y = -distance; y <= distance; y++)
            {
                for (int x = -distance; x <= distance; x++)
                {
                    chunksInDistance.Add(position + new Vector2Int(x, y));
                }
            }
        }

        private void GetChunksOutsideDistance(Vector2Int position, int distance,
            ICollection<Vector2Int> chunks, HashSet<Vector2Int> chunksOutsideDistance)
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

        public (Vector2Int chunk, Vector2Int localTile) GetChunkAndLocalTilePositionFromTile(Vector2Int tilePos)
        {
            int chunkX = (int)Math.Floor((double)tilePos.x / CHUNK_SIZE);
            int chunkY = (int)Math.Floor((double)tilePos.y / CHUNK_SIZE);

            int localX = ((tilePos.x % CHUNK_SIZE) + CHUNK_SIZE) % CHUNK_SIZE;
            int localY = ((tilePos.y % CHUNK_SIZE) + CHUNK_SIZE) % CHUNK_SIZE;

            return (new Vector2Int(chunkX, chunkY), new Vector2Int(localX, localY));
        }

        // ~ File I/O ~

        private static string ResolveChunkDirectory(string configured)
        {
            if (string.IsNullOrWhiteSpace(configured))
                return Path.Combine(Application.persistentDataPath, "tile_chunks");
            if (Path.IsPathRooted(configured))
                return configured;
            return Path.Combine(Application.persistentDataPath, configured);
        }

        private static (Vector2Int region, int slot) GetRegionAndSlot(Vector2Int chunkCoord)
        {
            int regionX = (int)Math.Floor((double)chunkCoord.x / REGION_SIZE);
            int regionY = (int)Math.Floor((double)chunkCoord.y / REGION_SIZE);
            int localX = ((chunkCoord.x % REGION_SIZE) + REGION_SIZE) % REGION_SIZE;
            int localY = ((chunkCoord.y % REGION_SIZE) + REGION_SIZE) % REGION_SIZE;
            int slot = localY * REGION_SIZE + localX;
            return (new Vector2Int(regionX, regionY), slot);
        }

        private string GetRegionPath(Vector2Int region) =>
            Path.Combine(_chunkDirectory, $"region_{region.x}_{region.y}.bin");

        private object GetRegionLock(Vector2Int region) =>
            _regionLocks.GetOrAdd(region, _ => new object());

        /// <summary>
        /// Path of the file that stores the coordinates of every chunk that has ever been
        /// generated and its current stage of generation (1 through 4).
        /// </summary>
        private string GetAllGeneratedBlocksPath() =>
            Path.Combine(_chunkDirectory, "chunk_layers.dat");

        private Task SaveChunkAsync(Vector2Int chunkCoord, int[] tiles)
        {
            (Vector2Int region, int slot) = GetRegionAndSlot(chunkCoord);
            string path = GetRegionPath(region);
            object regionLock = GetRegionLock(region);

            // Snapshot the tile data on the main thread — _loadedChunks arrays are mutated by
            // the next generation cycle, and Task.Run won't observe a consistent buffer if we
            // capture by ref.
            byte[] payload = ArrayPool<byte>.Shared.Rent(CHUNK_PAYLOAD_BYTES);
            MemoryMarshal.AsBytes(tiles.AsSpan()).CopyTo(payload);

            return Task.Run(() =>
            {
                try
                {
                    lock (regionLock)
                    {
                        using FileStream fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write,
                            FileShare.None, bufferSize: 4096, useAsync: false);
                        fs.Seek((long)slot * CHUNK_PAYLOAD_BYTES, SeekOrigin.Begin);
                        fs.Write(payload, 0, CHUNK_PAYLOAD_BYTES);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(payload);
                }
            });
        }

        private Task<int[]> LoadChunkAsync(Vector2Int chunkCoord)
        {
            (Vector2Int region, int slot) = GetRegionAndSlot(chunkCoord);
            string path = GetRegionPath(region);
            object regionLock = GetRegionLock(region);

            // Region I/O can't use FileStream's async path because we hold a lock across the
            // read — awaiting inside a lock is a deadlock hazard. Push the whole synchronous
            // read to the thread pool instead so the main thread never blocks on filesystem I/O.
            return Task.Run(() =>
            {
                lock (regionLock)
                {
                    using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.Read, bufferSize: 4096, useAsync: false);
                    fs.Seek((long)slot * CHUNK_PAYLOAD_BYTES, SeekOrigin.Begin);

                    byte[] buffer = new byte[CHUNK_PAYLOAD_BYTES];
                    int totalRead = 0;
                    while (totalRead < CHUNK_PAYLOAD_BYTES)
                    {
                        int n = fs.Read(buffer, totalRead, CHUNK_PAYLOAD_BYTES - totalRead);
                        if (n == 0) break;
                        totalRead += n;
                    }

                    int[] tiles = new int[CHUNK_SIZE * CHUNK_SIZE];
                    Buffer.BlockCopy(buffer, 0, tiles, 0, CHUNK_PAYLOAD_BYTES);
                    return tiles;
                }
            });
        }

        public static Task SaveAllGeneratedBlocksDictAsync(Dictionary<Vector2Int, byte> chunkLayers, string path)
        {
            // Serialize on the main thread because the dictionary may be mutated as soon as we
            // yield — we need a snapshot before going async. Use a pooled byte buffer so the
            // allocation itself doesn't grow GC pressure as the world expands.
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

            // FileStream construction is a synchronous OS syscall; push it (and the write) to
            // a thread-pool thread so the main thread never blocks on filesystem I/O.
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

        // One-time migration: the previous on-disk format wrote one file per chunk. On first
        // launch after the format change, pull every legacy `chunk_X_Y.bin` into its
        // corresponding region file and delete the original. Idempotent — if there are no
        // legacy files, this is a cheap directory listing and returns immediately.
        private async Task MigrateLegacyChunkFilesAsync()
        {
            string directory = _chunkDirectory;
            await Task.Run(() =>
            {
                string[] oldFiles = Directory.GetFiles(directory, "chunk_*.bin");
                if (oldFiles.Length == 0) return;

                Debug.Log($"[{nameof(WfcWorldStreamer)}] Migrating {oldFiles.Length} legacy chunk file(s) into region files");

                byte[] buffer = new byte[CHUNK_PAYLOAD_BYTES];
                int migrated = 0;
                foreach (string oldPath in oldFiles)
                {
                    string name = Path.GetFileNameWithoutExtension(oldPath);
                    string[] parts = name.Split('_');
                    if (parts.Length != 3 || parts[0] != "chunk") continue;
                    if (!int.TryParse(parts[1], out int x)) continue;
                    if (!int.TryParse(parts[2], out int y)) continue;

                    try
                    {
                        using (FileStream src = new FileStream(oldPath, FileMode.Open, FileAccess.Read,
                                   FileShare.Read, bufferSize: 4096, useAsync: false))
                        {
                            if (src.Length != CHUNK_PAYLOAD_BYTES)
                            {
                                Debug.LogWarning($"[{nameof(WfcWorldStreamer)}] Legacy chunk {oldPath} has size {src.Length}, expected {CHUNK_PAYLOAD_BYTES}; skipping");
                                continue;
                            }
                            int read = 0;
                            while (read < CHUNK_PAYLOAD_BYTES)
                            {
                                int n = src.Read(buffer, read, CHUNK_PAYLOAD_BYTES - read);
                                if (n == 0) break;
                                read += n;
                            }
                        }

                        (Vector2Int region, int slot) = GetRegionAndSlot(new Vector2Int(x, y));
                        string regionPath = GetRegionPath(region);
                        using (FileStream dst = new FileStream(regionPath, FileMode.OpenOrCreate,
                                   FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: false))
                        {
                            dst.Seek((long)slot * CHUNK_PAYLOAD_BYTES, SeekOrigin.Begin);
                            dst.Write(buffer, 0, CHUNK_PAYLOAD_BYTES);
                        }

                        File.Delete(oldPath);
                        migrated++;
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[{nameof(WfcWorldStreamer)}] Failed to migrate {oldPath}: {e.Message}");
                    }
                }

                Debug.Log($"[{nameof(WfcWorldStreamer)}] Migrated {migrated}/{oldFiles.Length} legacy chunk file(s)");
            }).ConfigureAwait(false);
        }
    }
}
