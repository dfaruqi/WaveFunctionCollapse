using System.Collections;
using System.Threading.Tasks;

namespace MagusStudios.WaveFunctionCollapse.Utils
{
    public static class TaskExtensions
    {
        public static IEnumerator AsCoroutine(this Task task)
        {
            while (!task.IsCompleted)
                yield return null;
            if (task.IsFaulted)
                throw task.Exception!.InnerException!;
        }
    }
}