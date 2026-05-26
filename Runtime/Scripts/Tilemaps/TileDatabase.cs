using UnityEngine;
using UnityEngine.Tilemaps;
using System.Linq;
using AYellowpaper.SerializedCollections;
using System.Collections.Generic;

namespace MagusStudios.WaveFunctionCollapse
{
    [CreateAssetMenu(fileName = "TileDatabase", menuName = "Tiles/TileDatabase")]
    public class TileDatabase : ScriptableObject
    {
        public SerializedDictionary<int, Tile> Tiles;

        // Lazy string→id lookup. Built on first call to TryGetId from the asset names in `Tiles`;
        // invalidated by OnValidate in the editor so inspector edits are picked up. Name identity
        // is the tile asset's `.name` — renaming a tile asset is a breaking change to any code
        // that hard-codes the old name.
        private Dictionary<string, int> _idsByName;

        public Tile this[int key] => Tiles[key];

        /// <summary>
        /// Attempts to get a Tile from the database by its key.
        /// </summary>
        /// <param name="key">The tile key.</param>
        /// <param name="tile">The tile found, or null if not found.</param>
        /// <returns>True if a tile was found; otherwise false.</returns>
        public bool TryGetTile(int key, out Tile tile)
        {
            return Tiles.TryGetValue(key, out tile);
        }

        /// <summary>
        /// Looks up a tile's integer key by its asset name. Slower than int-keyed access — meant
        /// for ergonomic callers (e.g. biome post-generation), not hot paths.
        /// </summary>
        public bool TryGetId(string name, out int id)
        {
            return GetIdsByName().TryGetValue(name, out id);
        }

        /// <summary>
        /// Returns the asset name associated with a tile key, or false if the key is missing or
        /// its tile is null.
        /// </summary>
        public bool TryGetName(int id, out string name)
        {
            if (Tiles.TryGetValue(id, out Tile tile) && tile != null)
            {
                name = tile.name;
                return true;
            }
            name = null;
            return false;
        }

        private Dictionary<string, int> GetIdsByName()
        {
            if (_idsByName != null) return _idsByName;

            _idsByName = new Dictionary<string, int>(Tiles.Count);
            foreach (KeyValuePair<int, Tile> kvp in Tiles)
            {
                if (kvp.Value == null) continue;
                if (!_idsByName.TryAdd(kvp.Value.name, kvp.Key))
                {
                    Debug.LogWarning(
                        $"[{nameof(TileDatabase)}] Duplicate tile name '{kvp.Value.name}' on " +
                        $"ids {_idsByName[kvp.Value.name]} and {kvp.Key}; first wins.");
                }
            }
            return _idsByName;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            _idsByName = null;
            WarnOnDuplicateNames();
        }

        private void WarnOnDuplicateNames()
        {
            if (Tiles == null) return;

            HashSet<string> seen = new HashSet<string>(Tiles.Count);
            foreach (KeyValuePair<int, Tile> kvp in Tiles)
            {
                if (kvp.Value == null) continue;
                if (!seen.Add(kvp.Value.name))
                {
                    Debug.LogWarning(
                        $"[{nameof(TileDatabase)}] Duplicate tile name '{kvp.Value.name}' " +
                        $"in '{name}'. Name-based lookups will resolve to whichever entry " +
                        "comes first.", this);
                }
            }
        }
#endif

        /// <summary>
        /// Returns the key of any matching tile in the database from a TileBase (typically from an active tilemap). 
        /// Throws an exception if no matching tile was found. Searches the whole database, so should not be used outside the editor.
        /// </summary>
        /// <param name="tileBase">The TileBase to find the key for.</param>
        /// <returns>The key associated with the tile.</returns>
        public int GetKeyFromMapTile(Tile tile)
        {
            return Tiles.First(kvp => kvp.Value.name == tile.name).Key;
        }

        public bool TryGetKeyFromMapTile(Tile tile, out int key)
        {
            try
            {
                KeyValuePair<int, Tile> kvp = Tiles.First(kvp => kvp.Value.name == tile.name);
                key = kvp.Key;
                return true;
            }
            catch
            {
                key = -1;
                return false;
            }
        }
        
        public void ScanTilemapAndOverwrite(Tilemap tilemap)
        {
            Tiles.Clear();

            int count = 0;
            foreach (Vector3Int pos in tilemap.cellBounds.allPositionsWithin)
            {
                Tile tile = tilemap.GetTile(pos) as Tile;
                if (tile == null) continue;
                if (TryGetKeyFromMapTile(tile, out int key)) continue;

                Tiles.Add(count, tile);
                count++;
            }
        }
    }
}