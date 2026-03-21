using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MagusStudios.WaveFunctionCollapse.Utils;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Tilemaps;
using Random = Unity.Mathematics.Random;

namespace MagusStudios.WaveFunctionCollapse
{
    public class WfcWorldStreamer : MonoBehaviour
    {
        public Transform Target; // The target transform to generate the world around (the player)
        public Tilemap TargetTilemap; // The target tilemap to generate the world upon

        [SerializeField] [Header("Load chunks that are within this radius (in units of chunks)")]
        float loadRadius = 2.5f;

        [SerializeField] [Header("Unload chunks that are outside this radius (in units of chunks)")]
        float unloadRadius = 4.5f;

        [SerializeField] [Header("Overlap for layered block evaluation")]
        private int _overlap = 2;

        [SerializeField] private WfcModuleSet _moduleSet;

        // ~ Constants ~

        // directory where chunks are saved
        private string _chunkDirectory;

        private const int CHUNK_SIZE = 48;

        // ~ State ~

        // Chunks currently loaded and their data
        private readonly Dictionary<Vector2Int, int[]> _loadedChunks = new();

        // all blocks generated and the layer they have been generated through
        private Dictionary<Vector2Int, byte> _generatedBlocks = new Dictionary<Vector2Int, byte>();

        // all chunks pregenerated with grass only
        private HashSet<Vector2Int> _pregeneratedChunks = new();

        // todo cached containers for UpdateChunks
        // chunks generated this update
        // chunks loaded this update

        private Vector2Int _lastPlayerChunk = new Vector2Int(int.MaxValue, int.MaxValue);

        private static readonly Vector2Int[] _neighborOffsets =
        {
            new Vector2Int(-1, -1),
            new Vector2Int(0, -1),
            new Vector2Int(1, -1),
            new Vector2Int(-1, 0),
            new Vector2Int(1, 0),
            new Vector2Int(-1, 1),
            new Vector2Int(0, 1),
            new Vector2Int(1, 1),
        };

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

            // load all generated chunk coords
            _generatedBlocks = LoadChunkLayers(GetChunkLayersPath());

            // load all pregenerated chunk coords
            _pregeneratedChunks = LoadPregeneratedChunks(GetPregeneratedChunksPath());

            TargetTilemap.ClearAllTiles();
            TargetTilemap.RefreshAllTiles();
        }

        private void OnEnable()
        {
            StartCoroutine(StreamWorld());
        }

        private void OnDisable()
        {
            StopAllCoroutines();
        }

        private IEnumerator StreamWorld()
        {
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

        // private void UpdateChunks(Vector2Int playerChunkPosition)
        // {
        //     // --- Get blocks and layers to generate  ---
        //
        //     // Get positions of all chunks that are close to the player and not already loaded. They will be loaded and
        //     // generated if not already.
        //     GetNewBlocksInLoadRadius(playerChunkPosition, out HashSet<Vector2Int> newBlocksInLoadRadius);
        //
        //     // container to get all blocks that should be generated and to what layer they should be generated to
        //     HashSet<Vector2Int>[] blocksToGenerate = new HashSet<Vector2Int>[4]; // 4 passes of generation
        //     
        //     // initialize it
        //     for (int i = 0; i < blocksToGenerate.Length; i++)
        //     {
        //         blocksToGenerate[i] = new HashSet<Vector2Int>();
        //     }
        //
        //     // first, add all chunks we want fully generated to layer 4
        //     foreach (Vector2Int block in newBlocksInLoadRadius)
        //     {
        //         if (!_generatedBlocks.ContainsKey(block))
        //         {
        //             blocksToGenerate[3].Add(block);
        //         }
        //         if (_generatedBlocks[block] < 4)
        //             blocksToGenerate[3].Add(block);
        //     }
        //
        //     // Fill the dict with adjacent dependent blocks for each block that will be fully generated through layer 4.
        //     // The layer 4 block at x, y depends on layer 3 blocks at x, y and x - 1, y
        //     // The layer 3 block at x, y depends on layer 2 blocks at x, y and x, y+1
        //     // The layer 2 block at x, y depends  on layer 1 blocks x, y and x+1, y
        //     // The layer 1 block at x, y has no dependencies
        //     // Thus, a layer 4 block at x, y will have 12 other blocks at different layers that it depends on if you 
        //     // cascade the dependencies. The system will generate blocks in lower layers first to accommodate blocks
        //     // that are scheduled for generation at higher layers. This keeps generation deterministic and constant
        //     // time. It also reduces the error rate and prevents errors at edges of the map.
        //
        //     // add all dependent chunks for each layer
        //
        //     // The layer 4 block at x, y depends on layer 3 blocks at x, y and x - 1, y
        //     foreach (Vector2Int block in blocksToGenerate[3])
        //     {
        //         for (int x = 0; x >= -1; x--)
        //         {
        //             Vector2Int dependent = block + new Vector2Int(block.x + x, block.y);
        //             if (!_generatedBlocks.ContainsKey(dependent) || _generatedBlocks[dependent] < 3)
        //             {
        //                 if (!blocksToGenerate[2].Contains(dependent))
        //                     blocksToGenerate[2].Add(dependent);
        //             }
        //         }
        //     }
        //
        //     // The layer 3 block at x, y depends on layer 2 blocks at x, y and x, y+1
        //     foreach (Vector2Int block in blocksToGenerate[2])
        //     {
        //         for (int y = 0; y <= 1; y++)
        //         {
        //             Vector2Int dependent = block + new Vector2Int(block.x, block.y + y);
        //             if (!_generatedBlocks.ContainsKey(dependent) || _generatedBlocks[dependent] < 2)
        //             {
        //                 if(!blocksToGenerate[1].Contains(dependent))
        //                     blocksToGenerate[1].Add(dependent);
        //             }
        //         }
        //     }
        //
        //     // The layer 2 block at x, y depends  on layer 1 blocks x, y and x+1, y
        //     foreach (Vector2Int block in blocksToGenerate[1])
        //     {
        //         for (int x = 0; x <= 1; x++)
        //         {
        //             Vector2Int dependent = block + new Vector2Int(block.x + x, block.y);
        //             if (!_generatedBlocks.ContainsKey(dependent) || _generatedBlocks[dependent] < 1)
        //             {
        //                 blocksToGenerate[0].Add(dependent);
        //             }
        //         }
        //     }
        //     
        //     // todo seed randomization with hash of block position and layer
        //     // todo get positions of all chunks that are loaded currently and outside the load radius. They will be unloaded.
        //     // GetChunksToUnload(playerChunkPosition, out HashSet<Vector2Int> chunksToUnload);
        //     
        //     // --- Generate using WfcJob instances ---
        //
        //     // create a dictionary to track all the wave function collapse runs
        //     Dictionary<Vector2Int, WfcState> stateDict = new();
        //
        //     // Create the globals for wfc
        //     WfcGlobals wfcGlobals = new WfcGlobals(_moduleSet);
        //     // todo when biomes are added, one of these will be needed for each module set
        //
        //     // create rng
        //     Random blockSeedGenerator = new Random(Seed);
        //
        //     // generate the chunks in 4 passes using the modifying-in-blocks approach
        //     for (int pass = 0; pass < 4; pass++)
        //     {
        //         List<JobHandle> jobHandles = new List<JobHandle>();
        //         foreach (Vector2Int chunk in blocksToGenerate[pass])
        //         {
        //             // get the border information from the neighbors and stuff
        //             int blockSize = CHUNK_SIZE + _overlap * 2;
        //             WfcUtils.Borders borders = GetBorders(extraChunks, chunk, wfcGlobals.moduleKeyToIndex);
        //             WfcState wfcState = new WfcState(new Vector2Int(blockSize, blockSize),
        //                 _moduleSet.Modules.Count, borders);
        //
        //             // add to state dict to keep track of this run of wfc
        //             stateDict.Add(chunk, wfcState);
        //
        //             Unity.Mathematics.Random rng = new Random(blockSeedGenerator.NextUInt());
        //
        //             // === Create and schedule the job ===
        //             WfcJob wfc = new WfcJob
        //             {
        //                 Modules = wfcGlobals.Modules,
        //                 Weights = wfcGlobals.Weights,
        //                 Cells = wfcState.Cells,
        //                 AllDirectionPermutations = wfcGlobals.directions,
        //                 UpBorder = wfcState.UpBorder,
        //                 DownBorder = wfcState.DownBorder,
        //                 LeftBorder = wfcState.LeftBorder,
        //                 RightBorder = wfcState.RightBorder,
        //                 EntropyHeap = wfcState.EntropyHeap,
        //                 EntropyIndices = wfcState.EntropyIndices,
        //                 random = rng,
        //                 PropagationStack = wfcState.PropagationStack,
        //                 PropagationStackTop = 0,
        //                 Width = blockSize,
        //                 Height = blockSize,
        //                 Output = wfcState.Output,
        //                 Flag = WfcJob.State.OK
        //             };
        //
        //             wfcState.Job = wfc;
        //
        //             // generate the chunk
        //             jobHandles.Add(wfc.Schedule());
        //         }
        //
        //         // Complete all scheduled jobs
        //         NativeArray<JobHandle> jobHandlesNative =
        //             new NativeArray<JobHandle>(WfcUtils.AllDirectionOrders.Length, Allocator.Persistent);
        //
        //         jobHandlesNative = new NativeArray<JobHandle>(jobHandles.Count, Allocator.Persistent);
        //         for (int i = 0; i < jobHandles.Count; i++)
        //         {
        //             jobHandlesNative[i] = jobHandles[i];
        //         }
        //
        //         JobHandle.CompleteAll(jobHandlesNative);
        //
        //         // Update the affected chunks
        //         foreach (KeyValuePair<Vector2Int, WfcState> kvp in stateDict)
        //         {
        //             Vector2Int pos = kvp.Key;
        //             WfcState wfcState = kvp.Value;
        //
        //             UpdateChunksFromBlock(pos, wfcState);
        //         }
        //     }
        //
        //     // todo update the generated blocks global and also write that to file
        //
        //     // write changes to file
        //
        //     // finally, update the tilemap with the changes to _loadedChunks
        // }

        private IEnumerator UpdateChunks(Vector2Int playerChunkPosition)
        {
            GetUnloadedChunksInLoadRadius(playerChunkPosition, out HashSet<Vector2Int> chunksToLoadOrGenerate);

            // keep track of chunks we will generate or load
            HashSet<Vector2Int> chunksGenerated = new HashSet<Vector2Int>();
            HashSet<Vector2Int> chunksLoadedOrGenerated = new HashSet<Vector2Int>();

            // load or pre-generate
            foreach (Vector2Int chunkPos in chunksToLoadOrGenerate)
            {
                // case: chunk has never been pregenerated -> pregenerate it then load it into _loadedChunks
                if (!_pregeneratedChunks.Contains(chunkPos))
                {
                    // pre-generate the chunk with grass only
                    int size = CHUNK_SIZE * CHUNK_SIZE;
                    int[] grass = new int[size];
                    for (int i = 0; i < size; i++)
                    {
                        grass[i] = _moduleSet.DefaultTileKey;
                    }

                    _loadedChunks.Add(chunkPos, grass);
                    chunksGenerated.Add(chunkPos);
                    chunksLoadedOrGenerated.Add(chunkPos);
                    _pregeneratedChunks.Add(chunkPos);
                    continue;
                }

                // case: chunk exists but not loaded -> load it into _loadedChunks
                if (!_loadedChunks.ContainsKey(chunkPos))
                {
                    _loadedChunks.Add(chunkPos, LoadChunk(chunkPos)); // load from file
                    chunksLoadedOrGenerated.Add(chunkPos);
                    yield return null;
                }
            }

            yield return null;

            // write new pregenerated chunks to file
            foreach (Vector2Int chunkPos in chunksGenerated)
            {
                SaveChunk(chunkPos, _loadedChunks[chunkPos]);
            }

            Debug.Log($"[{nameof(WfcWorldStreamer)}] Pre-generated {chunksGenerated.Count} new chunks.");

            // write pregeneratedChunks hash set to file
            SavePregeneratedChunks(GetPregeneratedChunksPath(), _pregeneratedChunks);

            Debug.Log($"[{nameof(WfcWorldStreamer)}] Saved {chunksGenerated.Count} new chunks to file.");
            yield return null;

            // unload chunks
            GetLoadedChunksOutsideUnloadRadius(playerChunkPosition, out HashSet<Vector2Int> chunksToUnload);

            foreach (Vector2Int chunkPos in chunksToUnload)
            {
                _loadedChunks.Remove(chunkPos);
            }

            // update the tilemap with all chunks generated or loaded this map update
            foreach (Vector2Int chunkPos in chunksLoadedOrGenerated)
            {
                TileBase[] tileBases = new TileBase[CHUNK_SIZE * CHUNK_SIZE];

                for (int i = 0; i < CHUNK_SIZE * CHUNK_SIZE; i++)
                {
                    tileBases[i] = _moduleSet.TileDatabase.Tiles[_loadedChunks[chunkPos][i]];
                }

                Vector3Int tilePositionOfChunk = (chunkPos * CHUNK_SIZE).ToVector3Int();
                BoundsInt bounds = new BoundsInt(tilePositionOfChunk, new Vector3Int(CHUNK_SIZE, CHUNK_SIZE, 1));

                TargetTilemap.SetTilesBlock(bounds, tileBases);
                yield return null;
            }

            Debug.Log($"[{nameof(WfcWorldStreamer)}] Updated the tilemap with {chunksLoadedOrGenerated.Count} chunks.");

            // finally, unload any chunks that are outside the unload radius and update the tilemap again
            foreach (Vector2Int chunkPos in chunksToUnload)
            {
                _loadedChunks.Remove(chunkPos);

                Vector3Int tilePositionOfChunk = (chunkPos * CHUNK_SIZE).ToVector3Int();
                BoundsInt bounds = new BoundsInt(tilePositionOfChunk, new Vector3Int(CHUNK_SIZE, CHUNK_SIZE, 1));

                TileBase[] nullArray = new TileBase[CHUNK_SIZE * CHUNK_SIZE];

                for (int i = 0; i < nullArray.Length; i++)
                {
                    nullArray[i] = null;
                }

                TargetTilemap.SetTilesBlock(bounds, nullArray);
                yield return null;
            }

            Debug.Log(
                $"[{nameof(WfcWorldStreamer)}] Unloaded {chunksToUnload.Count} chunks and updated tilemap.");
        }

        private void GetUnloadedChunksInLoadRadius(Vector2Int playerChunkPosition,
            out HashSet<Vector2Int> chunksToLoadOrGenerate)
        {
            chunksToLoadOrGenerate = new();

            // get chunks that should be loaded (if previously generated) or generated (if new) with proximity to the player
            float rSquared = loadRadius * loadRadius;

            int chunkCeil = Mathf.CeilToInt(loadRadius);
            for (int y = -chunkCeil; y <= chunkCeil; y++)
            {
                for (int x = -chunkCeil; x <= chunkCeil; x++)
                {
                    if (x * x + y * y > rSquared) continue;

                    Vector2Int chunkPos = playerChunkPosition + new Vector2Int(x, y);
                    if (!_loadedChunks.ContainsKey(chunkPos)) chunksToLoadOrGenerate.Add(chunkPos);
                }
            }
        }

        private void GetLoadedChunksOutsideUnloadRadius(Vector2Int playerChunkPosition,
            out HashSet<Vector2Int> chunksToUnload)
        {
            chunksToUnload = new HashSet<Vector2Int>();

            float rSquared = unloadRadius * unloadRadius;

            foreach (KeyValuePair<Vector2Int, int[]> kvp in _loadedChunks)
            {
                Vector2Int chunk = kvp.Key;

                Vector2Int delta = playerChunkPosition - chunk;

                if (delta.x * delta.x + delta.y * delta.y >= rSquared)
                {
                    chunksToUnload.Add(chunk);
                }
            }
        }

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

        private void SaveChunk(Vector2Int chunkCoord, int[] tiles)
        {
            string path = Path.Combine(_chunkDirectory, $"chunk_{chunkCoord.x}_{chunkCoord.y}.bin");
            using (BinaryWriter writer = new BinaryWriter(File.Open(path, FileMode.Create)))
            {
                for (int i = 0; i < CHUNK_SIZE * CHUNK_SIZE; i++)
                {
                    writer.Write(tiles[i]);
                }
            }
        }

        private int[] LoadChunk(Vector2Int chunkCoord)
        {
            string path = Path.Combine(_chunkDirectory, $"chunk_{chunkCoord.x}_{chunkCoord.y}.bin");
            using (BinaryReader reader = new BinaryReader(File.Open(path, FileMode.Open)))
            {
                int size = CHUNK_SIZE * CHUNK_SIZE;
                int[] tiles = new int[size];

                for (int i = 0; i < size; i++)
                {
                    tiles[i] = reader.ReadInt32();
                }

                return tiles;
            }
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
        private string GetChunkLayersPath()
        {
            string fileName = "chunk_layers.dat";
            return Path.Combine(_chunkDirectory, fileName);
        }

        private string GetPregeneratedChunksPath()
        {
            string fileName = "pregenerated_chunks.dat";
            return Path.Combine(_chunkDirectory, fileName);
        }

        public static void SaveChunkLayers(Dictionary<Vector2Int, byte> chunkLayers, string path)
        {
            using (BinaryWriter writer = new BinaryWriter(File.Open(path, FileMode.Create)))
            {
                writer.Write(chunkLayers.Count);

                foreach (var pair in chunkLayers)
                {
                    writer.Write(pair.Key.x);
                    writer.Write(pair.Key.y);
                    writer.Write(pair.Value);
                }
            }
        }

        public static Dictionary<Vector2Int, byte> LoadChunkLayers(string path)
        {
            Dictionary<Vector2Int, byte> chunkLayers = new Dictionary<Vector2Int, byte>();

            if (!File.Exists(path))
                return chunkLayers;

            using (BinaryReader reader = new BinaryReader(File.Open(path, FileMode.Open)))
            {
                int count = reader.ReadInt32();

                for (int i = 0; i < count; i++)
                {
                    int x = reader.ReadInt32();
                    int y = reader.ReadInt32();
                    byte layersGenerated = reader.ReadByte();

                    chunkLayers[new Vector2Int(x, y)] = layersGenerated;
                }
            }

            return chunkLayers;
        }

        public static HashSet<Vector2Int> LoadPregeneratedChunks(string path)
        {
            HashSet<Vector2Int> pregeneratedChunks = new HashSet<Vector2Int>();

            if (!File.Exists(path))
                return pregeneratedChunks;

            using (BinaryReader reader = new BinaryReader(File.Open(path, FileMode.Open)))
            {
                int count = reader.ReadInt32();

                for (int i = 0; i < count; i++)
                {
                    int x = reader.ReadInt32();
                    int y = reader.ReadInt32();

                    pregeneratedChunks.Add(new Vector2Int(x, y));
                }
            }

            return pregeneratedChunks;
        }

        public static void SavePregeneratedChunks(string path, HashSet<Vector2Int> pregeneratedChunks)
        {
            using (BinaryWriter writer = new BinaryWriter(File.Open(path, FileMode.Create)))
            {
                writer.Write(pregeneratedChunks.Count);

                foreach (Vector2Int chunk in pregeneratedChunks)
                {
                    writer.Write(chunk.x);
                    writer.Write(chunk.y);
                }
            }
        }
    }
}