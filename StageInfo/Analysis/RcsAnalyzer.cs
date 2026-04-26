using System;
using System.Collections.Generic;
using System.Diagnostics;
using CommunityToolkit.HighPerformance.Buffers;
using KSA;
using StageInfo.Core;

namespace StageInfo.Analysis;

internal record struct StageRcsInfo
{
    public int StageNumber;
    public float CurrentMass;
    public float MaxMass;
    public float FuelFraction;
    /// <summary>
    /// Pool-backed list - either by the analyzer (live result) or by the
    /// AnalysisCache (retained snapshot). Treat as borrowed: don't store the
    /// reference past the producer's next reset.
    /// </summary>
    public List<RcsSubstanceInfo>? Substances;
}

internal record struct RcsSubstanceInfo
{
    public KeyHash Hash;
    public string Name;       // long form, e.g. "MMH (Monomethylhydrazine)"
    public string ShortName;  // compact, e.g. "MMH"
    public float CurrentMass;
    public float MaxMass;
}

internal record struct VehicleRcsAnalysis
{
    public bool HasRcs;
    public List<StageRcsInfo> Stages;
    public float TotalCurrentMass;
    public float TotalMaxMass;
    public float TotalThrustMax;
    public float TotalMassFlowMax;
    public float ExhaustVelocity;
    public float DeltaV;
}

/// <summary>
/// Per-stage RCS propellant pools and a vehicle-total dV budget. A substance
/// is treated as "RCS-only" when some active ThrusterController reaches it
/// but no active EngineController does, which keeps shared mains+RCS tanks
/// (e.g. LFOX feeding both a vernier and the main engine) off the RCS UI -
/// they're already on the main fuel bar. Main-thread only (shared pools).
/// </summary>
internal static class RcsAnalyzer
{
    private const float MinMassFlowRate = 1e-6f;
    // Floor avoids log(startMass / 0) when a vehicle is almost pure propellant.
    private const float MinDryMass = 1f;

    #region Pooled Collections

    private static readonly List<StageRcsInfo> _pooledStages = new();
    private static readonly Dictionary<int, int> _stageIndexByNumber = new();
    private static readonly HashSet<KeyHash> _engineSubstanceHashes = new();
    private static readonly HashSet<KeyHash> _rcsSubstanceHashes = new();
    private static readonly HashSet<ulong> _claimedTankIds = new();

    // Reused across Analyze calls so per-frame allocations stay near zero.
    private static readonly Stack<List<RcsSubstanceInfo>> _stageSubstanceListPool = new();
    private static readonly List<List<RcsSubstanceInfo>> _activeStageSubstanceLists = new();

    public static void ResetPools()
    {
        _pooledStages.Clear();
        _stageIndexByNumber.Clear();
        _engineSubstanceHashes.Clear();
        _rcsSubstanceHashes.Clear();
        _claimedTankIds.Clear();
        _stageSubstanceListPool.Clear();
        _activeStageSubstanceLists.Clear();
    }

    private static List<RcsSubstanceInfo> RentStageSubstanceList()
    {
        List<RcsSubstanceInfo> list = _stageSubstanceListPool.Count > 0
            ? _stageSubstanceListPool.Pop()
            : new List<RcsSubstanceInfo>();
        list.Clear();
        _activeStageSubstanceLists.Add(list);
        return list;
    }

    private static void ReturnAllStageSubstanceLists()
    {
        for (int i = 0; i < _activeStageSubstanceLists.Count; i++)
        {
            var list = _activeStageSubstanceLists[i];
            list.Clear();
            _stageSubstanceListPool.Push(list);
        }
        _activeStageSubstanceLists.Clear();
    }

    #endregion

    public static VehicleRcsAnalysis Analyze(Vehicle vehicle, float ambientPressure)
    {
#if DEBUG
        long perfStart = DebugConfig.Performance ? Stopwatch.GetTimestamp() : 0;
#endif
        ReturnAllStageSubstanceLists();
        _pooledStages.Clear();
        _stageIndexByNumber.Clear();
        _engineSubstanceHashes.Clear();
        _rcsSubstanceHashes.Clear();
        _claimedTankIds.Clear();

        var result = new VehicleRcsAnalysis
        {
            HasRcs = false,
            Stages = _pooledStages
        };

        Span<EngineController> engines = vehicle.Parts.Modules.Get<EngineController>();
        Span<ThrusterController> thrusters = vehicle.Parts.Modules.Get<ThrusterController>();

        // IsActive filter mirrors FlightComputer.ReadUpdatedVehicleConfiguration.
        for (int i = 0; i < engines.Length; i++)
        {
            EngineController engine = engines[i];
            if (!engine.IsActive) continue;
            CollectMixHashes(engine.Cores, _engineSubstanceHashes);
        }

        bool hasActiveThruster = false;
        for (int i = 0; i < thrusters.Length; i++)
        {
            if (thrusters[i].IsActive) { hasActiveThruster = true; break; }
        }

        if (hasActiveThruster)
        {
            ReadOnlySpan<MoleState> moleStates = vehicle.Parts.Moles.States;

            for (int i = 0; i < thrusters.Length; i++)
            {
                ThrusterController thruster = thrusters[i];
                if (!thruster.IsActive) continue;

                CollectMixHashes(thruster.Cores, _rcsSubstanceHashes);

                // Scalar sum (not vector) so opposing RCS pairs don't cancel;
                // upper bound, treats every nozzle as if it could fire prograde.
                foreach (RocketCore core in thruster.Cores)
                {
                    RocketCoreConditions combustion = core.ComputeConditions(1f);
                    foreach (RocketNozzle nozzle in core.Rocket.Nozzles)
                    {
                        NozzlePerformance perf = nozzle.ComputePerformance(in combustion, ambientPressure);
                        result.TotalThrustMax += perf.GetTotalThrust();
                        result.TotalMassFlowMax += perf.MassFlowRate;
                    }

                    if (core.ResourceManager == null) continue;
                    AccumulateTanksFromCore(core.ResourceManager, moleStates, ref result);
                }
            }
        }

        for (int i = 0; i < _pooledStages.Count; i++)
        {
            StageRcsInfo info = _pooledStages[i];
            info.FuelFraction = info.MaxMass > 0f ? info.CurrentMass / info.MaxMass : 0f;
            _pooledStages[i] = info;
        }
        _pooledStages.Sort(static (a, b) => a.StageNumber.CompareTo(b.StageNumber));

        result.HasRcs = _pooledStages.Count > 0
            && (result.TotalThrustMax > 0f || result.TotalMaxMass > 0f);

        if (result.TotalMassFlowMax > MinMassFlowRate)
            result.ExhaustVelocity = result.TotalThrustMax / result.TotalMassFlowMax;

        if (result.HasRcs && result.ExhaustVelocity > 0f && result.TotalCurrentMass > 0f)
        {
            // Tsiolkovsky from full vehicle mass; upper bound, assumes all RCS
            // is spent prograde. UI labels this "~XX m/s" to flag the assumption.
            float startMass = vehicle.TotalMass;
            float burnable = MathF.Min(result.TotalCurrentMass, startMass - MinDryMass);
            if (burnable > 0f)
            {
                float endMass = startMass - burnable;
                result.DeltaV = result.ExhaustVelocity * MathF.Log(startMass / endMass);
            }
        }

#if DEBUG
        if (DebugConfig.Performance)
            PerfTracker.Record("RcsAnalyzer.Analyze", Stopwatch.GetTimestamp() - perfStart);
#endif
        return result;
    }

    private static void CollectMixHashes(RocketCore[] cores, HashSet<KeyHash> sink)
    {
        foreach (RocketCore core in cores)
        {
            ReadOnlySpan<Reactant> reactants = core.Combustion.Reactants;
            for (int i = 0; i < reactants.Length; i++)
                sink.Add(reactants[i].SubstancePhase.Hash);
        }
    }

    private static void AccumulateTanksFromCore(ResourceManager rm, ReadOnlySpan<MoleState> moleStates,
        ref VehicleRcsAnalysis result)
    {
        MemoryOwner<MemoryOwner<Tank>>? nodes = rm.FurtherestToNearestNodeSameStage;
        if (nodes == null || nodes.Length == 0) return;

        Span<MemoryOwner<Tank>> nodeSpan = nodes.Span;
        for (int i = 0; i < nodeSpan.Length; i++)
        {
            if (nodeSpan[i] == null || nodeSpan[i].Length == 0) continue;

            Span<Tank> tanks = nodeSpan[i].Span;
            for (int j = 0; j < tanks.Length; j++)
            {
                Tank tank = tanks[j];
                if (tank == null) continue;
                // Dedup tanks reachable from multiple thrusters' cores.
                if (!_claimedTankIds.Add(tank.InstanceId)) continue;

                int stageNum = tank.Parent.FullPart.Stage;
                AccumulateMolesFromTank(tank, moleStates, stageNum, ref result);
            }
        }
    }

    private static void AccumulateMolesFromTank(Tank tank, ReadOnlySpan<MoleState> moleStates,
        int stageNum, ref VehicleRcsAnalysis result)
    {
        foreach (Mole mole in tank.Moles)
        {
            KeyHash hash = mole.SubstancePhase.Hash;
            if (_engineSubstanceHashes.Contains(hash)) continue;
            if (!_rcsSubstanceHashes.Contains(hash)) continue;

            float current = moleStates[mole.StatesIdx].Mass;
            float max = mole.GetLiquidMass(mole.ContainerVolume);

            result.TotalCurrentMass += current;
            result.TotalMaxMass += max;
            AddToStage(stageNum, current, max);
            AddToStageSubstance(stageNum, mole.SubstancePhase, current, max);
        }
    }

    private static void AddToStage(int stageNum, float current, float max)
    {
        if (!_stageIndexByNumber.TryGetValue(stageNum, out int idx))
        {
            idx = _pooledStages.Count;
            _stageIndexByNumber[stageNum] = idx;
            _pooledStages.Add(new StageRcsInfo { StageNumber = stageNum });
        }
        StageRcsInfo info = _pooledStages[idx];
        info.CurrentMass += current;
        info.MaxMass += max;
        _pooledStages[idx] = info;
    }

    // Stages have at most ~3 substances in practice, so linear scan beats
    // a dictionary lookup.
    private static void AddToStageSubstance(int stageNum, SubstancePhase phase, float current, float max)
    {
        int stageIdx = _stageIndexByNumber[stageNum];
        StageRcsInfo info = _pooledStages[stageIdx];
        if (info.Substances == null)
        {
            info.Substances = RentStageSubstanceList();
            _pooledStages[stageIdx] = info;
        }

        KeyHash hash = phase.Hash;
        List<RcsSubstanceInfo> list = info.Substances;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Hash == hash)
            {
                RcsSubstanceInfo s = list[i];
                s.CurrentMass += current;
                s.MaxMass += max;
                list[i] = s;
                return;
            }
        }

        string longName = phase.Substance?.Name ?? phase.Name;
        list.Add(new RcsSubstanceInfo
        {
            Hash = hash,
            Name = longName,
            ShortName = ShortenSubstanceName(longName),
            CurrentMass = current,
            MaxMass = max
        });
    }

    private static string ShortenSubstanceName(string longName)
    {
        if (string.IsNullOrEmpty(longName))
            return longName;

        // Defensive cover for the phase.Name fallback path: SubstanceTemplate
        // prepends "Liquid "/"Solid "/"Gaseous " there. Substance.Name itself
        // has no such prefix.
        if (longName.StartsWith("Liquid ", StringComparison.Ordinal))
            longName = longName[7..];
        else if (longName.StartsWith("Solid ", StringComparison.Ordinal))
            longName = longName[6..];
        else if (longName.StartsWith("Gaseous ", StringComparison.Ordinal))
            longName = longName[8..];

        // "ABK (Long Name)" -> "ABK".
        int paren = longName.IndexOf('(');
        if (paren > 0)
            return longName[..paren].TrimEnd();

        return longName;
    }
}
