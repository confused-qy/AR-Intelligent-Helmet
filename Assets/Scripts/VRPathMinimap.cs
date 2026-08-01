using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace MotorcycleNavigation
{
    public sealed class VRPathMinimap : MonoBehaviour
    {
        [Header("References")]
        public MotorcycleNavigationManager navigationManager;
        public Transform playerView;
        public Transform centerSource;
        public RawImage minimapImage;
        public Texture2D displayMapTexture;

        [Header("Placement")]
        public Vector3 localPosition = new Vector3(0.42f, -0.25f, 0.75f);
        public Vector2 sizeMeters = new Vector2(0.28f, 0.28f);

        [Header("Rendering")]
        public int pixels = 256;
        public float windowMeters = 18f;
        public float refreshIntervalSeconds = 0.2f;
        public bool useSourceMapTexture = true;
        public bool flipVerticalDisplay = true;
        public bool rotateWithHeading = true;
        public bool hideUntilPathExists = true;
        public float pathLineWidthMeters = 0.012f;
        public Color pathLineColor = new Color(0f, 0.7f, 1f, 0.95f);
        public Color pathOutlineColor = new Color(0.05f, 0.05f, 0.05f, 1f);
        public float currentMarkerSizeMeters = 0.018f;
        public Color currentMarkerColor = new Color(0f, 0.82f, 0.35f, 1f);

        private Canvas canvas;
        private float nextRefreshTime;
        private RectTransform pathLayerRect;
        private Image[] pathSegmentImages = new Image[0];
        private RectTransform currentMarkerRect;
        private Texture2D renderedMinimapTexture;
        private NavigationResult trackedPlan;
        private int forwardSegmentIndex;
        private readonly List<NavPose> forwardPath = new List<NavPose>();

        private void Awake()
        {
            if (navigationManager == null)
                navigationManager = FindObjectOfType<MotorcycleNavigationManager>();
            if (playerView == null && Camera.main != null)
                playerView = Camera.main.transform;
            if (centerSource == null)
                centerSource = playerView;

            EnsureUi();
        }

        private void LateUpdate()
        {
            if (playerView != null && canvas != null)
            {
                canvas.transform.SetParent(playerView, false);
                canvas.transform.localPosition = localPosition;
                canvas.transform.localRotation = Quaternion.identity;
            }

            if (Time.time < nextRefreshTime)
                return;

            nextRefreshTime = Time.time + Mathf.Max(0.02f, refreshIntervalSeconds);
            Refresh();
        }

        private void OnDestroy()
        {
            if (renderedMinimapTexture != null)
                Destroy(renderedMinimapTexture);
        }

        private void EnsureUi()
        {
            if (minimapImage != null)
            {
                canvas = minimapImage.GetComponentInParent<Canvas>();
                return;
            }

            GameObject canvasObject = new GameObject("VR Path Minimap Canvas");
            canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 100;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 1000f;

            canvasObject.AddComponent<GraphicRaycaster>();

            GameObject imageObject = new GameObject("Path Minimap");
            imageObject.transform.SetParent(canvasObject.transform, false);
            minimapImage = imageObject.AddComponent<RawImage>();
            minimapImage.color = Color.white;

            RectTransform rect = minimapImage.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = sizeMeters;

            RectTransform canvasRect = canvas.GetComponent<RectTransform>();
            canvasRect.sizeDelta = sizeMeters;

            EnsureOverlayUi();
        }

        private void EnsureOverlayUi()
        {
            if (minimapImage == null)
                return;

            if (pathLayerRect == null)
            {
                GameObject pathLayerObject = new GameObject("Path Overlay");
                pathLayerObject.transform.SetParent(minimapImage.rectTransform, false);
                pathLayerRect = pathLayerObject.AddComponent<RectTransform>();
                pathLayerRect.anchorMin = Vector2.zero;
                pathLayerRect.anchorMax = Vector2.one;
                pathLayerRect.offsetMin = Vector2.zero;
                pathLayerRect.offsetMax = Vector2.zero;
                pathLayerRect.pivot = new Vector2(0.5f, 0.5f);
            }

            if (currentMarkerRect == null)
            {
                GameObject markerObject = new GameObject("Current Marker");
                markerObject.transform.SetParent(minimapImage.rectTransform, false);
                Image marker = markerObject.AddComponent<Image>();
                marker.color = currentMarkerColor;
                currentMarkerRect = marker.rectTransform;
                currentMarkerRect.anchorMin = new Vector2(0.5f, 0.5f);
                currentMarkerRect.anchorMax = new Vector2(0.5f, 0.5f);
                currentMarkerRect.pivot = new Vector2(0.5f, 0.5f);
            }

            currentMarkerRect.sizeDelta = new Vector2(currentMarkerSizeMeters, currentMarkerSizeMeters);
        }

        private void Refresh()
        {
            bool hasPath = navigationManager != null &&
                navigationManager.InflatedCostmap != null &&
                navigationManager.CurrentPlan != null &&
                navigationManager.CurrentPlan.success &&
                navigationManager.CurrentPlan.path.Count > 0;

            if (minimapImage != null && hideUntilPathExists)
                minimapImage.enabled = hasPath;

            if (!hasPath || minimapImage == null || playerView == null || centerSource == null)
                return;

            NavigationResult plan = navigationManager.CurrentPlan;
            IList<NavPose> path = plan.path;
            Vector3 center = centerSource.position;
            if (!ReferenceEquals(trackedPlan, plan))
            {
                trackedPlan = plan;
                forwardSegmentIndex = FindClosestPathSegment(path, center);
            }

            UpdateForwardSegment(path, center);
            BuildForwardPath(path, center);
            Texture2D mapTexture = displayMapTexture != null
                ? displayMapTexture
                : navigationManager.sourceMapTexture;
            GridCostmap map = navigationManager.InflatedCostmap;
            float headingYaw = rotateWithHeading ? centerSource.eulerAngles.y : 0f;

            EnsureOverlayUi();
            HidePathSegments();
            if (currentMarkerRect != null)
                currentMarkerRect.gameObject.SetActive(false);

            Texture2D nextTexture = useSourceMapTexture && mapTexture != null
                ? SubmapRenderer.RenderOnSourceTexture(
                    mapTexture,
                    map,
                    forwardPath,
                    center,
                    0,
                    windowMeters,
                    Mathf.Clamp(pixels, 64, 1024),
                    flipVerticalDisplay,
                    headingYaw)
                : SubmapRenderer.Render(
                    map,
                    forwardPath,
                    center,
                    0,
                    windowMeters,
                    Mathf.Clamp(pixels, 64, 1024),
                    headingYaw);

            Texture2D previousTexture = renderedMinimapTexture;
            renderedMinimapTexture = nextTexture;
            minimapImage.texture = renderedMinimapTexture;
            minimapImage.uvRect = new Rect(0f, 0f, 1f, 1f);
            if (previousTexture != null)
                Destroy(previousTexture);
        }

        private void UpdateForwardSegment(IList<NavPose> path, Vector3 position)
        {
            if (path == null || path.Count < 2)
            {
                forwardSegmentIndex = 0;
                return;
            }

            forwardSegmentIndex = Mathf.Clamp(forwardSegmentIndex, 0, path.Count - 2);
            while (forwardSegmentIndex < path.Count - 2)
            {
                Vector3 start = path[forwardSegmentIndex].position;
                Vector3 end = path[forwardSegmentIndex + 1].position;
                start.y = position.y;
                end.y = position.y;
                Vector3 segment = end - start;
                float lengthSquared = segment.sqrMagnitude;
                float progress = lengthSquared > 0.0001f
                    ? Vector3.Dot(position - start, segment) / lengthSquared
                    : 1f;

                if (progress < 0.98f && Vector3.Distance(position, end) > 0.5f)
                    break;

                forwardSegmentIndex++;
            }
        }

        private static int FindClosestPathSegment(IList<NavPose> path, Vector3 position)
        {
            if (path == null || path.Count < 2)
                return 0;

            int bestSegment = 0;
            float bestDistanceSquared = float.PositiveInfinity;
            Vector2 point = new Vector2(position.x, position.z);
            for (int i = 0; i < path.Count - 1; i++)
            {
                Vector2 start = new Vector2(path[i].position.x, path[i].position.z);
                Vector2 end = new Vector2(path[i + 1].position.x, path[i + 1].position.z);
                Vector2 segment = end - start;
                float progress = segment.sqrMagnitude > 0.0001f
                    ? Mathf.Clamp01(Vector2.Dot(point - start, segment) / segment.sqrMagnitude)
                    : 0f;
                float distanceSquared = (point - (start + progress * segment)).sqrMagnitude;
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    bestSegment = i;
                }
            }

            return bestSegment;
        }

        private void BuildForwardPath(IList<NavPose> path, Vector3 position)
        {
            forwardPath.Clear();
            if (path == null || path.Count == 0)
                return;
            if (path.Count == 1)
            {
                forwardPath.Add(path[0]);
                return;
            }

            int segmentIndex = Mathf.Clamp(forwardSegmentIndex, 0, path.Count - 2);
            Vector3 start = path[segmentIndex].position;
            Vector3 end = path[segmentIndex + 1].position;
            Vector3 segment = end - start;
            segment.y = 0f;
            Vector3 toPlayer = position - start;
            toPlayer.y = 0f;
            float progress = segment.sqrMagnitude > 0.0001f
                ? Mathf.Clamp01(Vector3.Dot(toPlayer, segment) / segment.sqrMagnitude)
                : 1f;
            Vector3 projected = Vector3.Lerp(start, end, progress);
            float yaw = Mathf.Atan2(segment.x, segment.z) * Mathf.Rad2Deg;
            forwardPath.Add(new NavPose(projected, yaw));

            for (int i = segmentIndex + 1; i < path.Count; i++)
                forwardPath.Add(path[i]);
        }

        private Rect BuildMapUvRect(GridCostmap map, Vector3 center)
        {
            float mapWidthMeters = map.Width * map.Resolution;
            float mapHeightMeters = map.Height * map.ResolutionZ;
            float halfWindow = windowMeters * 0.5f;

            float uMin = (center.x - halfWindow - map.OriginXZ.x) / mapWidthMeters;
            float uMax = (center.x + halfWindow - map.OriginXZ.x) / mapWidthMeters;
            float vMin = (center.z - halfWindow - map.OriginXZ.y) / mapHeightMeters;
            float vMax = (center.z + halfWindow - map.OriginXZ.y) / mapHeightMeters;

            float width = Mathf.Clamp01(uMax) - Mathf.Clamp01(uMin);
            float height = Mathf.Clamp01(vMax) - Mathf.Clamp01(vMin);
            float x = Mathf.Clamp01(uMin);
            float y = Mathf.Clamp01(vMin);

            if (flipVerticalDisplay)
                return new Rect(x, y + height, width, -height);

            return new Rect(x, y, width, height);
        }

        private void UpdatePathOverlay(IList<NavPose> path, GridCostmap map)
        {
            if (pathLayerRect == null || path == null || path.Count < 2)
            {
                HidePathSegments();
                return;
            }

            int segmentCount = path.Count - 1;
            EnsurePathSegmentCapacity(segmentCount);

            Rect rect = minimapImage.rectTransform.rect;
            Rect uvRect = minimapImage.uvRect;
            for (int i = 0; i < segmentCount; i++)
            {
                Vector2 a;
                Vector2 b;
                if (!WorldToAnchored(path[i].position, map, uvRect, rect, out a) ||
                    !WorldToAnchored(path[i + 1].position, map, uvRect, rect, out b))
                {
                    pathSegmentImages[i].gameObject.SetActive(false);
                    continue;
                }

                ConfigurePathSegment(pathSegmentImages[i].rectTransform, a, b);
                pathSegmentImages[i].gameObject.SetActive(true);
            }
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
                Outline outline = segmentObject.AddComponent<Outline>();
                outline.effectColor = pathOutlineColor;
                outline.effectDistance = new Vector2(1f, 1f);
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

        private void HidePathSegments()
        {
            for (int i = 0; i < pathSegmentImages.Length; i++)
            {
                if (pathSegmentImages[i] != null)
                    pathSegmentImages[i].gameObject.SetActive(false);
            }
        }

        private void ConfigurePathSegment(RectTransform segment, Vector2 start, Vector2 end)
        {
            Vector2 delta = end - start;
            float length = delta.magnitude;
            segment.anchoredPosition = start;
            segment.sizeDelta = new Vector2(length, pathLineWidthMeters);
            segment.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        }

        private bool WorldToAnchored(Vector3 world, GridCostmap map, Rect uvRect, Rect rect, out Vector2 anchored)
        {
            float u = (world.x - map.OriginXZ.x) / (map.Width * map.Resolution);
            float v = (world.z - map.OriginXZ.y) / (map.Height * map.ResolutionZ);
            if (u < 0f || u > 1f || v < 0f || v > 1f)
            {
                anchored = Vector2.zero;
                return false;
            }

            float cropXMin = Mathf.Min(uvRect.xMin, uvRect.xMax);
            float cropXMax = Mathf.Max(uvRect.xMin, uvRect.xMax);
            float cropYMin = Mathf.Min(uvRect.yMin, uvRect.yMax);
            float cropYMax = Mathf.Max(uvRect.yMin, uvRect.yMax);
            if (u < cropXMin || u > cropXMax || v < cropYMin || v > cropYMax)
            {
                anchored = Vector2.zero;
                return false;
            }

            float localU = Mathf.InverseLerp(cropXMin, cropXMax, u);
            float localV = Mathf.InverseLerp(cropYMin, cropYMax, v);
            if (uvRect.width < 0f)
                localU = 1f - localU;
            if (uvRect.height < 0f)
                localV = 1f - localV;

            anchored = new Vector2(
                Mathf.Lerp(rect.xMin, rect.xMax, localU),
                Mathf.Lerp(rect.yMin, rect.yMax, localV));
            return true;
        }

        private void UpdateCurrentMarker(Vector3 center)
        {
            if (currentMarkerRect == null || minimapImage == null)
                return;

            currentMarkerRect.anchoredPosition = Vector2.zero;
            currentMarkerRect.SetAsLastSibling();
        }
    }
}
