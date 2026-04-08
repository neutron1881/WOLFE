using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using GexBotA.Models;
using GexBotA.Services;
using ATAS.Indicators;
using OFT.Rendering.Settings;

namespace GexBotA;

/// <summary>
/// GexBotA Flow — Panel-based indicator that displays total Call/Put GEX as lines
/// with historical values per bar, plus velocity (rate of change) series.
/// Designed to run in a separate ATAS panel (like Volume or Delta).
/// Consumes the same Gexbot REST API as GexBotAClassic.
/// </summary>
[DisplayName("GexBotA Flow")]
[Category("GexBot")]
[Description("Total Call/Put GEX flow as lines with velocity. Runs in a separate panel.")]
public sealed class GexBotFlow : Indicator
{
    #region Enums

    public enum FlowGexType
    {
        [Description("Volume")] Volume,
        [Description("Open Interest")] OpenInterest
    }

    public enum FlowTicker
    {
        [Description("SPX (Index)")] SPX,
        [Description("SPX -> ES (Index)")] SPX_ES,
        [Description("NDX (Index)")] NDX,
        [Description("NDX -> NQ (Index)")] NDX_NQ,
        [Description("RUT (Index)")] RUT,
        [Description("VIX (Index)")] VIX,
        [Description("SPY (ETF)")] SPY,
        [Description("QQQ (ETF)")] QQQ,
        [Description("IWM (ETF)")] IWM,
        [Description("TLT (ETF)")] TLT,
        [Description("GLD (ETF)")] GLD,
        [Description("USO (ETF)")] USO,
        [Description("TQQQ (ETF)")] TQQQ,
        [Description("UVXY (ETF)")] UVXY,
        [Description("AAPL (Equity)")] AAPL,
        [Description("AMD (Equity)")] AMD,
        [Description("AMZN (Equity)")] AMZN,
        [Description("APP (Equity)")] APP,
        [Description("AVGO (Equity)")] AVGO,
        [Description("BABA (Equity)")] BABA,
        [Description("COIN (Equity)")] COIN,
        [Description("CRWD (Equity)")] CRWD,
        [Description("GME (Equity)")] GME,
        [Description("GOOG (Equity)")] GOOG,
        [Description("GOOGL (Equity)")] GOOGL,
        [Description("HOOD (Equity)")] HOOD,
        [Description("HYG (Equity)")] HYG,
        [Description("IBIT (Equity)")] IBIT,
        [Description("INTC (Equity)")] INTC,
        [Description("IONQ (Equity)")] IONQ,
        [Description("META (Equity)")] META,
        [Description("MSFT (Equity)")] MSFT,
        [Description("MSTR (Equity)")] MSTR,
        [Description("MU (Equity)")] MU,
        [Description("NFLX (Equity)")] NFLX,
        [Description("NVDA (Equity)")] NVDA,
        [Description("PLTR (Equity)")] PLTR,
        [Description("SLV (Equity)")] SLV,
        [Description("SMCI (Equity)")] SMCI,
        [Description("SNOW (Equity)")] SNOW,
        [Description("SOFI (Equity)")] SOFI,
        [Description("TSLA (Equity)")] TSLA,
        [Description("TSM (Equity)")] TSM,
        [Description("UNH (Equity)")] UNH,
        [Description("VALE (Equity)")] VALE
    }

    public enum FlowAggregation
    {
        [Description("90d (agg)")] Full,
        [Description("Latest expiry")] Latest,
        [Description("Next expiry")] Next
    }

    #endregion

    #region Private Fields

    private readonly GexApiClient _apiClient;
    private GexClassicData? _gexData;
    private readonly object _dataLock = new();
    private Task? _fetchLoop;
    private CancellationTokenSource? _cts;
    private readonly ManualResetEventSlim _fetchSignal = new(true);
    private bool _dataLoaded;
    private long _lastDataTimestamp;
    private int _lastCalculatedBar = -1;

    // Setup
    private FlowGexType _gexType = FlowGexType.Volume;
    private FlowTicker _selectedTicker = FlowTicker.SPX;
    private FlowAggregation _aggregation = FlowAggregation.Full;
    private string _apiKey = "";
    private int _refreshIntervalSec = 1;

    // Velocity
    private int _velocityPeriod = 5;

    #endregion

    #region Data Series

    // ── Main flow lines ──

    private readonly ValueDataSeries _callSeries = new("TotalCalls", "Total Calls GEX")
    {
        Color = Color.FromArgb(255, 0, 200, 0).Convert(),
        VisualType = VisualMode.Line,
        LineDashStyle = LineDashStyle.Solid,
        Width = 2,
        ShowZeroValue = false,
        UseMinimizedModeIfEnabled = true,
        ShowCurrentValue = true
    };

    private readonly ValueDataSeries _putSeries = new("TotalPuts", "Total Puts GEX")
    {
        Color = Color.FromArgb(255, 220, 50, 50).Convert(),
        VisualType = VisualMode.Line,
        LineDashStyle = LineDashStyle.Solid,
        Width = 2,
        ShowZeroValue = false,
        UseMinimizedModeIfEnabled = true,
        ShowCurrentValue = true
    };

    private readonly ValueDataSeries _netSeries = new("NetGEX", "Net GEX")
    {
        Color = Color.FromArgb(255, 255, 200, 50).Convert(),
        VisualType = VisualMode.Line,
        LineDashStyle = LineDashStyle.Dash,
        Width = 1,
        ShowZeroValue = false,
        UseMinimizedModeIfEnabled = true,
        ShowCurrentValue = true
    };

    // ── Velocity series (Speed of Tape style) ──

    private readonly ValueDataSeries _callVelocity = new("CallVelocity", "Call Velocity")
    {
        Color = Color.FromArgb(180, 100, 255, 100).Convert(),
        VisualType = VisualMode.Hide,
        ShowZeroValue = false,
        UseMinimizedModeIfEnabled = true,
        ShowCurrentValue = true
    };

    private readonly ValueDataSeries _putVelocity = new("PutVelocity", "Put Velocity")
    {
        Color = Color.FromArgb(180, 255, 100, 100).Convert(),
        VisualType = VisualMode.Hide,
        ShowZeroValue = false,
        UseMinimizedModeIfEnabled = true,
        ShowCurrentValue = true
    };

    private readonly ValueDataSeries _netVelocity = new("NetVelocity", "Net Velocity")
    {
        Color = Color.FromArgb(180, 255, 200, 100).Convert(),
        VisualType = VisualMode.Hide,
        ShowZeroValue = false,
        UseMinimizedModeIfEnabled = true,
        ShowCurrentValue = true
    };

    #endregion

    #region Properties - Setup

    [Display(Name = "Gex Type", GroupName = "Setup", Order = 0,
        Description = "Volume: reactive intraday. Open Interest: stable accumulated positions.")]
    public FlowGexType SelectedGexType
    {
        get => _gexType;
        set { _gexType = value; RecalculateValues(); }
    }

    [Display(Name = "Ticker", GroupName = "Setup", Order = 1)]
    public FlowTicker SelectedTicker
    {
        get => _selectedTicker;
        set
        {
            _selectedTicker = value;
            _apiClient.ClearCache();
            TriggerFetch();
        }
    }

    [Display(Name = "Aggregation", GroupName = "Setup", Order = 2)]
    public FlowAggregation SelectedAggregation
    {
        get => _aggregation;
        set
        {
            _aggregation = value;
            _apiClient.ClearCache();
            TriggerFetch();
        }
    }

    [Display(Name = "API Key", GroupName = "Setup", Order = 3)]
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            _apiKey = value;
            if (!string.IsNullOrWhiteSpace(value))
                TriggerFetch();
        }
    }

    [Display(Name = "Refresh (sec)", GroupName = "Setup", Order = 4,
        Description = "Polling interval in seconds")]
    [Range(1, 600)]
    public int RefreshIntervalSec
    {
        get => _refreshIntervalSec;
        set => _refreshIntervalSec = Math.Clamp(value, 1, 600);
    }

    #endregion

    #region Properties - Velocity

    [Display(Name = "Velocity Period", GroupName = "Velocity", Order = 10,
        Description = "Number of bars to calculate rate of change (Speed of Tape style). " +
                      "Higher = smoother velocity, lower = more reactive.")]
    [Range(1, 50)]
    public int VelocityPeriod
    {
        get => _velocityPeriod;
        set => _velocityPeriod = Math.Clamp(value, 1, 50);
    }

    #endregion

    #region Properties - Visual

    [Display(Name = "Show Net GEX Line", GroupName = "Visual", Order = 20,
        Description = "Show the Net GEX line (Calls + Puts). Yellow dashed line.")]
    public bool ShowNetGex
    {
        get => _netSeries.VisualType != VisualMode.Hide;
        set
        {
            _netSeries.VisualType = value ? VisualMode.Line : VisualMode.Hide;
            RecalculateValues();
        }
    }

    [Display(Name = "Show Call Velocity", GroupName = "Visual", Order = 21,
        Description = "Show Call GEX velocity as histogram. Green bars = increasing call gamma.")]
    public bool ShowCallVelocity
    {
        get => _callVelocity.VisualType != VisualMode.Hide;
        set
        {
            _callVelocity.VisualType = value ? VisualMode.Histogram : VisualMode.Hide;
            RecalculateValues();
        }
    }

    [Display(Name = "Show Put Velocity", GroupName = "Visual", Order = 22,
        Description = "Show Put GEX velocity as histogram. Red bars = increasing put gamma.")]
    public bool ShowPutVelocity
    {
        get => _putVelocity.VisualType != VisualMode.Hide;
        set
        {
            _putVelocity.VisualType = value ? VisualMode.Histogram : VisualMode.Hide;
            RecalculateValues();
        }
    }

    [Display(Name = "Show Net Velocity", GroupName = "Visual", Order = 23,
        Description = "Show Net GEX velocity as histogram. Positive = calls gaining faster.")]
    public bool ShowNetVelocity
    {
        get => _netVelocity.VisualType != VisualMode.Hide;
        set
        {
            _netVelocity.VisualType = value ? VisualMode.Histogram : VisualMode.Hide;
            RecalculateValues();
        }
    }

    #endregion

    #region Constructor

    public GexBotFlow() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DenyToChangePanel = false;

        _apiClient = new GexApiClient();

        // Register data series — order determines display stacking
        DataSeries[0] = _callSeries;
        DataSeries.Add(_putSeries);
        DataSeries.Add(_netSeries);
        DataSeries.Add(_callVelocity);
        DataSeries.Add(_putVelocity);
        DataSeries.Add(_netVelocity);
    }

    #endregion

    #region Indicator Lifecycle

    protected override void OnCalculate(int bar, decimal value)
    {
        if (!_dataLoaded && !string.IsNullOrWhiteSpace(_apiKey))
        {
            _dataLoaded = true;
            TriggerFetch();
        }

        GexClassicData? data;
        lock (_dataLock) { data = _gexData; }

        if (data == null || data.Strikes.Count == 0)
            return;

        bool useVolume = _gexType == FlowGexType.Volume;

        // Aggregate total Call GEX (positive gamma) and total Put GEX (negative gamma)
        double totalCalls = 0;
        double totalPuts = 0;
        foreach (var strike in data.Strikes)
        {
            double gex = useVolume ? strike.GexByVolume : strike.GexByOi;
            if (gex >= 0)
                totalCalls += gex;
            else
                totalPuts += gex; // negative value
        }

        double netGex = totalCalls + totalPuts;

        // Write flow values to the current bar
        _callSeries[bar] = (decimal)totalCalls;
        _putSeries[bar] = (decimal)totalPuts;
        _netSeries[bar] = (decimal)netGex;

        // ── Velocity (Speed of Tape style) ──
        // Change in value over the last N bars
        if (bar > 0)
        {
            int lookback = Math.Min(_velocityPeriod, bar);
            decimal prevCall = _callSeries[bar - lookback];
            decimal prevPut = _putSeries[bar - lookback];
            decimal prevNet = _netSeries[bar - lookback];

            _callVelocity[bar] = prevCall != 0 ? _callSeries[bar] - prevCall : 0;
            _putVelocity[bar] = prevPut != 0 ? _putSeries[bar] - prevPut : 0;
            _netVelocity[bar] = prevNet != 0 ? _netSeries[bar] - prevNet : 0;
        }
        else
        {
            _callVelocity[bar] = 0;
            _putVelocity[bar] = 0;
            _netVelocity[bar] = 0;
        }

        _lastCalculatedBar = bar;
    }

    protected override void OnInitialize()
    {
        base.OnInitialize();
        _cts = new CancellationTokenSource();
        _fetchLoop = Task.Run(() => BackgroundFetchLoopAsync(_cts.Token));
    }

    protected override void OnDispose()
    {
        _cts?.Cancel();
        _fetchSignal.Set();
        try { _fetchLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* swallow */ }
        _cts?.Dispose();
        _fetchSignal.Dispose();
        _apiClient.Dispose();
        base.OnDispose();
    }

    public override string ToString()
    {
        return "GexBotA Flow";
    }

    #endregion

    #region Data Fetching

    private void TriggerFetch()
    {
        _fetchSignal.Set();
    }

    /// <summary>
    /// Background async polling loop with exponential backoff.
    /// Same pattern as GexBotAClassic but lighter (no rendering).
    /// </summary>
    private async Task BackgroundFetchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _fetchSignal.Wait(ct);
                _fetchSignal.Reset();

                if (string.IsNullOrWhiteSpace(_apiKey))
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }

                // Exponential backoff on consecutive failures
                int backoffSec = _apiClient.CurrentBackoffSeconds;
                if (backoffSec > 0)
                    await Task.Delay(backoffSec * 1000, ct).ConfigureAwait(false);

                var apiTicker = GetApiTicker(_selectedTicker);
                string agg = _aggregation switch
                {
                    FlowAggregation.Full => "full",
                    FlowAggregation.Latest => "zero",
                    FlowAggregation.Next => "one",
                    _ => "full"
                };

                var data = await _apiClient.FetchClassicAsync(apiTicker, agg, _apiKey, ct)
                    .ConfigureAwait(false);

                if (data != null)
                {
                    bool changed;
                    lock (_dataLock)
                    {
                        changed = data.Timestamp != _lastDataTimestamp;
                        if (changed)
                        {
                            _lastDataTimestamp = data.Timestamp;
                            _gexData = data;
                        }
                    }

                    if (changed)
                        RecalculateValues();
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* swallow, keep polling */ }

            if (!ct.IsCancellationRequested)
            {
                try
                {
                    int delayMs = Math.Max(1000, _refreshIntervalSec * 1000);
                    _fetchSignal.Wait(delayMs, ct);
                }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    #endregion

    #region Helpers

    private static string GetApiTicker(FlowTicker ticker)
    {
        return ticker switch
        {
            FlowTicker.SPX_ES => "ES_SPX",
            FlowTicker.NDX_NQ => "NQ_NDX",
            _ => ticker.ToString()
        };
    }

    #endregion
}
