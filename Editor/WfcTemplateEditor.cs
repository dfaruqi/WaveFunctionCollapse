using AYellowpaper.SerializedCollections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;


namespace MagusStudios.WaveFunctionCollapse
{
    [CustomEditor(typeof(WfcTemplate))]
    public class WfcTemplateEditor : Editor
    {
        private WfcTemplate _template;
        private Vector2 scrollPosition;
        private SerializedProperty tileDatabaseProperty;
        private SerializedProperty tileRulesProperty;
        private SerializedProperty defaultTileIdProperty;
        private SerializedProperty weightsProperty;

        private Dictionary<int, bool> neighborFoldouts = new Dictionary<int, bool>();

        private void OnEnable()
        {
            _template = (WfcTemplate)target;
            defaultTileIdProperty = serializedObject.FindProperty("DefaultTileKey");
            tileRulesProperty = serializedObject.FindProperty("TileRules");
            tileDatabaseProperty = serializedObject.FindProperty("TileDatabase");
            weightsProperty = serializedObject.FindProperty("Weights");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(tileDatabaseProperty);
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(tileRulesProperty);
            EditorGUILayout.Space();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(weightsProperty);
            if (GUILayout.Button("New", GUILayout.Width(64)))
            {
                CreateAndAssignWeightsAsset(copyFromExisting: false);
            }
            using (new EditorGUI.DisabledScope(_template.Weights == null))
            {
                if (GUILayout.Button("Copy", GUILayout.Width(64)))
                {
                    CreateAndAssignWeightsAsset(copyFromExisting: true);
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(defaultTileIdProperty);
            EditorGUILayout.Space();

            if (GUILayout.Button("Scan Active Tilemap and Overwrite"))
            {
                Tilemap targetTilemap = FindFirstObjectByType<Tilemap>(FindObjectsInactive.Exclude);

                if (targetTilemap == null)
                {
                    Debug.LogError("No active tilemap found in the scene.");
                }
                else
                {
                    _template.ScanTilemapAndOverwrite(targetTilemap);
                    EditorUtility.SetDirty(_template);
                    EditorUtility.SetDirty(_template.TileRules);
                }
            }

            EditorGUILayout.Space();

            if (_template.TileDatabase == null)
            {
                EditorGUILayout.HelpBox("Tile Database is not assigned. Please assign a TileDatabase to view sprites.",
                    MessageType.Warning);
            }

            if (_template.TileRules == null)
            {
                EditorGUILayout.HelpBox("Tile Rules is not assigned. Please assign a WfcTileRules asset to add or edit modules.",
                    MessageType.Warning);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            SerializedDictionary<int, WfcTileRules.AllowedNeighbors> modules = _template.TileRules.Modules;
            if (modules == null)
            {
                modules = new SerializedDictionary<int, WfcTileRules.AllowedNeighbors>();
                _template.TileRules.Modules = modules;
            }

            EditorGUILayout.LabelField("Tile Modules", EditorStyles.boldLabel);

            bool wasModified = false;

            if (modules.Count == 0)
            {
                EditorGUILayout.HelpBox("No modules defined. Drag tiles into the box below to add new modules.", MessageType.Info);
            }
            else
            {
                scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

                List<int> keys = new List<int>(modules.Keys);

                foreach (int tileKey in keys)
                {
                    if (DrawTileModule(tileKey, modules[tileKey]))
                        wasModified = true;
                }

                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.Space();
            if (DrawAddModuleDropArea(modules))
                wasModified = true;

            serializedObject.ApplyModifiedProperties();

            if (wasModified)
            {
                EditorUtility.SetDirty(_template);
                EditorUtility.SetDirty(_template.TileRules);
                if (_template.Weights != null)
                    EditorUtility.SetDirty(_template.Weights);
            }
        }

        private bool DrawTileModule(int tileKey, WfcTileRules.AllowedNeighbors module)
        {
            bool modified = false;

            if (!neighborFoldouts.ContainsKey(tileKey))
                neighborFoldouts[tileKey] = false;

            EditorGUILayout.BeginVertical("box");

            EditorGUILayout.BeginHorizontal();

            // Left: foldout + weight
            EditorGUILayout.BeginVertical();

            neighborFoldouts[tileKey] = EditorGUILayout.Foldout(
                neighborFoldouts[tileKey],
                $"Tile Key: {tileKey}",
                true,
                EditorStyles.foldoutHeader
            );

            if (_template.Weights != null)
            {
                EditorGUI.BeginChangeCheck();
                bool weightAssigned = _template.Weights.TryGetWeight(tileKey, out float weight);
                string weightLabel = weightAssigned ? "Weight" : "Weight (none assigned)";
                float newWeight = EditorGUILayout.FloatField(weightLabel, weight);

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(_template.Weights, "Modify Tile Weight");
                    _template.Weights[tileKey] = newWeight;
                    EditorUtility.SetDirty(_template.Weights);
                    modified = true;
                }
            }

            EditorGUILayout.EndVertical();

            // Right: sprite preview + name
            DrawSpritePreview(tileKey);

            EditorGUILayout.EndHorizontal();

            if (neighborFoldouts[tileKey])
            {
                EditorGUILayout.Space();
                EditorGUI.indentLevel++;
                if (DrawCompatibleNeighborsWithSprites(module))
                    modified = true;
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();

            return modified;
        }

        private void CreateAndAssignWeightsAsset(bool copyFromExisting)
        {
            WfcWeights source = copyFromExisting ? _template.Weights : null;
            string folder = WfcEditorUtils.GetActiveProjectFolder();
            string baseName = source != null ? $"{source.name}_Copy" : $"{_template.name}_Weights";
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{baseName}.asset");

            WfcWeights weights;
            if (source != null)
            {
                weights = Object.Instantiate(source);
                AssetDatabase.CreateAsset(weights, path);
            }
            else
            {
                weights = ScriptableObject.CreateInstance<WfcWeights>();
                AssetDatabase.CreateAsset(weights, path);
            }
            AssetDatabase.SaveAssets();

            weightsProperty.objectReferenceValue = weights;
            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(_template);

            EditorGUIUtility.PingObject(weights);
            string title = source != null ? "WfcWeights Copied" : "WfcWeights Created";
            string body = source != null
                ? $"Copied \"{source.name}\" to:\n{path}\n\nIt has been assigned to template \"{_template.name}\"."
                : $"Created new WfcWeights asset at:\n{path}\n\nIt has been assigned to template \"{_template.name}\".";
            EditorUtility.DisplayDialog(title, body, "OK");
        }

        private void DrawSpritePreview(int tileKey)
        {
            if (_template.TileDatabase == null) return;

            if (_template.TileDatabase.TryGetTile(tileKey, out Tile tile) && tile != null)
            {
                if (tile.sprite != null)
                {
                    Texture2D texture = AssetPreview.GetAssetPreview(tile.sprite);
                    if (texture != null)
                        GUILayout.Label(texture, GUILayout.Width(40), GUILayout.Height(40));
                    else
                        GUILayout.Label("Loading...", GUILayout.Width(40), GUILayout.Height(40));

                    EditorGUILayout.LabelField(tile.name, GUILayout.Width(100));
                }
                else
                {
                    GUILayout.Label("No Sprite", GUILayout.Width(40), GUILayout.Height(40));
                }
            }
            else
            {
                GUILayout.Label("Not Found", GUILayout.Width(40), GUILayout.Height(40));
            }
        }

        private bool DrawCompatibleNeighborsWithSprites(WfcTileRules.AllowedNeighbors module)
        {
            bool modified = false;

            if (module.Neighbors == null) return false;

            SerializedDictionary<Direction, SerializedHashSet<int>> compatibleNeighbors = module.Neighbors;

            foreach (Direction direction in System.Enum.GetValues(typeof(Direction)))
            {
                if (!compatibleNeighbors.ContainsKey(direction))
                    compatibleNeighbors[direction] = new SerializedHashSet<int>();

                SerializedHashSet<int> neighborSet = compatibleNeighbors[direction];

                Rect boxRect = EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField($"{direction}:", EditorStyles.miniLabel);

                List<int> compatibleTiles = neighborSet.ToList();
                if (compatibleTiles.Count == 0)
                {
                    EditorGUILayout.LabelField("(drop tiles here)", EditorStyles.centeredGreyMiniLabel);
                }
                else
                {
                    int spriteCount = compatibleTiles.Count;
                    int maxSpritesPerRow = Mathf.Max(1, Mathf.FloorToInt(EditorGUIUtility.currentViewWidth / 45f));
                    int rowsNeeded = Mathf.CeilToInt((float)spriteCount / maxSpritesPerRow);

                    int currentIndex = 0;
                    for (int row = 0; row < rowsNeeded; row++)
                    {
                        EditorGUILayout.BeginHorizontal();

                        int spritesThisRow = Mathf.Min(maxSpritesPerRow, spriteCount - currentIndex);
                        GUILayout.FlexibleSpace();

                        for (int i = 0; i < spritesThisRow; i++)
                        {
                            DrawSmallNeighborSprite(compatibleTiles[currentIndex]);
                            currentIndex++;

                            if (i < spritesThisRow - 1)
                                GUILayout.Space(5);
                        }

                        GUILayout.FlexibleSpace();
                        EditorGUILayout.EndHorizontal();
                    }
                }

                EditorGUILayout.EndVertical();

                if (HandleTileDragAndDrop(boxRect, out List<int> droppedKeys))
                {
                    List<int> newKeys = droppedKeys.Where(k => !neighborSet.Contains(k)).ToList();
                    if (newKeys.Count > 0)
                    {
                        Undo.RecordObject(_template.TileRules, "Add Allowed Neighbor");
                        foreach (int k in newKeys)
                            neighborSet.Add(k);
                        modified = true;
                    }
                }
            }

            return modified;
        }

        private bool DrawAddModuleDropArea(SerializedDictionary<int, WfcTileRules.AllowedNeighbors> modules)
        {
            EditorGUILayout.LabelField("Add Module", EditorStyles.boldLabel);

            Rect dropArea = GUILayoutUtility.GetRect(0, 48, GUILayout.ExpandWidth(true));
            GUI.Box(dropArea, "Drag tile(s) here to add new module entries", EditorStyles.helpBox);

            if (!HandleTileDragAndDrop(dropArea, out List<int> droppedKeys))
                return false;

            Undo.RecordObject(_template.TileRules, "Add Module");

            bool modified = false;
            foreach (int key in droppedKeys)
            {
                if (modules.ContainsKey(key))
                {
                    Debug.Log($"Module for tile key {key} already exists.");
                    continue;
                }

                modules[key] = new WfcTileRules.AllowedNeighbors
                {
                    Neighbors = new SerializedDictionary<Direction, SerializedHashSet<int>>
                    {
                        { Direction.Up, new SerializedHashSet<int>() },
                        { Direction.Down, new SerializedHashSet<int>() },
                        { Direction.Left, new SerializedHashSet<int>() },
                        { Direction.Right, new SerializedHashSet<int>() },
                    }
                };

                if (_template.Weights != null && !_template.Weights.TryGetWeight(key, out _))
                {
                    Undo.RecordObject(_template.Weights, "Add Module Weight");
                    _template.Weights[key] = _template.Weights.DefaultWeight;
                }

                neighborFoldouts[key] = true;
                modified = true;
            }

            return modified;
        }

        /// <summary>
        /// Handles drag-and-drop of Tile assets into the given drop area.
        /// Logs an error and skips any dragged Tile that is not in the assigned TileDatabase.
        /// Never modifies the TileDatabase itself.
        /// </summary>
        private bool HandleTileDragAndDrop(Rect dropArea, out List<int> droppedTileKeys)
        {
            droppedTileKeys = null;

            Event evt = Event.current;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)
                return false;
            if (!dropArea.Contains(evt.mousePosition))
                return false;

            bool anyTileDragged = DragAndDrop.objectReferences.Any(o => o is Tile);
            if (!anyTileDragged)
                return false;

            DragAndDrop.visualMode = (_template.TileDatabase != null)
                ? DragAndDropVisualMode.Copy
                : DragAndDropVisualMode.Rejected;

            if (evt.type != EventType.DragPerform)
                return false;

            DragAndDrop.AcceptDrag();
            evt.Use();

            if (_template.TileDatabase == null)
            {
                Debug.LogError("Cannot resolve dragged tile(s): TileDatabase is not assigned to this template.");
                return false;
            }

            droppedTileKeys = new List<int>();
            foreach (Object obj in DragAndDrop.objectReferences)
            {
                if (!(obj is Tile tile)) continue;

                if (_template.TileDatabase.TryGetKeyFromMapTile(tile, out int key))
                {
                    droppedTileKeys.Add(key);
                }
                else
                {
                    Debug.LogError(
                        $"Tile \"{tile.name}\" is not in the TileDatabase \"{_template.TileDatabase.name}\". " +
                        "Add it to the TileDatabase first — the template editor will not modify the database.");
                }
            }

            return droppedTileKeys.Count > 0;
        }

        private void DrawSmallNeighborSprite(int tileKey)
        {
            if (_template.TileDatabase == null) return;

            if (_template.TileDatabase.TryGetTile(tileKey, out Tile tile) && tile != null)
            {
                if (tile.sprite != null)
                {
                    Texture2D texture = AssetPreview.GetAssetPreview(tile.sprite);
                    if (texture != null)
                    {
                        GUIContent content = new GUIContent(texture, $"{tileKey}: {tile.sprite.name}");
                        GUILayout.Label(content, GUILayout.Width(40), GUILayout.Height(40));
                    }
                    else
                    {
                        GUILayout.Box(tileKey.ToString(), GUILayout.Width(40), GUILayout.Height(40));
                    }
                }
                else
                {
                    GUILayout.Box(tileKey.ToString(), GUILayout.Width(40), GUILayout.Height(40));
                }
            }
            else
            {
                GUILayout.Box("?", GUILayout.Width(40), GUILayout.Height(40));
            }
        }
    }
}
