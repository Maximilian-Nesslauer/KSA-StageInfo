using System;
using System.Collections.Generic;
using System.Diagnostics;
using KSA;
using StageInfo.UI;

namespace StageInfo.Analysis;

internal static class EditorAnalysisCache
{
    private static readonly AnalysisSlot _primary = new();

    // Recompute gate: the editor tab calls Update every frame, but the vehicle
    // only changes on edits. SequencePerformanceList.SetDirty (patched in
    // StageInfoSection) marks the cache dirty on every stock invalidation
    // (part/fuel/sequence/environment changes); body selection and PartTree
    // identity are tracked here. The periodic refresh covers any mutation path
    // that bypasses SetDirty.
    private static bool _dirty = true;
    private static PartTree? _lastParts;
    private static string? _lastBodyId;
    private static long _lastRunTimestamp;

    public static void MarkDirty() => _dirty = true;

    private static VehicleFuelAnalysis? _cachedStages;
    private static readonly List<StageFuelInfo> _cachedStageList = new();
    private static readonly Dictionary<int, StageFuelInfo> _stageLookup = new();

    private static VehicleRcsAnalysis? _cachedRcs;
    private static readonly List<StageRcsInfo> _cachedRcsStageList = new();
    private static readonly Dictionary<int, StageRcsInfo> _rcsStageLookup = new();

    private static readonly Stack<List<RcsSubstanceInfo>> _cachedStageSubstancePool = new();
    private static readonly List<List<RcsSubstanceInfo>> _activeCachedStageSubstanceLists = new();

    public static VehicleBurnAnalysis? Sequences => _primary.Sequences;
    public static VehicleFuelAnalysis? Stages => _cachedStages;
    public static VehicleRcsAnalysis? Rcs => _cachedRcs;

    public static bool TryGetSequenceInfo(int sequenceNumber, out SequenceBurnInfo info)
        => _primary.SequenceLookup.TryGetValue(sequenceNumber, out info);

    public static bool TryGetStageFuelInfo(int stageNumber, out StageFuelInfo info)
        => _stageLookup.TryGetValue(stageNumber, out info);

    public static bool TryGetStageRcsInfo(int stageNumber, out StageRcsInfo info)
        => _rcsStageLookup.TryGetValue(stageNumber, out info);

    public static void Update(PartTree parts)
    {
        bool inputsChanged = !ReferenceEquals(parts, _lastParts)
            || !string.Equals(EditorStageInfoSettings.SelectedBodyId, _lastBodyId, StringComparison.Ordinal);
        long now = Stopwatch.GetTimestamp();
        bool periodicRefresh = now - _lastRunTimestamp > Stopwatch.Frequency;
        if (!_dirty && !inputsChanged && !periodicRefresh)
            return;
        _dirty = false;
        _lastParts = parts;
        _lastRunTimestamp = now;

        if (parts.Moles == null)
        {
            ClearAll();
            return;
        }

        float totalMass = ComputeTotalMass(parts);
        if (totalMass <= 0f)
        {
            ClearAll();
            return;
        }

        var env = EditorStageInfoSettings.ResolveEnvironment(parts);
        _lastBodyId = EditorStageInfoSettings.SelectedBodyId;

        _primary.RunSequenceAnalysis(parts, totalMass, env.Pressure, env.SurfaceGravity, env.PerSequence);

        RunStageAnalysis(parts);
        RunRcsAnalysis(parts, totalMass, env.Pressure);
    }

    public static void Reset()
    {
        ClearAll();
        _cachedStageSubstancePool.Clear();
        _dirty = true;
        _lastParts = null;
        _lastBodyId = null;
        _lastRunTimestamp = 0;
    }

    private static float ComputeTotalMass(PartTree parts)
    {
        var combined = parts.ComputeInertMassPropertiesAsmb()
                     + parts.ComputePropellantMassPropertiesAsmb();
        return combined.Props.Mass;
    }

    private static void RunStageAnalysis(PartTree parts)
    {
        var result = StageFuelAnalyzer.Analyze(parts);

        _cachedStageList.Clear();
        _cachedStageList.AddRange(result.Stages);
        _cachedStages = new VehicleFuelAnalysis { Stages = _cachedStageList };

        _stageLookup.Clear();
        foreach (var s in _cachedStageList)
            _stageLookup[s.StageNumber] = s;
    }

    private static void RunRcsAnalysis(PartTree parts, float totalMass, float ambientPressure)
    {
        var result = RcsAnalyzer.Analyze(parts, totalMass, ambientPressure);

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

    private static void ClearAll()
    {
        _primary.Clear();

        _cachedStages = null;
        _cachedStageList.Clear();
        _stageLookup.Clear();

        _cachedRcs = null;
        ReturnCachedStageSubstanceLists();
        _cachedRcsStageList.Clear();
        _rcsStageLookup.Clear();
    }
}
