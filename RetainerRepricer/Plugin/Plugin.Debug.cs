using System;

using FFXIVClientStructs.FFXIV.Component.GUI;
using RetainerRepricer.Services;

namespace RetainerRepricer;

/// <summary>
/// Debug helpers exposed via /repricer commands.
/// </summary>
public unsafe sealed partial class Plugin
{
    private void TestUniversalisGate()
    {
        if (!IsAddonOpen("ItemSearchResult"))
        {
            Log.Information("[RR][TestGate] ItemSearchResult is not open. Open market board listings first.");
            ChatGui.Print("[RetainerRepricer] Open ItemSearchResult first (/mbdump to verify).");
            return;
        }

        if (!TryGetCurrentMarketItemId(out var itemId))
        {
            Log.Warning("[RR][TestGate] Could not resolve current item id.");
            return;
        }

        var region = GetWorldDcRegionKey();
        if (string.IsNullOrWhiteSpace(region))
        {
            Log.Warning("[RR][TestGate] Could not resolve world/DC region.");
            return;
        }

        if (!Configuration.EnableUndercutPreventionGate)
        {
            Log.Information("[RR][TestGate] Gate is disabled in config. Using legacy behavior.");
        }
        else if (!Configuration.UseUniversalisApi)
        {
            Log.Information("[RR][TestGate] Universalis API is disabled in config. Using legacy behavior.");
        }

        var baseUrl = UniversalisApiClient.AggregatedBaseUrl;

        Log.Information($"[RR][TestGate] Fetching Universalis data for item {itemId}, region='{region}', HQ={_currentIsHq}");

        decimal avg;
        try
        {
            var result = _universalisClient.GetAveragePriceAsync(baseUrl, region, itemId, _currentIsHq);
            avg = result != null ? (result.Result ?? 0) : 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RR][TestGate] Universalis API call failed.");
            avg = 0;
        }

        if (avg <= 0)
        {
            Log.Information("[RR][TestGate] Universalis returned no data (or gate disabled). Using legacy row logic.");
            avg = 0;
        }

        var (marketAvg, marketCount) = TryGetMarketAverage();
        if (marketCount > 0 && avg > 0)
        {
            var threshold = marketAvg * (decimal)MarketValidationThreshold;
            if (avg > threshold)
            {
                Log.Information($"[RR][TestGate] API avg {avg:N0} > market avg {marketAvg:N0} * {MarketValidationThreshold:F1} = {threshold:N0}; using market avg instead.");
                avg = marketAvg;
            }
        }

        var floor = ComputeUniversalisFloor(avg, Configuration.UndercutPreventionPercent);
        var percentDisplay = Configuration.UndercutPreventionPercent * 100f;

        Log.Information("[RR][TestGate] === Universalis Results ===");
        Log.Information($"[RR][TestGate] Item ID: {itemId}");
        Log.Information($"[RR][TestGate] Region: {region}");
        Log.Information($"[RR][TestGate] API Average: {(avg > 0 ? avg.ToString("N0") : "N/A")}");
        Log.Information($"[RR][TestGate] Market Average ({marketCount} listings): {(marketAvg > 0 ? marketAvg.ToString("N0") : "N/A")}");
        Log.Information($"[RR][TestGate] Config Percent: {percentDisplay:0}%");
        Log.Information($"[RR][TestGate] Floor Price: {(floor > 0 ? floor.ToString("N0") : "N/A")}");
        Log.Information("[RR][TestGate] ========================");

        var list = _uiReader.GetMarketList();
        if (list == null)
        {
            Log.Warning("[RR][TestGate] Could not get market list.");
            return;
        }

        var count = list->GetItemCount();
        if (count <= 0)
        {
            Log.Information("[RR][TestGate] No listings in market.");
            return;
        }

        var maxRows = Math.Min(count, 10);
        var selectedRow = -1;
        var selectedPrice = 0;

        for (int i = 0; i < maxRows; i++)
        {
            if (!TryReadMarketRow(i, out var price, out var seller, out var isHq))
                continue;

            bool passes = floor <= 0 || price >= floor;
            var status = passes ? "PASS" : "SKIP (below floor)";

            Log.Information($"[RR][TestGate] Row {i}: price={price:N0} hq={isHq} seller='{seller}' => {status}");

            if (selectedRow < 0 && passes)
            {
                selectedRow = i;
                selectedPrice = price;
            }
        }

        if (selectedRow < 0 && floor > 0)
        {
            selectedRow = -1;
            selectedPrice = floor;
            Log.Information($"[RR][TestGate] No rows passed floor. Using floor price: {floor:N0}");
        }
        else if (selectedRow < 0)
        {
            if (TryReadMarketRow(0, out var p0, out var s0, out _))
            {
                selectedRow = 0;
                selectedPrice = p0;
                Log.Information($"[RR][TestGate] No floor set, using row 0: {p0:N0}");
            }
            else
            {
                Log.Information("[RR][TestGate] Could not read any market rows.");
                return;
            }
        }

        var undercutPrice = Math.Max(1, selectedPrice - 1);
        Log.Information("[RR][TestGate] === Final Decision ===");
        Log.Information($"[RR][TestGate] Selected Row: {(selectedRow >= 0 ? selectedRow.ToString() : "FLOOR")}");
        Log.Information($"[RR][TestGate] Selected Price: {selectedPrice:N0}");
        Log.Information($"[RR][TestGate] Undercut Price: {undercutPrice:N0}");
        Log.Information("[RR][TestGate] ========================");

        ChatGui.Print("[RetainerRepricer] TestGate complete - check log for details.");
    }

    private void DumpMarketRows()
    {
        var list = _uiReader.GetMarketList();
        if (list == null)
        {
            Log.Information("[MB] Market list not found. Open ItemSearchResult first.");
            return;
        }

        var count = list->GetItemCount();
        Log.Information($"[MB] renderer count = {count}");

        var max = Math.Min(count, 10);
        for (int i = 0; i < max; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            var seller = _uiReader.ReadRendererText(r, Ui.NodePaths.SellerNodeId);
            var unitRaw = _uiReader.ReadRendererText(r, Ui.NodePaths.UnitPriceNodeId);
            var qtyRaw = _uiReader.ReadRendererText(r, Ui.NodePaths.QuantityNodeId);

            _uiReader.DumpHqIconState(r, i, s => Log.Verbose(s));
            var isHq = _uiReader.RowIsHq(r);

            var unit = Ui.UiReader.ParseGil(unitRaw);
            var qty = int.TryParse(qtyRaw, out var q) ? q : 0;

            Log.Information($"[MB] row {i}: seller={seller} unit={unit} qty={qty} hq={(isHq ? "HQ" : "NQ")}");
        }
    }

    private void DumpRetainerRows()
    {
        var list = _uiReader.GetRetainerList();
        if (list == null)
        {
            Log.Information("[RL] RetainerList not found. Open the summoning bell RetainerList.");
            return;
        }

        var count = list->GetItemCount();
        Log.Information($"[RL] renderer count = {count}");

        var max = Math.Min(count, 10);
        for (int i = 0; i < max; i++)
        {
            var r = list->GetItemRenderer(i);
            if (r == null) continue;

            var name = _uiReader.ReadRendererText(r, Ui.NodePaths.RetainerNameNodeId);
            Log.Information($"[RL] row {i}: name='{name}'");
        }
    }
}
