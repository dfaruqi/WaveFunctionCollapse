using AYellowpaper.SerializedCollections;
using MagusStudios.Arcanist.Tilemaps;
using MagusStudios.Arcanist.Utils;
using MagusStudios.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;


namespace MagusStudios.Arcanist.WaveFunctionCollapse
{

    [CustomEditor(typeof(WfcModuleSet))]
    public class WfcModuleSetEditor : Editor
    {
        private WfcModuleSet moduleSet;
        private Vector2 scrollPosition;
        private SerializedProperty tileDatabaseProperty;

        private void OnEnable()
        {
            moduleSet = (WfcModuleSet)target;
            tileDatabaseProperty = serializedObject.FindProperty("TileDatabase");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Draw TileDatabase field manually
            if (tileDatabaseProperty != null)
            {
                EditorGUILayout.PropertyField(tileDatabaseProperty);
            }
            else
            {
                // Fallback: use default property drawing but exclude Modules
                DrawPropertiesExcluding(serializedObject, "Modules");
            }

            EditorGUILayout.Space();

            if (GUILayout.Button("Scan Active Tilemap and Overwrite"))
            {
                // Get the first tilemap in the scene
                Tilemap targetTilemap = FindFirstObjectByType<Tilemap>(FindObjectsInactive.Exclude);

                if (targetTilemap == null)
                {
                    Debug.LogError("No active tilemap found in the scene.");
                }
                else
                    moduleSet.ScanTilemapAndOverwrite(targetTilemap);
            }

            EditorGUILayout.Space();

            if (moduleSet.Modules == null || moduleSet.Modules.Count == 0)
            {
                EditorGUILayout.HelpBox("No modules defined.", MessageType.Info);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            if (moduleSet.TileDatabase == null)
            {
                EditorGUILayout.HelpBox("Tile Database is not assigned. Please assign a TileDatabase to view sprites.", MessageType.Warning);
            }

            EditorGUILayout.LabelField("Tile Modules", EditorStyles.boldLabel);

            bool wasModified = false;

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            // Create a list of keys first to avoid modifying during iteration
            List<int> keys = new List<int>(moduleSet.Modules.Keys);

            foreach (int tileKey in keys)
            {
                if (DrawTileModule(tileKey, moduleSet.Modules[tileKey]))
                {
                    wasModified = true;
                }
                EditorGUILayout.Space();
            }

            EditorGUILayout.EndScrollView();

            serializedObject.ApplyModifiedProperties();

            if (wasModified)
            {
                EditorUtility.SetDirty(moduleSet);
            }
        }

        private bool DrawTileModule(int tileKey, WfcModuleSet.TileModule module)
        {
            bool modified = false;

            EditorGUILayout.BeginVertical("box");

            // Header section
            EditorGUILayout.BeginHorizontal();

            // Tile info
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField($"Tile Key: {tileKey}", EditorStyles.boldLabel);

            // Weight field - direct modification with undo
            EditorGUI.BeginChangeCheck();
            float newWeight = EditorGUILayout.FloatField("Weight", module.weight);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(moduleSet, "Modify Tile Weight");
                moduleSet.Modules[tileKey] = new WfcModuleSet.TileModule
                {
                    weight = newWeight,
                    compatibleNeighbors = module.compatibleNeighbors
                };
                modified = true;
            }

            EditorGUILayout.EndVertical();

            // Sprite preview
            DrawSpritePreview(tileKey);

            EditorGUILayout.EndHorizontal();

            // Compatible neighbors with sprites
            EditorGUILayout.Space();
            if (module.compatibleNeighbors != null)
            {
                DrawCompatibleNeighborsWithSprites(module.compatibleNeighbors);
            }

            EditorGUILayout.EndVertical();

            return modified;
        }

        private void DrawSpritePreview(int tileKey)
        {
            if (moduleSet.TileDatabase == null) return;

            if (moduleSet.TileDatabase.TryGetTile(tileKey, out Tile tile))
            {
                if (tile.sprite != null)
                {
                    Texture2D texture = AssetPreview.GetAssetPreview(tile.sprite);
                    if (texture != null)
                    {
                        GUILayout.Label(texture, GUILayout.Width(40), GUILayout.Height(40));
                    }
                    else
                    {
                        GUILayout.Label("Loading...", GUILayout.Width(40), GUILayout.Height(40));
                    }
                    EditorGUILayout.LabelField(tile.sprite.name, GUILayout.Width(100));
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

        private void DrawCompatibleNeighborsWithSprites(SerializedDictionary<Direction, SerializedHashSet<int>> compatibleNeighbors)
        {
            EditorGUILayout.LabelField("Allowed Neighbors:", EditorStyles.miniBoldLabel);

            foreach (Direction direction in System.Enum.GetValues(typeof(Direction)))
            {
                if (!compatibleNeighbors.ContainsKey(direction)) continue;

                var compatibleTiles = compatibleNeighbors[direction].ToList();
                if (compatibleTiles == null || compatibleTiles.Count == 0) continue;

                EditorGUILayout.BeginVertical("box");

                // Direction label
                EditorGUILayout.LabelField($"{direction}:", EditorStyles.miniLabel);

                // Calculate flexible layout
                int spriteCount = compatibleTiles.Count;
                int maxSpritesPerRow = Mathf.Max(1, Mathf.FloorToInt(EditorGUIUtility.currentViewWidth / 45f));
                int rowsNeeded = Mathf.CeilToInt((float)spriteCount / maxSpritesPerRow);

                // Draw sprites in flexible rows
                int currentIndex = 0;
                for (int row = 0; row < rowsNeeded; row++)
                {
                    EditorGUILayout.BeginHorizontal();

                    // Calculate how many sprites in this row
                    int spritesThisRow = Mathf.Min(maxSpritesPerRow, spriteCount - currentIndex);

                    // Add flexible space to center if needed
                    GUILayout.FlexibleSpace();

                    // Draw sprites for this row
                    for (int i = 0; i < spritesThisRow; i++)
                    {
                        DrawSmallNeighborSprite(compatibleTiles[currentIndex]);
                        currentIndex++;

                        // Add spacing except for the last sprite
                        if (i < spritesThisRow - 1)
                        {
                            GUILayout.Space(5);
                        }
                    }

                    // Add flexible space to center if needed
                    GUILayout.FlexibleSpace();

                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.EndVertical();
            }
        }

        private void DrawSmallNeighborSprite(int tileKey)
        {
            if (moduleSet.TileDatabase == null) return;

            if (moduleSet.TileDatabase.TryGetTile(tileKey, out Tile tile))
            {
                if (tile.sprite != null)
                {
                    Texture2D texture = AssetPreview.GetAssetPreview(tile.sprite);
                    if (texture != null)
                    {
                        // Small compact preview with tooltip
                        GUIContent content = new GUIContent(texture, $"{tileKey}: {tile.sprite.name}");
                        GUILayout.Label(content, GUILayout.Width(40), GUILayout.Height(40));
                    }
                    else
                    {
                        // Fallback: show just the key in a small box
                        GUILayout.Box(tileKey.ToString(), GUILayout.Width(40), GUILayout.Height(40));
                    }
                }
                else
                {
                    // No sprite - show key in a small box
                    GUILayout.Box(tileKey.ToString(), GUILayout.Width(40), GUILayout.Height(40));
                }
            }
            else
            {
                // Tile not found
                GUILayout.Box("?", GUILayout.Width(40), GUILayout.Height(40));
            }
        }

        

    }
}