using Colossal.UI.Binding;
using Game.Buildings;
using Game.UI.InGame;
using Unity.Entities;
using UnityEngine.Scripting;

namespace StraightTrainStop
{

/// <summary>
/// Carries the station panel in the info tab of stations that have no Visual Customisation, such
/// as the Eastern Europe stations. Where Visual Customisation exists the panel stays there and this
/// section is hidden. It is added after VisualCustomizeSection, the last of the game's middle
/// sections, so that section has already decided its visibility for the current selection.
/// The content comes from the bindings of <see cref="StationJunctionLockUISystem"/>.
/// </summary>
public sealed partial class StationTracksSection : InfoSectionBase
{
    private VisualCustomizeSection _visualCustomize = null!;

    protected override string group => nameof(StationTracksSection);

    [Preserve]
    protected override void OnCreate()
    {
        base.OnCreate();
        _visualCustomize = World.GetOrCreateSystemManaged<VisualCustomizeSection>();
        m_InfoUISystem.AddMiddleSection(this);
    }

    protected override void Reset()
    {
    }

    [Preserve]
    protected override void OnUpdate()
    {
        visible = !_visualCustomize.visible && IsRailwayStation(selectedEntity);
    }

    protected override void OnProcess()
    {
    }

    public override void OnWriteProperties(IJsonWriter writer)
    {
    }

    private bool IsRailwayStation(Entity entity)
    {
        if (entity == Entity.Null
            || !EntityManager.Exists(entity)
            || !EntityManager.HasComponent<TransportStation>(entity))
            return false;
        return StationLayout.IsRailwayStation(EntityManager, entity);
    }
}

}
