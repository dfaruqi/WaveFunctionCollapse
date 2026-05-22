using AYellowpaper.SerializedCollections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MagusStudios.WaveFunctionCollapse
{

    [CustomEditor(typeof(TileDatabase))]
    public class TileDatabaseEditor : Editor
    {
        private TileDatabase _tileDatabase;
        private Vector2 scrollPosition;
        private Dictionary<int, bool> foldoutStates = new Dictionary<int, bool>();
        private Dictionary<Tile, Editor> tileEditors = new Dictionary<Tile, Editor>();
        private int newKeyInput = 0;
        private Tile newTileInput;

        // Style for the preview area
        private GUIStyle previewStyle;

        private void OnEnable()
        {
            _tileDatabase = (TileDatabase)target;
        }

        private void OnDisable()
        {
            foreach (var editor in tileEditors.Values)
            {
                if (editor != null)
                    DestroyImmediate(editor);
            }
            tileEditors.Clear();
        }

        private Editor GetTileEditor(Tile tile)
        {
            if (!tileEditors.TryGetValue(tile, out Editor editor) || editor == null)
            {
                editor = CreateEditor(tile);
                tileEditors[tile] = editor;
            }
            return editor;
        }

        public override void OnInspectorGUI()
        {
            previewStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                fixedHeight = 80,
                fixedWidth = 80
            };

            _tileDatabase = (TileDatabase)target;

            EditorGUILayout.Space();

            // Draw dictionary entries
            DrawDictionary();

            // Add new entry section
            DrawAddNewEntry();

            // Additional utility buttons
            DrawUtilityButtons();

            // Apply any changes
            if (GUI.changed)
            {
                EditorUtility.SetDirty(_tileDatabase);
            }
        }

        private void DrawDictionary()
        {
            EditorGUILayout.LabelField("Tile Dictionary", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            if (_tileDatabase.Tiles == null)
            {
                EditorGUILayout.HelpBox("Tiles dictionary is null.", MessageType.Error);
                return;
            }

            EditorGUILayout.LabelField($"Total Tiles: {_tileDatabase.Tiles.Count}");
            EditorGUILayout.Space();

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(720));

            // Draw each dictionary entry
            var keys = _tileDatabase.Tiles.Keys.ToList();
            foreach (int key in keys)
            {
                DrawDictionaryEntry(key);
            }

            if (_tileDatabase.Tiles.Count == 0)
            {
                EditorGUILayout.HelpBox("No tiles in database. Add some tiles using the form below.", MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawDictionaryEntry(int key)
        {
            if (!_tileDatabase.Tiles.TryGetValue(key, out Tile tile))
                return;

            if (tile == null)
            {
                EditorGUILayout.BeginVertical(GUI.skin.box);
                EditorGUILayout.LabelField($"Key: {key} - NULL TILE (Missing Reference)");
                if (GUILayout.Button("Remove Broken Entry"))
                {
                    _tileDatabase.Tiles.Remove(key);
                    foldoutStates.Remove(key);
                    EditorUtility.SetDirty(_tileDatabase);
                }
                EditorGUILayout.EndVertical();
                return;
            }

            // Ensure foldout state exists for this key
            if (!foldoutStates.ContainsKey(key))
                foldoutStates[key] = false;

            // Header with key and tile name
            EditorGUILayout.BeginVertical(GUI.skin.box);

            EditorGUILayout.BeginHorizontal();

            // Foldout arrow
            foldoutStates[key] = EditorGUILayout.Foldout(foldoutStates[key], $"Key: {key}", true);

            // Tile name
            EditorGUILayout.LabelField(tile.name, GUILayout.ExpandWidth(true));

            // Sprite preview (small)
            if (tile.sprite != null)
            {
                Rect previewRect = GUILayoutUtility.GetRect(40, 40, GUILayout.Width(40));
                DrawSpritePreview(previewRect, tile.sprite);
            }
            else
            {
                GUILayout.Label("No Sprite", GUILayout.Width(60));
            }

            // Remove button
            if (GUILayout.Button("X", GUILayout.Width(20)))
            {
                _tileDatabase.Tiles.Remove(key);
                foldoutStates.Remove(key);
                EditorUtility.SetDirty(_tileDatabase);
                return;
            }

            EditorGUILayout.EndHorizontal();

            // Expanded content
            if (foldoutStates[key])
            {
                EditorGUILayout.Space();

                // Key field (editable)
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Key:", GUILayout.Width(50));
                int newKey = EditorGUILayout.IntField(key);

                if (newKey != key)
                {
                    if (!_tileDatabase.Tiles.ContainsKey(newKey))
                    {
                        // Update the key by removing and re-adding
                        _tileDatabase.Tiles.Remove(key);
                        _tileDatabase.Tiles[newKey] = tile;

                        // Update foldout state
                        foldoutStates[newKey] = foldoutStates[key];
                        foldoutStates.Remove(key);

                        EditorUtility.SetDirty(_tileDatabase);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox($"Key {newKey} already exists!", MessageType.Warning);
                    }
                }
                EditorGUILayout.EndHorizontal();

                // Tile reference field (editable — drag a different tile in to replace)
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Tile:", GUILayout.Width(50));
                Tile newTile = (Tile)EditorGUILayout.ObjectField(tile, typeof(Tile), false);
                EditorGUILayout.EndHorizontal();

                if (newTile != tile)
                {
                    _tileDatabase.Tiles[key] = newTile;
                    EditorUtility.SetDirty(_tileDatabase);
                    EditorGUILayout.EndVertical();
                    return;
                }

                // Tile properties — mirror what the default inspector shows for the Tile asset
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Tile Properties:", EditorStyles.boldLabel);

                Editor tileEditor = GetTileEditor(tile);
                if (tileEditor != null)
                {
                    tileEditor.OnInspectorGUI();
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawSpritePreview(Rect position, Sprite sprite)
        {
            if (sprite == null)
                return;

            Texture2D preview = AssetPreview.GetAssetPreview(sprite);
            if (preview != null)
            {
                GUI.DrawTexture(position, preview, ScaleMode.ScaleToFit, true);
            }
            else
            {
                Texture2D miniThumb = AssetPreview.GetMiniThumbnail(sprite);
                if (miniThumb != null)
                    GUI.DrawTexture(position, miniThumb, ScaleMode.ScaleToFit, true);
            }

            GUI.Box(position, GUIContent.none, previewStyle);
        }

        private void DrawAddNewEntry()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Add New Tile", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(GUI.skin.box);

            EditorGUILayout.HelpBox("Enter a unique key and assign a Tile to add it to the dictionary.", MessageType.Info);

            newKeyInput = EditorGUILayout.IntField("Key:", newKeyInput);
            newTileInput = (Tile)EditorGUILayout.ObjectField("Tile:", newTileInput, typeof(Tile), false);

            EditorGUILayout.BeginHorizontal();

            bool canAdd = newTileInput != null && !_tileDatabase.Tiles.ContainsKey(newKeyInput);
            EditorGUI.BeginDisabledGroup(!canAdd);

            if (GUILayout.Button("Add Tile"))
            {
                _tileDatabase.Tiles[newKeyInput] = newTileInput;
                foldoutStates[newKeyInput] = true;
                newTileInput = null;
                newKeyInput = 0;
                EditorUtility.SetDirty(_tileDatabase);
            }

            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button("Clear"))
            {
                newTileInput = null;
                newKeyInput = 0;
            }

            EditorGUILayout.EndHorizontal();

            if (newTileInput != null && _tileDatabase.Tiles.ContainsKey(newKeyInput))
            {
                EditorGUILayout.HelpBox($"Key {newKeyInput} already exists! Please choose a different key.", MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawUtilityButtons()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Utilities", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(GUI.skin.box);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Sort by Key"))
            {
                SortDictionaryByKey();
            }

            if (GUILayout.Button("Validate Database"))
            {
                ValidateDatabase();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Find Duplicate Tiles"))
            {
                FindDuplicateTiles();
            }

            if (GUILayout.Button("Find Missing Tiles"))
            {
                FindMissingTiles();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Clear All"))
            {
                if (EditorUtility.DisplayDialog("Clear All Tiles",
                    "Are you sure you want to remove all tiles from the database?",
                    "Yes", "No"))
                {
                    _tileDatabase.Tiles.Clear();
                    foldoutStates.Clear();
                    EditorUtility.SetDirty(_tileDatabase);
                }
            }

            if (GUILayout.Button("Remove Null Entries"))
            {
                RemoveNullEntries();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Overwrite from Tilemap"))
            {
                if (EditorUtility.DisplayDialog("Overwrite database from Tilemap",
                    "Are you sure you want clear the database and fill it with tiles from the tilemap?",
                    "Yes", "No"))
                {

                    _tileDatabase.ScanTilemapAndOverwrite(GetActiveTilemap());
                    EditorUtility.SetDirty(_tileDatabase);
                }
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void SortDictionaryByKey()
        {
            var sortedEntries = _tileDatabase.Tiles.OrderBy(kvp => kvp.Key).ToList();
            _tileDatabase.Tiles.Clear();

            foreach (var entry in sortedEntries)
            {
                _tileDatabase.Tiles[entry.Key] = entry.Value;
            }

            EditorUtility.SetDirty(_tileDatabase);
            Debug.Log("Tile database sorted by key.");
        }

        private void ValidateDatabase()
        {
            int nullCount = 0;
            int duplicateKeys = 0;

            if (_tileDatabase.Tiles != null)
            {
                var keys = new HashSet<int>();

                foreach (var kvp in _tileDatabase.Tiles)
                {
                    if (kvp.Value == null)
                        nullCount++;

                    if (!keys.Add(kvp.Key))
                        duplicateKeys++;
                }
            }

            string message = "Database Validation Results:\n";
            message += $"Total Entries: {_tileDatabase.Tiles.Count}\n";
            message += $"Null Tiles: {nullCount}\n";
            message += $"Duplicate Keys: {duplicateKeys}";

            EditorUtility.DisplayDialog("Validation Complete", message, "OK");
        }

        private void FindDuplicateTiles()
        {
            if (_tileDatabase.Tiles == null) return;

            var tileCounts = new Dictionary<string, List<int>>();

            foreach (var kvp in _tileDatabase.Tiles)
            {
                if (kvp.Value != null)
                {
                    string tileName = kvp.Value.name;
                    if (!tileCounts.ContainsKey(tileName))
                        tileCounts[tileName] = new List<int>();

                    tileCounts[tileName].Add(kvp.Key);
                }
            }

            var duplicates = tileCounts.Where(x => x.Value.Count > 1).ToList();

            if (duplicates.Count == 0)
            {
                EditorUtility.DisplayDialog("No Duplicates", "No duplicate tiles found in the database.", "OK");
            }
            else
            {
                string message = "Duplicate Tiles Found:\n\n";
                foreach (var duplicate in duplicates)
                {
                    message += $"{duplicate.Key} (Keys: {string.Join(", ", duplicate.Value)})\n";
                }
                EditorUtility.DisplayDialog("Duplicates Found", message, "OK");
            }
        }

        private void FindMissingTiles()
        {
            if (_tileDatabase.Tiles == null) return;

            var missingTiles = _tileDatabase.Tiles.Where(kvp => kvp.Value == null)
                                                .Select(kvp => kvp.Key)
                                                .ToList();

            if (missingTiles.Count == 0)
            {
                EditorUtility.DisplayDialog("No Missing Tiles", "All tile references are valid.", "OK");
            }
            else
            {
                string message = $"Missing Tile References ({missingTiles.Count}):\n\n";
                message += $"Keys: {string.Join(", ", missingTiles)}";
                EditorUtility.DisplayDialog("Missing Tiles Found", message, "OK");
            }
        }

        private void RemoveNullEntries()
        {
            int removedCount = 0;
            var keysToRemove = new List<int>();

            foreach (var kvp in _tileDatabase.Tiles)
            {
                if (kvp.Value == null)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (int key in keysToRemove)
            {
                _tileDatabase.Tiles.Remove(key);
                foldoutStates.Remove(key);
                removedCount++;
            }

            if (removedCount > 0)
            {
                EditorUtility.SetDirty(_tileDatabase);
                Debug.Log($"Removed {removedCount} null entries from tile database.");
            }

            EditorUtility.DisplayDialog("Cleanup Complete",
                removedCount > 0 ? $"Removed {removedCount} null entries." : "No null entries found.",
                "OK");
        }

        /// <summary>
        /// Returns null if no tilemap found.
        /// </summary>
        /// <returns></returns>
        private Tilemap GetActiveTilemap()
        {
            // Get the first tilemap in the scene
            Tilemap targetTilemap = FindFirstObjectByType<Tilemap>(FindObjectsInactive.Exclude);

            if (targetTilemap == null)
            {
                Debug.LogError("No active tilemap found in the scene.");
                return null;
            }

            return targetTilemap;
        }
    }
}
