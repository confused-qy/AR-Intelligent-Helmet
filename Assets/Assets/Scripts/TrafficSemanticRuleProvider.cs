using System;
using System.Collections.Generic;
using UnityEngine;

namespace MotorcycleNavigation
{
    public enum LaneBoundaryType
    {
        None,
        Dashed,
        Solid
    }

    public enum TrafficTurnKind
    {
        Straight,
        Left,
        Right,
        UTurn,
        Any
    }

    [Serializable]
    public sealed class TrafficSemanticMap
    {
        public float metersPerPixel = 0.1f;
        public List<NamedGoal> goals = new List<NamedGoal>();
        public List<SemanticLane> lanes = new List<SemanticLane>();
        public List<TurnConnector> turnConnectors = new List<TurnConnector>();
        public List<RuleRegion> regions = new List<RuleRegion>();
    }

    [Serializable]
    public sealed class SemanticLane
    {
        public string id = "lane";
        public string group = "";
        public Vector2[] polygonXZ;
        public Rect worldRectXZ;
        public bool useRect = true;
        public float yawDeg;
        public float toleranceDeg = 55f;
        public LaneBoundaryType leftBoundary = LaneBoundaryType.Solid;
        public LaneBoundaryType rightBoundary = LaneBoundaryType.Solid;
        public float alignedCostMultiplier = 0.85f;
        public float wrongWayCostMultiplier = 20f;
        public float centerlinePenalty = 0f;
        public bool blockWrongWay = true;
        public bool allowUTurn = false;

        public bool Contains(Vector3 world)
        {
            Vector2 p = new Vector2(world.x, world.z);
            if (useRect)
            {
                return p.x >= worldRectXZ.xMin && p.x <= worldRectXZ.xMax
                    && p.y >= worldRectXZ.yMin && p.y <= worldRectXZ.yMax;
            }

            return TrafficGeometry.PointInPolygon(p, polygonXZ);
        }
    }

    [Serializable]
    public sealed class TurnConnector
    {
        public string id = "connector";
        public string fromLaneId = "";
        public string toLaneId = "";
        public TrafficTurnKind turn = TrafficTurnKind.Any;
        public Rect worldRectXZ;
        public bool useRect = true;
        public Vector2[] polygonXZ;
        public bool allowed = true;
        public float costMultiplier = 1f;

        public bool Contains(Vector3 world)
        {
            Vector2 p = new Vector2(world.x, world.z);
            if (useRect)
            {
                return p.x >= worldRectXZ.xMin && p.x <= worldRectXZ.xMax
                    && p.y >= worldRectXZ.yMin && p.y <= worldRectXZ.yMax;
            }

            return TrafficGeometry.PointInPolygon(p, polygonXZ);
        }
    }

    [Serializable]
    public sealed class RuleRegion
    {
        public string id = "region";
        public Rect worldRectXZ;
        public bool useRect = true;
        public Vector2[] polygonXZ;
        public bool noUTurn;
        public bool blockLaneChange;
        public float costMultiplier = 1f;

        public bool Contains(Vector3 world)
        {
            Vector2 p = new Vector2(world.x, world.z);
            if (useRect)
            {
                return p.x >= worldRectXZ.xMin && p.x <= worldRectXZ.xMax
                    && p.y >= worldRectXZ.yMin && p.y <= worldRectXZ.yMax;
            }

            return TrafficGeometry.PointInPolygon(p, polygonXZ);
        }
    }

    public sealed class TrafficSemanticRuleProvider : NavigationRuleProviderBase
    {
        [Tooltip("Optional JSON text asset. It can use world coordinates or normalized image coordinates.")]
        public TextAsset semanticJson;

        [Tooltip("Used when semantic JSON contains normalized coordinates.")]
        public int sourceImageWidth = 1024;

        [Tooltip("Used when semantic JSON contains normalized coordinates.")]
        public int sourceImageHeight = 1024;

        [Tooltip("Bottom-left world origin of the map.")]
        public Vector2 worldOriginXZ = Vector2.zero;

        [Tooltip("Fallback map resolution when JSON does not define meters_per_pixel.")]
        public float metersPerPixel = 0.1f;
        public float metersPerPixelZ = 0f;

        public bool loadJsonOnAwake = true;
        public bool blockUnknownLaneChanges = false;
        public float unknownLaneChangePenalty = 3f;
        public float solidLineChangePenalty = 30f;
        public float dashedLineChangePenalty = 2f;
        public TrafficSemanticMap semanticMap = new TrafficSemanticMap();

        private readonly Dictionary<string, SemanticLane> laneById = new Dictionary<string, SemanticLane>();

        private void Awake()
        {
            if (loadJsonOnAwake && semanticJson != null)
                LoadFromJson(semanticJson.text);
        }

        public void LoadFromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return;

            TrafficSemanticConfig config = JsonUtility.FromJson<TrafficSemanticConfig>(json);
            if (config == null)
                return;

            ApplyConfig(config);
        }

        public void ApplyConfig(TrafficSemanticConfig config)
        {
            if (config == null)
                return;

            float resolution = metersPerPixel > 0f ? metersPerPixel : config.meters_per_pixel;
            float resolutionZ = metersPerPixelZ > 0f ? metersPerPixelZ : resolution;
            semanticMap = new TrafficSemanticMap();
            semanticMap.metersPerPixel = resolution;

            if (config.buildings != null)
            {
                for (int i = 0; i < config.buildings.Length; i++)
                {
                    BuildingConfig b = config.buildings[i];
                    Vector3 goalPos;
                    if (!TryConfigPointToWorld(b.entrance_world, b.entrance_norm, b.entrance_px, resolution, resolutionZ, out goalPos))
                        continue;

                    semanticMap.goals.Add(new NamedGoal
                    {
                        name = b.name,
                        worldPosition = goalPos,
                        yawDeg = b.yaw_deg,
                        requireYaw = b.require_yaw
                    });
                }
            }

            if (config.lanes != null)
            {
                for (int i = 0; i < config.lanes.Length; i++)
                {
                    LaneConfig laneConfig = config.lanes[i];
                    SemanticLane lane = new SemanticLane
                    {
                        id = laneConfig.id,
                        group = laneConfig.group,
                        yawDeg = laneConfig.yaw_deg,
                        toleranceDeg = laneConfig.tolerance_deg > 0f ? laneConfig.tolerance_deg : 55f,
                        leftBoundary = ParseBoundary(laneConfig.left_boundary, LaneBoundaryType.Solid),
                        rightBoundary = ParseBoundary(laneConfig.right_boundary, LaneBoundaryType.Solid),
                        alignedCostMultiplier = laneConfig.aligned_cost_multiplier > 0f ? laneConfig.aligned_cost_multiplier : 0.85f,
                        wrongWayCostMultiplier = laneConfig.wrong_way_cost_multiplier > 0f ? laneConfig.wrong_way_cost_multiplier : 20f,
                        centerlinePenalty = laneConfig.centerline_penalty,
                        blockWrongWay = !laneConfig.allow_wrong_way,
                        allowUTurn = laneConfig.allow_uturn
                    };

                    if (TryConfigRectToWorld(laneConfig.rect_world, laneConfig.rect_norm, laneConfig.rect_px, resolution, resolutionZ, out lane.worldRectXZ))
                    {
                        lane.useRect = true;
                    }
                    else
                    {
                        lane.useRect = false;
                        lane.polygonXZ = ConfigPolygonToWorld(laneConfig.polygon_world, laneConfig.polygon_norm, laneConfig.polygon_px, resolution, resolutionZ);
                    }

                    if (!string.IsNullOrEmpty(lane.id) && (lane.useRect || lane.polygonXZ != null && lane.polygonXZ.Length >= 3))
                        semanticMap.lanes.Add(lane);
                }
            }

            if (config.turn_connectors != null)
            {
                for (int i = 0; i < config.turn_connectors.Length; i++)
                {
                    TurnConnectorConfig c = config.turn_connectors[i];
                    TurnConnector connector = new TurnConnector
                    {
                        id = c.id,
                        fromLaneId = c.from_lane,
                        toLaneId = c.to_lane,
                        turn = ParseTurn(c.type),
                        allowed = !c.disallowed,
                        costMultiplier = c.cost_multiplier > 0f ? c.cost_multiplier : 1f
                    };

                    if (TryConfigRectToWorld(c.rect_world, c.rect_norm, c.rect_px, resolution, resolutionZ, out connector.worldRectXZ))
                    {
                        connector.useRect = true;
                    }
                    else
                    {
                        connector.useRect = false;
                        connector.polygonXZ = ConfigPolygonToWorld(c.polygon_world, c.polygon_norm, c.polygon_px, resolution, resolutionZ);
                    }

                    if (connector.useRect || connector.polygonXZ != null && connector.polygonXZ.Length >= 3)
                        semanticMap.turnConnectors.Add(connector);
                }
            }

            if (config.regions != null)
            {
                for (int i = 0; i < config.regions.Length; i++)
                {
                    RegionConfig r = config.regions[i];
                    RuleRegion region = new RuleRegion
                    {
                        id = r.id,
                        noUTurn = r.no_uturn,
                        blockLaneChange = r.block_lane_change,
                        costMultiplier = r.cost_multiplier > 0f ? r.cost_multiplier : 1f
                    };

                    if (TryConfigRectToWorld(r.rect_world, r.rect_norm, r.rect_px, resolution, resolutionZ, out region.worldRectXZ))
                    {
                        region.useRect = true;
                    }
                    else
                    {
                        region.useRect = false;
                        region.polygonXZ = ConfigPolygonToWorld(r.polygon_world, r.polygon_norm, r.polygon_px, resolution, resolutionZ);
                    }

                    if (region.useRect || region.polygonXZ != null && region.polygonXZ.Length >= 3)
                        semanticMap.regions.Add(region);
                }
            }

            RebuildLaneIndex();
        }

        public List<NamedGoal> GetConfiguredGoals()
        {
            return semanticMap != null ? semanticMap.goals : null;
        }

        public string DescribeWorldPosition(GridCostmap map, Vector3 world, float yawDeg)
        {
            string laneId = "none";
            string connectorId = "none";
            string regionId = "none";

            SemanticLane lane = FindLane(world);
            if (lane != null)
                laneId = lane.id;

            TrafficTurnKind turnKind = TrafficTurnKind.Any;
            TurnConnector connector = FindConnector(world, lane != null ? lane.id : "", "", turnKind);
            if (connector != null)
                connectorId = connector.id;

            if (semanticMap != null && semanticMap.regions != null)
            {
                for (int i = 0; i < semanticMap.regions.Count; i++)
                {
                    RuleRegion region = semanticMap.regions[i];
                    if (region != null && region.Contains(world))
                    {
                        regionId = region.id;
                        break;
                    }
                }
            }

            string cell = "outside";
            string cost = "n/a";
            if (map != null && map.WorldToCell(world, out int x, out int y))
            {
                cell = x + "," + y;
                cost = map.GetCost(x, y).ToString();
            }

            return "world=(" + world.x.ToString("F2") + "," + world.z.ToString("F2") + ")"
                + " cell=(" + cell + ")"
                + " cost=" + cost
                + " yaw=" + yawDeg.ToString("F1")
                + " lane=" + laneId
                + " connector=" + connectorId
                + " region=" + regionId;
        }

        public bool IsOnSemanticRoad(Vector3 world)
        {
            if (FindLane(world) != null)
                return true;

            if (semanticMap == null || semanticMap.turnConnectors == null)
                return false;

            for (int i = 0; i < semanticMap.turnConnectors.Count; i++)
            {
                TurnConnector connector = semanticMap.turnConnectors[i];
                if (connector != null && connector.Contains(world))
                    return true;
            }

            return false;
        }

        public override bool AllowsPose(GridCostmap map, int x, int y, float yawDeg)
        {
            SemanticLane lane = FindLane(map.CellToWorld(x, y));
            if (lane == null)
                return true;

            float diff = Mathf.Abs(Mathf.DeltaAngle(yawDeg, lane.yawDeg));
            if (diff <= lane.toleranceDeg)
                return true;

            return !lane.blockWrongWay;
        }

        public override float TraversalMultiplier(GridCostmap map, int x, int y, float yawDeg)
        {
            Vector3 world = map.CellToWorld(x, y);
            float multiplier = 1f;
            SemanticLane lane = FindLane(world);
            if (lane != null)
            {
                float diff = Mathf.Abs(Mathf.DeltaAngle(yawDeg, lane.yawDeg));
                multiplier *= diff <= lane.toleranceDeg
                    ? Mathf.Max(0.01f, lane.alignedCostMultiplier)
                    : Mathf.Max(1f, lane.wrongWayCostMultiplier);

                if (lane.centerlinePenalty > 0f && lane.useRect)
                {
                    bool horizontal = Mathf.Abs(Mathf.Sin(lane.yawDeg * Mathf.Deg2Rad)) >= Mathf.Abs(Mathf.Cos(lane.yawDeg * Mathf.Deg2Rad));
                    float offset;
                    if (horizontal)
                    {
                        float halfWidth = Mathf.Max(0.1f, lane.worldRectXZ.height * 0.5f);
                        offset = Mathf.Abs(world.z - lane.worldRectXZ.center.y) / halfWidth;
                    }
                    else
                    {
                        float halfWidth = Mathf.Max(0.1f, lane.worldRectXZ.width * 0.5f);
                        offset = Mathf.Abs(world.x - lane.worldRectXZ.center.x) / halfWidth;
                    }

                    multiplier *= 1f + lane.centerlinePenalty * Mathf.Pow(Mathf.Clamp01(offset), 2f);
                }
            }

            if (semanticMap != null)
            {
                for (int i = 0; i < semanticMap.regions.Count; i++)
                {
                    RuleRegion region = semanticMap.regions[i];
                    if (region != null && region.Contains(world))
                        multiplier *= Mathf.Max(0.01f, region.costMultiplier);
                }

                for (int i = 0; i < semanticMap.turnConnectors.Count; i++)
                {
                    TurnConnector connector = semanticMap.turnConnectors[i];
                    if (connector != null && connector.Contains(world))
                        multiplier *= Mathf.Max(0.01f, connector.costMultiplier);
                }
            }

            return multiplier;
        }

        public override bool AllowsTurn(Vector3 worldPosition, float fromYawDeg, float toYawDeg)
        {
            float turnAngle = Mathf.Abs(Mathf.DeltaAngle(fromYawDeg, toYawDeg));
            if (turnAngle < 135f)
                return true;

            SemanticLane lane = FindLane(worldPosition);
            if (lane != null && lane.allowUTurn)
                return true;

            TurnConnector uturnConnector = FindConnector(worldPosition, lane != null ? lane.id : "", "", TrafficTurnKind.UTurn);
            if (uturnConnector != null)
                return uturnConnector.allowed;

            if (semanticMap != null)
            {
                for (int i = 0; i < semanticMap.regions.Count; i++)
                {
                    RuleRegion region = semanticMap.regions[i];
                    if (region != null && region.noUTurn && region.Contains(worldPosition))
                        return false;
                }
            }

            return false;
        }

        public override bool AllowsTransition(GridCostmap map, NavPose fromPose, NavPose toPose)
        {
            SemanticLane fromLane = FindLane(fromPose.position);
            SemanticLane toLane = FindLane(toPose.position);
            TrafficTurnKind turnKind = ClassifyTurn(fromPose.yawDeg, toPose.yawDeg);

            TurnConnector turnConnector = FindConnectorBetween(fromPose.position, toPose.position,
                fromLane != null ? fromLane.id : "",
                toLane != null ? toLane.id : "",
                turnKind);

            if (turnKind == TrafficTurnKind.UTurn)
            {
                if (turnConnector != null)
                    return turnConnector.allowed;
                if (!AllowsTurn(fromPose.position, fromPose.yawDeg, toPose.yawDeg))
                    return false;
            }

            if (fromLane != null && toLane != null && fromLane.id != toLane.id)
            {
                if (turnConnector != null)
                    return turnConnector.allowed;

                if (!LaneChangeAllowed(fromLane, toLane, fromPose, toPose))
                    return false;

                if (blockUnknownLaneChanges)
                    return false;
            }

            if (turnKind != TrafficTurnKind.Straight)
            {
                if (turnConnector != null)
                    return turnConnector.allowed;

                if (fromLane != null && toLane != null && fromLane.id != toLane.id)
                    return false;
            }

            if (semanticMap != null)
            {
                Vector3 mid = (fromPose.position + toPose.position) * 0.5f;
                for (int i = 0; i < semanticMap.regions.Count; i++)
                {
                    RuleRegion region = semanticMap.regions[i];
                    if (region == null || !region.Contains(mid))
                        continue;
                    if (region.blockLaneChange && fromLane != null && toLane != null && fromLane.id != toLane.id)
                        return false;
                    if (region.noUTurn && turnKind == TrafficTurnKind.UTurn)
                        return false;
                }
            }

            return true;
        }

        public override float TransitionMultiplier(GridCostmap map, NavPose fromPose, NavPose toPose)
        {
            SemanticLane fromLane = FindLane(fromPose.position);
            SemanticLane toLane = FindLane(toPose.position);
            TrafficTurnKind turnKind = ClassifyTurn(fromPose.yawDeg, toPose.yawDeg);
            TurnConnector connector = FindConnectorBetween(fromPose.position, toPose.position,
                fromLane != null ? fromLane.id : "",
                toLane != null ? toLane.id : "",
                turnKind);

            if (connector != null)
                return Mathf.Max(0.01f, connector.costMultiplier);

            if (fromLane != null && toLane != null && fromLane.id != toLane.id)
            {
                LaneBoundaryType boundary = BoundaryCrossed(fromLane, fromPose, toPose);
                if (boundary == LaneBoundaryType.Dashed)
                    return Mathf.Max(0.01f, dashedLineChangePenalty);
                if (boundary == LaneBoundaryType.Solid)
                    return Mathf.Max(0.01f, solidLineChangePenalty);
                return Mathf.Max(0.01f, unknownLaneChangePenalty);
            }

            return 1f;
        }

        private void RebuildLaneIndex()
        {
            laneById.Clear();
            if (semanticMap == null || semanticMap.lanes == null)
                return;

            for (int i = 0; i < semanticMap.lanes.Count; i++)
            {
                SemanticLane lane = semanticMap.lanes[i];
                if (lane != null && !string.IsNullOrEmpty(lane.id))
                    laneById[lane.id] = lane;
            }
        }

        private SemanticLane FindLane(Vector3 world)
        {
            if (semanticMap == null || semanticMap.lanes == null)
                return null;

            for (int i = 0; i < semanticMap.lanes.Count; i++)
            {
                SemanticLane lane = semanticMap.lanes[i];
                if (lane != null && lane.Contains(world))
                    return lane;
            }

            return null;
        }

        private TurnConnector FindConnectorBetween(Vector3 fromWorld, Vector3 toWorld, string fromLaneId, string toLaneId, TrafficTurnKind turn)
        {
            Vector3 mid = (fromWorld + toWorld) * 0.5f;
            TurnConnector direct = FindConnector(mid, fromLaneId, toLaneId, turn);
            if (direct != null)
                return direct;

            direct = FindConnector(fromWorld, fromLaneId, toLaneId, turn);
            if (direct != null)
                return direct;

            direct = FindConnector(toWorld, fromLaneId, toLaneId, turn);
            if (direct != null)
                return direct;

            return null;
        }

        private TurnConnector FindConnector(Vector3 world, string fromLaneId, string toLaneId, TrafficTurnKind turn)
        {
            if (semanticMap == null || semanticMap.turnConnectors == null)
                return null;

            for (int i = 0; i < semanticMap.turnConnectors.Count; i++)
            {
                TurnConnector c = semanticMap.turnConnectors[i];
                if (c == null)
                    continue;

                bool fromMatches = string.IsNullOrEmpty(c.fromLaneId) || string.IsNullOrEmpty(fromLaneId) || c.fromLaneId == fromLaneId;
                bool toMatches = string.IsNullOrEmpty(c.toLaneId) || string.IsNullOrEmpty(toLaneId) || c.toLaneId == toLaneId;
                bool turnMatches = c.turn == TrafficTurnKind.Any || turn == TrafficTurnKind.Any || c.turn == turn;
                if (!fromMatches || !toMatches || !turnMatches)
                    continue;

                if (c.Contains(world))
                    return c;
            }

            return null;
        }

        private bool LaneChangeAllowed(SemanticLane fromLane, SemanticLane toLane, NavPose fromPose, NavPose toPose)
        {
            if (fromLane == null || toLane == null)
                return true;

            if (!string.IsNullOrEmpty(fromLane.group) && fromLane.group != toLane.group)
                return false;

            Vector3 forward = Quaternion.Euler(0f, fromLane.yawDeg, 0f) * Vector3.forward;
            Vector3 delta = toPose.position - fromPose.position;
            LaneBoundaryType boundary = BoundaryCrossed(fromLane, fromPose, toPose);
            if (boundary == LaneBoundaryType.Solid)
                return false;

            float forwardMove = Vector3.Dot(delta, forward);
            if (forwardMove < -0.1f)
                return false;

            return true;
        }

        private static LaneBoundaryType BoundaryCrossed(SemanticLane fromLane, NavPose fromPose, NavPose toPose)
        {
            if (fromLane == null)
                return LaneBoundaryType.None;

            Vector3 right = Quaternion.Euler(0f, fromLane.yawDeg, 0f) * Vector3.right;
            Vector3 delta = toPose.position - fromPose.position;
            float lateral = Vector3.Dot(delta, right);
            return lateral >= 0f ? fromLane.rightBoundary : fromLane.leftBoundary;
        }

        private bool TryConfigPointToWorld(float[] world, float[] norm, float[] px, float resolution, float resolutionZ, out Vector3 result)
        {
            result = Vector3.zero;
            if (world != null && world.Length >= 2)
            {
                result = new Vector3(world[0], world.Length >= 3 ? world[1] : 0f, world.Length >= 3 ? world[2] : world[1]);
                return true;
            }

            if (norm != null && norm.Length >= 2)
            {
                float x = worldOriginXZ.x + norm[0] * sourceImageWidth * resolution;
                float z = worldOriginXZ.y + (1f - norm[1]) * sourceImageHeight * resolutionZ;
                result = new Vector3(x, 0f, z);
                return true;
            }

            if (px != null && px.Length >= 2)
            {
                float x = worldOriginXZ.x + px[0] * resolution;
                float z = worldOriginXZ.y + (sourceImageHeight - 1f - px[1]) * resolutionZ;
                result = new Vector3(x, 0f, z);
                return true;
            }

            return false;
        }

        private bool TryConfigRectToWorld(float[] world, float[] norm, float[] px, float resolution, float resolutionZ, out Rect rect)
        {
            rect = new Rect();
            if (world != null && world.Length >= 4)
            {
                rect = Rect.MinMaxRect(
                    Mathf.Min(world[0], world[2]),
                    Mathf.Min(world[1], world[3]),
                    Mathf.Max(world[0], world[2]),
                    Mathf.Max(world[1], world[3]));
                return true;
            }

            if (norm != null && norm.Length >= 4)
            {
                float x0 = worldOriginXZ.x + norm[0] * sourceImageWidth * resolution;
                float z0 = worldOriginXZ.y + (1f - norm[1]) * sourceImageHeight * resolutionZ;
                float x1 = worldOriginXZ.x + norm[2] * sourceImageWidth * resolution;
                float z1 = worldOriginXZ.y + (1f - norm[3]) * sourceImageHeight * resolutionZ;
                rect = Rect.MinMaxRect(Mathf.Min(x0, x1), Mathf.Min(z0, z1), Mathf.Max(x0, x1), Mathf.Max(z0, z1));
                return true;
            }

            if (px != null && px.Length >= 4)
            {
                float x0 = worldOriginXZ.x + px[0] * resolution;
                float z0 = worldOriginXZ.y + (sourceImageHeight - 1f - px[1]) * resolutionZ;
                float x1 = worldOriginXZ.x + px[2] * resolution;
                float z1 = worldOriginXZ.y + (sourceImageHeight - 1f - px[3]) * resolutionZ;
                rect = Rect.MinMaxRect(Mathf.Min(x0, x1), Mathf.Min(z0, z1), Mathf.Max(x0, x1), Mathf.Max(z0, z1));
                return true;
            }

            return false;
        }

        private Vector2[] ConfigPolygonToWorld(float[][] world, float[][] norm, float[][] px, float resolution, float resolutionZ)
        {
            if (world != null && world.Length >= 3)
            {
                Vector2[] points = new Vector2[world.Length];
                for (int i = 0; i < world.Length; i++)
                    points[i] = new Vector2(world[i][0], world[i][1]);
                return points;
            }

            if (norm != null && norm.Length >= 3)
            {
                Vector2[] points = new Vector2[norm.Length];
                for (int i = 0; i < norm.Length; i++)
                {
                    float x = worldOriginXZ.x + norm[i][0] * sourceImageWidth * resolution;
                    float z = worldOriginXZ.y + (1f - norm[i][1]) * sourceImageHeight * resolutionZ;
                    points[i] = new Vector2(x, z);
                }
                return points;
            }

            if (px != null && px.Length >= 3)
            {
                Vector2[] points = new Vector2[px.Length];
                for (int i = 0; i < px.Length; i++)
                {
                    float x = worldOriginXZ.x + px[i][0] * resolution;
                    float z = worldOriginXZ.y + (sourceImageHeight - 1f - px[i][1]) * resolutionZ;
                    points[i] = new Vector2(x, z);
                }
                return points;
            }

            return null;
        }

        private static LaneBoundaryType ParseBoundary(string value, LaneBoundaryType fallback)
        {
            if (string.IsNullOrEmpty(value))
                return fallback;

            string v = value.Trim().ToLowerInvariant();
            if (v == "none")
                return LaneBoundaryType.None;
            if (v == "dashed" || v == "broken")
                return LaneBoundaryType.Dashed;
            if (v == "solid")
                return LaneBoundaryType.Solid;
            return fallback;
        }

        private static TrafficTurnKind ParseTurn(string value)
        {
            if (string.IsNullOrEmpty(value))
                return TrafficTurnKind.Any;

            string v = value.Trim().ToLowerInvariant();
            if (v == "straight")
                return TrafficTurnKind.Straight;
            if (v == "left")
                return TrafficTurnKind.Left;
            if (v == "right")
                return TrafficTurnKind.Right;
            if (v == "uturn" || v == "u_turn" || v == "u-turn")
                return TrafficTurnKind.UTurn;
            return TrafficTurnKind.Any;
        }

        private static TrafficTurnKind ClassifyTurn(float fromYawDeg, float toYawDeg)
        {
            float delta = Mathf.DeltaAngle(fromYawDeg, toYawDeg);
            float abs = Mathf.Abs(delta);
            if (abs > 135f)
                return TrafficTurnKind.UTurn;
            if (abs < 25f)
                return TrafficTurnKind.Straight;
            return delta < 0f ? TrafficTurnKind.Left : TrafficTurnKind.Right;
        }
    }

    internal static class TrafficGeometry
    {
        public static bool PointInPolygon(Vector2 p, Vector2[] polygon)
        {
            if (polygon == null || polygon.Length < 3)
                return false;

            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                Vector2 pi = polygon[i];
                Vector2 pj = polygon[j];
                float denom = pj.y - pi.y;
                if (Mathf.Abs(denom) < 0.000001f)
                    denom = denom < 0f ? -0.000001f : 0.000001f;

                bool intersect = ((pi.y > p.y) != (pj.y > p.y))
                    && (p.x < (pj.x - pi.x) * (p.y - pi.y) / denom + pi.x);
                if (intersect)
                    inside = !inside;
            }

            return inside;
        }
    }

    [Serializable]
    public sealed class TrafficSemanticConfig
    {
        public float meters_per_pixel;
        public BuildingConfig[] buildings;
        public LaneConfig[] lanes;
        public TurnConnectorConfig[] turn_connectors;
        public RegionConfig[] regions;
    }

    [Serializable]
    public sealed class BuildingConfig
    {
        public string name;
        public float[] entrance_world;
        public float[] entrance_norm;
        public float[] entrance_px;
        public float yaw_deg;
        public bool require_yaw;
    }

    [Serializable]
    public sealed class LaneConfig
    {
        public string id;
        public string group;
        public float[] rect_world;
        public float[] rect_norm;
        public float[] rect_px;
        public float[][] polygon_world;
        public float[][] polygon_norm;
        public float[][] polygon_px;
        public float yaw_deg;
        public float tolerance_deg;
        public string left_boundary;
        public string right_boundary;
        public float aligned_cost_multiplier;
        public float wrong_way_cost_multiplier;
        public float centerline_penalty;
        public bool allow_wrong_way;
        public bool allow_uturn;
    }

    [Serializable]
    public sealed class TurnConnectorConfig
    {
        public string id;
        public string from_lane;
        public string to_lane;
        public string type;
        public bool disallowed;
        public float cost_multiplier;
        public float[] rect_world;
        public float[] rect_norm;
        public float[] rect_px;
        public float[][] polygon_world;
        public float[][] polygon_norm;
        public float[][] polygon_px;
    }

    [Serializable]
    public sealed class RegionConfig
    {
        public string id;
        public float cost_multiplier;
        public bool no_uturn;
        public bool block_lane_change;
        public float[] rect_world;
        public float[] rect_norm;
        public float[] rect_px;
        public float[][] polygon_world;
        public float[][] polygon_norm;
        public float[][] polygon_px;
    }
}
