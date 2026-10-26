using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RetainerRepricer.Services;

internal sealed class UniversalisApiClient : IDisposable
{
    // Default aggregated endpoint used for deterministic Universalis lookups.
    internal const string AggregatedBaseUrl = "https://universalis.app/api/v2/aggregated";
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _httpClient;
    private readonly PluginLogger _log;
    private readonly bool _ownsClient;
    private bool _disposed;
    private readonly ConcurrentDictionary<StatsCacheKey, StatsCacheEntry> _statsCache = new();
    private readonly object _diagnosticsLock = new();
    private DateTime _cooldownUntilUtc = DateTime.MinValue;
    private long _cacheHits;
    private long _outboundRequests;
    private long _timeouts;
    private long _connectionFailures;
    private long _httpFailures;
    private long _rateLimitFailures;
    private long _suppressedRequests;
    private int _suppressedRequestsDuringCooldown;

    public UniversalisApiClient(PluginLogger log, HttpClient? httpClient = null)
    {
        _log = log;
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public async Task<decimal?> GetAveragePriceAsync(
        string baseUrl,
        string worldDcRegion,
        uint itemId,
        bool isHighQuality,
        CancellationToken cancellationToken = default,
        string source = "gate")
    {
        var stats = await GetListingStatsAsync(
                baseUrl,
                worldDcRegion,
                itemId,
                isHighQuality,
                cacheDuration: TimeSpan.FromMinutes(1),
                cancellationToken: cancellationToken,
                source: source)
            .ConfigureAwait(false);
        return stats?.AveragePrice;
    }

    public async Task<string?> GetAggregatedPayloadAsync(
        string baseUrl,
        string worldDcRegion,
        uint itemId,
        CancellationToken cancellationToken = default,
        string source = "debug")
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(worldDcRegion))
            return null;

        if (TrySuppressUncachedRequest(out _))
            return null;

        var requestUri = BuildRequestUri(baseUrl, worldDcRegion, itemId);
        var startedUtc = DateTime.UtcNow;
        RecordOutboundRequest();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                ThrowForUnsuccessfulResponse(response, itemId, false, worldDcRegion, source, startedUtc);
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw CreateTimeoutException(itemId, false, worldDcRegion, source, startedUtc, ex);
        }
        catch (HttpRequestException ex)
        {
            throw CreateConnectionException(itemId, false, worldDcRegion, source, startedUtc, ex);
        }
        catch (UniversalisRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[Universalis] Failed to fetch aggregated payload for item {itemId}.");
            return null;
        }
    }

    public async Task<UniversalisListingStats?> GetListingStatsAsync(
        string baseUrl,
        string worldDcRegion,
        uint itemId,
        bool isHighQuality,
        TimeSpan? cacheDuration = null,
        CancellationToken cancellationToken = default,
        string source = "unknown")
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(worldDcRegion))
            return null;

        var duration = cacheDuration.GetValueOrDefault(TimeSpan.FromMinutes(5));
        var key = new StatsCacheKey(baseUrl.TrimEnd('/'), worldDcRegion, itemId, isHighQuality);

        if (_statsCache.TryGetValue(key, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
        {
            RecordCacheHit();
            return cached.Stats;
        }

        if (TrySuppressUncachedRequest(out _))
            return null;

        var requestUri = BuildRequestUri(baseUrl, worldDcRegion, itemId);
        var startedUtc = DateTime.UtcNow;
        RecordOutboundRequest();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                ThrowForUnsuccessfulResponse(response, itemId, isHighQuality, worldDcRegion, source, startedUtc);
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<AggregatedResponse>(contentStream, _jsonOptions, cancellationToken).ConfigureAwait(false);

            var result = payload?.Results?.FirstOrDefault();
            if (result == null)
            {
                _log.Debug($"[Universalis] No results for item {itemId} (HQ={isHighQuality}).");
                return null;
            }

            var quality = isHighQuality ? result.Hq : result.Nq;
            if (quality == null)
            {
                _log.Debug($"[Universalis] Missing quality bucket for item {itemId} (HQ={isHighQuality}).");
                return null;
            }

            var worldAverageSalePrice = quality.AverageSalePrice?.World?.Price;
            var worldMinimumListingPrice = quality.MinListing?.World?.Price;
            var avgPrice = quality.AverageSalePrice?.GetBestPrice();
            if (avgPrice is null)
            {
                var fallbackPrice = quality.GetFallbackPrice(out var fallbackSource);
                if (fallbackPrice is not null)
                {
                    _log.Debug($"[Universalis] Missing average price for item {itemId} (HQ={isHighQuality}); using {fallbackSource} price {fallbackPrice.Value:0}.");
                    avgPrice = fallbackPrice;
                }
            }
            var velocity = quality.DailySaleVelocity?.GetBestQuantity();
            var lastUpload = result.GetLatestUploadUtc();

            if (avgPrice is null && velocity is null)
                return null;

            var stats = new UniversalisListingStats(
                avgPrice, velocity, lastUpload, worldAverageSalePrice, worldMinimumListingPrice);

            _statsCache[key] = new StatsCacheEntry
            {
                Stats = stats,
                ExpiresUtc = DateTime.UtcNow + duration,
            };

            return stats;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw CreateTimeoutException(itemId, isHighQuality, worldDcRegion, source, startedUtc, ex);
        }
        catch (HttpRequestException ex)
        {
            throw CreateConnectionException(itemId, isHighQuality, worldDcRegion, source, startedUtc, ex);
        }
        catch (UniversalisRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[Universalis] Failed to fetch listing stats for item {itemId}.");
            return null;
        }
    }

    internal bool TryGetCooldownRemaining(out TimeSpan remaining)
    {
        lock (_diagnosticsLock)
        {
            remaining = _cooldownUntilUtc - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
                return true;

            remaining = TimeSpan.Zero;
            return false;
        }
    }

    internal UniversalisDiagnostics GetDiagnostics()
    {
        lock (_diagnosticsLock)
        {
            var remaining = _cooldownUntilUtc - DateTime.UtcNow;
            return new UniversalisDiagnostics(
                _cacheHits,
                _outboundRequests,
                _timeouts,
                _connectionFailures,
                _httpFailures,
                _rateLimitFailures,
                _suppressedRequests,
                remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
    }

    private bool TrySuppressUncachedRequest(out TimeSpan remaining)
    {
        var cooldownEndedSuppressedRequests = 0;

        lock (_diagnosticsLock)
        {
            remaining = _cooldownUntilUtc - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                _suppressedRequests++;
                _suppressedRequestsDuringCooldown++;
                return true;
            }

            if (_cooldownUntilUtc != DateTime.MinValue)
            {
                cooldownEndedSuppressedRequests = _suppressedRequestsDuringCooldown;
                _cooldownUntilUtc = DateTime.MinValue;
                _suppressedRequestsDuringCooldown = 0;
            }
        }

        if (cooldownEndedSuppressedRequests > 0)
        {
            _log.Information(
                $"[Universalis] Cooldown ended; suppressed {cooldownEndedSuppressedRequests} uncached request(s).");
        }

        remaining = TimeSpan.Zero;
        return false;
    }

    private void RecordCacheHit()
    {
        lock (_diagnosticsLock)
        {
            _cacheHits++;
        }
    }

    private void RecordOutboundRequest()
    {
        lock (_diagnosticsLock)
        {
            _outboundRequests++;
        }
    }

    private UniversalisRequestException CreateTimeoutException(
        uint itemId,
        bool isHighQuality,
        string region,
        string source,
        DateTime startedUtc,
        Exception innerException)
    {
        RegisterFailure(
            UniversalisRequestFailureKind.Timeout,
            itemId,
            isHighQuality,
            region,
            source,
            DateTime.UtcNow - startedUtc,
            null,
            startCooldown: true);
        return new UniversalisRequestException(
            UniversalisRequestFailureKind.Timeout,
            "The Universalis request timed out.",
            innerException);
    }

    private UniversalisRequestException CreateConnectionException(
        uint itemId,
        bool isHighQuality,
        string region,
        string source,
        DateTime startedUtc,
        HttpRequestException innerException)
    {
        RegisterFailure(
            UniversalisRequestFailureKind.Connection,
            itemId,
            isHighQuality,
            region,
            source,
            DateTime.UtcNow - startedUtc,
            null,
            startCooldown: true);
        return new UniversalisRequestException(
            UniversalisRequestFailureKind.Connection,
            "The Universalis connection failed.",
            innerException);
    }

    private void ThrowForUnsuccessfulResponse(
        HttpResponseMessage response,
        uint itemId,
        bool isHighQuality,
        string region,
        string source,
        DateTime startedUtc)
    {
        var statusCode = (int)response.StatusCode;
        var retryAfter = response.Headers.RetryAfter?.Delta;
        var cooldown = statusCode == 429 || statusCode >= 500;
        var kind = statusCode == 429
            ? UniversalisRequestFailureKind.RateLimited
            : UniversalisRequestFailureKind.HttpStatus;

        RegisterFailure(
            kind,
            itemId,
            isHighQuality,
            region,
            source,
            DateTime.UtcNow - startedUtc,
            cooldown ? retryAfter : null,
            cooldown);

        if (!cooldown)
        {
            _log.Warning(
                $"[Universalis] HTTP {statusCode} for item {itemId} (HQ={isHighQuality}, region={region}, source={source}).");
        }

        throw new UniversalisRequestException(
            kind,
            $"Universalis returned HTTP {statusCode}.",
            null,
            statusCode);
    }

    private void RegisterFailure(
        UniversalisRequestFailureKind kind,
        uint itemId,
        bool isHighQuality,
        string region,
        string source,
        TimeSpan elapsed,
        TimeSpan? retryAfter,
        bool startCooldown)
    {
        var now = DateTime.UtcNow;
        var cooldownDuration = retryAfter is { } requested && requested > TimeSpan.Zero
            ? requested
            : CooldownDuration;
        var newCooldownUntilUtc = now + cooldownDuration;
        var startedCooldown = false;

        lock (_diagnosticsLock)
        {
            switch (kind)
            {
                case UniversalisRequestFailureKind.Timeout:
                    _timeouts++;
                    break;
                case UniversalisRequestFailureKind.Connection:
                    _connectionFailures++;
                    break;
                case UniversalisRequestFailureKind.RateLimited:
                    _rateLimitFailures++;
                    _httpFailures++;
                    break;
                case UniversalisRequestFailureKind.HttpStatus:
                    _httpFailures++;
                    break;
            }

            if (startCooldown && _cooldownUntilUtc <= now)
            {
                _cooldownUntilUtc = newCooldownUntilUtc;
                _suppressedRequestsDuringCooldown = 0;
                startedCooldown = true;
            }
            else if (startCooldown && newCooldownUntilUtc > _cooldownUntilUtc)
            {
                _cooldownUntilUtc = newCooldownUntilUtc;
            }
        }

        if (startedCooldown)
        {
            _log.Warning(
                $"[Universalis] {kind} for item {itemId} (HQ={isHighQuality}, region={region}, source={source}, " +
                $"elapsed={elapsed.TotalSeconds:0.0}s); pausing uncached requests for {cooldownDuration.TotalSeconds:0}s.");
        }
    }

    private static string BuildRequestUri(string baseUrl, string worldDcRegion, uint itemId)
    {
        var trimmedBase = baseUrl.TrimEnd('/');
        var encodedRegion = Uri.EscapeDataString(worldDcRegion);
        return $"{trimmedBase}/{encodedRegion}/{itemId}";
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_ownsClient)
            _httpClient.Dispose();

        _disposed = true;
    }

    private sealed class AggregatedResponse
    {
        [JsonPropertyName("results")]
        public List<AggregatedItem> Results { get; set; } = new();
    }

    private sealed class AggregatedItem
    {
        [JsonPropertyName("itemId")]
        public uint ItemId { get; set; }

        [JsonPropertyName("nq")]
        public AggregatedQuality? Nq { get; set; }

        [JsonPropertyName("hq")]
        public AggregatedQuality? Hq { get; set; }

        [JsonPropertyName("worldUploadTimes")]
        public List<AggregatedUploadTime> WorldUploadTimes { get; set; } = new();

        public DateTime? GetLatestUploadUtc()
        {
            if (WorldUploadTimes == null || WorldUploadTimes.Count == 0)
                return null;

            var max = WorldUploadTimes.Max(t => t.Timestamp);
            if (max <= 0)
                return null;

            return DateTimeOffset.FromUnixTimeMilliseconds(max).UtcDateTime;
        }
    }

    private sealed class AggregatedQuality
    {
        [JsonPropertyName("averageSalePrice")]
        public AggregatedAverage? AverageSalePrice { get; set; }

        [JsonPropertyName("dailySaleVelocity")]
        public AggregatedVelocity? DailySaleVelocity { get; set; }

        [JsonPropertyName("minListing")]
        public AggregatedListingPoint? MinListing { get; set; }

        [JsonPropertyName("recentPurchase")]
        public AggregatedPurchasePoint? RecentPurchase { get; set; }

        public decimal? GetFallbackPrice(out string source)
        {
            var recent = RecentPurchase?.GetBestPrice();
            if (recent is { } recentPrice && recentPrice > 0)
            {
                source = "recent purchase";
                return recentPrice;
            }

            var minListing = MinListing?.GetBestPrice();
            if (minListing is { } minListingPrice && minListingPrice > 0)
            {
                source = "min listing";
                return minListingPrice;
            }

            source = string.Empty;
            return null;
        }
    }

    private sealed class AggregatedAverage
    {
        [JsonPropertyName("world")]
        public AggregatedAveragePrice? World { get; set; }

        [JsonPropertyName("dc")]
        public AggregatedAveragePrice? Dc { get; set; }

        [JsonPropertyName("region")]
        public AggregatedAveragePrice? Region { get; set; }

        public decimal? GetBestPrice()
            => World?.Price ?? Dc?.Price ?? Region?.Price;
    }

    private sealed class AggregatedAveragePrice
    {
        [JsonPropertyName("price")]
        public decimal? Price { get; set; }
    }

    private sealed class AggregatedListingPoint
    {
        [JsonPropertyName("world")]
        public AggregatedListingEntry? World { get; set; }

        [JsonPropertyName("dc")]
        public AggregatedListingEntry? Dc { get; set; }

        [JsonPropertyName("region")]
        public AggregatedListingEntry? Region { get; set; }

        public decimal? GetBestPrice()
            => World?.Price ?? Dc?.Price ?? Region?.Price;
    }

    private sealed class AggregatedListingEntry
    {
        [JsonPropertyName("price")]
        public decimal? Price { get; set; }

        [JsonPropertyName("worldId")]
        public uint? WorldId { get; set; }
    }

    private sealed class AggregatedPurchasePoint
    {
        [JsonPropertyName("world")]
        public AggregatedPurchaseEntry? World { get; set; }

        [JsonPropertyName("dc")]
        public AggregatedPurchaseEntry? Dc { get; set; }

        [JsonPropertyName("region")]
        public AggregatedPurchaseEntry? Region { get; set; }

        public decimal? GetBestPrice()
            => World?.Price ?? Dc?.Price ?? Region?.Price;
    }

    private sealed class AggregatedPurchaseEntry
    {
        [JsonPropertyName("price")]
        public decimal? Price { get; set; }

        [JsonPropertyName("timestamp")]
        public long? Timestamp { get; set; }

        [JsonPropertyName("worldId")]
        public uint? WorldId { get; set; }
    }

    private sealed class AggregatedVelocity
    {
        [JsonPropertyName("world")]
        public AggregatedVelocityValue? World { get; set; }

        [JsonPropertyName("dc")]
        public AggregatedVelocityValue? Dc { get; set; }

        [JsonPropertyName("region")]
        public AggregatedVelocityValue? Region { get; set; }

        public double? GetBestQuantity()
            => World?.Quantity ?? Dc?.Quantity ?? Region?.Quantity;
    }

    private sealed class AggregatedVelocityValue
    {
        [JsonPropertyName("quantity")]
        public double? Quantity { get; set; }
    }

    private sealed class AggregatedUploadTime
    {
        [JsonPropertyName("worldId")]
        public uint WorldId { get; set; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }
    }

    private readonly record struct StatsCacheKey(string BaseUrl, string Region, uint ItemId, bool IsHq);

    private sealed class StatsCacheEntry
    {
        public UniversalisListingStats Stats { get; init; } = null!;
        public DateTime ExpiresUtc { get; init; }
    }
}

internal enum UniversalisRequestFailureKind
{
    Timeout,
    Connection,
    HttpStatus,
    RateLimited,
}

internal sealed class UniversalisRequestException : Exception
{
    public UniversalisRequestException(
        UniversalisRequestFailureKind kind,
        string message,
        Exception? innerException,
        int? statusCode = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public UniversalisRequestFailureKind Kind { get; }
    public int? StatusCode { get; }
}

internal readonly record struct UniversalisDiagnostics(
    long CacheHits,
    long OutboundRequests,
    long Timeouts,
    long ConnectionFailures,
    long HttpFailures,
    long RateLimitFailures,
    long SuppressedRequests,
    TimeSpan CooldownRemaining);

public sealed record UniversalisListingStats(
    decimal? AveragePrice,
    double? DailySaleVelocity,
    DateTime? LastUploadUtc,
    decimal? WorldAverageSalePrice = null,
    decimal? WorldMinimumListingPrice = null);
