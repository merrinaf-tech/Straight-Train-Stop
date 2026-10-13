using System.Collections.Generic;
using Game.Common;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace StraightTrainStop
{

/// <summary>
/// The lanes rail vehicles are on or have already planned to drive: under every car's front and
/// rear bogie, and in each train's navigation list. Trams are trains to the game, so this covers
/// them too. A lane in here must not be deleted yet: the vehicle systems read these lanes every
/// frame, and one deleted under them takes the game down. A lane that is only further along a
/// path is fine to delete once that path is marked obsolete.
/// </summary>
internal sealed class RailVehicleLanes
{
    private readonly EntityQuery _carQuery;
    private readonly EntityQuery _navigationQuery;

    /// <param name="getQuery">The owning system's GetEntityQuery, so the queries are tracked by it.</param>
    public RailVehicleLanes(System.Func<EntityQueryDesc, EntityQuery> getQuery)
    {
        _carQuery = getQuery(new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<TrainCurrentLane>() },
            None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() }
        });
        _navigationQuery = getQuery(new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<TrainNavigationLane>() },
            None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() }
        });
    }

    public HashSet<Entity> Collect(EntityManager entityManager)
    {
        var lanes = new HashSet<Entity>();
        using (NativeArray<Entity> cars = _carQuery.ToEntityArray(Allocator.Temp))
        {
            foreach (Entity car in cars)
            {
                TrainCurrentLane current = entityManager.GetComponentData<TrainCurrentLane>(car);
                lanes.Add(current.m_Front.m_Lane);
                lanes.Add(current.m_Rear.m_Lane);
            }
        }
        using (NativeArray<Entity> trains = _navigationQuery.ToEntityArray(Allocator.Temp))
        {
            foreach (Entity train in trains)
            {
                DynamicBuffer<TrainNavigationLane> navigation = entityManager.GetBuffer<TrainNavigationLane>(train, true);
                for (int i = 0; i < navigation.Length; i++)
                    lanes.Add(navigation[i].m_Lane);
            }
        }
        lanes.Remove(Entity.Null);
        return lanes;
    }
}

}
