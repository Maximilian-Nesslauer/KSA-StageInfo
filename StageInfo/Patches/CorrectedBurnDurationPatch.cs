using System.Diagnostics;
using HarmonyLib;
using KSA;
using StageInfo.Analysis;
using StageInfo.Core;

namespace StageInfo.Patches;

/// <summary>
/// Main-thread writer <-> worker-thread reader handoff for the corrected
/// burn duration. Volatile fields pin the visibility barrier; one-frame
/// staleness is fine because dV consumed per frame is tiny.
/// </summary>
static class CorrectedBurnState
{
    // FC's copy ctor shares the Burn reference, so ReferenceEquals on BurnTarget
    // uniquely identifies the controlled vehicle's FC on the worker thread.
    public static volatile BurnTarget? TrackedBurn;

    // Aligned float reads/writes are atomic on x64; volatile pins visibility.
    public static volatile float CorrectedDuration;

    // False when the worker patch didn't apply; we then suppress main-thread
    // writes to fc.Burn to avoid single-frame flicker from the stock recompute.
    public static bool WorkerFixEnabled;

    public static void Clear()
    {
        TrackedBurn = null;
        CorrectedDuration = 0f;
    }
}

/// <summary>
/// Drives the analysis cache and writes the corrected BurnDuration /
/// IgnitionTime for the controlled vehicle.
/// </summary>
[HarmonyPatch(typeof(Vehicle), "UpdateFromTaskResults")]
static class Patch_CorrectedBurnDuration
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
            fc.Burn.BurnDuration = corrected.Value;
            fc.Burn.IgnitionTime = fc.Burn.ImpulsiveInstant - 0.5 * (double)fc.Burn.BurnDuration;

            CorrectedBurnState.CorrectedDuration = corrected.Value;
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
static class Patch_WorkerIgnitionTiming
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
        burn.IgnitionTime = burn.ImpulsiveInstant - 0.5 * (double)duration;
    }
}
