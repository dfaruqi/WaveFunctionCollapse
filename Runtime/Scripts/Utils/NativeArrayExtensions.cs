using Unity.Collections;
using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    public static class NativeArrayExtensions
    {
        public static int[,] ToSquare2DArray(this NativeArray<int> source)
        {
            int length = source.Length;

            // Determine the smallest square that can fit all elements
            int size = (int)Mathf.Ceil(Mathf.Sqrt(length));

            int[,] result = new int[size, size];

            // Initialize with -1
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    result[y, x] = -1;
                }
            }

            // Copy data
            for (int i = 0; i < length; i++)
            {
                int row = i / size;
                int col = i % size;
                result[row, col] = source[i];
            }

            return result;
        }
    }
}
