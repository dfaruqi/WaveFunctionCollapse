using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MagusStudios.WaveFunctionCollapse
{
    [CustomEditor(typeof(WaveFunctionCollapse))]
    public class WaveFunctionCollapseEditor : Editor
    {
        SerializedProperty moduleSet;
        SerializedProperty mapSize;
        SerializedProperty seed;
        SerializedProperty generationMode;
        SerializedProperty blocks;
        SerializedProperty blockSize;
        SerializedProperty defaultTileId;

        bool autoRandomize = true;

        private uint timeSeed = 1;

        private Unity.Mathematics.Random random;

        private void OnEnable()
        {
            moduleSet = serializedObject.FindProperty("ModuleSet");
            mapSize = serializedObject.FindProperty("MapSize");
            seed = serializedObject.FindProperty("Seed");
            generationMode = serializedObject.FindProperty("GenerationMode");
            blocks = serializedObject.FindProperty("Blocks");
            blockSize = serializedObject.FindProperty("BlockSize");
            defaultTileId = serializedObject.FindProperty("DefaultTileId");

            uint tickCount = (uint)Environment.TickCount;
            if (tickCount == 0) tickCount = 1;

            var rng = new Unity.Mathematics.Random(tickCount);

            random = new Unity.Mathematics.Random(timeSeed);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // --- MODULES ---
            EditorGUILayout.LabelField("Modules", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(moduleSet);
            EditorGUILayout.Space(8);

            EditorGUILayout.LabelField("Map Generation Mode", EditorStyles.boldLabel);

            generationMode.enumValueIndex = GUILayout.Toolbar(
                generationMode.enumValueIndex,
                new[] { "Simple", "Chunked" }
            );

            WaveFunctionCollapse.MapGenerationMode mode =
                (WaveFunctionCollapse.MapGenerationMode)generationMode.enumValueIndex;

            switch (mode)
            {
                case WaveFunctionCollapse.MapGenerationMode.Simple:
                    // --- DIMENSIONS ---
                    EditorGUILayout.LabelField("Dimensions", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(mapSize);
                    EditorGUILayout.Space(8);
                    break;
                case WaveFunctionCollapse.MapGenerationMode.Chunked:
                    // --- DIMENSIONS ---
                    EditorGUILayout.LabelField("Dimensions", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(blocks);
                    EditorGUILayout.PropertyField(blockSize);
                    EditorGUILayout.PropertyField(defaultTileId);
                    EditorGUILayout.Space(8);
                    break;
            }


            // --- RANDOMIZATION ---
            EditorGUILayout.LabelField("Randomization", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(seed);
            if (GUILayout.Button("Randomize", GUILayout.MaxWidth(100)))
            {
                seed.uintValue = random.NextUInt();
            }

            EditorGUILayout.EndHorizontal();
            autoRandomize = EditorGUILayout.Toggle("Auto Randomize", autoRandomize);
            EditorGUILayout.Space(12);

            // --- GENERATE BUTTON ---
            if (GUILayout.Button("Generate", GUILayout.Height(30)))
            {
                if (autoRandomize)
                {
                    seed.uintValue = random.NextUInt();
                    serializedObject.ApplyModifiedProperties();
                }

                WaveFunctionCollapse wfc = (WaveFunctionCollapse)target;
                wfc.Generate();
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}