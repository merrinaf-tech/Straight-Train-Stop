using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace StraightTrainStop
{

/// <summary>Saved on a tram stop whose neighbouring crossings the player asked to remove.</summary>
public struct TramStopLock : IComponentData, IQueryTypeParameter, ISerializable
{
    public const int CurrentSchema = 1;

    public int m_SchemaVersion;

    public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
    {
        writer.Write(CurrentSchema);
    }

    public void Deserialize<TReader>(TReader reader) where TReader : IReader
    {
        reader.Read(out m_SchemaVersion);
    }
}

/// <summary>
/// Removes the tram crossings at the two road nodes either side of a tram stop, when the player
/// switches it on for that stop.
///
/// This is the one place the mod touches junctions between networks the player built: tram
/// tracks are lanes inside the player's roads. It is kept as narrow as possible: only the two end
/// nodes of the road edge the stop is attached to, only tram lanes, only lanes running into or out
/// of that edge, and only lane changes - a lane that turns more than <see cref="MaximumTurn"/> is a
/// turn into another road at an intersection and is kept, as is anything a car uses.
///
/// At a large intersection the crossings run diagonally across it, so their sideways shift is the
/// width of the junction (about 50 m on a six-way one), not a track spacing.
///
/// The nodes are regenerated together with every edge meeting them, never alone: the edges' lanes
/// list the node lanes they overlap, and regenerating only the nodes left those lists pointing at
/// deleted crossings, which crashed the game intermittently at busy intersections (2026-10-12/13).
/// Any lane a rail vehicle is on or has in its navigation is left for a later pass, and lanes cars
/// drive are never candidates.
///
/// It uses the same pipeline as the stations: the nodes are marked updated by
/// StationRegenerationSystem (Modification1), LaneSystem regenerates their lanes (Modification4),
/// and this system deletes the crossings right after (Modification4B, before
/// LaneReferencesSystem).
/// </summary>
public sealed partial class TramCrossingSystem : GameSystemBase
{
    private const float LaneChangeMargin = 1.5f;

    private const float MaximumTurn = 0.5236f;
    private const int RetryInterval = 256;

    private EntityQuery _lockedStopQuery;
    private EntityQuery _tramStopQuery;
    private EntityQuery _regeneratedTrackLaneQuery;
    private EntityQuery _tramQuery;
    private StationRegenerationSystem _regeneration = null!;
    private RailVehicleLanes _vehicleLanes = null!;
    private readonly List<(Entity Stop, bool Enabled)> _requests = new List<(Entity, bool)>();
    private readonly HashSet<Entity> _deferredStops = new HashSet<Entity>();
    private bool _checkAllAfterLoad;
    private int _ticks;

    internal int Revision { get; private set; }
    internal Entity LastStop { get; private set; }
    internal string LastMessage { get; private set; } = string.Empty;

    [Preserve]
    protected override void OnCreate()
    {
        base.OnCreate();
        _regeneration = World.GetOrCreateSystemManaged<StationRegenerationSystem>();
        _vehicleLanes = new RailVehicleLanes(desc => GetEntityQuery(desc));
        _lockedStopQuery = GetEntityQuery(new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<Game.Routes.TramStop>(),
                ComponentType.ReadOnly<TramStopLock>(),
                ComponentType.ReadOnly<Game.Objects.Attached>()
            },
            None = new[]
            {
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Temp>()
            }
        });
        _tramStopQuery = GetEntityQuery(new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<Game.Routes.TramStop>(),
                ComponentType.ReadOnly<Game.Objects.Attached>()
            },
            None = new[]
            {
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Temp>()
            }
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
        _tramQuery = GetEntityQuery(new EntityQueryDesc
        {
            All = new[]
            {
                // Trams are trains to the game: same vehicle components, tram track type.
                ComponentType.ReadOnly<Game.Vehicles.Train>(),
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
        _deferredStops.Clear();
        // Deleted lanes are not saved; check once anyway. Unlike stations this goes through a
        // regeneration of the nodes and their edges, so the edges' overlap lists are rebuilt too.
        _checkAllAfterLoad = true;
    }

    [Preserve]
    protected override void OnUpdate()
    {
        try
        {
            if (_requests.Count > 0)
            {
                var requests = new List<(Entity Stop, bool Enabled)>(_requests);
                _requests.Clear();
                foreach ((Entity stop, bool enabled) in requests)
                    ApplyRequest(stop, enabled);
            }

            if (_lockedStopQuery.IsEmptyIgnoreFilter)
                return;

            if (_checkAllAfterLoad)
            {
                _checkAllAfterLoad = false;
                using NativeArray<Entity> stops = _lockedStopQuery.ToEntityArray(Allocator.Temp);
                foreach (Entity stop in stops)
                    _regeneration.RequestNodes(StopNodes(stop, out _));
            }
            else if (!_regeneratedTrackLaneQuery.IsEmptyIgnoreFilter)
            {
                RemoveCrossings(false);
            }

            // A crossing with a tram on it is left until the tram has gone.
            if (_deferredStops.Count > 0 && ++_ticks >= RetryInterval)
            {
                _ticks = 0;
                foreach (Entity stop in _deferredStops)
                    if (IsLocked(stop))
                        _regeneration.RequestNodes(StopNodes(stop, out _));
                _deferredStops.Clear();
            }
        }
        catch (Exception exception)
        {
            Mod.Log.Error("Straight Train Stop could not update a tram stop: " + exception);
            LastMessage = "The tram stop command failed; see StraightTrainStop.log.";
            Revision++;
        }
    }

    internal bool IsTramStop(Entity entity)
    {
        return entity != Entity.Null
            && EntityManager.Exists(entity)
            && EntityManager.HasComponent<Game.Routes.TramStop>(entity)
            && EntityManager.HasComponent<Game.Objects.Attached>(entity)
            && !EntityManager.HasComponent<Deleted>(entity);
    }

    /// <summary>
    /// True when this stop, or another tram stop on the same road edge, is switched on. Stops on
    /// one edge, typically one per direction on opposite sides of the road, act on the same two
    /// nodes, so they share one state.
    /// </summary>
    internal bool IsLocked(Entity stop)
    {
        if (!IsTramStop(stop))
            return false;
        foreach (Entity sibling in StopsOnSameEdge(stop))
            if (EntityManager.HasComponent<TramStopLock>(sibling))
                return true;
        return false;
    }

    /// <summary>Every tram stop attached to the same road edge as this one, itself included.</summary>
    private List<Entity> StopsOnSameEdge(Entity stop)
    {
        var stops = new List<Entity> { stop };
        StopNodes(stop, out Entity edge);
        if (edge == Entity.Null)
            return stops;
        using NativeArray<Entity> all = _tramStopQuery.ToEntityArray(Allocator.Temp);
        foreach (Entity other in all)
        {
            if (other == stop)
                continue;
            StopNodes(other, out Entity otherEdge);
            if (otherEdge == edge)
                stops.Add(other);
        }
        return stops;
    }

    internal void Request(Entity stop, bool enabled)
    {
        _requests.Add((stop, enabled));
    }

    /// <summary>The tram crossings currently present at the stop's two nodes.</summary>
    internal int CountCrossings(Entity stop)
    {
        if (!IsTramStop(stop))
            return 0;
        EntityManager.CompleteAllTrackedJobs();
        List<Entity> nodes = StopNodes(stop, out Entity edge);
        var crossings = new HashSet<Entity>();
        foreach (Entity node in nodes)
            FindCrossings(node, edge, crossings, null);
        return crossings.Count;
    }

    private void ApplyRequest(Entity stop, bool enabled)
    {
        if (!IsTramStop(stop))
            return;

        EntityManager.CompleteAllTrackedJobs();
        LastStop = stop;
        List<Entity> siblings = StopsOnSameEdge(stop);
        foreach (Entity sibling in siblings)
        {
            if (enabled && !EntityManager.HasComponent<TramStopLock>(sibling))
                EntityManager.AddComponentData(sibling, new TramStopLock { m_SchemaVersion = TramStopLock.CurrentSchema });
            else if (!enabled && EntityManager.HasComponent<TramStopLock>(sibling))
                EntityManager.RemoveComponent<TramStopLock>(sibling);
            if (!enabled)
                _deferredStops.Remove(sibling);
        }

        List<Entity> nodes = StopNodes(stop, out Entity edge);
        _regeneration.RequestNodes(nodes);
        LastMessage = enabled ? "Updating tram crossings." : "Tram crossings will be rebuilt.";
        Revision++;

        var crossings = new HashSet<Entity>();
        foreach (Entity node in nodes)
            FindCrossings(node, edge, crossings, null);
        Mod.Log.Info($"Tram stop #{stop.Index}: {(enabled ? "on" : "off")} with {siblings.Count - 1} other stop(s) on edge #{edge.Index}, {nodes.Count} node(s), {crossings.Count} crossing(s) to remove.");

    }

    /// <summary>
    /// Deletes the crossings at the nodes of every locked stop that LaneSystem has just
    /// regenerated lanes for.
    /// </summary>
    private void RemoveCrossings(bool allStops)
    {
        EntityManager.CompleteAllTrackedJobs();
        using NativeArray<Entity> regenerated = _regeneratedTrackLaneQuery.ToEntityArray(Allocator.Temp);
        using NativeArray<Entity> stops = _lockedStopQuery.ToEntityArray(Allocator.Temp);

        var lanesByNode = new Dictionary<Entity, List<Entity>>();
        foreach (Entity lane in regenerated)
        {
            Entity owner = EntityManager.GetComponentData<Owner>(lane).m_Owner;
            if (!lanesByNode.TryGetValue(owner, out List<Entity> list))
                lanesByNode[owner] = list = new List<Entity>();
            list.Add(lane);
        }

        HashSet<Entity>? inUse = null;
        var doneEdges = new HashSet<Entity>();
        foreach (Entity stop in stops)
        {
            List<Entity> nodes = StopNodes(stop, out Entity edge);
            if (!doneEdges.Add(edge))
                continue;
            if (!allStops && !nodes.Exists(node => lanesByNode.ContainsKey(node)))
                continue;

            var crossings = new HashSet<Entity>();
            foreach (Entity node in nodes)
            {
                lanesByNode.TryGetValue(node, out List<Entity>? created);
                FindCrossings(node, edge, crossings, created);
            }

            int removed = 0;
            int busy = 0;
            foreach (Entity lane in crossings)
            {
                inUse ??= _vehicleLanes.Collect(EntityManager);
                if (IsLaneOccupied(lane) || inUse.Contains(lane))
                {
                    busy++;
                    continue;
                }
                InvalidateTramPathsUsing(lane);
                EntityManager.AddComponent<Deleted>(lane);
                removed++;
            }

            if (busy > 0)
                _deferredStops.Add(stop);
            if (removed == 0 && busy == 0)
                continue;
            if (LastStop == stop)
                LastMessage = (removed == 1 ? "1 tram crossing removed." : removed + " tram crossings removed.")
                    + (busy > 0 ? " " + busy + " will go when the tram on it has passed." : "");
            Revision++;
            Mod.Log.Info($"Tram stop #{stop.Index}: removed {removed} crossing(s), {busy} occupied left for later.");
        }
    }

    /// <summary>The two end nodes of the road edge the stop is attached to.</summary>
    private List<Entity> StopNodes(Entity stop, out Entity edge)
    {
        var nodes = new List<Entity>();
        edge = Entity.Null;
        if (!EntityManager.HasComponent<Game.Objects.Attached>(stop))
            return nodes;
        Entity parent = EntityManager.GetComponentData<Game.Objects.Attached>(stop).m_Parent;
        if (!EntityManager.HasComponent<Game.Net.Edge>(parent) && EntityManager.HasComponent<Owner>(parent))
            parent = EntityManager.GetComponentData<Owner>(parent).m_Owner;
        if (!EntityManager.Exists(parent) || !EntityManager.HasComponent<Game.Net.Edge>(parent))
            return nodes;
        edge = parent;
        Game.Net.Edge ends = EntityManager.GetComponentData<Game.Net.Edge>(edge);
        nodes.Add(ends.m_Start);
        nodes.Add(ends.m_End);
        return nodes;
    }

    /// <summary>
    /// The tram lanes of a node that run into or out of the stop's edge and change track: of the
    /// near-straight lanes leaving or reaching one lane end, those shifting sideways clearly more
    /// than the straightest. Turns into another road are never candidates.
    /// </summary>
    private void FindCrossings(Entity node, Entity stopEdge, HashSet<Entity> output, List<Entity>? createdLanes)
    {
        if (stopEdge == Entity.Null || !EntityManager.Exists(node) || !EntityManager.HasBuffer<SubLane>(node))
            return;

        var lanes = new HashSet<Entity>();
        foreach (SubLane subLane in EntityManager.GetBuffer<SubLane>(node, true))
            if ((subLane.m_PathMethods & PathMethod.Track) != 0)
                lanes.Add(subLane.m_SubLane);
        // A lane LaneSystem has just created is not in the SubLane buffer until
        // LaneReferencesSystem runs, after this system.
        if (createdLanes != null)
            lanes.UnionWith(createdLanes);

        var shifts = new Dictionary<Entity, float>();
        var bySource = new Dictionary<PathNode, List<Entity>>();
        var byTarget = new Dictionary<PathNode, List<Entity>>();
        foreach (Entity lane in lanes)
        {
            if (!IsStraightTramLane(lane, out float shift))
                continue;
            Lane laneData = EntityManager.GetComponentData<Lane>(lane);
            shifts[lane] = shift;
            Add(bySource, laneData.m_StartNode.StripCurvePos(), lane);
            Add(byTarget, laneData.m_EndNode.StripCurvePos(), lane);
        }

        var edgeNode = new PathNode(stopEdge, (ushort)0);
        foreach (Dictionary<PathNode, List<Entity>> groups in new[] { bySource, byTarget })
        {
            foreach (List<Entity> group in groups.Values)
            {
                if (group.Count < 2)
                    continue;
                float straightest = float.MaxValue;
                foreach (Entity lane in group)
                    straightest = math.min(straightest, shifts[lane]);
                foreach (Entity lane in group)
                {
                    if (shifts[lane] < straightest + LaneChangeMargin)
                        continue;
                    Lane laneData = EntityManager.GetComponentData<Lane>(lane);
                    if (laneData.m_StartNode.OwnerEquals(edgeNode) || laneData.m_EndNode.OwnerEquals(edgeNode))
                        output.Add(lane);
                }
            }
        }
    }

    /// <summary>True for a tram lane that keeps roughly its direction, with its sideways shift.</summary>
    private bool IsStraightTramLane(Entity lane, out float shift)
    {
        shift = 0f;
        // Tram tracks often run inside car lanes; a lane cars drive is never touched.
        if (!StationNets.IsRailLane(EntityManager, lane, TrackTypes.Tram)
            || EntityManager.HasComponent<CarLane>(lane)
            || !EntityManager.HasComponent<Curve>(lane)
            || EntityManager.HasComponent<Deleted>(lane))
            return false;
        Bezier4x3 bezier = EntityManager.GetComponentData<Curve>(lane).m_Bezier;
        if (Turn(bezier) > MaximumTurn)
            return false;
        shift = StationJunctionLockSystem.SidewaysShift(bezier);
        return true;
    }

    private static float Turn(Bezier4x3 bezier)
    {
        float3 start = math.normalizesafe(MathUtils.StartTangent(bezier) * new float3(1f, 0f, 1f));
        float3 end = math.normalizesafe(MathUtils.EndTangent(bezier) * new float3(1f, 0f, 1f));
        return math.acos(math.clamp(math.dot(start, end), -1f, 1f));
    }

    private static void Add(Dictionary<PathNode, List<Entity>> groups, PathNode key, Entity lane)
    {
        if (!groups.TryGetValue(key, out List<Entity> list))
            groups[key] = list = new List<Entity>();
        list.Add(lane);
    }

    private bool IsLaneOccupied(Entity lane)
    {
        if (EntityManager.HasBuffer<LaneObject>(lane)
            && EntityManager.GetBuffer<LaneObject>(lane, true).Length > 0)
            return true;
        return EntityManager.HasComponent<LaneReservation>(lane)
            && EntityManager.GetComponentData<LaneReservation>(lane).m_Blocker != Entity.Null;
    }

    private void InvalidateTramPathsUsing(Entity removedLane)
    {
        using NativeArray<Entity> trams = _tramQuery.ToEntityArray(Allocator.Temp);
        foreach (Entity tram in trams)
        {
            if (!EntityManager.HasBuffer<PathElement>(tram))
                continue;
            DynamicBuffer<PathElement> path = EntityManager.GetBuffer<PathElement>(tram, true);
            bool usesLane = false;
            for (int i = 0; i < path.Length && !usesLane; i++)
                usesLane = path[i].m_Target == removedLane;
            if (!usesLane)
                continue;
            PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(tram);
            pathOwner.m_State |= PathFlags.Obsolete;
            EntityManager.SetComponentData(tram, pathOwner);
        }
    }
}

}
