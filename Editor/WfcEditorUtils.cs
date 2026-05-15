using System.Reflection;
using UnityEditor;

namespace MagusStudios.WaveFunctionCollapse
{
    internal static class WfcEditorUtils
    {
        private static readonly MethodInfo GetActiveFolderPathMethod = typeof(ProjectWindowUtil).GetMethod(
            "GetActiveFolderPath",
            BindingFlags.Static | BindingFlags.NonPublic);

        public static string GetActiveProjectFolder()
        {
            if (GetActiveFolderPathMethod != null)
            {
                string result = GetActiveFolderPathMethod.Invoke(null, null) as string;
                if (!string.IsNullOrEmpty(result))
                    return result;
            }
            return "Assets";
        }
    }
}
