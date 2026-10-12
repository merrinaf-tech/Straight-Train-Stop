using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace StraightTrainStop
{

/// <summary>One end of a platform track: the nodes and asset edges out to the player's track.</summary>
internal sealed class TrackSide
{
    public readonly HashSet<Entity> Nodes = new HashSet<Entity>();

    /// <summary>Each edge with its distance in edges from the platform, which itself is 0.</summary>
    public readonly Dictionary<Entity, int> Edges = new Dictionary<Entity, int>();

    /// <summary>True where the track stops at the platform, as at a terminal's buffer stops.</summary>
    public bool DeadEnd;
}

/// <summary>A platform track of the station, possibly made of several edges in a line.</summary>
internal sealed class TrackRow
{
    public readonly List<Entity> Edges = new List<Entity>();
    public float Lateral;
    public readonly TrackSide Left = new TrackSide();
    public readonly TrackSide Right = new TrackSide();

    public TrackSide Side(int side) => side == 0 ? Left : Right;

    /// <summary>Train stops of the station placed along this track, found by position.</summary>
    public int StopCount;

    /// <summary>True for a metro track, false for a railway one.</summary>
    public bool Metro;

    /// <summary>True when this track's platforms point at the street: its ends are far and street.</summary>
    public bool FacesStreet;
}

/// <summary>
/// The station split into platform tracks, each with two ends, in the frame of the station
/// building as seen from its street side. Railway tracks come first, then metro tracks, each kind
/// laid out on its own: a metro beneath a station can run another way, and putting the metro rows
/// after the railway ones keeps the saved railway masks on their tracks.
///
/// Platforms parallel to the street: rows run from the track farthest from the street to the
/// nearest, and the ends are the viewer's left and right. Platforms pointing at the street, as in
/// a terminal: rows run from the viewer's left to right, and the ends are the far one and the
/// street one. Row indices are what the saved masks in <see cref="StationJunctionLock"/> refer
/// to, so the order has to come out the same every time for an unchanged station.
/// </summary>
internal sealed class StationLayout
{
    private const float RowMergeDistance = 2f;
    private const float StopTrackDistance = 3f;
    private const float ParallelDot = 0.95f;
    private const int MaximumApproachDepth = 3;

    public readonly HashSet<Entity> Nets = new HashSet<Entity>();
    public readonly List<TrackRow> Rows = new List<TrackRow>();

    /// <summary>
    /// Asset tracks that end in a buffer stop without being platforms: an integrated depot or
    /// yard. A lane into one of them is a real junction, never a lane change, and is kept.
    /// </summary>
    public readonly HashSet<Entity> Sidings = new HashSet<Entity>();

    /// <summary>False for a station without railway or metro track, such as a bus station.</summary>
    public bool HasRailway;

    /// <summary>How many of the station's train stops are attached to each edge.</summary>
    private readonly Dictionary<Entity, int> _stopCounts = new Dictionary<Entity, int>();
    private int _directionGroups;

    /// <summary>
    /// Builds the layout. A platform is an edge of the station that one of its passenger train
    /// stops stands on; depot, yard and freight tracks have none. Only when the station has no
    /// such stop at all does it fall back to edges whose track lanes carry the Station flag.
    ///
    /// A stop's lanes can reach past the platform onto the approach track, which then looks like a
    /// platform of its own. So where some stop edges carry the Station flag, the rest are dropped.
    /// </summary>
    public static StationLayout Build(EntityManager entityManager, Entity station, NativeArray<Entity> passengerStops)
    {
        var layout = new StationLayout();
        StationNets.Collect(entityManager, station, layout.Nets);
        layout.HasRailway = HasRailwayEdge(entityManager, layout.Nets);
        if (!layout.HasRailway)
            return layout;

        foreach (KeyValuePair<Entity, int> pair in StopEdges(entityManager, station, passengerStops))
            layout._stopCounts[pair.Key] = pair.Value;

        float3 origin = entityManager.HasComponent<Game.Objects.Transform>(station)
            ? entityManager.GetComponentData<Game.Objects.Transform>(station).m_Position
            : float3.zero;

        // Rows per kind first, then the sidings over every platform, then each row's two ends,
        // which need the sidings to tell a buffer stop from a way out.
        var kinds = new List<(List<TrackRow> Rows, float3 Axis)>();
        var allPlatforms = new HashSet<Entity>();
        foreach (bool metro in new[] { false, true })
        {
            // Platforms pointing another way than the longest one, as on the outer ring of a round
            // station, form a group of their own, laid out on its own axis and listed after it.
            TrackTypes type = metro ? TrackTypes.Subway : TrackTypes.Train;
            List<Entity> remaining = layout.PlatformEdges(entityManager, type);
            while (remaining.Count > 0)
            {
                List<TrackRow> rows = layout.BuildRows(
                    entityManager, station, passengerStops, remaining, origin, metro, out float3 axis, out List<Entity> otherWay);
                if (rows.Count > 0)
                {
                    kinds.Add((rows, axis));
                    layout._directionGroups++;
                    foreach (TrackRow row in rows)
                        allPlatforms.UnionWith(row.Edges);
                }
                if (otherWay.Count >= remaining.Count)
                    break;
                remaining = otherWay;
            }
        }

        if (kinds.Count == 0)
        {
            layout.AddWholeStationRow(entityManager);
            return layout;
        }

        layout.FindSidings(entityManager, allPlatforms);
        foreach ((List<TrackRow> rows, float3 axis) in kinds)
        {
            foreach (TrackRow row in rows)
                layout.FillSides(entityManager, row, axis, origin, allPlatforms);
            layout.Rows.AddRange(rows);
        }
        return layout;
    }

    /// <summary>
    /// The station's platform edges of one track type: edges a stop is attached to, kept to
    /// those with the Station flag where some have it (a stop's lanes can reach past the platform
    /// onto the approach track), or failing any stop, every edge with the Station flag.
    /// </summary>
    private List<Entity> PlatformEdges(EntityManager entityManager, TrackTypes type)
    {
        var platformEdges = new List<Entity>();
        foreach (Entity edge in _stopCounts.Keys)
            if (Nets.Contains(edge) && entityManager.HasComponent<Curve>(edge) && HasRailTrack(entityManager, edge, type))
                platformEdges.Add(edge);
        if (platformEdges.Exists(edge => IsPlatformEdge(entityManager, edge, type)))
            platformEdges.RemoveAll(edge => !IsPlatformEdge(entityManager, edge, type));
        if (platformEdges.Count == 0)
            foreach (Entity net in Nets)
                if (IsPlatformEdge(entityManager, net, type))
                    platformEdges.Add(net);
        return platformEdges;
    }

    /// <summary>
    /// Groups one kind's platform edges into rows across the platforms and orders them: parallel
    /// to the street, track 1 is the farthest from it; pointing at the street, track 1 is the
    /// viewer's leftmost. The viewer faces the building, so their right is its -right.
    /// </summary>
    private List<TrackRow> BuildRows(
        EntityManager entityManager,
        Entity station,
        NativeArray<Entity> passengerStops,
        List<Entity> platformEdges,
        float3 origin,
        bool metro,
        out float3 axis,
        out List<Entity> otherWay)
    {
        axis = FindAxis(entityManager, station, platformEdges, out bool facesStreet);
        float3 side = new float3(-axis.z, 0f, axis.x);
        var rows = new List<TrackRow>();
        otherWay = new List<Entity>();

        foreach (Entity edge in platformEdges)
        {
            Bezier4x3 curve = entityManager.GetComponentData<Curve>(edge).m_Bezier;
            float3 direction = Horizontal(curve.d - curve.a);
            if (math.abs(math.dot(math.normalizesafe(direction), axis)) < ParallelDot)
            {
                otherWay.Add(edge);
                continue;
            }
            float lateral = math.dot(Horizontal(MathUtils.Position(curve, 0.5f) - origin), side);

            TrackRow? row = null;
            foreach (TrackRow candidate in rows)
            {
                if (math.abs(candidate.Lateral - lateral) > RowMergeDistance)
                    continue;
                row = candidate;
                break;
            }
            if (row == null)
            {
                row = new TrackRow { Lateral = lateral, Metro = metro, FacesStreet = facesStreet };
                rows.Add(row);
            }
            row.Edges.Add(edge);
        }

        // Building stops carry no RouteLane, so when no stop named its edge, match the stops to
        // the tracks by where they stand. A track no stop stands on is a depot or yard track.
        if (_stopCounts.Count == 0)
            KeepRowsWithStops(entityManager, station, passengerStops, axis, side, origin, platformEdges, rows);

        float order = 1f;
        if (entityManager.HasComponent<Game.Objects.Transform>(station))
        {
            quaternion rotation = entityManager.GetComponentData<Game.Objects.Transform>(station).m_Rotation;
            float3 reference = facesStreet
                ? -math.mul(rotation, new float3(1f, 0f, 0f))
                : math.mul(rotation, new float3(0f, 0f, 1f));
            order = math.dot(side, reference) >= 0f ? 1f : -1f;
        }
        rows.Sort((a, b) => (a.Lateral * order).CompareTo(b.Lateral * order));
        return rows;
    }

    /// <summary>The rows whose given side (0 left, 1 right) leads somewhere and can be switched.</summary>
    public ulong OpenMask(int side)
    {
        ulong mask = 0;
        for (int i = 0; i < Rows.Count && i < 64; i++)
            if (!Rows[i].Side(side).DeadEnd)
                mask |= 1UL << i;
        return mask;
    }

    /// <summary>One line for the log: each row's edges, sideways position and open ends.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        foreach (TrackRow row in Rows)
            parts.Add($"{(row.Metro ? "metro " : "")}{row.Edges.Count}e@{row.Lateral:F1}m {RowStops(row)}st{(row.FacesStreet ? " street" : "")}{(row.Left.DeadEnd ? " L-dead" : "")}{(row.Right.DeadEnd ? " R-dead" : "")}");
        return $"{Rows.Count} track(s) in {_directionGroups} direction group(s), {Sidings.Count} siding edge(s): " + string.Join("; ", parts);
    }

    private int RowStops(TrackRow row)
    {
        int stops = row.StopCount;
        foreach (Entity edge in row.Edges)
            if (_stopCounts.TryGetValue(edge, out int count))
                stops += count;
        return stops;
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
                row.Left.Edges[net] = 0;
                row.Right.Edges[net] = 0;
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
            row.Left.Edges[edge] = 0;
            row.Right.Edges[edge] = 0;
        }

        var rowEdges = new HashSet<Entity>(row.Edges);
        row.Left.DeadEnd = IsDeadEnd(entityManager, leftmost, rowEdges);
        row.Right.DeadEnd = IsDeadEnd(entityManager, rightmost, rowEdges);
        WalkApproach(entityManager, leftmost, row.Left, platformEdges);
        WalkApproach(entityManager, rightmost, row.Right, platformEdges);
    }

    /// <summary>True when no track leaves the platform end other than the platform and sidings.</summary>
    private bool IsDeadEnd(EntityManager entityManager, Entity end, HashSet<Entity> rowEdges)
    {
        foreach (Entity edge in TrackEdgesAt(entityManager, end))
            if (!rowEdges.Contains(edge) && !Sidings.Contains(edge))
                return false;
        return true;
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
                    if (!Nets.Contains(edge) || platformEdges.Contains(edge) || side.Edges.ContainsKey(edge))
                        continue;
                    side.Edges[edge] = depth + 1;
                    Edge ends = entityManager.GetComponentData<Edge>(edge);
                    Entity other = ends.m_Start == node ? ends.m_End : ends.m_Start;
                    if (side.Nodes.Add(other))
                        next.Add(other);
                }
            }
            frontier = next;
        }
    }

    /// <summary>
    /// Walks back from every dead end of the station's track through plain two-way nodes, marking
    /// the edges on the way, until a switch, a platform or track that is not the station's.
    /// </summary>
    private void FindSidings(EntityManager entityManager, HashSet<Entity> platformEdges)
    {
        foreach (Entity net in Nets)
        {
            if (!entityManager.Exists(net) || !entityManager.HasComponent<Node>(net))
                continue;
            List<Entity> tracks = TrackEdgesAt(entityManager, net);
            if (tracks.Count != 1)
                continue;

            Entity node = net;
            Entity edge = tracks[0];
            for (int guard = 0; guard < 32; guard++)
            {
                if (!Nets.Contains(edge) || platformEdges.Contains(edge) || !Sidings.Add(edge))
                    break;
                Edge ends = entityManager.GetComponentData<Edge>(edge);
                node = ends.m_Start == node ? ends.m_End : ends.m_Start;
                List<Entity> next = TrackEdgesAt(entityManager, node);
                if (next.Count != 2)
                    break;
                edge = next[0] == edge ? next[1] : next[0];
            }
        }
    }

    private static List<Entity> TrackEdgesAt(EntityManager entityManager, Entity node)
    {
        var edges = new List<Entity>();
        if (!entityManager.HasBuffer<ConnectedEdge>(node))
            return edges;
        var connected = new List<Entity>();
        foreach (ConnectedEdge item in entityManager.GetBuffer<ConnectedEdge>(node, true))
            connected.Add(item.m_Edge);
        foreach (Entity edge in connected)
            if (HasRailTrack(entityManager, edge))
                edges.Add(edge);
        return edges;
    }

    /// <summary>True when the station or one of its upgrades has railway or metro track.</summary>
    public static bool IsRailwayStation(EntityManager entityManager, Entity station)
    {
        var nets = new HashSet<Entity>();
        StationNets.Collect(entityManager, station, nets);
        return HasRailwayEdge(entityManager, nets);
    }

    private static bool HasRailwayEdge(EntityManager entityManager, HashSet<Entity> nets)
    {
        foreach (Entity net in nets)
            if (entityManager.HasComponent<Edge>(net) && HasRailTrack(entityManager, net))
                return true;
        return false;
    }

    public static bool HasRailTrack(EntityManager entityManager, Entity edge, TrackTypes types = StationNets.RailTypes)
    {
        if (!entityManager.Exists(edge)
            || entityManager.HasComponent<Deleted>(edge)
            || !entityManager.HasBuffer<SubLane>(edge))
            return false;
        foreach (SubLane subLane in entityManager.GetBuffer<SubLane>(edge, true))
            if (StationNets.IsRailLane(entityManager, subLane.m_SubLane, types))
                return true;
        return false;
    }

    /// <summary>
    /// Assigns every train stop of the station to the track it stands on: within
    /// <see cref="StopTrackDistance"/> sideways of the track and within its length. If any track
    /// gets a stop, the tracks without one are dropped, together with their edges.
    /// </summary>
    private void KeepRowsWithStops(
        EntityManager entityManager,
        Entity station,
        NativeArray<Entity> stops,
        float3 axis,
        float3 side,
        float3 origin,
        List<Entity> platformEdges,
        List<TrackRow> rows)
    {
        var spans = new Dictionary<TrackRow, float2>();
        foreach (TrackRow row in rows)
        {
            float2 span = new float2(float.MaxValue, float.MinValue);
            foreach (Entity edge in row.Edges)
            {
                Bezier4x3 curve = entityManager.GetComponentData<Curve>(edge).m_Bezier;
                float a = math.dot(Horizontal(curve.a - origin), axis);
                float d = math.dot(Horizontal(curve.d - origin), axis);
                span = new float2(math.min(span.x, math.min(a, d)), math.max(span.y, math.max(a, d)));
            }
            spans[row] = span;
        }

        foreach (Entity stop in stops)
        {
            if (!OwnedBy(entityManager, stop, station) || !entityManager.HasComponent<Game.Objects.Transform>(stop))
                continue;
            float3 position = entityManager.GetComponentData<Game.Objects.Transform>(stop).m_Position;
            float lateral = math.dot(Horizontal(position - origin), side);
            float along = math.dot(Horizontal(position - origin), axis);

            TrackRow? nearest = null;
            float best = StopTrackDistance;
            foreach (TrackRow row in rows)
            {
                float2 span = spans[row];
                if (along < span.x - StopTrackDistance || along > span.y + StopTrackDistance)
                    continue;
                float distance = math.abs(row.Lateral - lateral);
                if (distance > best)
                    continue;
                best = distance;
                nearest = row;
            }
            if (nearest != null)
                nearest.StopCount++;
        }

        if (!rows.Exists(row => row.StopCount > 0))
            return;
        foreach (TrackRow row in rows.FindAll(row => row.StopCount == 0))
            foreach (Entity edge in row.Edges)
                platformEdges.Remove(edge);
        rows.RemoveAll(row => row.StopCount == 0);
    }

    /// <summary>
    /// The edges owning the track lanes that the station's passenger stops stand on, with how
    /// many stops stand on each. A stop names its lane through RouteLane, or is attached to an
    /// edge or lane; building stops usually do neither, which KeepRowsWithStops handles.
    /// </summary>
    private static Dictionary<Entity, int> StopEdges(EntityManager entityManager, Entity station, NativeArray<Entity> passengerStops)
    {
        var edges = new Dictionary<Entity, int>();
        foreach (Entity stop in passengerStops)
        {
            if (!OwnedBy(entityManager, stop, station))
                continue;
            var stopEdges = new HashSet<Entity>();
            if (entityManager.HasComponent<RouteLane>(stop))
            {
                RouteLane route = entityManager.GetComponentData<RouteLane>(stop);
                AddLaneOwner(entityManager, route.m_StartLane, stopEdges);
                AddLaneOwner(entityManager, route.m_EndLane, stopEdges);
            }
            else if (entityManager.HasComponent<Game.Objects.Attached>(stop))
            {
                Entity parent = entityManager.GetComponentData<Game.Objects.Attached>(stop).m_Parent;
                if (entityManager.HasComponent<Edge>(parent) && HasRailTrack(entityManager, parent))
                    stopEdges.Add(parent);
                else
                    AddLaneOwner(entityManager, parent, stopEdges);
            }
            foreach (Entity edge in stopEdges)
            {
                edges.TryGetValue(edge, out int count);
                edges[edge] = count + 1;
            }
        }
        return edges;
    }

    private static void AddLaneOwner(EntityManager entityManager, Entity lane, HashSet<Entity> edges)
    {
        if (!StationNets.IsRailLane(entityManager, lane) || !entityManager.HasComponent<Owner>(lane))
            return;
        Entity owner = entityManager.GetComponentData<Owner>(lane).m_Owner;
        if (entityManager.HasComponent<Edge>(owner))
            edges.Add(owner);
    }

    private static bool OwnedBy(EntityManager entityManager, Entity entity, Entity station)
    {
        Entity current = entity;
        for (int depth = 0; depth < 4; depth++)
        {
            if (!entityManager.HasComponent<Owner>(current))
                return false;
            current = entityManager.GetComponentData<Owner>(current).m_Owner;
            if (current == station)
                return true;
        }
        return false;
    }

    private static bool IsPlatformEdge(EntityManager entityManager, Entity net, TrackTypes type)
    {
        if (!entityManager.Exists(net)
            || !entityManager.HasComponent<Edge>(net)
            || !entityManager.HasComponent<Curve>(net)
            || !entityManager.HasBuffer<SubLane>(net))
            return false;
        foreach (SubLane subLane in entityManager.GetBuffer<SubLane>(net, true))
        {
            Entity lane = subLane.m_SubLane;
            if (StationNets.IsRailLane(entityManager, lane, type)
                && (entityManager.GetComponentData<TrackLane>(lane).m_Flags & TrackLaneFlags.Station) != 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Direction of the platforms, pointing from end 0 to end 1 as seen by someone standing at the
    /// building's front (its street side) and facing it. That viewer looks along the building's
    /// -forward, so their right is the building's -right. Parallel to the street, end 0 is the
    /// viewer's left; pointing at the street, end 0 is the far one and end 1 the street one.
    /// </summary>
    private static float3 FindAxis(
        EntityManager entityManager,
        Entity station,
        List<Entity> platformEdges,
        out bool endsFaceStreet)
    {
        endsFaceStreet = false;
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
            endsFaceStreet = math.abs(math.dot(axis, right)) < 0.5f;
            float reference = endsFaceStreet
                ? math.dot(axis, forward)
                : -math.dot(axis, right);
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
