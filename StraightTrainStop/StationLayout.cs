using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Unity.Entities;
using Unity.Mathematics;

namespace StraightTrainStop
{

/// <summary>One end of a platform track: the nodes and asset edges out to the player's track.</summary>
internal sealed class TrackSide
{
    public readonly HashSet<Entity> Nodes = new HashSet<Entity>();
    public readonly HashSet<Entity> Edges = new HashSet<Entity>();
}

/// <summary>A platform track of the station, possibly made of several edges in a line.</summary>
internal sealed class TrackRow
{
    public readonly List<Entity> Edges = new List<Entity>();
    public float Lateral;
    public readonly TrackSide Left = new TrackSide();
    public readonly TrackSide Right = new TrackSide();
}

/// <summary>
/// The station split into platform tracks, each with a left and a right end, in the frame of the
/// station building, as seen from its street side: rows run from the track farthest from the
/// street to the nearest, and left and right are the viewer's. Only railway tracks count, not a metro beneath. Row indices are what the saved masks in
/// <see cref="StationJunctionLock"/> refer to, so the order has to come out the same every time
/// for an unchanged station.
/// </summary>
internal sealed class StationLayout
{
    private const float RowMergeDistance = 2f;
    private const float ParallelDot = 0.95f;
    private const int MaximumApproachDepth = 3;

    public readonly HashSet<Entity> Nets = new HashSet<Entity>();
    public readonly List<TrackRow> Rows = new List<TrackRow>();

    public static StationLayout Build(EntityManager entityManager, Entity station)
    {
        var layout = new StationLayout();
        StationNets.Collect(entityManager, station, layout.Nets);

        var platformEdges = new List<Entity>();
        foreach (Entity net in layout.Nets)
            if (IsPlatformEdge(entityManager, net))
                platformEdges.Add(net);

        if (platformEdges.Count == 0)
        {
            layout.AddWholeStationRow(entityManager);
            return layout;
        }

        float3 axis = FindAxis(entityManager, station, platformEdges);
        float3 side = new float3(-axis.z, 0f, axis.x);
        float3 origin = entityManager.HasComponent<Game.Objects.Transform>(station)
            ? entityManager.GetComponentData<Game.Objects.Transform>(station).m_Position
            : float3.zero;

        foreach (Entity edge in platformEdges)
        {
            Bezier4x3 curve = entityManager.GetComponentData<Curve>(edge).m_Bezier;
            float3 direction = Horizontal(curve.d - curve.a);
            if (math.abs(math.dot(math.normalizesafe(direction), axis)) < ParallelDot)
                continue;
            float lateral = math.dot(Horizontal(MathUtils.Position(curve, 0.5f) - origin), side);

            TrackRow? row = null;
            foreach (TrackRow candidate in layout.Rows)
            {
                if (math.abs(candidate.Lateral - lateral) > RowMergeDistance)
                    continue;
                row = candidate;
                break;
            }
            if (row == null)
            {
                row = new TrackRow { Lateral = lateral };
                layout.Rows.Add(row);
            }
            row.Edges.Add(edge);
        }

        if (layout.Rows.Count == 0)
        {
            layout.AddWholeStationRow(entityManager);
            return layout;
        }

        // Track 1 is the one farthest from the building's street side, as the tracks appear when
        // looking at the station from the street.
        float towardsStreet = 1f;
        if (entityManager.HasComponent<Game.Objects.Transform>(station))
        {
            quaternion rotation = entityManager.GetComponentData<Game.Objects.Transform>(station).m_Rotation;
            towardsStreet = math.dot(side, math.mul(rotation, new float3(0f, 0f, 1f))) >= 0f ? 1f : -1f;
        }
        layout.Rows.Sort((a, b) => (a.Lateral * towardsStreet).CompareTo(b.Lateral * towardsStreet));
        var platformSet = new HashSet<Entity>(platformEdges);
        foreach (TrackRow row in layout.Rows)
            layout.FillSides(entityManager, row, axis, origin, platformSet);
        return layout;
    }

    /// <summary>For each node, the edges whose lane changes may go given the enabled sides.</summary>
    public Dictionary<Entity, HashSet<Entity>> EnabledScope(ulong leftMask, ulong rightMask)
    {
        var scope = new Dictionary<Entity, HashSet<Entity>>();
        for (int i = 0; i < Rows.Count && i < 64; i++)
        {
            ulong bit = 1UL << i;
            if ((leftMask & bit) != 0)
                AddSide(scope, Rows[i].Left);
            if ((rightMask & bit) != 0)
                AddSide(scope, Rows[i].Right);
        }
        return scope;
    }

    public ulong AllRowsMask => Rows.Count >= 64 ? ulong.MaxValue : (1UL << Rows.Count) - 1;

    private static void AddSide(Dictionary<Entity, HashSet<Entity>> scope, TrackSide side)
    {
        foreach (Entity node in side.Nodes)
        {
            if (!scope.TryGetValue(node, out HashSet<Entity> edges))
                scope[node] = edges = new HashSet<Entity>();
            edges.UnionWith(side.Edges);
        }
    }

    /// <summary>Fallback when no platform edge is recognised: one row covering the whole station.</summary>
    private void AddWholeStationRow(EntityManager entityManager)
    {
        var row = new TrackRow();
        foreach (Entity net in Nets)
        {
            if (!entityManager.Exists(net))
                continue;
            if (entityManager.HasComponent<Node>(net))
            {
                row.Left.Nodes.Add(net);
                row.Right.Nodes.Add(net);
            }
            else if (entityManager.HasComponent<Edge>(net))
            {
                row.Edges.Add(net);
                row.Left.Edges.Add(net);
                row.Right.Edges.Add(net);
            }
        }
        Rows.Add(row);
    }

    private void FillSides(
        EntityManager entityManager,
        TrackRow row,
        float3 axis,
        float3 origin,
        HashSet<Entity> platformEdges)
    {
        // Every end node of the row's edges goes to the nearer end; the outermost two start the
        // walk out along the asset's approach track.
        var ends = new Dictionary<Entity, float>();
        foreach (Entity edge in row.Edges)
        {
            Edge nodes = entityManager.GetComponentData<Edge>(edge);
            ends[nodes.m_Start] = Project(entityManager, nodes.m_Start, axis, origin);
            ends[nodes.m_End] = Project(entityManager, nodes.m_End, axis, origin);
        }

        Entity leftmost = Entity.Null;
        Entity rightmost = Entity.Null;
        float min = float.MaxValue;
        float max = float.MinValue;
        foreach (KeyValuePair<Entity, float> end in ends)
        {
            if (end.Value < min) { min = end.Value; leftmost = end.Key; }
            if (end.Value > max) { max = end.Value; rightmost = end.Key; }
        }
        float middle = (min + max) * 0.5f;

        foreach (KeyValuePair<Entity, float> end in ends)
            (end.Value < middle ? row.Left : row.Right).Nodes.Add(end.Key);
        foreach (Entity edge in row.Edges)
        {
            row.Left.Edges.Add(edge);
            row.Right.Edges.Add(edge);
        }

        WalkApproach(entityManager, leftmost, row.Left, platformEdges);
        WalkApproach(entityManager, rightmost, row.Right, platformEdges);
    }

    /// <summary>Follows asset edges that are not platforms outwards from a platform end.</summary>
    private void WalkApproach(
        EntityManager entityManager,
        Entity start,
        TrackSide side,
        HashSet<Entity> platformEdges)
    {
        var frontier = new List<Entity> { start };
        for (int depth = 0; depth < MaximumApproachDepth && frontier.Count > 0; depth++)
        {
            var next = new List<Entity>();
            foreach (Entity node in frontier)
            {
                if (!entityManager.HasBuffer<ConnectedEdge>(node))
                    continue;
                var edges = new List<Entity>();
                foreach (ConnectedEdge connected in entityManager.GetBuffer<ConnectedEdge>(node, true))
                    edges.Add(connected.m_Edge);
                foreach (Entity edge in edges)
                {
                    if (!Nets.Contains(edge) || platformEdges.Contains(edge) || !side.Edges.Add(edge))
                        continue;
                    Edge ends = entityManager.GetComponentData<Edge>(edge);
                    Entity other = ends.m_Start == node ? ends.m_End : ends.m_Start;
                    if (side.Nodes.Add(other))
                        next.Add(other);
                }
            }
            frontier = next;
        }
    }

    private static bool IsPlatformEdge(EntityManager entityManager, Entity net)
    {
        if (!entityManager.Exists(net)
            || !entityManager.HasComponent<Edge>(net)
            || !entityManager.HasComponent<Curve>(net)
            || !entityManager.HasBuffer<SubLane>(net))
            return false;
        foreach (SubLane subLane in entityManager.GetBuffer<SubLane>(net, true))
        {
            Entity lane = subLane.m_SubLane;
            if (StationNets.IsTrainLane(entityManager, lane)
                && (entityManager.GetComponentData<TrackLane>(lane).m_Flags & TrackLaneFlags.Station) != 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Direction of the platforms, pointing from the left end to the right end as seen by someone
    /// standing at the building's front (its street side) and facing it. That viewer looks along
    /// the building's -forward, so their right is the building's -right.
    /// </summary>
    private static float3 FindAxis(EntityManager entityManager, Entity station, List<Entity> platformEdges)
    {
        float3 axis = new float3(1f, 0f, 0f);
        float longest = 0f;
        foreach (Entity edge in platformEdges)
        {
            Bezier4x3 curve = entityManager.GetComponentData<Curve>(edge).m_Bezier;
            float3 direction = Horizontal(curve.d - curve.a);
            float length = math.length(direction);
            if (length <= longest)
                continue;
            longest = length;
            axis = direction / length;
        }

        if (entityManager.HasComponent<Game.Objects.Transform>(station))
        {
            quaternion rotation = entityManager.GetComponentData<Game.Objects.Transform>(station).m_Rotation;
            float3 right = math.mul(rotation, new float3(1f, 0f, 0f));
            float3 forward = math.mul(rotation, new float3(0f, 0f, 1f));
            float reference = math.abs(math.dot(axis, right)) >= 0.5f
                ? -math.dot(axis, right)
                : math.dot(axis, forward);
            if (reference < 0f)
                axis = -axis;
        }
        else if (axis.x < 0f || (axis.x == 0f && axis.z < 0f))
        {
            axis = -axis;
        }
        return axis;
    }

    private static float Project(EntityManager entityManager, Entity node, float3 axis, float3 origin)
    {
        if (!entityManager.HasComponent<Node>(node))
            return 0f;
        return math.dot(Horizontal(entityManager.GetComponentData<Node>(node).m_Position - origin), axis);
    }

    private static float3 Horizontal(float3 value)
    {
        value.y = 0f;
        return value;
    }
}

}
