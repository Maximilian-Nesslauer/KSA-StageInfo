using System.Collections.Generic;
using System.Diagnostics;
using KSA;
using StageInfo.Core;
using StageInfo.UI;

namespace StageInfo.Analysis;

/// <summary>
/// One analysis axis (primary or secondary). Holds sequence + burn results
/// and their dictionary lookups. All mutation goes through the methods so
/// the cache-owned lists aren't aliased with the analyzer's pooled lists.
/// </summary>
internal sealed class AnalysisSlot
{
    public VehicleBurnAnalysis? Sequences;
    public readonly List<SequenceBurnInfo> SequenceList = new();
    public readonly Dictionary<int, SequenceBurnInfo> SequenceLookup = new();

    public BurnAnalysis? Burn;
    public readonly List<BurnSequenceAllocation> Allocations = new();
    public readonly Dictionary<int, BurnSequenceAllocation> AllocationLookup = new();

    public void RunSequenceAnalysis(Vehicle vehicle, float ambientPressure, float? surfaceGravity,
        PerSequenceEnv? perSequenceEnv = null)
    {
        float sg = surfaceGravity ?? EnvironmentHelpers.ComputeSurfaceGravity(vehicle.Parent);
        RunSequenceAnalysis(vehicle.Parts, vehicle.TotalMass, ambientPressure, sg, perSequenceEnv);
    }

    public void RunSequenceAnalysis(PartTree parts, float totalMass, float ambientPressure, float surfaceGravity,
        PerSequenceEnv? perSequenceEnv = null)
    {
        var result = SequenceAnalyzer.Analyze(parts, totalMass,
            ambientPressure, surfaceGravity, perSequenceEnv: perSequenceEnv);

        SequenceList.Clear();
        SequenceList.AddRange(result.Sequences);
        Sequences = new VehicleBurnAnalysis
        {
            Sequences = SequenceList,
            TotalDeltaV = result.TotalDeltaV,
            TotalBurnTime = result.TotalBurnTime
        };

        SequenceLookup.Clear();
        foreach (var s in SequenceList)
            SequenceLookup[s.SequenceNumber] = s;
    }

    /// <summary>No-op when no sequence analysis exists.</summary>
    public void UpdateBurnAnalysis(float requiredDv)
    {
        if (Sequences == null)
            return;

        var result = SequenceAnalyzer.AnalyzeBurn(Sequences.Value, requiredDv);

        Allocations.Clear();
        Allocations.AddRange(result.SequenceAllocations);
        Burn = new BurnAnalysis
        {
            RequiredDv = result.RequiredDv,
            AvailableDv = result.AvailableDv,
            TotalBurnTime = result.TotalBurnTime,
            IsSufficient = result.IsSufficient,
            SequenceAllocations = Allocations
        };

        AllocationLookup.Clear();
        foreach (var a in Allocations)
            AllocationLookup[a.SequenceNumber] = a;
    }

    /// <summary>Clears sequence and burn state.</summary>
    public void Clear()
    {
        Sequences = null;
        SequenceList.Clear();
        SequenceLookup.Clear();
        ClearBurn();
    }

    public void ClearBurn()
    {
        Burn = null;
        AllocationLookup.Clear();
    }
}

/// <summary>
/// Per-frame analysis cache. Driven by Patch_CorrectedBurnDuration on
/// Vehicle.UpdateFromTaskResults. Skips analysis when neither a burn is
/// planned nor the panel is visible.
/// </summary>
internal static class AnalysisCache
{
    private static readonly AnalysisSlot _primary = new();
    private static readonly AnalysisSlot _secondary = new();

    private static VehicleFuelAnalysis? _cachedStages;
    private static readonly List<StageFuelInfo> _cachedStageList = new();
    private static readonly Dictionary<int, StageFuelInfo> _stageLookup = new();

    private static VehicleRcsAnalysis? _cachedRcs;
    private static readonly List<StageRcsInfo> _cachedRcsStageList = new();
    private static readonly Dictionary<int, StageRcsInfo> _rcsStageLookup = new();

    // Must NOT alias RcsAnalyzer's pool: the analyzer recycles on every Analyze
    // call, this cache holds the snapshot until the next RunRcsAnalysis.
    private static readonly Stack<List<RcsSubstanceInfo>> _cachedStageSubstancePool = new();
    private static readonly List<List<RcsSubstanceInfo>> _activeCachedStageSubstanceLists = new();

    public static string PrimaryLabel { get; private set; } = "";
    public static string? SecondaryLabel { get; private set; }
    public static bool IsPrimaryCurrentCondition { get; private set; } = true;

    /// <summary>
    /// Set by the panel each rendered frame. Read and reset by Update() to
    /// decide whether the panel needs fresh analysis. Main thread only.
    /// </summary>
    private static bool _panelNeedsData;

    public static void MarkPanelActive() => _panelNeedsData = true;

    public static VehicleBurnAnalysis? Sequences => _primary.Sequences;
    public static BurnAnalysis? BurnAnalysis => _primary.Burn;
    public static VehicleBurnAnalysis? SecondarySequences => _secondary.Sequences;
    public static BurnAnalysis? SecondaryBurnAnalysis => _secondary.Burn;
    public static VehicleFuelAnalysis? Stages => _cachedStages;
    public static VehicleRcsAnalysis? Rcs => _cachedRcs;

    public static bool TryGetSequenceInfo(int sequenceNumber, out SequenceBurnInfo info)
        => _primary.SequenceLookup.TryGetValue(sequenceNumber, out info);

    public static bool TryGetBurnAllocation(int sequenceNumber, out BurnSequenceAllocation alloc)
        => _primary.AllocationLookup.TryGetValue(sequenceNumber, out alloc);

    public static bool TryGetSecondarySequenceInfo(int sequenceNumber, out SequenceBurnInfo info)
        => _secondary.SequenceLookup.TryGetValue(sequenceNumber, out info);

    public static bool TryGetSecondaryBurnAllocation(int sequenceNumber, out BurnSequenceAllocation alloc)
        => _secondary.AllocationLookup.TryGetValue(sequenceNumber, out alloc);

    public static bool TryGetStageFuelInfo(int stageNumber, out StageFuelInfo info)
        => _stageLookup.TryGetValue(stageNumber, out info);

    public static bool TryGetStageRcsInfo(int stageNumber, out StageRcsInfo info)
        => _rcsStageLookup.TryGetValue(stageNumber, out info);

    public static float? GetCorrectedBurnDuration() => _primary.Burn?.TotalBurnTime;

    public static void Update(Vehicle vehicle)
    {
#if DEBUG
        long perfStart = DebugConfig.Performance ? Stopwatch.GetTimestamp() : 0;
#endif
        BurnTarget? burn = vehicle.FlightComputer.Burn;
        float requiredDv = burn != null ? burn.DeltaVToGoCci.Length() : 0f;
        bool panelActive = _panelNeedsData;
        _panelNeedsData = false;

        if (burn == null && !panelActive)
        {
            ClearAll();
            return;
        }

        var env = StageInfoSettings.ResolveEnvironment(vehicle);
        PrimaryLabel = env.PrimaryLabel;
        SecondaryLabel = env.SecondaryLabel;
        IsPrimaryCurrentCondition = env.IsPrimaryCurrentCondition;

        _primary.RunSequenceAnalysis(vehicle, env.PrimaryPressure, env.PrimarySurfaceGravity,
            env.PrimaryPerSequence);

        // Custom mode's (VAC)/(ATM)/(mixed) label depends on which sequences
        // actually burned and at what pressure, so it is derived from the
        // analyzed rows rather than from every toggled sequence.
        if (env.PrimaryPerSequence.HasValue)
            PrimaryLabel = EnvironmentHelpers.AtmosphericLabel(_primary.Sequences);

        if (env.SecondaryPressure.HasValue)
            _secondary.RunSequenceAnalysis(vehicle, env.SecondaryPressure.Value, env.SecondarySurfaceGravity);
        else
            _secondary.Clear();

        if (panelActive)
        {
            RunStageAnalysis(vehicle);
            RunRcsAnalysis(vehicle, env.PrimaryPressure);
        }
        else
        {
            ClearStages();
            ClearRcs();
        }

        if (requiredDv > 0f)
        {
            _primary.UpdateBurnAnalysis(requiredDv);
            _secondary.UpdateBurnAnalysis(requiredDv);
        }
        else
        {
            _primary.ClearBurn();
            _secondary.ClearBurn();
        }

#if DEBUG
        if (DebugConfig.Performance)
            PerfTracker.Record("AnalysisCache.Update", Stopwatch.GetTimestamp() - perfStart);
#endif
    }

    public static void Reset()
    {
        ClearAll();
        _cachedStageSubstancePool.Clear();
        _panelNeedsData = false;
    }

    private static void RunStageAnalysis(Vehicle vehicle)
    {
        var result = StageFuelAnalyzer.Analyze(vehicle);

        _cachedStageList.Clear();
        _cachedStageList.AddRange(result.Stages);
        _cachedStages = new VehicleFuelAnalysis { Stages = _cachedStageList };

        _stageLookup.Clear();
        foreach (var s in _cachedStageList)
            _stageLookup[s.StageNumber] = s;
    }

    private static void ClearStages()
    {
        _cachedStages = null;
        _cachedStageList.Clear();
        _stageLookup.Clear();
    }

    private static void RunRcsAnalysis(Vehicle vehicle, float ambientPressure)
    {
        var result = RcsAnalyzer.Analyze(vehicle, ambientPressure);

        ReturnCachedStageSubstanceLists();
        _cachedRcsStageList.Clear();
        for (int i = 0; i < result.Stages.Count; i++)
        {
            StageRcsInfo stage = result.Stages[i];
            if (stage.Substances != null && stage.Substances.Count > 0)
            {
                List<RcsSubstanceInfo> copy = RentCachedStageSubstanceList();
                copy.AddRange(stage.Substances);
                stage.Substances = copy;
            }
            else
            {
                stage.Substances = null;
            }
            _cachedRcsStageList.Add(stage);
        }

        _cachedRcs = new VehicleRcsAnalysis
        {
            HasRcs = result.HasRcs,
            Stages = _cachedRcsStageList,
            TotalCurrentMass = result.TotalCurrentMass,
            TotalMaxMass = result.TotalMaxMass,
            TotalThrustMax = result.TotalThrustMax,
            TotalMassFlowMax = result.TotalMassFlowMax,
            ExhaustVelocity = result.ExhaustVelocity,
            DeltaV = result.DeltaV
        };

        _rcsStageLookup.Clear();
        foreach (var s in _cachedRcsStageList)
            _rcsStageLookup[s.StageNumber] = s;
    }

    private static List<RcsSubstanceInfo> RentCachedStageSubstanceList()
    {
        List<RcsSubstanceInfo> list = _cachedStageSubstancePool.Count > 0
            ? _cachedStageSubstancePool.Pop()
            : new List<RcsSubstanceInfo>();
        list.Clear();
        _activeCachedStageSubstanceLists.Add(list);
        return list;
    }

    private static void ReturnCachedStageSubstanceLists()
    {
        for (int i = 0; i < _activeCachedStageSubstanceLists.Count; i++)
        {
            var list = _activeCachedStageSubstanceLists[i];
            list.Clear();
            _cachedStageSubstancePool.Push(list);
        }
        _activeCachedStageSubstanceLists.Clear();
    }

    private static void ClearRcs()
    {
        _cachedRcs = null;
        ReturnCachedStageSubstanceLists();
        _cachedRcsStageList.Clear();
        _rcsStageLookup.Clear();
    }

    private static void ClearAll()
    {
        _primary.Clear();
        _secondary.Clear();
        ClearStages();
        ClearRcs();

        PrimaryLabel = "";
        SecondaryLabel = null;
        IsPrimaryCurrentCondition = true;
    }
}
