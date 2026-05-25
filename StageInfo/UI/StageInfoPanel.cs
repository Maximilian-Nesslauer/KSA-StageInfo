using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using MethodInvoker = System.Reflection.MethodInvoker;
using Brutal.ImGuiApi;
using Brutal.Logging;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using StageInfo.Analysis;
using StageInfo.Core;
using static StageInfo.UI.StageInfoUiHelpers;

namespace StageInfo.UI;

/// <summary>
/// Replaces StagingWindow.DrawContent. Sequences first (with dV/TWR/burn/Isp),
/// separator, then Stages (fuel pool + mass + counts). Data from AnalysisCache.
/// </summary>
internal static class StageInfoPanel
{
    private static readonly ImColor8 ColorInsufficient = new ImColor8(255, 60, 60, 255);
    private static readonly ImColor8 ColorRcsBar = new ImColor8(70, 150, 230, 255);
    private static readonly ImColor8 ColorRcsLow = new ImColor8(230, 90, 60, 255);
    private const float RcsBarLowFraction = 0.2f;

    // Stock default is (400, 300), which truncates the per-sequence info line
    // and barely fits two stages. The width is overridden at runtime to match
    // the BurnControl gauge when found; height stays at this default.
    private static readonly float2 DefaultInitialWindowSize = new float2(700f, 700f);

    private static bool _runtimeApplied;

    // Closed-generic invokers for StagingWindow.DrawComponent<T>. Order
    // mirrors stock's per-part call order (Thruster, Engine, Decoupler).
    private static readonly Type[] DrawComponentTypes =
    {
        typeof(ThrusterController),
        typeof(EngineController),
        typeof(Decoupler),
    };

    private static readonly MethodInvoker?[] _drawComponentInvokers =
        new MethodInvoker?[DrawComponentTypes.Length];

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

    /// <summary>
    /// Applies the StagingWindow.DrawContent prefix and prepares the
    /// reflection invokers. Caller should have already passed
    /// GameReflection.ValidatePanelTargets().
    /// </summary>
    public static void ApplyPatches(Harmony harmony)
    {
        MethodInfo openComponent = GameReflection.StagingWindow_DrawComponentOpen!;
        for (int i = 0; i < DrawComponentTypes.Length; i++)
            _drawComponentInvokers[i] = MethodInvoker.Create(
                openComponent.MakeGenericMethod(DrawComponentTypes[i]));

        harmony.Patch(GameReflection.StagingWindow_DrawContent!,
            prefix: new HarmonyMethod(typeof(StageInfoPanel), nameof(DrawContentPrefix)));

        if (DebugConfig.StageInfo)
            DefaultCategory.Log.Debug("[StageInfo] Panel patch applied.");
    }

    public static void Reset()
    {
        // Invokers are reassigned on next ApplyPatches; clear them so a
        // partial reload can't reuse a stale binding.
        Array.Clear(_drawComponentInvokers);

        // Re-arm so a reload runs ApplyInitialWindowSize again. The window's
        // ImGui size state itself can't be reverted from outside Begin/End,
        // and stock's SetNextWindowSize(_initialSize, Once) is already spent
        // for this ImGui session, so there is nothing further to restore.
        _runtimeApplied = false;
    }

    // First-frame setup: pick a width that matches the BurnControl gauge,
    // compute the stock anchor-from-right position so the wider window stays
    // inside the viewport, and apply both via ImGui.SetWindowSize/SetWindowPos
    // with ImGuiCond.Always. Runs inside the Begin/End scope of OnDrawUi so
    // the resize takes effect on the current frame's layout.
    private static void ApplyInitialWindowSize()
    {
        float targetWidth = DefaultInitialWindowSize.X;
        foreach (GaugeCanvas canvas in GaugeCanvas.AllCanvases)
        {
            if (canvas.Id == "BurnControl")
            {
                float w = canvas.GetPixelsSize().X;
                if (w > 0f)
                    targetWidth = w;
                break;
            }
        }

        float2 newSize = new float2(targetWidth, DefaultInitialWindowSize.Y);

        // Mirror StagingWindow's constructor formula: anchor to viewport's
        // top-right with a line-height inset.
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        float lineHeight = ImGui.GetTextLineHeightWithSpacing();
        float2 newPos = new float2(
            (viewport.Pos + viewport.Size).X - newSize.X - lineHeight,
            viewport.Pos.Y + lineHeight * 2f);

        ImGui.SetWindowSize(in newSize, ImGuiCond.Always);
        ImGui.SetWindowPos(newPos, ImGuiCond.Always);

        if (DebugConfig.StageInfo)
            DefaultCategory.Log.Debug(
                $"[StageInfo] Applied window size {newSize.X} x {newSize.Y} at ({newPos.X}, {newPos.Y}).");
    }

    #region DrawContent

    static bool DrawContentPrefix(object __instance, Viewport viewport)
    {
#if DEBUG
        long perfStart = DebugConfig.Performance ? Stopwatch.GetTimestamp() : 0;
#endif
        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle == null)
            return false;

        if (!_runtimeApplied)
        {
            _runtimeApplied = true;
            ApplyInitialWindowSize();
        }

        AnalysisCache.MarkPanelActive();

        ClearPartHighlights(vehicle);
        DrawModeSelector();

        bool hasSecondary = AnalysisCache.SecondarySequences != null;
        bool hasRcsFooter = AnalysisCache.Rcs is { HasRcs: true, DeltaV: > 0f };
        float footerLines = (hasSecondary ? 2f : 1f) + (hasRcsFooter ? 1f : 0f);
        float footerHeight = ImGui.GetTextLineHeightWithSpacing() * footerLines + 4f;
        float tableHeight = ImGui.GetContentRegionAvail().Y - footerHeight;
        if (tableHeight < 50f)
            tableHeight = 50f;

        ImGuiTableFlags flags = ImGuiTableFlags.BordersV
            | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.BordersOuterH
            | ImGuiTableFlags.NoBordersInBody
            | ImGuiTableFlags.ScrollY;

        if (!ImGui.BeginTable("stagesequences"u8, 1, flags,
                new float2?(new float2(0f, tableHeight))))
        {
            // BeginTable can fail in degenerate layouts; let stock try its
            // own version so the window isn't blank below the mode selector.
            return true;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(vehicle.Id, ImGuiTableColumnFlags.NoHide);
        ImGui.TableHeadersRow();

        DrawSequencesSection(vehicle, __instance);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Separator();

        DrawStagesSection(vehicle, __instance);

        ImGui.EndTable();

        DrawTotalFooter();

#if DEBUG
        if (DebugConfig.Performance)
            PerfTracker.Record("StageInfoPanel.DrawContent", Stopwatch.GetTimestamp() - perfStart);
#endif
        return false;
    }

    private static void ClearPartHighlights(Vehicle vehicle)
    {
        ReadOnlySpan<Part> parts = vehicle.Parts.Parts;
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i].HighlightedForSequence = false;
            parts[i].HighlightedForStage = false;
        }
    }

    #endregion

    #region Mode Selector

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
            DrawBodySelector();
        }
    }

    private static void DrawBodySelector()
    {
        List<Astronomical> bodies = StageInfoSettings.GetCelestialBodies();
        if (bodies.Count == 0)
        {
            ImGui.Text("(no bodies)");
            return;
        }

        // Default selection is set during ResolveEnvironment; may still be null on
        // the very first frame before Update runs.
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

    #endregion

    #region Sections

    private enum PartFilter { Sequenceable, All }
    private enum HighlightTarget { Sequence, Stage }

    private static void DrawSequencesSection(Vehicle vehicle, object instance)
    {
        ReadOnlySpan<Sequence> sequences = vehicle.Parts.SequenceList.Sequences;
        ImGuiTreeNodeFlags treeFlags = ImGuiTreeNodeFlags.DefaultOpen
            | ImGuiTreeNodeFlags.FramePadding
            | ImGuiTreeNodeFlags.DrawLinesToNodes;

        for (int i = 0; i < sequences.Length; i++)
        {
            Sequence sequence = sequences[i];
            if (sequence.Parts.IsEmpty)
                continue;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            bool activated = sequence.Activated;
            if (!activated)
                PushDimmedTextColor(extraDim: false);

            string header = $"Sequence {sequence.Number}";
            bool expanded = ImGui.TreeNodeEx(header, treeFlags);
            sequence.Highlight = ImGui.IsItemHovered();

            if (AnalysisCache.TryGetSequenceInfo(sequence.Number, out var info) && info.EngineCount > 0)
                DrawFuelProgressBar(info.FuelFraction);

            if (expanded)
            {
                DrawSequenceInfoLine(sequence.Number);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawPartsSubtree(sequence.Parts, instance, treeFlags,
                    PartFilter.Sequenceable, HighlightTarget.Sequence);
                ImGui.TreePop();
            }

            if (!activated)
                ImGui.PopStyleColor();
        }
    }

    private static void DrawStagesSection(Vehicle vehicle, object instance)
    {
        // StageList.ResetCaches sorts ascending by Number.
        ReadOnlySpan<Stage> stages = vehicle.Parts.StageList.Stages;
        ImGuiTreeNodeFlags treeFlags = ImGuiTreeNodeFlags.FramePadding
            | ImGuiTreeNodeFlags.DrawLinesToNodes;

        for (int i = 0; i < stages.Length; i++)
        {
            Stage stage = stages[i];
            if (stage.Parts.IsEmpty)
                continue;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            // Stages are always dim; they are informational only.
            PushDimmedTextColor(extraDim: false);

            string header = $"Stage {stage.Number}";
            bool expanded = ImGui.TreeNodeEx(header, treeFlags);
            stage.Highlight = ImGui.IsItemHovered();

            DrawStageHeaderBars(stage.Number);

            if (expanded)
            {
                DrawStageInfoLine(stage.Number);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawPartsSubtree(stage.Parts, instance, treeFlags,
                    PartFilter.All, HighlightTarget.Stage);
                ImGui.TreePop();
            }

            ImGui.PopStyleColor();
        }
    }

    // RCS bar shows only substances no active main engine can consume;
    // shared tanks are already on the fuel bar.
    private static void DrawStageHeaderBars(int stageNumber)
    {
        bool hasFuel = AnalysisCache.TryGetStageFuelInfo(stageNumber, out var fuelInfo)
            && fuelInfo.MaxFuelMass > 0f;
        bool hasRcs = AnalysisCache.TryGetStageRcsInfo(stageNumber, out var rcsInfo)
            && rcsInfo.MaxMass > 0f;

        if (!hasFuel && !hasRcs)
            return;

        // SameLine() committed up front because GetContentRegionAvail() needs
        // the post-tree-node cursor to report the row's remaining width. If
        // the squeeze drops both bars below threshold we return without a
        // widget; an unbound SameLine is harmless.
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
                // Squeezed window: drop RCS, keep fuel bar full width.
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
            return;

        float lineHeight = ImGui.GetTextLineHeight();
        float barHeight = lineHeight * 0.6f;
        float yOffset = (lineHeight - barHeight) * 0.5f;
        float2 cursorStart = ImGui.GetCursorPos();

        if (renderFuel)
        {
            ImGui.SetCursorPos(new float2(cursorStart.X, cursorStart.Y + yOffset));
            ImGui.ProgressBar(fuelInfo.FuelFraction,
                new float2?(new float2(fuelBarWidth, barHeight)), ""u8);
            ImGui.SameLine();
            ImGui.SetCursorPosY(cursorStart.Y);
            ImGui.Text(string.Format(Inv, "{0}% fuel",
                (int)MathF.Round(fuelInfo.FuelFraction * 100f)));
        }

        if (renderRcs)
        {
            if (renderFuel) ImGui.SameLine();
            float2 cur = ImGui.GetCursorPos();
            ImGui.SetCursorPos(new float2(cur.X, cursorStart.Y + yOffset));
            ImColor8 barColor = rcsInfo.FuelFraction < RcsBarLowFraction
                ? ColorRcsLow : ColorRcsBar;
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, barColor);
            ImGui.ProgressBar(rcsInfo.FuelFraction,
                new float2?(new float2(rcsBarWidth, barHeight)), ""u8);
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.SetCursorPosY(cursorStart.Y);
            ImGui.Text(string.Format(Inv, "RCS {0}%",
                (int)MathF.Round(rcsInfo.FuelFraction * 100f)));
        }
    }

    private static void DrawPartsSubtree(ReadOnlySpan<Part> parts, object instance,
        ImGuiTreeNodeFlags treeFlags, PartFilter filter, HighlightTarget highlight)
    {
        for (int j = 0; j < parts.Length; j++)
        {
            Part part = parts[j];
            if (filter == PartFilter.Sequenceable
                && !part.HasAny<ThrusterController>()
                && !part.HasAny<Decoupler>()
                && !part.HasAny<EngineController>())
                continue;

            bool partExpanded = ImGui.TreeNodeEx(part.DisplayName, treeFlags);
            if (ImGui.IsItemHovered())
            {
                if (highlight == HighlightTarget.Sequence) part.HighlightedForSequence = true;
                else part.HighlightedForStage = true;
            }

            if (partExpanded)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                foreach (MethodInvoker? invoker in _drawComponentInvokers)
                    invoker?.Invoke(instance, part);
                ImGui.TreePop();
            }
        }
    }

    #endregion

    #region Info Lines

    private static void DrawSequenceInfoLine(int sequenceNumber)
    {
        if (!AnalysisCache.TryGetSequenceInfo(sequenceNumber, out var info)) return;
        if (info.EngineCount == 0) return;

        BurnSequenceAllocation? primaryAlloc =
            AnalysisCache.TryGetBurnAllocation(sequenceNumber, out var pa) ? pa : null;

        bool hasSecondary = AnalysisCache.TryGetSecondarySequenceInfo(sequenceNumber, out var secondaryInfo)
            && secondaryInfo.EngineCount > 0;

        bool primaryDimmed = hasSecondary && !AnalysisCache.IsPrimaryCurrentCondition;
        DrawSingleSequenceInfoLine(info, AnalysisCache.PrimaryLabel, primaryAlloc, primaryDimmed);

        if (hasSecondary)
        {
            BurnSequenceAllocation? secondaryAlloc =
                AnalysisCache.TryGetSecondaryBurnAllocation(sequenceNumber, out var sa) ? sa : null;
            bool secondaryDimmed = AnalysisCache.IsPrimaryCurrentCondition;
            DrawSingleSequenceInfoLine(secondaryInfo, AnalysisCache.SecondaryLabel ?? "",
                secondaryAlloc, secondaryDimmed);
        }
    }

    private static void DrawSingleSequenceInfoLine(SequenceBurnInfo info, string label,
        BurnSequenceAllocation? alloc, bool isDimmed)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Indent();

        PushDimmedTextColor(extraDim: isDimmed);

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

        ImGui.PopStyleColor();
        ImGui.Unindent();
    }

    private static void DrawStageInfoLine(int stageNumber)
    {
        if (!AnalysisCache.TryGetStageFuelInfo(stageNumber, out var v)) return;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Indent();

        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float availWidth = ImGui.GetContentRegionAvail().X;
        float lineX = 0f;

        DrawInfoSegment(string.Format(Inv, "Mass: {0:N0} kg", v.DryMass + v.CurrentFuelMass),
            ref lineX, availWidth, spacing);

        if (v.MaxFuelMass > 0f)
            DrawInfoSegment(string.Format(Inv, "Fuel: {0:N0}/{1:N0} kg", v.CurrentFuelMass, v.MaxFuelMass),
                ref lineX, availWidth, spacing);

        if (AnalysisCache.TryGetStageRcsInfo(stageNumber, out var rcs)
            && rcs.Substances != null && rcs.Substances.Count > 0)
        {
            DrawInfoSegment("RCS:", ref lineX, availWidth, spacing);
            for (int i = 0; i < rcs.Substances.Count; i++)
            {
                if (i > 0)
                    DrawInfoSegment("|", ref lineX, availWidth, spacing);
                RcsSubstanceInfo s = rcs.Substances[i];
                string segText = string.Format(Inv, "{0} {1:N0}/{2:N0} kg",
                    s.ShortName, s.CurrentMass, s.MaxMass);
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

        ImGui.Unindent();
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

    #endregion

    #region Footer

    private static void DrawTotalFooter()
    {
        var analysis = AnalysisCache.Sequences;
        if (analysis == null || analysis.Value.Sequences.Count == 0) return;

        ImGui.Separator();

        bool hasSecondary = AnalysisCache.SecondarySequences != null;
        bool primaryDimmed = hasSecondary && !AnalysisCache.IsPrimaryCurrentCondition;

        DrawTotalLine(analysis.Value, AnalysisCache.BurnAnalysis,
            AnalysisCache.PrimaryLabel, primaryDimmed);

        if (hasSecondary)
        {
            bool secondaryDimmed = AnalysisCache.IsPrimaryCurrentCondition;
            DrawTotalLine(AnalysisCache.SecondarySequences!.Value,
                AnalysisCache.SecondaryBurnAnalysis,
                AnalysisCache.SecondaryLabel ?? "", secondaryDimmed);
        }

        var rcs = AnalysisCache.Rcs;
        if (rcs is { HasRcs: true, DeltaV: > 0f })
            DrawRcsFooterLine(rcs.Value);
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

    #endregion

    #region Helpers

    private static string WithLabel(string? label, string body)
        => string.IsNullOrEmpty(label) ? body : label + " " + body;

    #endregion
}
