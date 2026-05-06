using UnityEngine;
using UnityEngine.Tilemaps;


namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(menuName = "Tiles/RandomGameObjectTile")]
    public class RandomGameObjectTile : GameObjectTile
    {
        public Spawn[] spawnPossibilities;
        public Sprite[] tileSpritePossibilities;

        [System.Serializable]
        public struct Spawn
        {
            public GameObject Prefab;
            public float Weight;
        }

        public override void GetTileData(Vector3Int position, ITilemap tilemap, ref TileData tileData)
        {
            if (tileSpritePossibilities == null || tileSpritePossibilities.Length == 0)
            {
                tileData.sprite = base.sprite;
            }
            else
            {
                Sprite choice =
                    tileSpritePossibilities[
                        TileUtils.HashPosition(position.ToVector2Int(), tileSpritePossibilities.Length)];
                if (choice == null) tileData.sprite = base.sprite;
                else tileData.sprite = choice;
            }

            tileData.color = color;
            tileData.transform = transform;

            // if (spawnPossibilities == null || spawnPossibilities.Length == 0)
            // {
            //     tileData.gameObject = base.gameObject;
            // }
            // else
            // {
            //     Spawn spawnChoice = DeterministicWeightedRandom(position.ToVector2Int());
            //     GameObject choice = spawnChoice.Prefab ?? base.gameObject;
            //     tileData.gameObject = choice;
            // }
            tileData.gameObject = null; // Spawns are handled separately in the WfcWorldStreamer when drawing chunks,
                                        // so we don't want the tilemap to instantiate anything on its own.

            tileData.flags = flags;
            tileData.colliderType = colliderType;
        }

        public override GameObject GetGameObject(Vector2Int position)
        {
            Spawn spawnChoice = DeterministicWeightedRandom(position);
            GameObject choice = spawnChoice.Prefab;
            return choice;
        }
        
        private Spawn DeterministicWeightedRandom(Vector2Int position)
        {
            float total = 0f;
            foreach (var spawn in spawnPossibilities)
                total += spawn.Weight;

             // Seed the random generator with the tile position for consistency
            float random = TileUtils.HashPositionFloat(position) * total; // [0, total]

            float cursor = 0f;
            for (int i = 0; i < spawnPossibilities.Length; i++)
            {
                cursor += spawnPossibilities[i].Weight;
                if (cursor >= random)
                    return spawnPossibilities[i];
            }

            return spawnPossibilities[^1]; // fallback for floating-point edge cases
        }
    }
}