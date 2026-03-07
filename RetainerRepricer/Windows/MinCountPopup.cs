using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;

namespace RetainerRepricer.Windows;

/// <summary>
/// Simple pop-up that lets the user enter a per-item minimum count directly from the context menu.
/// </summary>
internal sealed class MinCountPopup : Window, IDisposable
{
    private const string HqIcon = "\uE03C";

    private uint _pendingItemId;
    private bool _pendingIsHq;
    private string _pendingItemName = string.Empty;
    private Action<uint, bool, int>? _pendingCallback;
    private string _inputText = "1";
    private bool _initialPositionSet;

    public MinCountPopup()
        : base(
            "Set Minimum Count##RR_MinCount",
            ImGuiWindowFlags.AlwaysAutoResize
        )
    {
        Size = new(250, 140);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(200, 130),
            MaximumSize = new(360, 200),
        };
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse;
        IsOpen = false;
    }

    public void Show(uint itemId, bool isHq, string itemName, Action<uint, bool, int> onConfirm)
    {
        _pendingItemId = itemId;
        _pendingIsHq = isHq;
        _pendingItemName = itemName;
        _pendingCallback = onConfirm;
        _inputText = "1";
        _initialPositionSet = false;

        IsOpen = true;
    }

    public override void Draw()
    {
        EnsureInitialPosition();

        const float labelWidth = 80f;
        const float minValueWidth = 140f;
        var spacingY = ImGui.GetStyle().ItemSpacing.Y;

        var itemLabel = BuildItemLabel();
        DrawCenteredLabelValueRow("Item", itemLabel, labelWidth, minValueWidth);
        ImGui.Dummy(new Vector2(0, spacingY));

        DrawCenteredInputRow(labelWidth, minValueWidth);

        ImGui.Dummy(new Vector2(0, spacingY));

        DrawCenteredButtons();
    }

    private void DrawCenteredButtons()
    {
        const float buttonWidth = 100f;
        const float buttonSpacing = 12f;

        var region = ImGui.GetContentRegionAvail().X;
        var totalButtonWidth = (buttonWidth * 2) + buttonSpacing;
        var indent = MathF.Max(0f, (region - totalButtonWidth) * 0.5f);

        ImGui.Indent(indent);

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(buttonSpacing, ImGui.GetStyle().ItemSpacing.Y));
        if (ImGui.Button("Cancel", new Vector2(buttonWidth, 0)))
        {
            IsOpen = false;
        }

        ImGui.SameLine();

        if (ImGui.Button("Add to Sell", new Vector2(buttonWidth, 0)))
        {
            if (!int.TryParse(_inputText, out var minCount))
            {
                minCount = 1;
            }

            minCount = Math.Clamp(minCount, 1, 999);
            _pendingCallback?.Invoke(_pendingItemId, _pendingIsHq, minCount);
            IsOpen = false;
        }

        ImGui.PopStyleVar();

        ImGui.Unindent(indent);
    }

    public void Dispose()
    {
    }

    private void DrawCenteredLabelValueRow(string label, string value, float labelWidth, float minValueWidth)
    {
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var region = ImGui.GetContentRegionAvail().X;
        var valueWidth = MathF.Max(minValueWidth, region - labelWidth - spacingX);
        var rowWidth = labelWidth + spacingX + valueWidth;
        var rowStartOffset = MathF.Max(0f, (region - rowWidth) * 0.5f);

        var cursor = ImGui.GetCursorPos();
        var rowStartX = cursor.X + rowStartOffset;
        var valueStartX = rowStartX + labelWidth + spacingX;

        ImGui.SetCursorPos(new Vector2(rowStartX, cursor.Y));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);

        ImGui.SetCursorPos(new Vector2(valueStartX, cursor.Y));
        ImGui.PushTextWrapPos(valueStartX + valueWidth);
        ImGui.TextWrapped(value);
        ImGui.PopTextWrapPos();

        var nextY = MathF.Max(ImGui.GetCursorPosY(), cursor.Y + ImGui.GetTextLineHeightWithSpacing());
        ImGui.SetCursorPos(new Vector2(cursor.X, nextY));
    }

    private void DrawCenteredInputRow(float labelWidth, float minValueWidth)
    {
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var region = ImGui.GetContentRegionAvail().X;
        var valueWidth = MathF.Max(minValueWidth, region - labelWidth - spacingX);
        var rowWidth = labelWidth + spacingX + valueWidth;
        var rowStartOffset = MathF.Max(0f, (region - rowWidth) * 0.5f);

        var cursor = ImGui.GetCursorPos();
        var rowStartX = cursor.X + rowStartOffset;
        var valueStartX = rowStartX + labelWidth + spacingX;

        ImGui.SetCursorPos(new Vector2(rowStartX, cursor.Y));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Min Count");

        ImGui.SetCursorPos(new Vector2(valueStartX, cursor.Y));
        ImGui.SetNextItemWidth(valueWidth);
        if (ImGui.InputTextWithHint("##mincount_input", "1", ref _inputText, 8, ImGuiInputTextFlags.CharsDecimal)
            && string.IsNullOrWhiteSpace(_inputText))
        {
            _inputText = "1";
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("Items will be listed only when you have at least this many in your inventory.");
        }

        var nextY = MathF.Max(ImGui.GetCursorPosY(), cursor.Y + ImGui.GetTextLineHeightWithSpacing());
        ImGui.SetCursorPos(new Vector2(cursor.X, nextY));
    }

    private string BuildItemLabel()
    {
        if (string.IsNullOrWhiteSpace(_pendingItemName))
            return _pendingIsHq ? HqIcon : string.Empty;

        return _pendingIsHq
            ? string.Concat(_pendingItemName, " ", HqIcon)
            : _pendingItemName;
    }

    private void EnsureInitialPosition()
    {
        if (_initialPositionSet)
            return;

        var center = ImGui.GetMainViewport().GetCenter();
        var size = Size ?? new Vector2(250, 140);
        Position = new Vector2(center.X - (size.X / 2), center.Y - (size.Y / 2));
        PositionCondition = ImGuiCond.Once;
        _initialPositionSet = true;
    }
}
