using UnityEngine;

namespace MotorcycleNavigation
{
    public sealed class FootprintCollisionChecker
    {
        private readonly GridCostmap map;
        private readonly MotorcycleFootprintSettings settings;

        public FootprintCollisionChecker(GridCostmap map, MotorcycleFootprintSettings settings)
        {
            this.map = map;
            this.settings = settings;
        }

        public bool IsPoseFree(NavPose pose)
        {
            Vector2 forward;
            Vector2 right;
            GetAxes(pose.yawDeg, out forward, out right);

            Vector2 center = new Vector2(pose.position.x, pose.position.z)
                + right * settings.centerOffsetLocal.x
                + forward * settings.centerOffsetLocal.y;

            float halfWidth = settings.widthMeters * 0.5f;
            float halfLength = settings.lengthMeters * 0.5f;
            float radius = Mathf.Sqrt(halfWidth * halfWidth + halfLength * halfLength);

            int minX = Mathf.FloorToInt((center.x - radius - map.OriginXZ.x) / map.Resolution);
            int maxX = Mathf.FloorToInt((center.x + radius - map.OriginXZ.x) / map.Resolution);
            int minY = Mathf.FloorToInt((center.y - radius - map.OriginXZ.y) / map.ResolutionZ);
            int maxY = Mathf.FloorToInt((center.y + radius - map.OriginXZ.y) / map.ResolutionZ);

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!map.InBounds(x, y))
                        return false;

                    Vector3 world = map.CellToWorld(x, y);
                    Vector2 rel = new Vector2(world.x - center.x, world.z - center.y);
                    float localX = Vector2.Dot(rel, right);
                    float localY = Vector2.Dot(rel, forward);

                    if (Mathf.Abs(localX) > halfWidth || Mathf.Abs(localY) > halfLength)
                        continue;

                    byte cost = map.GetCost(x, y);
                    if (cost == GridCostmap.NoInformation && !settings.allowUnknown)
                        return false;
                    if (cost != GridCostmap.NoInformation && cost >= settings.obstacleThreshold)
                        return false;
                }
            }

            return true;
        }

        public bool IsSegmentFree(NavPose from, NavPose to)
        {
            float distance = Vector3.Distance(from.position, to.position);
            int samples = Mathf.Max(2, Mathf.CeilToInt(distance * settings.segmentCollisionSamplesPerMeter));
            for (int i = 0; i <= samples; i++)
            {
                float t = i / (float)samples;
                NavPose sample = new NavPose(
                    Vector3.Lerp(from.position, to.position, t),
                    Mathf.LerpAngle(from.yawDeg, to.yawDeg, t));

                if (!IsPoseFree(sample))
                    return false;
            }
            return true;
        }

        private static void GetAxes(float yawDeg, out Vector2 forward, out Vector2 right)
        {
            float rad = yawDeg * Mathf.Deg2Rad;
            forward = new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
            right = new Vector2(Mathf.Cos(rad), -Mathf.Sin(rad));
        }
    }
}
