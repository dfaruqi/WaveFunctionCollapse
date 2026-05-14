using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Tilemaps;

namespace MagusStudios.WaveFunctionCollapse
{
    /// <summary>
    ///     Generic visual tile for creating different tilesets like terrain, pipeline, random or animated tiles.
    ///     This is templated to accept a Neighbor Rule Class for Custom Rules.
    /// </summary>
    /// <typeparam name="T">Neighbor Rule Class for Custom Rules</typeparam>
    public class RuleTile<T> : RuleTile
    {
        /// <summary>
        ///     Returns the Neighbor Rule Class type for this Rule Tile.
        /// </summary>
        public sealed override Type m_NeighborType => typeof(T);
    }

    /// <summary>
    ///     Generic visual tile for creating different tilesets like terrain, pipeline, random or animated tiles.
    ///     Inherits from <see cref="Tile" /> so callers can read <c>tile.sprite</c>, <c>tile.colliderType</c>,
    ///     <c>tile.gameObject</c>, etc. without knowing the concrete tile type. The default sprite / collider /
    ///     gameObject are stored on the inherited <see cref="Tile" /> fields; per-rule overrides still come from
    ///     <see cref="TilingRule" />.
    /// </summary>
    [Serializable]
    [HelpURL(
        "https://docs.unity3d.com/Packages/com.unity.2d.tilemap.extras@latest/index.html?subfolder=/manual/RuleTile.html")]
    public class RuleTile : Tile
    {
        private static Dictionary<Tilemap, KeyValuePair<HashSet<TileBase>, HashSet<Vector3Int>>>
            m_CacheTilemapsNeighborPositions = new();

        private static TileBase[] m_AllocatedUsedTileArr = Array.Empty<TileBase>();

        /// <summary>
        ///     A list of Tiling Rules for the Rule Tile.
        /// </summary>
        [HideInInspector] public List<TilingRule> m_TilingRules = new();

        private HashSet<Vector3Int> m_NeighborPositions = new();

        /// <summary>
        ///     Returns the default Neighbor Rule Class type.
        /// </summary>
        public virtual Type m_NeighborType => typeof(TilingRuleOutput.Neighbor);

        /// <summary>
        ///     Angle in which the RuleTile is rotated by for matching in Degrees.
        /// </summary>
        public virtual int m_RotationAngle => 90;

        /// <summary>
        ///     Number of rotations the RuleTile can be rotated by for matching.
        /// </summary>
        public int m_RotationCount => 360 / m_RotationAngle;

        /// <summary>
        ///     Returns a set of neighboring positions for this RuleTile
        /// </summary>
        public HashSet<Vector3Int> neighborPositions
        {
            get
            {
                if (m_NeighborPositions.Count == 0)
                    UpdateNeighborPositions();

                return m_NeighborPositions;
            }
        }

        /// <summary>
        ///     Updates the neighboring positions of this RuleTile
        /// </summary>
        public void UpdateNeighborPositions()
        {
            m_CacheTilemapsNeighborPositions.Clear();

            var positions = m_NeighborPositions;
            positions.Clear();

            foreach (var rule in m_TilingRules)
            foreach (var neighbor in rule.GetNeighbors())
            {
                var position = neighbor.Key;
                positions.Add(position);

                if (rule.m_RuleTransform == TilingRuleOutput.Transform.Rotated)
                {
                    for (var angle = m_RotationAngle; angle < 360; angle += m_RotationAngle)
                        positions.Add(GetRotatedPosition(position, angle));
                }
                else if (rule.m_RuleTransform == TilingRuleOutput.Transform.MirrorXY)
                {
                    positions.Add(GetMirroredPosition(position, true, true));
                    positions.Add(GetMirroredPosition(position, true, false));
                    positions.Add(GetMirroredPosition(position, false, true));
                }
                else if (rule.m_RuleTransform == TilingRuleOutput.Transform.MirrorX)
                {
                    positions.Add(GetMirroredPosition(position, true, false));
                }
                else if (rule.m_RuleTransform == TilingRuleOutput.Transform.MirrorY)
                {
                    positions.Add(GetMirroredPosition(position, false, true));
                }
                else if (rule.m_RuleTransform == TilingRuleOutput.Transform.RotatedMirror)
                {
                    var mirroredPosition = GetMirroredPosition(position, true, false);
                    for (var angle = m_RotationAngle; angle < 360; angle += m_RotationAngle)
                    {
                        positions.Add(GetRotatedPosition(position, angle));
                        positions.Add(GetRotatedPosition(mirroredPosition, angle));
                    }
                }
            }
        }

        /// <summary>
        ///     StartUp is called on the first frame of the running Scene.
        /// </summary>
        public override bool StartUp(Vector3Int position, ITilemap tilemap, GameObject instantiatedGameObject)
        {
            if (instantiatedGameObject != null)
            {
                var tmpMap = tilemap.GetComponent<Tilemap>();
                var orientMatrix = tmpMap.orientationMatrix;

                var iden = Matrix4x4.identity;
                var gameObjectTranslation = new Vector3();
                var gameObjectRotation = new Quaternion();
                var gameObjectScale = new Vector3();

                var ruleMatched = false;
                var ruleTransform = iden;
                foreach (var rule in m_TilingRules)
                    if (RuleMatches(rule, position, tilemap, ref ruleTransform))
                    {
                        ruleTransform = orientMatrix * ruleTransform;

                        gameObjectTranslation = new Vector3(ruleTransform.m03, ruleTransform.m13, ruleTransform.m23);
                        gameObjectRotation = Quaternion.LookRotation(
                            new Vector3(ruleTransform.m02, ruleTransform.m12, ruleTransform.m22),
                            new Vector3(ruleTransform.m01, ruleTransform.m11, ruleTransform.m21));
                        gameObjectScale = ruleTransform.lossyScale;

                        ruleMatched = true;
                        break;
                    }

                if (!ruleMatched)
                {
                    gameObjectTranslation = new Vector3(orientMatrix.m03, orientMatrix.m13, orientMatrix.m23);
                    gameObjectRotation = Quaternion.LookRotation(
                        new Vector3(orientMatrix.m02, orientMatrix.m12, orientMatrix.m22),
                        new Vector3(orientMatrix.m01, orientMatrix.m11, orientMatrix.m21));
                    gameObjectScale = orientMatrix.lossyScale;
                }

                instantiatedGameObject.transform.localPosition = gameObjectTranslation +
                                                                 tmpMap.CellToLocalInterpolated(position +
                                                                     tmpMap.tileAnchor);
                instantiatedGameObject.transform.localRotation = gameObjectRotation;
                instantiatedGameObject.transform.localScale = gameObjectScale;
            }

            return true;
        }

        /// <summary>
        ///     Retrieves any tile rendering data from the scripted tile.
        /// </summary>
        public override void GetTileData(Vector3Int position, ITilemap tilemap, ref TileData tileData)
        {
            var iden = Matrix4x4.identity;

            tileData.sprite = sprite;
            tileData.gameObject = gameObject;
            tileData.colliderType = colliderType;
            tileData.flags = TileFlags.LockTransform;
            tileData.transform = iden;

            var ruleTransform = iden;
            foreach (var rule in m_TilingRules)
                if (RuleMatches(rule, position, tilemap, ref ruleTransform))
                {
                    switch (rule.m_Output)
                    {
                        case TilingRuleOutput.OutputSprite.Single:
                        case TilingRuleOutput.OutputSprite.Animation:
                            tileData.sprite = rule.m_Sprites[0];
                            break;
                        case TilingRuleOutput.OutputSprite.Random:
                            var index = Mathf.Clamp(
                                Mathf.FloorToInt(GetPerlinValue(position, rule.m_PerlinScale, 100000f) *
                                                 rule.m_Sprites.Length), 0, rule.m_Sprites.Length - 1);
                            tileData.sprite = rule.m_Sprites[index];
                            if (rule.m_RandomTransform != TilingRuleOutput.Transform.Fixed)
                                ruleTransform = ApplyRandomTransform(rule.m_RandomTransform, ruleTransform,
                                    rule.m_PerlinScale, position);
                            break;
                    }

                    tileData.transform = ruleTransform;
                    tileData.gameObject = rule.m_GameObject;
                    tileData.colliderType = rule.m_ColliderType;
                    break;
                }
        }

        /// <summary>
        ///     Returns a Perlin Noise value based on the given inputs.
        /// </summary>
        public static float GetPerlinValue(Vector3Int position, float scale, float offset)
        {
            return Mathf.PerlinNoise((position.x + offset) * scale, (position.y + offset) * scale);
        }

        private static bool IsTilemapUsedTilesChange(Tilemap tilemap,
            out KeyValuePair<HashSet<TileBase>, HashSet<Vector3Int>> hashSet)
        {
            if (!m_CacheTilemapsNeighborPositions.TryGetValue(tilemap, out hashSet))
                return true;

            var oldUsedTiles = hashSet.Key;
            var newUsedTilesCount = tilemap.GetUsedTilesCount();
            if (newUsedTilesCount != oldUsedTiles.Count)
                return true;

            if (m_AllocatedUsedTileArr.Length < newUsedTilesCount)
                Array.Resize(ref m_AllocatedUsedTileArr, newUsedTilesCount);

            tilemap.GetUsedTilesNonAlloc(m_AllocatedUsedTileArr);
            for (var i = 0; i < newUsedTilesCount; i++)
            {
                var newUsedTile = m_AllocatedUsedTileArr[i];
                if (!oldUsedTiles.Contains(newUsedTile))
                    return true;
            }

            return false;
        }

        private static KeyValuePair<HashSet<TileBase>, HashSet<Vector3Int>> CachingTilemapNeighborPositions(
            Tilemap tilemap)
        {
            var usedTileCount = tilemap.GetUsedTilesCount();
            var usedTiles = new HashSet<TileBase>();
            var neighborPositions = new HashSet<Vector3Int>();

            if (m_AllocatedUsedTileArr.Length < usedTileCount)
                Array.Resize(ref m_AllocatedUsedTileArr, usedTileCount);

            tilemap.GetUsedTilesNonAlloc(m_AllocatedUsedTileArr);

            for (var i = 0; i < usedTileCount; i++)
            {
                var tile = m_AllocatedUsedTileArr[i];
                usedTiles.Add(tile);

                if (tile is RuleTile ruleTile)
                    foreach (var neighborPosition in ruleTile.neighborPositions)
                        neighborPositions.Add(neighborPosition);
            }

            var value = new KeyValuePair<HashSet<TileBase>, HashSet<Vector3Int>>(usedTiles, neighborPositions);
            m_CacheTilemapsNeighborPositions[tilemap] = value;
            return value;
        }

        private static bool NeedRelease()
        {
            foreach (var keypair in m_CacheTilemapsNeighborPositions)
                if (keypair.Key == null)
                    return true;

            return false;
        }

        private static void ReleaseDestroyedTilemapCacheData()
        {
            if (!NeedRelease())
                return;

            var hasCleared = false;
            var keys = m_CacheTilemapsNeighborPositions.Keys.ToArray();
            foreach (var key in keys)
                if (key == null && m_CacheTilemapsNeighborPositions.Remove(key))
                    hasCleared = true;

            if (hasCleared)
                m_CacheTilemapsNeighborPositions =
                    new Dictionary<Tilemap, KeyValuePair<HashSet<TileBase>, HashSet<Vector3Int>>>(
                        m_CacheTilemapsNeighborPositions);
        }

        /// <summary>
        ///     Retrieves any tile animation data from the scripted tile.
        /// </summary>
        public override bool GetTileAnimationData(Vector3Int position, ITilemap tilemap,
            ref TileAnimationData tileAnimationData)
        {
            var ruleTransform = Matrix4x4.identity;
            foreach (var rule in m_TilingRules)
                if (rule.m_Output == TilingRuleOutput.OutputSprite.Animation)
                    if (RuleMatches(rule, position, tilemap, ref ruleTransform))
                    {
                        tileAnimationData.animatedSprites = rule.m_Sprites;
                        tileAnimationData.animationSpeed =
                            UnityEngine.Random.Range(rule.m_MinAnimationSpeed, rule.m_MaxAnimationSpeed);
                        return true;
                    }

            return false;
        }

        /// <summary>
        ///     This method is called when the tile is refreshed.
        /// </summary>
        public override void RefreshTile(Vector3Int position, ITilemap tilemap)
        {
            base.RefreshTile(position, tilemap);

            var baseTilemap = tilemap.GetComponent<Tilemap>();

            ReleaseDestroyedTilemapCacheData();

            if (IsTilemapUsedTilesChange(baseTilemap, out var neighborPositionsSet))
                neighborPositionsSet = CachingTilemapNeighborPositions(baseTilemap);

            var neighborPositionsRuleTile = neighborPositionsSet.Value;
            foreach (var offset in neighborPositionsRuleTile)
            {
                var offsetPosition = GetOffsetPositionReverse(position, offset);
                var tile = tilemap.GetTile(offsetPosition);

                if (tile is RuleTile ruleTile)
                    if (ruleTile == this || ruleTile.neighborPositions.Contains(offset))
                        base.RefreshTile(offsetPosition, tilemap);
            }
        }

        /// <summary>
        ///     Does a Rule Match given a Tiling Rule and neighboring Tiles.
        /// </summary>
        public virtual bool RuleMatches(TilingRule rule, Vector3Int position, ITilemap tilemap,
            ref Matrix4x4 transform)
        {
            if (RuleMatches(rule, position, tilemap, 0))
            {
                transform = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, 0f, 0f), Vector3.one);
                return true;
            }

            if (rule.m_RuleTransform == TilingRuleOutput.Transform.Rotated)
            {
                for (var angle = m_RotationAngle; angle < 360; angle += m_RotationAngle)
                    if (RuleMatches(rule, position, tilemap, angle))
                    {
                        transform = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, 0f, -angle), Vector3.one);
                        return true;
                    }
            }
            else if (rule.m_RuleTransform == TilingRuleOutput.Transform.MirrorXY)
            {
                if (RuleMatches(rule, position, tilemap, true, true))
                {
                    transform = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(-1f, -1f, 1f));
                    return true;
                }

                if (RuleMatches(rule, position, tilemap, true, false))
                {
                    transform = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(-1f, 1f, 1f));
                    return true;
                }

                if (RuleMatches(rule, position, tilemap, false, true))
                {
                    transform = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1f, -1f, 1f));
                    return true;
                }
            }
            else if (rule.m_RuleTransform == TilingRuleOutput.Transform.MirrorX)
            {
                if (RuleMatches(rule, position, tilemap, true, false))
                {
                    transform = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(-1f, 1f, 1f));
                    return true;
                }
            }
            else if (rule.m_RuleTransform == TilingRuleOutput.Transform.MirrorY)
            {
                if (RuleMatches(rule, position, tilemap, false, true))
                {
                    transform = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1f, -1f, 1f));
                    return true;
                }
            }
            else if (rule.m_RuleTransform == TilingRuleOutput.Transform.RotatedMirror)
            {
                for (var angle = 0; angle < 360; angle += m_RotationAngle)
                {
                    if (angle != 0 && RuleMatches(rule, position, tilemap, angle))
                    {
                        transform = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, 0f, -angle), Vector3.one);
                        return true;
                    }

                    if (RuleMatches(rule, position, tilemap, angle, true))
                    {
                        transform = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, 0f, -angle),
                            new Vector3(-1f, 1f, 1f));
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        ///     Returns a random transform matrix given the random transform rule.
        /// </summary>
        public virtual Matrix4x4 ApplyRandomTransform(TilingRuleOutput.Transform type, Matrix4x4 original,
            float perlinScale, Vector3Int position)
        {
            var perlin = GetPerlinValue(position, perlinScale, 200000f);
            switch (type)
            {
                case TilingRuleOutput.Transform.MirrorXY:
                    return original * Matrix4x4.TRS(Vector3.zero, Quaternion.identity,
                        new Vector3(Math.Abs(perlin - 0.5) > 0.25 ? 1f : -1f, perlin < 0.5 ? 1f : -1f, 1f));
                case TilingRuleOutput.Transform.MirrorX:
                    return original * Matrix4x4.TRS(Vector3.zero, Quaternion.identity,
                        new Vector3(perlin < 0.5 ? 1f : -1f, 1f, 1f));
                case TilingRuleOutput.Transform.MirrorY:
                    return original * Matrix4x4.TRS(Vector3.zero, Quaternion.identity,
                        new Vector3(1f, perlin < 0.5 ? 1f : -1f, 1f));
                case TilingRuleOutput.Transform.Rotated:
                {
                    var angle = Mathf.Clamp(Mathf.FloorToInt(perlin * m_RotationCount), 0, m_RotationCount - 1) *
                                m_RotationAngle;
                    return Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, 0f, -angle), Vector3.one);
                }
                case TilingRuleOutput.Transform.RotatedMirror:
                {
                    var angle = Mathf.Clamp(Mathf.FloorToInt(perlin * m_RotationCount), 0, m_RotationCount - 1) *
                                m_RotationAngle;
                    return Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, 0f, -angle),
                        new Vector3(perlin < 0.5 ? 1f : -1f, 1f, 1f));
                }
            }

            return original;
        }

        /// <summary>
        ///     Returns custom fields for this RuleTile
        /// </summary>
        public FieldInfo[] GetCustomFields(bool isOverrideInstance)
        {
            return GetType().GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                       BindingFlags.NonPublic)
                .Where(field => typeof(RuleTile).GetField(field.Name) == null)
                .Where(field => field.IsPublic || field.IsDefined(typeof(SerializeField)))
                .Where(field => !field.IsDefined(typeof(HideInInspector)))
                .Where(field => !isOverrideInstance || !field.IsDefined(typeof(DontOverride)))
                .ToArray();
        }

        /// <summary>
        ///     Checks if there is a match given the neighbor matching rule and a Tile.
        /// </summary>
        public virtual bool RuleMatch(int neighbor, TileBase other)
        {
            switch (neighbor)
            {
                case TilingRuleOutput.Neighbor.This: return other == this;
                case TilingRuleOutput.Neighbor.NotThis: return other != this;
            }

            return true;
        }

        /// <summary>
        ///     Checks if there is a match given the neighbor matching rule and a Tile with a rotation angle.
        /// </summary>
        public bool RuleMatches(TilingRule rule, Vector3Int position, ITilemap tilemap, int angle,
            bool mirrorX = false)
        {
            var minCount = Math.Min(rule.m_Neighbors.Count, rule.m_NeighborPositions.Count);
            for (var i = 0; i < minCount; i++)
            {
                var neighbor = rule.m_Neighbors[i];
                var neighborPosition = rule.m_NeighborPositions[i];
                if (mirrorX)
                    neighborPosition = GetMirroredPosition(neighborPosition, true, false);
                var positionOffset = GetRotatedPosition(neighborPosition, angle);
                var other = tilemap.GetTile(GetOffsetPosition(position, positionOffset));
                if (!RuleMatch(neighbor, other)) return false;
            }

            return true;
        }

        /// <summary>
        ///     Checks if there is a match given the neighbor matching rule and a Tile with mirrored axii.
        /// </summary>
        public bool RuleMatches(TilingRule rule, Vector3Int position, ITilemap tilemap, bool mirrorX, bool mirrorY)
        {
            var minCount = Math.Min(rule.m_Neighbors.Count, rule.m_NeighborPositions.Count);
            for (var i = 0; i < minCount; i++)
            {
                var neighbor = rule.m_Neighbors[i];
                var positionOffset = GetMirroredPosition(rule.m_NeighborPositions[i], mirrorX, mirrorY);
                var other = tilemap.GetTile(GetOffsetPosition(position, positionOffset));
                if (!RuleMatch(neighbor, other)) return false;
            }

            return true;
        }

        /// <summary>
        ///     Gets a rotated position given its original position and the rotation in degrees.
        /// </summary>
        public virtual Vector3Int GetRotatedPosition(Vector3Int position, int rotation)
        {
            switch (rotation)
            {
                case 0:
                    return position;
                case 90:
                    return new Vector3Int(position.y, -position.x, 0);
                case 180:
                    return new Vector3Int(-position.x, -position.y, 0);
                case 270:
                    return new Vector3Int(-position.y, position.x, 0);
            }

            return position;
        }

        /// <summary>
        ///     Gets a mirrored position given its original position and the mirroring axii.
        /// </summary>
        public virtual Vector3Int GetMirroredPosition(Vector3Int position, bool mirrorX, bool mirrorY)
        {
            if (mirrorX)
                position.x *= -1;
            if (mirrorY)
                position.y *= -1;
            return position;
        }

        /// <summary>
        ///     Get the offset for the given position with the given offset.
        /// </summary>
        public virtual Vector3Int GetOffsetPosition(Vector3Int position, Vector3Int offset)
        {
            return position + offset;
        }

        /// <summary>
        ///     Get the reversed offset for the given position with the given offset.
        /// </summary>
        public virtual Vector3Int GetOffsetPositionReverse(Vector3Int position, Vector3Int offset)
        {
            return position - offset;
        }

        /// <summary>
        ///     The data structure holding the Rule information for matching Rule Tiles with
        ///     its neighbors.
        /// </summary>
        [Serializable]
        public class TilingRuleOutput
        {
            /// <summary>The Output for the Tile which fits this Rule.</summary>
            public enum OutputSprite
            {
                Single,
                Random,
                Animation
            }

            /// <summary>The enumeration for the transform rule used when matching Rule Tiles.</summary>
            public enum Transform
            {
                Fixed,
                Rotated,
                MirrorX,
                MirrorY,
                MirrorXY,
                RotatedMirror
            }

            /// <summary>Id for this Rule.</summary>
            public int m_Id;

            /// <summary>The output Sprites for this Rule.</summary>
            public Sprite[] m_Sprites = new Sprite[1];

            /// <summary>The output GameObject for this Rule.</summary>
            public GameObject m_GameObject;

            /// <summary>The output minimum Animation Speed for this Rule.</summary>
            [FormerlySerializedAs("m_AnimationSpeed")]
            public float m_MinAnimationSpeed = 1f;

            /// <summary>The output maximum Animation Speed for this Rule.</summary>
            [FormerlySerializedAs("m_AnimationSpeed")]
            public float m_MaxAnimationSpeed = 1f;

            /// <summary>The perlin scale factor for this Rule.</summary>
            public float m_PerlinScale = 0.5f;

            /// <summary>The output type for this Rule.</summary>
            public OutputSprite m_Output = OutputSprite.Single;

            /// <summary>The output Collider Type for this Rule.</summary>
            public Tile.ColliderType m_ColliderType = Tile.ColliderType.Sprite;

            /// <summary>The randomized transform output for this Rule.</summary>
            public Transform m_RandomTransform;

            /// <summary>The enumeration for matching Neighbors when matching Rule Tiles</summary>
            public class Neighbor
            {
                public const int This = 1;
                public const int NotThis = 2;
            }
        }

        /// <summary>
        ///     The data structure holding the Rule information for matching Rule Tiles with
        ///     its neighbors.
        /// </summary>
        [Serializable]
        public class TilingRule : TilingRuleOutput
        {
            /// <summary>The matching Rule conditions for each of its neighboring Tiles.</summary>
            public List<int> m_Neighbors = new();

            /// <summary>
            ///     Preset this list to RuleTile backward compatible, but not support for HexagonalRuleTile backward compatible.
            /// </summary>
            public List<Vector3Int> m_NeighborPositions = new()
            {
                new(-1, 1, 0),
                new(0, 1, 0),
                new(1, 1, 0),
                new(-1, 0, 0),
                new(1, 0, 0),
                new(-1, -1, 0),
                new(0, -1, 0),
                new(1, -1, 0)
            };

            /// <summary>The transform matching Rule for this Rule.</summary>
            public Transform m_RuleTransform;

            /// <summary>This clones a copy of the TilingRule.</summary>
            public TilingRule Clone()
            {
                var rule = new TilingRule
                {
                    m_Neighbors = new List<int>(m_Neighbors),
                    m_NeighborPositions = new List<Vector3Int>(m_NeighborPositions),
                    m_RuleTransform = m_RuleTransform,
                    m_Sprites = new Sprite[m_Sprites.Length],
                    m_GameObject = m_GameObject,
                    m_MinAnimationSpeed = m_MinAnimationSpeed,
                    m_MaxAnimationSpeed = m_MaxAnimationSpeed,
                    m_PerlinScale = m_PerlinScale,
                    m_Output = m_Output,
                    m_ColliderType = m_ColliderType,
                    m_RandomTransform = m_RandomTransform
                };
                Array.Copy(m_Sprites, rule.m_Sprites, m_Sprites.Length);
                return rule;
            }

            /// <summary>Returns all neighbors of this Tile as a dictionary</summary>
            public Dictionary<Vector3Int, int> GetNeighbors()
            {
                var dict = new Dictionary<Vector3Int, int>();

                for (var i = 0; i < m_Neighbors.Count && i < m_NeighborPositions.Count; i++)
                    dict.Add(m_NeighborPositions[i], m_Neighbors[i]);

                return dict;
            }

            /// <summary>Applies the values from the given dictionary as this Tile's neighbors</summary>
            public void ApplyNeighbors(Dictionary<Vector3Int, int> dict)
            {
                m_NeighborPositions = dict.Keys.ToList();
                m_Neighbors = dict.Values.ToList();
            }

            /// <summary>Gets the cell bounds of the TilingRule.</summary>
            public BoundsInt GetBounds()
            {
                var bounds = new BoundsInt(Vector3Int.zero, Vector3Int.one);
                foreach (var neighbor in GetNeighbors())
                {
                    bounds.xMin = Mathf.Min(bounds.xMin, neighbor.Key.x);
                    bounds.yMin = Mathf.Min(bounds.yMin, neighbor.Key.y);
                    bounds.xMax = Mathf.Max(bounds.xMax, neighbor.Key.x + 1);
                    bounds.yMax = Mathf.Max(bounds.yMax, neighbor.Key.y + 1);
                }

                return bounds;
            }
        }

        /// <summary>
        ///     Attribute which marks a property which cannot be overridden by a RuleOverrideTile
        /// </summary>
        public class DontOverride : Attribute
        {
        }
    }
}
