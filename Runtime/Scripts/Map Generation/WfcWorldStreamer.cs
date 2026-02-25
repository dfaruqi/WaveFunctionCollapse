using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MagusStudios.WaveFunctionCollapse.Utils;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Tilemaps;
using Random = Unity.Mathematics.Random;

namespace MagusStudios.WaveFunctionCollapse
{
    public class WfcWorldStreamer : MonoBehaviour
    {
        public Transform Target; // The target transform to generate the world around (the player)
        public uint Seed = 0;
        
        [SerializeField] private WfcModuleSet _moduleSet; // todo make this depend on chunk position for more biomes

        [SerializeField] [Tooltip("Load chunks that are within this radius")]
        int loadRadius = 4;

        [SerializeField] [Tooltip("Unload chunks that are outside this radius")]
        int unloadRadius = 5;

        [SerializeField] [Tooltip("Overlap for Layered Modifying in Blocks Approach")]
        private int _overlap = 2;

        [SerializeField] private WaveFunctionCollapse _waveFunctionCollapse;
        [SerializeField] private Tilemap _tilemap;
        [SerializeField] private Grid _grid;

        private readonly Dictionary<Vector2Int, int[,]> _loadedChunks = new();
        private Vector2Int _lastPlayerChunk;

       // directory where chunks are saved
        private string _chunkDirectory;

        private const int CHUNK_SIZE = 64;

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
            if (!Directory.Exists(_chunkDirectory)) Directory.CreateDirectory(_chunkDirectory);
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
            _lastPlayerChunk = GetPlayerChunk(Target.position);

            while (true)
            {
                Vector2Int currentChunk = GetPlayerChunk(Target.position);

                if (currentChunk != _lastPlayerChunk)
                {
                    UpdateChunks(currentChunk);
                    _lastPlayerChunk = currentChunk;
                }

                yield return new WaitForSeconds(0.25f); // throttle
            }
        }

        private void UpdateChunks(Vector2Int currentChunk)
        {
            HashSet<Vector2Int> chunksToLoadOrGenerate = new HashSet<Vector2Int>();
            HashSet<Vector2Int> chunksToUnload = new HashSet<Vector2Int>();

            //get chunks that should be loaded because the player has come near them and store in chunksToLoadOrGenerate
            int rSquared = loadRadius * loadRadius;

            for (int y = -loadRadius; y <= loadRadius; y++)
            {
                for (int x = -loadRadius; x <= loadRadius; x++)
                {
                    float yCenterOfCell = y + 0.5f;
                    float xCenterOfCell = x + 0.5f;

                    if (xCenterOfCell * xCenterOfCell + yCenterOfCell * yCenterOfCell <= rSquared)
                    {
                        Vector2Int chunk = new Vector2Int(
                            currentChunk.x + x,
                            currentChunk.y + y
                        );

                        if (!_loadedChunks.ContainsKey(chunk)) // fixed bug here
                        {
                            chunksToLoadOrGenerate.Add(chunk);
                        }
                    }
                }
            }


            //get distant chunks that should be unloaded because they are too far from the player and store in
            //chunksToUnload
            foreach (KeyValuePair<Vector2Int, int[,]> kvp in _loadedChunks)
            {
                Vector2Int chunk = kvp.Key;
                int x = chunk.x;
                int y = chunk.y;
                rSquared = unloadRadius * unloadRadius;
                if (x * x + y * y > rSquared)
                {
                    chunksToUnload.Add(chunk);
                }
            }

            HashSet<Vector2Int> chunksToLoad = new HashSet<Vector2Int>();
            HashSet<Vector2Int> chunksToGenerate = new HashSet<Vector2Int>();

            //separate the chunks that should load from the chunks that should generate and store
            foreach (Vector2Int chunk in chunksToLoadOrGenerate)
            {
                string path = GetChunkPath(chunk);
                bool exists = File.Exists(path);
                if (!exists)
                {
                    chunksToGenerate.Add(chunk);
                }
                else
                {
                    chunksToLoad.Add(chunk);
                }
            }

            //load any chunks previously generated here
            foreach (Vector2Int chunk in chunksToLoad)
            {
                int[,] loaded = LoadChunk(GetChunkPath(chunk));
                _loadedChunks[chunk] = loaded;
            }

            // before generating any new chunks, load any extra chunks that are adjacent or diagonal to the chunks
            // to generate and not already loaded, as they will be needed for providing crucial border information. In
            // addition, some of their edge tiles may be overwritten by the generation of the new chunks, so their
            // files will need to be updated. If they are currently loaded chunks, they will also need to be
            // updated there.
            Dictionary<Vector2Int, int[,]> extraChunks = new();

            foreach (Vector2Int chunkPos in chunksToGenerate)
            {
                foreach (var offset in _neighborOffsets)
                {
                    Vector2Int neighbor = chunkPos + offset;

                    if (!_loadedChunks.ContainsKey(neighbor) && !extraChunks.ContainsKey(neighbor))
                    {
                        extraChunks[neighbor] = LoadChunk(GetChunkPath(neighbor));
                    }
                }
            }

            // create a dictionary to track all the wave function collapse runs
            Dictionary<Vector2Int, WfcState> stateDict = new();
            
            // Create the globals for wfc
            WfcGlobals wfcGlobals = new WfcGlobals(_moduleSet);
            
            //create rng
            Random blockSeedGenerator = new Random(Seed);
            
            // generate the chunks in passes using the modifying-in-blocks approach
            for (int pass = 0; pass < 4; pass++)
            {
                List<JobHandle> jobHandles = new List<JobHandle>();
                foreach (Vector2Int chunk in chunksToGenerate)
                {
                    bool shouldProcess = pass == (((chunk.x & 1) << 1) | (chunk.y & 1));

                    if (!shouldProcess)
                        continue;

                    // get the border information from the neighbors and stuff
                    int blockSize = CHUNK_SIZE + _overlap * 2;
                    WfcState wfcState = new WfcState(new Vector2Int(blockSize, blockSize),
                        _moduleSet.Modules.Count,
                        GetBorders(extraChunks, chunk));

                    stateDict.Add(chunk, wfcState);

                    Unity.Mathematics.Random rng = new Random(blockSeedGenerator.NextUInt());
                    
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
                        Width = blockSize,
                        Height = blockSize,
                        Output = wfcState.Output,
                        Flag = WfcJob.State.OK
                    };

                    // generate the chunk

                    // using the output, update all affected chunks in the loaded dict or extras dict

                    // maintain a list of which chunks were updated (all of them?) so that at the end of all this,
                    // each chunk in the list can be written to file
                }
            }

            //finally, update the tilemap with the changes to _loadedChunks
        }

        private static void SaveChunk(string path, int[,] tiles)
        {
            using (BinaryWriter writer = new BinaryWriter(File.Open(path, FileMode.Create)))
            {
                int width = tiles.GetLength(0);
                int height = tiles.GetLength(1);

                writer.Write(width);
                writer.Write(height);

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        writer.Write(tiles[x, y]);
                    }
                }
            }
        }

        private enum NeighborBlockDirection
        {
            UpLeft,
            Up,
            UpRight,
            Left,
            Right,
            DownLeft,
            Down,
            DownRight
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

        private WfcUtils.Borders GetBorders(Dictionary<Vector2Int, int[,]> extraChunks, Vector2Int position)
        {
            int blockSize = CHUNK_SIZE + _overlap * 2;

            // create the borders
            WfcUtils.Borders borders = new WfcUtils.Borders();

            // create a dict to access neighbor blocks (including diagonals, so there are 8 neighbors)
            Dictionary<NeighborBlockDirection, int[,]> neighbors = new();

            // fill the neighbor dict with the neighbors
            foreach (NeighborBlockDirection dir in Enum.GetValues(typeof(NeighborBlockDirection)))
            {
                Vector2Int neighborPos = position + NeighborOffsets[(int)dir];
                neighbors[dir] = GetChunk(extraChunks, neighborPos);
            }

            // fill top border
            List<int> borderUp = new List<int>();
            for (int x = 0; x < blockSize; x++)
            {
                if (x < _overlap)
                {
                    int[,] chunkUpLeft = neighbors[NeighborBlockDirection.UpLeft];

                    if (chunkUpLeft == null) borderUp.Add(_moduleSet.DefaultTileKey);
                    else borderUp.Add(chunkUpLeft[CHUNK_SIZE - _overlap + x, _overlap]);
                }
                else if (x >= CHUNK_SIZE + _overlap)
                {
                    int[,] chunkUpRight = neighbors[NeighborBlockDirection.UpRight];

                    if (chunkUpRight == null) borderUp.Add(_moduleSet.DefaultTileKey);
                    else borderUp.Add(chunkUpRight[x - _overlap - CHUNK_SIZE, _overlap]);
                }
                else
                {
                    int[,] chunkUp = neighbors[NeighborBlockDirection.Up];

                    if (chunkUp == null) borderUp.Add(_moduleSet.DefaultTileKey);
                    else borderUp.Add(chunkUp[x - _overlap, _overlap]);
                }
            }

            borders.BorderUp = borderUp;

            // fill bottom border
            List<int> borderDown = new List<int>();
            int startY = CHUNK_SIZE - _overlap - 1;

            for (int x = 0; x < blockSize; x++)
            {
                if (x < _overlap)
                {
                    int[,] chunkDownLeft = neighbors[NeighborBlockDirection.DownLeft];

                    if (chunkDownLeft == null)
                        borderDown.Add(_moduleSet.DefaultTileKey);
                    else
                        borderDown.Add(chunkDownLeft[CHUNK_SIZE - _overlap + x, startY]);
                }
                else if (x >= CHUNK_SIZE + _overlap)
                {
                    int[,] chunkDownRight = neighbors[NeighborBlockDirection.DownRight];

                    if (chunkDownRight == null)
                        borderDown.Add(_moduleSet.DefaultTileKey);
                    else
                        borderDown.Add(chunkDownRight[x - _overlap - CHUNK_SIZE, startY]);
                }
                else
                {
                    int[,] chunkDown = neighbors[NeighborBlockDirection.Down];

                    if (chunkDown == null)
                        borderDown.Add(_moduleSet.DefaultTileKey);
                    else
                        borderDown.Add(chunkDown[x - _overlap, startY]);
                }
            }

            borders.BorderDown = borderDown;

            // fill left border
            List<int> borderLeft = new List<int>();
            int xStart = CHUNK_SIZE - _overlap - 1;

            for (int y = 0; y < blockSize; y++)
            {
                if (y < _overlap)
                {
                    int[,] chunkDownLeft = neighbors[NeighborBlockDirection.DownLeft];

                    if (chunkDownLeft == null)
                        borderLeft.Add(_moduleSet.DefaultTileKey);
                    else
                        borderLeft.Add(chunkDownLeft[xStart, CHUNK_SIZE - _overlap + y]);
                }
                else if (y >= CHUNK_SIZE + _overlap)
                {
                    int[,] chunkUpLeft = neighbors[NeighborBlockDirection.UpLeft];

                    if (chunkUpLeft == null)
                        borderLeft.Add(_moduleSet.DefaultTileKey);
                    else
                        borderLeft.Add(chunkUpLeft[xStart, y - CHUNK_SIZE - _overlap]);
                }
                else
                {
                    int[,] chunkLeft = neighbors[NeighborBlockDirection.Left];

                    if (chunkLeft == null)
                        borderLeft.Add(_moduleSet.DefaultTileKey);
                    else
                        borderLeft.Add(chunkLeft[xStart, y - _overlap]);
                }
            }

            borders.BorderLeft = borderLeft;

            // fill right border
            List<int> borderRight = new List<int>();

            for (int y = 0; y < blockSize; y++)
            {
                if (y < _overlap)
                {
                    int[,] chunkDownRight = neighbors[NeighborBlockDirection.DownRight];

                    if (chunkDownRight == null)
                        borderRight.Add(_moduleSet.DefaultTileKey);
                    else
                        borderRight.Add(chunkDownRight[_overlap, CHUNK_SIZE - _overlap + y]);
                }
                else if (y >= CHUNK_SIZE + _overlap)
                {
                    int[,] chunkUpRight = neighbors[NeighborBlockDirection.UpRight];

                    if (chunkUpRight == null)
                        borderRight.Add(_moduleSet.DefaultTileKey);
                    else
                        borderRight.Add(chunkUpRight[_overlap, y - CHUNK_SIZE - _overlap]);
                }
                else
                {
                    int[,] chunkRight = neighbors[NeighborBlockDirection.Right];

                    if (chunkRight == null)
                        borderRight.Add(_moduleSet.DefaultTileKey);
                    else
                        borderRight.Add(chunkRight[_overlap, y - _overlap]);
                }
            }

            borders.BorderRight = borderRight;

            borders.BorderUp = borderUp;
            borders.BorderDown = borderDown;
            borders.BorderLeft = borderLeft;
            borders.BorderRight = borderRight;

            return borders;
        }

        /// <summary>
        /// Helper for GetBorders
        /// </summary>
        /// <param name="extraChunks"></param>
        /// <param name="pos"></param>
        /// <returns></returns>
        private int[,] GetChunk(
            Dictionary<Vector2Int, int[,]> extraChunks,
            Vector2Int pos)
        {
            if (extraChunks.TryGetValue(pos, out var chunk))
                return chunk;

            if (_loadedChunks.TryGetValue(pos, out chunk))
                return chunk;

            return null;
        }

        private static int[,] LoadChunk(string path)
        {
            using (BinaryReader reader = new BinaryReader(File.Open(path, FileMode.Open)))
            {
                int width = reader.ReadInt32();
                int height = reader.ReadInt32();

                int[,] tiles = new int[width, height];

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        tiles[x, y] = reader.ReadInt32();
                    }
                }

                return tiles;
            }
        }

        private Vector2Int GetPlayerChunk(Vector3 cell)
        {
            int cx = Mathf.FloorToInt(cell.x / CHUNK_SIZE);
            int cy = Mathf.FloorToInt(cell.y / CHUNK_SIZE);
            return new Vector2Int(cx, cy);
        }

        private string GetChunkPath(Vector2Int chunkCoord)
        {
            string fileName = $"chunk_{chunkCoord.x}_{chunkCoord.y}.json";
            return Path.Combine(_chunkDirectory, fileName);
        }
    }
}