using System.Collections.Generic;
using UnityEngine;

namespace MagusStudios.Arcanist.Utils
{
    public static class Vector3Extension
    {
        public static Vector3 Rotate(this Vector3 v, float degrees)
        {
            return Quaternion.Euler(0, 0, degrees) * v;
        }
    }

    public static class MathExtension
    {
        public static float RandomGaussian(float minValue = 0.0f, float maxValue = 1.0f)
        {
            float u, v, S;

            do
            {
                u = 2.0f * UnityEngine.Random.value - 1.0f;
                v = 2.0f * UnityEngine.Random.value - 1.0f;
                S = u * u + v * v;
            }
            while (S >= 1.0f);

            // Standard Normal Distribution
            float std = u * Mathf.Sqrt(-2.0f * Mathf.Log(S) / S);

            // Normal Distribution centered between the min and max value
            // and clamped following the "three-sigma rule"
            float mean = (minValue + maxValue) / 2.0f;
            float sigma = (maxValue - mean) / 3.0f;
            return Mathf.Clamp(std * sigma + mean, minValue, maxValue);
        }
    }

    public static class RandomExtension
    {
        public static void Shuffle<T>(this IList<T> ts)
        {
            var count = ts.Count;
            var last = count - 1;
            for (var i = 0; i < last; ++i)
            {
                var r = UnityEngine.Random.Range(i, count);
                var tmp = ts[i];
                ts[i] = ts[r];
                ts[r] = tmp;
            }
        }
    }

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
