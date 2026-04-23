using System.Collections.Generic;
using AYellowpaper.SerializedCollections;
using MagusStudios.WaveFunctionCollapse.Utils;
using Unity.Collections;
using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    public class WfcBlockState
    {
        public readonly NativeHeap<WfcJob.CellEntropy, WfcJob.EntropyComparer> EntropyHeap;
        public readonly NativeArray<NativeHeapIndex> EntropyIndices;
        public readonly NativeArray<WfcJob.Cell> Cells;
        public readonly NativeArray<int> PropagationStack;
        public readonly NativeArray<int> Output;
        public readonly NativeArray<int> UpBorder;
        public readonly NativeArray<int> DownBorder;
        public readonly NativeArray<int> LeftBorder;
        public readonly NativeArray<int> RightBorder;
        public NativeParallelHashMap<int, float> Weights;

        public WfcBlockState(Vector2Int size, int moduleCount, WfcTemplate template, Unity.Mathematics.Random rng, WfcUtils.Borders borders = default)
        {
            SerializedDictionary<int, WfcTileRules.AllowedNeighbors> moduleDict = template.TileRules.Modules;

            int cellCount = size.x * size.y;

            // weights
            Weights = new NativeParallelHashMap<int, float>(moduleDict.Count, Allocator.Persistent);
            int moduleIndex = 0;
            foreach (KeyValuePair<int, WfcTileRules.AllowedNeighbors> kvp in moduleDict)
            {
                Weights.Add(moduleIndex, template.Weights[kvp.Key]);
                moduleIndex++;
            }

            // entropy 
            EntropyHeap =
                new NativeHeap<WfcJob.CellEntropy, WfcJob.EntropyComparer>(Allocator.Persistent, cellCount);

            EntropyIndices =
                new NativeArray<NativeHeapIndex>(cellCount, Allocator.Persistent);

            // the starting entropy will be applied to all cells at the start of generation

            Cells = new NativeArray<WfcJob.Cell>(cellCount, Allocator.Persistent);

            // fill domains with all tiles to start
            for (int i = 0; i < cellCount; i++)
            {
                Cells[i] = WfcJob.Cell.CreateWithAllTiles(moduleCount);
            }

            // domains
            NativeArray<WfcJob.Cell> cells = new NativeArray<WfcJob.Cell>(cellCount, Allocator.Persistent);

            // fill domains with all tiles to start
            for (int i = 0;
                 i < cellCount;
                 i++)
            {
                cells[i] = WfcJob.Cell.CreateWithAllTiles(moduleCount);
            }

            // === Initialize Stack for Propagation Step
            PropagationStack = new NativeArray<int>(cellCount, Allocator.Persistent);

            // === Initialize Output Structure ===
            Output = new NativeArray<int>(cellCount, Allocator.Persistent);

            // Fill the optional borders
            int bordersUpCount = borders.BorderUp?.Count ?? 0;
            int bordersDownCount = borders.BorderDown?.Count ?? 0;
            int bordersRightCount = borders.BorderRight?.Count ?? 0;
            int bordersLeftCount = borders.BorderLeft?.Count ?? 0;

            UpBorder = new NativeArray<int>(Mathf.Min(size.x, bordersUpCount), Allocator.Persistent);
            DownBorder = new NativeArray<int>(Mathf.Min(size.x, bordersDownCount), Allocator.Persistent);
            LeftBorder = new NativeArray<int>(Mathf.Min(size.y, bordersLeftCount), Allocator.Persistent);
            RightBorder = new NativeArray<int>(Mathf.Min(size.y, bordersRightCount), Allocator.Persistent);

            // ───── UP ─────
            List<int> bordersUp = borders.BorderUp;
            if (bordersUp != null)
            {
                for (int i = 0; i < UpBorder.Length; i++)
                {
                    UpBorder[i] = bordersUp[i];
                }
            }

            // ───── DOWN ─────
            List<int> bordersDown = borders.BorderDown;
            if (bordersDown != null)
            {
                for (int i = 0; i < DownBorder.Length; i++)
                {
                    DownBorder[i] = bordersDown[i];
                }
            }

            // ───── LEFT ─────
            List<int> bordersLeft = borders.BorderLeft;
            if (bordersLeft != null)
            {
                for (int i = 0; i < LeftBorder.Length; i++)
                {
                    LeftBorder[i] = bordersLeft[i];
                }
            }

            // ───── RIGHT ─────
            List<int> bordersRight = borders.BorderRight;
            if (bordersRight != null)
            {
                for (int i = 0; i < RightBorder.Length; i++)
                {
                    RightBorder[i] = bordersRight[i];
                }
            }
        }

        public void Dispose()
        {
            EntropyHeap.Dispose();
            EntropyIndices.Dispose();
            Cells.Dispose();
            PropagationStack.Dispose();
            Output.Dispose();
            UpBorder.Dispose();
            DownBorder.Dispose();
            LeftBorder.Dispose();
            RightBorder.Dispose();
            Weights.Dispose();
        }
    }
}