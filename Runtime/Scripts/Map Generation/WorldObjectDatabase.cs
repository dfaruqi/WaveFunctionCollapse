using System.Collections.Generic;
using AYellowpaper.SerializedCollections;
using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(menuName = "Databases/WorldObjectDatabase")]
    public class WorldObjectDatabase : ScriptableObject
    {
        [SerializeField] SerializedDictionary<int, GameObject> _prefabs = new SerializedDictionary<int, GameObject>();

        // Lazy string→id lookup. Built on first call to TryGetId from prefab `.name`s;
        // invalidated by OnValidate in the editor. Name identity is the prefab asset's `.name` —
        // renaming a prefab is a breaking change to any code that hard-codes the old name.
        private Dictionary<string, int> _idsByName;

        public bool TryGetObject(int objPrefabId, out GameObject o)
        {
            return _prefabs.TryGetValue(objPrefabId, out o);
        }

        /// <summary>
        /// Looks up a prefab's integer id by its asset name. Slower than int-keyed access —
        /// meant for ergonomic callers (e.g. biome post-generation), not hot paths.
        /// </summary>
        public bool TryGetId(string name, out int id)
        {
            return GetIdsByName().TryGetValue(name, out id);
        }

        /// <summary>
        /// Returns the asset name associated with a prefab id, or false if the id is missing or
        /// its prefab is null.
        /// </summary>
        public bool TryGetName(int id, out string name)
        {
            if (_prefabs.TryGetValue(id, out GameObject prefab) && prefab != null)
            {
                name = prefab.name;
                return true;
            }
            name = null;
            return false;
        }

        private Dictionary<string, int> GetIdsByName()
        {
            if (_idsByName != null) return _idsByName;

            _idsByName = new Dictionary<string, int>(_prefabs.Count);
            foreach (KeyValuePair<int, GameObject> kvp in _prefabs)
            {
                if (kvp.Value == null) continue;
                if (!_idsByName.TryAdd(kvp.Value.name, kvp.Key))
                {
                    Debug.LogWarning(
                        $"[{nameof(WorldObjectDatabase)}] Duplicate prefab name '{kvp.Value.name}' " +
                        $"on ids {_idsByName[kvp.Value.name]} and {kvp.Key}; first wins.");
                }
            }
            return _idsByName;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            _idsByName = null;
            WarnOnDuplicateNames();
        }

        private void WarnOnDuplicateNames()
        {
            if (_prefabs == null) return;

            HashSet<string> seen = new HashSet<string>(_prefabs.Count);
            foreach (KeyValuePair<int, GameObject> kvp in _prefabs)
            {
                if (kvp.Value == null) continue;
                if (!seen.Add(kvp.Value.name))
                {
                    Debug.LogWarning(
                        $"[{nameof(WorldObjectDatabase)}] Duplicate prefab name '{kvp.Value.name}' " +
                        $"in '{name}'. Name-based lookups will resolve to whichever entry " +
                        "comes first.", this);
                }
            }
        }
#endif
    }
}