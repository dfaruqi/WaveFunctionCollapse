using UnityEngine;
using UnityEngine.Tilemaps;


namespace MagusStudios.Arcanist.Tilemaps
{
    public class GameObjectTile : Tile
    {
        public GameObject Prefab => prefab;

        [SerializeField] GameObject prefab;
    }
}
