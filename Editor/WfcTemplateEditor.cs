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
            EditorGUILayout.PropertyField(weightsProperty);
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

            if (_template.TileRules == null || _template.TileRules.Modules.Count == 0)
            {
                EditorGUILayout.HelpBox("No modules defined.", MessageType.Info);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            SerializedDictionary<int, WfcTileRules.AllowedNeighbors> modules = _template.TileRules.Modules;

            if (_template.TileDatabase == null)
            {
                EditorGUILayout.HelpBox("Tile Database is not assigned. Please assign a TileDatabase to view sprites.",
                    MessageType.Warning);
            }

            EditorGUILayout.LabelField("Tile Modules", EditorStyles.boldLabel);

            bool wasModified = false;

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            List<int> keys = new List<int>(modules.Keys);

            foreach (int tileKey in keys)
            {
                if (DrawTileModule(tileKey, modules[tileKey]))
                    wasModified = true;
            }

            EditorGUILayout.EndScrollView();

            serializedObject.ApplyModifiedProperties();

            if (wasModified)
                EditorUtility.SetDirty(_template);
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

            if (neighborFoldouts[tileKey] && module.Neighbors != null)
            {
                EditorGUILayout.Space();
                EditorGUI.indentLevel++;
                DrawCompatibleNeighborsWithSprites(module.Neighbors);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();

            return modified;
        }

        private void DrawSpritePreview(int tileKey)
        {
            if (_template.TileDatabase == null) return;

            if (_template.TileDatabase.TryGetTile(tileKey, out Tile tile))
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

        private void DrawCompatibleNeighborsWithSprites(
            SerializedDictionary<Direction, SerializedHashSet<int>> compatibleNeighbors)
        {
            foreach (Direction direction in System.Enum.GetValues(typeof(Direction)))
            {
                if (!compatibleNeighbors.ContainsKey(direction)) continue;

                var compatibleTiles = compatibleNeighbors[direction].ToList();
                if (compatibleTiles == null || compatibleTiles.Count == 0) continue;

                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField($"{direction}:", EditorStyles.miniLabel);

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

                EditorGUILayout.EndVertical();
            }
        }

        private void DrawSmallNeighborSprite(int tileKey)
        {
            if (_template.TileDatabase == null) return;

            if (_template.TileDatabase.TryGetTile(tileKey, out Tile tile))
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