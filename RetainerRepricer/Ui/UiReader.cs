using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace RetainerRepricer.Ui;

internal sealed unsafe class UiReader
{
    private readonly IGameGui _gui;

    public UiReader(IGameGui gui) => _gui = gui;

    // ItemSearchResult -> NodeId 26 is the AtkComponentList (type 1013 in your build)
    public AtkComponentList* GetMarketList()
    {
        var addon = _gui.GetAddonByName("ItemSearchResult", 1);
        if (addon.IsNull) return null;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null) return null;

        var nl = unit->UldManager.NodeList;
        var count = unit->UldManager.NodeListCount;
        if (nl == null || count <= 0) return null;

        for (int i = 0; i < count; i++)
        {
            var n = nl[i];
            if (n == null) continue;
            if (n->NodeId != 26) continue;

            var compNode = (AtkComponentNode*)n;
            var comp = compNode->Component;
            if (comp == null) return null;

            return (AtkComponentList*)comp;
        }

        return null;
    }

    public string? ReadRendererText(AtkComponentListItemRenderer* renderer, ushort nodeId)
        => GetRendererTextByNodeId(renderer, nodeId);

    public string? GetRendererTextByNodeId(AtkComponentListItemRenderer* renderer, ushort nodeId)
    {
        if (renderer == null) return null;

        var uld = renderer->UldManager;
        var nodes = uld.NodeList;
        var count = uld.NodeListCount;
        if (nodes == null || count <= 0) return null;

        for (int i = 0; i < count; i++)
        {
            var n = nodes[i];
            if (n == null) continue;
            if (n->NodeId != nodeId) continue;
            if (n->Type != NodeType.Text) return null;

            return ((AtkTextNode*)n)->NodeText.ToString();
        }

        return null;
    }

    public AtkResNode* GetRendererNodeById(AtkComponentListItemRenderer* renderer, ushort nodeId)
    {
        if (renderer == null) return null;

        var uld = renderer->UldManager;
        var nodes = uld.NodeList;
        var count = uld.NodeListCount;
        if (nodes == null || count <= 0) return null;

        for (int i = 0; i < count; i++)
        {
            var n = nodes[i];
            if (n == null) continue;
            if (n->NodeId == nodeId) return n;
        }

        return null;
    }

    public bool RowIsHq(AtkComponentListItemRenderer* renderer)
    {
        var n = GetRendererNodeById(renderer, NodePaths.HqIconNodeId);
        if (n == null) return false;

        // HQ -> DrawFlags = 0x0
        // NQ -> DrawFlags = 0x100
        return n->DrawFlags == 0;
    }

    // Debug helper: confirm draw flags per row
    public void DumpHqIconState(AtkComponentListItemRenderer* renderer, int rowIndex, Action<string> log)
    {
        var n = GetRendererNodeById(renderer, NodePaths.HqIconNodeId);
        if (n == null)
        {
            log($"[HQ] row {rowIndex}: node {NodePaths.HqIconNodeId} not found");
            return;
        }

        log($"[HQ] row {rowIndex}: draw=0x{n->DrawFlags:X} a={n->Color.A}");
    }

    // Debug helper: flat NodeList dump of an addon
    public void DumpAddonNodeList(string addonName, Action<string> log)
    {
        var addon = _gui.GetAddonByName(addonName, 1);
        if (addon.IsNull) { log("[NodeList] addon not open"); return; }

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null) { log("[NodeList] unit null"); return; }

        var nl = unit->UldManager.NodeList;
        var count = unit->UldManager.NodeListCount;

        log($"[NodeList] {addonName} NodeListCount={count}");

        if (nl == null || count <= 0) return;

        for (int i = 0; i < count; i++)
        {
            var n = nl[i];
            if (n == null) continue;

            log($"[NodeList] i={i} id={n->NodeId} type={n->Type} x={n->X} y={n->Y}");
        }
    }
}
