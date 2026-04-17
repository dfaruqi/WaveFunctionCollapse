using AYellowpaper.SerializedCollections;
using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(fileName = "WfcModuleSet", menuName = "Wave Function Collapse/WfcWeights")]
    public class WfcWeights : ScriptableObject
    {
        public float DefaultWeight;
        [SerializeField] SerializedDictionary<int, float> Weights;
        
        public float this[int tileId]
        {
            get => Weights[tileId];
            set => Weights[tileId] = value;
        }
        
        public bool TryGetWeight(int tileId, out float weight)
        {
            if (Weights != null && Weights.TryGetValue(tileId, out weight))
                return true;

            weight = DefaultWeight;
            return false;
        }
    }
}