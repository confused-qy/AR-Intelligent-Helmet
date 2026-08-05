using UnityEngine;

namespace MotorcycleNavigation
{
    public static class InflationLayer2D
    {
        private struct CellData
        {
            public int index;
            public int x;
            public int y;
            public int sourceX;
            public int sourceY;
            public float distanceCells;
        }

        private static readonly int[] NeighborDx = { -1, 1, 0, 0, -1, -1, 1, 1 };
        private static readonly int[] NeighborDy = { 0, 0, -1, 1, -1, 1, -1, 1 };

        public static void Apply(GridCostmap map, InflationSettings settings)
        {
            if (map == null || settings == null)
                return;

            float minResolution = Mathf.Min(map.Resolution, map.ResolutionZ);
            int cellRadius = Mathf.CeilToInt(settings.inflationRadiusMeters / minResolution);
            if (cellRadius <= 0)
                return;

            byte[] original = new byte[map.Costs.Length];
            System.Array.Copy(map.Costs, original, original.Length);

            bool[] seen = new bool[map.Costs.Length];
            MinHeap<CellData> queue = new MinHeap<CellData>((a, b) => a.distanceCells.CompareTo(b.distanceCells));

            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    int index = map.Index(x, y);
                    if (original[index] == GridCostmap.LethalObstacle)
                    {
                        queue.Push(new CellData
                        {
                            index = index,
                            x = x,
                            y = y,
                            sourceX = x,
                            sourceY = y,
                            distanceCells = 0f
                        });
                    }
                }
            }

            while (queue.Count > 0)
            {
                CellData cell = queue.Pop();
                if (seen[cell.index])
                    continue;

                seen[cell.index] = true;
                byte oldCost = map.Costs[cell.index];
                byte newCost = ComputeCost(cell.distanceCells, minResolution, settings);

                if (oldCost == GridCostmap.NoInformation)
                {
                    if (settings.inflateUnknown && newCost > GridCostmap.FreeSpace)
                        map.Costs[cell.index] = newCost;
                }
                else if (newCost > oldCost)
                {
                    map.Costs[cell.index] = newCost;
                }

                for (int i = 0; i < NeighborDx.Length; i++)
                {
                    int nx = cell.x + NeighborDx[i];
                    int ny = cell.y + NeighborDy[i];
                    if (!map.InBounds(nx, ny))
                        continue;

                    int nextIndex = map.Index(nx, ny);
                    if (seen[nextIndex])
                        continue;

                    float dx = nx - cell.sourceX;
                    float dy = ny - cell.sourceY;
                    float distanceCells = Mathf.Sqrt(dx * dx + dy * dy);
                    if (distanceCells > cellRadius)
                        continue;

                    queue.Push(new CellData
                    {
                        index = nextIndex,
                        x = nx,
                        y = ny,
                        sourceX = cell.sourceX,
                        sourceY = cell.sourceY,
                        distanceCells = distanceCells
                    });
                }
            }
        }

        public static byte ComputeCost(float distanceCells, float resolution, InflationSettings settings)
        {
            if (distanceCells <= 0f)
                return GridCostmap.LethalObstacle;

            float distanceMeters = distanceCells * resolution;
            if (distanceMeters <= settings.inscribedRadiusMeters)
                return GridCostmap.InscribedInflatedObstacle;

            float factor = Mathf.Exp(-settings.costScalingFactor * (distanceMeters - settings.inscribedRadiusMeters));
            int cost = Mathf.RoundToInt((GridCostmap.InscribedInflatedObstacle - 1) * factor);
            return (byte)Mathf.Clamp(cost, 1, GridCostmap.InscribedInflatedObstacle - 1);
        }
    }
}
