using System;
using System.Collections.Generic;
using System.Diagnostics;
using KSA;
using StageInfo.Core;

namespace StageInfo.Analysis;

public record struct StageFuelInfo
{
    public int StageNumber;
    public float CurrentFuelMass;
    public float MaxFuelMass;
    public float FuelFraction;
    public float DryMass;          // inert mass of parts in this stage
    public int EngineCount;
    public int DecouplerCount;
}

public record struct VehicleFuelAnalysis
{
    public List<StageFuelInfo> Stages;
}

/// <summary>
/// Per-stage fuel pool + mass snapshot. Stages are jettison / fuel-pool
/// groups; dV is a sequence-level concept and lives in SequenceAnalyzer.
/// </summary>
public static class StageFuelAnalyzer
{
    private static readonly List<StageFuelInfo> _pooledStages = new();
    private static readonly Dictionary<int, int> _stageIndex = new();

    public static void ResetPools()
    {
        _pooledStages.Clear();
        _stageIndex.Clear();
    }

    public static VehicleFuelAnalysis Analyze(Vehicle vehicle)
    {
#if DEBUG
        long perfStart = DebugConfig.Performance ? Stopwatch.GetTimestamp() : 0;
#endif
        _pooledStages.Clear();
        _stageIndex.Clear();

        // Seed one row per declared stage so empty ones still render.
        ReadOnlySpan<Stage> stages = vehicle.Parts.StageList.Stages;
        for (int i = 0; i < stages.Length; i++)
        {
            int num = stages[i].Number;
            _stageIndex[num] = _pooledStages.Count;
            _pooledStages.Add(new StageFuelInfo { StageNumber = num });
        }

        ReadOnlySpan<Part> parts = vehicle.Parts.Parts;
        ReadOnlySpan<MoleState> moleStates = vehicle.Parts.Moles.States;

        for (int i = 0; i < parts.Length; i++)
        {
            Part part = parts[i];
            int stageNum = part.Stage;
            if (!_stageIndex.TryGetValue(stageNum, out int idx))
            {
                idx = _pooledStages.Count;
                _stageIndex[stageNum] = idx;
                _pooledStages.Add(new StageFuelInfo { StageNumber = stageNum });
            }

            StageFuelInfo info = _pooledStages[idx];
            info.DryMass += MassHelpers.SumInertMassWithSubParts(part);

            Span<Tank> tanks = part.Modules.Get<Tank>();
            for (int t = 0; t < tanks.Length; t++)
            {
                Tank tank = tanks[t];
                float cur = tank.ComputeSubstanceMass(moleStates);
                float frac = tank.FilledFraction(moleStates);
                float max = frac > 0.001f ? cur / frac : 0f;
                info.CurrentFuelMass += cur;
                info.MaxFuelMass += max;
            }

            if (part.Modules.HasAny<EngineController>()) info.EngineCount++;
            if (part.Modules.HasAny<Decoupler>()) info.DecouplerCount++;

            _pooledStages[idx] = info;
        }

        for (int i = 0; i < _pooledStages.Count; i++)
        {
            StageFuelInfo info = _pooledStages[i];
            info.FuelFraction = info.MaxFuelMass > 0f ? info.CurrentFuelMass / info.MaxFuelMass : 0f;
            _pooledStages[i] = info;
        }

        _pooledStages.Sort(static (a, b) => a.StageNumber.CompareTo(b.StageNumber));

#if DEBUG
        if (DebugConfig.Performance)
            PerfTracker.Record("StageFuelAnalyzer.Analyze", Stopwatch.GetTimestamp() - perfStart);
#endif
        return new VehicleFuelAnalysis { Stages = _pooledStages };
    }
}
