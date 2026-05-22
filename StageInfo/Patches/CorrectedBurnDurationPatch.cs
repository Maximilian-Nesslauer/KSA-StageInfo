using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using KSA;
using StageInfo.Analysis;
using StageInfo.Core;

namespace StageInfo.Patches;

/// <summary>
/// Handoff from the main-thread writer to the worker-thread reader for the
/// corrected burn duration. The worker recomputes IgnitionTime from the burn's
/// current ImpulsiveInstant, so a maneuver-node edit between main snapshot
/// and worker apply doesn't write a stale ignition.
///
/// One-frame staleness on duration is fine because dV consumed per frame is tiny.
/// </summary>
internal static class CorrectedBurnState
{
    // FC's copy ctor shares the Burn reference, so ReferenceEquals on BurnTarget
    // uniquely identifies the controlled vehicle's FC on the worker thread.
    internal static volatile BurnTarget? TrackedBurn;

    internal static volatile float CorrectedDuration;

    internal static void ClearBurn()
    {
        TrackedBurn = null;
        CorrectedDuration = 0f;
    }
}

/// <summary>
/// Drives the analysis cache and writes the corrected BurnDuration /
/// IgnitionTime for the controlled vehicle.
/// </summary>
[HarmonyPatch]
internal static class Patch_CorrectedBurnDuration
{
    static MethodBase TargetMethod() => GameReflection.Vehicle_UpdateFromTaskResults!;

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
            CorrectedBurnState.ClearBurn();
            return;
        }

        float? corrected = AnalysisCache.GetCorrectedBurnDuration();
        if (corrected == null || corrected.Value <= 0f)
        {
            CorrectedBurnState.ClearBurn();
            return;
        }

        // Without the worker patch we skip the entire write: the next tick's
        // stock UpdateBurnTarget would recompute BurnDuration and we'd flicker.
        // Mod.OnFullyLoaded registers the worker patch iff this reflection
        // target resolved, so the null check stands in for "patch registered".
        if (GameReflection.FlightComputer_UpdateBurnTarget != null)
        {
            CorrectedBurnState.CorrectedDuration = corrected.Value;
            CorrectedBurnState.TrackedBurn = fc.Burn;

            // Auto only: in Manual mode, stock divides BurnDuration by EngineThrottle,
            // so our full-throttle correction would flicker against the stock recompute.
            if (fc.BurnMode == FlightComputerBurnMode.Auto)
            {
                fc.Burn.BurnDuration = corrected.Value;
                fc.Burn.IgnitionTime = fc.Burn.ImpulsiveInstant - 0.5 * (double)corrected.Value;
            }
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
        // Recompute from the burn's current ImpulsiveInstant so a maneuver-node
        // edit between the main-thread snapshot and this worker call doesn't
        // write a stale IgnitionTime. Mirrors stock's UpdateBurnTarget formula.
        burn.IgnitionTime = burn.ImpulsiveInstant - 0.5 * (double)duration;
    }
}
