using System.Reflection;
using Brutal.Logging;
using HarmonyLib;
using KSA;

namespace StageInfo.Core;

/// <summary>
/// Non-public game internals. Resolved once at load; ValidatePanelTargets and
/// ValidateBurnTarget flag missing entries so each feature can degrade
/// independently. Public APIs use plain [HarmonyPatch] instead.
/// </summary>
internal static class GameReflection
{
    public static readonly Type? StagingWindowType =
        typeof(Staging).GetNestedType("StagingWindow", BindingFlags.NonPublic);

    public static readonly MethodInfo? StagingWindow_DrawContent =
        StagingWindowType?.GetMethod("DrawContent", BindingFlags.Public | BindingFlags.Instance);

    // Open generic; features instantiate it per module type.
    public static readonly MethodInfo? StagingWindow_DrawComponentOpen =
        StagingWindowType?.GetMethod("DrawComponent", BindingFlags.NonPublic | BindingFlags.Instance);

    public static readonly MethodInfo? FlightComputer_UpdateBurnTarget =
        AccessTools.Method(typeof(FlightComputer), "UpdateBurnTarget", new[]
        {
            typeof(ManualControlInputs).MakeByRefType(),
            typeof(FlightComputerOutput).MakeByRefType(),
        });

    public static bool ValidatePanelTargets()
    {
        var targets = new (string name, object? target)[]
        {
            ("Staging.StagingWindow",          StagingWindowType),
            ("StagingWindow.DrawContent",      StagingWindow_DrawContent),
            ("StagingWindow.DrawComponent<T>", StagingWindow_DrawComponentOpen),
        };
        return AllPresent(targets);
    }

    public static bool ValidateBurnTarget()
    {
        var targets = new (string name, object? target)[]
        {
            ("FlightComputer.UpdateBurnTarget", FlightComputer_UpdateBurnTarget),
        };
        return AllPresent(targets);
    }

    private static bool AllPresent((string name, object? target)[] targets)
    {
        bool allOk = true;
        foreach (var (name, target) in targets)
        {
            if (target == null)
            {
                DefaultCategory.Log.Error(
                    $"[StageInfo] {name} not found - game version may have changed.");
                allOk = false;
            }
        }
        return allOk;
    }
}
