using UnityEngine;
using UnityEngine.Tilemaps;
using System.Linq;
using AYellowpaper.SerializedCollections;
using System.Collections.Generic;

namespace MagusStudios.Arcanist.Tilemaps
{
    [CreateAssetMenu(fileName = "TileDatabase", menuName = "Databases/TileDatabase")]
    public class TileDatabase : ScriptableObject
    {
        public SerializedDictionary<int, Tile> Tiles;

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
        /// Returns the key of any matching tile in the database from a TileBase (typically from an active tilemap). 
        /// Throws an exception if no matching tile was found. Searches the whole database, so should not be used outside the editor.
        /// </summary>
        /// <param name="tileBase">The TileBase to find the key for.</param>
        /// <returns>The key associated with the tile.</returns>
        public int GetKeyFromMapTile(Tile tile)
        {
            return Tiles.First(kvp => kvp.Value.name == tile.name).Key;
        }

        public bool TryGetKeyFromMapTile(TileBase tile, out int key)
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
    }
}