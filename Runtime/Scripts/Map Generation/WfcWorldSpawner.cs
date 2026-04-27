using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

namespace MagusStudios.WaveFunctionCollapse
{
    [RequireComponent(typeof(WfcWorldStreamer))]
    public class WfcWorldSpawner : MonoBehaviour
    {
        [SerializeField] private WfcWorldStreamer worldStreamer;

        private Dictionary<GameObject, ObjectPool<WorldSpawn>> _spawnPools =
            new Dictionary<GameObject, ObjectPool<WorldSpawn>>();

        // Tracks all spawned objects per chunk so we can release them on undrawn
        private Dictionary<Vector2Int, List<(WorldSpawn spawn, GameObject prefab)>> _chunkSpawns =
            new Dictionary<Vector2Int, List<(WorldSpawn, GameObject)>>();

        private void OnEnable()
        {
            worldStreamer.OnChunkDrawn += HandleChunkDrawn;
            worldStreamer.OnChunkUndrawn += HandleChunkUndrawn;
        }

        private void OnDisable()
        {
            worldStreamer.OnChunkDrawn -= HandleChunkDrawn;
            worldStreamer.OnChunkUndrawn -= HandleChunkUndrawn;
        }

        private void HandleChunkDrawn(Vector2Int chunkPos, IReadOnlyList<int> chunkData)
        {
            int chunkSize = WfcWorldStreamer.CHUNK_SIZE;
            List<(WorldSpawn, GameObject)> spawns = new List<(WorldSpawn, GameObject)>();

            for (int i = 0; i < chunkData.Count; i++)
            {
                var tile = worldStreamer.Template.TileDatabase[chunkData[i]];
                if (tile is not GameObjectTile gameObjectTile)
                    continue;

                int localX = i % chunkSize;
                int localY = i / chunkSize;
                Vector2Int cellWorldPos = TileUtils.GetWorldPosition(chunkPos, new Vector2Int(localX, localY), chunkSize);
                Vector2 cellCenterPos = TileUtils.GetTileCenterPosition(cellWorldPos);
                
                GameObject prefab = gameObjectTile.Prefab;
                ObjectPool<WorldSpawn> pool = GetOrCreatePool(prefab);
                WorldSpawn spawn = pool.Get();
                spawn.transform.position = cellCenterPos;
                spawns.Add((spawn, prefab));
            }

            _chunkSpawns[chunkPos] = spawns;
        }

        private void HandleChunkUndrawn(Vector2Int chunkPos)
        {
            if (!_chunkSpawns.TryGetValue(chunkPos, out var spawns))
                return;

            foreach (var (spawn, prefab) in spawns)
                _spawnPools[prefab].Release(spawn);

            _chunkSpawns.Remove(chunkPos);
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
                    maxSize: 1000
                );
                _spawnPools[prefab] = pool;
            }

            return pool;
        }
    }
}