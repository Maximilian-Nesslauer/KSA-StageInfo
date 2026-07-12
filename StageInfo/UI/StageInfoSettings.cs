using System;
using System.Collections.Generic;
using KSA;
using StageInfo.Analysis;

namespace StageInfo.UI;

internal enum StageDisplayMode
{
    // The resting state: every sequence follows its own Vac/Atm toggle, the
    // active sequence its Amb/Vac/Atm override. The VAC and ASL dropdown
    // entries are presets that write those toggles and land back here.
    Custom,
    VacAsl,
    Planning
}

/// <summary>
/// Panel UI state: display mode, the active-sequence environment override,
/// body selection, plus the <see cref="AnalysisEnvironment"/> resolver.
/// Label style: parens for single mode ((VAC)), brackets for dual ([VAC][ASL]).
/// </summary>
internal static class StageInfoSettings
{
    public static StageDisplayMode Mode = StageDisplayMode.Custom;
    public static ActiveSequenceEnv ActiveSequenceOverride = ActiveSequenceEnv.Ambient;
    public static string? SelectedBodyId;

    // The override belongs to one specific active sequence; on staging or a
    // vehicle switch it resets to Ambient rather than silently carrying over.
    // Tracked by Sequence object identity, not number: stock renumbers on
    // staging (SequenceList.Remove shifts every following Number down and
    // SetActiveSequence follows), so the active NUMBER often stays the same
    // across a staging event while the sequence it names is a different one.
    private static Vehicle? _overrideVehicle;
    private static Sequence? _overrideActiveSequence;

    private static readonly List<Astronomical> _bodiesCache = new();
    private static CelestialSystem? _bodiesCacheSystem;

    public static AnalysisEnvironment ResolveEnvironment(Vehicle vehicle)
    {
        ResetOverrideIfStale(vehicle);

        float currentPressure = vehicle.PhysicsEnvironment.AtmosphericPressure;
        bool inAtmosphere = currentPressure > 0f;

        return Mode switch
        {
            StageDisplayMode.Custom => ResolveCustomEnvironment(vehicle, currentPressure),

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

    // Custom mode: each sequence is evaluated at its own stock Vac/Atm toggle,
    // the active sequence per ActiveSequenceOverride (Ambient by default).
    // PrimaryPressure carries the true ambient (used for the Ambient override
    // and the RCS analysis); PrimaryPerSequence carries the sea-level pressure
    // Atm-toggled sequences use, the active sequence number, and the override.
    // The (VAC)/(ATM)/(mixed) label is derived from the analyzed rows in
    // AnalysisCache.Update, so it is left empty here.
    private static AnalysisEnvironment ResolveCustomEnvironment(Vehicle vehicle, float ambientPressure)
    {
        float seaLevel = EnvironmentHelpers.GetSeaLevelPressure(vehicle.Parent);
        int activeSequence = vehicle.Parts.SequenceList.ActiveSequence;

        return new AnalysisEnvironment(
            PrimaryPressure: ambientPressure,
            PrimarySurfaceGravity: null,
            SecondaryPressure: null,
            SecondarySurfaceGravity: null,
            PrimaryLabel: "",
            SecondaryLabel: null,
            IsPrimaryCurrentCondition: true,
            PrimaryPerSequence: new PerSequenceEnv(seaLevel, activeSequence, ActiveSequenceOverride));
    }

    private static void ResetOverrideIfStale(Vehicle vehicle)
    {
        Sequence? activeSequence = FindActiveSequence(vehicle);
        if (!ReferenceEquals(vehicle, _overrideVehicle)
            || !ReferenceEquals(activeSequence, _overrideActiveSequence))
        {
            ActiveSequenceOverride = ActiveSequenceEnv.Ambient;
            _overrideVehicle = vehicle;
            _overrideActiveSequence = activeSequence;
        }
    }

    private static Sequence? FindActiveSequence(Vehicle vehicle)
    {
        SequenceList list = vehicle.Parts.SequenceList;
        ReadOnlySpan<Sequence> sequences = list.Sequences;
        for (int i = 0; i < sequences.Length; i++)
        {
            if (sequences[i].Number == list.ActiveSequence)
                return sequences[i];
        }
        return null;
    }

    /// <summary>
    /// Writes every sequence's stock Vac/Atm toggle (and the active-sequence
    /// override) to one uniform environment and returns to Custom, which then
    /// derives the matching VAC / ASL dropdown label. The toggle writes are
    /// buffered like stock's own button, so they apply in the next input pass
    /// and the label follows one frame later.
    /// </summary>
    public static void ApplyUniformPreset(Vehicle vehicle, bool atmospheric)
    {
        PerformanceEnvironment environment = atmospheric
            ? PerformanceEnvironment.Atmospheric
            : PerformanceEnvironment.Vacuum;
        ReadOnlySpan<Sequence> sequences = vehicle.Parts.SequenceList.Sequences;
        for (int i = 0; i < sequences.Length; i++)
            StageInfoUiHelpers.EnqueueSetSequenceEnvironment(vehicle.Parts, sequences[i], environment);
        ActiveSequenceOverride = atmospheric
            ? ActiveSequenceEnv.Atmospheric
            : ActiveSequenceEnv.Vacuum;
        Mode = StageDisplayMode.Custom;
    }

    /// <summary>
    /// Dropdown label for the Custom state, derived from the visible toggles:
    /// "VAC" when every sequence toggle is Vac (and the active override is Vac,
    /// or Ambient while actually in vacuum), "ASL" when everything is Atm, else
    /// "Custom". An Ambient active sequence inside an atmosphere is neither
    /// vacuum nor sea level, so it reads as Custom.
    /// </summary>
    public static string DeriveCustomLabel(Vehicle vehicle)
    {
        ReadOnlySpan<Sequence> sequences = vehicle.Parts.SequenceList.Sequences;
        if (sequences.IsEmpty)
            return "Custom";

        int activeSequence = vehicle.Parts.SequenceList.ActiveSequence;
        bool inAtmosphere = vehicle.PhysicsEnvironment.AtmosphericPressure > 0f;
        bool allVac = true;
        bool allAsl = true;

        for (int i = 0; i < sequences.Length; i++)
        {
            Sequence sequence = sequences[i];
            if (sequence.Number == activeSequence)
            {
                switch (ActiveSequenceOverride)
                {
                    case ActiveSequenceEnv.Vacuum:
                        allAsl = false;
                        break;
                    case ActiveSequenceEnv.Atmospheric:
                        allVac = false;
                        break;
                    default:
                        allAsl = false;
                        if (inAtmosphere)
                            allVac = false;
                        break;
                }
            }
            else if (sequence.Environment == PerformanceEnvironment.Atmospheric)
            {
                allVac = false;
            }
            else
            {
                allAsl = false;
            }
        }

        if (allVac)
            return "VAC";
        if (allAsl)
            return "ASL";
        return "Custom";
    }

    /// <summary>
    /// Called (via the Sequence.Environment setter patch, filtered to the
    /// controlled vehicle) when a Vac/Atm toggle actually changes. A toggle
    /// click while a uniform what-if mode is shown returns the panel to the
    /// per-sequence view, so the click has a visible effect.
    /// </summary>
    public static void NotifySequenceEnvironmentChanged()
    {
        if (Mode == StageDisplayMode.VacAsl || Mode == StageDisplayMode.Planning)
            Mode = StageDisplayMode.Custom;
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
            // Stars are IParentBody but TWR / sea-level pressure against a star
            // is meaningless as a launch reference, so they're left out.
            if (astro.IsStar())
                continue;
            if (astro is IParentBody)
                _bodiesCache.Add(astro);
        }

        return _bodiesCache;
    }

    public static void Reset()
    {
        Mode = StageDisplayMode.Custom;
        ActiveSequenceOverride = ActiveSequenceEnv.Ambient;
        _overrideVehicle = null;
        _overrideActiveSequence = null;
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
