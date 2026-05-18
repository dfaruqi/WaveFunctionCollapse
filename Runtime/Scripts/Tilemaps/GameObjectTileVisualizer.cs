using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.SceneManagement;
#endif

namespace MagusStudios.WaveFunctionCollapse
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class GameObjectTileVisualizer : MonoBehaviour
    {
#if UNITY_EDITOR
        private readonly Dictionary<(Tilemap, Vector3Int), GameObject> _spawned =
            new Dictionary<(Tilemap, Vector3Int), GameObject>();

        private void OnEnable()
        {
            if (Application.isPlaying)
            {
                ClearAll();
                return;
            }

            Tilemap.tilemapTileChanged += OnTilemapTileChanged;
            RefreshAll();
        }

        private void OnDisable()
        {
            Tilemap.tilemapTileChanged -= OnTilemapTileChanged;
            ClearAll();
        }

        private void OnDestroy()
        {
            ClearAll();
        }

        private void OnTilemapTileChanged(Tilemap tilemap, Tilemap.SyncTile[] syncTiles)
        {
            if (tilemap == null) return;

            var positions = new Vector3Int[syncTiles.Length];
            for (int i = 0; i < syncTiles.Length; i++) positions[i] = syncTiles[i].position;


            if (this == null || tilemap == null) return;
            foreach (var pos in positions)
            {
                UpdateCell(tilemap, pos, tilemap.GetTile(pos) as GameObjectTile);
            }
        }

        private void RefreshAll()
        {
            ClearAll();

            foreach (var tilemap in FindObjectsByType<Tilemap>(FindObjectsSortMode.None))
            {
                var bounds = tilemap.cellBounds;
                foreach (var pos in bounds.allPositionsWithin)
                {
                    var tile = tilemap.GetTile(pos) as GameObjectTile;
                    if (tile != null) UpdateCell(tilemap, pos, tile);
                }
            }
        }

        private void UpdateCell(Tilemap tilemap, Vector3Int position, GameObjectTile tile)
        {
            var key = (tilemap, position);
            if (_spawned.TryGetValue(key, out var existing) && existing != null)
            {
                DestroyImmediate(existing);
                _spawned.Remove(key);
            }

            if (tile == null) return;

            var prefab = tile.GetGameObject(new Vector2Int(position.x, position.y));
            if (prefab == null) return;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, transform);
            if (instance == null) return;

            instance.transform.position = tilemap.GetCellCenterWorld(position);
            instance.hideFlags = HideFlags.DontSave | HideFlags.HideInHierarchy;
            _spawned[key] = instance;
        }

        private void ClearAll()
        {
            foreach (var go in _spawned.Values)
            {
                if (go != null) DestroyImmediate(go);
            }

            _spawned.Clear();
        }
#endif
    }
}