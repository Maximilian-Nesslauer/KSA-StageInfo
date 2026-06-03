using System;
using System.Collections.Generic;
using KSA;
using StageInfo.Analysis;

namespace StageInfo.UI;

internal enum StageDisplayMode
{
    Auto,
    Vac,
    Asl,
    VacAsl,
    Planning
}

/// <summary>
/// Panel UI state: display mode + body selection, plus the
/// <see cref="AnalysisEnvironment"/> resolver.
/// Label style: parens for single mode ((VAC)), brackets for dual ([VAC][ASL]).
/// </summary>
internal static class StageInfoSettings
{
    public static StageDisplayMode Mode = StageDisplayMode.Auto;
    public static string? SelectedBodyId;

    private static readonly List<Astronomical> _bodiesCache = new();
    private static CelestialSystem? _bodiesCacheSystem;

    public static AnalysisEnvironment ResolveEnvironment(Vehicle vehicle)
    {
        float currentPressure = vehicle.PhysicsEnvironment.AtmosphericPressure;
        bool inAtmosphere = currentPressure > 0f;

        return Mode switch
        {
            StageDisplayMode.Auto => new AnalysisEnvironment(
                PrimaryPressure: currentPressure,
                PrimarySurfaceGravity: null,
                SecondaryPressure: null,
                SecondarySurfaceGravity: null,
                PrimaryLabel: inAtmosphere ? "(ATM)" : "(VAC)",
                SecondaryLabel: null,
                IsPrimaryCurrentCondition: true),

            StageDisplayMode.Vac => new AnalysisEnvironment(
                PrimaryPressure: 0f,
                PrimarySurfaceGravity: null,
                SecondaryPressure: null,
                SecondarySurfaceGravity: null,
                PrimaryLabel: "(VAC)",
                SecondaryLabel: null,
                IsPrimaryCurrentCondition: !inAtmosphere),

            StageDisplayMode.Asl => new AnalysisEnvironment(
                PrimaryPressure: EnvironmentHelpers.GetSeaLevelPressure(vehicle.Parent),
                PrimarySurfaceGravity: null,
                SecondaryPressure: null,
                SecondarySurfaceGravity: null,
                PrimaryLabel: "(ASL)",
                SecondaryLabel: null,
                IsPrimaryCurrentCondition: false),

            StageDisplayMode.VacAsl => new AnalysisEnvironment(
                PrimaryPressure: 0f,
                PrimarySurfaceGravity: null,
                SecondaryPressure: EnvironmentHelpers.GetSeaLevelPressure(vehicle.Parent),
                SecondarySurfaceGravity: null,
                PrimaryLabel: "[VAC]",
                SecondaryLabel: "[ASL]",
                IsPrimaryCurrentCondition: !inAtmosphere),

            StageDisplayMode.Planning => ResolvePlanningEnvironment(vehicle),

            _ => throw new ArgumentOutOfRangeException(
                nameof(Mode), Mode, "Unhandled StageDisplayMode")
        };
    }

    /// <summary>Cached; rebuilds only on system change.</summary>
    public static List<Astronomical> GetCelestialBodies()
    {
        CelestialSystem? system = Universe.CurrentSystem;
        if (system == null)
        {
            if (_bodiesCacheSystem != null)
            {
                _bodiesCache.Clear();
                _bodiesCacheSystem = null;
            }
            return _bodiesCache;
        }

        if (ReferenceEquals(system, _bodiesCacheSystem))
            return _bodiesCache;

        _bodiesCacheSystem = system;
        _bodiesCache.Clear();
        foreach (Astronomical astro in system.All.AsSpan())
        {
            if (astro is Vehicle)
                continue;
            if (astro is IParentBody)
                _bodiesCache.Add(astro);
        }

        return _bodiesCache;
    }

    public static void Reset()
    {
        Mode = StageDisplayMode.Auto;
        SelectedBodyId = null;
        _bodiesCache.Clear();
        _bodiesCacheSystem = null;
    }

    private static AnalysisEnvironment ResolvePlanningEnvironment(Vehicle vehicle)
    {
        IParentBody? body = FindSelectedBody();
        if (body == null)
        {
            // Either SelectedBodyId is unset (first run, Universe wasn't ready
            // at mod load) or the saved id doesn't exist in the current system
            // (system change). Snap to the first available body so we never
            // silently fall through to VAC when planning is actually possible.
            var bodies = GetCelestialBodies();
            if (bodies.Count > 0)
            {
                SelectedBodyId = bodies[0].Id;
                body = bodies[0] as IParentBody;
            }
        }

        if (body == null)
        {
            return new AnalysisEnvironment(0f, null, null, null, "(VAC)", null, true);
        }

        float pressure = EnvironmentHelpers.GetSeaLevelPressure(body);
        float gravity = EnvironmentHelpers.ComputeSurfaceGravity(body);
        bool hasAtmosphere = pressure > 0f;
        string bodyName = (body as Astronomical)?.Id ?? "?";
        string label = hasAtmosphere
            ? $"({bodyName} ASL)"
            : $"({bodyName})";

        return new AnalysisEnvironment(
            PrimaryPressure: pressure,
            PrimarySurfaceGravity: gravity,
            SecondaryPressure: null,
            SecondarySurfaceGravity: null,
            PrimaryLabel: label,
            SecondaryLabel: null,
            IsPrimaryCurrentCondition: false);
    }

    // Linear scan of the already-filtered body list (vehicles excluded, only
    // IParentBody candidates). GetCelestialBodies() caches the list per
    // system, so the scan covers a small (~10-20 entry) list, not the full
    // Universe.CurrentSystem.All collection.
    private static IParentBody? FindSelectedBody()
    {
        if (SelectedBodyId == null)
            return null;

        foreach (Astronomical astro in GetCelestialBodies())
            if (astro.Id == SelectedBodyId)
                return (IParentBody)astro;

        return null;
    }
}
