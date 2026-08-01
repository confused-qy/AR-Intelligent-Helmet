using System;
using System.Collections.Generic;
using UnityEngine;

namespace MotorcycleNavigation
{
    public abstract class NavigationRuleProviderBase : MonoBehaviour
    {
        public abstract bool AllowsPose(GridCostmap map, int x, int y, float yawDeg);
        public abstract float TraversalMultiplier(GridCostmap map, int x, int y, float yawDeg);
        public abstract bool AllowsTurn(Vector3 worldPosition, float fromYawDeg, float toYawDeg);

        public virtual bool AllowsTransition(GridCostmap map, NavPose fromPose, NavPose toPose)
        {
            return AllowsTurn(fromPose.position, fromPose.yawDeg, toPose.yawDeg);
        }

        public virtual float TransitionMultiplier(GridCostmap map, NavPose fromPose, NavPose toPose)
        {
            return 1f;
        }
    }

    public sealed class NoNavigationRules : NavigationRuleProviderBase
    {
        public override bool AllowsPose(GridCostmap map, int x, int y, float yawDeg)
        {
            return true;
        }

        public override float TraversalMultiplier(GridCostmap map, int x, int y, float yawDeg)
        {
            return 1f;
        }

        public override bool AllowsTurn(Vector3 worldPosition, float fromYawDeg, float toYawDeg)
        {
            return true;
        }
    }

    public sealed class DirectionalLaneRuleProvider : NavigationRuleProviderBase
    {
        [Serializable]
        public class LaneZone
        {
            public string name = "lane";
            public Rect worldRectXZ;
            public float allowedYawDeg;
            public float toleranceDeg = 75f;
            public bool blockUTurn = true;
            public float alignedCostMultiplier = 0.85f;
            public float wrongWayCostMultiplier = 8.0f;

            public bool Contains(Vector3 world)
            {
                return world.x >= worldRectXZ.xMin && world.x <= worldRectXZ.xMax
                    && world.z >= worldRectXZ.yMin && world.z <= worldRectXZ.yMax;
            }
        }

        public List<LaneZone> lanes = new List<LaneZone>();

        public override bool AllowsPose(GridCostmap map, int x, int y, float yawDeg)
        {
            LaneZone lane = FindLane(map.CellToWorld(x, y));
            if (lane == null)
                return true;

            float diff = Mathf.Abs(Mathf.DeltaAngle(yawDeg, lane.allowedYawDeg));
            return diff <= lane.toleranceDeg;
        }

        public override float TraversalMultiplier(GridCostmap map, int x, int y, float yawDeg)
        {
            LaneZone lane = FindLane(map.CellToWorld(x, y));
            if (lane == null)
                return 1f;

            float diff = Mathf.Abs(Mathf.DeltaAngle(yawDeg, lane.allowedYawDeg));
            if (diff <= lane.toleranceDeg)
                return Mathf.Max(0.01f, lane.alignedCostMultiplier);
            return Mathf.Max(1f, lane.wrongWayCostMultiplier);
        }

        public override bool AllowsTurn(Vector3 worldPosition, float fromYawDeg, float toYawDeg)
        {
            LaneZone lane = FindLane(worldPosition);
            if (lane == null || !lane.blockUTurn)
                return true;

            return Mathf.Abs(Mathf.DeltaAngle(fromYawDeg, toYawDeg)) < 135f;
        }

        private LaneZone FindLane(Vector3 world)
        {
            for (int i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] != null && lanes[i].Contains(world))
                    return lanes[i];
            }
            return null;
        }
    }
}
