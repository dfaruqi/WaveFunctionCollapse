using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(fileName = "WorldSpawn", menuName = "World Generation/WorldSpawn")]
    public class WorldSpawnTemplate : ScriptableObject
    {
        public GameObject Prefab;
    }
}
