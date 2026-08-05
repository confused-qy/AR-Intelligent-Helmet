using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace MotorcycleNavigation
{
    public sealed class MotorcycleNavigationManager : MonoBehaviour
    {
        [Header("Map Source")]
        public Texture2D sourceMapTexture;
        public Camera topDownCamera;
        public int cameraCaptureWidth = 1024;
        public int cameraCaptureHeight = 1024;
        public bool buildOnStart = true;
        [Tooltip("When TopDownCamera only renders road layers, convert every visible non-black pixel to white before building the costmap.")]
        public bool binarizeCameraCaptureByVisibility = true;
        [Range(0f, 1f)]
        public float visiblePixelLumaThreshold = 0.02f;
        [Tooltip("Use TrafficSemanticRuleProvider lanes/connectors as the drivable mask. This is useful when the source image contains dark lane markings or annotations that would otherwise split roads.")]
        public bool useSemanticRoadsAsCostmap = false;

        [Header("Navigation Settings")]
        public CostmapBuildSettings costmapBuild = new CostmapBuildSettings();
        public InflationSettings inflation = new InflationSettings();
        public MotorcycleFootprintSettings footprint = new MotorcycleFootprintSettings();
        public PlannerSettings plannerSettings = new PlannerSettings();
        public NavigationRuntimeSettings runtime = new NavigationRuntimeSettings();

        [Header("Goals and Rules")]
        public List<NamedGoal> goals = new List<NamedGoal>();
        public NavigationRuleProviderBase ruleProvider;

        [Header("Android/Scene Output")]
        public string mobileObjectName = "mobile";
        public string navTypeMethodName = "sendNavType";
        public string imageObjectName = "Camera";
        public string imageMethodName = "AndroidSendPic";
        public bool emitSceneSendMessage = true;
        public bool emitSubmapEveryTick = true;
        public bool renderSubmapOnSourceMap = true;

        [Header("Rotation Parsing")]
        public EulerYawAxis eulerYawAxis = EulerYawAxis.Y;
        public bool invertEulerYaw = false;

        public UnityEvent<string> OnNavTypeString = new UnityEvent<string>();
        public UnityEvent<string> OnSubmapBase64 = new UnityEvent<string>();

        public GridCostmap RawCostmap { get; private set; }
        public GridCostmap InflatedCostmap { get; private set; }
        public NavigationResult CurrentPlan { get; private set; }
        public NamedGoal CurrentGoal { get; private set; }
        public bool HasPose { get; private set; }
        public NavigationPhase Phase { get; private set; } = NavigationPhase.Idle;
        public float LastPlanningDurationSeconds { get; private set; }
        public bool LastPlanningExceededDeadline { get; private set; }
        public NavTurnType CurrentTurnType { get; private set; } = NavTurnType.Straight;

        private readonly PoseAStarPlanner planner = new PoseAStarPlanner();
        private readonly GridAStarFallbackPlanner gridFallbackPlanner = new GridAStarFallbackPlanner();
        private FootprintCollisionChecker collision;
        private NavPose currentPose;
        private Vector3 lastPosition;
        private bool hasOrientation;
        private float nextTickTime;
        private float lastPlanTime = -999f;
        private int closestPathIndex;
        private float lastRemainingDistance = float.PositiveInfinity;
        private float lastProgressTime;
        private NavTurnType lastTurnType = NavTurnType.Straight;
        private float nextNavigationOutputTime;
        private bool pendingPlanningOutput;

        private void Start()
        {
            if (buildOnStart)
            {
                if (sourceMapTexture != null)
                    BuildNavigationMapFromTexture(sourceMapTexture);
                else if (topDownCamera != null)
                    BuildNavigationMapFromCamera();
            }
        }

        private void Update()
        {
            if (Time.time < nextTickTime)
                return;

            nextTickTime = Time.time + Mathf.Max(0.02f, runtime.tickPeriodSeconds);
            NavigationTick();
        }

        public void BuildNavigationMapFromCamera()
        {
            if (topDownCamera == null)
                throw new InvalidOperationException("topDownCamera is null.");

            Texture2D captured = CaptureCamera(topDownCamera, cameraCaptureWidth, cameraCaptureHeight);
            if (binarizeCameraCaptureByVisibility)
                BinarizeVisiblePixels(captured, visiblePixelLumaThreshold);
            BuildNavigationMapFromTexture(captured);
            Destroy(captured);
        }

        public void BuildNavigationMapFromTexture(Texture2D texture)
        {
            ConfigureSemanticProvider(texture);
            RawCostmap = GridCostmap.FromTexture(texture, costmapBuild);
            if (useSemanticRoadsAsCostmap)
                ApplySemanticRoadMask(RawCostmap);
            InflatedCostmap = RawCostmap.Clone();
            InflationLayer2D.Apply(InflatedCostmap, inflation);
            collision = new FootprintCollisionChecker(InflatedCostmap, footprint);
            CurrentPlan = null;
            Phase = NavigationPhase.Idle;
            closestPathIndex = 0;
        }

        private void ApplySemanticRoadMask(GridCostmap map)
        {
            TrafficSemanticRuleProvider semanticProvider = ruleProvider as TrafficSemanticRuleProvider;
            if (map == null || semanticProvider == null)
                return;

            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    Vector3 world = map.CellToWorld(x, y);
                    map.SetCost(x, y, semanticProvider.IsOnSemanticRoad(world)
                        ? GridCostmap.FreeSpace
                        : GridCostmap.LethalObstacle);
                }
            }
        }

        private void ConfigureSemanticProvider(Texture2D texture)
        {
            TrafficSemanticRuleProvider semanticProvider = ruleProvider as TrafficSemanticRuleProvider;
            if (semanticProvider == null || texture == null)
                return;

            semanticProvider.sourceImageWidth = texture.width;
            semanticProvider.sourceImageHeight = texture.height;
            semanticProvider.worldOriginXZ = costmapBuild.worldOriginXZ;
            semanticProvider.metersPerPixel = costmapBuild.metersPerPixel;
            semanticProvider.metersPerPixelZ = costmapBuild.metersPerPixelZ > 0f
                ? costmapBuild.metersPerPixelZ
                : costmapBuild.metersPerPixel;

            if (semanticProvider.semanticJson != null)
                semanticProvider.LoadFromJson(semanticProvider.semanticJson.text);

            List<NamedGoal> configuredGoals = semanticProvider.GetConfiguredGoals();
            if (configuredGoals != null && configuredGoals.Count > 0)
            {
                goals.Clear();
                goals.AddRange(configuredGoals);
            }
        }

        public bool SetGoalByName(string goalName)
        {
            for (int i = 0; i < goals.Count; i++)
            {
                if (goals[i] != null && string.Equals(goals[i].name, goalName, StringComparison.OrdinalIgnoreCase))
                {
                    CurrentGoal = goals[i];
                    RequestReplan(true);
                    return true;
                }
            }
            return false;
        }

        public void SetGoalWorld(Vector3 position, float yawDeg = 0f, bool requireYaw = false)
        {
            CurrentGoal = new NamedGoal
            {
                name = "runtime_goal",
                worldPosition = position,
                yawDeg = yawDeg,
                requireYaw = requireYaw
            };
            RequestReplan(true);
        }

        public void SetGoalMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
                return;

            float[] values;
            if (TryParseFloats(message, out values) && values.Length >= 2)
            {
                float yaw = values.Length >= 3 ? values[2] : 0f;
                bool requireYaw = values.Length >= 4 && Mathf.Abs(values[3]) > 0.5f;
                SetGoalWorld(new Vector3(values[0], currentPose.position.y, values[1]), yaw, requireYaw);
                return;
            }

            SetGoalByName(message.Trim());
        }

        public void androidMoveSend(string message)
        {
            float[] values;
            if (!TryParseFloats(message, out values) || values.Length < 3)
                return;

            UpdatePosition(new Vector3(values[0], values[1], values[2]));
        }

        public void androidSend(string message)
        {
            float[] values;
            if (!TryParseFloats(message, out values))
                return;

            if (values.Length >= 4)
            {
                UpdateRotationQuaternion(new Quaternion(values[0], values[1], values[2], values[3]));
            }
            else if (values.Length >= 3)
            {
                UpdateRotationEuler(new Vector3(values[0], values[1], values[2]));
            }
        }

        public void UpdatePosition(Vector3 position)
        {
            if (HasPose && !hasOrientation)
            {
                Vector3 delta = position - lastPosition;
                delta.y = 0f;
                if (delta.sqrMagnitude > 0.0004f)
                    currentPose.yawDeg = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
            }

            currentPose.position = position;
            lastPosition = position;
            HasPose = true;
        }

        public void UpdateRotationEuler(Vector3 euler)
        {
            float yaw = eulerYawAxis == EulerYawAxis.X ? euler.x : eulerYawAxis == EulerYawAxis.Y ? euler.y : euler.z;
            currentPose.yawDeg = invertEulerYaw ? -yaw : yaw;
            hasOrientation = true;
        }

        public void UpdateRotationQuaternion(Quaternion quaternion)
        {
            currentPose.yawDeg = quaternion.eulerAngles.y;
            hasOrientation = true;
        }

        public void ForceReplan()
        {
            RequestReplan(true);
        }

        public bool IsPoseNavigable(Vector3 position, float yawDeg)
        {
            if (collision == null)
                return false;

            return collision.IsPoseFree(new NavPose(position, yawDeg));
        }

        public void RequestReplan(bool emitPlanningOutput)
        {
            lastPlanTime = -999f;
            CurrentPlan = null;
            lastRemainingDistance = float.PositiveInfinity;
            lastProgressTime = Time.time;
            pendingPlanningOutput = emitPlanningOutput;
            Phase = NavigationPhase.Planning;
        }

        private void NavigationTick()
        {
            if (InflatedCostmap == null || collision == null || !HasPose || CurrentGoal == null)
                return;

            if (Phase == NavigationPhase.Planning)
            {
                PlanningTick();
                return;
            }

            if (CurrentPlan == null || !CurrentPlan.success || CurrentPlan.path.Count == 0)
            {
                RequestReplan(true);
                return;
            }

            closestPathIndex = FindClosestPathIndex(CurrentPlan.path, currentPose.position);
            if (ShouldReplan())
            {
                RequestReplan(true);
                return;
            }

            if (Vector3.Distance(currentPose.position, CurrentGoal.worldPosition) <= plannerSettings.goalToleranceMeters)
            {
                Phase = NavigationPhase.Arrived;
                EmitNavType(NavTurnType.Straight);
                return;
            }

            if (Time.time < nextNavigationOutputTime)
                return;

            nextNavigationOutputTime = Time.time + Mathf.Max(0.05f, runtime.navigationOutputPeriodSeconds);
            NavTurnType turnType = ComputeTurnType(CurrentPlan.path, closestPathIndex);
            EmitNavType(turnType);

            if (emitSubmapEveryTick || turnType != lastTurnType)
                EmitSubmap();

            lastTurnType = turnType;
        }

        private void PlanningTick()
        {
            if (pendingPlanningOutput)
            {
                // Keep the last visual maneuver while a short replan is running.
                // The straight value is still sent to external listeners as the
                // existing loading signal, but it must not erase CurrentTurnType.
                EmitNavType(NavTurnType.Straight, false);
                if (runtime.emitLoadingMapDuringPlanning)
                    EmitLoadingMap();
                pendingPlanningOutput = false;
            }

            PlanNow();

            if (CurrentPlan != null && CurrentPlan.success && CurrentPlan.path.Count > 0)
            {
                Phase = NavigationPhase.Navigating;
                nextNavigationOutputTime = 0f;
            }
            else
            {
                Phase = NavigationPhase.Failed;
                EmitNavType(NavTurnType.Straight);
            }
        }

        private bool ShouldReplan()
        {
            if (CurrentPlan == null || !CurrentPlan.success || CurrentPlan.path.Count == 0)
                return Time.time - lastPlanTime >= runtime.minReplanIntervalSeconds;

            closestPathIndex = FindClosestPathIndex(CurrentPlan.path, currentPose.position);
            Vector3 closestPathPosition = CurrentPlan.path[closestPathIndex].position;
            float deltaX = currentPose.position.x - closestPathPosition.x;
            float deltaZ = currentPose.position.z - closestPathPosition.z;
            float distToPath = Mathf.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
            float timeSinceLastPlan = Time.time - lastPlanTime;

            if (runtime.severeReplanDistanceFromPathMeters > 0f &&
                distToPath > runtime.severeReplanDistanceFromPathMeters)
            {
                return true;
            }

            if (runtime.replanDistanceFromPathMeters > 0f &&
                distToPath > runtime.replanDistanceFromPathMeters &&
                timeSinceLastPlan >= runtime.minReplanIntervalSeconds)
            {
                return true;
            }

            if (runtime.replanWhenPathAheadBlocked && PathAheadBlocked(closestPathIndex) &&
                timeSinceLastPlan >= runtime.minReplanIntervalSeconds)
            {
                return true;
            }

            float remaining = RemainingDistance(CurrentPlan.path, closestPathIndex, currentPose.position);
            if (lastRemainingDistance - remaining > runtime.progressEpsilonMeters)
            {
                lastRemainingDistance = remaining;
                lastProgressTime = Time.time;
                return false;
            }

            if (runtime.replanWhenStuck &&
                Time.time - lastProgressTime > runtime.stuckTimeoutSeconds &&
                timeSinceLastPlan >= runtime.minReplanIntervalSeconds)
            {
                lastProgressTime = Time.time;
                return true;
            }

            return false;
        }

        private void PlanNow()
        {
            float startTime = Time.realtimeSinceStartup;
            if (!hasOrientation)
            {
                Vector3 toGoal = CurrentGoal.worldPosition - currentPose.position;
                toGoal.y = 0f;
                if (toGoal.sqrMagnitude > 0.0001f)
                    currentPose.yawDeg = Mathf.Atan2(toGoal.x, toGoal.z) * Mathf.Rad2Deg;
            }

            if (plannerSettings != null && plannerSettings.useGridPlannerOnly)
            {
                CurrentPlan = gridFallbackPlanner.Plan(
                    InflatedCostmap,
                    currentPose.position,
                    CurrentGoal.worldPosition,
                    plannerSettings,
                    ruleProvider);

                if (CurrentPlan != null && CurrentPlan.success)
                    CurrentPlan.failureReason = "Used grid planner only.";
            }
            else
            {
                CurrentPlan = planner.Plan(
                    InflatedCostmap,
                    currentPose,
                    CurrentGoal,
                    plannerSettings,
                    collision,
                    ruleProvider);
            }

            if ((CurrentPlan == null || !CurrentPlan.success) &&
                plannerSettings != null &&
                plannerSettings.fallbackToGridPlanner)
            {
                string poseFailure = CurrentPlan != null ? CurrentPlan.failureReason : "Pose planner returned null.";
                NavigationResult fallback = gridFallbackPlanner.Plan(
                    InflatedCostmap,
                    currentPose.position,
                    CurrentGoal.worldPosition,
                    plannerSettings,
                    ruleProvider);
                if (fallback.success)
                {
                    fallback.failureReason = "Pose planner failed: " + poseFailure + ". Used grid fallback.";
                    CurrentPlan = fallback;
                }
            }

            LastPlanningDurationSeconds = Time.realtimeSinceStartup - startTime;
            LastPlanningExceededDeadline = LastPlanningDurationSeconds > runtime.planningDeadlineSeconds;
            lastPlanTime = Time.time;
            closestPathIndex = 0;
            lastProgressTime = Time.time;
            lastRemainingDistance = CurrentPlan != null && CurrentPlan.success
                ? RemainingDistance(CurrentPlan.path, 0, currentPose.position)
                : float.PositiveInfinity;
        }

        private bool PathAheadBlocked(int startIndex)
        {
            if (CurrentPlan == null || !CurrentPlan.success)
                return true;

            int end = Mathf.Min(CurrentPlan.path.Count - 1, startIndex + 12);
            for (int i = startIndex; i <= end; i++)
            {
                if (!collision.IsPoseFree(CurrentPlan.path[i]))
                    return true;
            }
            return false;
        }

        private int FindClosestPathIndex(IList<NavPose> path, Vector3 position)
        {
            int best = 0;
            float bestDist = float.PositiveInfinity;
            for (int i = 0; i < path.Count; i++)
            {
                float dist = (path[i].position - position).sqrMagnitude;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = i;
                }
            }
            return best;
        }

        private float RemainingDistance(IList<NavPose> path, int startIndex, Vector3 currentPosition)
        {
            if (path == null || path.Count == 0)
                return float.PositiveInfinity;

            float total = Vector3.Distance(currentPosition, path[Mathf.Clamp(startIndex, 0, path.Count - 1)].position);
            for (int i = Mathf.Clamp(startIndex, 0, path.Count - 1); i < path.Count - 1; i++)
                total += Vector3.Distance(path[i].position, path[i + 1].position);
            return total;
        }

        private NavTurnType ComputeTurnType(IList<NavPose> path, int startIndex)
        {
            if (path == null || path.Count < 3)
                return NavTurnType.Straight;

            float lookahead = Mathf.Max(0.5f, runtime.arrowLookaheadMeters);
            int segmentIndex;
            Vector3 projection;
            FindClosestPathProjection(
                path,
                currentPose.position,
                out segmentIndex,
                out projection);

            float distanceToCorner = Vector3.Distance(
                projection,
                path[segmentIndex + 1].position);
            float threshold = Mathf.Clamp(
                runtime.arrowTurnThresholdDegrees,
                5f,
                60f);

            // Each interior path vertex is a possible maneuver. Select the
            // first meaningful vertex inside the announcement distance.
            for (int cornerIndex = segmentIndex + 1;
                 cornerIndex < path.Count - 1;
                 cornerIndex++)
            {
                if (distanceToCorner > lookahead)
                    break;

                Vector3 incoming =
                    path[cornerIndex].position -
                    path[cornerIndex - 1].position;
                Vector3 outgoing =
                    path[cornerIndex + 1].position -
                    path[cornerIndex].position;
                incoming.y = 0f;
                outgoing.y = 0f;

                if (incoming.sqrMagnitude > 0.001f &&
                    outgoing.sqrMagnitude > 0.001f)
                {
                    float signedAngle = Vector3.SignedAngle(
                        incoming,
                        outgoing,
                        Vector3.up);
                    if (Mathf.Abs(signedAngle) >= threshold)
                    {
                        if (Mathf.Abs(signedAngle) > 135f)
                            return NavTurnType.UTurn;
                        return signedAngle < 0f
                            ? NavTurnType.Left
                            : NavTurnType.Right;
                    }
                }

                distanceToCorner += Vector3.Distance(
                    path[cornerIndex].position,
                    path[cornerIndex + 1].position);
            }

            return NavTurnType.Straight;
        }

        private static void FindClosestPathProjection(
            IList<NavPose> path,
            Vector3 position,
            out int segmentIndex,
            out Vector3 projection)
        {
            segmentIndex = 0;
            projection = path[0].position;
            float bestDistanceSquared = float.PositiveInfinity;

            for (int i = 0; i < path.Count - 1; i++)
            {
                Vector3 start = path[i].position;
                Vector3 end = path[i + 1].position;
                Vector3 segment = end - start;
                float lengthSquared = segment.sqrMagnitude;
                float t = lengthSquared > 0.0001f
                    ? Mathf.Clamp01(Vector3.Dot(position - start, segment) / lengthSquared)
                    : 0f;
                Vector3 candidate = start + segment * t;
                float distanceSquared = (position - candidate).sqrMagnitude;
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    segmentIndex = i;
                    projection = candidate;
                }
            }
        }

        private void EmitNavType(
            NavTurnType turnType,
            bool updateCurrentTurnType = true)
        {
            if (updateCurrentTurnType)
                CurrentTurnType = turnType;
            string typeString = ((int)turnType).ToString();
            OnNavTypeString.Invoke(typeString);

            if (!emitSceneSendMessage || string.IsNullOrEmpty(mobileObjectName) || string.IsNullOrEmpty(navTypeMethodName))
                return;

            GameObject target = GameObject.Find(mobileObjectName);
            if (target != null)
                target.SendMessage(navTypeMethodName, typeString, SendMessageOptions.DontRequireReceiver);
        }

        private void EmitSubmap()
        {
            Texture2D submap = renderSubmapOnSourceMap && sourceMapTexture != null
                ? SubmapRenderer.RenderOnSourceTexture(
                    sourceMapTexture,
                    InflatedCostmap,
                    CurrentPlan.path,
                    currentPose.position,
                    closestPathIndex,
                    runtime.submapWindowMeters,
                    runtime.submapPixels,
                    costmapBuild.flipVertical)
                : SubmapRenderer.Render(
                    InflatedCostmap,
                    CurrentPlan.path,
                    currentPose.position,
                    closestPathIndex,
                    runtime.submapWindowMeters,
                    runtime.submapPixels);

            string base64 = SubmapRenderer.EncodeJpegDataUri(submap, runtime.jpegQuality);
            OnSubmapBase64.Invoke(base64);

            if (emitSceneSendMessage && !string.IsNullOrEmpty(imageObjectName) && !string.IsNullOrEmpty(imageMethodName))
            {
                GameObject target = GameObject.Find(imageObjectName);
                if (target != null)
                    target.SendMessage(imageMethodName, base64, SendMessageOptions.DontRequireReceiver);
            }

            Destroy(submap);
        }

        private void EmitLoadingMap()
        {
            Texture2D texture = CreateLoadingTexture(runtime.loadingMapPixels);
            string base64 = SubmapRenderer.EncodeJpegDataUri(texture, runtime.jpegQuality);
            OnSubmapBase64.Invoke(base64);

            if (emitSceneSendMessage && !string.IsNullOrEmpty(imageObjectName) && !string.IsNullOrEmpty(imageMethodName))
            {
                GameObject target = GameObject.Find(imageObjectName);
                if (target != null)
                    target.SendMessage(imageMethodName, base64, SendMessageOptions.DontRequireReceiver);
            }

            Destroy(texture);
        }

        private static Texture2D CreateLoadingTexture(int size)
        {
            int pixels = Mathf.Clamp(size, 64, 1024);
            Texture2D texture = new Texture2D(pixels, pixels, TextureFormat.RGB24, false);
            Color32[] colors = new Color32[pixels * pixels];
            Color32 bg = new Color32(245, 245, 245, 255);
            Color32 fg = new Color32(50, 100, 220, 255);
            Color32 muted = new Color32(190, 200, 225, 255);
            int cx = pixels / 2;
            int cy = pixels / 2;
            int outer = pixels / 7;
            int inner = Mathf.Max(outer - 8, outer / 2);

            for (int i = 0; i < colors.Length; i++)
                colors[i] = bg;

            for (int y = 0; y < pixels; y++)
            {
                for (int x = 0; x < pixels; x++)
                {
                    int dx = x - cx;
                    int dy = y - cy;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d >= inner && d <= outer)
                    {
                        float a = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                        if (a < 0f)
                            a += 360f;
                        colors[x + y * pixels] = a < 270f ? fg : muted;
                    }
                }
            }

            texture.SetPixels32(colors);
            texture.Apply(false);
            return texture;
        }

        private static Texture2D CaptureCamera(Camera camera, int width, int height)
        {
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGB24, false);

            camera.targetTexture = rt;
            RenderTexture.active = rt;
            camera.Render();
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture.Apply(false);

            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            UnityEngine.Object.Destroy(rt);
            return texture;
        }

        private static void BinarizeVisiblePixels(Texture2D texture, float lumaThreshold)
        {
            Color32[] pixels = texture.GetPixels32();
            byte threshold = (byte)Mathf.Clamp(Mathf.RoundToInt(lumaThreshold * 255f), 0, 255);
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 c = pixels[i];
                byte max = (byte)Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                pixels[i] = max > threshold
                    ? new Color32(255, 255, 255, 255)
                    : new Color32(0, 0, 0, 255);
            }

            texture.SetPixels32(pixels);
            texture.Apply(false);
        }

        private static bool TryParseFloats(string message, out float[] values)
        {
            values = null;
            if (string.IsNullOrEmpty(message))
                return false;

            string[] parts = message.Split(',');
            List<float> parsed = new List<float>();
            for (int i = 0; i < parts.Length; i++)
            {
                float value;
                if (float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
                {
                    parsed.Add(value);
                }
            }

            values = parsed.ToArray();
            return values.Length > 0;
        }
    }
}
