using KSA;

namespace StageInfo.Analysis;

/// <summary>
/// Environment of the flight-active sequence. Ambient (the default) evaluates
/// it at the analyzer's ambientPressure argument, i.e. the vehicle's true
/// surroundings; Vacuum and Atmospheric override that with the same fixed
/// conditions the stock Vac/Atm toggle gives every other sequence.
/// </summary>
internal enum ActiveSequenceEnv
{
    Ambient,
    Vacuum,
    Atmospheric
}

/// <summary>
/// Per-sequence environment for the Custom mode: each sequence is evaluated at
/// its own stock Vac/Atm toggle (Vac -> 0, Atm -> <see cref="SeaLevelPressure"/>),
/// except the flight-active sequence which follows
/// <see cref="ActiveEnvironment"/>. <see cref="ActiveSequenceNumber"/> is -1 in
/// the editor, where no sequence is active.
/// </summary>
internal readonly record struct PerSequenceEnv(
    float SeaLevelPressure,
    int ActiveSequenceNumber,
    ActiveSequenceEnv ActiveEnvironment = ActiveSequenceEnv.Ambient
);

/// <summary>
/// dV-analysis inputs: ambient pressure and gravity, plus display labels.
/// Secondary is set only in VAC+ASL mode. <see cref="PrimaryPerSequence"/> is set
/// only in Custom mode, where PrimaryPressure carries the active-sequence ambient.
/// </summary>
internal readonly record struct AnalysisEnvironment(
    float PrimaryPressure,
    float? PrimarySurfaceGravity,
    float? SecondaryPressure,
    float? SecondarySurfaceGravity,
    string PrimaryLabel,
    string? SecondaryLabel,
    bool IsPrimaryCurrentCondition,
    PerSequenceEnv? PrimaryPerSequence = null
);

internal static class EnvironmentHelpers
{
    public static float ComputeSurfaceGravity(IParentBody? body)
    {
        if (body == null)
            return 0f;
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

    /// <summary>
    /// (VAC) if every burning sequence was evaluated in vacuum, (ATM) if every one
    /// was atmospheric, (mixed) otherwise. Reads the analyzer's own per-row
    /// Atmospheric flag so the Custom-mode label always matches the sequences that
    /// actually produced a row (engine-less / zero-flow sequences are already
    /// excluded from the analysis).
    /// </summary>
    public static string AtmosphericLabel(VehicleBurnAnalysis? analysis)
    {
        bool anyVac = false;
        bool anyAtm = false;
        if (analysis != null)
        {
            foreach (SequenceBurnInfo s in analysis.Value.Sequences)
            {
                if (s.Atmospheric)
                    anyAtm = true;
                else
                    anyVac = true;
            }
        }
        if (anyVac && anyAtm)
            return "(mixed)";
        return anyAtm ? "(ATM)" : "(VAC)";
    }
}
