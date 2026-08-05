using System.Collections.Generic;
using UnityEngine;

namespace MotorcycleNavigation
{
    public sealed class PlannedPathAutoMover : MonoBehaviour
    {
        public MotorcycleNavigationManager navigationManager;
        public Transform playerRoot;
        public float speedMetersPerSecond = 2.0f;
        public float waypointToleranceMeters = 0.15f;
        public bool updateNavigationPose = true;
        public bool rotateRootTowardMovement = true;
        public bool navigationYawFollowsMovement = true;
        public float rotationLerp = 8f;

        private NavigationResult activePlan;
        private int pathIndex;
        private Vector3 lastMoveDirection;
        private bool manualOverride;

        public bool IsManualOverrideActive => manualOverride;

        private void Awake()
        {
            if (navigationManager == null)
                navigationManager = FindObjectOfType<MotorcycleNavigationManager>();
            if (playerRoot == null)
                playerRoot = transform;
        }

        private void Update()
        {
            if (navigationManager == null || playerRoot == null)
                return;

            if (manualOverride)
                return;

            NavigationResult plan = navigationManager.CurrentPlan;
            if (plan == null || !plan.success || plan.path.Count == 0)
            {
                if (navigationManager.Phase != NavigationPhase.Planning)
                    PushPoseToNavigation();
                return;
            }

            if (!ReferenceEquals(activePlan, plan))
            {
                activePlan = plan;
                pathIndex = FindClosestPathIndex(plan.path, playerRoot.position);
            }

            MoveAlong(plan.path);
            PushPoseToNavigation();
        }

        public void SetManualOverride(bool active)
        {
            if (manualOverride == active)
                return;

            manualOverride = active;
            if (!manualOverride)
            {
                // Reacquire the closest path point after manual movement.
                activePlan = null;
                pathIndex = 0;
            }
        }

        private void MoveAlong(IList<NavPose> path)
        {
            if (pathIndex >= path.Count)
                return;

            float remainingStep = Mathf.Max(0f, speedMetersPerSecond) * Time.deltaTime;
            Vector3 position = playerRoot.position;
            lastMoveDirection = Vector3.zero;

            while (remainingStep > 0f && pathIndex < path.Count)
            {
                Vector3 target = path[pathIndex].position;
                target.y = position.y;
                Vector3 delta = target - position;
                float distance = delta.magnitude;

                if (distance <= waypointToleranceMeters)
                {
                    pathIndex++;
                    continue;
                }

                float move = Mathf.Min(remainingStep, distance);
                Vector3 direction = delta / distance;
                position += direction * move;
                remainingStep -= move;
                lastMoveDirection = direction;

                if (rotateRootTowardMovement && direction.sqrMagnitude > 0.0001f)
                {
                    Quaternion targetRotation = Quaternion.LookRotation(direction, Vector3.up);
                    playerRoot.rotation = Quaternion.Slerp(
                        playerRoot.rotation,
                        targetRotation,
                        Mathf.Clamp01(rotationLerp * Time.deltaTime));
                }
            }

            playerRoot.position = position;
        }

        private void PushPoseToNavigation()
        {
            if (!updateNavigationPose || navigationManager == null || playerRoot == null)
                return;

            navigationManager.UpdatePosition(playerRoot.position);
            if (navigationYawFollowsMovement && lastMoveDirection.sqrMagnitude > 0.0001f)
                navigationManager.UpdateRotationQuaternion(Quaternion.LookRotation(lastMoveDirection, Vector3.up));
            else
                navigationManager.UpdateRotationQuaternion(playerRoot.rotation);
        }

        private static int FindClosestPathIndex(IList<NavPose> path, Vector3 position)
        {
            int best = 0;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < path.Count; i++)
            {
                float distance = (path[i].position - position).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
            return best;
        }
    }
}
