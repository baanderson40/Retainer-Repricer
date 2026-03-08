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
    private Action<uint, bool, int, int>? _pendingCallback;
    private Vector2? _pendingAnchorPosition;
    private string _inputText = "1";
    private string _priorityText = "1";
    private int _priorityMax = 1;
    private bool _initialPositionSet;

    public MinCountPopup()
        : base(
            "Set Minimum Count##RR_MinCount",
            ImGuiWindowFlags.AlwaysAutoResize
        )
    {
        Size = new(285, 160);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(200, 150),
            MaximumSize = new(400, 260),
        };
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse;
        IsOpen = false;
    }

    public void Show(
        uint itemId,
        bool isHq,
        string itemName,
        Action<uint, bool, int, int> onConfirm,
        Vector2? anchorPosition = null,
        int defaultPriority = 1)
    {
        _pendingItemId = itemId;
        _pendingIsHq = isHq;
        _pendingItemName = itemName;
        _pendingCallback = onConfirm;
        _pendingAnchorPosition = anchorPosition;
        _inputText = "1";
        _priorityMax = Math.Max(1, defaultPriority);
        _priorityText = _priorityMax.ToString();
        _initialPositionSet = false;

        IsOpen = true;
    }

    public override void Draw()
    {
        ApplyPendingPosition();

        const float labelWidth = 47f;
        const float minValueWidth = 100f;
        var spacingY = ImGui.GetStyle().ItemSpacing.Y;

        var itemLabel = BuildItemLabel();
        DrawCenteredLabelValueRow("Item", itemLabel, labelWidth, minValueWidth);
        ImGui.Dummy(new Vector2(0, spacingY));

        DrawCenteredInputRow(labelWidth, minValueWidth);
        ImGui.Dummy(new Vector2(0, spacingY));
        DrawPriorityInputRow(labelWidth, minValueWidth);

        ImGui.Dummy(new Vector2(0, spacingY));

        DrawCenteredButtons();
    }

    private void DrawCenteredButtons()
    {
        const float buttonWidth = 100f;
        const float buttonSpacing = 16f;

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

            if (!int.TryParse(_priorityText, out var priority))
                priority = _priorityMax;

            priority = Math.Clamp(priority, 1, _priorityMax);

            _pendingCallback?.Invoke(_pendingItemId, _pendingIsHq, minCount, priority);
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
        ImGui.TextUnformatted("Min Inv");

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

    private void DrawPriorityInputRow(float labelWidth, float minValueWidth)
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
        ImGui.TextUnformatted("Priority");

        ImGui.SetCursorPos(new Vector2(valueStartX, cursor.Y));
        ImGui.SetNextItemWidth(valueWidth);
        if (ImGui.InputTextWithHint("##priority_input", _priorityMax.ToString(), ref _priorityText, 8, ImGuiInputTextFlags.CharsDecimal)
            && string.IsNullOrWhiteSpace(_priorityText))
        {
            _priorityText = _priorityMax.ToString();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("Lower numbers run first. Enter 1 to add at the top, or leave the default to append.");
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

    private void ApplyPendingPosition()
    {
        var viewport = ImGui.GetMainViewport();
        var margin = new Vector2(4f, 4f);
        var fallbackSize = Size ?? new Vector2(250, 140);

        if (_pendingAnchorPosition.HasValue)
        {
            var windowSize = ImGui.GetWindowSize();
            if (windowSize.X <= 0 || windowSize.Y <= 0)
                windowSize = fallbackSize;

            var halfSize = windowSize / 2f;
            var cursorOffset = new Vector2(0f, 8f);
            var anchor = _pendingAnchorPosition.Value - halfSize + cursorOffset;
            var min = viewport.Pos + margin;
            var max = viewport.Pos + viewport.Size - windowSize - margin;
            var target = new Vector2(
                Math.Clamp(anchor.X, min.X, Math.Max(min.X, max.X)),
                Math.Clamp(anchor.Y, min.Y, Math.Max(min.Y, max.Y))
            );

            ImGui.SetWindowPos(target, ImGuiCond.Always);

            _pendingAnchorPosition = null;
            _initialPositionSet = true;
            return;
        }

        if (_initialPositionSet)
            return;

        var currentSize = ImGui.GetWindowSize();
        if (currentSize.X <= 0 || currentSize.Y <= 0)
            currentSize = fallbackSize;

        var center = viewport.GetCenter();
        var centeredPosition = new Vector2(center.X - (currentSize.X / 2), center.Y - (currentSize.Y / 2));
        ImGui.SetWindowPos(centeredPosition, ImGuiCond.Once);

        _initialPositionSet = true;
    }
}
