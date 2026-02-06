namespace RetainerRepricer.Ui;

internal static class NodePaths
{
    // =========================================================
    // ItemSearchResult row renderer nodes
    // =========================================================
    public const ushort SellerNodeId = 10;
    public const ushort UnitPriceNodeId = 5;
    public const ushort TotalPriceNodeId = 8;
    public const ushort QuantityNodeId = 6;
    public const ushort HqIconNodeId = 3;

    // =========================================================
    // RetainerList list + row renderer nodes
    // =========================================================
    public const ushort RetainerListNodeId = 27;
    public const ushort RetainerNameNodeId = 3;

    // =========================================================
    // RetainerSellList nodes
    // =========================================================
    public const int RetainerSellList_CountNodeId = 19; // "19/20" style

    // =========================================================
    // RetainerSell nodes
    // =========================================================
    public const int RetainerSell_ItemNameNodeId = 7;   // item name text (ends with HQ glyph when HQ)
    public const int RetainerSell_AskingPriceNumericNodeId = 10; // NumericInput component node
    public const ushort RetainerSell_AskingPriceTextNodeId = 5;  // child text node inside the component
}
