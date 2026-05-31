using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

namespace MagusStudios.WaveFunctionCollapse
{
    [RequireComponent(typeof(WfcWorldStreamer))]
    public class WorldObjectSpawner : MonoBehaviour
    {
        [SerializeField] private int _spawnsPerFrame = 64;
        [SerializeField] private GameObject _spawnParentPrefab;

        private IWorldStreamer _worldStreamer;
        private Transform _spawnParent;

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
            _worldStreamer = GetComponent<IWorldStreamer>();
            _spawnParent = _spawnParentPrefab != null
                ? Instantiate(_spawnParentPrefab).transform
                : new GameObject($"{name}_Spawns").transform;
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

        private void HandleChunkDrawn(Vector2Int chunkPos, WorldObjectDatabase worldObjectDatabase)
        {
            if (!_worldStreamer.TryGetChunk(chunkPos, out ChunkSnapshot snapshot))
                return;

            _chunkSpawns[chunkPos] = new List<(WorldSpawn, GameObject)>();

            _chunkGeneration.TryGetValue(chunkPos, out int gen);
            gen++;
            _chunkGeneration[chunkPos] = gen;

            int chunkSize = snapshot.ChunkSize;
            TileDatabase tileDatabase = snapshot.Template.TileDatabase;
            int chunkOriginX = chunkPos.x * chunkSize;
            int chunkOriginY = chunkPos.y * chunkSize;

            // GameObjectTile cells: prefab is supplied by the tile itself.
            for (int y = 0; y < chunkSize; y++)
            {
                for (int x = 0; x < chunkSize; x++)
                {
                    int tileKey = snapshot.GetTileKey(x, y);
                    if (tileKey < 0) continue;
                    if (!tileDatabase.TryGetTile(tileKey, out UnityEngine.Tilemaps.Tile tile)) continue;
                    if (tile is not GameObjectTile gameObjectTile) continue;

                    Vector2Int worldTilePos = new Vector2Int(chunkOriginX + x, chunkOriginY + y);
                    GameObject prefab = gameObjectTile.GetGameObject(worldTilePos);
                    if (prefab == null) continue;

                    // Cell-center so a tile at (x, y) spawns in the middle of its cell.
                    Vector2 worldPos = new Vector2(chunkOriginX + x + 0.5f, chunkOriginY + y + 0.5f);
                    _spawnQueue.Enqueue(new SpawnRequest
                    {
                        ChunkPos = chunkPos,
                        Generation = gen,
                        Prefab = prefab,
                        Position = worldPos,
                    });
                }
            }

            // Stored world-object spawns: resolve prefab id through our database.
            IReadOnlyList<ChunkData.WorldObjectSpawn> stored = snapshot.WorldObjects;
            for (int i = 0; i < stored.Count; i++)
            {
                ChunkData.WorldObjectSpawn entry = stored[i];
                if (!worldObjectDatabase.TryGetObject(entry.prefabId, out GameObject prefab))
                    continue;

                Vector2 worldPos = new Vector2(chunkOriginX, chunkOriginY) + entry.localPosition;
                _spawnQueue.Enqueue(new SpawnRequest
                {
                    ChunkPos = chunkPos,
                    Generation = gen,
                    Prefab = prefab,
                    Position = worldPos,
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
                        var instance = Instantiate(prefab, _spawnParent);
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
