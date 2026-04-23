using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MagusStudios.WaveFunctionCollapse
{
    /// <summary>
    /// A burst-compilable, preallocated, fast implementation of Wave Function Collapse.
    /// </summary>
    [BurstCompile]
    public struct WfcJob : IJob
    {
        // Lookup structures - immutable, for reference only, and accessible in parallel

        // module constraints
        [ReadOnly] [NativeDisableParallelForRestriction]
        public NativeParallelHashMap<int, AllowedNeighborModule> Modules;

        [ReadOnly] [NativeDisableParallelForRestriction]
        public NativeArray<Direction> AllDirectionPermutations;

        // weights
        [ReadOnly]
        public NativeParallelHashMap<int, float> Weights;

        [ReadOnly] public NativeArray<int> UpBorder;
        [ReadOnly] public NativeArray<int> DownBorder;
        [ReadOnly] public NativeArray<int> LeftBorder;
        [ReadOnly] public NativeArray<int> RightBorder;

        // Algorithm State

        // domains
        public NativeArray<Cell> Cells; // this represents the grid of cells being operated upon
        // each cell has a domain and a "selected" field (for efficiency) , which
        // is -1 until the cell has collapsed to one outcome, in which case it is 
        // set as the id of the selected tile for this cell

        // entropy
        public NativeHeap<CellEntropy, EntropyComparer> EntropyHeap;
        public NativeArray<NativeHeapIndex> EntropyIndices;

        // map size and cell count
        public int Width;
        public int Height;

        // rng
        public Unity.Mathematics.Random random;

        // stack for propagation step
        public NativeArray<int> PropagationStack;
        public int PropagationStackTop;

        // output
        public NativeArray<int> Output;

        // state of operation (error, ok)
        public State Flag;

        public enum State
        {
            OK,
            ERROR
        }

        /// <summary>
        /// Represents one cell in the (flattened) grid that Wave Function Collapse operates upon. 
        /// Its domain of tiles is hard capped at 128. 
        /// </summary>
        public struct Cell
        {
            public ulong domainMask0;
            public ulong domainMask1;
            public int domainCount;
            public int selected;

            public static Cell CreateWithAllTiles(int size)
            {
                Cell cell = default;

                if (size <= 0)
                {
                    cell.domainMask0 = 0UL;
                    cell.domainMask1 = 0UL;
                    cell.domainCount = 0;
                    return cell;
                }

                if (size >= 128)
                {
                    // all 128 bits set
                    cell.domainMask0 = ulong.MaxValue;
                    cell.domainMask1 = ulong.MaxValue;
                    cell.domainCount = 128;
                    return cell;
                }

                cell.domainCount = size;

                if (size <= 64)
                {
                    // lower bits only
                    cell.domainMask0 = (size == 64) ? ulong.MaxValue : ((1UL << size) - 1UL);
                    cell.domainMask1 = 0UL;
                }
                else
                {
                    // fill lower 64 bits, then set upper (n-64) bits
                    cell.domainMask0 = ulong.MaxValue;
                    int highBits = size - 64; // 1..63
                    cell.domainMask1 = ((1UL << highBits) - 1UL);
                }

                cell.selected = -1;

                return cell;
            }

            public void Collapse(int tileId)
            {
                // Clear everything first
                domainMask0 = 0UL;
                domainMask1 = 0UL;

                if ((uint)tileId >= 128)
                {
                    // Out-of-range tile: collapsed to nothing
                    domainCount = 0;
                    selected = -1;
                    return;
                }

                domainCount = 1;

                if (tileId < 64)
                {
                    domainMask0 = 1UL << tileId;
                }
                else
                {
                    domainMask1 = 1UL << (tileId - 64);
                }

                selected = tileId;
            }
        }

        public struct CellEntropy
        {
            public int Id;
            public float Entropy;
        }

        public struct EntropyComparer : IComparer<CellEntropy>
        {
            public int Compare(CellEntropy x, CellEntropy y)
            {
                return x.Entropy.CompareTo(y.Entropy);
            }
        }

        /// <summary>
        /// Query allNeighbors with the indices and counts here to find all the neighbor data
        /// </summary>
        public struct AllowedNeighborModule
        {
            public ulong allowedUp0;
            public ulong allowedUp1;
            public ulong allowedDown0;
            public ulong allowedDown1;
            public ulong allowedLeft0;
            public ulong allowedLeft1;
            public ulong allowedRight0;
            public ulong allowedRight1;
        }

        public void Execute()
        {
            // Initialize all cells to the starting entropy
            for (int i = 0; i < Cells.Length; i++)
            {
                NativeHeapIndex nativeHeapIndex = EntropyHeap.Insert(new CellEntropy()
                {
                    Entropy = GetEntropy(i),
                    Id = i
                });
                EntropyIndices[i] = nativeHeapIndex;
            }

            // Enforce any constraints from optional borders of other maps

            // top border
            int index = (Height - 1) * Width;
            for (int i = 0; i < UpBorder.Length; i++)
            {
                int enforcerTile = UpBorder[i];
                if (enforcerTile < 0)
                {
                    index++;
                    continue;
                }

                ulong enforcerDomain0 = 0;
                ulong enforcerDomain1 = 0;
                if (enforcerTile < 64)
                {
                    enforcerDomain0 = (1UL << (enforcerTile));
                }
                else
                {
                    enforcerDomain1 = (1UL << (enforcerTile - 64));
                }

                if (ConstrainCell(index, enforcerDomain0, enforcerDomain1, Direction.Down))
                    PushToPropagationStack(index);

                index++;
            }

            // bottom border
            index = 0;
            for (int i = 0; i < DownBorder.Length; i++)
            {
                int enforcerTile = DownBorder[i];
                if (enforcerTile < 0)
                {
                    index++;
                    continue;
                }

                ulong enforcerDomain0 = 0;
                ulong enforcerDomain1 = 0;
                if (enforcerTile < 64)
                {
                    enforcerDomain0 = (1UL << (enforcerTile));
                }
                else
                {
                    enforcerDomain1 = (1UL << (enforcerTile - 64));
                }

                if (ConstrainCell(index, enforcerDomain0, enforcerDomain1, Direction.Up))
                    PushToPropagationStack(index);

                index++;
            }

            // left border
            index = 0;
            for (int i = 0; i < LeftBorder.Length; i++)
            {
                int enforcerTile = LeftBorder[i];
                if (enforcerTile < 0)
                {
                    index++;
                    continue;
                }

                ulong enforcerDomain0 = 0;
                ulong enforcerDomain1 = 0;
                if (enforcerTile < 64)
                {
                    enforcerDomain0 = (1UL << (enforcerTile));
                }
                else
                {
                    enforcerDomain1 = (1UL << (enforcerTile - 64));
                }

                if (ConstrainCell(index, enforcerDomain0, enforcerDomain1, Direction.Right))
                    PushToPropagationStack(index);

                index += Width;
            }

            // right border
            index = Width - 1;
            for (int i = 0; i < RightBorder.Length; i++)
            {
                int enforcerTile = RightBorder[i];
                if (enforcerTile < 0)
                {
                    index++;
                    continue;
                }

                ulong enforcerDomain0 = 0;
                ulong enforcerDomain1 = 0;
                if (enforcerTile < 64)
                {
                    enforcerDomain0 = (1UL << (enforcerTile));
                }
                else
                {
                    enforcerDomain1 = (1UL << (enforcerTile - 64));
                }

                if (ConstrainCell(index, enforcerDomain0, enforcerDomain1, Direction.Left))
                    PushToPropagationStack(index);

                index += Width;
            }

            // propagate any constraints from borders that were pushed to the propagation stack
            Propagate();

            // Execute passes of the algorithm until complete
            bool done = false;
            while (!done)
            {
                done = WaveFunctionCollapse();
            }

            // Prepare output
            for (int i = 0; i < Width * Height; i++)
            {
                Output[i] = Cells[i].selected;
            }
        }

        // One pass of the main algorithm, which collapses one lowest-entropy cell then recursively propagates constraints
        // to neighboring tiles until no more constraint propagation is possible.
        private bool WaveFunctionCollapse()
        {
            // Collapse a random lowest-entropy cell
            int selectedCell = GetRandomLowestEntropyCell();
            if (selectedCell == -1)
                return true; // Algorithm finished

            CollapseCell(selectedCell);

            // Push the initial collapsed cell
            PushToPropagationStack(selectedCell);

            Propagate();

            return false;
        }

        private void Propagate()
        {
            // Propagation loop
            while (PropagationStackTop > 0)
            {
                int enforcerCellIndex = PopFromPropagationStack();

                // If a cell has entropy 0 it has no possibilities → no need to propagate
                if (Cells[enforcerCellIndex].domainCount == 0)
                    continue;

                int x = enforcerCellIndex % Width;
                int y = enforcerCellIndex / Width;

                int perm = random.NextInt(24);
                int baseIdx = perm * 4;

                Cell enforcerCell = Cells[enforcerCellIndex];
                ulong enforcerDomain0 = enforcerCell.domainMask0;
                ulong enforcerDomain1 = enforcerCell.domainMask1;

                for (int i = 0; i < 4; i++)
                {
                    switch (AllDirectionPermutations[baseIdx + i])
                    {
                        case Direction.Up:
                            if (y + 1 < Height)
                            {
                                int neighborIndex = enforcerCellIndex + Width;
                                if (ConstrainCell(neighborIndex, enforcerDomain0, enforcerDomain1, Direction.Up))
                                    PushToPropagationStack(neighborIndex);
                            }

                            break;

                        case Direction.Down:
                            if (y - 1 >= 0)
                            {
                                int neighborIndex = enforcerCellIndex - Width;
                                if (ConstrainCell(neighborIndex, enforcerDomain0, enforcerDomain1, Direction.Down))
                                    PushToPropagationStack(neighborIndex);
                            }

                            break;

                        case Direction.Left:
                            if (x - 1 >= 0)
                            {
                                int neighborIndex = enforcerCellIndex - 1;
                                if (ConstrainCell(neighborIndex, enforcerDomain0, enforcerDomain1, Direction.Left))
                                    PushToPropagationStack(neighborIndex);
                            }

                            break;

                        case Direction.Right:
                            if (x + 1 < Width)
                            {
                                int neighborIndex = enforcerCellIndex + 1;
                                if (ConstrainCell(neighborIndex, enforcerDomain0, enforcerDomain1, Direction.Right))
                                    PushToPropagationStack(neighborIndex);
                            }

                            break;
                    }
                }
            }

            // Reset the stack for the next collapse cycle
            PropagationStackTop = 0;
        }


        void PushToPropagationStack(int v) => PropagationStack[PropagationStackTop++] = v;
        int PopFromPropagationStack() => PropagationStack[--PropagationStackTop];


        private int GetRandomLowestEntropyCell()
        {
            if (EntropyHeap.Count == 0) return -1; // Algorithm complete.

            return EntropyHeap.Peek().Id;
        }

        private void UpdateEntropy(int cellId)
        {
            float newEntropy = GetEntropy(cellId);

            // When we retrieve the lowest entropy cell, we want to ignore any already collapsed cells, so we remove 
            // them from the entropy heap
            if (newEntropy <= 0)
            {
                NativeHeapIndex index = EntropyIndices[cellId];
                if (!EntropyHeap.IsValidIndex(index)) return;
                EntropyHeap.Remove(index);
                return;
            }

            EntropyHeap.Remove(EntropyIndices[cellId]);
            NativeHeapIndex nativeHeapIndex =
                EntropyHeap.Insert(new CellEntropy() { Entropy = newEntropy, Id = cellId });
            EntropyIndices[cellId] = nativeHeapIndex;
        }

        // Calculate entropy based on a cell's domain 
        private float GetEntropy(int cellId)
        {
            var cell = Cells[cellId];

            // If domain is empty or has only one element, entropy is 0
            if (cell.domainCount <= 1)
            {
                return 0f;
            }

            // Calculate sum of weights for normalization
            float sumWeights = 0f;

            // Check domainMask0 (tiles 0-63)
            ulong mask0 = cell.domainMask0;
            for (int bitIndex = 0; bitIndex < 64; bitIndex++)
            {
                if ((mask0 & (1UL << bitIndex)) != 0)
                {
                    if (!Weights.TryGetValue(bitIndex, out float weight))
                    {
                        continue;
                    }

                    sumWeights += weight;
                }
            }

            // Check domainMask1 (tiles 64-127)
            ulong mask1 = cell.domainMask1;
            for (int bitIndex = 0; bitIndex < 64; bitIndex++)
            {
                if ((mask1 & (1UL << bitIndex)) != 0)
                {
                    if (Weights.TryGetValue(64 + bitIndex, out float weight))
                    {
                        sumWeights += weight;
                    }
                }
            }

            // Avoid division by zero
            if (sumWeights <= 0f)
            {
                return 0f;
            }

            // Calculate Shannon entropy: H = -Σ(p_i * log(p_i))
            float entropy = 0f;

            // Check domainMask0
            mask0 = cell.domainMask0;
            for (int bitIndex = 0; bitIndex < 64; bitIndex++)
            {
                if ((mask0 & (1UL << bitIndex)) != 0)
                {
                    if (Weights.TryGetValue(bitIndex, out float weight))
                    {
                        float probability = weight / sumWeights;
                        if (probability > 0f)
                        {
                            entropy -= probability * math.log2(probability);
                        }
                    }
                }
            }

            // Check domainMask1
            mask1 = cell.domainMask1;
            for (int bitIndex = 0; bitIndex < 64; bitIndex++)
            {
                if ((mask1 & (1UL << bitIndex)) != 0)
                {
                    if (Weights.TryGetValue(64 + bitIndex, out float weight))
                    {
                        float probability = weight / sumWeights;
                        if (probability > 0f)
                        {
                            entropy -= probability * math.log2(probability);
                        }
                    }
                }
            }

            return entropy;
        }

        private void CollapseCell(int cellId)
        {
            var cell = Cells[cellId];

            // If already collapsed, exit (this represents a standard constraint error in generation)
            if (cell.domainCount <= 1)
            {
                if (Flag == State.OK)
                    Flag = State.ERROR;
                return;
            }

            // Calculate total weight by iterating through set bits
            float totalWeight = 0;

            int tileCount = Weights.Count();

            // Check domainMask0 (tiles 0-63)
            ulong mask0 = cell.domainMask0;
            for (int bitIndex = 0; bitIndex < tileCount && bitIndex < 64; bitIndex++)
            {
                if ((mask0 & (1UL << bitIndex)) != 0)
                {
                    totalWeight += Weights[bitIndex];
                }
            }

            // Check domainMask1 (tiles 64-127)
            ulong mask1 = cell.domainMask1;
            for (int bitIndex = 0; bitIndex + 64 < tileCount && bitIndex < 64; bitIndex++)
            {
                if ((mask1 & (1UL << bitIndex)) != 0)
                {
                    totalWeight += Weights[64 + bitIndex];
                }
            }

            // Make weighted random choice
            float choice = random.NextFloat() * totalWeight;
            float cumulative = 0;
            int selected = -1;

            // Check domainMask0
            mask0 = cell.domainMask0;
            for (int bitIndex = 0; bitIndex < tileCount && bitIndex < 64; bitIndex++)
            {
                if ((mask0 & (1UL << bitIndex)) != 0)
                {
                    cumulative += Weights[bitIndex];
                    if (choice < cumulative)
                    {
                        selected = bitIndex;
                        break;
                    }
                }
            }

            // If not found in mask0, check domainMask1
            if (selected == -1)
            {
                mask1 = cell.domainMask1;
                for (int bitIndex = 0; bitIndex + 64 < tileCount && bitIndex < 64; bitIndex++)
                {
                    if ((mask1 & (1UL << bitIndex)) != 0)
                    {
                        cumulative += Weights[64 + bitIndex];
                        if (choice < cumulative)
                        {
                            selected = 64 + bitIndex;
                            break;
                        }
                    }
                }
            }

            // Collapse domain to single selected value
            cell.Collapse(selected);
            Cells[cellId] = cell;

            // Update entropy
            UpdateEntropy(cellId);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="cellToConstrain">Cell that will be constrained</param>
        /// <param name="enforcerDomain0">Mask representing the domain of tile ids (0-63) enforcing neighbor constraints.</param>
        /// <param name="enforcerDomain1">Mask representing the domain of tile ids (64-127) enforcing neighbor constraints.</param>
        /// <param name="direction">Direction from enforcer cell to the cell to constrain</param>
        /// <returns></returns>
        private bool ConstrainCell(int cellToConstrain, ulong enforcerDomain0, ulong enforcerDomain1,
            Direction direction)
        {
            Cell cell = Cells[cellToConstrain];

            // In the domain of the enforcer cell, which tiles are allowed in [direction] for each remaining possibility?
            // We will OR-in all the possibilities declared in the module set. 
            ulong allowedMask0 = 0;
            ulong allowedMask1 = 0;

            // iterate through each possibility in the domain of the enforcer cell
            while (enforcerDomain0 != 0)
            {
                ulong lowestBit = enforcerDomain0 & (~enforcerDomain0 + 1); // isolate lowest set bit
                int index = math.tzcnt(lowestBit);

                switch (direction)
                {
                    case Direction.Up:
                        allowedMask0 |= Modules[index].allowedUp0;
                        allowedMask1 |= Modules[index].allowedUp1;
                        break;
                    case Direction.Down:
                        allowedMask0 |= Modules[index].allowedDown0;
                        allowedMask1 |= Modules[index].allowedDown1;
                        break;
                    case Direction.Left:
                        allowedMask0 |= Modules[index].allowedLeft0;
                        allowedMask1 |= Modules[index].allowedLeft1;
                        break;
                    case Direction.Right:
                        allowedMask0 |= Modules[index].allowedRight0;
                        allowedMask1 |= Modules[index].allowedRight1;
                        break;
                }

                enforcerDomain0 &= enforcerDomain0 - 1; // clear lowest set bit
            }

            while (enforcerDomain1 != 0)
            {
                ulong lowestBit = enforcerDomain1 & (~enforcerDomain1 + 1); // isolate lowest set bit
                int index = math.tzcnt(lowestBit) + 64; // offset into modules 64–127

                switch (direction)
                {
                    case Direction.Up:
                        allowedMask0 |= Modules[index].allowedUp0;
                        allowedMask1 |= Modules[index].allowedUp1;
                        break;
                    case Direction.Down:
                        allowedMask0 |= Modules[index].allowedDown0;
                        allowedMask1 |= Modules[index].allowedDown1;
                        break;
                    case Direction.Left:
                        allowedMask0 |= Modules[index].allowedLeft0;
                        allowedMask1 |= Modules[index].allowedLeft1;
                        break;
                    case Direction.Right:
                        allowedMask0 |= Modules[index].allowedRight0;
                        allowedMask1 |= Modules[index].allowedRight1;
                        break;
                }

                enforcerDomain1 &= enforcerDomain1 - 1; // clear lowest set bit
            }

            ulong constrainedMask0 = cell.domainMask0;
            ulong constrainedMask1 = cell.domainMask1;

            // constrain
            constrainedMask0 &= allowedMask0;
            constrainedMask1 &= allowedMask1;

            // No change → no propagation needed
            if (constrainedMask0 == cell.domainMask0 && constrainedMask1 == cell.domainMask1)
                return false;

            // Update cell
            cell.domainMask0 = constrainedMask0;
            cell.domainMask1 = constrainedMask1;

            // Recompute domain count
            cell.domainCount =
                math.countbits(constrainedMask0) +
                math.countbits(constrainedMask1);

            if (cell.domainCount == 0)
            {
                Cells[cellToConstrain] = cell;
                UpdateEntropy(cellToConstrain);

                Flag = State.ERROR; // A standard error where a cell's domain is constrained to nothing. 
                // Errors are expected with large module sets.

                return false; //don't propagate error cells
            }

            // Collapse cell if its domain is 1 element
            if (cell.domainCount == 1)
            {
                if (cell.domainMask0 != 0)
                {
                    // Bit is in [0..63]
                    cell.selected = math.tzcnt(cell.domainMask0);
                }
                else
                {
                    // Bit is in [64..127]
                    cell.selected = 64 + math.tzcnt(cell.domainMask1);
                }
            }

            Cells[cellToConstrain] = cell;

            UpdateEntropy(cellToConstrain);

            return true; // we constrained this cell to a smaller domain, so return true
        }
    }
}