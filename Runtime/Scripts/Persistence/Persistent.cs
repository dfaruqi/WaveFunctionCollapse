using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    public abstract class Persistent<T> : MonoBehaviour
    {
        public abstract T GetState();

        public abstract void LoadState(T state);
    }
}
