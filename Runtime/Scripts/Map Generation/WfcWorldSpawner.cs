using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

namespace MagusStudios.WaveFunctionCollapse
{
    [RequireComponent(typeof(WfcWorldStreamer))]
    public class WfcWorldSpawner : MonoBehaviour
    {
        [SerializeField] private int _spawnsPerFrame = 64;

        private WfcWorldStreamer _worldStreamer;

        private Dictionary<GameObject, ObjectPool<WorldSpawn>> _spawnPools =
            new Dictionary<GameObject, ObjectPool<WorldSpawn>>();

        private Dictionary<Vector2Int, List<(WorldSpawn spawn, GameObject prefab)>> _chunkSpawns =
            new Dictionary<Vector2Int, List<(WorldSpawn, GameObject)>>();

        private readonly Dictionary<Vector2Int, int> _chunkGeneration = new Dictionary<Vector2Int, int>();

        private readonly Queue<SpawnRequest> _spawnQueue = new Queue<SpawnRequest>();

        private struct SpawnRequest
        {
            public Vector2Int ChunkPos;
            public int Generation;
            public GameObject Prefab;
            public Vector2 Position;
        }

        private void Awake()
        {
            _worldStreamer = GetComponent<WfcWorldStreamer>();
        }

        private void OnEnable()
        {
            _worldStreamer.OnChunkDrawn += HandleChunkDrawn;
            _worldStreamer.OnChunkUndrawn += HandleChunkUndrawn;
        }

        private void OnDisable()
        {
            _worldStreamer.OnChunkDrawn -= HandleChunkDrawn;
            _worldStreamer.OnChunkUndrawn -= HandleChunkUndrawn;
        }

        private void Update()
        {
            int remaining = _spawnsPerFrame;

            while (remaining > 0 && _spawnQueue.Count > 0)
            {
                var request = _spawnQueue.Dequeue();

                if (!_chunkGeneration.TryGetValue(request.ChunkPos, out int currentGen)
                    || currentGen != request.Generation)
                    continue;

                ObjectPool<WorldSpawn> pool = GetOrCreatePool(request.Prefab);
                WorldSpawn spawn = pool.Get();
                spawn.transform.position = request.Position;
                _chunkSpawns[request.ChunkPos].Add((spawn, request.Prefab));
                remaining--;
            }
        }

        private void HandleChunkDrawn(Vector2Int chunkPos, IReadOnlyList<int> chunkData, Biome biome)
        {
            int chunkSize = WfcWorldStreamer.CHUNK_SIZE;
            _chunkSpawns[chunkPos] = new List<(WorldSpawn, GameObject)>();

            _chunkGeneration.TryGetValue(chunkPos, out int gen);
            gen++;
            _chunkGeneration[chunkPos] = gen;

            for (int i = 0; i < chunkData.Count; i++)
            {
                var tile = biome.GetTemplate(chunkPos).TileDatabase[chunkData[i]];
                if (tile is not GameObjectTile gameObjectTile)
                    continue;

                int localX = i % chunkSize;
                int localY = i / chunkSize;
                Vector2Int cellWorldPos =
                    TileUtils.GetWorldPosition(chunkPos, new Vector2Int(localX, localY), chunkSize);
                Vector2 cellCenterPos = TileUtils.GetTileCenterPosition(cellWorldPos);

                GameObject prefab = gameObjectTile.GetGameObject(cellWorldPos);
                _spawnQueue.Enqueue(new SpawnRequest
                {
                    ChunkPos = chunkPos,
                    Generation = gen,
                    Prefab = prefab,
                    Position = cellCenterPos
                });
            }
        }

        private void HandleChunkUndrawn(Vector2Int chunkPos)
        {
            if (!_chunkSpawns.TryGetValue(chunkPos, out var spawns))
                return;

            foreach (var (spawn, prefab) in spawns)
                _spawnPools[prefab].Release(spawn);

            _chunkSpawns.Remove(chunkPos);
            _chunkGeneration.Remove(chunkPos);
        }

        private ObjectPool<WorldSpawn> GetOrCreatePool(GameObject prefab)
        {
            if (!_spawnPools.TryGetValue(prefab, out var pool))
            {
                pool = new ObjectPool<WorldSpawn>(
                    createFunc: () =>
                    {
                        var instance = Instantiate(prefab);
                        return instance.GetComponent<WorldSpawn>() ?? instance.AddComponent<WorldSpawn>();
                    },
                    actionOnGet: worldSpawn => worldSpawn.gameObject.SetActive(true),
                    actionOnRelease: worldSpawn => worldSpawn.gameObject.SetActive(false),
                    actionOnDestroy: worldSpawn => Destroy(worldSpawn.gameObject),
                    defaultCapacity: 32,
                    maxSize: 2048
                );
                _spawnPools[prefab] = pool;
            }

            return pool;
        }
    }
}