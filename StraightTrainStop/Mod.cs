using Colossal.Logging;
using Game;
using Game.Modding;
using Game.Net;

namespace StraightTrainStop
{

public sealed class Mod : IMod
{
    internal static readonly ILog Log = LogManager
        .GetLogger(nameof(StraightTrainStop))
        .SetShowsErrorsInUI(false);

    public void OnLoad(UpdateSystem updateSystem)
    {
        Log.Info("Loading Straight Train Stop v1.0.0");

        // Runs right after LaneSystem has generated lanes and before LaneReferencesSystem files them
        // under their owners, so a removed crossover goes through the same path as any lane
        // LaneSystem itself discards.
        updateSystem.UpdateBefore<StationJunctionLockSystem, LaneReferencesSystem>(SystemUpdatePhase.Modification4B);
        updateSystem.UpdateAt<StationRegenerationSystem>(SystemUpdatePhase.Modification1);
        updateSystem.UpdateAt<StationJunctionLockUISystem>(SystemUpdatePhase.UIUpdate);
    }

    public void OnDispose()
    {
        Log.Info("Disposing Straight Train Stop");
    }
}
}
