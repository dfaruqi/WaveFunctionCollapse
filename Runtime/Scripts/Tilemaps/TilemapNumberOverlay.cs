using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace MagusStudios.Arcanist.Tilemaps
{
    public class TilemapNumberOverlay : MonoBehaviour
    {
        [SerializeField] GameObject numbersPrefab;
        [SerializeField] GameObject numbersParent;

        private Dictionary<Vector3Int, TMP_Text> _numberTexts = new Dictionary<Vector3Int, TMP_Text>();

        private void OnEnable()
        {
            numbersParent?.SetActive(true);
        }

        private void OnDisable()
        {
            numbersParent?.SetActive(false);
        }

        public void CreateNumberOverlay(Tilemap tilemap, Vector2Int size, int defaultValue = -1)
        {
            Clear(tilemap);

            BoundsInt bounds = new BoundsInt();
            bounds.x = size.x;
            bounds.y = size.y;
            bounds.z = 1;
            bounds.xMin = 0;
            bounds.yMin = 0;
            bounds.zMin = 0;
            bounds.ClampToBounds(bounds);

            int count = 0;
            foreach (var pos in bounds.allPositionsWithin)
            {
                Vector3 worldPos = tilemap.GetCellCenterWorld(pos);
                var textObj = Instantiate(numbersPrefab, worldPos, Quaternion.identity, numbersParent.transform);
                var text = textObj.GetComponent<TextMeshPro>();
                text.text = defaultValue == -1 ? "" : defaultValue.ToString();
                _numberTexts[pos] = text;
                count++;
            }
        }

        private void Clear(Tilemap tilemap)
        {
            foreach (var child in _numberTexts.Values)
            {
                Destroy(child.gameObject);
            }
            _numberTexts.Clear();
        }

        public void SetAll(int value)
        {
            foreach (var tmp in _numberTexts.Values)
            {
                tmp.text = value.ToString();
            }
        }

        public void SetNumber(Vector3Int position, int value)
        {
            if (_numberTexts.TryGetValue(position, out var text))
            {
                text.text = value.ToString();
            }
            else
            {
                Debug.LogError($"No text object found at position {position} of the tilemap");
            }
        }
    }

}
