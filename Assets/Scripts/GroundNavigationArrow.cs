using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MotorcycleNavigation
{
    public enum NavigationArrowPreview
    {
        Navigation,
        Straight,
        Left,
        Right
    }

    /// <summary>
    /// Displays one of three navigation textures on a mesh projected onto the
    /// ground in front of the viewer. The mesh and material are created at
    /// runtime, so no arrow prefab or child GameObjects are required.
    /// </summary>
    public sealed class GroundNavigationArrow : MonoBehaviour
    {
        [Header("References")]
        public MotorcycleNavigationManager navigationManager;
        [Tooltip("Usually the XR/Main Camera. Its horizontal forward direction controls arrow placement.")]
        public Transform viewTransform;
        [Tooltip("The moving XR/player root. Used only to measure progress on the existing plan; it does not update or replan navigation.")]
        public Transform navigationPositionSource;

        [Header("Fixed-route Turn Detection")]
        [Tooltip("Calculate turns from the player's progress on CurrentPlan without updating MotorcycleNavigationManager pose.")]
        public bool usePlayerProgressForTurns = true;
        [Min(0.5f)]
        public float turnAnnouncementDistanceMeters = 12f;
        [Range(5f, 60f)]
        public float turnAngleThresholdDegrees = 15f;

        [Header("Arrow Textures")]
        public Texture2D straightTexture;
        public Texture2D leftTexture;
        public Texture2D rightTexture;

        [Header("View-locked Placement")]
        [Tooltip("Show the arrow below the center of view. This mode does not need ground Colliders or raycasts.")]
        public bool lockToView = true;
        [Tooltip("Camera-local position: X is right, Y is up, Z is forward.")]
        public Vector3 viewLocalOffset = new Vector3(0f, -0.35f, 1.2f);
        [Min(0.05f)]
        public float viewArrowWidthMeters = 0.32f;
        [Min(0.05f)]
        public float viewArrowLengthMeters = 0.46f;

        [Header("Placement")]
        [Min(0.1f)]
        public float distanceAheadMeters = 3f;
        [Min(0.05f)]
        public float arrowWidthMeters = 1.2f;
        [Min(0.05f)]
        public float arrowLengthMeters = 1.8f;
        [Min(0f)]
        public float groundOffsetMeters = 0.025f;
        [Min(0.1f)]
        public float raycastHeightMeters = 2f;
        [Min(0.1f)]
        public float raycastDistanceMeters = 6f;
        [Tooltip("Set this to the Road/Ground layers so the ray cannot hit vehicles or props.")]
        public LayerMask groundLayers = ~0;
        [Tooltip("If Ground Layers misses, try all physics layers. Useful while setting up Road layers.")]
        public bool fallbackToAllLayers = true;
        [Tooltip("When the road has no Collider, place the arrow on a flat world-space ground plane.")]
        public bool useFallbackGroundPlane = true;
        [Tooltip("World-space Y coordinate used by the fallback ground plane.")]
        public float fallbackGroundHeight = 0f;

        [Header("Visibility")]
        public bool showOnlyWhileNavigating = true;
        public bool hideWhenGroundIsNotFound = true;
        [Header("Turn Stability")]
        [Tooltip("Once a turn appears, keep it visible for at least this long.")]
        [Min(0f)]
        public float minimumTurnDisplaySeconds = 3f;
        [Tooltip("Require a continuous straight signal for this long before returning to the straight arrow.")]
        [Min(0f)]
        public float straightConfirmationSeconds = 0.5f;
        [Tooltip("Write a Console message whenever the displayed direction changes.")]
        public bool logTurnChanges = true;
        [Tooltip("Use Left or Right to verify textures without driving. Return this to Navigation afterward.")]
        public NavigationArrowPreview preview = NavigationArrowPreview.Navigation;

        private GameObject runtimeArrowObject;
        private Mesh runtimeMesh;
        private Material runtimeMaterial;
        private MeshRenderer arrowRenderer;
        private MaterialPropertyBlock propertyBlock;
        private NavTurnType currentTurn = NavTurnType.Straight;
        private float turnDisplayStartedTime = -999f;
        private float lastTurnSignalTime = -999f;

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int ZTestId = Shader.PropertyToID("_ZTest");

        private void Awake()
        {
            if (navigationManager == null)
                navigationManager = FindObjectOfType<MotorcycleNavigationManager>();

            if (viewTransform == null && Camera.main != null)
                viewTransform = Camera.main.transform;

            if (navigationPositionSource == null)
            {
                PlannedPathAutoMover mover =
                    FindObjectOfType<PlannedPathAutoMover>();
                if (mover != null)
                    navigationPositionSource = mover.playerRoot;
            }

            CreateRuntimeArrow();
            ApplyCurrentTexture();
        }

        private void OnEnable()
        {
            if (navigationManager != null)
                navigationManager.OnNavTypeString.AddListener(HandleNavigationType);
        }

        private void OnDisable()
        {
            if (navigationManager != null)
                navigationManager.OnNavTypeString.RemoveListener(HandleNavigationType);
        }

        private void LateUpdate()
        {
            if (arrowRenderer == null)
                return;

            if (navigationManager == null || viewTransform == null || !HasCurrentTexture())
            {
                arrowRenderer.enabled = false;
                return;
            }

            if (showOnlyWhileNavigating &&
                navigationManager.Phase != NavigationPhase.Navigating)
            {
                arrowRenderer.enabled = false;
                return;
            }

            switch (preview)
            {
                case NavigationArrowPreview.Left:
                    ForceTurnType(NavTurnType.Left);
                    break;
                case NavigationArrowPreview.Right:
                    ForceTurnType(NavTurnType.Right);
                    break;
                case NavigationArrowPreview.Straight:
                    ForceTurnType(NavTurnType.Straight);
                    break;
                default:
                    UpdateStableTurn(usePlayerProgressForTurns
                        ? ComputeTurnFromPlayerProgress()
                        : navigationManager.CurrentTurnType);
                    break;
            }

            if (lockToView)
            {
                UpdateViewLockedPose();
                return;
            }

            UpdateArrowPose();
        }

        private void OnDestroy()
        {
            if (runtimeMaterial != null)
                Destroy(runtimeMaterial);
            if (runtimeMesh != null)
                Destroy(runtimeMesh);
            if (runtimeArrowObject != null)
                Destroy(runtimeArrowObject);
        }

        private void HandleNavigationType(string value)
        {
            if (usePlayerProgressForTurns)
                return;

            int rawValue;
            if (!int.TryParse(value, out rawValue))
                return;

            UpdateStableTurn((NavTurnType)rawValue);
        }

        private NavTurnType ComputeTurnFromPlayerProgress()
        {
            if (navigationManager == null ||
                navigationPositionSource == null ||
                navigationManager.CurrentPlan == null ||
                !navigationManager.CurrentPlan.success)
            {
                return NavTurnType.Straight;
            }

            IList<NavPose> path = navigationManager.CurrentPlan.path;
            if (path == null || path.Count < 3)
                return NavTurnType.Straight;

            int segmentIndex;
            Vector3 projection;
            FindClosestPathProjection(
                path,
                navigationPositionSource.position,
                out segmentIndex,
                out projection);

            float distanceToCorner = Vector3.Distance(
                projection,
                path[segmentIndex + 1].position);
            float announcementDistance = Mathf.Max(
                0.5f,
                turnAnnouncementDistanceMeters);
            float threshold = Mathf.Clamp(
                turnAngleThresholdDegrees,
                5f,
                60f);

            for (int cornerIndex = segmentIndex + 1;
                 cornerIndex < path.Count - 1;
                 cornerIndex++)
            {
                if (distanceToCorner > announcementDistance)
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
                    float angle = Vector3.SignedAngle(
                        incoming,
                        outgoing,
                        Vector3.up);
                    if (Mathf.Abs(angle) >= threshold)
                    {
                        if (Mathf.Abs(angle) > 135f)
                            return NavTurnType.UTurn;
                        return angle < 0f
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
                Vector3 segment = path[i + 1].position - start;
                float lengthSquared = segment.sqrMagnitude;
                float t = lengthSquared > 0.0001f
                    ? Mathf.Clamp01(
                        Vector3.Dot(position - start, segment) /
                        lengthSquared)
                    : 0f;
                Vector3 candidate = start + segment * t;
                float distanceSquared =
                    (position - candidate).sqrMagnitude;
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    segmentIndex = i;
                    projection = candidate;
                }
            }
        }

        private void UpdateStableTurn(NavTurnType received)
        {
            NavTurnType requestedTurn = SimplifyTurnType(received);
            if (requestedTurn != NavTurnType.Straight)
            {
                lastTurnSignalTime = Time.time;
                if (currentTurn != requestedTurn)
                {
                    turnDisplayStartedTime = Time.time;
                    ForceTurnType(requestedTurn);
                }
                return;
            }

            if (currentTurn == NavTurnType.Straight)
                return;

            bool minimumDisplayElapsed =
                Time.time - turnDisplayStartedTime >= minimumTurnDisplaySeconds;
            bool straightConfirmed =
                Time.time - lastTurnSignalTime >= straightConfirmationSeconds;
            if (minimumDisplayElapsed && straightConfirmed)
                ForceTurnType(NavTurnType.Straight);
        }

        private static NavTurnType SimplifyTurnType(NavTurnType received)
        {
            switch (received)
            {
                case NavTurnType.Left:
                case NavTurnType.LeftForward:
                case NavTurnType.UTurn:
                    return NavTurnType.Left;
                case NavTurnType.Right:
                case NavTurnType.RightForward:
                    return NavTurnType.Right;
                default:
                    return NavTurnType.Straight;
            }
        }

        private void ForceTurnType(NavTurnType nextTurn)
        {
            if (nextTurn == currentTurn)
                return;

            currentTurn = nextTurn;
            ApplyCurrentTexture();

            if (logTurnChanges)
                Debug.Log(
                    "[GroundNavigationArrow] Displaying " + currentTurn,
                    this);
        }

        private void CreateRuntimeArrow()
        {
            runtimeArrowObject = new GameObject("Runtime Ground Navigation Arrow");
            runtimeArrowObject.transform.SetParent(transform, false);

            MeshFilter meshFilter = runtimeArrowObject.AddComponent<MeshFilter>();
            arrowRenderer = runtimeArrowObject.AddComponent<MeshRenderer>();

            runtimeMesh = BuildGroundQuad();
            meshFilter.sharedMesh = runtimeMesh;

            Shader shader = Resources.Load<Shader>("GroundNavigationArrow");
            if (shader == null)
                shader = Shader.Find("MotorcycleNavigation/GroundNavigationArrow");
            if (shader == null)
            {
                Debug.LogError(
                    "GroundNavigationArrow shader was not found. " +
                    "Keep GroundNavigationArrow.shader inside Assets/Resources.",
                    this);
                arrowRenderer.enabled = false;
                return;
            }

            runtimeMaterial = new Material(shader)
            {
                name = "Runtime Ground Navigation Arrow Material"
            };
            runtimeMaterial.SetFloat(
                ZTestId,
                lockToView
                    ? (float)CompareFunction.Always
                    : (float)CompareFunction.LessEqual);
            arrowRenderer.sharedMaterial = runtimeMaterial;
            arrowRenderer.shadowCastingMode = ShadowCastingMode.Off;
            arrowRenderer.receiveShadows = false;
            arrowRenderer.lightProbeUsage = LightProbeUsage.Off;
            arrowRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            propertyBlock = new MaterialPropertyBlock();
        }

        private void UpdateViewLockedPose()
        {
            runtimeArrowObject.transform.SetPositionAndRotation(
                viewTransform.TransformPoint(viewLocalOffset),
                Quaternion.LookRotation(
                    viewTransform.up,
                    -viewTransform.forward));

            runtimeArrowObject.transform.localScale = new Vector3(
                viewArrowWidthMeters / Mathf.Max(0.05f, arrowWidthMeters),
                1f,
                viewArrowLengthMeters / Mathf.Max(0.05f, arrowLengthMeters));

            if (runtimeMaterial != null)
                runtimeMaterial.SetFloat(ZTestId, (float)CompareFunction.Always);
            arrowRenderer.enabled = true;
        }

        private Mesh BuildGroundQuad()
        {
            float halfWidth = arrowWidthMeters * 0.5f;
            float halfLength = arrowLengthMeters * 0.5f;

            Mesh mesh = new Mesh
            {
                name = "Runtime Ground Navigation Arrow Mesh",
                vertices = new[]
                {
                    new Vector3(-halfWidth, 0f, -halfLength),
                    new Vector3(-halfWidth, 0f,  halfLength),
                    new Vector3( halfWidth, 0f,  halfLength),
                    new Vector3( halfWidth, 0f, -halfLength)
                },
                uv = new[]
                {
                    new Vector2(0f, 0f),
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f),
                    new Vector2(1f, 0f)
                },
                triangles = new[]
                {
                    0, 1, 2,
                    0, 2, 3
                },
                normals = new[]
                {
                    Vector3.up,
                    Vector3.up,
                    Vector3.up,
                    Vector3.up
                }
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        private void UpdateArrowPose()
        {
            runtimeArrowObject.transform.localScale = Vector3.one;
            if (runtimeMaterial != null)
                runtimeMaterial.SetFloat(ZTestId, (float)CompareFunction.LessEqual);

            Vector3 flatForward = Vector3.ProjectOnPlane(
                viewTransform.forward,
                Vector3.up);
            if (flatForward.sqrMagnitude < 0.0001f)
            {
                arrowRenderer.enabled = false;
                return;
            }

            flatForward.Normalize();
            Vector3 target = viewTransform.position +
                flatForward * distanceAheadMeters;
            Vector3 rayOrigin = target + Vector3.up * raycastHeightMeters;

            RaycastHit hit;
            bool foundGround = Physics.Raycast(
                rayOrigin,
                Vector3.down,
                out hit,
                raycastDistanceMeters,
                groundLayers,
                QueryTriggerInteraction.Ignore);

            if (!foundGround && fallbackToAllLayers &&
                groundLayers.value != Physics.AllLayers)
            {
                foundGround = Physics.Raycast(
                    rayOrigin,
                    Vector3.down,
                    out hit,
                    raycastDistanceMeters,
                    Physics.AllLayers,
                    QueryTriggerInteraction.Ignore);
            }

            if (!foundGround)
            {
                if (useFallbackGroundPlane)
                {
                    runtimeArrowObject.transform.SetPositionAndRotation(
                        new Vector3(
                            target.x,
                            fallbackGroundHeight + groundOffsetMeters,
                            target.z),
                        Quaternion.LookRotation(flatForward, Vector3.up));
                    arrowRenderer.enabled = true;
                }
                else
                {
                    arrowRenderer.enabled = !hideWhenGroundIsNotFound;
                    if (!hideWhenGroundIsNotFound)
                    {
                        runtimeArrowObject.transform.SetPositionAndRotation(
                            target - Vector3.up,
                            Quaternion.LookRotation(flatForward, Vector3.up));
                    }
                }
                return;
            }

            Vector3 groundForward = Vector3.ProjectOnPlane(
                flatForward,
                hit.normal);
            if (groundForward.sqrMagnitude < 0.0001f)
            {
                arrowRenderer.enabled = false;
                return;
            }

            runtimeArrowObject.transform.SetPositionAndRotation(
                hit.point + hit.normal * groundOffsetMeters,
                Quaternion.LookRotation(groundForward.normalized, hit.normal));
            arrowRenderer.enabled = true;
        }

        private void ApplyCurrentTexture()
        {
            if (arrowRenderer == null || propertyBlock == null)
                return;

            Texture texture = GetCurrentTexture();
            arrowRenderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetTexture(BaseMapId, texture);
            propertyBlock.SetTexture(MainTexId, texture);
            arrowRenderer.SetPropertyBlock(propertyBlock);
        }

        private Texture GetCurrentTexture()
        {
            switch (currentTurn)
            {
                case NavTurnType.Left:
                    return leftTexture;
                case NavTurnType.Right:
                    return rightTexture;
                default:
                    return straightTexture;
            }
        }

        private bool HasCurrentTexture()
        {
            return GetCurrentTexture() != null;
        }

        private void OnValidate()
        {
            arrowWidthMeters = Mathf.Max(0.05f, arrowWidthMeters);
            arrowLengthMeters = Mathf.Max(0.05f, arrowLengthMeters);
            viewArrowWidthMeters = Mathf.Max(0.05f, viewArrowWidthMeters);
            viewArrowLengthMeters = Mathf.Max(0.05f, viewArrowLengthMeters);
            minimumTurnDisplaySeconds = Mathf.Max(0f, minimumTurnDisplaySeconds);
            straightConfirmationSeconds = Mathf.Max(0f, straightConfirmationSeconds);
            turnAnnouncementDistanceMeters = Mathf.Max(
                0.5f,
                turnAnnouncementDistanceMeters);

            if (runtimeMesh == null)
                return;

            Mesh replacement = BuildGroundQuad();
            MeshFilter meshFilter = runtimeArrowObject != null
                ? runtimeArrowObject.GetComponent<MeshFilter>()
                : null;
            if (meshFilter != null)
                meshFilter.sharedMesh = replacement;

            Destroy(runtimeMesh);
            runtimeMesh = replacement;
        }
    }
}
