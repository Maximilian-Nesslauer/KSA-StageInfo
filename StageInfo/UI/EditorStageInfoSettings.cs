using System;
using System.Collections.Generic;
using KSA;
using StageInfo.Analysis;

namespace StageInfo.UI;

internal readonly record struct EditorEnvironment(
    float Pressure,
    float SurfaceGravity,
    PerSequenceEnv PerSequence
);

/// <summary>
/// Editor panel state: body selection plus the environment resolver. The
/// editor always evaluates per-sequence (each sequence at its own stock
/// Vac/Atm toggle against the selected body); the VAC / ASL dropdown entries
/// are presets that write those toggles.
/// </summary>
internal static class EditorStageInfoSettings
{
    public static string? SelectedBodyId;

    private static readonly List<Astronomical> _bodiesCache = new();
    private static CelestialSystem? _bodiesCacheSystem;

    public static EditorEnvironment ResolveEnvironment(PartTree parts)
    {
        IParentBody? body = FindSelectedBody();
        if (body == null)
        {
            var bodies = GetCelestialBodies();
            if (bodies.Count > 0)
            {
                SelectedBodyId = bodies[0].Id;
                body = bodies[0] as IParentBody;
            }
        }

        if (body == null)
            return new EditorEnvironment(0f, 0f, new PerSequenceEnv(0f, -1));

        float gravity = EnvironmentHelpers.ComputeSurfaceGravity(body);
        float seaLevel = EnvironmentHelpers.GetSeaLevelPressure(body);

        // Each sequence at its own Vac/Atm toggle (Vac -> 0, Atm -> the selected
        // body's sea level). No sequence is active in the editor, so
        // ActiveSequenceNumber is -1. Pressure feeds the vehicle-wide RCS
        // analysis: vacuum when every toggle is Vac, otherwise the body's
        // surface, matching the sequences' dominant environment.
        float rcsPressure = AllSequencesVacuum(parts) ? 0f : seaLevel;
        return new EditorEnvironment(rcsPressure, gravity, new PerSequenceEnv(seaLevel, -1));
    }

    /// <summary>
    /// Writes every sequence's stock Vac/Atm toggle to one uniform environment.
    /// Buffered like stock's own button, so the toggles apply in the next input
    /// pass and the derived label follows one frame later.
    /// </summary>
    public static void ApplyUniformPreset(PartTree parts, bool atmospheric)
    {
        PerformanceEnvironment environment = atmospheric
            ? PerformanceEnvironment.Atmospheric
            : PerformanceEnvironment.Vacuum;
        ReadOnlySpan<Sequence> sequences = parts.SequenceList.Sequences;
        for (int i = 0; i < sequences.Length; i++)
            StageInfoUiHelpers.EnqueueSetSequenceEnvironment(parts, sequences[i], environment);
    }

    /// <summary>
    /// Dropdown label derived from the visible toggles: "VAC" when every
    /// sequence toggle is Vac, "ASL" when every one is Atm, else "Custom".
    /// </summary>
    public static string DeriveCustomLabel(PartTree parts)
    {
        ReadOnlySpan<Sequence> sequences = parts.SequenceList.Sequences;
        if (sequences.IsEmpty)
            return "Custom";

        bool anyVac = false;
        bool anyAtm = false;
        for (int i = 0; i < sequences.Length; i++)
        {
            if (sequences[i].Environment == PerformanceEnvironment.Atmospheric)
                anyAtm = true;
            else
                anyVac = true;
        }

        if (anyVac && anyAtm)
            return "Custom";
        return anyAtm ? "ASL" : "VAC";
    }

    private static bool AllSequencesVacuum(PartTree parts)
    {
        ReadOnlySpan<Sequence> sequences = parts.SequenceList.Sequences;
        for (int i = 0; i < sequences.Length; i++)
        {
            if (sequences[i].Environment == PerformanceEnvironment.Atmospheric)
                return false;
        }
        return true;
    }

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
        SelectedBodyId = null;
        _bodiesCache.Clear();
        _bodiesCacheSystem = null;
    }

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
