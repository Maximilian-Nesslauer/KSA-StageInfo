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
}
