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
public sealed class Mod
{
    private static Harmony? _harmony;

    private const string TestedGameVersion = "v2026.4.17.4184";

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

        bool panelOk = GameReflection.ValidatePanelTargets();
        bool burnOk = GameReflection.ValidateBurnTarget();

        if (panelOk)
        {
            StageInfoPanel.ApplyPatches(_harmony);
        }
        else
        {
            DefaultCategory.Log.Warning(
                "[StageInfo] Panel disabled - StagingWindow targets not found.");
        }

        // Drives the cache for the controlled vehicle; writes corrected BurnDuration
        // only if the worker patch below is active (else it would flicker).
        _harmony.CreateClassProcessor(typeof(Patch_CorrectedBurnDuration)).Patch();

        if (burnOk)
        {
            _harmony.Patch(GameReflection.FlightComputer_UpdateBurnTarget!,
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

#if DEBUG
        // Verbose analyzer logging duplicates AnalysisCache work for the same
        // tick; gated behind DEBUG so Release never pays for the redundancy.
        _harmony.CreateClassProcessor(typeof(DebugLoggingPatches.Patch_AnalyzeAfterStaging)).Patch();
        _harmony.CreateClassProcessor(typeof(DebugLoggingPatches.Patch_InitialAnalysis)).Patch();
#endif

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
