using System.Text;
using Colossal.UI.Binding;
using Game.UI;
using Game.UI.InGame;
using Unity.Entities;
using UnityEngine.Scripting;

namespace StraightTrainStop
{

/// <summary>Bindings for the per-station panel shown under Visual Customisation.</summary>
public sealed partial class StationJunctionLockUISystem : UISystemBase
{
    private const string Group = "straightTrainStop";

    private SelectedInfoUISystem _selectedInfo = null!;
    private StationJunctionLockSystem _junctionLocks = null!;
    private ValueBinding<bool> _stationSelected = null!;
    private ValueBinding<string> _rows = null!;
    private ValueBinding<int> _removedCount = null!;
    private ValueBinding<string> _status = null!;
    private Entity _lastSelection;
    private int _lastRevision = -1;

    [Preserve]
    protected override void OnCreate()
    {
        base.OnCreate();
        _selectedInfo = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
        _junctionLocks = World.GetOrCreateSystemManaged<StationJunctionLockSystem>();
        AddBinding(_stationSelected = new ValueBinding<bool>(Group, "stationSelected", false));
        AddBinding(_rows = new ValueBinding<string>(Group, "tracks", "[]"));
        AddBinding(_removedCount = new ValueBinding<int>(Group, "removedCount", 0));
        AddBinding(_status = new ValueBinding<string>(Group, "junctionStatus", string.Empty));

        // One int keeps the binding to a single argument: row * 4 + side * 2 + (on ? 1 : 0).
        AddBinding(new TriggerBinding<int>(Group, "setTrackSide", code =>
        {
            int row = code / 4;
            int side = (code / 2) % 2;
            _junctionLocks.Request(_selectedInfo.selectedEntity, row, side, code % 2 == 1);
        }));
        AddBinding(new TriggerBinding<bool>(Group, "setAllTracks", enabled =>
            _junctionLocks.Request(_selectedInfo.selectedEntity, -1, 0, enabled)));
    }

    [Preserve]
    protected override void OnUpdate()
    {
        Entity selected = _selectedInfo.selectedEntity;
        int revision = _junctionLocks.Revision;
        if (selected == _lastSelection && revision == _lastRevision)
            return;

        _lastSelection = selected;
        _lastRevision = revision;
        StationLockStatus state = _junctionLocks.Inspect(selected);
        _stationSelected.Update(state.IsStation);
        _rows.Update(ToJson(state));
        _removedCount.Update(state.RemovedCount);

        string message = string.Empty;
        if (state.IsStation)
        {
            if (state.Rows.Count == 0)
                message = "No platform tracks were found.";
            else if (_junctionLocks.LastStation == selected)
                message = _junctionLocks.LastMessage;
        }
        _status.Update(message);
    }

    /// <summary>[{"l":true,"r":false,"lc":2,"rc":0}, ...], one entry per platform track.</summary>
    private static string ToJson(StationLockStatus state)
    {
        var json = new StringBuilder("[");
        for (int i = 0; i < state.Rows.Count; i++)
        {
            RowStatus row = state.Rows[i];
            if (i > 0)
                json.Append(',');
            json.Append("{\"l\":").Append(row.Left ? "true" : "false")
                .Append(",\"r\":").Append(row.Right ? "true" : "false")
                .Append(",\"lc\":").Append(row.LeftChanges)
                .Append(",\"rc\":").Append(row.RightChanges)
                .Append('}');
        }
        return json.Append(']').ToString();
    }
}

}
