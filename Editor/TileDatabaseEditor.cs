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
        private int newKeyInput = 0;
        private Tile newTileInput;

        // Style for the preview area
        private GUIStyle previewStyle;

        private void OnEnable()
        {
            _tileDatabase = (TileDatabase)target;
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

                // Large sprite preview
                if (tile.sprite != null)
                {
                    EditorGUILayout.LabelField("Sprite Preview:", EditorStyles.boldLabel);
                    Rect largePreviewRect = GUILayoutUtility.GetRect(100, 100, GUILayout.Height(100));
                    DrawSpritePreview(largePreviewRect, tile.sprite);
                    EditorGUILayout.Space();
                }
                else
                {
                    EditorGUILayout.HelpBox("No sprite assigned to this tile.", MessageType.Warning);
                }

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

                // Tile properties using direct object field
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Tile Properties:", EditorStyles.boldLabel);

                // Create a temporary serialized object for the tile to use PropertyField
                SerializedObject tileSerializedObject = new SerializedObject(tile);
                tileSerializedObject.Update();

                SerializedProperty iterator = tileSerializedObject.GetIterator();
                bool enterChildren = true;
                while (iterator.NextVisible(enterChildren))
                {
                    if (iterator.name == "m_Script") // Skip the script reference
                        continue;

                    EditorGUILayout.PropertyField(iterator, true);
                    enterChildren = false;
                }

                if (tileSerializedObject.ApplyModifiedProperties())
                {
                    EditorUtility.SetDirty(tile);
                }

                // Quick access to test the database methods
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Debug Info:", EditorStyles.miniBoldLabel);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Test TryGetTile", EditorStyles.miniButton))
                {
                    if (_tileDatabase.TryGetTile(key, out Tile foundTile))
                    {
                        Debug.Log($"Successfully found tile with key {key}: {foundTile.name}");
                    }
                    else
                    {
                        Debug.LogWarning($"Failed to find tile with key {key}");
                    }
                }

                if (GUILayout.Button("Test GetKeyFromMapTile", EditorStyles.miniButton))
                {
                    try
                    {
                        int foundKey = _tileDatabase.GetKeyFromMapTile(tile);
                        Debug.Log($"Tile {tile.name} has key: {foundKey}");
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError($"Error getting key: {e.Message}");
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawSpritePreview(Rect position, Sprite sprite)
        {
            if (sprite == null || sprite.texture == null)
                return;

            Texture2D texture = sprite.texture;
            Rect spriteRect = sprite.rect;

            // Calculate UV coordinates for the sprite
            Rect uv = new Rect(
                spriteRect.x / texture.width,
                spriteRect.y / texture.height,
                spriteRect.width / texture.width,
                spriteRect.height / texture.height
            );

            // Draw the sprite preview
            GUI.DrawTextureWithTexCoords(position, texture, uv, true);

            // Draw border
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

                    ScanTilemapAndOverwrite(GetActiveTilemap());
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

        public void ScanTilemapAndOverwrite(Tilemap tilemap)
        {
            _tileDatabase.Tiles.Clear();

            int count = 0;
            foreach (Vector3Int pos in tilemap.cellBounds.allPositionsWithin)
            {
                TileBase tilebase = tilemap.GetTile(pos);
                if (_tileDatabase.TryGetKeyFromMapTile(tilebase, out int id)) continue;
                if (tilebase == null) continue;

                _tileDatabase.Tiles.Add(count, tilebase as Tile);
                count++;
            }
        }

        public void ScanTilemapAndAdd(Tilemap tilemap)
        {
            if (tilemap == null) return;
            if (_tileDatabase == null || _tileDatabase.Tiles == null) return;

            foreach (Vector3Int pos in tilemap.cellBounds.allPositionsWithin)
            {
                TileBase tilebase = tilemap.GetTile(pos);
                if (tilebase == null) continue;

                // Skip if tile already exists in the database
                if (_tileDatabase.TryGetKeyFromMapTile(tilebase, out int existingId))
                    continue;

                // Find the lowest unused integer key
                int newKey = 0;
                while (_tileDatabase.Tiles.ContainsKey(newKey))
                    newKey++;

                // Add tile at that key
                _tileDatabase.Tiles.Add(newKey, tilebase as Tile);
            }
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
