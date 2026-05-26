using System.Collections.Generic;
using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    public abstract class Biome : ScriptableObject
    {
        public abstract WfcTemplate GetTemplate(int chunkX, int chunkY);

        public WfcTemplate GetTemplate(Vector2Int pos) => GetTemplate(pos.x, pos.y);

        /// <summary>
        /// Implement this function to generate gameobjects in this biome that do not conform to the tilemap's tiling.
        /// Useful for resources that need strictly-controlled expected yield per chunk or do not fit well onto the
        /// grid. Examples: cattails (custom spawned on water edge tiles), mushroom clusters, sticks.
        /// </summary>
        public virtual void PostGenerate(in BiomePostGenContext context)
        {
        }
    }

    /// <summary>
    /// Data and helpers passed to <see cref="Biome.PostGenerate"/>. Bundles the chunk's tile
    /// array with the active databases so subclasses can read tiles and emit spawns by either
    /// integer id (fast) or string name (ergonomic; looked up through the databases).
    /// </summary>
    public readonly struct BiomePostGenContext
    {
        public readonly Vector2Int ChunkPos;
        public readonly int ChunkSize;

        private readonly int[] _tiles;
        private readonly List<ChunkData.WorldObjectSpawn> _spawns;
        
        private readonly TileDatabase _tileDatabase;
        private readonly WorldObjectDatabase _objectDatabase;

        public BiomePostGenContext(
            Vector2Int chunkPos, int chunkSize, int[] tiles,
            TileDatabase tileDatabase, WorldObjectDatabase objectDatabase,
            List<ChunkData.WorldObjectSpawn> spawns)
        {
            ChunkPos = chunkPos;
            ChunkSize = chunkSize;
            _tiles = tiles;
            _tileDatabase = tileDatabase;
            _objectDatabase = objectDatabase;
            _spawns = spawns;
        }

        /// <summary>Tile id at the given chunk-local cell. Fast — direct array read.</summary>
        public int GetTileId(int x, int y) => _tiles[y * ChunkSize + x];

        /// <summary>
        /// Tile asset name at the given chunk-local cell. Slower than <see cref="GetTileId"/>
        /// (one dictionary lookup); returns false if the tile id isn't in the database.
        /// </summary>
        public bool TryGetTileName(int x, int y, out string name) =>
            _tileDatabase.TryGetName(_tiles[y * ChunkSize + x], out name);

        /// <summary>Adds a spawn by prefab id. Fast path — no name resolution.</summary>
        public void AddSpawn(Vector2 localPosition, int prefabId)
        {
            _spawns.Add(new ChunkData.WorldObjectSpawn
            {
                localPosition = localPosition,
                prefabId = prefabId,
            });
        }

        /// <summary>
        /// Adds a spawn by prefab name. Returns false (without adding) if the name isn't in the
        /// world object database.
        /// </summary>
        public bool TryAddSpawn(Vector2 localPosition, string prefabName)
        {
            if (!_objectDatabase.TryGetId(prefabName, out int id)) return false;
            AddSpawn(localPosition, id);
            return true;
        }
    }
}
