using System;
using KSA;

namespace StageInfo.Analysis;

internal static class MassHelpers
{
    /// <summary>Sum of InertMass on a module list (one part's worth).</summary>
    public static float SumInertMass(ModuleList modules)
    {
        float mass = 0f;
        Span<InertMass> inerts = modules.Get<InertMass>();
        for (int i = 0; i < inerts.Length; i++)
            mass += inerts[i].MassPropertiesAsmb.Props.Mass;
        return mass;
    }

    /// <summary>Sum of InertMass on a part and all its SubParts.</summary>
    public static float SumInertMassWithSubParts(Part part)
    {
        float mass = SumInertMass(part.Modules);
        ReadOnlySpan<Part> subs = part.SubParts;
        for (int i = 0; i < subs.Length; i++)
            mass += SumInertMass(subs[i].Modules);
        return mass;
    }

    /// <summary>Tank capacity in kg, derived from each mole's density and
    /// container volume. Stable for nearly-empty tanks where dividing the
    /// current mass by the filled fraction would underestimate.</summary>
    public static float ComputeTankMaxMass(Tank tank)
    {
        float maxMass = 0f;
        foreach (Mole mole in tank.Moles)
            maxMass += ComputeMoleMaxMass(mole);
        return maxMass;
    }

    // Stock Mole.GetLiquidMass returns 0 for non-liquid phases, so a Solid
    // mole would report 0 max even when state.Mass is non-zero. Currently
    // dormant because Tank.ConfigureFor sets ContainerVolume=0 for non-Liquid
    // moles, but kept defensive against a future solid propellant.
    private static float ComputeMoleMaxMass(Mole mole) => mole.SubstancePhase switch
    {
        Liquid liquid => liquid.ComputeMass(mole.ContainerVolume),
        Solid solid => solid.ComputeMass(mole.ContainerVolume),
        _ => 0f,
    };
}
