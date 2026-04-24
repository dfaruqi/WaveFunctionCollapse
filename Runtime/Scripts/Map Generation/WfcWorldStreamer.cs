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
        [SerializeField] private WfcTemplate template;

        // ~ Constants ~

        // directory where chunks are saved
        private string _chunkDirectory;

        // size of loaded/saved chunks, must be even
        // suggestions: 16,32,48,64
        private const int CHUNK_SIZE = 48;

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
        private readonly List<JobHandle> _jobHandles = new List<JobHandle>();

        // record of all blocks generated and the layer they have been generated through
        // (0=pregenerated, 1-4=layers 1-4)
        private Dictionary<Vector2Int, byte> _allGeneratedBlocks = new Dictionary<Vector2Int, byte>();

        private Vector2Int _lastPlayerChunk = new Vector2Int(int.MaxValue, int.MaxValue);

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

            // initialize offsets for blocks (global data)
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

            HashSet<Vector2Int> unloadedChunksInLoadDistance = new HashSet<Vector2Int>();
            GetUnloadedChunksInLoadDistance(playerChunkPosition, ref unloadedChunksInLoadDistance);

            // keep track of chunks that get generated, loaded, or unloaded
            HashSet<Vector2Int> chunksPregenerated = new HashSet<Vector2Int>();
            HashSet<Vector2Int> chunksUnloaded = new HashSet<Vector2Int>();

            // load or pre-generate
            Task loadTask = Task.WhenAll(
                unloadedChunksInLoadDistance.Select(coord => LoadOrPregenerateChunkAsync(coord, chunksPregenerated))
            );

            while (!loadTask.IsCompleted)
                yield return null;

            if (loadTask.IsFaulted)
                throw loadTask.Exception;

            // - Generate Blocks -

            // generate blocks
            // container to get all blocks that should be generated and to what layer they should be generated to
            HashSet<Vector2Int>[] blocksToGenerate = new HashSet<Vector2Int>[4]; // 4 passes of generation 0-3
            for (int i = 0; i < blocksToGenerate.Length; i++)
            {
                blocksToGenerate[i] = new HashSet<Vector2Int>();
            }

            GetChunksInDistance(playerChunkPosition, drawDistance, out HashSet<Vector2Int> chunksInDrawDistance);

            foreach (Vector2Int chunk in chunksInDrawDistance)
            {
                if (_allGeneratedBlocks[chunk] < 4) blocksToGenerate[3].Add(chunk);
            }

            CascadeBlockDependencies(ref blocksToGenerate);

            yield return StartCoroutine(GenerateBlocks(blocksToGenerate));

            // update all generated blocks dictionary
            for (byte i = 0; i < 4; i++)
            {
                foreach (Vector2Int block in blocksToGenerate[i])
                {
                    byte oldBlockGeneratedTo = _allGeneratedBlocks[block];
                    byte newBlockGeneratedTo = (byte)(i + 1);
                    if (oldBlockGeneratedTo < newBlockGeneratedTo) _allGeneratedBlocks[block] = newBlockGeneratedTo;
                }
            }

            yield return null;

            // - Unload Chunks -

            // unload chunks
            GetChunksOutsideDistance(playerChunkPosition, drawDistance + 3, _loadedChunks.Keys,
                out HashSet<Vector2Int> chunksToUnload);

            foreach (Vector2Int chunkPos in chunksToUnload)
            {
                if (_loadedChunks.Remove(chunkPos)) chunksUnloaded.Add(chunkPos);
            }

            // - Update Files -

            // get chunks affected by generation from block dependencies
            HashSet<Vector2Int> chunksAffectedByGeneration = new HashSet<Vector2Int>();

            foreach (Vector2Int block in blocksToGenerate[0])
            {
                chunksAffectedByGeneration.Add(block);
                chunksAffectedByGeneration.Add(block + Vector2Int.down);
            }

            foreach (Vector2Int block in blocksToGenerate[1])
            {
                chunksAffectedByGeneration.Add(block);
                chunksAffectedByGeneration.Add(block + Vector2Int.right);
                chunksAffectedByGeneration.Add(block + Vector2Int.down);
                chunksAffectedByGeneration.Add(block + Vector2Int.right + Vector2Int.down);
            }

            foreach (Vector2Int block in blocksToGenerate[2])
            {
                chunksAffectedByGeneration.Add(block);
                chunksAffectedByGeneration.Add(block + Vector2Int.right);
            }

            foreach (Vector2Int block in blocksToGenerate[3])
            {
                chunksAffectedByGeneration.Add(block);
            }

            // add chunks that were pregenerated (if not already added)
            foreach (Vector2Int chunk in chunksPregenerated)
            {
                chunksAffectedByGeneration.Add(chunk);
            }

            // write all chunks that were changed to file and the all generated chunk positions dict
            List<Task> saveTasks = new List<Task>();
            saveTasks.AddRange(
                chunksAffectedByGeneration.Select(chunkPos => SaveChunkAsync(chunkPos, _loadedChunks[chunkPos])));
            saveTasks.Add(SaveAllGeneratedBlocksDictAsync(_allGeneratedBlocks, GetAllGeneratedBlocksPath()));

            Task saveAll = Task.WhenAll(saveTasks);
            yield return new WaitUntil(() => saveAll.IsCompleted);

            if (saveAll.IsFaulted)
                throw saveAll.Exception;

            // - Update Tilemap - 

            // get all chunks within draw distance that are not drawn or should be redrawn because generation affected
            // them (at the edges)
            HashSet<Vector2Int> chunksToDraw =
                new HashSet<Vector2Int>(chunksInDrawDistance.Where(x =>
                    !_drawnChunks.Contains(x) ||
                    chunksAffectedByGeneration.Contains(x)));
            yield return StartCoroutine(DrawChunks(chunksToDraw));

            // un-draw chunks that are drawn and outside draw distance
            GetChunksOutsideDistance(playerChunkPosition, drawDistance + 1, _drawnChunks,
                out HashSet<Vector2Int> chunksToUndraw);
            TileBase[] nullArray = new TileBase[CHUNK_SIZE * CHUNK_SIZE];
            foreach (Vector2Int chunkPos in chunksToUndraw)
            {
                Vector3Int tilePositionOfChunk = (chunkPos * CHUNK_SIZE).ToVector3Int();
                BoundsInt bounds = new BoundsInt(tilePositionOfChunk, new Vector3Int(CHUNK_SIZE, CHUNK_SIZE, 1));

                TargetTilemap.SetTilesBlock(bounds, nullArray);

                _drawnChunks.Remove(chunkPos);

                yield return null;
            }

            // - Log -

            Debug.Log(
                $"{nameof(WfcWorldStreamer)} Chunk Updates - \n" +
                $"   loaded/generated: {unloadedChunksInLoadDistance.Count}]\n" +
                $"   unloaded: {chunksToUnload.Count})\n" +
                $"   drawn: {chunksToDraw.Count}");
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
                TileBase[] tileBases = new TileBase[CHUNK_SIZE * CHUNK_SIZE];

                for (int i = 0; i < CHUNK_SIZE * CHUNK_SIZE; i++)
                {
                    int tile = _loadedChunks[chunkPos][i];

                    if (tile < 0) tileBases[i] = null;
                    else tileBases[i] = template.TileDatabase.Tiles[tile];
                }

                Vector3Int tilePositionOfChunk = (chunkPos * CHUNK_SIZE).ToVector3Int();
                BoundsInt bounds = new BoundsInt(tilePositionOfChunk, new Vector3Int(CHUNK_SIZE, CHUNK_SIZE, 1));

                TargetTilemap.SetTilesBlock(bounds, tileBases);

                _drawnChunks.Add(chunkPos);
                yield return null;
            }
        }

        private IEnumerator GenerateBlocks(HashSet<Vector2Int>[] blocksToGenerate)
        {
            // create a dictionary to track all the wave function collapse runs
            Dictionary<Vector2Int, WfcBlockState> stateDict = new();

            // Create the globals for wfc
            WfcBiomeData wfcBiomeData = new WfcBiomeData(template);
            // todo when biomes are added, one of these will be needed for each module set

            // generate the chunks in 4 passes using the modifying-in-blocks approach
            for (byte pass = 0; pass < 4; pass++)
            {
                foreach (Vector2Int chunk in blocksToGenerate[pass])
                {
                    // Create rng
                    Random rng = new Random(TileUtils.HashWorldBlock(Seed, chunk, pass));

                    // get the border information for this block from loaded chunks
                    WfcUtils.Borders borders = GetBordersOfBlock(chunk, pass, wfcBiomeData.moduleKeyToIndex);
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

                    // If has error in output, fall back to previous layer, otherwise update the loaded chunks

                    bool error = IsValidOutput(wfcBlockState.Output, template);
                    if (error)
                        Debug.LogWarning("error in chunk " + chunkPosition);

                    if (!error)
                        UpdateChunksFromBlock(chunkPosition, pass, wfcBlockState.Output, wfcBiomeData.moduleIndexToKey,
                            template.DefaultTileKey);

                    // clean up state
                    wfcBlockState.Dispose();
                }

                stateDict.Clear();
            }

            // clean up globals

            wfcBiomeData.Dispose();
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
            out HashSet<Vector2Int> chunksOutsideDistance)
        {
            chunksOutsideDistance = new HashSet<Vector2Int>();
            foreach (Vector2Int chunkPos in chunks)
            {
                if (Mathf.Abs(position.y - chunkPos.y) > distance ||
                    Mathf.Abs(position.x - chunkPos.x) > distance)
                {
                    chunksOutsideDistance.Add(chunkPos);
                }
            }
        }

        private void GetChunksInDistance(Vector2Int position, int distance, out HashSet<Vector2Int> chunksInDistance)
        {
            chunksInDistance = new();

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
                    grass[i] = template.DefaultTileKey;
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


        private bool IsValidOutput(NativeArray<int> output, WfcTemplate template)
        {
            SerializedDictionary<int, WfcTileRules.AllowedNeighbors> modules = template.TileRules.Modules;

            for (int i = 0; i < output.Length; i++)
            {
                int value = output[i];

                if (value < 0) return false;

                if (!modules.ContainsKey(value))
                {
                    Debug.LogError($"[{nameof(WfcWorldStreamer)}] tile in output not found in template");
                    return false;
                }

                int x = i % BLOCK_SIZE;
                int y = i / BLOCK_SIZE;

                if (x > 0 && !modules[output[i - 1]].Neighbors[Direction.Left].Contains(value))
                    return false;

                if (x < BLOCK_SIZE - 1 && !modules[output[i + 1]].Neighbors[Direction.Right].Contains(value))
                    return false;

                if (y > 0 && !modules[output[i - BLOCK_SIZE]].Neighbors[Direction.Down].Contains(value))
                    return false;

                if (y < BLOCK_SIZE - 1 && !modules[output[i + BLOCK_SIZE]].Neighbors[Direction.Up].Contains(value))
                    return false;
            }

            return true;
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