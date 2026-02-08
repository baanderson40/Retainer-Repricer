namespace RetainerRepricer.Ui;

internal static class NodePaths
{
    // =========================================================
    // ItemSearchResult (Market board) addon
    // =========================================================
    // NodeId for the main listing list component inside ItemSearchResult.
    public const int ItemSearchResult_ListNodeId = 26;

    // ItemSearchResult row renderer nodes (AtkComponentListItemRenderer)
    public const ushort HqIconNodeId = 3;
    public const ushort UnitPriceNodeId = 5;
    public const ushort QuantityNodeId = 6;
    public const ushort TotalPriceNodeId = 8;
    public const ushort SellerNodeId = 10;

    // =========================================================
    // RetainerList addon
    // =========================================================
    // NodeId for the list component inside RetainerList.
    public const ushort RetainerListNodeId = 27;

    // RetainerList row renderer nodes (AtkComponentListItemRenderer)
    public const ushort RetainerNameNodeId = 3;

    // =========================================================
    // RetainerSellList addon
    // =========================================================
    // "N/20" style count text
    public const int RetainerSellList_CountNodeId = 19;

    // =========================================================
    // RetainerSell addon
    // =========================================================
    // Item name text (contains HQ glyph when HQ)
    public const int RetainerSell_ItemNameNodeId = 7;

    // Asking price is a NumericInput component node with a child text node.
    public const int RetainerSell_AskingPriceNumericNodeId = 10; // NumericInput component node
    public const ushort RetainerSell_AskingPriceTextNodeId = 5;  // child text node inside component
}
