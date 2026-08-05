using System;
using System.Collections.Generic;
using UnityEngine;

namespace MotorcycleNavigation
{
    public enum NavTurnType
    {
        Straight = 0,
        Left = 1,
        LeftForward = 2,
        RightForward = 3,
        Right = 4,
        UTurn = 5
    }

    public enum EulerYawAxis
    {
        X,
        Y,
        Z
    }

    public enum NavigationPhase
    {
        Idle,
        Planning,
        Navigating,
        Failed,
        Arrived
    }

    [Serializable]
    public struct NavPose
    {
        public Vector3 position;
        public float yawDeg;

        public NavPose(Vector3 position, float yawDeg)
        {
            this.position = position;
            this.yawDeg = yawDeg;
        }
    }

    [Serializable]
    public class NamedGoal
    {
        public string name = "goal";
        public Vector3 worldPosition;
        public float yawDeg;
        public bool requireYaw;
    }

    [Serializable]
    public class CostmapBuildSettings
    {
        [Tooltip("How many world meters one source pixel represents.")]
        public float metersPerPixel = 0.1f;

        [Tooltip("Optional Z-axis meters per source pixel. Use 0 to reuse Meters Per Pixel.")]
        public float metersPerPixelZ = 0f;

        [Tooltip("World X/Z coordinate of the bottom-left cell of the map.")]
        public Vector2 worldOriginXZ = Vector2.zero;

        public bool flipVertical = true;
        public bool darkPixelsAreObstacles = true;

        [Range(0f, 1f)]
        public float obstacleLumaThreshold = 0.35f;

        [Range(0f, 1f)]
        public float unknownAlphaThreshold = 0.05f;

        [Tooltip("Treat saturated annotation colors such as lane lines, crosswalks, turn pockets, and no-U-turn overlays as free space instead of obstacles.")]
        public bool ignoreSemanticAnnotationColors = true;

        [Range(0f, 1f)]
        public float semanticSaturationThreshold = 0.45f;

        [Range(0f, 1f)]
        public float semanticValueThreshold = 0.25f;
    }

    [Serializable]
    public class InflationSettings
    {
        public float inflationRadiusMeters = 0.6f;
        public float inscribedRadiusMeters = 0.38f;
        public float costScalingFactor = 8f;
        public bool inflateUnknown = false;
    }

    [Serializable]
    public class MotorcycleFootprintSettings
    {
        public float lengthMeters = 2.1f;
        public float widthMeters = 0.75f;

        [Tooltip("Local X is right, local Y is forward in the Unity X/Z plane.")]
        public Vector2 centerOffsetLocal = Vector2.zero;

        [Range(1, 8)]
        public int segmentCollisionSamplesPerMeter = 3;

        public byte obstacleThreshold = GridCostmap.InscribedInflatedObstacle;
        public bool allowUnknown = false;
    }

    [Serializable]
    public class PlannerSettings
    {
        [Range(8, 72)]
        public int headingBins = 32;

        public float stepMeters = 1.0f;
        public float minTurningRadiusMeters = 2.0f;
        public float goalToleranceMeters = 0.45f;
        public float goalYawToleranceDeg = 35f;
        public bool requireGoalYawByDefault = false;

        public float turnPenalty = 0.25f;
        public float inflatedCostWeight = 3.0f;
        public float laneMismatchPenalty = 6.0f;

        public int maxExpandedNodes = 200000;

        [Tooltip("Use the position-only grid planner as the primary planner. This is more robust for VR demos on semantic road masks.")]
        public bool useGridPlannerOnly = false;

        [Tooltip("For VR/camera demos, fall back to a position-only grid A* when motorcycle pose planning fails.")]
        public bool fallbackToGridPlanner = false;

        [Tooltip("Bias fallback grid paths to the right side of the road based on travel direction.")]
        public bool rightSideDriving = false;

        public float rightSideOffsetMeters = 1.0f;
    }

    [Serializable]
    public class NavigationRuntimeSettings
    {
        public float tickPeriodSeconds = 0.25f;
        public float minReplanIntervalSeconds = 0.75f;
        public float replanDistanceFromPathMeters = 1.2f;
        public float severeReplanDistanceFromPathMeters = 2.5f;
        public float stuckTimeoutSeconds = 3.0f;
        public float progressEpsilonMeters = 0.25f;
        public bool replanWhenPathAheadBlocked = true;
        public bool replanWhenStuck = true;

        [Tooltip("How far along the planned path to search for the next turn.")]
        public float arrowLookaheadMeters = 12f;

        [Tooltip("Minimum path direction change that is shown as a left or right turn.")]
        [Range(5f, 60f)]
        public float arrowTurnThresholdDegrees = 15f;

        [Tooltip("Distance used to smooth short, jagged grid-planner path segments.")]
        public float arrowDirectionSampleMeters = 1.25f;
        public float navigationOutputPeriodSeconds = 1.0f;
        public float submapWindowMeters = 18.0f;
        public float planningDeadlineSeconds = 2.0f;
        public bool emitLoadingMapDuringPlanning = true;

        [Range(64, 1024)]
        public int submapPixels = 256;

        [Range(64, 1024)]
        public int loadingMapPixels = 256;

        [Range(30, 95)]
        public int jpegQuality = 70;
    }

    public class NavigationResult
    {
        public bool success;
        public string failureReason;
        public int expandedNodes;
        public readonly List<NavPose> path = new List<NavPose>();
    }
}
