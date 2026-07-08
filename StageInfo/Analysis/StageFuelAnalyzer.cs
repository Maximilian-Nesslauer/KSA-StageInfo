using System;
using System.Collections.Generic;
using System.Diagnostics;
using Brutal.Logging;
using KSA;
using StageInfo.Core;

namespace StageInfo.Analysis;

internal record struct StageFuelInfo
{
    public int StageNumber;
    public float CurrentFuelMass;
    public float MaxFuelMass;
    public float FuelFraction;
    public float DryMass;          // inert mass of parts in this stage
    public int EngineCount;
    public int DecouplerCount;
}

internal record struct VehicleFuelAnalysis
{
    public List<StageFuelInfo> Stages;
}

/// <summary>
/// Per-group fuel pool + mass snapshot. Resource groups (Part.Stage
/// numbers) are jettison / fuel-pool groups; dV is a sequence-level
/// concept and lives in SequenceAnalyzer.
/// </summary>
internal static class StageFuelAnalyzer
{
    private static readonly List<StageFuelInfo> _pooledStages = new();
    private static readonly Dictionary<int, int> _stageIndex = new();

    private static readonly Comparison<StageFuelInfo> StageNumberAscending =
        static (a, b) => a.StageNumber.CompareTo(b.StageNumber);

    private static bool _warnedMissingStage;

    public static void ResetPools()
    {
        _pooledStages.Clear();
        _stageIndex.Clear();
        _warnedMissingStage = false;
    }

    public static VehicleFuelAnalysis Analyze(Vehicle vehicle)
        => Analyze(vehicle.Parts);

    public static VehicleFuelAnalysis Analyze(PartTree parts)
    {
#if DEBUG
        long perfStart = DebugConfig.Performance ? Stopwatch.GetTimestamp() : 0;
#endif
        _pooledStages.Clear();
        _stageIndex.Clear();

        ReadOnlySpan<ResourceGroup> stages = parts.ResourceGroupList.Stages;
        for (int i = 0; i < stages.Length; i++)
        {
            int num = stages[i].Number;
            _stageIndex[num] = _pooledStages.Count;
            _pooledStages.Add(new StageFuelInfo { StageNumber = num });
        }

        ReadOnlySpan<Part> allParts = parts.Parts;
        ReadOnlySpan<MoleState> moleStates = parts.Moles.States;

        for (int i = 0; i < allParts.Length; i++)
        {
            Part part = allParts[i];
            int stageNum = part.Stage;
            if (!_stageIndex.TryGetValue(stageNum, out int idx))
            {
                if (!_warnedMissingStage)
                {
                    _warnedMissingStage = true;
                    DefaultCategory.Log.Warning(
                        $"[StageInfo] StageFuelAnalyzer: part '{part.DisplayName}' " +
                        $"(id={part.InstanceId}) has Stage={stageNum} not in " +
                        "ResourceGroupList.Stages, recovering. (logged once per session)");
                }
                idx = _pooledStages.Count;
                _stageIndex[stageNum] = idx;
                _pooledStages.Add(new StageFuelInfo { StageNumber = stageNum });
            }

            StageFuelInfo info = _pooledStages[idx];
            info.DryMass += MassHelpers.SumInertMassWithSubParts(part);

            AccumulateTanks(part.Modules, moleStates, ref info);
            ReadOnlySpan<Part> subParts = part.SubParts;
            for (int sp = 0; sp < subParts.Length; sp++)
                AccumulateTanks(subParts[sp].Modules, moleStates, ref info);

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

        _pooledStages.Sort(StageNumberAscending);

#if DEBUG
        if (DebugConfig.Performance)
            PerfTracker.Record("StageFuelAnalyzer.Analyze", Stopwatch.GetTimestamp() - perfStart);
#endif
        return new VehicleFuelAnalysis { Stages = _pooledStages };
    }

    private static void AccumulateTanks(ModuleList modules,
        ReadOnlySpan<MoleState> moleStates, ref StageFuelInfo info)
    {
        Span<Tank> tanks = modules.Get<Tank>();
        for (int t = 0; t < tanks.Length; t++)
        {
            Tank tank = tanks[t];
            info.CurrentFuelMass += tank.ComputeSubstanceMass(moleStates);
            info.MaxFuelMass += MassHelpers.ComputeTankMaxMass(tank);
        }
    }
}
