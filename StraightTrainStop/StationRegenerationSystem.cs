using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Unity.Entities;
using UnityEngine.Scripting;

namespace StraightTrainStop
{

/// <summary>
/// Marks a station's railway edges and their nodes updated on request, so LaneSystem regenerates
/// the station's track lanes. Roads, paths and other nets of the station and its upgrades are left
/// alone: updating all of them right after a load is the likely reason a station lost its road
/// connection after a reload. StationJunctionLockSystem then removes the lane changes of the
/// enabled track ends, and a switched-off end simply gets them back. Runs in Modification1, ahead
/// of LaneSystem (Modification4) in the same frame.
/// </summary>
public sealed partial class StationRegenerationSystem : GameSystemBase
{
    private readonly HashSet<Entity> _requests = new HashSet<Entity>();

    /// <summary>Has LaneSystem regenerate one station's lanes on the next frame.</summary>
    internal void RequestRegenerate(Entity station)
    {
        if (station != Entity.Null)
            _requests.Add(station);
    }

    [Preserve]
    protected override void OnUpdate()
    {
        if (_requests.Count == 0)
            return;

        try
        {
            EntityManager.CompleteAllTrackedJobs();
            var requests = new List<Entity>(_requests);
            _requests.Clear();
            foreach (Entity station in requests)
                RegenerateStation(station);
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
        foreach (Entity owner in owners)
        {
            if (!EntityManager.HasComponent<Game.Net.Edge>(owner) || !StationLayout.HasTrainTrack(EntityManager, owner))
                continue;
            Game.Net.Edge ends = EntityManager.GetComponentData<Game.Net.Edge>(owner);
            tracks.Add(owner);
            tracks.Add(ends.m_Start);
            tracks.Add(ends.m_End);
        }
        foreach (Entity net in tracks)
            if (EntityManager.Exists(net) && !EntityManager.HasComponent<Updated>(net))
                EntityManager.AddComponent<Updated>(net);
    }
}

}
