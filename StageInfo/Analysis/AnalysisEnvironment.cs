using KSA;

namespace StageInfo.Analysis;

/// <summary>
/// dV-analysis inputs: ambient pressure and gravity, plus display labels.
/// Secondary is set only in VAC+ASL mode.
/// </summary>
internal readonly record struct AnalysisEnvironment(
    float PrimaryPressure,
    float? PrimarySurfaceGravity,
    float? SecondaryPressure,
    float? SecondarySurfaceGravity,
    string PrimaryLabel,
    string? SecondaryLabel,
    bool IsPrimaryCurrentCondition
);

internal static class EnvironmentHelpers
{
    public static float ComputeSurfaceGravity(IParentBody body)
    {
        double r = body.MeanRadius;
        if (r <= 0.0)
            return 0f;
        return (float)(Constants.GRAVITATIONAL_CONSTANT * body.Mass / (r * r));
    }

    public static float GetSeaLevelPressure(IParentBody? body)
    {
        if (body is Astronomical astro)
        {
            var atmo = astro.GetAtmosphereReference();
            if (atmo != null)
                return PaFromPressureReference(atmo.Physical.SeaLevelPressure);
        }
        return 0f;
    }

    // PressureReference has an implicit double op; C# can't chain implicit + explicit in one cast.
    private static float PaFromPressureReference(PressureReference p) => (float)(double)p;
}
