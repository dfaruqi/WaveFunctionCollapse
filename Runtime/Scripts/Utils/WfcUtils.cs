using System.Collections.Generic;

namespace MagusStudios.WaveFunctionCollapse.Utils
{
    public class WfcUtils
    {
        // Contains border information
        public struct Borders
        {
            public List<int> BorderUp;
            public List<int> BorderDown;
            public List<int> BorderLeft;
            public List<int> BorderRight;
        }
        
        public static readonly Direction[] AllDirectionOrders =
            new Direction[24 * 4] // Don't ask. It's for efficiency. 
            {
                // 0
                Direction.Up, Direction.Down, Direction.Left, Direction.Right,
                // 1
                Direction.Up, Direction.Down, Direction.Right, Direction.Left,
                // 2
                Direction.Up, Direction.Left, Direction.Down, Direction.Right,
                // 3
                Direction.Up, Direction.Left, Direction.Right, Direction.Down,
                // 4
                Direction.Up, Direction.Right, Direction.Down, Direction.Left,
                // 5
                Direction.Up, Direction.Right, Direction.Left, Direction.Down,

                // 6
                Direction.Down, Direction.Up, Direction.Left, Direction.Right,
                // 7
                Direction.Down, Direction.Up, Direction.Right, Direction.Left,
                // 8
                Direction.Down, Direction.Left, Direction.Up, Direction.Right,
                // 9
                Direction.Down, Direction.Left, Direction.Right, Direction.Up,
                // 10
                Direction.Down, Direction.Right, Direction.Up, Direction.Left,
                // 11
                Direction.Down, Direction.Right, Direction.Left, Direction.Up,

                // 12
                Direction.Left, Direction.Up, Direction.Down, Direction.Right,
                // 13
                Direction.Left, Direction.Up, Direction.Right, Direction.Down,
                // 14
                Direction.Left, Direction.Down, Direction.Up, Direction.Right,
                // 15
                Direction.Left, Direction.Down, Direction.Right, Direction.Up,
                // 16
                Direction.Left, Direction.Right, Direction.Up, Direction.Down,
                // 17
                Direction.Left, Direction.Right, Direction.Down, Direction.Up,

                // 18
                Direction.Right, Direction.Up, Direction.Down, Direction.Left,
                // 19
                Direction.Right, Direction.Up, Direction.Left, Direction.Down,
                // 20
                Direction.Right, Direction.Down, Direction.Up, Direction.Left,
                // 21
                Direction.Right, Direction.Down, Direction.Left, Direction.Up,
                // 22
                Direction.Right, Direction.Left, Direction.Up, Direction.Down,
                // 23
                Direction.Right, Direction.Left, Direction.Down, Direction.Up,
            };
    }
}