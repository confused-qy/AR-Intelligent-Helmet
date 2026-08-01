using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace MotorcycleNavigation
{
    public sealed class VRFullMapGoalSelector : MonoBehaviour
    {
        [Header("References")]
        public MotorcycleNavigationManager navigationManager;
        public Camera gazeCamera;
        public Transform playerView;
        public Transform currentPositionSource;
        public RawImage mapImage;
        public Texture2D displayMapTexture;
        public Camera displayMapCamera;
        public int displayCaptureWidth = 1024;
        public int displayCaptureHeight = 1024;
        public bool captureDisplayMapOnStart = true;

        [Header("Placement")]
        public bool showOnStart = true;
        public Vector3 localPosition = new Vector3(0f, -0.05f, 1.2f);
        public Vector2 sizeMeters = new Vector2(0.8f, 0.8f);
        public bool flipVerticalDisplay = false;

        [Header("Goal Selection")]
        public LayerMask mapPanelLayer = 0;
        public bool requireFreeCostmapCell = true;
        public bool snapBlockedClicksToNearestRoad = true;
        public bool requireSemanticRoadWhenAvailable = true;
        public bool mouseUsesPointerPosition = true;
        public bool syncCurrentPoseBeforePlanning = true;
        public bool snapStartPoseToNearestRoad = false;
        public bool moveCurrentPositionSourceWhenSnapped = false;
        public bool requireGoalFootprintFree = true;
        public bool chooseFreeGoalYaw = true;
        public bool logSelectionDebug = true;
        public float goalYawDeg;
        public bool requireGoalYaw;

        [Header("Markers")]
        public bool showCurrentPositionMarker = true;
        public Vector2 currentMarkerSizeMeters = new Vector2(0.045f, 0.045f);
        public Color currentMarkerColor = new Color(1f, 0.08f, 0.02f, 1f);
        public Color currentMarkerOutlineColor = Color.white;
        public bool showGoalMarker = true;
        public Vector2 goalMarkerSizeMeters = new Vector2(0.04f, 0.04f);
        public Color goalMarkerColor = new Color(1f, 0.9f, 0f, 1f);
        public Color goalMarkerOutlineColor = Color.black;
        public bool showPlannedPath = true;
        public float pathLineWidthMeters = 0.012f;
        public Color pathLineColor = new Color(0f, 0.35f, 1f, 0.95f);

#if ENABLE_INPUT_SYSTEM
        [Header("Input System")]
        public InputActionReference confirmAction;
        public InputActionReference toggleAction;
#endif

        private Canvas canvas;
        private RectTransform mapRect;
        private RectTransform currentMarkerRect;
        private RectTransform currentMarkerOutlineRect;
        private RectTransform goalMarkerRect;
        private RectTransform goalMarkerOutlineRect;
        private RectTransform pathLayerRect;
        private Image[] pathSegmentImages = new Image[0];
        private NavigationResult renderedPlan;
        private Texture2D generatedMapTexture;
        private bool visible;
        private float lastGoalSelectionTime = -999f;
        private NavigationResult loggedFailedPlan;
        private NavigationResult loggedSuccessfulPlan;

        private void Awake()
        {
            if (navigationManager == null)
                navigationManager = FindObjectOfType<MotorcycleNavigationManager>();
            if (gazeCamera == null)
                gazeCamera = Camera.main;
            if (playerView == null && gazeCamera != null)
                playerView = gazeCamera.transform;
            if (currentPositionSource == null)
                currentPositionSource = playerView;

            EnsureUi();
            if (captureDisplayMapOnStart && displayMapTexture == null && displayMapCamera != null)
                displayMapTexture = CaptureCamera(displayMapCamera, displayCaptureWidth, displayCaptureHeight);
            SetVisible(showOnStart);
        }

        private void OnEnable()
        {
#if ENABLE_INPUT_SYSTEM
            if (confirmAction != null && confirmAction.action != null)
                confirmAction.action.Enable();
            if (toggleAction != null && toggleAction.action != null)
                toggleAction.action.Enable();
#endif
        }

        private void OnDisable()
        {
#if ENABLE_INPUT_SYSTEM
            if (confirmAction != null && confirmAction.action != null)
                confirmAction.action.Disable();
            if (toggleAction != null && toggleAction.action != null)
                toggleAction.action.Disable();
#endif
        }

        private void LateUpdate()
        {
            AssignTexture();

            if (playerView != null && canvas != null)
            {
                canvas.transform.SetParent(playerView, false);
                canvas.transform.localPosition = localPosition;
                canvas.transform.localRotation = Quaternion.identity;
            }

            UpdateCurrentPositionMarker();
            UpdatePlannedPathOverlay();
            ReportPlanningFailure();

#if ENABLE_INPUT_SYSTEM
            if (toggleAction != null && toggleAction.action != null && toggleAction.action.WasPressedThisFrame())
                SetVisible(!visible);

            if (confirmAction != null && confirmAction.action != null && confirmAction.action.WasPressedThisFrame())
                TrySelectGoalFromMap();

            if (Keyboard.current != null && Keyboard.current.mKey.wasPressedThisFrame)
                SetVisible(!visible);

            if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
                TrySelectGoalFromMap();

            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            {
                if (mouseUsesPointerPosition)
                    TrySelectGoalFromMap(gazeCamera.ScreenPointToRay(Mouse.current.position.ReadValue()));
                else
                    TrySelectGoalFromMap();
            }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.M))
                SetVisible(!visible);
            if (Input.GetKeyDown(KeyCode.Space) || Input.GetMouseButtonDown(0))
                TrySelectGoalFromMap();
#endif
        }

        private void OnDestroy()
        {
            if (generatedMapTexture != null)
                Destroy(generatedMapTexture);
            if (displayMapTexture != null && displayMapCamera != null)
                Destroy(displayMapTexture);
        }

        public void SetVisible(bool isVisible)
        {
            visible = isVisible;
            if (canvas != null)
                canvas.enabled = visible;
        }

        public bool TrySelectGoalFromMap()
        {
            if (gazeCamera == null)
            {
                LogSelection("No gaze camera.");
                return false;
            }

            return TrySelectGoalFromMap(new Ray(gazeCamera.transform.position, gazeCamera.transform.forward));
        }

        public bool TrySelectGoalFromMap(Ray selectionRay)
        {
            if (!visible)
            {
                LogSelection("Map is hidden.");
                return false;
            }

            if (navigationManager == null)
            {
                LogSelection("Navigation manager is not assigned.");
                return false;
            }

            if (navigationManager.InflatedCostmap == null)
            {
                LogSelection("Inflated costmap is null. Check Build On Start and TopDownCamera.");
                return false;
            }

            if (gazeCamera == null || mapRect == null)
            {
                LogSelection("Gaze camera or map rect is missing.");
                return false;
            }

            Vector2 normalized;
            if (!TryGetPointOnMap(selectionRay, out normalized))
            {
                LogSelection("Selection ray did not hit the map panel.");
                return false;
            }

            GridCostmap map = navigationManager.InflatedCostmap;
            float poseY = currentPositionSource != null ? currentPositionSource.position.y : navigationManager.transform.position.y;
            Vector3 clickedWorld;
            int cellX;
            int cellY;
            if (!TryMapPointToCostmapCell(normalized, map, poseY, out cellX, out cellY, out clickedWorld))
            {
                LogSelection("Selected point is outside the navigation costmap.");
                return false;
            }

            int clickedCellX = cellX;
            int clickedCellY = cellY;
            float resolvedYaw = goalYawDeg;

            if (requireFreeCostmapCell && !IsSelectableGoalCell(map, cellX, cellY, poseY, ref resolvedYaw))
            {
                if (!snapBlockedClicksToNearestRoad || !TryFindNearestRoadCell(map, cellX, cellY, poseY, out cellX, out cellY, out resolvedYaw))
                {
                    LogSelection("Clicked cell cannot be used as a goal and no nearby road cell with a free footprint was found.");
                    return false;
                }
            }
            else if (!requireFreeCostmapCell && snapBlockedClicksToNearestRoad && !IsSelectableGoalCell(map, cellX, cellY, poseY, ref resolvedYaw))
            {
                TryFindNearestRoadCell(map, cellX, cellY, poseY, out cellX, out cellY, out resolvedYaw);
            }

            if (!IsSelectableGoalCell(map, cellX, cellY, poseY, ref resolvedYaw))
            {
                LogSelection("Resolved goal cell is still blocked.");
                return false;
            }

            if (syncCurrentPoseBeforePlanning && currentPositionSource != null)
            {
                Vector3 startPosition = currentPositionSource.position;
                Quaternion startRotation = currentPositionSource.rotation;
                float startYaw = startRotation.eulerAngles.y;
                if (snapStartPoseToNearestRoad && !navigationManager.IsPoseNavigable(startPosition, startYaw))
                {
                    Vector3 snappedStart;
                    float snappedYaw;
                    if (TryFindNearestNavigablePose(map, startPosition, startYaw, out snappedStart, out snappedYaw))
                    {
                        startPosition = snappedStart;
                        startRotation = Quaternion.Euler(0f, snappedYaw, 0f);
                        LogSelection("Start pose was in collision; planning from nearest navigable road point " + snappedStart);
                    }
                    else
                    {
                        LogSelection("Start pose is in collision and no nearby navigable start pose was found.");
                    }
                }

                navigationManager.UpdatePosition(startPosition);
                navigationManager.UpdateRotationQuaternion(startRotation);
            }

            Vector3 goal = map.CellToWorld(cellX, cellY, poseY);
            UpdateGoalMarker(goal);
            navigationManager.SetGoalWorld(goal, resolvedYaw, requireGoalYaw);
            lastGoalSelectionTime = Time.time;
            loggedFailedPlan = null;
            loggedSuccessfulPlan = null;
            LogSelection(
                string.Format(
                    "Goal selected. clicked=({0},{1}) clickedWorld={2} resolved=({3},{4}) world={5} yaw={6} hasPose={7}",
                    clickedCellX,
                    clickedCellY,
                    clickedWorld,
                    cellX,
                    cellY,
                    goal,
                    resolvedYaw,
                    navigationManager.HasPose));
            LogNavigationDebug(map, goal, resolvedYaw);
            return true;
        }

        private bool TryMapPointToCostmapCell(Vector2 normalized, GridCostmap map, float worldY, out int cellX, out int cellY, out Vector3 world)
        {
            if (UseDisplayCameraProjection())
            {
                if (!TryDisplayCameraViewportToWorld(normalized, worldY, out world))
                {
                    cellX = 0;
                    cellY = 0;
                    return false;
                }

                return map.WorldToCell(world, out cellX, out cellY);
            }

            cellX = Mathf.Clamp(Mathf.FloorToInt(normalized.x * map.Width), 0, map.Width - 1);
            cellY = Mathf.Clamp(Mathf.FloorToInt(normalized.y * map.Height), 0, map.Height - 1);
            world = map.CellToWorld(cellX, cellY, worldY);
            return true;
        }

        private bool TryDisplayCameraViewportToWorld(Vector2 viewport, float worldY, out Vector3 world)
        {
            world = Vector3.zero;
            if (displayMapCamera == null)
                return false;

            Ray ray = displayMapCamera.ViewportPointToRay(new Vector3(viewport.x, viewport.y, 0f));
            Plane groundPlane = new Plane(Vector3.up, new Vector3(0f, worldY, 0f));
            if (!groundPlane.Raycast(ray, out float enter))
                return false;

            world = ray.GetPoint(enter);
            return true;
        }

        private void LogNavigationDebug(GridCostmap map, Vector3 goal, float goalYaw)
        {
            if (!logSelectionDebug || navigationManager == null || map == null)
                return;

            TrafficSemanticRuleProvider semanticRules = navigationManager.ruleProvider as TrafficSemanticRuleProvider;
            if (semanticRules == null)
                return;

            if (currentPositionSource != null)
            {
                float startYaw = currentPositionSource.rotation.eulerAngles.y;
                LogSelection("Start debug: " + semanticRules.DescribeWorldPosition(map, currentPositionSource.position, startYaw));
            }

            LogSelection("Goal debug: " + semanticRules.DescribeWorldPosition(map, goal, goalYaw));
        }

        private void EnsureUi()
        {
            if (mapImage != null)
            {
                canvas = mapImage.GetComponentInParent<Canvas>();
                mapRect = mapImage.rectTransform;
                AssignTexture();
                EnsurePathLayer();
                EnsureCurrentPositionMarker();
                EnsureGoalMarker();
                return;
            }

            GameObject canvasObject = new GameObject("VR Full Map Goal Canvas");
            canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 120;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 1000f;

            GameObject imageObject = new GameObject("Full Map Goal Image");
            imageObject.transform.SetParent(canvasObject.transform, false);
            if (mapPanelLayer.value != 0)
                imageObject.layer = FirstLayerInMask(mapPanelLayer);

            mapImage = imageObject.AddComponent<RawImage>();
            mapImage.color = Color.white;
            AssignTexture();

            mapRect = mapImage.rectTransform;
            mapRect.anchorMin = new Vector2(0.5f, 0.5f);
            mapRect.anchorMax = new Vector2(0.5f, 0.5f);
            mapRect.pivot = new Vector2(0.5f, 0.5f);
            mapRect.anchoredPosition = Vector2.zero;
            mapRect.sizeDelta = sizeMeters;

            RectTransform canvasRect = canvas.GetComponent<RectTransform>();
            canvasRect.sizeDelta = sizeMeters;

            EnsurePathLayer();
            EnsureCurrentPositionMarker();
            EnsureGoalMarker();
        }

        private void EnsurePathLayer()
        {
            if (mapRect == null || pathLayerRect != null)
                return;

            GameObject pathLayerObject = new GameObject("Planned Path Overlay");
            pathLayerObject.transform.SetParent(mapRect, false);
            pathLayerRect = pathLayerObject.AddComponent<RectTransform>();
            pathLayerRect.anchorMin = Vector2.zero;
            pathLayerRect.anchorMax = Vector2.one;
            pathLayerRect.offsetMin = Vector2.zero;
            pathLayerRect.offsetMax = Vector2.zero;
            pathLayerRect.SetAsLastSibling();
        }

        private void EnsureCurrentPositionMarker()
        {
            if (mapRect == null)
                return;

            if (currentMarkerOutlineRect == null)
            {
                GameObject outlineObject = new GameObject("Current Position Marker Outline");
                outlineObject.transform.SetParent(mapRect, false);
                Image outline = outlineObject.AddComponent<Image>();
                outline.color = currentMarkerOutlineColor;
                currentMarkerOutlineRect = outline.rectTransform;
                currentMarkerOutlineRect.anchorMin = new Vector2(0.5f, 0.5f);
                currentMarkerOutlineRect.anchorMax = new Vector2(0.5f, 0.5f);
                currentMarkerOutlineRect.pivot = new Vector2(0.5f, 0.5f);
            }

            if (currentMarkerRect == null)
            {
                GameObject markerObject = new GameObject("Current Position Marker");
                markerObject.transform.SetParent(mapRect, false);
                Image marker = markerObject.AddComponent<Image>();
                marker.color = currentMarkerColor;
                currentMarkerRect = marker.rectTransform;
                currentMarkerRect.anchorMin = new Vector2(0.5f, 0.5f);
                currentMarkerRect.anchorMax = new Vector2(0.5f, 0.5f);
                currentMarkerRect.pivot = new Vector2(0.5f, 0.5f);
            }

            currentMarkerOutlineRect.SetAsLastSibling();
            currentMarkerRect.SetAsLastSibling();
        }

        private void EnsureGoalMarker()
        {
            if (mapRect == null)
                return;

            if (goalMarkerOutlineRect == null)
            {
                GameObject outlineObject = new GameObject("Goal Marker Outline");
                outlineObject.transform.SetParent(mapRect, false);
                Image outline = outlineObject.AddComponent<Image>();
                outline.color = goalMarkerOutlineColor;
                goalMarkerOutlineRect = outline.rectTransform;
                goalMarkerOutlineRect.anchorMin = new Vector2(0.5f, 0.5f);
                goalMarkerOutlineRect.anchorMax = new Vector2(0.5f, 0.5f);
                goalMarkerOutlineRect.pivot = new Vector2(0.5f, 0.5f);
                goalMarkerOutlineRect.gameObject.SetActive(false);
            }

            if (goalMarkerRect == null)
            {
                GameObject markerObject = new GameObject("Goal Marker");
                markerObject.transform.SetParent(mapRect, false);
                Image marker = markerObject.AddComponent<Image>();
                marker.color = goalMarkerColor;
                goalMarkerRect = marker.rectTransform;
                goalMarkerRect.anchorMin = new Vector2(0.5f, 0.5f);
                goalMarkerRect.anchorMax = new Vector2(0.5f, 0.5f);
                goalMarkerRect.pivot = new Vector2(0.5f, 0.5f);
                goalMarkerRect.gameObject.SetActive(false);
            }
        }

        private void UpdateGoalMarker(Vector3 goalWorld)
        {
            if (!showGoalMarker || mapRect == null || navigationManager == null || navigationManager.InflatedCostmap == null)
                return;

            EnsureGoalMarker();
            GridCostmap map = navigationManager.InflatedCostmap;
            Rect rect = mapRect.rect;
            Vector2 anchored;
            if (!WorldToMapAnchored(goalWorld, map, rect, out anchored))
            {
                goalMarkerOutlineRect.gameObject.SetActive(false);
                goalMarkerRect.gameObject.SetActive(false);
                return;
            }

            goalMarkerOutlineRect.anchoredPosition = anchored;
            goalMarkerOutlineRect.sizeDelta = goalMarkerSizeMeters * 1.55f;
            goalMarkerRect.anchoredPosition = anchored;
            goalMarkerRect.sizeDelta = goalMarkerSizeMeters;
            goalMarkerOutlineRect.gameObject.SetActive(true);
            goalMarkerRect.gameObject.SetActive(true);
            goalMarkerOutlineRect.SetAsLastSibling();
            goalMarkerRect.SetAsLastSibling();
        }

        private void UpdateCurrentPositionMarker()
        {
            if (!showCurrentPositionMarker || currentPositionSource == null || mapRect == null ||
                navigationManager == null || navigationManager.InflatedCostmap == null)
            {
                SetMarkerVisible(false);
                return;
            }

            GridCostmap map = navigationManager.InflatedCostmap;
            Vector3 world = currentPositionSource.position;
            Rect rect = mapRect.rect;
            Vector2 anchored;
            if (!WorldToMapAnchored(world, map, rect, out anchored))
            {
                SetMarkerVisible(false);
                return;
            }

            EnsureCurrentPositionMarker();
            currentMarkerOutlineRect.anchoredPosition = anchored;
            currentMarkerOutlineRect.sizeDelta = currentMarkerSizeMeters * 1.55f;
            currentMarkerRect.anchoredPosition = anchored;
            currentMarkerRect.sizeDelta = currentMarkerSizeMeters;
            SetMarkerVisible(true);
        }

        private void SetMarkerVisible(bool isVisible)
        {
            if (currentMarkerOutlineRect != null)
                currentMarkerOutlineRect.gameObject.SetActive(isVisible);
            if (currentMarkerRect != null)
                currentMarkerRect.gameObject.SetActive(isVisible);
        }

        private void UpdatePlannedPathOverlay()
        {
            if (!showPlannedPath || mapRect == null || navigationManager == null ||
                navigationManager.InflatedCostmap == null || navigationManager.CurrentPlan == null ||
                !navigationManager.CurrentPlan.success || navigationManager.CurrentPlan.path.Count < 2)
            {
                HidePathSegments();
                return;
            }

            NavigationResult plan = navigationManager.CurrentPlan;
            if (!ReferenceEquals(renderedPlan, plan) || !AnyPathSegmentVisible())
                RenderPath(plan);
        }

        private void RenderPath(NavigationResult plan)
        {
            EnsurePathLayer();
            int segmentCount = plan.path.Count - 1;
            EnsurePathSegmentCapacity(segmentCount);

            Rect rect = mapRect.rect;
            GridCostmap map = navigationManager.InflatedCostmap;
            for (int i = 0; i < segmentCount; i++)
            {
                Vector2 a;
                Vector2 b;
                if (!WorldToMapAnchored(plan.path[i].position, map, rect, out a) ||
                    !WorldToMapAnchored(plan.path[i + 1].position, map, rect, out b))
                {
                    pathSegmentImages[i].gameObject.SetActive(false);
                    continue;
                }

                ConfigurePathSegment(pathSegmentImages[i].rectTransform, a, b);
                pathSegmentImages[i].color = pathLineColor;
                pathSegmentImages[i].gameObject.SetActive(true);
            }

            for (int i = segmentCount; i < pathSegmentImages.Length; i++)
                pathSegmentImages[i].gameObject.SetActive(false);

            renderedPlan = plan;
        }

        private void EnsurePathSegmentCapacity(int count)
        {
            if (pathSegmentImages.Length >= count)
                return;

            Image[] next = new Image[count];
            for (int i = 0; i < pathSegmentImages.Length; i++)
                next[i] = pathSegmentImages[i];

            for (int i = pathSegmentImages.Length; i < count; i++)
            {
                GameObject segmentObject = new GameObject("Path Segment");
                segmentObject.transform.SetParent(pathLayerRect, false);
                Image image = segmentObject.AddComponent<Image>();
                image.color = pathLineColor;
                image.raycastTarget = false;
                RectTransform rect = image.rectTransform;
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0f, 0.5f);
                next[i] = image;
            }

            pathSegmentImages = next;
        }

        private void ConfigurePathSegment(RectTransform segment, Vector2 start, Vector2 end)
        {
            Vector2 delta = end - start;
            float length = delta.magnitude;
            segment.anchoredPosition = start;
            segment.sizeDelta = new Vector2(length, pathLineWidthMeters);
            segment.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        }

        private bool WorldToMapAnchored(Vector3 world, GridCostmap map, Rect rect, out Vector2 anchored)
        {
            if (UseDisplayCameraProjection())
                return WorldToDisplayCameraAnchored(world, rect, out anchored);

            float u = (world.x - map.OriginXZ.x) / (map.Width * map.Resolution);
            float v = (world.z - map.OriginXZ.y) / (map.Height * map.ResolutionZ);
            if (u < 0f || u > 1f || v < 0f || v > 1f)
            {
                anchored = Vector2.zero;
                return false;
            }

            anchored = new Vector2(
                Mathf.Lerp(rect.xMin, rect.xMax, u),
                Mathf.Lerp(rect.yMin, rect.yMax, MapToDisplayV(v)));
            return true;
        }

        private bool WorldToDisplayCameraAnchored(Vector3 world, Rect rect, out Vector2 anchored)
        {
            anchored = Vector2.zero;
            if (displayMapCamera == null)
                return false;

            Vector3 viewport = displayMapCamera.WorldToViewportPoint(world);
            if (viewport.z < 0f || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
                return false;

            anchored = new Vector2(
                Mathf.Lerp(rect.xMin, rect.xMax, viewport.x),
                Mathf.Lerp(rect.yMin, rect.yMax, MapToDisplayV(viewport.y)));
            return true;
        }

        private bool AnyPathSegmentVisible()
        {
            for (int i = 0; i < pathSegmentImages.Length; i++)
            {
                if (pathSegmentImages[i] != null && pathSegmentImages[i].gameObject.activeSelf)
                    return true;
            }
            return false;
        }

        private void HidePathSegments()
        {
            for (int i = 0; i < pathSegmentImages.Length; i++)
            {
                if (pathSegmentImages[i] != null)
                    pathSegmentImages[i].gameObject.SetActive(false);
            }
            renderedPlan = null;
        }

        private void ReportPlanningFailure()
        {
            if (!logSelectionDebug || navigationManager == null)
                return;

            if (Time.time - lastGoalSelectionTime < 0.15f)
                return;

            NavigationResult plan = navigationManager.CurrentPlan;
            if (plan == null)
                return;

            if (plan.success)
            {
                if (ReferenceEquals(plan, loggedSuccessfulPlan))
                    return;

                loggedSuccessfulPlan = plan;
                LogSelection(BuildPlanSummary(plan));
                return;
            }

            if (!ReferenceEquals(plan, loggedFailedPlan))
            {
                loggedFailedPlan = plan;
                LogSelection("Planning failed: " + plan.failureReason + " Phase=" + navigationManager.Phase);
            }
        }

        private string BuildPlanSummary(NavigationResult plan)
        {
            float length = 0f;
            int turns = 0;
            for (int i = 1; i < plan.path.Count; i++)
            {
                length += Vector3.Distance(plan.path[i - 1].position, plan.path[i].position);
                if (i < plan.path.Count - 1)
                {
                    Vector3 a = plan.path[i].position - plan.path[i - 1].position;
                    Vector3 b = plan.path[i + 1].position - plan.path[i].position;
                    a.y = 0f;
                    b.y = 0f;
                    if (a.sqrMagnitude > 0.001f && b.sqrMagnitude > 0.001f &&
                        Mathf.Abs(Vector3.SignedAngle(a.normalized, b.normalized, Vector3.up)) > 35f)
                    {
                        turns++;
                    }
                }
            }

            return string.Format(
                "Planning succeeded: points={0} length={1:F1}m turns={2} expanded={3} Phase={4}",
                plan.path.Count,
                length,
                turns,
                plan.expandedNodes,
                navigationManager != null ? navigationManager.Phase.ToString() : "unknown");
        }

        private void AssignTexture()
        {
            if (mapImage == null || navigationManager == null)
                return;

            if (displayMapTexture != null)
            {
                mapImage.texture = displayMapTexture;
                ApplyMapUvRect();
                return;
            }

            if (navigationManager.sourceMapTexture != null)
            {
                mapImage.texture = navigationManager.sourceMapTexture;
                ApplyMapUvRect();
                return;
            }

            GridCostmap map = navigationManager.InflatedCostmap;
            if (map == null)
                return;

            if (generatedMapTexture != null &&
                generatedMapTexture.width == map.Width &&
                generatedMapTexture.height == map.Height)
            {
                if (mapImage.texture == null)
                    mapImage.texture = generatedMapTexture;
                ApplyMapUvRect();
                return;
            }

            if (generatedMapTexture != null)
                Destroy(generatedMapTexture);

            generatedMapTexture = BuildFullCostmapTexture(map);
            mapImage.texture = generatedMapTexture;
            ApplyMapUvRect();
        }

        private bool UseDisplayCameraProjection()
        {
            return displayMapCamera != null && displayMapTexture != null;
        }

        private void ApplyMapUvRect()
        {
            if (mapImage == null)
                return;

            mapImage.uvRect = flipVerticalDisplay
                ? new Rect(0f, 1f, 1f, -1f)
                : new Rect(0f, 0f, 1f, 1f);
        }

        private static Texture2D BuildFullCostmapTexture(GridCostmap map)
        {
            Texture2D texture = new Texture2D(map.Width, map.Height, TextureFormat.RGB24, false);
            Color32[] colors = new Color32[map.Width * map.Height];

            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    byte cost = map.GetCost(x, y);
                    colors[x + y * map.Width] = CostToColor(cost);
                }
            }

            texture.SetPixels32(colors);
            texture.Apply(false);
            return texture;
        }

        private static Color32 CostToColor(byte cost)
        {
            if (cost == GridCostmap.NoInformation)
                return new Color32(70, 70, 70, 255);
            if (cost >= GridCostmap.InscribedInflatedObstacle)
                return new Color32(20, 20, 20, 255);
            if (cost > GridCostmap.FreeSpace)
                return new Color32(160, 160, 120, 255);
            return new Color32(235, 235, 235, 255);
        }

        private bool IsSelectableGoalCell(GridCostmap map, int x, int y, float worldY, ref float resolvedYaw)
        {
            if (!map.InBounds(x, y))
                return false;

            byte cost = map.GetCost(x, y);
            if (cost == GridCostmap.NoInformation || cost >= GridCostmap.InscribedInflatedObstacle)
                return false;

            if (requireSemanticRoadWhenAvailable && navigationManager != null)
            {
                TrafficSemanticRuleProvider semanticRules = navigationManager.ruleProvider as TrafficSemanticRuleProvider;
                if (semanticRules != null && !semanticRules.IsOnSemanticRoad(map.CellToWorld(x, y, worldY)))
                    return false;
            }

            if (!requireGoalFootprintFree || navigationManager == null)
                return true;

            Vector3 position = map.CellToWorld(x, y, worldY);
            if (navigationManager.IsPoseNavigable(position, resolvedYaw))
                return true;

            if (!chooseFreeGoalYaw || requireGoalYaw)
                return false;

            for (int i = 0; i < 8; i++)
            {
                float candidateYaw = i * 45f;
                if (navigationManager.IsPoseNavigable(position, candidateYaw))
                {
                    resolvedYaw = candidateYaw;
                    return true;
                }
            }

            return false;
        }

        private bool TryFindNearestRoadCell(
            GridCostmap map,
            int startX,
            int startY,
            float worldY,
            out int roadX,
            out int roadY,
            out float roadYaw)
        {
            roadX = startX;
            roadY = startY;
            roadYaw = goalYawDeg;

            if (IsSelectableGoalCell(map, startX, startY, worldY, ref roadYaw))
                return true;

            int maxRadius = Mathf.Max(map.Width, map.Height);
            for (int radius = 1; radius <= maxRadius; radius++)
            {
                bool found = false;
                int bestX = startX;
                int bestY = startY;
                float bestYaw = goalYawDeg;
                int bestDistance = int.MaxValue;

                int minX = Mathf.Max(0, startX - radius);
                int maxX = Mathf.Min(map.Width - 1, startX + radius);
                int minY = Mathf.Max(0, startY - radius);
                int maxY = Mathf.Min(map.Height - 1, startY + radius);

                for (int x = minX; x <= maxX; x++)
                {
                    CheckCandidate(map, startX, startY, x, minY, worldY, ref found, ref bestX, ref bestY, ref bestYaw, ref bestDistance);
                    CheckCandidate(map, startX, startY, x, maxY, worldY, ref found, ref bestX, ref bestY, ref bestYaw, ref bestDistance);
                }

                for (int y = minY + 1; y <= maxY - 1; y++)
                {
                    CheckCandidate(map, startX, startY, minX, y, worldY, ref found, ref bestX, ref bestY, ref bestYaw, ref bestDistance);
                    CheckCandidate(map, startX, startY, maxX, y, worldY, ref found, ref bestX, ref bestY, ref bestYaw, ref bestDistance);
                }

                if (found)
                {
                    roadX = bestX;
                    roadY = bestY;
                    roadYaw = bestYaw;
                    return true;
                }
            }

            return false;
        }

        private bool TryFindNearestNavigablePose(
            GridCostmap map,
            Vector3 sourcePosition,
            float sourceYaw,
            out Vector3 snappedPosition,
            out float snappedYaw)
        {
            snappedPosition = sourcePosition;
            snappedYaw = sourceYaw;

            int startX;
            int startY;
            if (!map.WorldToCell(sourcePosition, out startX, out startY))
            {
                startX = Mathf.Clamp(Mathf.FloorToInt((sourcePosition.x - map.OriginXZ.x) / map.Resolution), 0, map.Width - 1);
                startY = Mathf.Clamp(Mathf.FloorToInt((sourcePosition.z - map.OriginXZ.y) / map.ResolutionZ), 0, map.Height - 1);
            }

            int maxRadius = Mathf.Max(map.Width, map.Height);
            for (int radius = 0; radius <= maxRadius; radius++)
            {
                int minX = Mathf.Max(0, startX - radius);
                int maxX = Mathf.Min(map.Width - 1, startX + radius);
                int minY = Mathf.Max(0, startY - radius);
                int maxY = Mathf.Min(map.Height - 1, startY + radius);

                for (int x = minX; x <= maxX; x++)
                {
                    if (TryCandidateStartPose(map, x, minY, sourcePosition.y, sourceYaw, out snappedPosition, out snappedYaw))
                        return true;
                    if (TryCandidateStartPose(map, x, maxY, sourcePosition.y, sourceYaw, out snappedPosition, out snappedYaw))
                        return true;
                }

                for (int y = minY + 1; y <= maxY - 1; y++)
                {
                    if (TryCandidateStartPose(map, minX, y, sourcePosition.y, sourceYaw, out snappedPosition, out snappedYaw))
                        return true;
                    if (TryCandidateStartPose(map, maxX, y, sourcePosition.y, sourceYaw, out snappedPosition, out snappedYaw))
                        return true;
                }
            }

            return false;
        }

        private bool TryCandidateStartPose(
            GridCostmap map,
            int x,
            int y,
            float worldY,
            float preferredYaw,
            out Vector3 snappedPosition,
            out float snappedYaw)
        {
            snappedPosition = map.CellToWorld(x, y, worldY);
            snappedYaw = preferredYaw;

            float candidateYaw = preferredYaw;
            if (!IsSelectableGoalCell(map, x, y, worldY, ref candidateYaw))
                return false;

            snappedYaw = candidateYaw;
            snappedPosition = map.CellToWorld(x, y, worldY);
            return true;
        }

        private void CheckCandidate(
            GridCostmap map,
            int startX,
            int startY,
            int x,
            int y,
            float worldY,
            ref bool found,
            ref int bestX,
            ref int bestY,
            ref float bestYaw,
            ref int bestDistance)
        {
            float candidateYaw = goalYawDeg;
            if (!IsSelectableGoalCell(map, x, y, worldY, ref candidateYaw))
                return;

            int dx = x - startX;
            int dy = y - startY;
            int distance = dx * dx + dy * dy;
            if (found && distance >= bestDistance)
                return;

            found = true;
            bestX = x;
            bestY = y;
            bestYaw = candidateYaw;
            bestDistance = distance;
        }

        private static Texture2D CaptureCamera(Camera camera, int width, int height)
        {
            int safeWidth = Mathf.Clamp(width, 64, 4096);
            int safeHeight = Mathf.Clamp(height, 64, 4096);
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture renderTexture = new RenderTexture(safeWidth, safeHeight, 24, RenderTextureFormat.ARGB32);
            Texture2D texture = new Texture2D(safeWidth, safeHeight, TextureFormat.RGB24, false);

            camera.targetTexture = renderTexture;
            RenderTexture.active = renderTexture;
            camera.Render();
            texture.ReadPixels(new Rect(0, 0, safeWidth, safeHeight), 0, 0);
            texture.Apply(false);

            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            Destroy(renderTexture);
            return texture;
        }

        private bool TryGetPointOnMap(Ray ray, out Vector2 normalized)
        {
            normalized = Vector2.zero;

            Plane plane = new Plane(mapRect.forward, mapRect.position);
            float enter;
            if (!plane.Raycast(ray, out enter))
                return false;

            Vector3 worldHit = ray.GetPoint(enter);
            Vector3 localHit = mapRect.InverseTransformPoint(worldHit);
            Rect rect = mapRect.rect;

            float u = Mathf.InverseLerp(rect.xMin, rect.xMax, localHit.x);
            float displayV = Mathf.InverseLerp(rect.yMin, rect.yMax, localHit.y);
            if (u < 0f || u > 1f || displayV < 0f || displayV > 1f)
                return false;

            normalized = new Vector2(u, DisplayToMapV(displayV));
            return true;
        }

        private float MapToDisplayV(float mapV)
        {
            return flipVerticalDisplay ? 1f - mapV : mapV;
        }

        private float DisplayToMapV(float displayV)
        {
            return flipVerticalDisplay ? 1f - displayV : displayV;
        }

        private void LogSelection(string message)
        {
            if (logSelectionDebug)
                Debug.Log("[VRFullMapGoalSelector] " + message, this);
        }

        private static int FirstLayerInMask(LayerMask mask)
        {
            int value = mask.value;
            for (int i = 0; i < 32; i++)
            {
                if ((value & (1 << i)) != 0)
                    return i;
            }
            return 0;
        }
    }
}
