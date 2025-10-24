using MagusStudios.Arcanist.Tilemaps;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MagusStudios.Arcanist.WaveFunctionCollapse
{
    [CustomEditor(typeof(WaveFunctionCollapse))]
    public class WaveFunctionCollapseEditor : Editor
    {
        SerializedProperty moduleSet;
        SerializedProperty mapSize;
        SerializedProperty animatedPassesPerSecond;
        SerializedProperty animate;
        SerializedProperty showDomains;
        SerializedProperty seed;

        bool autoRandomize = true;

        private void OnEnable()
        {
            moduleSet = serializedObject.FindProperty("moduleSet");
            mapSize = serializedObject.FindProperty("mapSize");
            animatedPassesPerSecond = serializedObject.FindProperty("animatedPassesPerSecond");
            animate = serializedObject.FindProperty("animate");
            showDomains = serializedObject.FindProperty("showDomains");
            seed = serializedObject.FindProperty("seed");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // --- MODULES ---
            EditorGUILayout.LabelField("Modules", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(moduleSet);
            EditorGUILayout.Space(8);

            // --- DIMENSIONS ---
            EditorGUILayout.LabelField("Dimensions", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(mapSize);
            EditorGUILayout.Space(8);

            // --- ANIMATION ---
            EditorGUILayout.LabelField("Animation", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(animatedPassesPerSecond);
            EditorGUILayout.PropertyField(animate);
            EditorGUILayout.PropertyField(showDomains);
            EditorGUILayout.Space(8);

            // --- RANDOMIZATION ---
            EditorGUILayout.LabelField("Randomization", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(seed);
            if (GUILayout.Button("Randomize", GUILayout.MaxWidth(100)))
            {
                seed.intValue = Random.Range(int.MinValue, int.MaxValue);
            }
            EditorGUILayout.EndHorizontal();
            autoRandomize = EditorGUILayout.Toggle("Auto Randomize", autoRandomize);
            EditorGUILayout.Space(12);

            // --- GENERATE BUTTON ---
            if (GUILayout.Button("Generate", GUILayout.Height(30)))
            {
                if (autoRandomize)
                {
                    seed.intValue = Random.Range(int.MinValue, int.MaxValue);
                    serializedObject.ApplyModifiedProperties();
                }

                WaveFunctionCollapse wfc = (WaveFunctionCollapse)target;
                wfc.Generate();
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
