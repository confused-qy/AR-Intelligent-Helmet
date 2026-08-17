using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MotorcycleNavigation
{
    /// <summary>
    /// Benchmark harness for PoseAStarPlanner.
    ///
    /// Usage:
    ///   1. Add this component to any GameObject in the navigation scene.
    ///   2. Assign the MotorcycleNavigationManager reference.
    ///   3. Enter Play Mode and wait until the costmap has been built.
    ///   4. Right-click the component header -> "Run Benchmark".
    ///
    /// Results are printed to the Console and written to a CSV under
    /// Application.persistentDataPath.
    /// </summary>
    public sealed class NavigationBenchmark : MonoBehaviour
    {
        [Header("Setup")]
        public MotorcycleNavigationManager manager;

        [Header("Trial Configuration")]
        [Tooltip("Number of randomized start/goal pairs to plan.")]
        public int trials = 200;

        [Tooltip("Fixed seed so results are reproducible across runs.")]
        public int randomSeed = 12345;

        [Tooltip("Reject start/goal pairs closer together than this.")]
        public float minStartGoalDistanceMeters = 15f;

        [Tooltip("How many times to retry sampling a valid free pose before giving up on a trial.")]
        public int maxSampleAttempts = 200;

        [Header("Comparisons")]
        [Tooltip("Also plan every pair with the position-only grid A* for a baseline comparison.")]
        public bool compareAgainstGridPlanner = true;

        [Tooltip("Also re-run every pair with the semantic rule layer disabled.")]
        public bool compareWithoutSemanticRules = true;

        [Header("Output")]
        public string csvFileName = "nav_benchmark.csv";

        private sealed class TrialRecord
        {
            public int index;
            public bool success;
            public double milliseconds;
            public int expandedNodes;
            public int waypoints;
            public float pathLengthMeters;
            public float totalHeadingChangeDeg;
            public string failureReason;
        }

        [ContextMenu("Run Benchmark")]
        public void RunBenchmark()
        {
            if (manager == null)
            {
                Debug.LogError("[Benchmark] Manager reference is not assigned.");
                return;
            }

            GridCostmap map = manager.InflatedCostmap;
            if (map == null)
            {
                Debug.LogError("[Benchmark] InflatedCostmap is null. Build the navigation map first (Play Mode, buildOnStart, or BuildNavigationMapFromCamera).");
                return;
            }

            PlannerSettings settings = manager.plannerSettings;
            FootprintCollisionChecker collision = new FootprintCollisionChecker(map, manager.footprint);

            ReportMapStatistics(map, settings);
            ReportInflationCost(map);

            List<int> freeCells = CollectFreeCells(map);
            if (freeCells.Count < 2)
            {
                Debug.LogError("[Benchmark] Fewer than two traversable cells found. Check the costmap build settings.");
                return;
            }

            Debug.Log($"[Benchmark] Traversable cells: {freeCells.Count:N0} of {map.Width * map.Height:N0} " +
                      $"({100f * freeCells.Count / (map.Width * map.Height):F1}% of grid)");

            List<TrialRecord> withRules = RunTrials(map, settings, collision, manager.ruleProvider, freeCells, "pose-A* + semantic rules");
            Summarize("Pose A* with semantic rules", withRules);

            List<TrialRecord> withoutRules = null;
            if (compareWithoutSemanticRules && manager.ruleProvider != null)
            {
                withoutRules = RunTrials(map, settings, collision, null, freeCells, "pose-A* no rules");
                Summarize("Pose A* without semantic rules", withoutRules);
            }

            List<TrialRecord> gridBaseline = null;
            if (compareAgainstGridPlanner)
            {
                gridBaseline = RunGridTrials(map, settings, freeCells);
                Summarize("Grid A* baseline (position only)", gridBaseline);
                CompareHeadingSmoothness(withRules, gridBaseline);
            }

            WriteCsv(withRules, withoutRules, gridBaseline);
        }

        // ------------------------------------------------------------------
        // Map-level statistics
        // ------------------------------------------------------------------

        private void ReportMapStatistics(GridCostmap map, PlannerSettings settings)
        {
            long cells = (long)map.Width * map.Height;
            long poseStates = cells * settings.headingBins;
            float widthMeters = map.Width * map.Resolution;
            float heightMeters = map.Height * map.ResolutionZ;

            Debug.Log(
                "[Benchmark] === Map ===\n" +
                $"Grid: {map.Width} x {map.Height} cells ({cells:N0} cells)\n" +
                $"Resolution: {map.Resolution:F3} m/cell (X), {map.ResolutionZ:F3} m/cell (Z)\n" +
                $"World coverage: {widthMeters:F1} m x {heightMeters:F1} m\n" +
                $"Heading bins: {settings.headingBins}\n" +
                $"Pose-space search states: {poseStates:N0}\n" +
                $"Step: {settings.stepMeters:F2} m | Min turning radius: {settings.minTurningRadiusMeters:F2} m\n" +
                $"Max expanded nodes: {settings.maxExpandedNodes:N0}");
        }

        private void ReportInflationCost(GridCostmap inflated)
        {
            GridCostmap raw = manager.RawCostmap;
            if (raw == null)
            {
                Debug.LogWarning("[Benchmark] RawCostmap unavailable; skipping inflation timing.");
                return;
            }

            GridCostmap copy = new GridCostmap(raw.Width, raw.Height, raw.Resolution, raw.ResolutionZ, raw.OriginXZ);
            Array.Copy(raw.Costs, copy.Costs, raw.Costs.Length);

            Stopwatch sw = Stopwatch.StartNew();
            InflationLayer2D.Apply(copy, manager.inflation);
            sw.Stop();

            Debug.Log($"[Benchmark] Inflation layer: {sw.Elapsed.TotalMilliseconds:F1} ms over {copy.Costs.Length:N0} cells " +
                      $"(radius {manager.inflation.inflationRadiusMeters:F2} m)");
        }

        private List<int> CollectFreeCells(GridCostmap map)
        {
            List<int> free = new List<int>();
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    byte cost = map.GetCost(x, y);
                    if (cost == GridCostmap.NoInformation)
                        continue;
                    if (cost >= GridCostmap.InscribedInflatedObstacle)
                        continue;
                    free.Add(map.Index(x, y));
                }
            }
            return free;
        }

        // ------------------------------------------------------------------
        // Trials
        // ------------------------------------------------------------------

        private List<TrialRecord> RunTrials(
            GridCostmap map,
            PlannerSettings settings,
            FootprintCollisionChecker collision,
            NavigationRuleProviderBase rules,
            List<int> freeCells,
            string label)
        {
            System.Random rng = new System.Random(randomSeed);
            PoseAStarPlanner planner = new PoseAStarPlanner();
            List<TrialRecord> records = new List<TrialRecord>(trials);
            float worldY = transform.position.y;

            for (int i = 0; i < trials; i++)
            {
                NavPose start;
                NavPose goal;
                if (!SamplePair(map, collision, freeCells, rng, worldY, out start, out goal))
                    continue;

                Stopwatch sw = Stopwatch.StartNew();
                NavigationResult result = planner.Plan(map, start, goal, false, settings, collision, rules);
                sw.Stop();

                records.Add(new TrialRecord
                {
                    index = i,
                    success = result.success,
                    milliseconds = sw.Elapsed.TotalMilliseconds,
                    expandedNodes = result.expandedNodes,
                    waypoints = result.path.Count,
                    pathLengthMeters = PathLength(result.path),
                    totalHeadingChangeDeg = TotalHeadingChange(result.path),
                    failureReason = result.failureReason
                });
            }

            Debug.Log($"[Benchmark] Completed {records.Count} trials for {label}.");
            return records;
        }

        private List<TrialRecord> RunGridTrials(GridCostmap map, PlannerSettings settings, List<int> freeCells)
        {
            System.Random rng = new System.Random(randomSeed);
            GridAStarFallbackPlanner planner = new GridAStarFallbackPlanner();
            FootprintCollisionChecker collision = new FootprintCollisionChecker(map, manager.footprint);
            List<TrialRecord> records = new List<TrialRecord>(trials);
            float worldY = transform.position.y;

            for (int i = 0; i < trials; i++)
            {
                NavPose start;
                NavPose goal;
                if (!SamplePair(map, collision, freeCells, rng, worldY, out start, out goal))
                    continue;

                Stopwatch sw = Stopwatch.StartNew();
                NavigationResult result = planner.Plan(map, start.position, goal.position, settings, manager.ruleProvider);
                sw.Stop();

                records.Add(new TrialRecord
                {
                    index = i,
                    success = result.success,
                    milliseconds = sw.Elapsed.TotalMilliseconds,
                    expandedNodes = result.expandedNodes,
                    waypoints = result.path.Count,
                    pathLengthMeters = PathLength(result.path),
                    totalHeadingChangeDeg = TotalHeadingChange(result.path),
                    failureReason = result.failureReason
                });
            }

            return records;
        }

        /// <summary>
        /// Samples a collision-free start and goal pose separated by at least minStartGoalDistanceMeters.
        /// Uses the same seeded rng sequence across planners so every planner sees identical pairs.
        /// </summary>
        private bool SamplePair(
            GridCostmap map,
            FootprintCollisionChecker collision,
            List<int> freeCells,
            System.Random rng,
            float worldY,
            out NavPose start,
            out NavPose goal)
        {
            start = default(NavPose);
            goal = default(NavPose);

            NavPose candidateStart;
            if (!SampleFreePose(map, collision, freeCells, rng, worldY, out candidateStart))
                return false;

            for (int attempt = 0; attempt < maxSampleAttempts; attempt++)
            {
                NavPose candidateGoal;
                if (!SampleFreePose(map, collision, freeCells, rng, worldY, out candidateGoal))
                    continue;

                float distance = Vector3.Distance(candidateStart.position, candidateGoal.position);
                if (distance < minStartGoalDistanceMeters)
                    continue;

                start = candidateStart;
                goal = candidateGoal;
                return true;
            }

            return false;
        }

        private bool SampleFreePose(
            GridCostmap map,
            FootprintCollisionChecker collision,
            List<int> freeCells,
            System.Random rng,
            float worldY,
            out NavPose pose)
        {
            for (int attempt = 0; attempt < maxSampleAttempts; attempt++)
            {
                int flatIndex = freeCells[rng.Next(freeCells.Count)];
                int x = flatIndex % map.Width;
                int y = flatIndex / map.Width;
                float yaw = (float)(rng.NextDouble() * 360.0);

                NavPose candidate = new NavPose(map.CellToWorld(x, y, worldY), yaw);
                if (collision.IsPoseFree(candidate))
                {
                    pose = candidate;
                    return true;
                }
            }

            pose = default(NavPose);
            return false;
        }

        // ------------------------------------------------------------------
        // Metrics
        // ------------------------------------------------------------------

        private static float PathLength(List<NavPose> path)
        {
            float total = 0f;
            for (int i = 1; i < path.Count; i++)
                total += Vector3.Distance(path[i - 1].position, path[i].position);
            return total;
        }

        private static float TotalHeadingChange(List<NavPose> path)
        {
            float total = 0f;
            for (int i = 1; i < path.Count; i++)
                total += Mathf.Abs(Mathf.DeltaAngle(path[i - 1].yawDeg, path[i].yawDeg));
            return total;
        }

        private void Summarize(string label, List<TrialRecord> records)
        {
            if (records == null || records.Count == 0)
            {
                Debug.LogWarning($"[Benchmark] {label}: no trials recorded.");
                return;
            }

            List<TrialRecord> succeeded = records.FindAll(r => r.success);
            int successCount = succeeded.Count;

            if (successCount == 0)
            {
                Debug.LogWarning($"[Benchmark] {label}: 0 / {records.Count} succeeded. " +
                                 $"First failure reason: {records[0].failureReason}");
                return;
            }

            List<double> times = succeeded.ConvertAll(r => r.milliseconds);
            times.Sort();

            List<double> expanded = succeeded.ConvertAll(r => (double)r.expandedNodes);
            expanded.Sort();

            double meanTime = 0.0;
            foreach (double t in times) meanTime += t;
            meanTime /= times.Count;

            double meanExpanded = 0.0;
            foreach (double e in expanded) meanExpanded += e;
            meanExpanded /= expanded.Count;

            float meanLength = 0f;
            float meanHeading = 0f;
            float meanWaypoints = 0f;
            foreach (TrialRecord r in succeeded)
            {
                meanLength += r.pathLengthMeters;
                meanHeading += r.totalHeadingChangeDeg;
                meanWaypoints += r.waypoints;
            }
            meanLength /= successCount;
            meanHeading /= successCount;
            meanWaypoints /= successCount;

            Debug.Log(
                $"[Benchmark] === {label} ===\n" +
                $"Success rate: {successCount} / {records.Count} ({100f * successCount / records.Count:F1}%)\n" +
                $"Planning time  mean {meanTime:F2} ms | median {Percentile(times, 0.50):F2} | " +
                $"p95 {Percentile(times, 0.95):F2} | max {times[times.Count - 1]:F2}\n" +
                $"Expanded nodes mean {meanExpanded:F0} | median {Percentile(expanded, 0.50):F0} | " +
                $"p95 {Percentile(expanded, 0.95):F0} | max {expanded[expanded.Count - 1]:F0}\n" +
                $"Path length mean {meanLength:F1} m | waypoints mean {meanWaypoints:F1}\n" +
                $"Total heading change mean {meanHeading:F1} deg");
        }

        private void CompareHeadingSmoothness(List<TrialRecord> pose, List<TrialRecord> grid)
        {
            if (pose == null || grid == null) return;

            float poseHeading = 0f;
            int poseCount = 0;
            foreach (TrialRecord r in pose)
            {
                if (!r.success) continue;
                poseHeading += r.totalHeadingChangeDeg;
                poseCount++;
            }

            float gridHeading = 0f;
            int gridCount = 0;
            foreach (TrialRecord r in grid)
            {
                if (!r.success) continue;
                gridHeading += r.totalHeadingChangeDeg;
                gridCount++;
            }

            if (poseCount == 0 || gridCount == 0) return;

            poseHeading /= poseCount;
            gridHeading /= gridCount;

            if (gridHeading <= 0f) return;

            float reduction = 100f * (gridHeading - poseHeading) / gridHeading;
            Debug.Log($"[Benchmark] === Comparison ===\n" +
                      $"Mean cumulative heading change: pose A* {poseHeading:F1} deg vs grid A* {gridHeading:F1} deg " +
                      $"({reduction:F1}% {(reduction >= 0f ? "smoother" : "rougher")}).");
        }

        private static double Percentile(List<double> sorted, double fraction)
        {
            if (sorted.Count == 0) return 0.0;
            int index = Mathf.Clamp(Mathf.RoundToInt((float)(fraction * (sorted.Count - 1))), 0, sorted.Count - 1);
            return sorted[index];
        }

        // ------------------------------------------------------------------
        // CSV
        // ------------------------------------------------------------------

        private void WriteCsv(List<TrialRecord> withRules, List<TrialRecord> withoutRules, List<TrialRecord> grid)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("planner,trial,success,ms,expanded_nodes,waypoints,path_length_m,heading_change_deg,failure_reason");

            AppendRows(sb, "pose_astar_with_rules", withRules);
            AppendRows(sb, "pose_astar_no_rules", withoutRules);
            AppendRows(sb, "grid_astar_baseline", grid);

            string path = Path.Combine(Application.persistentDataPath, csvFileName);
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[Benchmark] CSV written to: {path}");
        }

        private static void AppendRows(StringBuilder sb, string planner, List<TrialRecord> records)
        {
            if (records == null) return;
            foreach (TrialRecord r in records)
            {
                sb.AppendLine(string.Join(",", new string[]
                {
                    planner,
                    r.index.ToString(CultureInfo.InvariantCulture),
                    r.success ? "1" : "0",
                    r.milliseconds.ToString("F3", CultureInfo.InvariantCulture),
                    r.expandedNodes.ToString(CultureInfo.InvariantCulture),
                    r.waypoints.ToString(CultureInfo.InvariantCulture),
                    r.pathLengthMeters.ToString("F2", CultureInfo.InvariantCulture),
                    r.totalHeadingChangeDeg.ToString("F2", CultureInfo.InvariantCulture),
                    "\"" + (r.failureReason ?? string.Empty).Replace("\"", "'") + "\""
                }));
            }
        }
    }
}
