using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using MethodInvoker = System.Reflection.MethodInvoker;
using Brutal.ImGuiApi;
using Brutal.Logging;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using StageInfo.Analysis;
using StageInfo.Core;

namespace StageInfo.UI;

/// <summary>
/// Replaces StagingWindow.DrawContent. Sequences first (with dV/TWR/burn/Isp),
/// separator, then Stages (fuel pool + mass + counts). Data from AnalysisCache.
/// </summary>
internal static class StageInfoPanel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly ImColor8 ColorInsufficient = new ImColor8(255, 60, 60, 255);

    private static MethodInvoker? _drawThruster;
    private static MethodInvoker? _drawEngine;
    private static MethodInvoker? _drawDecoupler;

    private static readonly string[] ModeLabels = { "Auto", "VAC", "ASL", "VAC + ASL", "Planning" };

    /// <summary>
    /// Applies the StagingWindow.DrawContent prefix and prepares the
    /// reflection invokers. Caller should have already passed
    /// GameReflection.ValidatePanelTargets().
    /// </summary>
    public static void ApplyPatches(Harmony harmony)
    {
        MethodInfo openComponent = GameReflection.StagingWindow_DrawComponentOpen!;
        _drawThruster = MethodInvoker.Create(openComponent.MakeGenericMethod(typeof(ThrusterController)));
        _drawEngine   = MethodInvoker.Create(openComponent.MakeGenericMethod(typeof(EngineController)));
        _drawDecoupler = MethodInvoker.Create(openComponent.MakeGenericMethod(typeof(Decoupler)));

        harmony.Patch(GameReflection.StagingWindow_DrawContent!,
            prefix: new HarmonyMethod(typeof(StageInfoPanel), nameof(DrawContentPrefix)));

        if (DebugConfig.StageInfo)
            DefaultCategory.Log.Debug("[StageInfo] Panel patch applied.");
    }

    public static void Reset()
    {
        // MethodInvokers are reassigned on next ApplyPatches; null them so a
        // partial reload can't reuse a stale binding.
        _drawThruster = null;
        _drawEngine = null;
        _drawDecoupler = null;
    }

    #region DrawContent

    /// <summary>Prefix replacement; returns false so stock doesn't also run.</summary>
    static bool DrawContentPrefix(object __instance, Viewport viewport)
    {
#if DEBUG
        long perfStart = DebugConfig.Performance ? Stopwatch.GetTimestamp() : 0;
#endif
        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle == null)
            return false;


        AnalysisCache.MarkPanelActive();

        ClearPartHighlights(vehicle);
        DrawModeSelector();

        bool hasSecondary = AnalysisCache.SecondarySequences != null;
        float footerLines = hasSecondary ? 2f : 1f;
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
            return false;

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

    private static void InvokeDrawComponent(MethodInvoker? invoker, object instance, Part part)
    {
        invoker?.Invoke(instance, part);
    }

    #endregion

    #region Mode Selector

    private static void DrawModeSelector()
    {
        ImGui.PushItemWidth(110f);

        string currentLabel = ModeLabels[(int)StageInfoSettings.Mode];

        if (ImGui.BeginCombo("##StageInfoMode"u8, currentLabel))
        {
            for (int i = 0; i < ModeLabels.Length; i++)
            {
                bool isSelected = (int)StageInfoSettings.Mode == i;
                if (ImGui.Selectable(ModeLabels[i], isSelected))
                    StageInfoSettings.Mode = (StageDisplayMode)i;
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

            if (AnalysisCache.TryGetStageFuelInfo(stage.Number, out var info) && info.MaxFuelMass > 0f)
                DrawFuelProgressBar(info.FuelFraction);

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
                InvokeDrawComponent(_drawThruster, instance, part);
                InvokeDrawComponent(_drawEngine, instance, part);
                InvokeDrawComponent(_drawDecoupler, instance, part);
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
            DrawInfoSegment(WithLabel(label, body), ref lineX, availWidth, spacing, isFirst: true);
        }

        DrawInfoSegment(string.Format(Inv, "TWR: {0:F2}", info.Twr),
            ref lineX, availWidth, spacing, isFirst: false);

        float displayBurnTime = alloc?.AllocatedBurnTime ?? info.BurnTime;
        DrawInfoSegment(string.Format(Inv, "Burn: {0}", FormatBurnTime(displayBurnTime)),
            ref lineX, availWidth, spacing, isFirst: false);

        DrawInfoSegment(string.Format(Inv, "ISP: {0:F0}s", info.Isp),
            ref lineX, availWidth, spacing, isFirst: false);

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
            ref lineX, availWidth, spacing, isFirst: true);

        if (v.MaxFuelMass > 0f)
            DrawInfoSegment(string.Format(Inv, "Fuel: {0:N0}/{1:N0} kg", v.CurrentFuelMass, v.MaxFuelMass),
                ref lineX, availWidth, spacing, isFirst: false);

        if (v.EngineCount > 0)
            DrawInfoSegment(string.Format(Inv, "Engines: {0}", v.EngineCount),
                ref lineX, availWidth, spacing, isFirst: false);

        if (v.DecouplerCount > 0)
            DrawInfoSegment(string.Format(Inv, "Decouplers: {0}", v.DecouplerCount),
                ref lineX, availWidth, spacing, isFirst: false);

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
    }

    private static void DrawTotalLine(VehicleBurnAnalysis sequences, BurnAnalysis? burnAnalysis,
        string label, bool isDimmed)
    {
        if (isDimmed)
            PushDimmedTextColor(extraDim: true);

        string prefix = string.IsNullOrEmpty(label) ? "" : label + " ";

        if (burnAnalysis != null)
        {
            var burn = burnAnalysis.Value;
            ImGui.Text(string.Format(Inv,
                "{0}Total Delta V: {1:N0} m/s", prefix, sequences.TotalDeltaV));
            ImGui.SameLine();
            ImGui.Text("|");
            ImGui.SameLine();

            if (burn.IsSufficient)
            {
                ImGui.Text(string.Format(Inv,
                    "Burn: {0:N0} m/s  Burn Time: {1}",
                    burn.RequiredDv, FormatBurnTime(burn.TotalBurnTime)));
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ColorInsufficient);
                ImGui.Text(string.Format(Inv, "Burn: {0:N0} m/s  INSUFFICIENT", burn.RequiredDv));
                ImGui.PopStyleColor();
            }
        }
        else
        {
            ImGui.Text(string.Format(Inv,
                "{0}Total Delta V: {1:N0} m/s  Burn Time: {2}",
                prefix, sequences.TotalDeltaV, FormatBurnTime(sequences.TotalBurnTime)));
        }

        if (isDimmed)
            ImGui.PopStyleColor();
    }

    #endregion

    #region Shared helpers

    private static void DrawFuelProgressBar(float fuelFraction)
    {
        ImGui.SameLine();
        float availWidth = ImGui.GetContentRegionAvail().X;
        float pctTextWidth = ImGui.CalcTextSize("100% fuel"u8).X + 8f;
        float barWidth = availWidth - pctTextWidth;
        if (barWidth < 30f) return;

        float lineHeight = ImGui.GetTextLineHeight();
        float barHeight = lineHeight * 0.6f;
        float yOffset = (lineHeight - barHeight) * 0.5f;

        float2 cursor = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new float2(cursor.X, cursor.Y + yOffset));
        ImGui.ProgressBar(fuelFraction, new float2?(new float2(barWidth, barHeight)), ""u8);
        ImGui.SameLine();
        ImGui.SetCursorPosY(cursor.Y);
        ImGui.Text(string.Format(Inv, "{0}% fuel", (int)MathF.Round(fuelFraction * 100f)));
    }

    /// <summary>
    /// Pushes TextDisabled as text color; if extraDim, halves the alpha
    /// further. Caller pops with ImGui.PopStyleColor().
    /// </summary>
    private static void PushDimmedTextColor(bool extraDim)
    {
        var color = ImGui.GetStyleColorVec4(ImGuiCol.TextDisabled);
        if (extraDim) color.W *= 0.6f;
        ImGui.PushStyleColor(ImGuiCol.Text, color);
    }

    private static string WithLabel(string? label, string body)
        => string.IsNullOrEmpty(label) ? body : label + " " + body;

    private static void DrawInfoSegment(string text, ref float lineX,
        float availWidth, float spacing, bool isFirst)
    {
        DrawInfoSegmentColored(text, null, ref lineX, availWidth, spacing, isFirst);
    }

    private static void DrawInfoSegmentColored(string text, ImColor8? color,
        ref float lineX, float availWidth, float spacing, bool isFirst = false)
    {
        float2 textSize = ImGui.CalcTextSize(text);

        if (lineX > 0f && lineX + textSize.X <= availWidth)
            ImGui.SameLine(0f, spacing * 2f);
        else if (lineX > 0f)
            lineX = 0f;

        if (color != null)
            ImGui.PushStyleColor(ImGuiCol.Text, color.Value);
        ImGui.Text(text);
        if (color != null)
            ImGui.PopStyleColor();

        lineX += textSize.X + spacing * 2f;
    }

    private static string FormatBurnTime(float seconds)
    {
        if (seconds < 60f)
            return $"{seconds:F0}s";
        if (seconds < 3600f)
        {
            int m = (int)(seconds / 60f);
            int s = (int)(seconds % 60f);
            return s > 0 ? $"{m}m {s}s" : $"{m}m";
        }
        int h = (int)(seconds / 3600f);
        int min = (int)((seconds % 3600f) / 60f);
        return min > 0 ? $"{h}h {min}m" : $"{h}h";
    }

    #endregion
}
