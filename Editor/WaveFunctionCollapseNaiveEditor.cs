using System;
using UnityEditor;
using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    [CustomEditor(typeof(WaveFunctionCollapseNaive))]
    public class WaveFunctionCollapseNaiveEditor : Editor
    {
        SerializedProperty moduleSet;
        SerializedProperty mapSize;
        SerializedProperty animatedPassesPerSecond;
        SerializedProperty animate;
        SerializedProperty showDomains;
        SerializedProperty seed;
        
        bool autoRandomize = true;
        
        System.Random random = new System.Random();
        
        private void OnEnable()
        {
            moduleSet = serializedObject.FindProperty("ModuleSet");
            mapSize = serializedObject.FindProperty("MapSize");
            animatedPassesPerSecond = serializedObject.FindProperty("animatedPassesPerSecond");
            animate = serializedObject.FindProperty("animate");
            showDomains = serializedObject.FindProperty("showDomains");
            seed = serializedObject.FindProperty("Seed");
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
                seed.intValue = random.Next();
            }
            EditorGUILayout.EndHorizontal();
            autoRandomize = EditorGUILayout.Toggle("Auto Randomize", autoRandomize);
            EditorGUILayout.Space(12);

            // --- GENERATE BUTTON ---
            if (GUILayout.Button("Generate", GUILayout.Height(30)))
            {
                if (autoRandomize)
                {
                    seed.intValue = random.Next();
                    serializedObject.ApplyModifiedProperties();
                }

                WaveFunctionCollapseNaive wfc = (WaveFunctionCollapseNaive)target;
                wfc.GenerateFromEditor();
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
