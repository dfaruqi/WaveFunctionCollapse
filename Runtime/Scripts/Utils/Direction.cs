using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MagusStudios.Arcanist.Utils
{
    [System.Serializable]
    public enum Direction { Up, Down, Left, Right }

    public static class DirectionExtension
    {
        public static Direction Opposite(this Direction direction)
        {
            if (direction == Direction.Up) return Direction.Down;
            else if (direction == Direction.Down) return Direction.Up;
            else if (direction == Direction.Left) return Direction.Right;
            else return Direction.Left;
        }

        public static Vector2Int ToVector2Int(this Direction direction)
        {
            if (direction == Direction.Up) return Vector2Int.up;
            else if (direction == Direction.Down) return Vector2Int.down;
            else if (direction == Direction.Left) return Vector2Int.left;
            else return Vector2Int.right;
        }

        public static Vector3Int ToVector3Int(this Direction direction)
        {
            if (direction == Direction.Up) return Vector3Int.up;
            else if (direction == Direction.Down) return Vector3Int.down;
            else if (direction == Direction.Left) return Vector3Int.left;
            else return Vector3Int.right;
        }

        public static IEnumerable<Direction> EnumerateAll()
        {
            return new[] { Direction.Up, Direction.Down, Direction.Left, Direction.Right };
        }
    }

}
