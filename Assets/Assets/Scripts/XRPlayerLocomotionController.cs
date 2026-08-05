using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace MotorcycleNavigation
{
    [DefaultExecutionOrder(-200)]
    [DisallowMultipleComponent]
    public sealed class XRPlayerLocomotionController : MonoBehaviour
    {
        [Header("References")]
        public Transform playerRoot;
        public Camera viewCamera;
        [Tooltip("Usually the XR Origin Camera Offset transform. Mouse look is applied here so TrackedPoseDriver can keep controlling the camera locally.")]
        public Transform viewPivot;
        public MotorcycleNavigationManager navigationManager;
        public VRFullMapGoalSelector mapSelector;
        public PlannedPathAutoMover autoMover;

#if ENABLE_INPUT_SYSTEM
        [Header("Optional Input Actions")]
        [Tooltip("Optional Player/Move action. WASD is read directly from Keyboard.current when this is not assigned.")]
        public InputActionReference moveAction;
        [Tooltip("Optional Player/Look action. Pointer.current.delta is used when this is not assigned.")]
        public InputActionReference lookAction;
        [Tooltip("Optional Player/Jump action. Space is read directly when this is not assigned.")]
        public InputActionReference jumpAction;
        [Tooltip("Optional Player/Sprint action. Left/right Shift is read directly when this is not assigned.")]
        public InputActionReference sprintAction;
#endif

        [Header("Ground Movement")]
        [Min(0f)] public float moveSpeedMetersPerSecond = 5f;
        [Min(1f)] public float sprintMultiplier = 1.6f;
        [Min(0f)] public float jumpHeightMeters = 1.25f;
        [Tooltip("Use a negative value.")]
        public float gravityMetersPerSecondSquared = -18f;
        [Min(0f)] public float groundedToleranceMeters = 0.02f;
        [Tooltip("Reject ground movement that enters a blocked navigation footprint. Disabled by default because the current demo start can be outside the road mask.")]
        public bool constrainGroundMovementToNavigationMap = false;

        [Header("Minecraft-style Flight")]
        [Min(0.1f)] public float doubleSpaceIntervalSeconds = 0.3f;
        [Min(0f)] public float flyHorizontalSpeedMetersPerSecond = 8f;
        [Min(0f)] public float flyVerticalSpeedMetersPerSecond = 5f;
        [Tooltip("When enabled, flying X/Z movement is also restricted by the navigation costmap.")]
        public bool constrainFlightToNavigationMap = false;

        [Header("Mouse Look")]
        [Min(0f)] public float mouseSensitivityDegreesPerPixel = 0.12f;
        public float minimumPitchDegrees = -85f;
        public float maximumPitchDegrees = 85f;
        public bool invertMouseY = false;

        [Header("Navigation Coordination")]
        public bool replanAfterManualMovement = true;
        [Min(0f)] public float minimumHorizontalMoveForReplanMeters = 0.15f;
        [Min(0f)] public float replanAfterInputStopsSeconds = 0.2f;
        [Min(0f)] public float minimumManualReplanIntervalSeconds = 0.75f;
        [Tooltip("The route follower is paused while the user moves, jumps, flies, waits for a manual replan, or uses the full map.")]
        public bool coordinateWithAutoMover = true;
        public bool pauseAutoMoverWhileMapVisible = true;

        [Header("Map and Cursor")]
        public bool disableLocomotionWhileMapVisible = true;
        public bool manageCursorLock = true;
        public bool logStateChanges = true;

        public bool IsFlying { get; private set; }
        public bool IsGrounded
        {
            get
            {
                return !IsFlying &&
                       playerRoot != null &&
                       playerRoot.position.y <= groundHeight + Mathf.Max(0f, groundedToleranceMeters);
            }
        }

        public bool ShouldSuspendAutoMove { get; private set; }

        private Quaternion baseViewPivotLocalRotation = Quaternion.identity;
        private float mouseYawDegrees;
        private float mousePitchDegrees;
        private float groundHeight;
        private float verticalVelocity;
        private float lastSpacePressTime = -999f;
        private bool manualReplanPending;
        private float manualHorizontalDistance;
        private float lastManualMoveTime = -999f;
        private float lastManualReplanTime = -999f;
        private bool cursorStateInitialized;
        private bool lastCursorShouldLock;

#if ENABLE_INPUT_SYSTEM
        private bool enabledMoveAction;
        private bool enabledLookAction;
        private bool enabledJumpAction;
        private bool enabledSprintAction;
#endif

        private void Awake()
        {
            if (playerRoot == null)
                playerRoot = transform;
            if (viewCamera == null)
                viewCamera = Camera.main;
            if (viewPivot == null && viewCamera != null)
                viewPivot = viewCamera.transform.parent;
            if (navigationManager == null)
                navigationManager = Object.FindFirstObjectByType<MotorcycleNavigationManager>();
            if (mapSelector == null)
                mapSelector = Object.FindFirstObjectByType<VRFullMapGoalSelector>();
            if (autoMover == null && playerRoot != null)
                autoMover = playerRoot.GetComponent<PlannedPathAutoMover>();

            if (viewPivot != null)
                baseViewPivotLocalRotation = viewPivot.localRotation;
            if (playerRoot != null)
                groundHeight = playerRoot.position.y;
        }

        private void OnEnable()
        {
#if ENABLE_INPUT_SYSTEM
            EnableActionIfNeeded(moveAction, ref enabledMoveAction);
            EnableActionIfNeeded(lookAction, ref enabledLookAction);
            EnableActionIfNeeded(jumpAction, ref enabledJumpAction);
            EnableActionIfNeeded(sprintAction, ref enabledSprintAction);
#endif
            if (mapSelector != null)
                mapSelector.VisibilityChanged += OnMapVisibilityChanged;
        }

        private void Start()
        {
            PushPoseToNavigation(Vector3.zero);
            ApplyCursorState();
        }

        private void OnDisable()
        {
            if (mapSelector != null)
                mapSelector.VisibilityChanged -= OnMapVisibilityChanged;

            SetAutoMoverSuspended(false);

#if ENABLE_INPUT_SYSTEM
            DisableActionIfOwned(moveAction, ref enabledMoveAction);
            DisableActionIfOwned(lookAction, ref enabledLookAction);
            DisableActionIfOwned(jumpAction, ref enabledJumpAction);
            DisableActionIfOwned(sprintAction, ref enabledSprintAction);
#endif

            if (manageCursorLock)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        private void Update()
        {
            TryRequestPendingManualReplan();

            bool mapVisible = mapSelector != null && mapSelector.IsVisible;
            bool mapBlocksLocomotion = disableLocomotionWhileMapVisible && mapVisible;

            if (!mapBlocksLocomotion)
                ApplyMouseLook(ReadLookInput());

            if (mapBlocksLocomotion)
            {
                lastSpacePressTime = -999f;
                SetAutoMoverSuspended((pauseAutoMoverWhileMapVisible && mapVisible) || manualReplanPending);
                return;
            }

            Vector2 moveInput = ReadMoveInput();
            if (moveInput.sqrMagnitude > 1f)
                moveInput.Normalize();

            bool jumpPressed = ReadJumpPressedThisFrame();
            bool jumpHeld = ReadJumpHeld();
            bool shiftHeld = ReadShiftHeld();
            HandleJumpAndFlightToggle(jumpPressed);

            Vector3 horizontalDirection = CalculateViewRelativeDirection(moveInput);
            float horizontalSpeed = IsFlying
                ? Mathf.Max(0f, flyHorizontalSpeedMetersPerSecond)
                : Mathf.Max(0f, moveSpeedMetersPerSecond) * (shiftHeld ? Mathf.Max(1f, sprintMultiplier) : 1f);

            Vector3 requestedDelta = horizontalDirection * horizontalSpeed * Time.deltaTime;
            if (IsFlying)
            {
                float verticalInput = (jumpHeld ? 1f : 0f) - (shiftHeld ? 1f : 0f);
                requestedDelta.y = verticalInput * Mathf.Max(0f, flyVerticalSpeedMetersPerSecond) * Time.deltaTime;
                verticalVelocity = 0f;
            }
            else
            {
                verticalVelocity += Mathf.Min(-0.01f, gravityMetersPerSecondSquared) * Time.deltaTime;
                requestedDelta.y = verticalVelocity * Time.deltaTime;
            }

            Vector3 actualDelta = MovePlayer(requestedDelta, horizontalDirection);
            bool changedPosition = actualDelta.sqrMagnitude > 0.0000001f;
            bool changedHorizontally = new Vector2(actualDelta.x, actualDelta.z).sqrMagnitude > 0.0000001f;

            if (changedPosition)
                PushPoseToNavigation(changedHorizontally ? horizontalDirection : Vector3.zero);
            if (changedHorizontally)
                MarkManualMovementForReplan(actualDelta);

            bool hasMoveInput = moveInput.sqrMagnitude > 0.0001f;
            bool hasFlyVerticalInput = IsFlying && (jumpHeld || shiftHeld);
            bool manuallyControlling = hasMoveInput || hasFlyVerticalInput || IsFlying || !IsGrounded;
            SetAutoMoverSuspended(manuallyControlling || manualReplanPending);

            TryRequestPendingManualReplan();
        }

        private Vector3 MovePlayer(Vector3 requestedDelta, Vector3 horizontalDirection)
        {
            if (playerRoot == null)
                return Vector3.zero;

            Vector3 start = playerRoot.position;
            Vector3 horizontalDelta = new Vector3(requestedDelta.x, 0f, requestedDelta.z);
            bool constrainToMap = IsFlying ? constrainFlightToNavigationMap : constrainGroundMovementToNavigationMap;
            horizontalDelta = ResolveNavigableHorizontalDelta(start, horizontalDelta, horizontalDirection, constrainToMap);

            Vector3 next = start + horizontalDelta;
            next.y += requestedDelta.y;

            if (!IsFlying && next.y <= groundHeight)
            {
                next.y = groundHeight;
                if (verticalVelocity < 0f)
                    verticalVelocity = 0f;
            }

            playerRoot.position = next;
            return next - start;
        }

        private Vector3 ResolveNavigableHorizontalDelta(
            Vector3 start,
            Vector3 requestedDelta,
            Vector3 horizontalDirection,
            bool constrainToMap)
        {
            if (!constrainToMap ||
                navigationManager == null ||
                navigationManager.InflatedCostmap == null ||
                requestedDelta.sqrMagnitude <= 0.0000001f)
            {
                return requestedDelta;
            }

            float yaw = horizontalDirection.sqrMagnitude > 0.0001f
                ? Mathf.Atan2(horizontalDirection.x, horizontalDirection.z) * Mathf.Rad2Deg
                : playerRoot.eulerAngles.y;

            Vector3 candidate = start + requestedDelta;
            if (navigationManager.IsPoseNavigable(candidate, yaw))
                return requestedDelta;

            Vector3 xOnly = new Vector3(requestedDelta.x, 0f, 0f);
            Vector3 zOnly = new Vector3(0f, 0f, requestedDelta.z);
            bool canMoveX = xOnly.sqrMagnitude > 0.0000001f && navigationManager.IsPoseNavigable(start + xOnly, yaw);
            bool canMoveZ = zOnly.sqrMagnitude > 0.0000001f && navigationManager.IsPoseNavigable(start + zOnly, yaw);

            if (canMoveX && canMoveZ)
                return xOnly.sqrMagnitude >= zOnly.sqrMagnitude ? xOnly : zOnly;
            if (canMoveX)
                return xOnly;
            if (canMoveZ)
                return zOnly;
            return Vector3.zero;
        }

        private Vector3 CalculateViewRelativeDirection(Vector2 moveInput)
        {
            if (moveInput.sqrMagnitude <= 0.0001f)
                return Vector3.zero;

            Transform directionSource = viewCamera != null ? viewCamera.transform : playerRoot;
            Vector3 forward = directionSource != null ? directionSource.forward : Vector3.forward;
            Vector3 right = directionSource != null ? directionSource.right : Vector3.right;
            forward.y = 0f;
            right.y = 0f;

            if (forward.sqrMagnitude <= 0.0001f)
                forward = playerRoot != null ? Vector3.ProjectOnPlane(playerRoot.forward, Vector3.up) : Vector3.forward;
            if (right.sqrMagnitude <= 0.0001f)
                right = Vector3.Cross(Vector3.up, forward);

            forward.Normalize();
            right.Normalize();
            Vector3 direction = right * moveInput.x + forward * moveInput.y;
            return direction.sqrMagnitude > 1f ? direction.normalized : direction;
        }

        private void HandleJumpAndFlightToggle(bool jumpPressed)
        {
            if (!jumpPressed)
                return;

            float now = Time.unscaledTime;
            bool doublePress = now - lastSpacePressTime <= Mathf.Max(0.1f, doubleSpaceIntervalSeconds);
            if (doublePress)
            {
                lastSpacePressTime = -999f;
                SetFlying(!IsFlying);
                return;
            }

            lastSpacePressTime = now;
            if (!IsFlying && IsGrounded)
            {
                float gravityMagnitude = Mathf.Max(0.01f, -gravityMetersPerSecondSquared);
                verticalVelocity = Mathf.Sqrt(2f * gravityMagnitude * Mathf.Max(0f, jumpHeightMeters));
            }
        }

        public void SetFlying(bool flying)
        {
            if (IsFlying == flying)
                return;

            IsFlying = flying;
            verticalVelocity = 0f;
            if (logStateChanges)
                Debug.Log("[XRPlayerLocomotionController] Flight mode " + (IsFlying ? "enabled." : "disabled."), this);
        }

        public void ToggleFlying()
        {
            SetFlying(!IsFlying);
        }

        public void SetGroundHeightToCurrentPosition()
        {
            if (playerRoot != null)
                groundHeight = playerRoot.position.y;
        }

        private void ApplyMouseLook(Vector2 lookDelta)
        {
            if (viewPivot == null || lookDelta.sqrMagnitude <= 0f)
                return;

            float sensitivity = Mathf.Max(0f, mouseSensitivityDegreesPerPixel);
            mouseYawDegrees += lookDelta.x * sensitivity;
            float pitchDelta = lookDelta.y * sensitivity * (invertMouseY ? 1f : -1f);
            mousePitchDegrees = Mathf.Clamp(
                mousePitchDegrees + pitchDelta,
                Mathf.Min(minimumPitchDegrees, maximumPitchDegrees),
                Mathf.Max(minimumPitchDegrees, maximumPitchDegrees));

            viewPivot.localRotation = baseViewPivotLocalRotation * Quaternion.Euler(mousePitchDegrees, mouseYawDegrees, 0f);
        }

        private void PushPoseToNavigation(Vector3 horizontalDirection)
        {
            if (navigationManager == null || playerRoot == null)
                return;

            navigationManager.UpdatePosition(playerRoot.position);
            if (horizontalDirection.sqrMagnitude > 0.0001f)
                navigationManager.UpdateRotationQuaternion(Quaternion.LookRotation(horizontalDirection, Vector3.up));
            else
                navigationManager.UpdateRotationQuaternion(playerRoot.rotation);
        }

        private void MarkManualMovementForReplan(Vector3 actualDelta)
        {
            if (!replanAfterManualMovement || navigationManager == null || navigationManager.CurrentGoal == null)
                return;

            NavigationPhase phase = navigationManager.Phase;
            if (phase != NavigationPhase.Navigating && phase != NavigationPhase.Arrived)
                return;

            manualHorizontalDistance += new Vector2(actualDelta.x, actualDelta.z).magnitude;
            manualReplanPending = true;
            lastManualMoveTime = Time.unscaledTime;
        }

        private void TryRequestPendingManualReplan()
        {
            if (!manualReplanPending || navigationManager == null)
                return;

            if (navigationManager.CurrentGoal == null)
            {
                ClearPendingManualReplan();
                return;
            }

            if (navigationManager.Phase == NavigationPhase.Planning)
            {
                ClearPendingManualReplan();
                return;
            }

            float now = Time.unscaledTime;
            if (manualHorizontalDistance < Mathf.Max(0f, minimumHorizontalMoveForReplanMeters) ||
                now - lastManualMoveTime < Mathf.Max(0f, replanAfterInputStopsSeconds) ||
                now - lastManualReplanTime < Mathf.Max(0f, minimumManualReplanIntervalSeconds))
            {
                return;
            }

            NavigationPhase phase = navigationManager.Phase;
            if (phase != NavigationPhase.Navigating && phase != NavigationPhase.Arrived)
                return;

            navigationManager.RequestReplan(true);
            lastManualReplanTime = now;
            ClearPendingManualReplan();

            if (logStateChanges)
                Debug.Log("[XRPlayerLocomotionController] Requested route replan after manual movement.", this);
        }

        private void ClearPendingManualReplan()
        {
            manualReplanPending = false;
            manualHorizontalDistance = 0f;
        }

        private void SetAutoMoverSuspended(bool suspended)
        {
            ShouldSuspendAutoMove = suspended;
            if (coordinateWithAutoMover && autoMover != null)
                autoMover.SetManualOverride(suspended);
        }

        private void OnMapVisibilityChanged(bool isVisible)
        {
            lastSpacePressTime = -999f;
            ApplyCursorState();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
                ApplyCursorState();
        }

        private void ApplyCursorState()
        {
            if (!manageCursorLock)
                return;

            bool shouldLock = mapSelector == null || !mapSelector.IsVisible;
            if (cursorStateInitialized && shouldLock == lastCursorShouldLock)
                return;

            cursorStateInitialized = true;
            lastCursorShouldLock = shouldLock;
            Cursor.lockState = shouldLock ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !shouldLock;
        }

        private Vector2 ReadMoveInput()
        {
#if ENABLE_INPUT_SYSTEM
            if (moveAction != null && moveAction.action != null)
                return moveAction.action.ReadValue<Vector2>();

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                float x = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
                float y = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);
                return new Vector2(x, y);
            }
#endif
            return Vector2.zero;
        }

        private Vector2 ReadLookInput()
        {
#if ENABLE_INPUT_SYSTEM
            if (lookAction != null && lookAction.action != null)
                return lookAction.action.ReadValue<Vector2>();
            if (Pointer.current != null)
                return Pointer.current.delta.ReadValue();
#endif
            return Vector2.zero;
        }

        private bool ReadJumpPressedThisFrame()
        {
#if ENABLE_INPUT_SYSTEM
            if (jumpAction != null && jumpAction.action != null)
                return jumpAction.action.WasPressedThisFrame();
            return Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
#else
            return false;
#endif
        }

        private bool ReadJumpHeld()
        {
#if ENABLE_INPUT_SYSTEM
            if (jumpAction != null && jumpAction.action != null)
                return jumpAction.action.IsPressed();
            return Keyboard.current != null && Keyboard.current.spaceKey.isPressed;
#else
            return false;
#endif
        }

        private bool ReadShiftHeld()
        {
#if ENABLE_INPUT_SYSTEM
            if (sprintAction != null && sprintAction.action != null)
                return sprintAction.action.IsPressed();
            Keyboard keyboard = Keyboard.current;
            return keyboard != null && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
#else
            return false;
#endif
        }

#if ENABLE_INPUT_SYSTEM
        private static void EnableActionIfNeeded(InputActionReference reference, ref bool enabledByThisComponent)
        {
            enabledByThisComponent = false;
            if (reference == null || reference.action == null || reference.action.enabled)
                return;

            reference.action.Enable();
            enabledByThisComponent = true;
        }

        private static void DisableActionIfOwned(InputActionReference reference, ref bool enabledByThisComponent)
        {
            if (enabledByThisComponent && reference != null && reference.action != null)
                reference.action.Disable();
            enabledByThisComponent = false;
        }
#endif
    }
}
