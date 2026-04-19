using Brutal.Logging;
using HarmonyLib;
using KSA;
using StageInfo.Analysis;
using StageInfo.Core;
using StageInfo.Patches;
using StageInfo.UI;
using StarMap.API;

namespace StageInfo;

[StarMapMod]
public class Mod
{
    private static Harmony? _harmony;

    private const string TestedGameVersion = "v2026.4.15.4141";

    public static bool DebugMode => DebugConfig.Any;

    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        string gameVersion = VersionInfo.Current.VersionString;
        DefaultCategory.Log.Info($"[StageInfo] Game version: {gameVersion}");
        if (gameVersion != TestedGameVersion)
            DefaultCategory.Log.Warning(
                $"[StageInfo] Tested against {TestedGameVersion}, current is {gameVersion}. " +
                "Some features may not work correctly.");

        _harmony = new Harmony("com.maxi.stageinfo");

        if (!GameReflection.ValidateAll())
        {
            DefaultCategory.Log.Warning("[StageInfo] Disabled - reflection targets not found.");
            return;
        }

        StageInfoPanel.ApplyPatches(_harmony);

        // Drives the cache for the controlled vehicle; writes corrected BurnDuration
        // only if the worker patch below is active (else it would flicker).
        _harmony.CreateClassProcessor(typeof(Patch_CorrectedBurnDuration)).Patch();

        if (GameReflection.FlightComputer_UpdateBurnTarget != null)
        {
            _harmony.Patch(GameReflection.FlightComputer_UpdateBurnTarget,
                postfix: new HarmonyMethod(typeof(Patch_WorkerIgnitionTiming),
                    nameof(Patch_WorkerIgnitionTiming.Postfix)));
            CorrectedBurnState.WorkerFixEnabled = true;
        }
        else
        {
            CorrectedBurnState.WorkerFixEnabled = false;
            DefaultCategory.Log.Warning(
                "[StageInfo] FlightComputer.UpdateBurnTarget not found - burn duration correction disabled " +
                "(cache still drives the panel, but fc.Burn is not modified).");
        }

        _harmony.CreateClassProcessor(typeof(DebugLoggingPatches.Patch_AnalyzeAfterStaging)).Patch();
        _harmony.CreateClassProcessor(typeof(DebugLoggingPatches.Patch_InitialAnalysis)).Patch();

        DefaultCategory.Log.Info("[StageInfo] Loaded and patched.");
    }

    [StarMapUnload]
    public void Unload()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;

        StageInfoPanel.Reset();
        DebugLoggingPatches.Reset();
        AnalysisCache.Reset();
        StageInfoSettings.Reset();
        CorrectedBurnState.Clear();
        CorrectedBurnState.WorkerFixEnabled = false;
        SequenceAnalyzer.ResetPools();
        StageFuelAnalyzer.ResetPools();
#if DEBUG
        PerfTracker.Reset();
#endif
        DefaultCategory.Log.Info("[StageInfo] Unloaded.");
    }
}
