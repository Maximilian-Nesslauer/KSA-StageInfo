using System;
using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace StageInfo.UI;

internal static class StageInfoUiHelpers
{
    internal static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static string FormatBurnTime(float seconds)
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

    internal static void DrawFuelProgressBar(float fuelFraction)
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

    internal static void PushDimmedTextColor(bool extraDim = false)
    {
        var color = ImGui.GetStyleColorVec4(ImGuiCol.TextDisabled);
        if (extraDim) color.W *= 0.6f;
        ImGui.PushStyleColor(ImGuiCol.Text, color);
    }

    internal static void DrawInfoSegment(string text, ref float lineX,
        float availWidth, float spacing)
    {
        DrawInfoSegmentColored(text, null, ref lineX, availWidth, spacing);
    }

    internal static void DrawInfoSegmentColored(string text, ImColor8? color,
        ref float lineX, float availWidth, float spacing)
    {
        float2 textSize = ImGui.CalcTextSize(text);
        bool needsWrap = textSize.X > availWidth;

        if (lineX > 0f && !needsWrap && lineX + textSize.X <= availWidth)
            ImGui.SameLine(0f, spacing * 2f);
        else if (lineX > 0f)
            lineX = 0f;

        if (color != null)
            ImGui.PushStyleColor(ImGuiCol.Text, color.Value);

        if (needsWrap)
        {
            ImGui.TextWrapped(text);
            lineX = availWidth + 1f;
        }
        else
        {
            ImGui.Text(text);
            lineX += textSize.X + spacing * 2f;
        }

        if (color != null)
            ImGui.PopStyleColor();
    }
}
