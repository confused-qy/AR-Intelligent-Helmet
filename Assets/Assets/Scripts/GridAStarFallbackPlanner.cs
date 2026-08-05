using System.Collections.Generic;
using UnityEngine;

namespace MotorcycleNavigation
{
    public sealed class GridAStarFallbackPlanner
    {
        private struct OpenItem
        {
            public int index;
            public float f;
        }

        private static readonly int[] Dx = { 0, -1, 1, 0 };
        private static readonly int[] Dy = { -1, 0, 0, 1 };

        public NavigationResult Plan(
            GridCostmap map,
            Vector3 start,
            Vector3 goal,
            PlannerSettings settings,
            NavigationRuleProviderBase rules = null)
        {
            NavigationResult result = new NavigationResult();
            if (map == null)
                return Fail(result, "Costmap is null.");

            int startX;
            int startY;
            int goalX;
            int goalY;
            if (!map.WorldToCell(start, out startX, out startY))
                return Fail(result, "Start pose is outside the costmap.");
            if (!map.WorldToCell(goal, out goalX, out goalY))
                return Fail(result, "Goal pose is outside the costmap.");
            if (!IsTraversable(map, startX, startY))
                return Fail(result, "Start cell is blocked.");
            if (!IsTraversable(map, goalX, goalY))
                return Fail(result, "Goal cell is blocked.");

            int cellCount = map.Width * map.Height;
            float[] gScore = new float[cellCount];
            int[] parent = new int[cellCount];
            bool[] closed = new bool[cellCount];
            for (int i = 0; i < cellCount; i++)
            {
                gScore[i] = float.PositiveInfinity;
                parent[i] = -1;
            }

            int startIndex = map.Index(startX, startY);
            int goalIndex = map.Index(goalX, goalY);
            gScore[startIndex] = 0f;

            MinHeap<OpenItem> open = new MinHeap<OpenItem>((a, b) => a.f.CompareTo(b.f));
            open.Push(new OpenItem { index = startIndex, f = Heuristic(map, startX, startY, goalX, goalY) });

            int expanded = 0;
            int maxExpanded = settings != null ? settings.maxExpandedNodes : 200000;
            while (open.Count > 0 && expanded < maxExpanded)
            {
                OpenItem item = open.Pop();
                if (closed[item.index])
                    continue;

                closed[item.index] = true;
                expanded++;

                if (item.index == goalIndex)
                {
                    Reconstruct(map, parent, startIndex, goalIndex, start.y, settings, result.path);
                    result.success = result.path.Count > 0;
                    result.expandedNodes = expanded;
                    if (!result.success)
                        result.failureReason = "Grid fallback reconstruction failed.";
                    return result;
                }

                int cx = item.index % map.Width;
                int cy = item.index / map.Width;
                for (int i = 0; i < Dx.Length; i++)
                {
                    int nx = cx + Dx[i];
                    int ny = cy + Dy[i];
                    if (!IsTraversable(map, nx, ny))
                        continue;

                    int nextIndex = map.Index(nx, ny);
                    if (closed[nextIndex])
                        continue;

                    float yaw = DirectionToYaw(Dx[i], Dy[i]);
                    Vector3 fromWorld = map.CellToWorld(cx, cy, start.y);
                    Vector3 toWorld = map.CellToWorld(nx, ny, start.y);
                    if (rules != null)
                    {
                        if (!rules.AllowsPose(map, nx, ny, yaw))
                            continue;

                        NavPose fromPose = new NavPose(fromWorld, yaw);
                        NavPose toPose = new NavPose(toWorld, yaw);
                        if (!rules.AllowsTransition(map, fromPose, toPose))
                            continue;
                    }

                    float ruleMultiplier = rules != null
                        ? Mathf.Max(0.01f, rules.TraversalMultiplier(map, nx, ny, yaw))
                        : 1f;
                    float stepCost = Mathf.Abs(Dx[i]) > 0 ? map.Resolution : map.ResolutionZ;
                    float cost = (stepCost + map.GetCost(nx, ny) / 255f) * ruleMultiplier;
                    float tentative = gScore[item.index] + cost;
                    if (tentative >= gScore[nextIndex])
                        continue;

                    parent[nextIndex] = item.index;
                    gScore[nextIndex] = tentative;
                    open.Push(new OpenItem
                    {
                        index = nextIndex,
                        f = tentative + Heuristic(map, nx, ny, goalX, goalY)
                    });
                }
            }

            result.expandedNodes = expanded;
            return Fail(result, expanded >= maxExpanded ? "Grid fallback reached maxExpandedNodes." : "Grid fallback open set exhausted.");
        }

        private static float DirectionToYaw(int dx, int dy)
        {
            return Mathf.Atan2(dx, dy) * Mathf.Rad2Deg;
        }

        private static bool IsTraversable(GridCostmap map, int x, int y)
        {
            if (!map.InBounds(x, y))
                return false;

            byte cost = map.GetCost(x, y);
            return cost != GridCostmap.NoInformation && cost < GridCostmap.InscribedInflatedObstacle;
        }

        private static float Heuristic(GridCostmap map, int x, int y, int goalX, int goalY)
        {
            float dx = Mathf.Abs(goalX - x) * map.Resolution;
            float dy = Mathf.Abs(goalY - y) * map.ResolutionZ;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private static void Reconstruct(
            GridCostmap map,
            int[] parent,
            int startIndex,
            int goalIndex,
            float worldY,
            PlannerSettings settings,
            List<NavPose> output)
        {
            output.Clear();
            List<int> indices = new List<int>();
            int current = goalIndex;
            while (current >= 0)
            {
                indices.Add(current);
                if (current == startIndex)
                    break;
                current = parent[current];
            }

            if (indices.Count == 0 || indices[indices.Count - 1] != startIndex)
                return;

            indices.Reverse();
            List<int> simplified = Simplify(indices, map);
            for (int i = 0; i < simplified.Count; i++)
            {
                int index = simplified[i];
                int x = index % map.Width;
                int y = index / map.Width;
                Vector3 position = map.CellToWorld(x, y, worldY);
                float yaw = 0f;
                if (i < simplified.Count - 1)
                {
                    int next = simplified[i + 1];
                    Vector3 nextPosition = map.CellToWorld(next % map.Width, next / map.Width, worldY);
                    Vector3 delta = nextPosition - position;
                    yaw = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
                }
                else if (output.Count > 0)
                {
                    yaw = output[output.Count - 1].yawDeg;
                }

                output.Add(new NavPose(position, yaw));
            }

        }

        private static List<int> Simplify(List<int> indices, GridCostmap map)
        {
            if (indices.Count <= 2)
                return indices;

            List<int> simplified = new List<int>();
            simplified.Add(indices[0]);

            int lastDx = 0;
            int lastDy = 0;
            for (int i = 1; i < indices.Count; i++)
            {
                int previous = indices[i - 1];
                int current = indices[i];
                int dx = current % map.Width - previous % map.Width;
                int dy = current / map.Width - previous / map.Width;

                if (i == 1)
                {
                    lastDx = dx;
                    lastDy = dy;
                    continue;
                }

                if (dx != lastDx || dy != lastDy)
                {
                    simplified.Add(previous);
                    lastDx = dx;
                    lastDy = dy;
                }
            }

            simplified.Add(indices[indices.Count - 1]);
            return simplified;
        }

        private static NavigationResult Fail(NavigationResult result, string reason)
        {
            result.success = false;
            result.failureReason = reason;
            return result;
        }
    }
}
