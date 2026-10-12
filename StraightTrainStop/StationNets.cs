using System.Collections.Generic;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Unity.Entities;

namespace StraightTrainStop
{

/// <summary>Finds the net edges and nodes that make up a station, upgrades included.</summary>
internal static class StationNets
{
    /// <summary>Adds the station's sub-net edges, their end nodes, and its sub-net nodes.</summary>
    public static void Collect(EntityManager entityManager, Entity station, HashSet<Entity> output)
    {
        CollectSubNets(entityManager, station, output);
        if (!entityManager.HasBuffer<InstalledUpgrade>(station))
            return;

        var upgrades = new List<Entity>();
        foreach (InstalledUpgrade upgrade in entityManager.GetBuffer<InstalledUpgrade>(station, true))
            upgrades.Add(upgrade.m_Upgrade);
        foreach (Entity upgrade in upgrades)
            CollectSubNets(entityManager, upgrade, output);
    }

    /// <summary>The track types the mod works on: railway and metro, not tram.</summary>
    public const TrackTypes RailTypes = TrackTypes.Train | TrackTypes.Subway;

    /// <summary>
    /// True for a track lane of one of the given types. Train, tram and subway tracks all use
    /// TrackLane, so the type comes from the lane prefab.
    /// </summary>
    public static bool IsRailLane(EntityManager entityManager, Entity lane, TrackTypes types = RailTypes)
    {
        if (!entityManager.Exists(lane)
            || !entityManager.HasComponent<TrackLane>(lane)
            || !entityManager.HasComponent<Game.Prefabs.PrefabRef>(lane))
            return false;
        Entity prefab = entityManager.GetComponentData<Game.Prefabs.PrefabRef>(lane).m_Prefab;
        return entityManager.HasComponent<Game.Prefabs.TrackLaneData>(prefab)
            && (entityManager.GetComponentData<Game.Prefabs.TrackLaneData>(prefab).m_TrackTypes & types) != 0;
    }

    public static void AddNetOwner(EntityManager entityManager, Entity owner, HashSet<Entity> output)
    {
        if (!entityManager.Exists(owner) || entityManager.HasComponent<Deleted>(owner))
            return;
        output.Add(owner);
        if (entityManager.HasComponent<Edge>(owner))
        {
            Edge edge = entityManager.GetComponentData<Edge>(owner);
            output.Add(edge.m_Start);
            output.Add(edge.m_End);
        }
    }

    private static void CollectSubNets(EntityManager entityManager, Entity owner, HashSet<Entity> output)
    {
        if (!entityManager.Exists(owner) || !entityManager.HasBuffer<Game.Net.SubNet>(owner))
            return;

        var entities = new List<Entity>();
        foreach (Game.Net.SubNet subNet in entityManager.GetBuffer<Game.Net.SubNet>(owner, true))
            entities.Add(subNet.m_SubNet);
        foreach (Entity entity in entities)
            AddNetOwner(entityManager, entity, output);
    }
}

}
