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

    private const string TestedGameVersion = "v2026.6.7.4631";

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

#if DEBUG
        // Anchor the first reporting interval at mod load, not at type init.
        PerfTracker.Reset();
#endif

        bool coreOk = GameReflection.ValidateCore();
        if (!coreOk)
        {
            DefaultCategory.Log.Warning(
                "[StageInfo] Disabled, Vehicle.UpdateFromTaskResults not found.");
            return;
        }

        bool panelOk = GameReflection.ValidatePanelTargets();
        bool burnOk = GameReflection.ValidateBurnTarget();

        // Always on: drives the analysis cache for the controlled vehicle.
        // The main-thread BurnDuration write inside is gated on the worker
        // patch having been registered below (else it would flicker against
        // the stock recompute).
        _harmony.CreateClassProcessor(typeof(Patch_CorrectedBurnDuration)).Patch();

        if (panelOk)
        {
            StageInfoPanel.ApplyPatches(_harmony);
        }
        else
        {
            DefaultCategory.Log.Warning(
                "[StageInfo] Panel disabled, StagingWindow targets not found.");
        }

        if (burnOk)
        {
            _harmony.Patch(GameReflection.FlightComputer_UpdateBurnTarget!,
                postfix: new HarmonyMethod(typeof(Patch_WorkerIgnitionTiming),
                    nameof(Patch_WorkerIgnitionTiming.Postfix)));
        }
        else
        {
            DefaultCategory.Log.Warning(
                "[StageInfo] FlightComputer.UpdateBurnTarget not found, burn duration correction disabled " +
                "(cache still drives the panel, but fc.Burn is not modified).");
        }

        bool editorOk = GameReflection.ValidateEditorTargets();
        if (editorOk)
        {
            EditorStageInfoPanel.ApplyPatches(_harmony);
        }
        else
        {
            DefaultCategory.Log.Warning(
                "[StageInfo] Editor panel disabled, VehicleEditingSpace targets not found.");
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
        EditorStageInfoPanel.Reset();
        DebugLoggingPatches.Reset();
        AnalysisCache.Reset();
        EditorAnalysisCache.Reset();
        StageInfoSettings.Reset();
        EditorStageInfoSettings.Reset();
        CorrectedBurnState.ClearBurn();
        SequenceAnalyzer.ResetPools();
        StageFuelAnalyzer.ResetPools();
        RcsAnalyzer.ResetPools();
#if DEBUG
        PerfTracker.Reset();
#endif
        DefaultCategory.Log.Info("[StageInfo] Unloaded.");
    }
}
