using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    public static class EnumerableExtensions
    {
        public static IEnumerable<T> Shuffle<T>(this IEnumerable<T> source, System.Random random)
        {
            return source.OrderBy(x => random.Next());
        }
    }
}
