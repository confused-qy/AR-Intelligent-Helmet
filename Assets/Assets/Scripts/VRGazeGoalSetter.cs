using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace MotorcycleNavigation
{
    public sealed class VRGazeGoalSetter : MonoBehaviour
    {
        [Header("References")]
        public MotorcycleNavigationManager navigationManager;
        public Camera gazeCamera;

        [Header("Raycast")]
        public LayerMask goalLayers = ~0;
        public float maxDistance = 200f;
        public float goalYawDeg;
        public bool requireGoalYaw;

#if ENABLE_INPUT_SYSTEM
        [Header("Input System")]
        public InputActionReference confirmAction;
#endif

        private void Awake()
        {
            if (navigationManager == null)
                navigationManager = FindObjectOfType<MotorcycleNavigationManager>();
            if (gazeCamera == null)
                gazeCamera = Camera.main;
        }

        private void OnEnable()
        {
#if ENABLE_INPUT_SYSTEM
            if (confirmAction != null && confirmAction.action != null)
                confirmAction.action.Enable();
#endif
        }

        private void OnDisable()
        {
#if ENABLE_INPUT_SYSTEM
            if (confirmAction != null && confirmAction.action != null)
                confirmAction.action.Disable();
#endif
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (confirmAction != null && confirmAction.action != null && confirmAction.action.WasPressedThisFrame())
                SelectCurrentGazePoint();
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Space) || Input.GetMouseButtonDown(0))
                SelectCurrentGazePoint();
#endif
        }

        public bool SelectCurrentGazePoint()
        {
            if (navigationManager == null || gazeCamera == null)
                return false;

            Ray ray = new Ray(gazeCamera.transform.position, gazeCamera.transform.forward);
            RaycastHit hit;
            if (!Physics.Raycast(ray, out hit, maxDistance, goalLayers, QueryTriggerInteraction.Ignore))
                return false;

            navigationManager.SetGoalWorld(hit.point, goalYawDeg, requireGoalYaw);
            return true;
        }
    }
}
