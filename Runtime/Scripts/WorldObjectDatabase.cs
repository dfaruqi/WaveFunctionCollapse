using AYellowpaper.SerializedCollections;
using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(menuName = "Databases/WorldObjectDatabase")]
    public class WorldObjectDatabase : ScriptableObject
    {
        [SerializeField] SerializedDictionary<int, GameObject> _prefabs = new SerializedDictionary<int, GameObject>();

        public bool TryGetObject(int objPrefabId, out GameObject o)
        {
            return _prefabs.TryGetValue(objPrefabId, out o);
        }
    }
}