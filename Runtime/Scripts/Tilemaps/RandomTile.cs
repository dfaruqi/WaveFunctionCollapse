using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(fileName = "RandomTile", menuName = "Tiles/RandomTile")]
    public class RandomTile : Tile
    {
        [SerializeField] private List<Sprite> possibilities;

        public override void GetTileData(Vector3Int position, ITilemap tilemap, ref TileData tileData)
        {
            // Copy base tile properties except sprite
            tileData.color = color;
            tileData.transform = transform;
            tileData.gameObject = gameObject;
            tileData.flags = flags;
            tileData.colliderType = colliderType;

            // If we have possible sprites, pick one deterministically based on the map position
            int count = possibilities == null ? 0 : possibilities.Count;
            if (count == 0) return;
            if (count == 1)
            {
                tileData.sprite = possibilities[0];
                return;
            }

            int index = TileUtils.HashPosition(position.x, position.y, count);
            tileData.sprite = possibilities[index];
        }
    }
}
