using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RetainerRepricer.Services;

public sealed class UniversalisApiClient : IDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _httpClient;
    private readonly IPluginLog _log;
    private readonly bool _ownsClient;
    private bool _disposed;

    public UniversalisApiClient(IPluginLog log, HttpClient? httpClient = null)
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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(worldDcRegion))
            return null;

        var requestUri = BuildRequestUri(baseUrl, worldDcRegion, itemId);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.Warning($"[Universalis] Request failed ({response.StatusCode}) for '{requestUri}'.");
                return null;
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<AggregatedResponse>(contentStream, _jsonOptions, cancellationToken).ConfigureAwait(false);

            var result = payload?.Results is { Count: > 0 } list
                ? list[0]
                : null;

            if (result == null)
            {
                _log.Debug($"[Universalis] No results for item {itemId} (HQ={isHighQuality}).");
                return null;
            }

            var quality = isHighQuality ? result.Hq : result.Nq;
            var bestPrice = quality?.AverageSalePrice?.GetBestPrice();

            if (bestPrice is null)
            {
                _log.Debug($"[Universalis] Missing average price for item {itemId} (HQ={isHighQuality}).");
            }

            return bestPrice;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[Universalis] Failed to fetch average price for item {itemId}.");
            return null;
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
    }

    private sealed class AggregatedQuality
    {
        [JsonPropertyName("averageSalePrice")]
        public AggregatedAverage? AverageSalePrice { get; set; }
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
}
