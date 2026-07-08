using System.Reflection;
using Brutal.Logging;
using HarmonyLib;
using KSA;

namespace StageInfo.Core;

/// <summary>
/// Non-public game internals. Resolved once at load; ValidateCore and
/// ValidateBurnTarget flag missing entries so each feature can degrade
/// independently. Public APIs use plain Harmony patches instead.
/// </summary>
internal static class GameReflection
{
    // Parameter types pinned so a future overload doesn't make AccessTools.Method
    // throw AmbiguousMatchException out of this type initializer, which would
    // bypass ValidateCore's graceful disable path.
    public static readonly MethodInfo? Vehicle_UpdateFromTaskResults =
        AccessTools.Method(typeof(Vehicle), "UpdateFromTaskResults", new[]
        {
            typeof(VehicleUpdateData).MakeByRefType(),
            typeof(BubbleOrigin).MakeByRefType(),
            typeof(Vehicle),
            typeof(ReadOnlySpan<Vehicle>),
            typeof(Brutal.Numerics.double3),
            typeof(Brutal.Numerics.double3),
        });

    public static readonly MethodInfo? FlightComputer_UpdateBurnTarget =
        AccessTools.Method(typeof(FlightComputer), "UpdateBurnTarget", new[]
        {
            typeof(ManualControlInputs).MakeByRefType(),
            typeof(FlightComputerOutput).MakeByRefType(),
        });

    // The flight staging window is a private nested type; DrawContent runs
    // inside the window's ImGui Begin/End, so a prefix there can set a better
    // default window size. Optional: if missing, sizing is skipped.
    public static readonly Type? ResourceGroupsWindowType =
        typeof(ResourceGroups).GetNestedType("ResourceGroupsWindow", BindingFlags.NonPublic);

    public static readonly MethodInfo? ResourceGroupsWindow_DrawContent =
        ResourceGroupsWindowType?.GetMethod("DrawContent",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null, types: new[] { typeof(Viewport) }, modifiers: null);

    public static bool ValidateCore()
    {
        var targets = new (string name, object? target)[]
        {
            ("Vehicle.UpdateFromTaskResults", Vehicle_UpdateFromTaskResults),
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
                    $"[StageInfo] {name} not found, game version may have changed.");
                allOk = false;
            }
        }
        return allOk;
    }
}
