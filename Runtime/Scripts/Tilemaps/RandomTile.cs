using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MagusStudios.Arcanist.Tilemaps
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
            if (possibilities != null && possibilities.Count > 0)
            {
                int index = TileUtils.HashPosition(position, possibilities.Count) - 1;
                tileData.sprite = possibilities[index];
            }
        }
    }
}
