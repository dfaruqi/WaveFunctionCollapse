using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        [SerializeField] int drawDistance = 1;
        [SerializeField] bool clearOnStart = true;
        [SerializeField] Biome biome;

        // ~ Constants ~

        // directory where chunks are saved
        private string _chunkDirectory;

        // size of loaded/saved chunks, must be even
        // suggestions: 16,32,48,64
        public const int CHUNK_SIZE = 48;

        // size of generated blocks, which are later converted to chunks. Must satisfy the following:
        // BLOCKSIZE is even and BLOCK_SIZE < CHUNK_SIZE and BLOCK_SIZE > CHUNK_SIZE / 2
        // suggestions: 12,24,36,48
        private const int BLOCK_SIZE = 36;

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

        // the last chunk the player was in, used to determine when to update chunks
        private Vector2Int _lastPlayerChunk = new(int.MaxValue, int.MaxValue);
        
        // cached containers used in generation to avoid reallocation. Cleared every chunk update.
        private HashSet<Vector2Int> _unloadedChunksInLoadDistance = new();
        private readonly HashSet<Vector2Int> _chunksPregenerated = new();
        private readonly HashSet<Vector2Int> _chunksUnloaded = new();
        private HashSet<Vector2Int> _chunksInDrawDistance = new();
        private readonly HashSet<Vector2Int> _chunksAffectedByGeneration = new();
        private readonly HashSet<Vector2Int> _chunksToDraw = new();
        private HashSet<Vector2Int> _chunksToUndraw = new();
        private HashSet<Vector2Int> _chunksToUnload = new();
        private HashSet<Vector2Int>[] _blocksToGenerate = new HashSet<Vector2Int>[4];
        private readonly List<Task> _saveTasks = new();
        private readonly TileBase[] _tileDrawBuffer = new TileBase[CHUNK_SIZE * CHUNK_SIZE];
        private readonly TileBase[] _nullTileBuffer = new TileBase[CHUNK_SIZE * CHUNK_SIZE];

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
                return;
            }

            // initialize offsets for blocks (just some reference data that we won't have to recompute)
            int blockGap = CHUNK_SIZE - BLOCK_SIZE;
            BlockOffsets[0] = new Vector2Int(blockGap / 2, -CHUNK_SIZE / 2 + blockGap / 2);
            BlockOffsets[1] = new Vector2Int(CHUNK_SIZE - BLOCK_SIZE / 2, -CHUNK_SIZE / 2 + blockGap / 2);
            BlockOffsets[2] = new Vector2Int(CHUNK_SIZE - BLOCK_SIZE / 2, blockGap / 2);
            BlockOffsets[3] = new Vector2Int(blockGap / 2, blockGap / 2);

            if (clearOnStart)
            {
                TargetTilemap.RefreshAllTiles();
                TargetTilemap.ClearAllTiles();
            }
            
            // initialize blocks to generate array
            for(int i = 0; i < _blocksToGenerate.Length; i++)
            {
                _blocksToGenerate[i] = new HashSet<Vector2Int>();
            }

            // load all generated chunk coords
            _ = InitializeAsync();
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
            Task loadTask = Task.WhenAll(
                _unloadedChunksInLoadDistance.Select(coord => LoadOrPregenerateChunkAsync(coord, _chunksPregenerated))
            );

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

            _chunksInDrawDistance.Clear();
            GetChunksInDistance(playerChunkPosition, drawDistance, ref _chunksInDrawDistance);

            foreach (Vector2Int chunk in _chunksInDrawDistance)
            {
                if (_allGeneratedBlocks[chunk] < 4) _blocksToGenerate[3].Add(chunk);
            }

            CascadeBlockDependencies(ref _blocksToGenerate);

            yield return StartCoroutine(GenerateBlocks(_blocksToGenerate));

            // update all generated blocks dictionary
            for (byte i = 0; i < 4; i++)
            {
                foreach (Vector2Int block in _blocksToGenerate[i])
                {
                    byte oldBlockGeneratedTo = _allGeneratedBlocks[block];
                    byte newBlockGeneratedTo = (byte)(i + 1);
                    if (oldBlockGeneratedTo < newBlockGeneratedTo) _allGeneratedBlocks[block] = newBlockGeneratedTo;
                }
            }

            yield return null;

            // - Unload Chunks -

            // unload chunks
            _chunksToUnload.Clear();
            GetChunksOutsideDistance(playerChunkPosition, drawDistance + 3, _loadedChunks.Keys,
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
            _saveTasks.AddRange(
                _chunksAffectedByGeneration.Select(chunkPos => SaveChunkAsync(chunkPos, _loadedChunks[chunkPos])));
            _saveTasks.Add(SaveAllGeneratedBlocksDictAsync(_allGeneratedBlocks, GetAllGeneratedBlocksPath()));

            Task saveAll = Task.WhenAll(_saveTasks);
            bool allChunksSaved;
            do {
                allChunksSaved = true;
                for (int i = 0; i < _jobHandles.Count; i++)
                {
                    if (!_jobHandles[i].IsCompleted) { allChunksSaved = false; break; }
                }
                if (!allChunksSaved) yield return null;
            } while (!allChunksSaved);

            if (saveAll.IsFaulted)
                throw saveAll.Exception;

            // - Update Tilemap - 

            // get all chunks within draw distance that are not drawn or should be redrawn because generation affected
            // them (at the edges)
            _chunksToDraw.Clear();
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
                Vector3Int tilePositionOfChunk = (chunkPos * CHUNK_SIZE).ToVector3Int();
                BoundsInt bounds = new BoundsInt(tilePositionOfChunk, new Vector3Int(CHUNK_SIZE, CHUNK_SIZE, 1));

                TargetTilemap.SetTilesBlock(bounds, _nullTileBuffer);

                _drawnChunks.Remove(chunkPos);

                OnChunkUndrawn?.Invoke(chunkPos);

                yield return null;
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
                for (int i = 0; i < CHUNK_SIZE * CHUNK_SIZE; i++)
                {
                    int tileKey = _loadedChunks[chunkPos][i];

                    if (tileKey < 0)
                    {
                        _tileDrawBuffer[i] = null;
                        continue;
                    }

                    _tileDrawBuffer[i] = biome.GetTemplate(chunkPos).TileDatabase[tileKey];
                }

                Vector3Int tilePositionOfChunk = (chunkPos * CHUNK_SIZE).ToVector3Int();
                BoundsInt bounds = new BoundsInt(tilePositionOfChunk, new Vector3Int(CHUNK_SIZE, CHUNK_SIZE, 1));

                TargetTilemap.SetTilesBlock(bounds, _tileDrawBuffer);

                _drawnChunks.Add(chunkPos);

                OnChunkDrawn?.Invoke(chunkPos, _loadedChunks[chunkPos], biome);

                yield return null;
            }
        }

        private IEnumerator GenerateBlocks(HashSet<Vector2Int>[] blocksToGenerate)
        {
            // create a dictionary to track all the wave function collapse runs
            Dictionary<Vector2Int, WfcBlockState> stateDict = new();

            // Create the global data for each sub-biome (this data will be accessed in parallel by the wfc runs)
            Dictionary<Vector2Int, WfcBiomeData> biomeGlobalsDict = new Dictionary<Vector2Int, WfcBiomeData>();
            for (int layer = 0; layer < blocksToGenerate.Length; layer++)
            {
                foreach (Vector2Int chunk in blocksToGenerate[layer])
                {
                    if (!biomeGlobalsDict.ContainsKey(chunk))
                        biomeGlobalsDict[chunk] = new WfcBiomeData(biome.GetTemplate(chunk));
                }
            }

            // generate the chunks in 4 overlapping layers using the layered-block-evaluation approach
            for (byte layer = 0; layer < 4; layer++)
            {
                foreach (Vector2Int chunk in blocksToGenerate[layer])
                {
                    WfcBiomeData wfcBiomeData = biomeGlobalsDict[chunk];
                    WfcTemplate template = wfcBiomeData.Template;
                    // get the template and 

                    // Create rng
                    Random rng = new Random(TileUtils.HashWorldBlock(Seed, chunk, layer));

                    // get the border information for this block from loaded chunks
                    WfcUtils.Borders borders = GetBordersOfBlock(chunk, layer, wfcBiomeData.moduleKeyToIndex);
                    WfcBlockState wfcBlockState = new WfcBlockState(new Vector2Int(BLOCK_SIZE, BLOCK_SIZE),
                        template.TileRules.Modules.Count, template, rng, borders);

                    // add to state dict to keep track of this run of wfc
                    stateDict.Add(chunk, wfcBlockState);

                    // === Create and schedule the job ===
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

                    // generate the block
                    _jobHandles.Add(wfc.Schedule());
                }

                yield return new WaitUntil(() => { return _jobHandles.All(jobHandle => jobHandle.IsCompleted); });

                foreach (JobHandle jobHandle in _jobHandles)
                {
                    jobHandle.Complete();
                }

                _jobHandles.Clear();

                // Update the affected loaded chunks--the changes will be needed for future passes
                foreach (KeyValuePair<Vector2Int, WfcBlockState> kvp in stateDict)
                {
                    Vector2Int chunkPosition = kvp.Key;
                    WfcBlockState wfcBlockState = kvp.Value;

                    WfcBiomeData wfcBiomeData = biomeGlobalsDict[chunkPosition];

                    // If has error in output, fall back to previous layer, otherwise update the loaded chunks

                    bool valid = IsOutputValid(wfcBlockState.Output, chunkPosition, layer,
                        wfcBiomeData.moduleIndexToKey);
                    if (!valid)
                        Debug.LogWarning($"[{nameof(WfcWorldStreamer)}] Error in chunk {chunkPosition} on layer {layer}");
                    else
                    {
                        UpdateChunksFromBlock(chunkPosition, layer, wfcBlockState.Output, wfcBiomeData.moduleIndexToKey,
                            wfcBiomeData.Template.DefaultTileKey);
                    }

                    // clean up state
                    wfcBlockState.Dispose();
                }

                stateDict.Clear();
            }

            // clean up shared biome data
            foreach (var kvp in biomeGlobalsDict)
            {
                kvp.Value.Dispose();
            }
        }

        private void GetUnloadedChunksInLoadDistance(Vector2Int playerChunkPosition,
            ref HashSet<Vector2Int> chunksToLoadOrGenerate)
        {
            chunksToLoadOrGenerate.Clear();

            int chunkCeilX = drawDistance + 3;
            int chunkCeilY = drawDistance + 2;

            for (int y = -chunkCeilY; y <= chunkCeilY; y++)
            {
                for (int x = -chunkCeilX; x <= chunkCeilX; x++)
                {
                    Vector2Int chunkPos = playerChunkPosition + new Vector2Int(x, y);
                    if (!_loadedChunks.ContainsKey(chunkPos)) chunksToLoadOrGenerate.Add(chunkPos);
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

        private void GetChunksInDistance(Vector2Int position, int distance, ref HashSet<Vector2Int> chunksInDistance)
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

        private bool IsOutputValid(NativeArray<int> output, Vector2Int chunkPos, int layer,
            Dictionary<int, int> moduleIndexToKey)
        {
            SerializedDictionary<int, WfcTileRules.AllowedNeighbors> modules =
                biome.GetTemplate(chunkPos).TileRules.Modules;

            Vector2Int chunkStartTilePosGlobal = chunkPos * CHUNK_SIZE;
            Vector2Int blockStartTilePosGlobal = chunkStartTilePosGlobal + BlockOffsets[layer];

            for (int i = 0; i < output.Length; i++)
            {
                int localX = i % BLOCK_SIZE;
                int localY = i / BLOCK_SIZE;

                // tile left 
                int leftNeighborTileKey;
                if (localX == 0)
                {
                    (Vector2Int chunk, Vector2Int localTilePosition) =
                        GetChunkAndLocalTilePositionFromTile(blockStartTilePosGlobal + Vector2Int.up * localY +
                                                             Vector2Int.left);

                    leftNeighborTileKey =
                        _loadedChunks[chunk][TileUtils.Flatten(localTilePosition, CHUNK_SIZE)];
                }
                else
                {
                    int leftTileNeighborIndex = output[i - 1];
                    if (leftTileNeighborIndex < 0) return false;
                    leftNeighborTileKey = moduleIndexToKey[leftTileNeighborIndex];
                }

                if (!modules[leftNeighborTileKey].Neighbors[Direction.Right].Contains(output[i]))
                    return false;

                // tile right
                int rightNeighborTileKey;
                if (localX == BLOCK_SIZE - 1)
                {
                    (Vector2Int chunk, Vector2Int localTilePosition) =
                        GetChunkAndLocalTilePositionFromTile(blockStartTilePosGlobal + Vector2Int.up * localY +
                                                             Vector2Int.right * BLOCK_SIZE);
                    rightNeighborTileKey =
                        _loadedChunks[chunk][TileUtils.Flatten(localTilePosition, CHUNK_SIZE)];
                }
                else
                {
                    int rightTileNeighborIndex = output[i + 1];
                    if (rightTileNeighborIndex < 0) return false;
                    rightNeighborTileKey = moduleIndexToKey[rightTileNeighborIndex];
                }

                if (!modules[rightNeighborTileKey].Neighbors[Direction.Left].Contains(output[i]))
                    return false;

                // tile up
                int upNeighborTileKey;
                if (localY == BLOCK_SIZE - 1)
                {
                    (Vector2Int chunk, Vector2Int localTilePosition) =
                        GetChunkAndLocalTilePositionFromTile(blockStartTilePosGlobal + Vector2Int.up * BLOCK_SIZE +
                                                             Vector2Int.right * localX);
                    upNeighborTileKey =
                        _loadedChunks[chunk][TileUtils.Flatten(localTilePosition, CHUNK_SIZE)];
                }
                else
                {
                    int upNeighborTileIndex = output[i + BLOCK_SIZE];
                    if (upNeighborTileIndex < 0) return false;
                    upNeighborTileKey = moduleIndexToKey[output[i + BLOCK_SIZE]];
                }

                if (!modules[upNeighborTileKey].Neighbors[Direction.Down].Contains(output[i]))
                    return false;

                // tile down
                int downNeighborTileKey;
                if (localY == 0)
                {
                    (Vector2Int chunk, Vector2Int localTilePosition) =
                        GetChunkAndLocalTilePositionFromTile(blockStartTilePosGlobal + Vector2Int.down +
                                                             Vector2Int.right * localX);
                    downNeighborTileKey =
                        _loadedChunks[chunk][TileUtils.Flatten(localTilePosition, CHUNK_SIZE)];
                }
                else
                {
                    int downNeighborTileIndex = output[i - BLOCK_SIZE];
                    if (downNeighborTileIndex < 0) return false;
                    downNeighborTileKey = moduleIndexToKey[output[i - BLOCK_SIZE]];
                }

                if (!modules[downNeighborTileKey].Neighbors[Direction.Up].Contains(output[i]))
                    return false;
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
        private WfcUtils.Borders GetBordersOfBlock(Vector2Int chunk, int blockLayer,
            Dictionary<int, int> moduleKeyToIndex)
        {
            WfcUtils.Borders borders = new WfcUtils.Borders()
            {
                BorderDown = new List<int>(),
                BorderUp = new List<int>(),
                BorderLeft = new List<int>(),
                BorderRight = new List<int>(),
            };

            Vector2Int blockStartPos = BlockOffsets[blockLayer];

            // top border
            for (int t = 0; t < BLOCK_SIZE; t++)
            {
                Vector2Int borderStartPos = blockStartPos + new Vector2Int(t, BLOCK_SIZE);
                var chunkAndLocalTile = GetChunkAndLocalTilePositionFromTile(borderStartPos);
                Vector2Int chunkOffset = chunkAndLocalTile.chunk;
                Vector2Int localTilePosition = chunkAndLocalTile.localTile;
                int localTileIndex = localTilePosition.y * CHUNK_SIZE + localTilePosition.x;

                borders.BorderUp.Add(moduleKeyToIndex[_loadedChunks[chunk + chunkOffset][localTileIndex]]);
            }

            // bottom border
            for (int t = 0; t < BLOCK_SIZE; t++)
            {
                Vector2Int borderStartPos = blockStartPos + new Vector2Int(t, -1);
                var chunkAndLocalTile = GetChunkAndLocalTilePositionFromTile(borderStartPos);
                Vector2Int chunkOffset = chunkAndLocalTile.chunk;
                Vector2Int localTilePosition = chunkAndLocalTile.localTile;
                int localTileIndex = localTilePosition.y * CHUNK_SIZE + localTilePosition.x;

                borders.BorderDown.Add(moduleKeyToIndex[_loadedChunks[chunk + chunkOffset][localTileIndex]]);
            }

            // left border
            for (int t = 0; t < BLOCK_SIZE; t++)
            {
                Vector2Int borderStartPos = blockStartPos + new Vector2Int(-1, t);
                var chunkAndLocalTile = GetChunkAndLocalTilePositionFromTile(borderStartPos);
                Vector2Int chunkOffset = chunkAndLocalTile.chunk;
                Vector2Int localTilePosition = chunkAndLocalTile.localTile;
                int localTileIndex = localTilePosition.y * CHUNK_SIZE + localTilePosition.x;

                borders.BorderLeft.Add(moduleKeyToIndex[_loadedChunks[chunk + chunkOffset][localTileIndex]]);
            }

            // right border
            for (int t = 0; t < BLOCK_SIZE; t++)
            {
                Vector2Int borderStartPos = blockStartPos + new Vector2Int(BLOCK_SIZE, t);
                var chunkAndLocalTile = GetChunkAndLocalTilePositionFromTile(borderStartPos);
                Vector2Int chunkOffset = chunkAndLocalTile.chunk;
                Vector2Int localTilePosition = chunkAndLocalTile.localTile;
                int localTileIndex = localTilePosition.y * CHUNK_SIZE + localTilePosition.x;

                borders.BorderRight.Add(moduleKeyToIndex[_loadedChunks[chunk + chunkOffset][localTileIndex]]);
            }


            return borders;
        }

        private void UpdateChunksFromBlock(Vector2Int chunkPos, int layer, NativeArray<int> wfcOutput,
            Dictionary<int, int> wfcGlobalsModuleIndexToKey, int defaultTileKey)
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
                        output >= 0 ? wfcGlobalsModuleIndexToKey[output] : defaultTileKey;
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

        private async Task SaveChunkAsync(Vector2Int chunkCoord, int[] tiles)
        {
            string path = Path.Combine(_chunkDirectory, $"chunk_{chunkCoord.x}_{chunkCoord.y}.bin");
            await using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true);

            byte[] buffer = new byte[CHUNK_SIZE * CHUNK_SIZE * sizeof(int)];
            Buffer.BlockCopy(tiles, 0, buffer, 0, buffer.Length);
            await fs.WriteAsync(buffer);
        }

        private async Task<int[]> LoadChunkAsync(Vector2Int chunkCoord)
        {
            string path = Path.Combine(_chunkDirectory, $"chunk_{chunkCoord.x}_{chunkCoord.y}.bin");
            await using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true);

            byte[] buffer = new byte[CHUNK_SIZE * CHUNK_SIZE * sizeof(int)];
            await fs.ReadAsync(buffer);

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

        public static async Task SaveAllGeneratedBlocksDictAsync(Dictionary<Vector2Int, byte> chunkLayers, string path)
        {
            int count = chunkLayers.Count;
            byte[] buffer = new byte[sizeof(int) + count * (sizeof(int) * 2 + sizeof(byte))];

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

            await using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true);
            await fs.WriteAsync(buffer);
        }

        public static async Task<Dictionary<Vector2Int, byte>> LoadChunkLayersAsync(string path)
        {
            Dictionary<Vector2Int, byte> chunkLayers = new Dictionary<Vector2Int, byte>();

            if (!File.Exists(path))
                return chunkLayers;

            byte[] buffer = await File.ReadAllBytesAsync(path);

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