using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Unity.Entities;
using UnityEngine.Scripting;

namespace StraightTrainStop
{

/// <summary>
/// Marks a station's railway and metro edges and their nodes updated on request, so LaneSystem regenerates
/// the station's track lanes. Roads, paths and other nets of the station and its upgrades are left
/// alone: updating all of them right after a load is the likely reason a station lost its road
/// connection after a reload. StationJunctionLockSystem then removes the lane changes of the
/// enabled track ends, and a switched-off end simply gets them back. Runs in Modification1, ahead
/// of LaneSystem (Modification4) in the same frame.
/// </summary>
public sealed partial class StationRegenerationSystem : GameSystemBase
{
    private readonly HashSet<Entity> _requests = new HashSet<Entity>();
    private readonly HashSet<Entity> _nodeRequests = new HashSet<Entity>();

    /// <summary>Has LaneSystem regenerate one station's lanes on the next frame.</summary>
    internal void RequestRegenerate(Entity station)
    {
        if (station != Entity.Null)
            _requests.Add(station);
    }

    /// <summary>
    /// Has LaneSystem regenerate the lanes of single nodes, such as those by a tram stop, together
    /// with the edges meeting there. The edges' lanes keep a list of the node lanes they overlap;
    /// marking only the node left that list pointing at a deleted crossing, and a car checking the
    /// overlap later took the game down - intermittently, at busy intersections (2026-10-12/13).
    /// </summary>
    internal void RequestNodes(IEnumerable<Entity> nodes)
    {
        foreach (Entity node in nodes)
            if (node != Entity.Null)
                _nodeRequests.Add(node);
    }

    [Preserve]
    protected override void OnUpdate()
    {
        if (_requests.Count == 0 && _nodeRequests.Count == 0)
            return;

        try
        {
            EntityManager.CompleteAllTrackedJobs();
            var requests = new List<Entity>(_requests);
            _requests.Clear();
            foreach (Entity station in requests)
                RegenerateStation(station);

            var nodes = new List<Entity>(_nodeRequests);
            _nodeRequests.Clear();
            var marked = new HashSet<Entity>();
            foreach (Entity node in nodes)
            {
                if (!EntityManager.Exists(node) || EntityManager.HasComponent<Deleted>(node))
                    continue;
                marked.Add(node);
                AddConnectedEdges(node, marked, false);
            }
            foreach (Entity net in marked)
                MarkUpdated(net);
        }
        catch (Exception exception)
        {
            Mod.Log.Error("Straight Train Stop could not regenerate station lanes: " + exception);
        }
    }

    private void RegenerateStation(Entity station)
    {
        if (!EntityManager.Exists(station) || EntityManager.HasComponent<Deleted>(station))
            return;

        var owners = new HashSet<Entity>();
        StationNets.Collect(EntityManager, station, owners);
        var tracks = new HashSet<Entity>();
        var nodes = new HashSet<Entity>();
        foreach (Entity owner in owners)
        {
            if (!EntityManager.HasComponent<Game.Net.Edge>(owner) || !StationLayout.HasRailTrack(EntityManager, owner))
                continue;
            Game.Net.Edge ends = EntityManager.GetComponentData<Game.Net.Edge>(owner);
            tracks.Add(owner);
            nodes.Add(ends.m_Start);
            nodes.Add(ends.m_End);
        }
        // The rail edges meeting the station's nodes from outside too, so their overlap lists are
        // rebuilt along with the lanes they overlap (see RequestNodes).
        foreach (Entity node in nodes)
        {
            tracks.Add(node);
            AddConnectedEdges(node, tracks, true);
        }
        foreach (Entity net in tracks)
            MarkUpdated(net);
    }

    private void AddConnectedEdges(Entity node, HashSet<Entity> output, bool railOnly)
    {
        if (!EntityManager.Exists(node) || !EntityManager.HasBuffer<Game.Net.ConnectedEdge>(node))
            return;
        var edges = new List<Entity>();
        foreach (Game.Net.ConnectedEdge connected in EntityManager.GetBuffer<Game.Net.ConnectedEdge>(node, true))
            edges.Add(connected.m_Edge);
        foreach (Entity edge in edges)
            if (EntityManager.Exists(edge)
                && !EntityManager.HasComponent<Deleted>(edge)
                && (!railOnly || StationLayout.HasRailTrack(EntityManager, edge)))
                output.Add(edge);
    }

    private void MarkUpdated(Entity net)
    {
        if (EntityManager.Exists(net) && !EntityManager.HasComponent<Deleted>(net) && !EntityManager.HasComponent<Updated>(net))
            EntityManager.AddComponent<Updated>(net);
    }
}

}
