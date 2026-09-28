using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace StraightTrainStop
{

/// <summary>
/// Saved on a station while at least one end of one of its platform tracks is straight-only.
/// Bit i of each mask is track i of <see cref="StationLayout"/>.
/// </summary>
public struct StationJunctionLock : IComponentData, IQueryTypeParameter, ISerializable
{
    /// <summary>Schema 4 added the masks; earlier saves locked the whole station.</summary>
    public const int CurrentSchema = 4;

    public int m_SchemaVersion;
    public ulong m_LeftMask;
    public ulong m_RightMask;

    public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
    {
        writer.Write(CurrentSchema);
        writer.Write(m_LeftMask);
        writer.Write(m_RightMask);
    }

    public void Deserialize<TReader>(TReader reader) where TReader : IReader
    {
        reader.Read(out m_SchemaVersion);
        if (m_SchemaVersion >= 4)
        {
            reader.Read(out m_LeftMask);
            reader.Read(out m_RightMask);
        }
        else
        {
            m_LeftMask = ulong.MaxValue;
            m_RightMask = ulong.MaxValue;
        }
    }
}

internal readonly struct RowStatus
{
    public readonly bool Left;
    public readonly bool Right;
    public readonly int LeftChanges;
    public readonly int RightChanges;

    public RowStatus(bool left, bool right, int leftChanges, int rightChanges)
    {
        Left = left;
        Right = right;
        LeftChanges = leftChanges;
        RightChanges = rightChanges;
    }
}

internal readonly struct StationLockStatus
{
    public static readonly StationLockStatus NotAStation =
        new StationLockStatus(false, new List<RowStatus>(), 0);

    public readonly bool IsStation;
    public readonly List<RowStatus> Rows;
    public readonly int RemovedCount;

    public StationLockStatus(bool isStation, List<RowStatus> rows, int removed)
    {
        IsStation = isStation;
        Rows = rows;
        RemovedCount = removed;
    }
}

/// <summary>
/// Implements the manual station command: on a locked station the lanes that change track are
/// deleted and the straight ones kept, so each platform track is reachable only straight on, the
/// way Traffic's lane connector removes a connection. The junction itself stays.
///
/// The nodes looked at are the station asset's own, including those where an asset edge meets a
/// track the player drew. Only lanes running into or out of an asset edge (asset edges carry an
/// Owner, player edges do not) can go: a connection between two player tracks is never touched.
///
/// Where several track lanes leave from the same lane end, or arrive at the same one, the lane
/// with the smallest sideways shift stays, and any that shifts clearly further, by about a track
/// spacing, is a lane change and goes. The shift is measured across the lane's mean direction, so
/// a straight lane through a curved node scores near zero while an S-shaped crossover scores its
/// full offset; comparing heading instead fails, as both kinds leave and arrive parallel. A lane
/// alone in its group is never touched.
///
/// It works with the game's own lane pipeline rather than against it. Locking or unlocking a
/// station asks StationRegenerationSystem to mark the station's net edges and nodes updated
/// (Modification1). LaneSystem, the game's or Traffic's replacement, regenerates their lanes and
/// tags them Updated or Created (Modification4, applied by ModificationBarrier4). This system runs
/// next (Modification4B, before LaneReferencesSystem) and marks the diverging branches Deleted,
/// exactly as LaneSystem marks a lane it no longer wants; lane references, overlaps, vehicles and
/// the pathfinder then drop them the same way. Unlocking just lets LaneSystem rebuild them.
/// </summary>
public sealed partial class StationJunctionLockSystem : GameSystemBase
{
    private const int RetryInterval = 256;

    /// <summary>A lane shifting sideways at least this much more than the straightest is a lane change.</summary>
    private const float LaneChangeMargin = 1.5f;

    private EntityQuery _lockedStationQuery;
    private EntityQuery _regeneratedTrackLaneQuery;
    private EntityQuery _trainQuery;
    private StationRegenerationSystem _regeneration = null!;
    private readonly Dictionary<Entity, int> _removedCounts = new Dictionary<Entity, int>();
    private readonly HashSet<Entity> _deferredStations = new HashSet<Entity>();
    private readonly List<SideRequest> _requests = new List<SideRequest>();
    private int _ticks;

    private readonly struct SideRequest
    {
        public readonly Entity Station;
        public readonly int Row;
        public readonly int Side;
        public readonly bool Enabled;

        public SideRequest(Entity station, int row, int side, bool enabled)
        {
            Station = station;
            Row = row;
            Side = side;
            Enabled = enabled;
        }
    }

    internal int Revision { get; private set; }
    internal Entity LastStation { get; private set; }
    internal string LastMessage { get; private set; } = string.Empty;

    [Preserve]
    protected override void OnCreate()
    {
        base.OnCreate();
        _regeneration = World.GetOrCreateSystemManaged<StationRegenerationSystem>();
        _lockedStationQuery = GetEntityQuery(new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<TransportStation>(),
                ComponentType.ReadOnly<StationJunctionLock>()
            },
            None = new[] { ComponentType.ReadOnly<Deleted>() }
        });
        _regeneratedTrackLaneQuery = GetEntityQuery(new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<Lane>(),
                ComponentType.ReadOnly<TrackLane>(),
                ComponentType.ReadOnly<Owner>()
            },
            Any = new[]
            {
                ComponentType.ReadOnly<Created>(),
                ComponentType.ReadOnly<Updated>()
            },
            None = new[]
            {
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Temp>()
            }
        });
        _trainQuery = GetEntityQuery(new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<Train>(),
                ComponentType.ReadOnly<LayoutElement>(),
                ComponentType.ReadOnly<PathOwner>()
            },
            None = new[]
            {
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Temp>()
            }
        });
    }

    protected override void OnGameLoaded(Context serializationContext)
    {
        base.OnGameLoaded(serializationContext);
        _requests.Clear();
        _removedCounts.Clear();
        _deferredStations.Clear();

        // Deleted lanes are not saved, but a station can have been regenerated without the mod,
        // e.g. when the save was made by another version. Regenerate once to be sure.
        using NativeArray<Entity> stations = _lockedStationQuery.ToEntityArray(Allocator.Temp);
        foreach (Entity station in stations)
            _regeneration.RequestRegenerate(station);
    }

    [Preserve]
    protected override void OnUpdate()
    {
        try
        {
            if (_requests.Count > 0)
            {
                var requests = new List<SideRequest>(_requests);
                _requests.Clear();
                foreach (SideRequest request in requests)
                    ApplyRequest(request);
            }

            if (_lockedStationQuery.IsEmptyIgnoreFilter)
                return;

            if (!_regeneratedTrackLaneQuery.IsEmptyIgnoreFilter)
                RemoveRegeneratedBranches();

            // A branch with a train on it is left until the train has gone, then the station is
            // regenerated again.
            if (_deferredStations.Count > 0 && ++_ticks >= RetryInterval)
            {
                _ticks = 0;
                foreach (Entity station in _deferredStations)
                    if (IsLockedStation(station))
                        _regeneration.RequestRegenerate(station);
                _deferredStations.Clear();
            }
        }
        catch (Exception exception)
        {
            Mod.Log.Error("Straight Train Stop could not update the station junction lock: " + exception);
            LastMessage = "The station command failed; see StraightTrainStop.log.";
            Revision++;
        }
    }

    /// <summary>
    /// Queues a change from the UI: one end of one platform track, or with a negative row every
    /// end of every track at once. Side 0 is left, 1 is right.
    /// </summary>
    internal void Request(Entity station, int row, int side, bool enabled)
    {
        _requests.Add(new SideRequest(station, row, side, enabled));
    }

    internal StationLockStatus Inspect(Entity station)
    {
        if (!IsLiveStation(station))
            return StationLockStatus.NotAStation;

        EntityManager.CompleteAllTrackedJobs();
        StationLayout layout = StationLayout.Build(EntityManager, station);
        GetMasks(station, out ulong left, out ulong right);
        _removedCounts.TryGetValue(station, out int removed);

        var rows = new List<RowStatus>();
        for (int i = 0; i < layout.Rows.Count; i++)
        {
            ulong bit = i < 64 ? 1UL << i : 0UL;
            TrackRow row = layout.Rows[i];
            rows.Add(new RowStatus(
                (left & bit) != 0,
                (right & bit) != 0,
                CountLaneChanges(row.Left),
                CountLaneChanges(row.Right)));
        }
        return new StationLockStatus(true, rows, removed);
    }

    private int CountLaneChanges(TrackSide side)
    {
        var branches = new HashSet<Entity>();
        foreach (Entity node in side.Nodes)
            FindDivergingBranches(node, side.Edges, branches);
        return branches.Count;
    }

    private void GetMasks(Entity station, out ulong left, out ulong right)
    {
        left = 0;
        right = 0;
        if (!EntityManager.HasComponent<StationJunctionLock>(station))
            return;
        StationJunctionLock data = EntityManager.GetComponentData<StationJunctionLock>(station);
        left = data.m_LeftMask;
        right = data.m_RightMask;
    }

    private void ApplyRequest(SideRequest request)
    {
        Entity station = request.Station;
        if (!IsLiveStation(station))
            return;

        EntityManager.CompleteAllTrackedJobs();
        LastStation = station;
        StationLayout layout = StationLayout.Build(EntityManager, station);
        ulong all = layout.AllRowsMask;
        GetMasks(station, out ulong left, out ulong right);

        if (request.Row < 0)
        {
            left = request.Enabled ? all : 0;
            right = request.Enabled ? all : 0;
        }
        else if (request.Row < layout.Rows.Count && request.Row < 64)
        {
            ulong bit = 1UL << request.Row;
            if (request.Side == 0)
                left = request.Enabled ? left | bit : left & ~bit;
            else
                right = request.Enabled ? right | bit : right & ~bit;
        }
        left &= all;
        right &= all;

        if (left == 0 && right == 0)
        {
            if (EntityManager.HasComponent<StationJunctionLock>(station))
                EntityManager.RemoveComponent<StationJunctionLock>(station);
            _deferredStations.Remove(station);
        }
        else
        {
            var data = new StationJunctionLock
            {
                m_SchemaVersion = StationJunctionLock.CurrentSchema,
                m_LeftMask = left,
                m_RightMask = right
            };
            if (EntityManager.HasComponent<StationJunctionLock>(station))
                EntityManager.SetComponentData(station, data);
            else
                EntityManager.AddComponentData(station, data);
        }

        // Regenerating puts back what a switched-off end had lost; the enabled ends are cleared
        // again as their lanes come out of LaneSystem, which also recounts the removals.
        _removedCounts.Remove(station);
        _regeneration.RequestRegenerate(station);
        LastMessage = left == 0 && right == 0 ? "Lane changes will be rebuilt." : "Updating lane changes.";
        Revision++;

        Dictionary<Entity, HashSet<Entity>> scope = layout.EnabledScope(left, right);
        var branches = new HashSet<Entity>();
        foreach (KeyValuePair<Entity, HashSet<Entity>> pair in scope)
            FindDivergingBranches(pair.Key, pair.Value, branches);
        string what = request.Row < 0
            ? (request.Enabled ? "all ends on" : "all ends off")
            : $"track {request.Row + 1} {(request.Side == 0 ? "left" : "right")} {(request.Enabled ? "on" : "off")}";
        Mod.Log.Info($"Station #{station.Index}: {what}; {layout.Rows.Count} track(s), masks L={left:X} R={right:X}, {branches.Count} lane change(s) to remove.");

    }

    /// <summary>
    /// Deletes the lane changes at the enabled ends of every locked station LaneSystem has just
    /// regenerated lanes for. Only lanes carrying Created or Updated this frame lead to a station
    /// being looked at, so an unchanged network costs one empty query check.
    /// </summary>
    private void RemoveRegeneratedBranches()
    {
        EntityManager.CompleteAllTrackedJobs();
        using NativeArray<Entity> regenerated = _regeneratedTrackLaneQuery.ToEntityArray(Allocator.Temp);
        using NativeArray<Entity> stations = _lockedStationQuery.ToEntityArray(Allocator.Temp);

        var layouts = new Dictionary<Entity, StationLayout>();
        var scopes = new Dictionary<Entity, Dictionary<Entity, HashSet<Entity>>>();
        foreach (Entity station in stations)
        {
            GetMasks(station, out ulong left, out ulong right);
            StationLayout layout = StationLayout.Build(EntityManager, station);
            layouts[station] = layout;
            scopes[station] = layout.EnabledScope(left, right);
        }

        // Any regenerated lane of a station, edge lane or node lane, sends every enabled node of
        // that station through the check. Looking only at the nodes whose lanes were tagged left
        // lane changes in place at an end just switched on, apparently because LaneSystem does
        // not tag every lane of a node it regenerates.
        var touched = new HashSet<Entity>();
        var seenOwners = new HashSet<Entity>();
        foreach (Entity lane in regenerated)
        {
            Entity owner = EntityManager.GetComponentData<Owner>(lane).m_Owner;
            if (!seenOwners.Add(owner))
                continue;
            foreach (Entity station in stations)
                if (layouts[station].Nets.Contains(owner))
                    touched.Add(station);
        }

        var lanesByNode = new Dictionary<Entity, List<Entity>>();
        foreach (Entity lane in regenerated)
        {
            Entity owner = EntityManager.GetComponentData<Owner>(lane).m_Owner;
            if (!lanesByNode.TryGetValue(owner, out List<Entity> list))
                lanesByNode[owner] = list = new List<Entity>();
            list.Add(lane);
        }

        var nodesByStation = new Dictionary<Entity, HashSet<Entity>>();
        foreach (Entity station in touched)
            if (scopes[station].Count > 0)
                nodesByStation[station] = new HashSet<Entity>(scopes[station].Keys);

        foreach (KeyValuePair<Entity, HashSet<Entity>> pair in nodesByStation)
        {
            Entity station = pair.Key;
            var branches = new HashSet<Entity>();
            foreach (Entity node in pair.Value)
            {
                lanesByNode.TryGetValue(node, out List<Entity>? created);
                FindDivergingBranches(node, scopes[station][node], branches, created);
            }

            int removed = 0;
            int busy = 0;
            int rerouted = 0;
            foreach (Entity lane in branches)
            {
                if (IsLaneOccupied(lane))
                {
                    busy++;
                    continue;
                }

                rerouted += InvalidateTrainPathsUsing(lane);
                EntityManager.AddComponent<Deleted>(lane);
                removed++;
            }

            if (busy > 0)
                _deferredStations.Add(station);
            if (removed == 0 && busy == 0)
                continue;

            _removedCounts.TryGetValue(station, out int total);
            _removedCounts[station] = total + removed;
            if (LastStation == station)
                LastMessage = BuildEnabledMessage(total + removed, busy);
            Revision++;
            Mod.Log.Info($"Station #{station.Index}: removed {removed} lane change(s), {busy} occupied left for later, {rerouted} train path(s) refreshed.");
        }
    }

    /// <summary>
    /// Adds the track lanes of a node that leave a shared lane end, or arrive at one, less
    /// straight than the straightest lane of that group. Only lanes that run into or out of one
    /// of the station's own edges qualify: a connection between two player edges is left alone
    /// even when it is the lesser branch.
    /// </summary>
    private void FindDivergingBranches(
        Entity node,
        HashSet<Entity> allowedEdges,
        HashSet<Entity> output,
        List<Entity>? createdLanes = null)
    {
        if (!EntityManager.Exists(node) || !EntityManager.HasBuffer<SubLane>(node))
            return;

        var bySource = new Dictionary<PathNode, List<Entity>>();
        var byTarget = new Dictionary<PathNode, List<Entity>>();
        var turns = new Dictionary<Entity, float>();
        var lanes = new HashSet<Entity>();
        foreach (SubLane subLane in EntityManager.GetBuffer<SubLane>(node, true))
            if ((subLane.m_PathMethods & PathMethod.Track) != 0)
                lanes.Add(subLane.m_SubLane);

        // A lane LaneSystem has just created is not in the node's SubLane buffer yet:
        // LaneReferencesSystem adds it after this system has run.
        if (createdLanes != null)
            lanes.UnionWith(createdLanes);

        foreach (Entity lane in lanes)
        {
            if (!StationNets.IsTrainLane(EntityManager, lane)
                || !EntityManager.HasComponent<Curve>(lane)
                || EntityManager.HasComponent<Deleted>(lane))
                continue;

            Lane laneData = EntityManager.GetComponentData<Lane>(lane);
            Bezier4x3 bezier = EntityManager.GetComponentData<Curve>(lane).m_Bezier;
            turns[lane] = SidewaysShift(bezier);
            Add(bySource, laneData.m_StartNode.StripCurvePos(), lane);
            Add(byTarget, laneData.m_EndNode.StripCurvePos(), lane);
        }

        var lessStraight = new HashSet<Entity>();
        CollectLessStraight(bySource, turns, lessStraight);
        CollectLessStraight(byTarget, turns, lessStraight);
        foreach (Entity lane in lessStraight)
            if (TouchesStationEdge(lane, allowedEdges))
                output.Add(lane);
    }

    private bool TouchesStationEdge(Entity lane, HashSet<Entity> stationNets)
    {
        Lane laneData = EntityManager.GetComponentData<Lane>(lane);
        foreach (Entity net in stationNets)
        {
            if (!EntityManager.HasComponent<Game.Net.Edge>(net))
                continue;
            var edgeNode = new PathNode(net, (ushort)0);
            if (laneData.m_StartNode.OwnerEquals(edgeNode) || laneData.m_EndNode.OwnerEquals(edgeNode))
                return true;
        }
        return false;
    }

    private static void CollectLessStraight(
        Dictionary<PathNode, List<Entity>> groups,
        Dictionary<Entity, float> turns,
        HashSet<Entity> output)
    {
        foreach (List<Entity> group in groups.Values)
        {
            if (group.Count < 2)
                continue;
            float straightest = float.MaxValue;
            foreach (Entity lane in group)
                straightest = math.min(straightest, turns[lane]);
            foreach (Entity lane in group)
                if (turns[lane] >= straightest + LaneChangeMargin)
                    output.Add(lane);
        }
    }

    private static void Add(Dictionary<PathNode, List<Entity>> groups, PathNode key, Entity lane)
    {
        if (!groups.TryGetValue(key, out List<Entity> list))
            groups[key] = list = new List<Entity>();
        list.Add(lane);
    }

    /// <summary>
    /// Horizontal distance between the lane's end points measured across its mean direction:
    /// about zero for a plain arc, about the track spacing for an S-shaped crossover.
    /// </summary>
    private static float SidewaysShift(Bezier4x3 bezier)
    {
        float3 start = math.normalizesafe(MathUtils.StartTangent(bezier) * new float3(1f, 0f, 1f));
        float3 end = math.normalizesafe(MathUtils.EndTangent(bezier) * new float3(1f, 0f, 1f));
        float3 direction = math.normalizesafe(start + end);
        float3 chord = (bezier.d - bezier.a) * new float3(1f, 0f, 1f);
        return math.abs(direction.x * chord.z - direction.z * chord.x);
    }

    private bool IsLaneOccupied(Entity lane)
    {
        if (EntityManager.HasBuffer<LaneObject>(lane)
            && EntityManager.GetBuffer<LaneObject>(lane, true).Length > 0)
            return true;
        return EntityManager.HasComponent<LaneReservation>(lane)
            && EntityManager.GetComponentData<LaneReservation>(lane).m_Blocker != Entity.Null;
    }

    private int InvalidateTrainPathsUsing(Entity removedLane)
    {
        int invalidated = 0;
        using NativeArray<Entity> trains = _trainQuery.ToEntityArray(Allocator.Temp);
        foreach (Entity train in trains)
        {
            bool usesLane = false;
            if (EntityManager.HasBuffer<PathElement>(train))
            {
                DynamicBuffer<PathElement> path = EntityManager.GetBuffer<PathElement>(train, true);
                for (int i = 0; i < path.Length && !usesLane; i++)
                    usesLane = path[i].m_Target == removedLane;
            }

            if (!usesLane && EntityManager.HasBuffer<TrainNavigationLane>(train))
            {
                DynamicBuffer<TrainNavigationLane> navigation =
                    EntityManager.GetBuffer<TrainNavigationLane>(train, true);
                for (int i = 0; i < navigation.Length && !usesLane; i++)
                    usesLane = navigation[i].m_Lane == removedLane;
            }

            if (!usesLane)
                continue;

            PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(train);
            pathOwner.m_State |= PathFlags.Obsolete;
            EntityManager.SetComponentData(train, pathOwner);
            invalidated++;
        }
        return invalidated;
    }

    private bool IsLiveStation(Entity station)
    {
        return station != Entity.Null
            && EntityManager.Exists(station)
            && EntityManager.HasComponent<TransportStation>(station)
            && !EntityManager.HasComponent<Deleted>(station);
    }

    private bool IsLockedStation(Entity station)
    {
        return IsLiveStation(station) && EntityManager.HasComponent<StationJunctionLock>(station);
    }

    private static string BuildEnabledMessage(int removed, int busy)
    {
        string message = removed == 1
            ? "1 lane change removed."
            : removed + " lane changes removed.";
        if (busy > 0)
            message += " " + busy + " will go when the train on it has passed.";
        return message;
    }
}

}
