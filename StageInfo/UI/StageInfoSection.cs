using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using StageInfo.Analysis;
using StageInfo.Core;
using static StageInfo.UI.StageInfoUiHelpers;

namespace StageInfo.UI;

/// <summary>
/// Integrates StageInfo readouts into the stock staging window, in flight and
/// in the editor. Prefixes draw the environment selector at the top of each
/// tab; transpilers on SequenceList.DrawSequenceContent and
/// ResourceGroupList.DrawResourceGroupContent insert a per-row hook right after
/// each header so the fuel / RCS bars and the detail line render inline in the
/// stock tree. The sequence postfix draws a totals footer; the group postfix
/// draws a per-group block, but only as a fallback: when its transpiler could
/// not be applied, or when stock is in automatic mode (the default) and drew no
/// group tree for the hook to run against. If a transpiler's anchors no longer
/// match after a game update, the mod logs a warning and uses the rows-below
/// fallback. The stock window keeps full ownership of the tree and all edits.
/// </summary>
internal static class StageInfoSection
{
    private static readonly ImColor8 ColorInsufficient = new ImColor8(255, 60, 60, 255);
    private static readonly ImColor8 ColorRcsBar = new ImColor8(70, 150, 230, 255);
    private static readonly ImColor8 ColorRcsLow = new ImColor8(230, 90, 60, 255);
    private const float RcsBarLowFraction = 0.2f;

    private enum DrawHost { None, Flight, Editor }
    private enum FuelBar { None, OwnLineBefore, AtEnd }

    // Which host is currently inside each stock draw method. Set by the
    // prefix, cleared by the finalizer; read by the transpiler hooks and the
    // postfixes. Sequences and groups are separate tabs and never draw at the
    // same time, but each method tracks its own host for clarity.
    private static DrawHost _sequenceHost;
    private static DrawHost _groupHost;

    // Set while VehicleEditingSpace.DrawStageWindow is on the stack. The
    // editor can share the edited vehicle's PartTree, so the list instance
    // alone cannot tell the editor window apart from the flight staging window.
    private static bool _inEditorStageWindow;

    private static bool _sequenceInlineActive;
    private static bool _groupInlineActive;

    // The stock flight staging window opens at 400x300 in the top-right
    // corner. On its first draw we widen it to the burn-control gauge and
    // stretch it down to just above that gauge, once, so the user can still
    // resize afterwards.
    private static bool _windowSizeApplied;
    private const float FallbackWindowWidth = 400f;

    // Enum.GetValues<T>() allocates; cache it.
    private static readonly StageDisplayMode[] AllModes =
        Enum.GetValues<StageDisplayMode>();

    private static string ModeLabel(StageDisplayMode mode) => mode switch
    {
        StageDisplayMode.Auto => "Auto",
        StageDisplayMode.Vac => "VAC",
        StageDisplayMode.Asl => "ASL",
        StageDisplayMode.VacAsl => "VAC + ASL",
        StageDisplayMode.Planning => "Planning",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unhandled StageDisplayMode"),
    };

    public static void ApplyPatches(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(VehicleEditingSpace), nameof(VehicleEditingSpace.DrawStageWindow)),
            prefix: new HarmonyMethod(typeof(StageInfoSection), nameof(DrawStageWindowPrefix)),
            finalizer: new HarmonyMethod(typeof(StageInfoSection), nameof(DrawStageWindowFinalizer)));

        MethodInfo drawSequences =
            AccessTools.Method(typeof(SequenceList), nameof(SequenceList.DrawSequenceContent));
        harmony.Patch(drawSequences,
            prefix: new HarmonyMethod(typeof(StageInfoSection), nameof(DrawSequenceContentPrefix)),
            postfix: new HarmonyMethod(typeof(StageInfoSection), nameof(DrawSequenceContentPostfix)),
            finalizer: new HarmonyMethod(typeof(StageInfoSection), nameof(DrawSequenceContentFinalizer)));
        _sequenceInlineActive = TryPatchTranspiler(harmony, drawSequences,
            nameof(DrawSequenceContentTranspiler), "sequence");

        MethodInfo drawGroups =
            AccessTools.Method(typeof(ResourceGroupList), nameof(ResourceGroupList.DrawResourceGroupContent));
        harmony.Patch(drawGroups,
            prefix: new HarmonyMethod(typeof(StageInfoSection), nameof(DrawResourceGroupContentPrefix)),
            postfix: new HarmonyMethod(typeof(StageInfoSection), nameof(DrawResourceGroupContentPostfix)),
            finalizer: new HarmonyMethod(typeof(StageInfoSection), nameof(DrawResourceGroupContentFinalizer)));
        _groupInlineActive = TryPatchTranspiler(harmony, drawGroups,
            nameof(DrawResourceGroupContentTranspiler), "resource group");

        if (GameReflection.ResourceGroupsWindow_DrawContent != null)
        {
            harmony.Patch(GameReflection.ResourceGroupsWindow_DrawContent,
                prefix: new HarmonyMethod(typeof(StageInfoSection), nameof(WindowSizePrefix)));
        }
        else
        {
            Brutal.Logging.DefaultCategory.Log.Warning(
                "[StageInfo] Flight staging window not found, default sizing skipped.");
        }

        if (DebugConfig.StageInfo)
            Brutal.Logging.DefaultCategory.Log.Debug(
                $"[StageInfo] Staging section patches applied (seqInline={_sequenceInlineActive}, " +
                $"groupInline={_groupInlineActive}).");
    }

    // The transpilers are the only fragile patches; apply each separately so an
    // anchor mismatch after a game update degrades to the rows-below fallback
    // instead of disabling the section.
    private static bool TryPatchTranspiler(Harmony harmony, MethodInfo target,
        string transpilerName, string label)
    {
        try
        {
            harmony.Patch(target,
                transpiler: new HarmonyMethod(typeof(StageInfoSection), transpilerName));
            return true;
        }
        catch (Exception e)
        {
            Brutal.Logging.DefaultCategory.Log.Warning(
                $"[StageInfo] Inline {label} transpiler failed, game UI code changed. " +
                $"Falling back to rows below the tree. ({e.Message})");
            return false;
        }
    }

    public static void Reset()
    {
        _sequenceHost = DrawHost.None;
        _groupHost = DrawHost.None;
        _inEditorStageWindow = false;
        _sequenceInlineActive = false;
        _groupInlineActive = false;
        _windowSizeApplied = false;
    }

    #region Window sizing

    // Runs inside the flight staging window's ImGui Begin/End (DrawContent is
    // called between them), so SetWindowSize/Pos target that window.
    static void WindowSizePrefix()
    {
        if (_windowSizeApplied)
            return;
        _windowSizeApplied = true;
        ApplyInitialWindowSize();
    }

    private static void ApplyInitialWindowSize()
    {
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        float lineHeight = ImGui.GetTextLineHeightWithSpacing();
        float topY = viewport.Pos.Y + lineHeight * 2f;

        // Line the window up directly above the burn-control gauge: same width
        // and same left/right edges, stretched down to just above its top.
        // GaugeCanvas.OnDrawUi places the gauge at viewport.Pos + GetPixelsMin(),
        // so its absolute rect is derived the same way here. Fall back to the
        // stock top-right anchor and half the viewport height if it is absent.
        float targetWidth = FallbackWindowWidth;
        float leftX = (viewport.Pos + viewport.Size).X - targetWidth - lineHeight;
        float bottomLimit = viewport.Pos.Y + viewport.Size.Y * 0.5f;
        foreach (GaugeCanvas canvas in GaugeCanvas.AllCanvases)
        {
            if (canvas.Id != "BurnControl")
                continue;
            float2 gaugeMin = viewport.Pos + canvas.GetPixelsMin();
            float2 gaugeSize = canvas.GetPixelsSize();
            if (gaugeSize.X > 0f)
            {
                targetWidth = gaugeSize.X;
                leftX = gaugeMin.X;
            }
            if (gaugeMin.Y > viewport.Pos.Y)
                bottomLimit = gaugeMin.Y;
            break;
        }

        float2 newPos = new float2(leftX, topY);
        float height = bottomLimit - topY - lineHeight;
        if (height < 200f)
            height = 200f;

        float2 newSize = new float2(targetWidth, height);
        ImGui.SetWindowSize(in newSize, ImGuiCond.Always);
        ImGui.SetWindowPos(newPos, ImGuiCond.Always);

        if (DebugConfig.StageInfo)
            Brutal.Logging.DefaultCategory.Log.Debug(
                $"[StageInfo] Sized staging window to {newSize.X} x {newSize.Y} at ({newPos.X}, {newPos.Y}).");
    }

    #endregion

    #region Host tracking

    static void DrawStageWindowPrefix() => _inEditorStageWindow = true;

    // Finalizer, not postfix, so an ImGui exception inside the editor window
    // cannot leave the flag stuck and mislabel later flight draws.
    static void DrawStageWindowFinalizer() => _inEditorStageWindow = false;

    private static DrawHost ResolveSequenceHost(SequenceList drawnList)
    {
        if (_inEditorStageWindow)
        {
            PartTree? parts = Program.Editor?.EditingSpace.Parts;
            return parts != null && ReferenceEquals(parts.SequenceList, drawnList)
                ? DrawHost.Editor : DrawHost.None;
        }

        Vehicle? vehicle = Program.ControlledVehicle;
        return vehicle != null && ReferenceEquals(vehicle.Parts.SequenceList, drawnList)
            ? DrawHost.Flight : DrawHost.None;
    }

    private static DrawHost ResolveGroupHost(ResourceGroupList drawnList)
    {
        if (_inEditorStageWindow)
        {
            PartTree? parts = Program.Editor?.EditingSpace.Parts;
            return parts != null && ReferenceEquals(parts.ResourceGroupList, drawnList)
                ? DrawHost.Editor : DrawHost.None;
        }

        Vehicle? vehicle = Program.ControlledVehicle;
        return vehicle != null && ReferenceEquals(vehicle.Parts.ResourceGroupList, drawnList)
            ? DrawHost.Flight : DrawHost.None;
    }

    #endregion

    #region Sequence content patches

    static void DrawSequenceContentPrefix(SequenceList __instance)
    {
        _sequenceHost = ResolveSequenceHost(__instance);

        // The selector is drawn here so it lands at the top of the tab, above
        // the stock "Add" button and the sequence tree.
        if (_sequenceHost == DrawHost.Flight)
        {
            AnalysisCache.MarkPanelActive();
            DrawTopSelector(DrawModeSelector);
        }
        else if (_sequenceHost == DrawHost.Editor)
        {
            DrawTopSelector(DrawEditorEnvironmentSelector);
            PartTree? parts = Program.Editor?.EditingSpace.Parts;
            if (parts != null)
                EditorAnalysisCache.Update(parts);
        }
    }

    static void DrawSequenceContentPostfix()
    {
        if (_sequenceHost == DrawHost.Flight)
            DrawFlightFooter();
        else if (_sequenceHost == DrawHost.Editor)
            DrawEditorFooter();
    }

    static void DrawSequenceContentFinalizer() => _sequenceHost = DrawHost.None;

    static IEnumerable<CodeInstruction> DrawSequenceContentTranspiler(
        IEnumerable<CodeInstruction> instructions)
        => InjectAfterHeader(instructions,
            AccessTools.PropertyGetter(typeof(List<Sequence>), "Item"),
            AccessTools.Method(typeof(StageInfoSection), nameof(AfterSequenceHeader)));

    internal static void AfterSequenceHeader(Sequence sequence, bool expanded)
    {
        if (_sequenceHost == DrawHost.Flight)
            DrawFlightSequenceInline(sequence, expanded);
        else if (_sequenceHost == DrawHost.Editor)
            DrawEditorSequenceInline(sequence, expanded);
    }

    private static void DrawFlightSequenceInline(Sequence sequence, bool expanded)
    {
        if (!AnalysisCache.TryGetSequenceInfo(sequence.Number, out var info)
            || info.EngineCount == 0)
            return;

        // Fuel bar on the header row, always: the header stays visible when the
        // node is collapsed. The editor-only env button is absent in flight.
        if (info.MaxFuelMass > 0f)
            DrawFuelProgressBar(info.FuelFraction);

        // The dV / TWR / burn / Isp detail follows the stock tree: shown only
        // when the sequence node is expanded.
        if (!expanded)
            return;

        bool hasSecondary = AnalysisCache.TryGetSecondarySequenceInfo(sequence.Number, out var secondaryInfo)
            && secondaryInfo.EngineCount > 0;

        // The selector at the top already names the single condition, so only
        // tag the rows when both are shown and need telling apart.
        string primaryLabel = hasSecondary ? AnalysisCache.PrimaryLabel : "";

        BurnSequenceAllocation? alloc =
            AnalysisCache.TryGetBurnAllocation(sequence.Number, out var pa) ? pa : null;
        bool primaryDimmed = hasSecondary && !AnalysisCache.IsPrimaryCurrentCondition;
        DrawInlineInfoLine(info, primaryLabel, alloc, primaryDimmed, FuelBar.None);

        if (hasSecondary)
        {
            BurnSequenceAllocation? secondaryAlloc =
                AnalysisCache.TryGetSecondaryBurnAllocation(sequence.Number, out var sa) ? sa : null;
            DrawInlineInfoLine(secondaryInfo, AnalysisCache.SecondaryLabel ?? "",
                secondaryAlloc, AnalysisCache.IsPrimaryCurrentCondition, FuelBar.None);
        }
    }

    private static void DrawEditorSequenceInline(Sequence sequence, bool expanded)
    {
        if (!expanded)
            return;

        if (!EditorAnalysisCache.TryGetSequenceInfo(sequence.Number, out var info)
            || info.EngineCount == 0)
            return;

        // The stock editor header carries the env button on the right, so the
        // fuel bar goes on its own line above the detail instead of the header.
        DrawInlineInfoLine(info, "", alloc: null, isDimmed: false, FuelBar.OwnLineBefore);
    }

    private static void DrawInlineInfoLine(in SequenceBurnInfo info, string label,
        BurnSequenceAllocation? alloc, bool isDimmed, FuelBar fuelBar)
    {
        ImGui.Indent();
        PushDimmedTextColor(extraDim: isDimmed);

        if (fuelBar == FuelBar.OwnLineBefore && info.MaxFuelMass > 0f)
            DrawFuelProgressBar(info.FuelFraction, sameLine: false);

        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float availWidth = ImGui.GetContentRegionAvail().X;
        float lineX = 0f;

        if (alloc != null)
        {
            float ratio = alloc.Value.SequenceTotalDv > 0f
                ? alloc.Value.AllocatedDv / alloc.Value.SequenceTotalDv
                : 1f;
            ImColor8 burnColor = isDimmed
                ? new ImColor8(180, 180, 180, 160)
                : GetBurnGradientColor(ratio);

            string body = string.Format(Inv, "Burn allocated {0:N0} / {1:N0} m/s sequence deltaV",
                alloc.Value.AllocatedDv, info.DeltaV);
            DrawInfoSegmentColored(WithLabel(label, body), burnColor, ref lineX, availWidth, spacing);
        }
        else
        {
            string body = string.Format(Inv, "Delta V: {0:N0} m/s", info.DeltaV);
            DrawInfoSegment(WithLabel(label, body), ref lineX, availWidth, spacing);
        }

        DrawInfoSegment(string.Format(Inv, "TWR: {0:F2}", info.Twr),
            ref lineX, availWidth, spacing);

        float displayBurnTime = alloc?.AllocatedBurnTime ?? info.BurnTime;
        DrawInfoSegment(string.Format(Inv, "Burn: {0}", FormatBurnTime(displayBurnTime)),
            ref lineX, availWidth, spacing);

        DrawInfoSegment(string.Format(Inv, "ISP: {0:F0}s", info.Isp),
            ref lineX, availWidth, spacing);

        if (fuelBar == FuelBar.AtEnd && info.MaxFuelMass > 0f)
            DrawFuelProgressBar(info.FuelFraction);

        ImGui.PopStyleColor();
        ImGui.Unindent();
    }

    #endregion

    #region Sequence footer

    private static void DrawFlightFooter()
    {
#if DEBUG
        long perfStart = DebugConfig.Performance ? Stopwatch.GetTimestamp() : 0;
#endif
        var analysis = AnalysisCache.Sequences;
        if (analysis != null && analysis.Value.Sequences.Count > 0)
        {
            ImGui.Separator();

            if (!_sequenceInlineActive)
            {
                DrawFlightFallbackRows();
                ImGui.Separator();
            }

            bool hasSecondary = AnalysisCache.SecondarySequences != null;
            DrawTotalLine(analysis.Value, AnalysisCache.BurnAnalysis,
                AnalysisCache.PrimaryLabel,
                hasSecondary && !AnalysisCache.IsPrimaryCurrentCondition);

            if (hasSecondary)
            {
                DrawTotalLine(AnalysisCache.SecondarySequences!.Value,
                    AnalysisCache.SecondaryBurnAnalysis,
                    AnalysisCache.SecondaryLabel ?? "",
                    AnalysisCache.IsPrimaryCurrentCondition);
            }

            var rcs = AnalysisCache.Rcs;
            if (rcs is { HasRcs: true, DeltaV: > 0f })
                DrawRcsFooterLine(rcs.Value);
        }

#if DEBUG
        if (DebugConfig.Performance)
            PerfTracker.Record("StageInfoSection.DrawFlightFooter",
                Stopwatch.GetTimestamp() - perfStart);
#endif
    }

    // Fallback when the inline transpiler could not be applied: the same
    // per-sequence lines, prefixed with the sequence number, below the tree.
    private static void DrawFlightFallbackRows()
    {
        var analysis = AnalysisCache.Sequences;
        if (analysis == null)
            return;

        bool hasSecondary = AnalysisCache.SecondarySequences != null;
        bool primaryDimmed = hasSecondary && !AnalysisCache.IsPrimaryCurrentCondition;

        // Reverse so rows line up with the stock tree above, which draws the
        // highest sequence number first.
        List<SequenceBurnInfo> rows = analysis.Value.Sequences;
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            SequenceBurnInfo info = rows[i];
            if (info.EngineCount == 0)
                continue;

            BurnSequenceAllocation? alloc =
                AnalysisCache.TryGetBurnAllocation(info.SequenceNumber, out var pa) ? pa : null;
            string label = WithLabel(string.Format(Inv, "Seq {0}", info.SequenceNumber),
                AnalysisCache.PrimaryLabel);
            DrawInlineInfoLine(info, label, alloc,
                primaryDimmed || !info.IsActivated, FuelBar.AtEnd);

            if (hasSecondary
                && AnalysisCache.TryGetSecondarySequenceInfo(info.SequenceNumber, out var secondaryInfo)
                && secondaryInfo.EngineCount > 0)
            {
                BurnSequenceAllocation? secondaryAlloc =
                    AnalysisCache.TryGetSecondaryBurnAllocation(info.SequenceNumber, out var sa) ? sa : null;
                string secondaryLabel = WithLabel(string.Format(Inv, "Seq {0}", info.SequenceNumber),
                    AnalysisCache.SecondaryLabel ?? "");
                DrawInlineInfoLine(secondaryInfo, secondaryLabel, secondaryAlloc,
                    AnalysisCache.IsPrimaryCurrentCondition || !info.IsActivated, FuelBar.AtEnd);
            }
        }
    }

    private static void DrawEditorFooter()
    {
#if DEBUG
        long perfStart = DebugConfig.Performance ? Stopwatch.GetTimestamp() : 0;
#endif
        var analysis = EditorAnalysisCache.Sequences;
        if (analysis != null && analysis.Value.Sequences.Count > 0)
        {
            ImGui.Separator();

            if (!_sequenceInlineActive)
            {
                List<SequenceBurnInfo> rows = analysis.Value.Sequences;
                for (int i = rows.Count - 1; i >= 0; i--)
                {
                    SequenceBurnInfo info = rows[i];
                    if (info.EngineCount == 0)
                        continue;
                    DrawInlineInfoLine(info, string.Format(Inv, "Seq {0}", info.SequenceNumber),
                        alloc: null, isDimmed: false, FuelBar.AtEnd);
                }
                ImGui.Separator();
            }

            float spacing = ImGui.GetStyle().ItemSpacing.X;
            float availWidth = ImGui.GetContentRegionAvail().X;
            float lineX = 0f;

            DrawInfoSegment(string.Format(Inv, "Total Delta V: {0:N0} m/s", analysis.Value.TotalDeltaV),
                ref lineX, availWidth, spacing);
            DrawInfoSegment(string.Format(Inv, "Burn Time: {0}", FormatBurnTime(analysis.Value.TotalBurnTime)),
                ref lineX, availWidth, spacing);

            var rcs = EditorAnalysisCache.Rcs;
            if (rcs is { HasRcs: true, DeltaV: > 0f })
                DrawRcsFooterLine(rcs.Value);
        }

#if DEBUG
        if (DebugConfig.Performance)
            PerfTracker.Record("StageInfoSection.DrawEditorFooter",
                Stopwatch.GetTimestamp() - perfStart);
#endif
    }

    #endregion

    #region Selectors

    private static void DrawTopSelector(Action selector)
    {
        ImGui.TextDisabled("StageInfo"u8);
        ImGui.SameLine();
        selector();
    }

    private static void DrawModeSelector()
    {
        ImGui.PushItemWidth(110f);
        if (ImGui.BeginCombo("##StageInfoMode"u8, ModeLabel(StageInfoSettings.Mode)))
        {
            foreach (StageDisplayMode mode in AllModes)
            {
                bool isSelected = StageInfoSettings.Mode == mode;
                if (ImGui.Selectable(ModeLabel(mode), isSelected))
                    StageInfoSettings.Mode = mode;
            }
            ImGui.EndCombo();
        }
        ImGui.PopItemWidth();

        if (StageInfoSettings.Mode == StageDisplayMode.Planning)
        {
            ImGui.SameLine();
            DrawPlanningBodySelector();
        }
    }

    private static void DrawPlanningBodySelector()
    {
        List<Astronomical> bodies = StageInfoSettings.GetCelestialBodies();
        if (bodies.Count == 0)
        {
            ImGui.Text("(no bodies)");
            return;
        }

        // Default selection is set during ResolveEnvironment; may still be null
        // on the very first frame before the analysis tick runs.
        string currentName = StageInfoSettings.SelectedBodyId ?? bodies[0].Id;

        ImGui.PushItemWidth(140f);
        if (ImGui.BeginCombo("##PlanningBody"u8, currentName))
        {
            for (int i = 0; i < bodies.Count; i++)
            {
                string bodyId = bodies[i].Id;
                bool isSelected = bodyId == StageInfoSettings.SelectedBodyId;
                if (ImGui.Selectable(bodyId, isSelected))
                    StageInfoSettings.SelectedBodyId = bodyId;
            }
            ImGui.EndCombo();
        }
        ImGui.PopItemWidth();
    }

    private static void DrawEditorEnvironmentSelector()
    {
        List<Astronomical> bodies = EditorStageInfoSettings.GetCelestialBodies();
        if (bodies.Count == 0)
        {
            ImGui.Text("(no bodies)");
            return;
        }

        string currentName = EditorStageInfoSettings.SelectedBodyId ?? bodies[0].Id;

        ImGui.PushItemWidth(140f);
        if (ImGui.BeginCombo("##EditorPlanningBody"u8, currentName))
        {
            for (int i = 0; i < bodies.Count; i++)
            {
                string bodyId = bodies[i].Id;
                bool isSelected = bodyId == EditorStageInfoSettings.SelectedBodyId;
                if (ImGui.Selectable(bodyId, isSelected))
                    EditorStageInfoSettings.SelectedBodyId = bodyId;
            }
            ImGui.EndCombo();
        }
        ImGui.PopItemWidth();

        if (EditorStageInfoSettings.SelectedBodyHasAtmosphere())
        {
            ImGui.SameLine();
            bool useVacuum = EditorStageInfoSettings.UseVacuum;
            if (ImGui.Checkbox("Vacuum"u8, ref useVacuum))
                EditorStageInfoSettings.UseVacuum = useVacuum;
        }
    }

    #endregion

    #region Resource group patches

    static void DrawResourceGroupContentPrefix(ResourceGroupList __instance)
    {
        _groupHost = ResolveGroupHost(__instance);

        if (_groupHost == DrawHost.Flight)
        {
            AnalysisCache.MarkPanelActive();
        }
        else if (_groupHost == DrawHost.Editor)
        {
            PartTree? parts = Program.Editor?.EditingSpace.Parts;
            if (parts != null)
                EditorAnalysisCache.Update(parts);
        }
    }

    // Fallback block below the tree, used only when the inline hook cannot run:
    // the transpiler failed, or stock is in automatic mode and drew no groups.
    static void DrawResourceGroupContentPostfix()
    {
        if (_groupHost == DrawHost.None)
            return;
        if (_groupInlineActive && !VehicleEditor.AutomaticResourceGroupMode)
            return;

        bool flight = _groupHost == DrawHost.Flight;
        VehicleFuelAnalysis? stages = flight ? AnalysisCache.Stages : EditorAnalysisCache.Stages;
        if (stages == null)
            return;

        ImGui.Separator();
        ImGui.TextDisabled("StageInfo"u8);
        List<StageFuelInfo> rows = stages.Value.Stages;
        for (int i = 0; i < rows.Count; i++)
            DrawGroupDetailLine(rows[i], TryGetGroupRcs(rows[i].StageNumber, flight), withGroupPrefix: true);
    }

    static void DrawResourceGroupContentFinalizer() => _groupHost = DrawHost.None;

    static IEnumerable<CodeInstruction> DrawResourceGroupContentTranspiler(
        IEnumerable<CodeInstruction> instructions)
        => InjectAfterHeader(instructions,
            AccessTools.PropertyGetter(typeof(List<ResourceGroup>), "Item"),
            AccessTools.Method(typeof(StageInfoSection), nameof(AfterGroupHeader)));

    internal static void AfterGroupHeader(ResourceGroup group, bool expanded)
    {
        if (_groupHost == DrawHost.None)
            return;

        bool flight = _groupHost == DrawHost.Flight;
        StageFuelInfo? fuel = TryGetGroupFuel(group.Number, flight);
        StageRcsInfo? rcs = TryGetGroupRcs(group.Number, flight);

        // Fuel + RCS bars on the header row, always visible when collapsed.
        DrawGroupHeaderBars(fuel, rcs);

        if (!expanded || fuel == null)
            return;
        DrawGroupDetailLine(fuel.Value, rcs, withGroupPrefix: false);
    }

    private static StageFuelInfo? TryGetGroupFuel(int number, bool flight)
    {
        if (flight)
            return AnalysisCache.TryGetStageFuelInfo(number, out var f) ? f : (StageFuelInfo?)null;
        return EditorAnalysisCache.TryGetStageFuelInfo(number, out var e) ? e : (StageFuelInfo?)null;
    }

    private static StageRcsInfo? TryGetGroupRcs(int number, bool flight)
    {
        if (flight)
            return AnalysisCache.TryGetStageRcsInfo(number, out var r) ? r : (StageRcsInfo?)null;
        return EditorAnalysisCache.TryGetStageRcsInfo(number, out var e) ? e : (StageRcsInfo?)null;
    }

    private static void DrawGroupHeaderBars(StageFuelInfo? fuelOpt, StageRcsInfo? rcsOpt)
    {
        bool hasFuel = fuelOpt is { MaxFuelMass: > 0f };
        bool hasRcs = rcsOpt is { MaxMass: > 0f };
        if (!hasFuel && !hasRcs)
            return;

        ImGui.SameLine();
        float availWidth = ImGui.GetContentRegionAvail().X;
        float fuelTextWidth = ImGui.CalcTextSize("100% fuel"u8).X + 8f;
        float rcsTextWidth = ImGui.CalcTextSize("RCS 100%"u8).X + 8f;

        float fuelBarWidth = 0f;
        float rcsBarWidth = 0f;
        if (hasFuel && hasRcs)
        {
            float remaining = availWidth - fuelTextWidth - rcsTextWidth;
            if (remaining < 60f)
            {
                hasRcs = false;
                fuelBarWidth = availWidth - fuelTextWidth;
            }
            else
            {
                fuelBarWidth = remaining * 0.7f;
                rcsBarWidth = remaining - fuelBarWidth;
            }
        }
        else if (hasFuel)
        {
            fuelBarWidth = availWidth - fuelTextWidth;
        }
        else
        {
            rcsBarWidth = availWidth - rcsTextWidth;
        }

        bool renderFuel = hasFuel && fuelBarWidth >= 30f;
        bool renderRcs = hasRcs && rcsBarWidth >= 30f;
        if (!renderFuel && !renderRcs)
        {
            // Committed SameLine with no widget would pull the next row up.
            ImGui.NewLine();
            return;
        }

        float lineHeight = ImGui.GetTextLineHeight();
        float barHeight = lineHeight * 0.6f;
        float yOffset = (lineHeight - barHeight) * 0.5f;
        float2 cursorStart = ImGui.GetCursorPos();

        if (renderFuel)
        {
            StageFuelInfo fuel = fuelOpt!.Value;
            ImGui.SetCursorPos(new float2(cursorStart.X, cursorStart.Y + yOffset));
            ImGui.ProgressBar(fuel.FuelFraction,
                new float2?(new float2(fuelBarWidth, barHeight)), ""u8);
            ImGui.SameLine();
            ImGui.SetCursorPosY(cursorStart.Y);
            ImGui.Text(string.Format(Inv, "{0}% fuel",
                (int)MathF.Round(fuel.FuelFraction * 100f)));
        }

        if (renderRcs)
        {
            StageRcsInfo rcs = rcsOpt!.Value;
            if (renderFuel) ImGui.SameLine();
            float2 cur = ImGui.GetCursorPos();
            ImGui.SetCursorPos(new float2(cur.X, cursorStart.Y + yOffset));
            ImColor8 barColor = rcs.FuelFraction < RcsBarLowFraction ? ColorRcsLow : ColorRcsBar;
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, barColor);
            ImGui.ProgressBar(rcs.FuelFraction,
                new float2?(new float2(rcsBarWidth, barHeight)), ""u8);
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.SetCursorPosY(cursorStart.Y);
            ImGui.Text(string.Format(Inv, "RCS {0}%",
                (int)MathF.Round(rcs.FuelFraction * 100f)));
        }
    }

    private static void DrawGroupDetailLine(StageFuelInfo v, StageRcsInfo? rcs, bool withGroupPrefix)
    {
        if (!withGroupPrefix)
            ImGui.Indent();

        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float availWidth = ImGui.GetContentRegionAvail().X;
        float lineX = 0f;

        if (withGroupPrefix)
            DrawInfoSegment(string.Format(Inv, "Group {0}:", v.StageNumber),
                ref lineX, availWidth, spacing);

        DrawInfoSegment(string.Format(Inv, "Mass: {0:N0} kg", v.DryMass + v.CurrentFuelMass),
            ref lineX, availWidth, spacing);

        if (v.MaxFuelMass > 0f)
            DrawInfoSegment(string.Format(Inv, "Fuel: {0:N0}/{1:N0} kg", v.CurrentFuelMass, v.MaxFuelMass),
                ref lineX, availWidth, spacing);

        if (rcs is { } rcsInfo && rcsInfo.Substances is { Count: > 0 } substances)
        {
            DrawInfoSegment("RCS:", ref lineX, availWidth, spacing);
            for (int i = 0; i < substances.Count; i++)
            {
                RcsSubstanceInfo s = substances[i];
                // The separator is part of the segment so a wrap cannot orphan
                // a lone "|" at the start of a line.
                string segText = string.Format(Inv, "{0}{1} {2:N0}/{3:N0} kg",
                    i > 0 ? "| " : "", s.ShortName, s.CurrentMass, s.MaxMass);
                DrawInfoSegment(segText, ref lineX, availWidth, spacing);
                DrawSubstanceTooltip(s);
            }
        }

        if (v.EngineCount > 0)
            DrawInfoSegment(string.Format(Inv, "Engines: {0}", v.EngineCount),
                ref lineX, availWidth, spacing);

        if (v.DecouplerCount > 0)
            DrawInfoSegment(string.Format(Inv, "Decouplers: {0}", v.DecouplerCount),
                ref lineX, availWidth, spacing);

        if (!withGroupPrefix)
            ImGui.Unindent();
    }

    #endregion

    #region Transpiler

    /// <summary>
    /// Inserts a call to <paramref name="hook"/>(item, expanded) into the
    /// per-item loop of a stock staging draw method, right where the
    /// expanded-content branch begins. Anchors, verified against the
    /// 2026.7.4.4860 IL of both DrawSequenceContent and
    /// DrawResourceGroupContent: the item local is stored right after the first
    /// call to <paramref name="itemGetter"/> (the List indexer); the expanded
    /// flag is stored right after the first ImGui.TreeNodeEx call; the
    /// insertion point is that flag's first re-load (the `if (expanded)` /
    /// `if (!expanded) continue` check). The hook is passed the item and that
    /// same expanded flag. Throws on any mismatch so ApplyPatches can fall back.
    /// </summary>
    private static List<CodeInstruction> InjectAfterHeader(
        IEnumerable<CodeInstruction> instructions, MethodInfo itemGetter, MethodInfo hook)
    {
        var codes = new List<CodeInstruction>(instructions);

        int itemLocal = -1;
        for (int i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].Calls(itemGetter) && IsStloc(codes[i + 1]))
            {
                itemLocal = LocalIndex(codes[i + 1]);
                break;
            }
        }
        if (itemLocal < 0)
            throw new InvalidOperationException("item local not found");

        // Clone an existing value-load of the item local so the emitted operand
        // matches exactly whatever Harmony read. The item is a reference type,
        // loaded by value (ldloc, not ldloca) throughout the loop body.
        CodeInstruction? itemLoad = null;
        foreach (CodeInstruction ci in codes)
            if (IsLdloc(ci) && LocalIndex(ci) == itemLocal)
            {
                itemLoad = ci.Clone();
                break;
            }
        if (itemLoad == null)
            throw new InvalidOperationException("item local load not found");

        int expandedLocal = -1, treeNodeStore = -1;
        for (int i = 0; i < codes.Count - 1; i++)
        {
            if ((codes[i].opcode == OpCodes.Call || codes[i].opcode == OpCodes.Callvirt)
                && codes[i].operand is MethodInfo { Name: "TreeNodeEx" }
                && IsStloc(codes[i + 1]))
            {
                treeNodeStore = i + 1;
                expandedLocal = LocalIndex(codes[i + 1]);
                break;
            }
        }
        if (expandedLocal < 0)
            throw new InvalidOperationException("TreeNodeEx result store not found");

        int insertAt = -1;
        for (int i = treeNodeStore + 1; i < codes.Count; i++)
        {
            if (IsLdloc(codes[i]) && LocalIndex(codes[i]) == expandedLocal)
            {
                insertAt = i;
                break;
            }
        }
        if (insertAt < 0)
            throw new InvalidOperationException("expanded flag re-load not found");

        // A branch above the insertion point (e.g. the flight path skipping the
        // editor-only block) jumps to the re-load; the labels must move onto the
        // first inserted instruction or the hook would be skipped.
        itemLoad.MoveLabelsFrom(codes[insertAt]);

        // codes[insertAt] is the expanded-flag load (now label-free); clone it
        // so the hook can gate its detail on whether the node is expanded.
        CodeInstruction expandedLoad = codes[insertAt].Clone();

        codes.Insert(insertAt, itemLoad);
        codes.Insert(insertAt + 1, expandedLoad);
        codes.Insert(insertAt + 2, new CodeInstruction(OpCodes.Call, hook));

        return codes;
    }

    private static bool IsStloc(CodeInstruction ci) =>
        ci.opcode == OpCodes.Stloc_0 || ci.opcode == OpCodes.Stloc_1
        || ci.opcode == OpCodes.Stloc_2 || ci.opcode == OpCodes.Stloc_3
        || ci.opcode == OpCodes.Stloc_S || ci.opcode == OpCodes.Stloc;

    private static bool IsLdloc(CodeInstruction ci) =>
        ci.opcode == OpCodes.Ldloc_0 || ci.opcode == OpCodes.Ldloc_1
        || ci.opcode == OpCodes.Ldloc_2 || ci.opcode == OpCodes.Ldloc_3
        || ci.opcode == OpCodes.Ldloc_S || ci.opcode == OpCodes.Ldloc;

    private static int LocalIndex(CodeInstruction ci)
    {
        if (ci.opcode == OpCodes.Stloc_0 || ci.opcode == OpCodes.Ldloc_0) return 0;
        if (ci.opcode == OpCodes.Stloc_1 || ci.opcode == OpCodes.Ldloc_1) return 1;
        if (ci.opcode == OpCodes.Stloc_2 || ci.opcode == OpCodes.Ldloc_2) return 2;
        if (ci.opcode == OpCodes.Stloc_3 || ci.opcode == OpCodes.Ldloc_3) return 3;
        return ci.operand switch
        {
            LocalBuilder lb => lb.LocalIndex,
            LocalVariableInfo lvi => lvi.LocalIndex,
            byte b => b,
            int n => n,
            _ => -1,
        };
    }

    #endregion

    #region Shared drawing

    private static void DrawTotalLine(VehicleBurnAnalysis sequences, BurnAnalysis? burnAnalysis,
        string label, bool isDimmed)
    {
        if (isDimmed)
            PushDimmedTextColor(extraDim: true);

        string prefix = string.IsNullOrEmpty(label) ? "" : label + " ";
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float availWidth = ImGui.GetContentRegionAvail().X;
        float lineX = 0f;

        if (burnAnalysis != null)
        {
            var burn = burnAnalysis.Value;
            DrawInfoSegment(string.Format(Inv, "{0}Total Delta V: {1:N0} m/s",
                prefix, sequences.TotalDeltaV),
                ref lineX, availWidth, spacing);

            DrawInfoSegment("|", ref lineX, availWidth, spacing);

            if (burn.IsSufficient)
            {
                DrawInfoSegment(string.Format(Inv,
                    "Burn: {0:N0} m/s  Burn Time: {1}",
                    burn.RequiredDv, FormatBurnTime(burn.TotalBurnTime)),
                    ref lineX, availWidth, spacing);
            }
            else
            {
                DrawInfoSegmentColored(string.Format(Inv,
                    "Burn: {0:N0} m/s  INSUFFICIENT", burn.RequiredDv),
                    ColorInsufficient, ref lineX, availWidth, spacing);
            }
        }
        else
        {
            DrawInfoSegment(string.Format(Inv,
                "{0}Total Delta V: {1:N0} m/s  Burn Time: {2}",
                prefix, sequences.TotalDeltaV, FormatBurnTime(sequences.TotalBurnTime)),
                ref lineX, availWidth, spacing);
        }

        if (isDimmed)
            ImGui.PopStyleColor();
    }

    private static void DrawRcsFooterLine(VehicleRcsAnalysis rcs)
    {
        bool lowFraction = rcs.TotalMaxMass > 0f
            && rcs.TotalCurrentMass / rcs.TotalMaxMass < RcsBarLowFraction;
        if (lowFraction)
            ImGui.PushStyleColor(ImGuiCol.Text, ColorRcsLow);

        ImGui.Text(string.Format(Inv, "RCS dV ~{0:N0} m/s", rcs.DeltaV));
        DrawRcsEngineeringTooltip(rcs);

        if (lowFraction)
            ImGui.PopStyleColor();
    }

    private static void DrawRcsEngineeringTooltip(VehicleRcsAnalysis rcs)
    {
        if (!ImGui.IsItemHovered())
            return;

        // KSA. qualifier is needed: Brutal.ImGuiApi also exports Constants.
        float ispS = rcs.ExhaustVelocity / (float)KSA.Constants.STANDARD_GRAVITY;
        ImGui.BeginTooltip();
        ImGui.Text(string.Format(Inv, "Effective ISP: ~{0:N0} s", ispS));
        ImGui.Text(string.Format(Inv, "Vehicle propellant: {0:N0} / {1:N0} kg",
            rcs.TotalCurrentMass, rcs.TotalMaxMass));
        ImGui.Text(string.Format(Inv, "Scalar peak thrust: {0:N1} kN",
            rcs.TotalThrustMax / 1000f));
        ImGui.Text("(upper bound; assumes prograde-only burn)");
        ImGui.EndTooltip();
    }

    private static void DrawSubstanceTooltip(RcsSubstanceInfo s)
    {
        if (!ImGui.IsItemHovered())
            return;

        float frac = s.MaxMass > 0f ? s.CurrentMass / s.MaxMass : 0f;
        ImGui.BeginTooltip();
        ImGui.Text(s.Name);
        ImGui.Text(string.Format(Inv, "{0:N0} / {1:N0} kg ({2:P0})",
            s.CurrentMass, s.MaxMass, frac));
        ImGui.EndTooltip();
    }

    /// <summary>ratio=0 green -> 0.5 yellow -> 1.0 red.</summary>
    private static ImColor8 GetBurnGradientColor(float ratio)
    {
        ratio = Math.Clamp(ratio, 0f, 1f);
        byte r, g, b;
        if (ratio <= 0.5f)
        {
            float t = ratio * 2f;
            r = (byte)(80 + 175 * t);
            g = 220;
            b = (byte)(80 - 80 * t);
        }
        else
        {
            float t = (ratio - 0.5f) * 2f;
            r = 255;
            g = (byte)(220 - 160 * t);
            b = (byte)(60 * t);
        }
        return new ImColor8(r, g, b, 255);
    }

    private static string WithLabel(string? label, string body)
        => string.IsNullOrEmpty(label) ? body : label + " " + body;

    #endregion
}
