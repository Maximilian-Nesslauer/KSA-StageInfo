using System;
using System.Collections.Generic;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using StageInfo.Analysis;
using StageInfo.Core;
using static StageInfo.UI.StageInfoUiHelpers;

namespace StageInfo.UI;

internal static class EditorStageInfoPanel
{
    private static readonly ImColor8 ColorRcsBar = new ImColor8(70, 150, 230, 255);
    private static readonly ImColor8 ColorRcsLow = new ImColor8(230, 90, 60, 255);
    private const float RcsBarLowFraction = 0.2f;

    private static bool _showPanel = true;
    private static string? _cachedVehicleId;
    private static string _cachedWindowTitle = "";

    public static void ApplyPatches(Harmony harmony)
    {
        harmony.Patch(GameReflection.VehicleEditingSpace_DrawStageWindow!,
            postfix: new HarmonyMethod(typeof(EditorStageInfoPanel),
                nameof(DrawStageWindowPostfix)));

        harmony.Patch(GameReflection.Program_DrawProgramMenusHook!,
            postfix: new HarmonyMethod(typeof(EditorStageInfoPanel),
                nameof(DrawMenusHookPostfix)));

        if (DebugConfig.StageInfo)
            Brutal.Logging.DefaultCategory.Log.Debug(
                "[StageInfo] Editor panel patch applied.");
    }

    public static void Reset()
    {
        _showPanel = true;
        _cachedVehicleId = null;
        _cachedWindowTitle = "";
    }

    static void DrawMenusHookPostfix()
    {
        if (Program.Editor == null)
            return;

        if (ImGui.BeginMenu("StageInfo"u8))
        {
            if (ImGui.MenuItem("Show Panel"u8, default(ImString), _showPanel))
                _showPanel = !_showPanel;
            ImGui.EndMenu();
        }
    }

    static void DrawStageWindowPostfix(VehicleEditingSpace __instance, Viewport inViewPort)
    {
        if (!_showPanel)
            return;

        PartTree? parts = __instance.Parts;
        if (parts == null || parts.Count == 0)
            return;

        DrawPanel(__instance.Id, parts, inViewPort);
    }

    private static void DrawPanel(string vehicleId, PartTree parts, Viewport viewport)
    {
        ImGui.SetNextWindowPos(viewport.Position + new float2(420f, 450f),
            ImGuiCond.Appearing, (float2?)null);
        ImGui.SetNextWindowSize(new float2(600f, 500f), ImGuiCond.Appearing);

        if (vehicleId != _cachedVehicleId)
        {
            _cachedVehicleId = vehicleId;
            _cachedWindowTitle = vehicleId + " - StageInfo###" + vehicleId + "StageInfo";
        }

        if (!ImGui.Begin(_cachedWindowTitle,
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.End();
            return;
        }

        EditorAnalysisCache.Update(parts);
        var analysis = EditorAnalysisCache.Sequences;
        if (analysis == null)
        {
            ImGui.End();
            return;
        }

        DrawBodySelector();
        ImGui.Separator();

        DrawSequencesSection(parts);
        ImGui.Separator();
        DrawStagesSection();
        ImGui.Separator();
        DrawTotalFooter(analysis.Value);

        ImGui.End();
    }

    #region Body Selector

    private static void DrawBodySelector()
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

        bool bodyHasAtmosphere = EditorStageInfoSettings.SelectedBodyHasAtmosphere();
        if (bodyHasAtmosphere)
        {
            float checkboxWidth = ImGui.GetFrameHeight()
                + ImGui.GetStyle().ItemInnerSpacing.X
                + ImGui.CalcTextSize("Vacuum"u8).X;
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - checkboxWidth
                + ImGui.GetCursorPosX());
            bool useVacuum = EditorStageInfoSettings.UseVacuum;
            if (ImGui.Checkbox("Vacuum"u8, ref useVacuum))
                EditorStageInfoSettings.UseVacuum = useVacuum;
        }
    }

    #endregion

    #region Sequences

    private static void DrawSequencesSection(PartTree parts)
    {
        ReadOnlySpan<Sequence> sequences = parts.SequenceList.Sequences;
        for (int i = 0; i < sequences.Length; i++)
        {
            Sequence seq = sequences[i];
            if (seq.Parts.IsEmpty)
                continue;

            bool hasAnalysis = EditorAnalysisCache.TryGetSequenceInfo(seq.Number, out var info);
            bool hasEngines = hasAnalysis && info.EngineCount > 0;

            if (!hasEngines)
                PushDimmedTextColor();

            bool open = ImGui.TreeNodeEx(
                string.Format(Inv, "Sequence {0}", seq.Number),
                hasEngines ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);

            if (hasEngines)
                DrawFuelProgressBar(info.FuelFraction);

            if (open)
            {
                if (hasEngines)
                    DrawSequenceInfoLine(info);
                else
                    ImGui.TextDisabled("No engines");
                ImGui.TreePop();
            }

            if (!hasEngines)
                ImGui.PopStyleColor();
        }
    }

    private static void DrawSequenceInfoLine(SequenceBurnInfo info)
    {
        ImGui.Indent();

        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float availWidth = ImGui.GetContentRegionAvail().X;
        float lineX = 0f;

        DrawInfoSegment(string.Format(Inv, "Delta V: {0:N0} m/s", info.DeltaV),
            ref lineX, availWidth, spacing);
        DrawInfoSegment(string.Format(Inv, "TWR: {0:F2}", info.Twr),
            ref lineX, availWidth, spacing);
        DrawInfoSegment(string.Format(Inv, "Burn: {0}", FormatBurnTime(info.BurnTime)),
            ref lineX, availWidth, spacing);
        DrawInfoSegment(string.Format(Inv, "ISP: {0:F0}s", info.Isp),
            ref lineX, availWidth, spacing);

        ImGui.Unindent();
    }

    #endregion

    #region Stages

    private static void DrawStagesSection()
    {
        var stages = EditorAnalysisCache.Stages;
        if (stages == null)
            return;

        for (int i = 0; i < stages.Value.Stages.Count; i++)
        {
            StageFuelInfo info = stages.Value.Stages[i];

            PushDimmedTextColor();
            bool open = ImGui.TreeNodeEx(
                string.Format(Inv, "Stage {0}", info.StageNumber),
                ImGuiTreeNodeFlags.DefaultOpen);

            DrawStageHeaderBars(info.StageNumber);

            if (open)
            {
                DrawStageInfoLine(info);
                ImGui.TreePop();
            }
            ImGui.PopStyleColor();
        }
    }

    private static void DrawStageHeaderBars(int stageNumber)
    {
        bool hasFuel = EditorAnalysisCache.TryGetStageFuelInfo(stageNumber, out var fuelInfo)
            && fuelInfo.MaxFuelMass > 0f;
        bool hasRcs = EditorAnalysisCache.TryGetStageRcsInfo(stageNumber, out var rcsInfo)
            && rcsInfo.MaxMass > 0f;

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

    private static void DrawStageInfoLine(StageFuelInfo v)
    {
        ImGui.Indent();

        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float availWidth = ImGui.GetContentRegionAvail().X;
        float lineX = 0f;

        DrawInfoSegment(string.Format(Inv, "Mass: {0:N0} kg", v.DryMass + v.CurrentFuelMass),
            ref lineX, availWidth, spacing);

        if (v.MaxFuelMass > 0f)
            DrawInfoSegment(string.Format(Inv, "Fuel: {0:N0}/{1:N0} kg", v.CurrentFuelMass, v.MaxFuelMass),
                ref lineX, availWidth, spacing);

        if (EditorAnalysisCache.TryGetStageRcsInfo(v.StageNumber, out var rcs)
            && rcs.Substances != null && rcs.Substances.Count > 0)
        {
            DrawInfoSegment("RCS:", ref lineX, availWidth, spacing);
            for (int i = 0; i < rcs.Substances.Count; i++)
            {
                if (i > 0)
                    DrawInfoSegment("|", ref lineX, availWidth, spacing);
                RcsSubstanceInfo s = rcs.Substances[i];
                DrawInfoSegment(string.Format(Inv, "{0} {1:N0}/{2:N0} kg",
                    s.ShortName, s.CurrentMass, s.MaxMass),
                    ref lineX, availWidth, spacing);
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

    #endregion

    #region Footer

    private static void DrawTotalFooter(VehicleBurnAnalysis analysis)
    {
        if (analysis.Sequences.Count == 0)
            return;

        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float availWidth = ImGui.GetContentRegionAvail().X;
        float lineX = 0f;

        DrawInfoSegment(string.Format(Inv,
            "{0} Total Delta V: {1:N0} m/s",
            EditorAnalysisCache.PrimaryLabel, analysis.TotalDeltaV),
            ref lineX, availWidth, spacing);
        DrawInfoSegment(string.Format(Inv,
            "Burn Time: {0}", FormatBurnTime(analysis.TotalBurnTime)),
            ref lineX, availWidth, spacing);

        var rcs = EditorAnalysisCache.Rcs;
        if (rcs is { HasRcs: true, DeltaV: > 0f })
        {
            ImGui.Text(string.Format(Inv, "RCS dV ~{0:N0} m/s", rcs.Value.DeltaV));
            DrawRcsEngineeringTooltip(rcs.Value);
        }
    }

    private static void DrawRcsEngineeringTooltip(VehicleRcsAnalysis rcs)
    {
        if (!ImGui.IsItemHovered())
            return;

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

    #endregion

}
