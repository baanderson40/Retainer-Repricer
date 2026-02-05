using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace RetainerRepricer.Ui;

internal sealed unsafe class UiReader
{
    private readonly IGameGui _gui;

    public UiReader(IGameGui gui) => _gui = gui;

    // =========================================================
    // Market board: ItemSearchResult list access
    // =========================================================
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
            if (n->NodeId != 26) continue; // your known node id for AtkComponentList

            var compNode = (AtkComponentNode*)n;
            var comp = compNode->Component;
            if (comp == null) return null;

            return (AtkComponentList*)comp;
        }

        return null;
    }

    // =========================================================
    // Generic renderer node readers
    // =========================================================
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

    // =========================================================
    // HQ detection (market list renderer)
    // =========================================================
    public bool RowIsHq(AtkComponentListItemRenderer* renderer)
    {
        var n = GetRendererNodeById(renderer, NodePaths.HqIconNodeId);
        if (n == null) return false;

        // HQ -> DrawFlags = 0x0
        // NQ -> DrawFlags = 0x100 (observed)
        return n->DrawFlags == 0;
    }

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

    // =========================================================
    // Debug: dump addon NodeList
    // =========================================================
    public void DumpAddonNodeList(string addonName, Action<string> log)
    {
        var addon = _gui.GetAddonByName(addonName, 1);
        if (addon.IsNull)
        {
            log("[NodeList] addon not open");
            return;
        }

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null)
        {
            log("[NodeList] unit null");
            return;
        }

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

    // =========================================================
    // RetainerList: AtkComponentList access
    // =========================================================
    public AtkComponentList* GetRetainerList()
    {
        var addon = _gui.GetAddonByName("RetainerList", 1);
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
            if (n->NodeId != NodePaths.RetainerListNodeId) continue;

            var compNode = (AtkComponentNode*)n;
            var comp = compNode->Component;
            if (comp == null) return null;

            return (AtkComponentList*)comp;
        }

        return null;
    }

    // =========================================================
    // Generic: read a text node by NodeId from a named addon
    // =========================================================
    public string? ReadAddonTextNode(string addonName, int nodeId)
    {
        var addon = _gui.GetAddonByName(addonName, 1);
        if (addon.IsNull) return null;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null) return null;

        var uld = unit->UldManager;
        var count = uld.NodeListCount;
        if (count <= 0 || uld.NodeList == null) return null;

        for (int i = 0; i < count; i++)
        {
            var node = uld.NodeList[i];
            if (node == null) continue;

            if (node->NodeId != nodeId) continue;

            if (node->Type != NodeType.Text) return null;

            var textNode = (AtkTextNode*)node;
            return textNode->NodeText.ToString();
        }

        return null;
    }
}
