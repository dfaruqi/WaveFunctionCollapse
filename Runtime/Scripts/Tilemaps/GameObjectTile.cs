using UnityEngine;
using UnityEngine.Tilemaps;


namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(menuName = "Tiles/GameObjectTile")]
    public class GameObjectTile : Tile
    {
        public GameObject Prefab => prefab;

        [SerializeField] GameObject prefab;
    }
}
