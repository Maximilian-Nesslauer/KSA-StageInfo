using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using KSA;
using StageInfo.Analysis;
using StageInfo.Core;

namespace StageInfo.Patches;

/// <summary>
/// Handoff from the main-thread writer to the worker-thread reader for the
/// corrected burn duration and ignition time.
///
/// One-frame staleness is fine because dV consumed per frame is tiny.
/// </summary>
internal static class CorrectedBurnState
{
    // FC's copy ctor shares the Burn reference, so ReferenceEquals on BurnTarget
    // uniquely identifies the controlled vehicle's FC on the worker thread.
    internal static volatile BurnTarget? TrackedBurn;

    internal static volatile float CorrectedDuration;

    private static double _correctedIgnitionTimeSeconds;

    internal static SimTime CorrectedIgnitionTime
    {
        get => new SimTime(Volatile.Read(ref _correctedIgnitionTimeSeconds));
        set => Volatile.Write(ref _correctedIgnitionTimeSeconds, value.Seconds());
    }

    // False when the worker patch didn't apply; we then suppress main-thread
    // writes to fc.Burn to avoid single-frame flicker from the stock recompute.
    internal static bool WorkerFixEnabled;

    internal static void Clear()
    {
        TrackedBurn = null;
        CorrectedDuration = 0f;
        Volatile.Write(ref _correctedIgnitionTimeSeconds, 0.0);
    }
}

/// <summary>
/// Drives the analysis cache and writes the corrected BurnDuration /
/// IgnitionTime for the controlled vehicle.
/// </summary>
[HarmonyPatch(typeof(Vehicle), "UpdateFromTaskResults")]
internal static class Patch_CorrectedBurnDuration
{
    static void Postfix(Vehicle __instance)
    {
#if DEBUG
        long perfStart = DebugConfig.Performance ? Stopwatch.GetTimestamp() : 0;
#endif
        if (__instance != Program.ControlledVehicle) return;

        AnalysisCache.Update(__instance);

        FlightComputer fc = __instance.FlightComputer;
        if (fc.Burn == null)
        {
            CorrectedBurnState.Clear();
            return;
        }

        float? corrected = AnalysisCache.GetCorrectedBurnDuration();
        if (corrected == null || corrected.Value <= 0f)
        {
            CorrectedBurnState.Clear();
            return;
        }

        // Without the worker patch, the next tick's stock UpdateBurnTarget
        // recomputes BurnDuration and we'd flicker - suppress the write.
        if (CorrectedBurnState.WorkerFixEnabled)
        {
            SimTime ignitionTime = fc.Burn.ImpulsiveInstant - 0.5 * (double)corrected.Value;

            fc.Burn.BurnDuration = corrected.Value;
            fc.Burn.IgnitionTime = ignitionTime;

            CorrectedBurnState.CorrectedDuration = corrected.Value;
            CorrectedBurnState.CorrectedIgnitionTime = ignitionTime;
            CorrectedBurnState.TrackedBurn = fc.Burn;
        }

#if DEBUG
        if (DebugConfig.Performance)
            PerfTracker.Record("Patch_CorrectedBurnDuration.Postfix",
                Stopwatch.GetTimestamp() - perfStart);
#endif
    }
}

/// <summary>
/// Worker-thread postfix on the private FlightComputer.UpdateBurnTarget.
/// Replaces stock single-stage BurnDuration with the multi-sequence value
/// so auto-burn IgnitionTime leads the impulsive instant correctly.
/// Auto mode only; Manual keeps the throttle-adjusted stock duration.
/// </summary>
internal static class Patch_WorkerIgnitionTiming
{
    public static void Postfix(FlightComputer __instance)
    {
        if (__instance.BurnMode != FlightComputerBurnMode.Auto) return;

        BurnTarget? burn = __instance.Burn;
        if (burn == null) return;

        BurnTarget? tracked = CorrectedBurnState.TrackedBurn;
        if (tracked == null || !ReferenceEquals(burn, tracked)) return;

        float duration = CorrectedBurnState.CorrectedDuration;
        if (duration <= 0f) return;

        burn.BurnDuration = duration;
        burn.IgnitionTime = CorrectedBurnState.CorrectedIgnitionTime;
    }
}
