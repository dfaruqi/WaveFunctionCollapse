using UnityEngine;
using UnityEngine.Tilemaps;


namespace MagusStudios.WaveFunctionCollapse
{
    public class GameObjectTile : Tile
    {
        public GameObject Prefab => prefab;

        [SerializeField] GameObject prefab;
    }
}
