using System.Collections.Generic;
using KSA;
using StageInfo.Analysis;

namespace StageInfo.UI;

internal readonly record struct EditorEnvironment(
    float Pressure,
    float SurfaceGravity,
    string Label
);

internal static class EditorStageInfoSettings
{
    public static string? SelectedBodyId;
    public static bool UseVacuum = true;

    private static readonly List<Astronomical> _bodiesCache = new();
    private static CelestialSystem? _bodiesCacheSystem;

    public static EditorEnvironment ResolveEnvironment()
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
            return new EditorEnvironment(0f, 0f, "(VAC)");

        float gravity = EnvironmentHelpers.ComputeSurfaceGravity(body);
        string bodyName = (body as Astronomical)?.Id ?? "?";

        if (UseVacuum)
            return new EditorEnvironment(0f, gravity, "(VAC)");

        float pressure = EnvironmentHelpers.GetSeaLevelPressure(body);
        bool hasAtmosphere = pressure > 0f;
        string label = hasAtmosphere
            ? $"({bodyName} ASL)"
            : $"({bodyName})";

        return new EditorEnvironment(pressure, gravity, label);
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
        UseVacuum = true;
        _bodiesCache.Clear();
        _bodiesCacheSystem = null;
    }

    public static bool SelectedBodyHasAtmosphere()
    {
        IParentBody? body = FindSelectedBody();
        return body != null && EnvironmentHelpers.GetSeaLevelPressure(body) > 0f;
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
