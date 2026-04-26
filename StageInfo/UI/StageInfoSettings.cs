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
        float currentPressure = vehicle.LastKinematicStates.AtmosphericPressure;
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
        // Default selection on first resolve so the combo has a sensible initial value.
        if (SelectedBodyId == null)
        {
            var bodies = GetCelestialBodies();
            if (bodies.Count > 0)
                SelectedBodyId = bodies[0].Id;
        }

        IParentBody? body = FindSelectedBody();
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

    private static IParentBody? FindSelectedBody()
    {
        if (SelectedBodyId == null || Universe.CurrentSystem == null)
            return null;

        foreach (Astronomical astro in Universe.CurrentSystem.All.AsSpan())
        {
            if (astro is Vehicle)
                continue;
            if (astro is IParentBody body && astro.Id == SelectedBodyId)
                return body;
        }

        return null;
    }
}
