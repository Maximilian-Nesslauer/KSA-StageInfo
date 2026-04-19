using System.Reflection;
using Brutal.Logging;
using HarmonyLib;
using KSA;

namespace StageInfo.Core;

/// <summary>
/// Non-public game internals. Resolved once at load; ValidateAll flags missing
/// targets. Public APIs use plain [HarmonyPatch] instead.
/// </summary>
static class GameReflection
{
    public static readonly Type? StagingWindowType =
        typeof(Staging).GetNestedType("StagingWindow", BindingFlags.NonPublic);

    public static readonly MethodInfo? StagingWindow_DrawContent =
        StagingWindowType?.GetMethod("DrawContent", BindingFlags.Public | BindingFlags.Instance);

    // Open generic; features instantiate it per module type.
    public static readonly MethodInfo? StagingWindow_DrawComponentOpen =
        StagingWindowType?.GetMethod("DrawComponent", BindingFlags.NonPublic | BindingFlags.Instance);

    public static readonly MethodInfo? FlightComputer_UpdateBurnTarget =
        AccessTools.Method(typeof(FlightComputer), "UpdateBurnTarget");

    public static bool ValidateAll()
    {
        var targets = new (string name, object? target)[]
        {
            ("Staging.StagingWindow",          StagingWindowType),
            ("StagingWindow.DrawContent",      StagingWindow_DrawContent),
            ("StagingWindow.DrawComponent<T>", StagingWindow_DrawComponentOpen),
        };

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
