using UnityEngine;
using UnityEngine.Tilemaps;


namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(menuName = "Tiles/GameObjectTile")]
    public class RandomGameObjectTile : Tile
    {
        public GameObject[] prefabPossibilities;
        public Sprite[] spritePossibilities;

        // public new GameObject gameObject => prefabPossibilities.Length == 0
        //     ? base.gameObject
        //     : prefabPossibilities[
        //         TileUtils.HashPosition(transform.GetPosition().ToVector3Int(), prefabPossibilities.Length) - 1];
        
        public override void GetTileData(Vector3Int position, ITilemap tilemap, ref TileData tileData)
        {
            tileData.sprite = spritePossibilities.Length == 0
                ? base.sprite
                : spritePossibilities[TileUtils.HashPosition(position, spritePossibilities.Length) - 1];
            tileData.color = color;
            tileData.transform = transform;
            tileData.gameObject = prefabPossibilities.Length == 0
                ? base.gameObject
                : prefabPossibilities[TileUtils.HashPosition(position, prefabPossibilities.Length) - 1];
            tileData.flags = flags;
            tileData.colliderType = colliderType;
        }
    }
}