using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(fileName = "WorldSpawn", menuName = "World Generation/WorldSpawn")]
    public class WorldSpawn : ScriptableObject
    {
        public GameObject Prefab;
    }
}
