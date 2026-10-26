using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RetainerRepricer.Services;

internal readonly record struct VendorInventoryStack(
    int Container,
    int Slot,
    uint ItemId,
    bool IsHq,
    int Quantity,
    string Name,
    bool ItemExists,
    bool IsTradable,
    bool IsCollectable,
    bool IsBound,
    uint NpcSalePrice);

internal readonly record struct VendorPreviewPrice(
    decimal? UnitPrice,
    DateTime? LastUploadUtc,
    string Source,
    bool RequestFailed = false);

internal enum VendorPreviewReason
{
    VendorCandidate,
    ThresholdNotConfigured,
    NotAllowed,
    BlockedFromAutoList,
    ExplicitSellListItem,
    UnknownItem,
    Untradable,
    Collectable,
    Bound,
    NoNpcSaleValue,
    MissingMarketPrice,
    PriceRequestFailed,
    StaleMarketPrice,
    AtOrAboveThreshold,
}

internal sealed record VendorPreviewResult(
    VendorInventoryStack Stack,
    VendorPreviewReason Reason,
    decimal? UnitMarketValue,
    decimal? StackMarketValue,
    long? EstimatedNpcProceeds,
    string PriceSource,
    DateTime? PriceTimestampUtc)
{
    public bool IsCandidate => Reason == VendorPreviewReason.VendorCandidate;
}

/// <summary>Pure threshold and protection decisions for the non-destructive vendor preview.</summary>
internal static class VendorPreviewEvaluator
{
    internal static readonly TimeSpan MaximumPriceAge = TimeSpan.FromHours(24);

    public static VendorPreviewResult Evaluate(
        VendorInventoryStack stack,
        bool isAllowed,
        bool isBlockedFromAutoList,
        bool isExplicitlyAssigned,
        int maximumStackMarketValue,
        VendorPreviewPrice? price,
        DateTime nowUtc)
    {
        if (maximumStackMarketValue <= 0)
            return Result(VendorPreviewReason.ThresholdNotConfigured);
        if (!isAllowed)
            return Result(VendorPreviewReason.NotAllowed);
        if (isBlockedFromAutoList)
            return Result(VendorPreviewReason.BlockedFromAutoList);
        if (isExplicitlyAssigned)
            return Result(VendorPreviewReason.ExplicitSellListItem);
        if (!stack.ItemExists)
            return Result(VendorPreviewReason.UnknownItem);
        if (!stack.IsTradable)
            return Result(VendorPreviewReason.Untradable);
        if (stack.IsCollectable)
            return Result(VendorPreviewReason.Collectable);
        if (stack.IsBound)
            return Result(VendorPreviewReason.Bound);
        if (stack.NpcSalePrice <= 0)
            return Result(VendorPreviewReason.NoNpcSaleValue);
        if (price is not { } marketPrice)
            return Result(VendorPreviewReason.MissingMarketPrice);
        if (marketPrice.RequestFailed)
            return Result(VendorPreviewReason.PriceRequestFailed);
        if (marketPrice.UnitPrice is not { } unitPrice || unitPrice <= 0)
            return Result(VendorPreviewReason.MissingMarketPrice, marketPrice);
        if (marketPrice.LastUploadUtc is not { } lastUpload ||
            lastUpload > nowUtc + TimeSpan.FromMinutes(5) || nowUtc - lastUpload > MaximumPriceAge)
            return Result(VendorPreviewReason.StaleMarketPrice, marketPrice);

        var stackMarketValue = unitPrice * stack.Quantity;
        var proceeds = (long)stack.NpcSalePrice * stack.Quantity;
        return Result(stackMarketValue < maximumStackMarketValue
            ? VendorPreviewReason.VendorCandidate
            : VendorPreviewReason.AtOrAboveThreshold, marketPrice, unitPrice, stackMarketValue, proceeds);

        VendorPreviewResult Result(
            VendorPreviewReason reason,
            VendorPreviewPrice? evidence = null,
            decimal? unit = null,
            decimal? stackValue = null,
            long? npcProceeds = null)
            => new(stack, reason, unit, stackValue, npcProceeds,
                evidence?.Source ?? string.Empty, evidence?.LastUploadUtc);
    }
}

internal static class VendorPreviewResolver
{
    public static async Task<VendorPreviewPrice?> FetchPriceAsync(
        UniversalisApiClient client, uint itemId, bool isHq, string? region, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(region)) return null;
        var stats = await client.GetListingStatsAsync(
            UniversalisApiClient.AggregatedBaseUrl, region, itemId, isHq,
            TimeSpan.FromMinutes(1), cancellationToken, "vendor-sale-revalidation").ConfigureAwait(false);
        return stats is null
            ? new VendorPreviewPrice(null, null, "Universalis", RequestFailed: true)
            : new VendorPreviewPrice(stats.AveragePrice, stats.LastUploadUtc, "Universalis");
    }

    public static async Task<List<VendorPreviewResult>> ResolveAsync(
        UniversalisApiClient client,
        List<VendorInventoryStack> stacks,
        HashSet<ulong> allowed,
        Dictionary<ulong, int> thresholds,
        HashSet<ulong> blocked,
        HashSet<ulong> protectedItems,
        string? region,
        CancellationToken cancellationToken)
    {
        var prices = new Dictionary<(uint, bool), VendorPreviewPrice?>();
        var requestedPrices = 0;
        foreach (var stack in stacks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Configuration.MakeSellKey(stack.ItemId, stack.IsHq);
            var threshold = thresholds.GetValueOrDefault(key);
            var priceKey = (stack.ItemId, stack.IsHq);
            if (threshold <= 0 || !allowed.Contains(key) || blocked.Contains(key) || protectedItems.Contains(key) ||
                !stack.ItemExists || !stack.IsTradable || stack.IsCollectable || stack.IsBound || stack.NpcSalePrice == 0 ||
                prices.ContainsKey(priceKey)) continue;
            if (requestedPrices++ >= 50 || string.IsNullOrWhiteSpace(region))
            {
                prices[priceKey] = null;
                continue;
            }

            try
            {
                var stats = await client.GetListingStatsAsync(
                    UniversalisApiClient.AggregatedBaseUrl, region, stack.ItemId, stack.IsHq,
                    TimeSpan.FromMinutes(1), cancellationToken, "vendor-preview").ConfigureAwait(false);
                prices[priceKey] = stats is null
                    ? new VendorPreviewPrice(null, null, "Universalis", RequestFailed: true)
                    : new VendorPreviewPrice(stats.AveragePrice, stats.LastUploadUtc, "Universalis");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                prices[priceKey] = new VendorPreviewPrice(null, null, "Universalis", RequestFailed: true);
            }
        }

        return stacks.Select(stack =>
        {
            var key = Configuration.MakeSellKey(stack.ItemId, stack.IsHq);
            prices.TryGetValue((stack.ItemId, stack.IsHq), out var price);
            return VendorPreviewEvaluator.Evaluate(stack, allowed.Contains(key), blocked.Contains(key),
                protectedItems.Contains(key), thresholds.GetValueOrDefault(key), price, DateTime.UtcNow);
        }).ToList();
    }
}
