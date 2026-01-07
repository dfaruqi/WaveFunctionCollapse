using System.Collections.Generic;
using UnityEngine;

namespace MagusStudios.Arcanist.Utils
{
    public static class VectorExtension
    {
        public static Vector3 ToVector3(this Vector2Int v)
        {
            return new Vector3(v.x, v.y);
        }

        public static Vector3Int ToVector3Int(this Vector2Int v)
        {
            return new Vector3Int(v.x, v.y, 0);
        }

        public static Vector2 ToVector2(this Vector2Int v)
        {
            return new Vector2(v.x, v.y);
        }

        public static Vector2 MidPoint(this Vector2 v1, Vector2 v2)
        {
            return new Vector2((v1.x + v2.x) / 2f, (v1.y + v2.y) / 2f);
        }

        public static Vector3 MidPoint(this Vector3 v1, Vector3 v2)
        {
            return new Vector3((v1.x + v2.x) / 2f, (v1.y + v2.y) / 2f);
        }
    }
}
