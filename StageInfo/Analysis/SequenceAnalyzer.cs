using System;
using System.Collections.Generic;
using System.Diagnostics;
using Brutal.Logging;
using Brutal.Numerics;
using CommunityToolkit.HighPerformance.Buffers;
using KSA;
using StageInfo.Core;

namespace StageInfo.Analysis;

internal record struct SequenceBurnInfo
{
    public int SequenceNumber;
    public bool IsActivated;
    public float DeltaV;
    public float BurnTime;
    public float Thrust;
    public float ExhaustVelocity;
    public float Isp;
    public float StartMass;
    public float EndMass;
    public float FuelMass;
    public float MaxFuelMass;
    public float FuelFraction;
    public float MassFlowRate;
    public float Twr;
    public float JettisonedMass;
    public int EngineCount;
}

internal record struct VehicleBurnAnalysis
{
    public List<SequenceBurnInfo> Sequences;
    public float TotalDeltaV;
    public float TotalBurnTime;
}

internal record struct BurnSequenceAllocation
{
    public int SequenceNumber;
    public float AllocatedDv;
    public float SequenceTotalDv;
    public float AllocatedBurnTime;
}

internal record struct BurnAnalysis
{
    public float RequiredDv;
    public float AvailableDv;
    public float TotalBurnTime;
    public bool IsSufficient;
    public List<BurnSequenceAllocation> SequenceAllocations;
}

/// <summary>
/// Per-sequence Delta V analyzer. Walks sequences in activation order; for each,
/// decouplers jettison subtrees, then engines burn their SameStage fuel pool.
/// Main-thread only (shared pools).
///
/// Verbose <paramref name="log"/> output is opt-in; the parameter threads
/// through the helpers so each level can emit its own detail.
/// </summary>
internal static class SequenceAnalyzer
{
    private const float MinMassFlowRate = 1e-6f;
    private const float MinDryMass = 1f;

    #region Pooled Collections

    private static readonly List<SequenceBurnInfo> _pooledSequences = new();
    private static readonly HashSet<uint> _pooledJettisonedPartIds = new();
    private static readonly HashSet<ulong> _pooledFuelClaimedTankIds = new();
    private static readonly List<Sequence> _pooledSortedSequences = new();
    private static readonly List<EngineController> _pooledEngines = new();
    private static readonly List<BurnSequenceAllocation> _pooledAllocations = new();

    private static readonly Comparison<Sequence> SequenceAscending =
        static (a, b) => a.Number.CompareTo(b.Number);

    public static void ResetPools()
    {
        _pooledSequences.Clear();
        _pooledJettisonedPartIds.Clear();
        _pooledFuelClaimedTankIds.Clear();
        _pooledSortedSequences.Clear();
        _pooledEngines.Clear();
        _pooledAllocations.Clear();
    }

    #endregion

    public static VehicleBurnAnalysis Analyze(Vehicle vehicle,
        float ambientPressure = 0f, float? surfaceGravityOverride = null, bool log = false)
    {
#if DEBUG
        long perfStart = DebugConfig.Performance ? Stopwatch.GetTimestamp() : 0;
#endif
        _pooledSequences.Clear();
        _pooledJettisonedPartIds.Clear();
        _pooledFuelClaimedTankIds.Clear();

        var result = new VehicleBurnAnalysis
        {
            Sequences = _pooledSequences,
            TotalDeltaV = 0f,
            TotalBurnTime = 0f
        };

        ReadOnlySpan<Sequence> sequences = vehicle.Parts.SequenceList.Sequences;
        ReadOnlySpan<MoleState> moleStates = vehicle.Parts.Moles.States;
        float currentMass = vehicle.TotalMass;

        float surfaceGravity;
        if (surfaceGravityOverride.HasValue)
        {
            surfaceGravity = surfaceGravityOverride.Value;
        }
        else
        {
            double parentMass = vehicle.Parent?.Mass ?? 0.0;
            double parentRadius = vehicle.Parent?.MeanRadius ?? 1.0;
            surfaceGravity = (float)(Constants.GRAVITATIONAL_CONSTANT * parentMass / (parentRadius * parentRadius));
        }

        if (log)
        {
            DefaultCategory.Log.Debug(
                $"[StageInfo] SequenceAnalyzer: vehicle={vehicle.Id}, totalMass={currentMass:F1} kg, " +
                $"inertMass={vehicle.InertMass:F1} kg, propellant={vehicle.PropellantMass:F1} kg, " +
                $"sequences={sequences.Length}, surfaceG={surfaceGravity:F3} m/s^2, " +
                $"ambientPressure={ambientPressure:F0} Pa");
        }

        // SequenceList.ResetCaches already sorts by Number, but we sort our own
        // copy defensively so a future game change can't shuffle our walk order.
        SortSequencesAscending(sequences);

        for (int si = 0; si < _pooledSortedSequences.Count; si++)
        {
            Sequence sequence = _pooledSortedSequences[si];

            if (sequence.Parts.IsEmpty)
                continue;

            // Decouplers in this sequence fire and jettison their subtrees.
            float jettisonedMass = ComputeJettisonedMass(
                sequence, moleStates, _pooledJettisonedPartIds, _pooledFuelClaimedTankIds, log);
            currentMass -= jettisonedMass;

            if (log && jettisonedMass > 0f)
            {
                DefaultCategory.Log.Debug(
                    $"[StageInfo]   Sequence {sequence.Number}: jettisoned {jettisonedMass:F1} kg, " +
                    $"mass after jettison={currentMass:F1} kg");
            }

            // For an activated sequence, only currently-IsActive engines count (pilot may
            // have killed some); for future sequences all engines count (they'll co-ignite).
            CollectEngines(sequence, _pooledJettisonedPartIds, sequence.Activated, log);

            if (_pooledEngines.Count == 0)
            {
                if (log)
                    DefaultCategory.Log.Debug(
                        $"[StageInfo]   Sequence {sequence.Number}: no engines " +
                        (jettisonedMass > 0f ? "(decoupler-only sequence)" : "(empty)"));
                continue;
            }

            // Aggregate thrust + mass flow (pressure-aware).
            float totalThrust = 0f;
            float totalFlowRate = 0f;
            if (ambientPressure > 0f)
            {
                foreach (EngineController engine in _pooledEngines)
                {
                    var data = RocketControllerData.ComputeFromCores(
                        engine.Cores.AsSpan(), float3.Zero, ambientPressure);
                    totalThrust += data.ThrustMax.Length();
                    totalFlowRate += data.MassFlowRateMax;
                }
            }
            else
            {
                foreach (EngineController engine in _pooledEngines)
                {
                    totalThrust += engine.VacuumData.ThrustMax.Length();
                    totalFlowRate += engine.VacuumData.MassFlowRateMax;
                }
            }

            if (totalFlowRate < MinMassFlowRate)
            {
                if (log)
                    DefaultCategory.Log.Debug(
                        $"[StageInfo]   Sequence {sequence.Number}: zero mass flow rate, skipping");
                continue;
            }

            float ve = totalThrust / totalFlowRate;
            float isp = (float)(ve / Constants.STANDARD_GRAVITY);

            // Fuel reachable from these engines via each core's SameStage walk.
            var (fuelMass, maxFuelMass) = ComputeSequenceFuel(
                _pooledEngines, _pooledFuelClaimedTankIds, moleStates, log);

            // FuelFraction reflects tank fill state, not the burnable amount,
            // so it's computed before any clamp.
            float fuelFraction = maxFuelMass > 0f ? fuelMass / maxFuelMass : 0f;

            float burnableFuel = fuelMass;
            float maxBurnable = currentMass - MinDryMass;
            if (burnableFuel > maxBurnable)
            {
                if (log)
                    DefaultCategory.Log.Warning(
                        $"[StageInfo]   Sequence {sequence.Number}: fuel ({burnableFuel:F1} kg) clamped " +
                        $"to max burnable mass ({maxBurnable:F1} kg)");
                burnableFuel = Math.Max(0f, maxBurnable);
            }

            // Tsiolkovsky. After the clamp, endMass >= MinDryMass > 0.
            float startMass = currentMass;
            float endMass = currentMass - burnableFuel;
            float dv = burnableFuel > 0f
                ? ve * MathF.Log(startMass / endMass)
                : 0f;
            float burnTime = burnableFuel / totalFlowRate;
            float twr = (surfaceGravity > 0f)
                ? totalThrust / (startMass * surfaceGravity)
                : 0f;

            var info = new SequenceBurnInfo
            {
                SequenceNumber = sequence.Number,
                IsActivated = sequence.Activated,
                DeltaV = dv,
                BurnTime = burnTime,
                Thrust = totalThrust,
                ExhaustVelocity = ve,
                Isp = isp,
                StartMass = startMass,
                EndMass = endMass,
                FuelMass = fuelMass,
                MaxFuelMass = maxFuelMass,
                FuelFraction = fuelFraction,
                MassFlowRate = totalFlowRate,
                Twr = twr,
                JettisonedMass = jettisonedMass,
                EngineCount = _pooledEngines.Count
            };

            result.Sequences.Add(info);
            result.TotalDeltaV += dv;
            result.TotalBurnTime += burnTime;

            if (log)
            {
                string activatedTag = sequence.Activated ? " [ACTIVATED]" : "";
                DefaultCategory.Log.Debug(
                    $"[StageInfo]   Sequence {sequence.Number}{activatedTag}: " +
                    $"dV={dv:F1} m/s, burn={burnTime:F1} s, TWR={twr:F2}, " +
                    $"thrust={totalThrust:F0} N, Ve={ve:F1} m/s, Isp={isp:F1} s, " +
                    $"fuel={burnableFuel:F1}/{fuelMass:F1} kg burnable/in-tanks, " +
                    $"mass={startMass:F1}->{endMass:F1} kg, " +
                    $"engines={_pooledEngines.Count}");
            }

            currentMass = endMass;
        }

        if (log)
        {
            DefaultCategory.Log.Info(
                $"[StageInfo] SequenceAnalyzer result: {result.Sequences.Count} entries, " +
                $"total dV={result.TotalDeltaV:F1} m/s, total burn={result.TotalBurnTime:F1} s");
        }

#if DEBUG
        if (DebugConfig.Performance)
            PerfTracker.Record("SequenceAnalyzer.Analyze", Stopwatch.GetTimestamp() - perfStart);
#endif
        return result;
    }

    private static void SortSequencesAscending(ReadOnlySpan<Sequence> sequences)
    {
        _pooledSortedSequences.Clear();
        for (int i = 0; i < sequences.Length; i++)
            _pooledSortedSequences.Add(sequences[i]);
        _pooledSortedSequences.Sort(SequenceAscending);
    }

    private static void CollectEngines(Sequence sequence,
        HashSet<uint> jettisonedPartIds, bool sequenceActivated, bool log)
    {
        _pooledEngines.Clear();
        ReadOnlySpan<Part> parts = sequence.Parts;

        for (int pi = 0; pi < parts.Length; pi++)
        {
            Part part = parts[pi];
            if (jettisonedPartIds.Contains(part.InstanceId))
                continue;

            Span<EngineController> engines = part.Modules.Get<EngineController>();
            for (int ei = 0; ei < engines.Length; ei++)
            {
                EngineController engine = engines[ei];
                if (sequenceActivated && !engine.IsActive)
                    continue;
                _pooledEngines.Add(engine);

                if (log)
                {
                    string activeTag = engine.IsActive ? " [active]" : "";
                    DefaultCategory.Log.Debug(
                        $"[StageInfo]     Engine '{engine.TemplateId}'{activeTag}: " +
                        $"thrust={engine.VacuumData.ThrustMax.Length():F0} N, " +
                        $"flowRate={engine.VacuumData.MassFlowRateMax:F4} kg/s, " +
                        $"cores={engine.Cores.Length}");
                }
            }
        }
    }

    #region Burn Allocation

    /// <summary>
    /// Allocates a required dV across sequences in firing order. Partially
    /// consumed sequences use inverse Tsiolkovsky to derive burn time.
    /// </summary>
    public static BurnAnalysis AnalyzeBurn(VehicleBurnAnalysis analysis, float requiredDv)
    {
        _pooledAllocations.Clear();

        var result = new BurnAnalysis
        {
            RequiredDv = requiredDv,
            AvailableDv = analysis.TotalDeltaV,
            TotalBurnTime = 0f,
            IsSufficient = analysis.TotalDeltaV >= requiredDv,
            SequenceAllocations = _pooledAllocations
        };

        float dvRemaining = requiredDv;

        foreach (SequenceBurnInfo seq in analysis.Sequences)
        {
            if (dvRemaining <= 0f)
                break;

            if (seq.DeltaV <= 0f || seq.EngineCount == 0)
                continue;

            bool fullyConsumed = dvRemaining >= seq.DeltaV;
            float allocatedDv = fullyConsumed ? seq.DeltaV : dvRemaining;

            float burnTime;
            if (fullyConsumed)
            {
                burnTime = seq.BurnTime;
            }
            else
            {
                // Inverse Tsiolkovsky: endMass = startMass * exp(-dv/Ve).
                float endMass = seq.StartMass * MathF.Exp(-allocatedDv / seq.ExhaustVelocity);
                float fuelNeeded = seq.StartMass - endMass;
                burnTime = (seq.MassFlowRate > MinMassFlowRate)
                    ? fuelNeeded / seq.MassFlowRate
                    : 0f;
            }

            result.SequenceAllocations.Add(new BurnSequenceAllocation
            {
                SequenceNumber = seq.SequenceNumber,
                AllocatedDv = allocatedDv,
                SequenceTotalDv = seq.DeltaV,
                AllocatedBurnTime = burnTime
            });

            result.TotalBurnTime += burnTime;
            dvRemaining -= allocatedDv;
        }

        return result;
    }

    #endregion

    #region Fuel Calculation

    /// <summary>
    /// Sums reachable propellant via each RocketCore's SameStage tank list.
    /// Records claimed tank IDs so later decoupler walks treat them as empty.
    /// </summary>
    private static (float current, float max) ComputeSequenceFuel(
        List<EngineController> engines,
        HashSet<ulong> fuelClaimedTankIds,
        ReadOnlySpan<MoleState> moleStates,
        bool log)
    {
        float totalCurrent = 0f;
        float totalMax = 0f;

        foreach (EngineController engine in engines)
        {
            foreach (RocketCore core in engine.Cores)
            {
                if (core.ResourceManager == null)
                {
                    if (log)
                        DefaultCategory.Log.Warning(
                            $"[StageInfo]     Core '{core.TemplateId}' has no ResourceManager");
                    continue;
                }

                var (current, max) = WalkSameStage(
                    core.ResourceManager, fuelClaimedTankIds, moleStates, log);
                totalCurrent += current;
                totalMax += max;
            }
        }

        return (totalCurrent, totalMax);
    }

    private static (float current, float max) WalkSameStage(
        ResourceManager resourceManager,
        HashSet<ulong> fuelClaimedTankIds,
        ReadOnlySpan<MoleState> moleStates,
        bool log)
    {
        float current = 0f;
        float max = 0f;
        MemoryOwner<MemoryOwner<Tank>>? nodes =
            resourceManager.FurtherestToNearestNodeSameStage;

        if (nodes == null || nodes.Length == 0)
            return (0f, 0f);

        Span<MemoryOwner<Tank>> nodeSpan = nodes.Span;
        for (int i = 0; i < nodeSpan.Length; i++)
        {
            if (nodeSpan[i].Length == 0)
                continue;

            Span<Tank> tanks = nodeSpan[i].Span;
            for (int j = 0; j < tanks.Length; j++)
            {
                Tank tank = tanks[j];

                if (!fuelClaimedTankIds.Add(tank.InstanceId))
                    continue;

                float mass = tank.ComputeSubstanceMass(moleStates);
                float maxMass = MassHelpers.ComputeTankMaxMass(tank);

                current += mass;
                max += maxMass;

                if (log && mass > 0.01f)
                {
                    float filledFraction = maxMass > 0f ? mass / maxMass : 0f;
                    DefaultCategory.Log.Debug(
                        $"[StageInfo]       Tank '{tank.InstanceId}' on " +
                        $"'{tank.Parent.FullPart.DisplayName}' " +
                        $"(stage {tank.Parent.FullPart.Stage}): " +
                        $"{mass:F2}/{maxMass:F2} kg ({filledFraction:P0})");
                }
            }
        }

        return (current, max);
    }

    #endregion

    #region Jettisoned Mass

    /// <summary>
    /// Sum of subtree masses downstream of each decoupler in this sequence.
    /// Tanks claimed as fuel by an earlier sequence contribute 0 propellant.
    /// </summary>
    private static float ComputeJettisonedMass(
        Sequence sequence,
        ReadOnlySpan<MoleState> moleStates,
        HashSet<uint> jettisonedPartIds,
        HashSet<ulong> fuelClaimedTankIds,
        bool log)
    {
        float totalJettisoned = 0f;

        ReadOnlySpan<Part> parts = sequence.Parts;
        for (int pi = 0; pi < parts.Length; pi++)
        {
            Part part = parts[pi];
            if (!part.Modules.HasAny<Decoupler>())
                continue;

            float subtreeMass = CollectSubtreeMass(
                part, moleStates, jettisonedPartIds, fuelClaimedTankIds);
            totalJettisoned += subtreeMass;

            if (log)
            {
                DefaultCategory.Log.Debug(
                    $"[StageInfo]     Decoupler on '{part.DisplayName}' (id={part.InstanceId}): " +
                    $"jettisons {subtreeMass:F1} kg");
            }
        }

        return totalJettisoned;
    }

    private static float CollectSubtreeMass(
        Part part,
        ReadOnlySpan<MoleState> moleStates,
        HashSet<uint> jettisonedPartIds,
        HashSet<ulong> fuelClaimedTankIds)
    {
        if (!jettisonedPartIds.Add(part.InstanceId))
            return 0f;

        float mass = ComputePartMass(part, moleStates, fuelClaimedTankIds);

        List<Part> children = part.TreeChildren;
        for (int i = 0; i < children.Count; i++)
        {
            mass += CollectSubtreeMass(
                children[i], moleStates, jettisonedPartIds, fuelClaimedTankIds);
        }

        return mass;
    }

    private static float ComputePartMass(
        Part part,
        ReadOnlySpan<MoleState> moleStates,
        HashSet<ulong> fuelClaimedTankIds)
    {
        float mass = SumComponentMass(part.Modules, moleStates, fuelClaimedTankIds);

        ReadOnlySpan<Part> subParts = part.SubParts;
        for (int i = 0; i < subParts.Length; i++)
            mass += SumComponentMass(subParts[i].Modules, moleStates, fuelClaimedTankIds);

        return mass;
    }

    private static float SumComponentMass(
        ModuleList components,
        ReadOnlySpan<MoleState> moleStates,
        HashSet<ulong> fuelClaimedTankIds)
    {
        float mass = MassHelpers.SumInertMass(components);

        Span<Tank> tanks = components.Get<Tank>();
        for (int i = 0; i < tanks.Length; i++)
        {
            if (!fuelClaimedTankIds.Contains(tanks[i].InstanceId))
                mass += tanks[i].ComputeSubstanceMass(moleStates);
        }

        return mass;
    }

    #endregion
}
