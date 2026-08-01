using System.Collections.Generic;
using UnityEngine;

namespace MotorcycleNavigation
{
    public sealed class PoseAStarPlanner
    {
        private sealed class NodeRecord
        {
            public int key;
            public int x;
            public int y;
            public int heading;
            public float g;
            public float f;
            public int parentKey;
            public bool closed;
        }

        private struct OpenItem
        {
            public int key;
            public float f;
        }

        private GridCostmap map;
        private PlannerSettings settings;
        private FootprintCollisionChecker collision;
        private NavigationRuleProviderBase rules;
        private int cellsPerHeadingLayer;
        private float worldY;
        private int rejectedOutOfBounds;
        private int rejectedDuplicate;
        private int rejectedNoInformation;
        private int rejectedObstacle;
        private int rejectedPoseRule;
        private int rejectedTransitionRule;
        private int rejectedCollision;
        private int acceptedNeighbors;

        public NavigationResult Plan(
            GridCostmap costmap,
            NavPose start,
            NamedGoal goal,
            PlannerSettings plannerSettings,
            FootprintCollisionChecker collisionChecker,
            NavigationRuleProviderBase ruleProvider)
        {
            NavPose goalPose = new NavPose(goal.worldPosition, goal.yawDeg);
            bool requireGoalYaw = goal.requireYaw || plannerSettings.requireGoalYawByDefault;
            return Plan(costmap, start, goalPose, requireGoalYaw, plannerSettings, collisionChecker, ruleProvider);
        }

        public NavigationResult Plan(
            GridCostmap costmap,
            NavPose start,
            NavPose goal,
            bool requireGoalYaw,
            PlannerSettings plannerSettings,
            FootprintCollisionChecker collisionChecker,
            NavigationRuleProviderBase ruleProvider)
        {
            NavigationResult result = new NavigationResult();
            map = costmap;
            settings = plannerSettings;
            collision = collisionChecker;
            rules = ruleProvider;
            worldY = start.position.y;
            ResetDebugCounters();

            if (map == null)
                return Fail(result, "Costmap is null.");
            if (collision == null)
                return Fail(result, "Collision checker is null.");
            if (settings == null)
                return Fail(result, "Planner settings are null.");

            int startX;
            int startY;
            int goalX;
            int goalY;
            if (!map.WorldToCell(start.position, out startX, out startY))
                return Fail(result, "Start pose is outside the costmap.");
            if (!map.WorldToCell(goal.position, out goalX, out goalY))
                return Fail(result, "Goal pose is outside the costmap.");

            if (!collision.IsPoseFree(start))
                return Fail(result, "Start footprint is in collision.");

            NavPose checkedGoal = new NavPose(map.CellToWorld(goalX, goalY, worldY), goal.yawDeg);
            if (!collision.IsPoseFree(checkedGoal))
                return Fail(result, "Goal footprint is in collision.");

            cellsPerHeadingLayer = map.Width * map.Height;
            int startHeading = HeadingToIndex(start.yawDeg);
            int startKey = Key(startX, startY, startHeading);

            Dictionary<int, NodeRecord> records = new Dictionary<int, NodeRecord>();
            MinHeap<OpenItem> open = new MinHeap<OpenItem>((a, b) => a.f.CompareTo(b.f));

            NodeRecord startRecord = new NodeRecord
            {
                key = startKey,
                x = startX,
                y = startY,
                heading = startHeading,
                g = 0f,
                f = Heuristic(startX, startY, startHeading, goalX, goalY, goal.yawDeg, requireGoalYaw),
                parentKey = -1
            };

            records[startKey] = startRecord;
            open.Push(new OpenItem { key = startKey, f = startRecord.f });

            NodeRecord bestGoal = null;
            int expanded = 0;

            while (open.Count > 0 && expanded < settings.maxExpandedNodes)
            {
                OpenItem item = open.Pop();
                NodeRecord current;
                if (!records.TryGetValue(item.key, out current) || current.closed)
                    continue;

                current.closed = true;
                expanded++;

                if (IsGoal(current, goalX, goalY, goal.yawDeg, requireGoalYaw))
                {
                    bestGoal = current;
                    break;
                }

                Expand(current, goalX, goalY, goal.yawDeg, requireGoalYaw, records, open);
            }

            result.expandedNodes = expanded;

            if (bestGoal == null)
            {
                string reason = expanded >= settings.maxExpandedNodes
                    ? "Planner reached maxExpandedNodes."
                    : "Open set exhausted.";
                reason += DebugCounterSummary(expanded);
                return Fail(result, reason);
            }

            Reconstruct(bestGoal, records, result.path);
            result.success = result.path.Count > 0;
            if (!result.success)
                result.failureReason = "Path reconstruction failed.";
            return result;
        }

        private void Expand(
            NodeRecord current,
            int goalX,
            int goalY,
            float goalYaw,
            bool requireGoalYaw,
            Dictionary<int, NodeRecord> records,
            MinHeap<OpenItem> open)
        {
            int stepCells = Mathf.Max(1, Mathf.RoundToInt(settings.stepMeters / map.Resolution));
            float headingStepRad = 2f * Mathf.PI / settings.headingBins;
            float maxYawChangeRad = settings.minTurningRadiusMeters <= 0f
                ? headingStepRad
                : settings.stepMeters / settings.minTurningRadiusMeters;
            int maxTurnBins = Mathf.Max(1, Mathf.FloorToInt(maxYawChangeRad / headingStepRad + 0.001f));

            float currentYaw = IndexToHeading(current.heading);
            HashSet<int> generated = new HashSet<int>();

            for (int deltaHeading = -maxTurnBins; deltaHeading <= maxTurnBins; deltaHeading++)
            {
                int nextHeading = WrapHeading(current.heading + deltaHeading);
                float nextYaw = IndexToHeading(nextHeading);
                float moveYaw = Mathf.LerpAngle(currentYaw, nextYaw, 0.5f);

                int dx = Mathf.RoundToInt(Mathf.Sin(moveYaw * Mathf.Deg2Rad) * stepCells);
                int dy = Mathf.RoundToInt(Mathf.Cos(moveYaw * Mathf.Deg2Rad) * stepCells);
                if (dx == 0 && dy == 0)
                    continue;

                int nx = current.x + dx;
                int ny = current.y + dy;
                if (!map.InBounds(nx, ny))
                {
                    rejectedOutOfBounds++;
                    continue;
                }

                int nextKey = Key(nx, ny, nextHeading);
                if (!generated.Add(nextKey))
                {
                    rejectedDuplicate++;
                    continue;
                }

                byte cellCost = map.GetCost(nx, ny);
                if (cellCost == GridCostmap.NoInformation)
                {
                    rejectedNoInformation++;
                    continue;
                }
                if (cellCost >= GridCostmap.InscribedInflatedObstacle)
                {
                    rejectedObstacle++;
                    continue;
                }

                Vector3 nextWorld = map.CellToWorld(nx, ny, worldY);
                NavPose fromPose = new NavPose(map.CellToWorld(current.x, current.y, worldY), currentYaw);
                NavPose toPose = new NavPose(nextWorld, nextYaw);

                if (rules != null)
                {
                    if (!rules.AllowsPose(map, nx, ny, nextYaw))
                    {
                        rejectedPoseRule++;
                        continue;
                    }
                    if (!rules.AllowsTransition(map, fromPose, toPose))
                    {
                        rejectedTransitionRule++;
                        continue;
                    }
                }

                if (!collision.IsSegmentFree(fromPose, toPose))
                {
                    rejectedCollision++;
                    continue;
                }

                float distance = Vector2.Distance(
                    new Vector2(current.x * map.Resolution, current.y * map.ResolutionZ),
                    new Vector2(nx * map.Resolution, ny * map.ResolutionZ));
                float turnCost = Mathf.Abs(deltaHeading) * settings.turnPenalty;
                float inflatedCost = settings.inflatedCostWeight * (cellCost / (float)(GridCostmap.InscribedInflatedObstacle - 1));
                float laneMultiplier = rules != null ? Mathf.Max(0.01f, rules.TraversalMultiplier(map, nx, ny, nextYaw)) : 1f;
                if (rules != null)
                    laneMultiplier *= Mathf.Max(0.01f, rules.TransitionMultiplier(map, fromPose, toPose));
                float lanePenalty = laneMultiplier > 1f ? (laneMultiplier - 1f) * settings.laneMismatchPenalty : 0f;

                float tentativeG = current.g + distance * laneMultiplier + turnCost + inflatedCost + lanePenalty;

                NodeRecord oldRecord;
                if (records.TryGetValue(nextKey, out oldRecord) && tentativeG >= oldRecord.g)
                    continue;

                NodeRecord nextRecord = oldRecord ?? new NodeRecord { key = nextKey };
                nextRecord.x = nx;
                nextRecord.y = ny;
                nextRecord.heading = nextHeading;
                nextRecord.g = tentativeG;
                nextRecord.f = tentativeG + Heuristic(nx, ny, nextHeading, goalX, goalY, goalYaw, requireGoalYaw);
                nextRecord.parentKey = current.key;
                nextRecord.closed = false;

                records[nextKey] = nextRecord;
                open.Push(new OpenItem { key = nextKey, f = nextRecord.f });
                acceptedNeighbors++;
            }
        }

        private void ResetDebugCounters()
        {
            rejectedOutOfBounds = 0;
            rejectedDuplicate = 0;
            rejectedNoInformation = 0;
            rejectedObstacle = 0;
            rejectedPoseRule = 0;
            rejectedTransitionRule = 0;
            rejectedCollision = 0;
            acceptedNeighbors = 0;
        }

        private string DebugCounterSummary(int expanded)
        {
            return string.Format(
                " expanded={0} accepted={1} rejectedOut={2} rejectedDuplicate={3} rejectedUnknown={4} rejectedObstacle={5} rejectedPoseRule={6} rejectedTransitionRule={7} rejectedCollision={8}",
                expanded,
                acceptedNeighbors,
                rejectedOutOfBounds,
                rejectedDuplicate,
                rejectedNoInformation,
                rejectedObstacle,
                rejectedPoseRule,
                rejectedTransitionRule,
                rejectedCollision);
        }

        private bool IsGoal(NodeRecord node, int goalX, int goalY, float goalYaw, bool requireGoalYaw)
        {
            float dist = Vector2.Distance(
                new Vector2(node.x * map.Resolution, node.y * map.ResolutionZ),
                new Vector2(goalX * map.Resolution, goalY * map.ResolutionZ));
            if (dist > settings.goalToleranceMeters)
                return false;

            if (!requireGoalYaw)
                return true;

            return Mathf.Abs(Mathf.DeltaAngle(IndexToHeading(node.heading), goalYaw)) <= settings.goalYawToleranceDeg;
        }

        private float Heuristic(int x, int y, int heading, int goalX, int goalY, float goalYaw, bool requireGoalYaw)
        {
            float dist = Vector2.Distance(
                new Vector2(x * map.Resolution, y * map.ResolutionZ),
                new Vector2(goalX * map.Resolution, goalY * map.ResolutionZ));
            if (!requireGoalYaw)
                return dist;

            float yawPenalty = Mathf.Abs(Mathf.DeltaAngle(IndexToHeading(heading), goalYaw)) / 180f;
            return dist + yawPenalty;
        }

        private void Reconstruct(NodeRecord goal, Dictionary<int, NodeRecord> records, List<NavPose> output)
        {
            output.Clear();
            NodeRecord current = goal;
            while (current != null)
            {
                output.Add(new NavPose(map.CellToWorld(current.x, current.y, worldY), IndexToHeading(current.heading)));
                if (current.parentKey < 0)
                    break;
                records.TryGetValue(current.parentKey, out current);
            }
            output.Reverse();
        }

        private NavigationResult Fail(NavigationResult result, string reason)
        {
            result.success = false;
            result.failureReason = reason;
            return result;
        }

        private int Key(int x, int y, int heading)
        {
            return heading * cellsPerHeadingLayer + map.Index(x, y);
        }

        private int HeadingToIndex(float yawDeg)
        {
            float normalized = Mathf.Repeat(yawDeg, 360f);
            return Mathf.RoundToInt(normalized / (360f / settings.headingBins)) % settings.headingBins;
        }

        private float IndexToHeading(int heading)
        {
            return heading * (360f / settings.headingBins);
        }

        private int WrapHeading(int heading)
        {
            int h = heading % settings.headingBins;
            if (h < 0)
                h += settings.headingBins;
            return h;
        }
    }
}
