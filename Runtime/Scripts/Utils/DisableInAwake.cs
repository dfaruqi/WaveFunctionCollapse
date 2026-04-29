using System;
using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    public class DisableInAwake : MonoBehaviour
    {
        private void Awake()
        {
            gameObject.SetActive(false);
        }
    }
}
