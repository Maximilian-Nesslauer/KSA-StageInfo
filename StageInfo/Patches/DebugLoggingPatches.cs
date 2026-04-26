using Brutal.Logging;
using HarmonyLib;
using KSA;
using StageInfo.Analysis;
using StageInfo.Core;

namespace StageInfo.Patches;

/// <summary>
/// Logged analyzer passes on interesting events: after each staging, and
/// once per newly-controlled vehicle. The analyzer call duplicates the
/// AnalysisCache pass on the same tick. acceptable because both sites are
/// guarded by DebugConfig.StageInfo and only run in DEBUG builds.
/// </summary>
internal static class DebugLoggingPatches
{
    private static string? _lastVehicleId;

    internal static void Reset()
    {
        _lastVehicleId = null;
    }

    [HarmonyPatch(typeof(SequenceList), nameof(SequenceList.ActivateNextSequence))]
    internal static class Patch_AnalyzeAfterStaging
    {
        static void Postfix(Vehicle vehicle)
        {
            if (!DebugConfig.StageInfo) return;
            if (vehicle != Program.ControlledVehicle) return;

            DefaultCategory.Log.Info("[StageInfo] Staging event detected, running SequenceAnalyzer...");
            SequenceAnalyzer.Analyze(vehicle, log: true);
        }
    }

    [HarmonyPatch(typeof(Vehicle), "UpdateFromTaskResults")]
    internal static class Patch_InitialAnalysis
    {
        static void Postfix(Vehicle __instance)
        {
            if (!DebugConfig.StageInfo) return;
            if (__instance != Program.ControlledVehicle) return;

            string vehicleId = __instance.Id;
            if (vehicleId == _lastVehicleId)
                return;

            _lastVehicleId = vehicleId;

            DefaultCategory.Log.Info(
                $"[StageInfo] Vehicle '{vehicleId}' detected, running initial SequenceAnalyzer...");
            SequenceAnalyzer.Analyze(__instance, log: true);
        }
    }
}
