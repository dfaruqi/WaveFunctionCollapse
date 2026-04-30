using UnityEngine;
using UnityEngine.Tilemaps;

namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(menuName = "Tiles/GameObjectTile")]
    public class GameObjectTile : Tile
    {
        public GameObject Prefab;

        public override void GetTileData(Vector3Int position, ITilemap tilemap, ref TileData tileData)
        {
            tileData.sprite = sprite;
            tileData.color = color;
            tileData.transform = transform;
            tileData.gameObject = Prefab ?? base.gameObject;
            tileData.flags = flags;
            tileData.colliderType = colliderType;
        }
    }
}