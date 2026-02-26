using System.Net.Http;
using System.Text.Json;
using GexBotA.Models;

namespace GexBotA.Services;

/// <summary>
/// Async HTTP client for fetching GEX data from the Gexbot Classic API.
/// Uses SemaphoreSlim for async-safe single-flight, stream-based JSON parsing,
/// and timestamp-based change detection to minimize allocations and redraws.
/// </summary>
public sealed class GexApiClient : IDisposable
{
    private const string BaseUrl = "https://api.gexbot.com/";

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);

    private GexClassicData? _cachedClassic;
    private GexMajorsData? _cachedMajors;
    private long _lastClassicTimestamp;
    private long _lastMajorsTimestamp;

    public GexApiClient()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "GexBotA-ATAS/1.0");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <summary>
    /// Fetches full classic GEX data (histogram + levels + priors).
    /// Returns null on error; returns cached data if a fetch is already in-flight.
    /// </summary>
    public async Task<GexClassicData?> FetchClassicAsync(
        string ticker, string aggregation, string apiKey, CancellationToken ct = default)
    {
        if (!await _fetchGate.WaitAsync(0, ct).ConfigureAwait(false))
            return _cachedClassic; // another fetch is in-flight, return cached

        try
        {
            var url = $"{ticker}/classic/{aggregation}?key={apiKey}";
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var data = ParseClassicResponse(doc.RootElement);
            if (data != null)
            {
                _cachedClassic = data;
                _lastClassicTimestamp = data.Timestamp;
            }

            return data;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return _cachedClassic;
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    /// <summary>
    /// Fetches lightweight majors-only data (key levels, no strike histogram).
    /// Much faster response for rapid polling of level changes.
    /// GET /{TICKER}/classic/{AGGREGATION}/majors?key={API_KEY}
    /// </summary>
    public async Task<GexMajorsData?> FetchMajorsAsync(
        string ticker, string aggregation, string apiKey, CancellationToken ct = default)
    {
        try
        {
            var url = $"{ticker}/classic/{aggregation}/majors?key={apiKey}";
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var root = doc.RootElement;
            var data = new GexMajorsData
            {
                Timestamp = root.GetProperty("timestamp").GetInt64(),
                Ticker = root.GetProperty("ticker").GetString() ?? "",
                Spot = root.GetProperty("spot").GetDouble(),
                MajorPosVol = root.GetProperty("mpos_vol").GetDouble(),
                MajorPosOi = root.GetProperty("mpos_oi").GetDouble(),
                MajorNegVol = root.GetProperty("mneg_vol").GetDouble(),
                MajorNegOi = root.GetProperty("mneg_oi").GetDouble(),
                ZeroGamma = root.GetProperty("zero_gamma").GetDouble(),
                NetGexVol = root.GetProperty("net_gex_vol").GetDouble(),
                NetGexOi = root.GetProperty("net_gex_oi").GetDouble()
            };

            _cachedMajors = data;
            _lastMajorsTimestamp = data.Timestamp;
            return data;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return _cachedMajors;
        }
    }

    /// <summary>
    /// Returns true if the last fetched classic data has a newer timestamp than the given one.
    /// </summary>
    public bool HasNewerClassicData(long knownTimestamp) => _lastClassicTimestamp > knownTimestamp;

    /// <summary>
    /// Returns true if the last fetched majors data has a newer timestamp than the given one.
    /// </summary>
    public bool HasNewerMajorsData(long knownTimestamp) => _lastMajorsTimestamp > knownTimestamp;

    /// <summary>
    /// Clears all cached data, forcing a re-fetch on next call.
    /// </summary>
    public void ClearCache()
    {
        _cachedClassic = null;
        _cachedMajors = null;
        _lastClassicTimestamp = 0;
        _lastMajorsTimestamp = 0;
    }

    #region Parsing

    private static GexClassicData? ParseClassicResponse(JsonElement root)
    {
        try
        {
            var data = new GexClassicData
            {
                Timestamp = root.GetProperty("timestamp").GetInt64(),
                Ticker = root.GetProperty("ticker").GetString() ?? "",
                MinDte = root.TryGetProperty("min_dte", out var minDte) ? minDte.GetInt32() : 0,
                SecMinDte = root.TryGetProperty("sec_min_dte", out var secMinDte) ? secMinDte.GetInt32() : 0,
                Spot = root.GetProperty("spot").GetDouble(),
                ZeroGamma = root.GetProperty("zero_gamma").GetDouble(),
                MajorPosVol = root.GetProperty("major_pos_vol").GetDouble(),
                MajorPosOi = root.GetProperty("major_pos_oi").GetDouble(),
                MajorNegVol = root.GetProperty("major_neg_vol").GetDouble(),
                MajorNegOi = root.GetProperty("major_neg_oi").GetDouble(),
                SumGexVol = root.GetProperty("sum_gex_vol").GetDouble(),
                SumGexOi = root.GetProperty("sum_gex_oi").GetDouble()
            };

            if (root.TryGetProperty("strikes", out var strikesEl))
            {
                foreach (var strikeEl in strikesEl.EnumerateArray())
                {
                    if (strikeEl.ValueKind != JsonValueKind.Array)
                        continue;

                    int idx = 0;
                    double s = 0, gv = 0, go = 0;
                    JsonElement priorsEl = default;
                    bool hasPriors = false;

                    foreach (var item in strikeEl.EnumerateArray())
                    {
                        switch (idx)
                        {
                            case 0: s = item.GetDouble(); break;
                            case 1: gv = item.GetDouble(); break;
                            case 2: go = item.GetDouble(); break;
                            case 3:
                                priorsEl = item;
                                hasPriors = item.ValueKind == JsonValueKind.Array;
                                break;
                        }
                        idx++;
                        if (idx > 3) break;
                    }

                    if (idx < 3) continue;

                    var strike = new StrikeData { Strike = s, GexByVolume = gv, GexByOi = go };

                    if (hasPriors)
                    {
                        foreach (var prior in priorsEl.EnumerateArray())
                        {
                            if (prior.ValueKind == JsonValueKind.Number)
                                strike.Priors.Add(prior.GetDouble());
                        }
                    }

                    data.Strikes.Add(strike);
                }
            }

            if (root.TryGetProperty("max_priors", out var maxPriorsEl))
            {
                foreach (var entry in maxPriorsEl.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Array)
                        continue;

                    int idx = 0;
                    double ms = 0, mc = 0;
                    foreach (var item in entry.EnumerateArray())
                    {
                        switch (idx)
                        {
                            case 0: ms = item.GetDouble(); break;
                            case 1: mc = item.GetDouble(); break;
                        }
                        idx++;
                        if (idx > 1) break;
                    }

                    if (idx >= 2)
                        data.MaxPriors.Add(new MaxChangeEntry { Strike = ms, GexChange = mc });
                }
            }

            return data;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    public void Dispose()
    {
        _fetchGate.Dispose();
        _httpClient.Dispose();
    }
}
