using MagusStudios.Collections;
using System.Collections.Generic;
using Unity.Collections;

namespace MagusStudios.Arcanist.WaveFunctionCollapse
{
    public static class BurstUtils
    {
        public static NativeArray<int> ToNativeArray(SerializedHashSet<int> source, Allocator allocator)
        {
            var array = new NativeArray<int>(source.Count, allocator, NativeArrayOptions.UninitializedMemory);
            int index = 0;
            foreach (var value in source)
            {
                array[index++] = value;
            }
            return array;
        }
    }
}
