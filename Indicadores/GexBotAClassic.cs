using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using GexBotA.Models;
using GexBotA.Services;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace GexBotA;

[DisplayName("GexBotA Classic")]
[Category("GexBot")]
[Description("Gamma Exposure (GEX) Classic indicator with histogram overlay, key levels, and horizontal info panel")]
public sealed class GexBotAClassic : Indicator
{
    #region Enums

    public enum GexType
    {
        [Description("Volume")]
        Volume,
        [Description("Open Interest")]
        OpenInterest
    }

    public enum GexTicker
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

    public enum AggregationPeriod
    {
        [Description("90d (agg)")] Full,
        [Description("Latest expiry")] Latest,
        [Description("Next expiry")] Next
    }

    public enum PanelLocation
    {
        [Description("Right")] Right,
        [Description("Left")] Left
    }

    public enum PanelSizeOption
    {
        [Description("1/8th")] Eighth,
        [Description("1/4th")] Quarter,
        [Description("1/3rd")] Third,
        [Description("1/2")] Half
    }

    public enum ConversionMode
    {
        [Description("None (1:1)")]
        None,
        [Description("Auto (chart price / API spot)")]
        Auto,
        [Description("Manual")]
        Manual
    }

    #endregion

    #region Constants

    private static readonly string[] MaxChangeLabels = { "curr", "1m", "5m", "10m", "15m", "30m" };

    #endregion

    #region Private Fields

    private readonly GexApiClient _apiClient;
    private GexClassicData? _gexData;
    private readonly object _dataLock = new();
    private Task? _fetchLoop;
    private CancellationTokenSource? _cts;
    private readonly ManualResetEventSlim _fetchSignal = new(true); // starts signaled for immediate first fetch
    private string _lastError = string.Empty;
    private bool _dataLoaded;

    // Render resources
    private RenderPen? _zeroGammaPen;
    private RenderPen? _majorPosPen;
    private RenderPen? _majorNegPen;
    private RenderPen? _spotPen;
    private RenderPen? _centerLinePen;
    private RenderFont? _labelFont;
    private RenderFont? _headerFont;
    private RenderFont? _valueFont;
    private RenderFont? _infoPanelFont;
    private RenderFont? _strikeLabelFont;
    private RenderFont? _gammaCallLabelFont;
    private RenderFont? _gammaPutLabelFont;

    // Setup
    private GexType _gexType = GexType.Volume;
    private GexTicker _selectedTicker = GexTicker.SPX;
    private AggregationPeriod _aggregationPeriod = AggregationPeriod.Full;
    private string _apiKey = "";
    private int _refreshIntervalSec = 1;

    // Conversion
    private ConversionMode _conversionMode = ConversionMode.None;
    private double _manualFactor = 1.0;
    private double _autoFactor = 1.0;
    private decimal _lastChartPrice;

    // General Options
    private PanelLocation _panelLocation = PanelLocation.Right;
    private PanelSizeOption _panelSize = PanelSizeOption.Eighth;
    private bool _showBackground = true;
    private Color _backgroundColor = Color.Black;
    private Color _positiveColor = Color.Green;
    private Color _negativeColor = Color.Red;
    private int _barWidth = 3;

    // Center line (Call/Put divider)
    private bool _showCenterLine = true;
    private Color _centerLineColor = Color.FromArgb(180, 140, 140, 140);
    private DashStyle _centerLineStyle = DashStyle.Dot;
    private float _centerLineWidth = 1f;

    // Labels - Strike price
    private bool _showStrikeLabels = true;
    private Color _strikeLabelColor = Color.White;
    private Color _strikeLabelBg = Color.FromArgb(180, 30, 30, 40);
    private float _strikeLabelFontSize = 8f;
    private int _strikeLabelOffsetX;

    // Labels - Gamma value (Calls / positive GEX)
    private bool _showGammaCallLabels;
    private Color _gammaCallLabelColor = Color.FromArgb(255, 100, 255, 100);
    private Color _gammaCallLabelBg = Color.FromArgb(160, 20, 20, 30);
    private float _gammaCallLabelFontSize = 7.5f;
    private int _gammaCallLabelOffsetX = 4;

    // Labels - Gamma value (Puts / negative GEX)
    private bool _showGammaPutLabels;
    private Color _gammaPutLabelColor = Color.FromArgb(255, 255, 120, 120);
    private Color _gammaPutLabelBg = Color.FromArgb(160, 20, 20, 30);
    private float _gammaPutLabelFontSize = 7.5f;
    private int _gammaPutLabelOffsetX = 4;

    // Priors Dots (per-temporal)
    private bool _showPrior1m = true;
    private Color _prior1mColor = Color.FromArgb(255, 0, 255, 255);
    private int _prior1mSize = 3;

    private bool _showPrior5m = true;
    private Color _prior5mColor = Color.FromArgb(255, 100, 200, 255);
    private int _prior5mSize = 3;

    private bool _showPrior15m = true;
    private Color _prior15mColor = Color.FromArgb(255, 255, 200, 100);
    private int _prior15mSize = 4;

    private bool _showPrior30m = true;
    private Color _prior30mColor = Color.FromArgb(255, 255, 140, 140);
    private int _prior30mSize = 4;

    // Scaling
    private bool _logarithmicScaling;

    // Majors
    private bool _showMajorPositive = true;
    private bool _showMajorNegative = true;
    private DashStyle _majorLineStyle = DashStyle.Dot;
    private float _majorLineWidth = 2f;
    private Color _majorPosColor = Color.FromArgb(255, 0, 200, 0);
    private Color _majorNegColor = Color.FromArgb(255, 200, 0, 0);

    // Misc
    private bool _showZeroGammaLine = true;
    private DashStyle _zeroLineStyle = DashStyle.Dash;
    private Color _zeroLineColor = Color.Orange;
    private float _zeroLineWidth = 2f;

    // Spot line
    private bool _showSpotLine = true;
    private Color _spotLineColor = Color.FromArgb(255, 255, 165, 0);

    // Info Panel
    private bool _showInfoPanel = true;
    private int _infoPanelHeight = 85;
    private Color _infoPanelBg = Color.FromArgb(220, 15, 15, 25);
    private Color _infoPanelText = Color.White;
    private Color _infoPanelHeader = Color.FromArgb(255, 140, 140, 140);

    // Alerts - general
    private bool _enableAlerts = true;
    private bool _alertedMajorPos;
    private bool _alertedMajorNeg;
    private double _lastAlertSpot;
    private long _lastDataTimestamp;

    // Alert 1: Bullish Exhaustion
    private bool _enableBullishExhaustion = true;
    private int _exhaustionScanStrikes = 5;
    private double _exhaustionThresholdPct = 15.0;
    private int _exhaustionCooldownMin = 5;
    private DateTime _lastBullishExhaustionTime = DateTime.MinValue;

    // Alert 2: Magnet Effect (Efecto Imán)
    private bool _enableMagnetAlert = true;
    private double _magnetConcentrationPct = 40.0;
    private int _magnetCooldownMin = 10;
    private DateTime _lastMagnetAlertTime = DateTime.MinValue;
    private bool _wasAboveZeroGamma; // crossing state for edge detection

    // Alert 3: Institutional Liquidation (Gamma Drop)
    private bool _enableLiquidationAlert = true;
    private double _liquidationDropPct = 30.0;
    private int _liquidationProximityTicks = 10;
    private int _liquidationCooldownMin = 15;
    private DateTime _lastLiquidationAlertTime = DateTime.MinValue;

    // Alert 4: Negative Gamma Wall (Hard Support)
    private bool _enableNegativeWallAlert = true;
    private int _negWallScanStrikes = 5;
    private double _negWallVacuumPct = 10.0;
    private int _negWallProximityTicks = 15;
    private int _negWallCooldownMin = 5;
    private DateTime _lastNegativeWallAlertTime = DateTime.MinValue;

    // Visual alert overlay
    private string _activeAlertMessage = string.Empty;
    private Color _activeAlertColor = Color.Empty;
    private DateTime _alertMessageExpiry = DateTime.MinValue;
    private const int AlertDisplaySeconds = 30;

    #endregion

    #region Properties - Setup

    [Display(Name = "Gex Type", GroupName = "Setup", Order = 0)]
    public GexType SelectedGexType
    {
        get => _gexType;
        set { _gexType = value; RecalculateValues(); }
    }

    [Display(Name = "Ticker", GroupName = "Setup", Order = 1)]
    public GexTicker SelectedTicker
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
    public AggregationPeriod Aggregation
    {
        get => _aggregationPeriod;
        set
        {
            _aggregationPeriod = value;
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
            _apiClient.ClearCache();
            TriggerFetch();
        }
    }

    [Display(Name = "Refresh (sec)", GroupName = "Setup", Order = 4)]
    [Range(1, 600)]
    public int RefreshIntervalSec
    {
        get => _refreshIntervalSec;
        set
        {
            _refreshIntervalSec = Math.Clamp(value, 1, 600);
            TriggerFetch(); // wake the loop with the new interval
        }
    }

    #endregion

    #region Properties - Conversion

    [Display(Name = "Conversion Mode", GroupName = "Conversion", Order = 5,
        Description = "None=no conversion | Auto=calculates factor from chart price vs API spot | Manual=user-defined factor")]
    public ConversionMode PriceConversion
    {
        get => _conversionMode;
        set { _conversionMode = value; RecalculateValues(); }
    }

    [Display(Name = "Manual Factor", GroupName = "Conversion", Order = 6,
        Description = "Multiplier applied to API prices (e.g. ~40 for QQQ→NQ, ~10 for SPY→ES)")]
    public double ManualFactor
    {
        get => _manualFactor;
        set { _manualFactor = value; RecalculateValues(); }
    }

    #endregion

    #region Properties - General Options

    [Display(Name = "Panel Location", GroupName = "General Options", Order = 10)]
    public PanelLocation HistogramLocation
    {
        get => _panelLocation;
        set { _panelLocation = value; RecalculateValues(); }
    }

    [Display(Name = "Panel Size", GroupName = "General Options", Order = 11)]
    public PanelSizeOption HistogramPanelSize
    {
        get => _panelSize;
        set { _panelSize = value; RecalculateValues(); }
    }

    [Display(Name = "Background", GroupName = "General Options", Order = 12)]
    public bool ShowBackground
    {
        get => _showBackground;
        set { _showBackground = value; RecalculateValues(); }
    }

    [Display(Name = "Background Color", GroupName = "General Options", Order = 13)]
    public Color BackgroundColor
    {
        get => _backgroundColor;
        set { _backgroundColor = value; RecalculateValues(); }
    }

    [Display(Name = "Positive Values", GroupName = "General Options", Order = 14)]
    public Color PositiveColor
    {
        get => _positiveColor;
        set { _positiveColor = value; RecalculateValues(); }
    }

    [Display(Name = "Negative Values", GroupName = "General Options", Order = 15)]
    public Color NegativeColor
    {
        get => _negativeColor;
        set { _negativeColor = value; RecalculateValues(); }
    }

    [Display(Name = "Bar Width", GroupName = "General Options", Order = 16)]
    [Range(1, 12)]
    public int BarWidth
    {
        get => _barWidth;
        set { _barWidth = Math.Clamp(value, 1, 12); RecalculateValues(); }
    }

    [Display(Name = "Show Center Line", GroupName = "General Options", Order = 17,
        Description = "Vertical line separating positive (Call) and negative (Put) GEX")]
    public bool ShowCenterLine
    {
        get => _showCenterLine;
        set { _showCenterLine = value; RecalculateValues(); }
    }

    [Display(Name = "Center Line Color", GroupName = "General Options", Order = 18)]
    public Color CenterLineColor
    {
        get => _centerLineColor;
        set { _centerLineColor = value; RebuildPens(); RecalculateValues(); }
    }

    [Display(Name = "Center Line Style", GroupName = "General Options", Order = 19)]
    public DashStyle CenterLineStyle
    {
        get => _centerLineStyle;
        set { _centerLineStyle = value; RebuildPens(); RecalculateValues(); }
    }

    [Display(Name = "Center Line Width", GroupName = "General Options", Order = 20)]
    [Range(0.5, 4)]
    public float CenterLineWidth
    {
        get => _centerLineWidth;
        set { _centerLineWidth = value; RebuildPens(); RecalculateValues(); }
    }

    #endregion

    #region Properties - Labels

    // ── Strike Price Labels ──

    [Display(Name = "Show Strike Labels", GroupName = "Labels", Order = 21,
        Description = "Show the strike price next to each histogram bar")]
    public bool ShowStrikeLabels
    {
        get => _showStrikeLabels;
        set { _showStrikeLabels = value; RecalculateValues(); }
    }

    [Display(Name = "Strike Label Color", GroupName = "Labels", Order = 22)]
    public Color StrikeLabelColor
    {
        get => _strikeLabelColor;
        set { _strikeLabelColor = value; RecalculateValues(); }
    }

    [Display(Name = "Strike Label Background", GroupName = "Labels", Order = 23)]
    public Color StrikeLabelBg
    {
        get => _strikeLabelBg;
        set { _strikeLabelBg = value; RecalculateValues(); }
    }

    [Display(Name = "Strike Label Font Size", GroupName = "Labels", Order = 24)]
    [Range(6, 16)]
    public float StrikeLabelFontSize
    {
        get => _strikeLabelFontSize;
        set { _strikeLabelFontSize = Math.Clamp(value, 6, 16); RebuildFonts(); RecalculateValues(); }
    }

    [Display(Name = "Strike Label Offset X", GroupName = "Labels", Order = 25,
        Description = "Horizontal pixel offset from the histogram edge (negative = towards chart)")]
    [Range(-200, 200)]
    public int StrikeLabelOffsetX
    {
        get => _strikeLabelOffsetX;
        set { _strikeLabelOffsetX = Math.Clamp(value, -200, 200); RecalculateValues(); }
    }

    // ── Gamma Labels — Calls (positive GEX) ──

    [Display(Name = "Show Gamma Calls", GroupName = "Labels", Order = 26,
        Description = "Show the GEX value label on positive (Call) bars")]
    public bool ShowGammaCallLabels
    {
        get => _showGammaCallLabels;
        set { _showGammaCallLabels = value; RecalculateValues(); }
    }

    [Display(Name = "Gamma Call Color", GroupName = "Labels", Order = 27)]
    public Color GammaCallLabelColor
    {
        get => _gammaCallLabelColor;
        set { _gammaCallLabelColor = value; RecalculateValues(); }
    }

    [Display(Name = "Gamma Call Background", GroupName = "Labels", Order = 28)]
    public Color GammaCallLabelBg
    {
        get => _gammaCallLabelBg;
        set { _gammaCallLabelBg = value; RecalculateValues(); }
    }

    [Display(Name = "Gamma Call Font Size", GroupName = "Labels", Order = 29)]
    [Range(6, 16)]
    public float GammaCallLabelFontSize
    {
        get => _gammaCallLabelFontSize;
        set { _gammaCallLabelFontSize = Math.Clamp(value, 6, 16); RebuildFonts(); RecalculateValues(); }
    }

    [Display(Name = "Gamma Call Offset X", GroupName = "Labels", Order = 30,
        Description = "Horizontal offset from the strike label for Call gamma values")]
    [Range(-100, 100)]
    public int GammaCallLabelOffsetX
    {
        get => _gammaCallLabelOffsetX;
        set { _gammaCallLabelOffsetX = Math.Clamp(value, -100, 100); RecalculateValues(); }
    }

    // ── Gamma Labels — Puts (negative GEX) ──

    [Display(Name = "Show Gamma Puts", GroupName = "Labels", Order = 31,
        Description = "Show the GEX value label on negative (Put) bars")]
    public bool ShowGammaPutLabels
    {
        get => _showGammaPutLabels;
        set { _showGammaPutLabels = value; RecalculateValues(); }
    }

    [Display(Name = "Gamma Put Color", GroupName = "Labels", Order = 32)]
    public Color GammaPutLabelColor
    {
        get => _gammaPutLabelColor;
        set { _gammaPutLabelColor = value; RecalculateValues(); }
    }

    [Display(Name = "Gamma Put Background", GroupName = "Labels", Order = 33)]
    public Color GammaPutLabelBg
    {
        get => _gammaPutLabelBg;
        set { _gammaPutLabelBg = value; RecalculateValues(); }
    }

    [Display(Name = "Gamma Put Font Size", GroupName = "Labels", Order = 34)]
    [Range(6, 16)]
    public float GammaPutLabelFontSize
    {
        get => _gammaPutLabelFontSize;
        set { _gammaPutLabelFontSize = Math.Clamp(value, 6, 16); RebuildFonts(); RecalculateValues(); }
    }

    [Display(Name = "Gamma Put Offset X", GroupName = "Labels", Order = 35,
        Description = "Horizontal offset from the strike label for Put gamma values")]
    [Range(-100, 100)]
    public int GammaPutLabelOffsetX
    {
        get => _gammaPutLabelOffsetX;
        set { _gammaPutLabelOffsetX = Math.Clamp(value, -100, 100); RecalculateValues(); }
    }

    #endregion

    #region Properties - Priors Dots

    // ── 1 min ──

    [Display(Name = "Show 1 min", GroupName = "Priors Dots", Order = 24)]
    public bool ShowPrior1m
    {
        get => _showPrior1m;
        set { _showPrior1m = value; RecalculateValues(); }
    }

    [Display(Name = "1 min Color", GroupName = "Priors Dots", Order = 25)]
    public Color Prior1mColor
    {
        get => _prior1mColor;
        set { _prior1mColor = value; RecalculateValues(); }
    }

    [Display(Name = "1 min Size", GroupName = "Priors Dots", Order = 26)]
    [Range(1, 12)]
    public int Prior1mSize
    {
        get => _prior1mSize;
        set { _prior1mSize = Math.Clamp(value, 1, 12); RecalculateValues(); }
    }

    // ── 5 min ──

    [Display(Name = "Show 5 min", GroupName = "Priors Dots", Order = 27)]
    public bool ShowPrior5m
    {
        get => _showPrior5m;
        set { _showPrior5m = value; RecalculateValues(); }
    }

    [Display(Name = "5 min Color", GroupName = "Priors Dots", Order = 28)]
    public Color Prior5mColor
    {
        get => _prior5mColor;
        set { _prior5mColor = value; RecalculateValues(); }
    }

    [Display(Name = "5 min Size", GroupName = "Priors Dots", Order = 29)]
    [Range(1, 12)]
    public int Prior5mSize
    {
        get => _prior5mSize;
        set { _prior5mSize = Math.Clamp(value, 1, 12); RecalculateValues(); }
    }

    // ── 15 min ──

    [Display(Name = "Show 15 min", GroupName = "Priors Dots", Order = 30)]
    public bool ShowPrior15m
    {
        get => _showPrior15m;
        set { _showPrior15m = value; RecalculateValues(); }
    }

    [Display(Name = "15 min Color", GroupName = "Priors Dots", Order = 31)]
    public Color Prior15mColor
    {
        get => _prior15mColor;
        set { _prior15mColor = value; RecalculateValues(); }
    }

    [Display(Name = "15 min Size", GroupName = "Priors Dots", Order = 32)]
    [Range(1, 12)]
    public int Prior15mSize
    {
        get => _prior15mSize;
        set { _prior15mSize = Math.Clamp(value, 1, 12); RecalculateValues(); }
    }

    // ── 30 min ──

    [Display(Name = "Show 30 min", GroupName = "Priors Dots", Order = 33)]
    public bool ShowPrior30m
    {
        get => _showPrior30m;
        set { _showPrior30m = value; RecalculateValues(); }
    }

    [Display(Name = "30 min Color", GroupName = "Priors Dots", Order = 34)]
    public Color Prior30mColor
    {
        get => _prior30mColor;
        set { _prior30mColor = value; RecalculateValues(); }
    }

    [Display(Name = "30 min Size", GroupName = "Priors Dots", Order = 35)]
    [Range(1, 12)]
    public int Prior30mSize
    {
        get => _prior30mSize;
        set { _prior30mSize = Math.Clamp(value, 1, 12); RecalculateValues(); }
    }

    #endregion

    #region Properties - Scaling Options

    [Display(Name = "Logarithmic Scaling", GroupName = "Scaling Options", Order = 40)]
    public bool LogarithmicScaling
    {
        get => _logarithmicScaling;
        set { _logarithmicScaling = value; RecalculateValues(); }
    }

    #endregion

    #region Properties - Majors

    [Display(Name = "Show Major Positive", GroupName = "Majors", Order = 41)]
    public bool ShowMajorPositive
    {
        get => _showMajorPositive;
        set { _showMajorPositive = value; RecalculateValues(); }
    }

    [Display(Name = "Show Major Negative", GroupName = "Majors", Order = 42)]
    public bool ShowMajorNegative
    {
        get => _showMajorNegative;
        set { _showMajorNegative = value; RecalculateValues(); }
    }

    [Display(Name = "Major Positive Color", GroupName = "Majors", Order = 43)]
    public Color MajorPosColor
    {
        get => _majorPosColor;
        set { _majorPosColor = value; RebuildPens(); RecalculateValues(); }
    }

    [Display(Name = "Major Negative Color", GroupName = "Majors", Order = 44)]
    public Color MajorNegColor
    {
        get => _majorNegColor;
        set { _majorNegColor = value; RebuildPens(); RecalculateValues(); }
    }

    [Display(Name = "Line Style", GroupName = "Majors", Order = 45)]
    public DashStyle MajorLineStyle
    {
        get => _majorLineStyle;
        set { _majorLineStyle = value; RebuildPens(); RecalculateValues(); }
    }

    [Display(Name = "Line Width", GroupName = "Majors", Order = 46)]
    [Range(1, 5)]
    public float MajorLineWidth
    {
        get => _majorLineWidth;
        set { _majorLineWidth = value; RebuildPens(); RecalculateValues(); }
    }

    #endregion

    #region Properties - Misc

    [Display(Name = "Show Zero Gamma Line", GroupName = "Misc", Order = 50)]
    public bool ShowZeroGammaLine
    {
        get => _showZeroGammaLine;
        set { _showZeroGammaLine = value; RecalculateValues(); }
    }

    [Display(Name = "Zero Line Style", GroupName = "Misc", Order = 51)]
    public DashStyle ZeroLineStyle
    {
        get => _zeroLineStyle;
        set { _zeroLineStyle = value; RebuildPens(); RecalculateValues(); }
    }

    [Display(Name = "Zero Line Color", GroupName = "Misc", Order = 52)]
    public Color ZeroLineColor
    {
        get => _zeroLineColor;
        set { _zeroLineColor = value; RebuildPens(); RecalculateValues(); }
    }

    [Display(Name = "Zero Line Width", GroupName = "Misc", Order = 53)]
    [Range(1, 5)]
    public float ZeroLineWidth
    {
        get => _zeroLineWidth;
        set { _zeroLineWidth = value; RebuildPens(); RecalculateValues(); }
    }

    [Display(Name = "Show Spot Line", GroupName = "Misc", Order = 54)]
    public bool ShowSpotLine
    {
        get => _showSpotLine;
        set { _showSpotLine = value; RecalculateValues(); }
    }

    [Display(Name = "Spot Line Color", GroupName = "Misc", Order = 55)]
    public Color SpotLineColor
    {
        get => _spotLineColor;
        set { _spotLineColor = value; RebuildPens(); RecalculateValues(); }
    }

    #endregion

    #region Properties - Alert 1: Agotamiento

    [Display(Name = "Enable Alerts", GroupName = "Alert 1: Agotamiento", Order = 70,
        Description = "Master toggle for all alert types")]
    public bool EnableAlerts
    {
        get => _enableAlerts;
        set => _enableAlerts = value;
    }

    [Display(Name = "Bullish Exhaustion", GroupName = "Alert 1: Agotamiento", Order = 71,
        Description = "Alert when price crosses above Major Positive and there is no Gamma above to sustain the rally")]
    public bool EnableBullishExhaustion
    {
        get => _enableBullishExhaustion;
        set => _enableBullishExhaustion = value;
    }

    [Display(Name = "Scan Strikes", GroupName = "Alert 1: Agotamiento", Order = 72,
        Description = "Number of strikes above spot to scan for remaining Gamma (default 5)")]
    [Range(2, 20)]
    public int ExhaustionScanStrikes
    {
        get => _exhaustionScanStrikes;
        set => _exhaustionScanStrikes = Math.Clamp(value, 2, 20);
    }

    [Display(Name = "Threshold %", GroupName = "Alert 1: Agotamiento", Order = 73,
        Description = "If Gamma above is less than this % of Major Positive, fire alert (default 15%)")]
    [Range(1, 50)]
    public double ExhaustionThresholdPct
    {
        get => _exhaustionThresholdPct;
        set => _exhaustionThresholdPct = Math.Clamp(value, 1, 50);
    }

    [Display(Name = "Cooldown (min)", GroupName = "Alert 1: Agotamiento", Order = 74,
        Description = "Minimum minutes between repeated exhaustion alerts (anti-spam)")]
    [Range(1, 60)]
    public int ExhaustionCooldownMin
    {
        get => _exhaustionCooldownMin;
        set => _exhaustionCooldownMin = Math.Clamp(value, 1, 60);
    }

    #endregion

    #region Properties - Alert 2: Efecto Imán

    [Display(Name = "Magnet Alert", GroupName = "Alert 2: Efecto Im\u00e1n", Order = 80,
        Description = "Alert when price breaks above Zero Gamma and a massive Call Gamma block acts as magnet above")]
    public bool EnableMagnetAlert
    {
        get => _enableMagnetAlert;
        set => _enableMagnetAlert = value;
    }

    [Display(Name = "Concentration %", GroupName = "Alert 2: Efecto Im\u00e1n", Order = 81,
        Description = "Target strike must represent at least this % of total Gamma above to qualify as magnet (default 40%)")]
    [Range(10, 80)]
    public double MagnetConcentrationPct
    {
        get => _magnetConcentrationPct;
        set => _magnetConcentrationPct = Math.Clamp(value, 10, 80);
    }

    [Display(Name = "Cooldown (min)", GroupName = "Alert 2: Efecto Im\u00e1n", Order = 82,
        Description = "Minimum minutes between magnet alerts, or until price falls below Zero Gamma again")]
    [Range(1, 60)]
    public int MagnetCooldownMin
    {
        get => _magnetCooldownMin;
        set => _magnetCooldownMin = Math.Clamp(value, 1, 60);
    }

    #endregion

    #region Properties - Alert 3: Liquidaciones

    [Display(Name = "Liquidation Alert", GroupName = "Alert 3: Liquidaciones", Order = 90,
        Description = "Alert when institutional players close options at Major Positive, causing a sudden Gamma drop")]
    public bool EnableLiquidationAlert
    {
        get => _enableLiquidationAlert;
        set => _enableLiquidationAlert = value;
    }

    [Display(Name = "Drop %", GroupName = "Alert 3: Liquidaciones", Order = 91,
        Description = "Minimum Gamma drop from 15 min ago to trigger alert (default 30% = wall lost 30%+ of strength)")]
    [Range(10, 80)]
    public double LiquidationDropPct
    {
        get => _liquidationDropPct;
        set => _liquidationDropPct = Math.Clamp(value, 10, 80);
    }

    [Display(Name = "Proximity (ticks)", GroupName = "Alert 3: Liquidaciones", Order = 92,
        Description = "Spot must be within this many ticks of Major Positive to monitor for liquidation")]
    [Range(1, 50)]
    public int LiquidationProximityTicks
    {
        get => _liquidationProximityTicks;
        set => _liquidationProximityTicks = Math.Clamp(value, 1, 50);
    }

    [Display(Name = "Cooldown (min)", GroupName = "Alert 3: Liquidaciones", Order = 93,
        Description = "Minimum minutes between liquidation alerts (structural alert, default 15 min)")]
    [Range(1, 60)]
    public int LiquidationCooldownMin
    {
        get => _liquidationCooldownMin;
        set => _liquidationCooldownMin = Math.Clamp(value, 1, 60);
    }

    #endregion

    #region Properties - Alert 4: Muro Negativo

    [Display(Name = "Negative Wall Alert", GroupName = "Alert 4: Muro Negativo", Order = 100,
        Description = "Alert when price falls towards Major Negative and there is no Gamma below (hard support / bounce zone)")]
    public bool EnableNegativeWallAlert
    {
        get => _enableNegativeWallAlert;
        set => _enableNegativeWallAlert = value;
    }

    [Display(Name = "Scan Strikes Below", GroupName = "Alert 4: Muro Negativo", Order = 101,
        Description = "Number of strikes below Major Negative to scan for vacuum (default 5)")]
    [Range(2, 20)]
    public int NegWallScanStrikes
    {
        get => _negWallScanStrikes;
        set => _negWallScanStrikes = Math.Clamp(value, 2, 20);
    }

    [Display(Name = "Vacuum Threshold %", GroupName = "Alert 4: Muro Negativo", Order = 102,
        Description = "If absolute Gamma below is less than this % of Major Negative, it is a vacuum (default 10%)")]
    [Range(1, 30)]
    public double NegWallVacuumPct
    {
        get => _negWallVacuumPct;
        set => _negWallVacuumPct = Math.Clamp(value, 1, 30);
    }

    [Display(Name = "Proximity (ticks)", GroupName = "Alert 4: Muro Negativo", Order = 103,
        Description = "Spot must be within this many ticks above Major Negative to trigger (default 15)")]
    [Range(1, 100)]
    public int NegWallProximityTicks
    {
        get => _negWallProximityTicks;
        set => _negWallProximityTicks = Math.Clamp(value, 1, 100);
    }

    [Display(Name = "Cooldown (min)", GroupName = "Alert 4: Muro Negativo", Order = 104,
        Description = "Minimum minutes between negative wall alerts")]
    [Range(1, 60)]
    public int NegWallCooldownMin
    {
        get => _negWallCooldownMin;
        set => _negWallCooldownMin = Math.Clamp(value, 1, 60);
    }

    #endregion

    #region Properties - Info Panel

    [Display(Name = "Show Info Panel", GroupName = "Info Panel", Order = 60)]
    public bool ShowInfoPanel
    {
        get => _showInfoPanel;
        set { _showInfoPanel = value; RecalculateValues(); }
    }

    [Display(Name = "Panel Height", GroupName = "Info Panel", Order = 61)]
    [Range(50, 200)]
    public int InfoPanelHeight
    {
        get => _infoPanelHeight;
        set { _infoPanelHeight = Math.Clamp(value, 50, 200); RecalculateValues(); }
    }

    [Display(Name = "Panel Background", GroupName = "Info Panel", Order = 62)]
    public Color InfoPanelBg
    {
        get => _infoPanelBg;
        set { _infoPanelBg = value; RecalculateValues(); }
    }

    [Display(Name = "Text Color", GroupName = "Info Panel", Order = 63)]
    public Color InfoPanelTextColor
    {
        get => _infoPanelText;
        set { _infoPanelText = value; RecalculateValues(); }
    }

    [Display(Name = "Header Color", GroupName = "Info Panel", Order = 64)]
    public Color InfoPanelHeaderColor
    {
        get => _infoPanelHeader;
        set { _infoPanelHeader = value; RecalculateValues(); }
    }

    #endregion

    #region Constructor

    public GexBotAClassic() : base(true)
    {
        Panel = IndicatorDataProvider.CandlesPanel;
        EnableCustomDrawing = true;
        DenyToChangePanel = true;
        SubscribeToDrawingEvents(DrawingLayouts.LatestBar);

        _apiClient = new GexApiClient();
        RebuildPens();
        RebuildFonts();
    }

    #endregion

    #region Indicator Lifecycle

    protected override void OnCalculate(int bar, decimal value)
    {
        // Track the latest candle close for Auto conversion
        _lastChartPrice = value;

        // Recalculate auto factor on every bar so it doesn't depend on fetch timing
        RecalcAutoFactor();

        if (!_dataLoaded && !string.IsNullOrWhiteSpace(_apiKey))
        {
            _dataLoaded = true;
            TriggerFetch();
        }
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
        _fetchSignal.Set(); // unblock if waiting
        try { _fetchLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* swallow */ }
        _cts?.Dispose();
        _fetchSignal.Dispose();
        _apiClient.Dispose();
        base.OnDispose();
    }

    #endregion

    #region Rendering - Main

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (layout != DrawingLayouts.LatestBar)
            return;

        GexClassicData? data;
        lock (_dataLock)
        {
            data = _gexData;
        }

        var fullRect = ChartInfo.PriceChartContainer.Region;
        if (fullRect.Width <= 0 || fullRect.Height <= 0)
            return;

        if (data == null || data.Strikes.Count == 0)
        {
            DrawStatusMessage(context, fullRect);
            return;
        }

        // Fallback: refresh auto factor during render using visible chart price
        if (_conversionMode == ConversionMode.Auto)
        {
            if (_lastChartPrice == 0)
            {
                var container = ChartInfo.PriceChartContainer;
                _lastChartPrice = (container.High + container.Low) / 2;
            }
            RecalcAutoFactor();
        }

        // Calculate histogram panel area
        var histRect = GetHistogramRect(fullRect);

        // Draw histogram panel background
        if (_showBackground)
            context.FillRectangle(Color.FromArgb(_backgroundColor.A, _backgroundColor.R, _backgroundColor.G, _backgroundColor.B), histRect);

        // Draw histogram bars
        DrawHistogram(context, data, histRect);

        // Draw lookback dots (per-temporal)
        DrawLookbackDots(context, data, histRect);

        // Draw strike / gamma labels
        if (_showStrikeLabels || _showGammaCallLabels || _showGammaPutLabels)
            DrawStrikeLabels(context, data, histRect);

        // Draw key levels across the full chart width
        DrawKeyLevels(context, data, fullRect);

        // Draw horizontal info panel at the top
        if (_showInfoPanel)
            DrawInfoPanel(context, data, fullRect);

        // Process alerts
        if (_enableAlerts)
            ProcessAlerts(data);

        // Draw visual alert overlay (if active)
        DrawAlertOverlay(context, fullRect);
    }

    #endregion

    #region Rendering - Histogram

    private void DrawHistogram(RenderContext context, GexClassicData data, Rectangle histRect)
    {
        int centerX = histRect.Left + histRect.Width / 2;
        int maxBarHalf = histRect.Width / 2 - 4;

        // Draw vertical center line (Call/Put divider)
        if (_showCenterLine && _centerLinePen != null)
            context.DrawLine(_centerLinePen, centerX, histRect.Top, centerX, histRect.Bottom);

        bool useVolume = _gexType == GexType.Volume;
        double maxAbsGex = data.Strikes.Max(s => Math.Abs(useVolume ? s.GexByVolume : s.GexByOi));
        if (maxAbsGex < 0.0001) maxAbsGex = 1;

        foreach (var strike in data.Strikes)
        {
            decimal adjustedPrice = ApplyMultiplier(strike.Strike);
            int y = PriceToY(adjustedPrice);
            if (y < histRect.Top - _barWidth || y > histRect.Bottom + _barWidth)
                continue;

            double gex = useVolume ? strike.GexByVolume : strike.GexByOi;
            int width = ScaleGex(Math.Abs(gex), maxAbsGex, maxBarHalf);
            if (width < 1) width = 1;

            int barTop = y - _barWidth / 2;
            Rectangle barRect;
            Color barColor;

            if (gex >= 0)
            {
                barRect = new Rectangle(centerX, barTop, width, _barWidth);
                barColor = _positiveColor;
            }
            else
            {
                barRect = new Rectangle(centerX - width, barTop, width, _barWidth);
                barColor = _negativeColor;
            }

            context.FillRectangle(barColor, barRect);
        }
    }

    #endregion

    #region Rendering - Lookback Dots

    /// <summary>
    /// Prior dot configuration: (priors array index, show toggle, color, dot size).
    /// Priors array from API: [0]=1min, [1]=5min, [2]=10min, [3]=15min, [4]=30min.
    /// We expose 1m (idx 0), 5m (idx 1), 15m (idx 3), 30m (idx 4).
    /// </summary>
    private void DrawLookbackDots(RenderContext context, GexClassicData data, Rectangle histRect)
    {
        // Per-temporal config: (priorIndex, enabled, color, dotSize)
        // Priors array from API: [0]=1min, [1]=5min, [2]=10min, [3]=15min, [4]=30min
        var configs = new (int idx, bool show, Color color, int size)[]
        {
            (0, _showPrior1m,  _prior1mColor,  _prior1mSize),
            (1, _showPrior5m,  _prior5mColor,  _prior5mSize),
            (3, _showPrior15m, _prior15mColor, _prior15mSize),
            (4, _showPrior30m, _prior30mColor, _prior30mSize)
        };

        // Quick check: anything enabled?
        bool anyEnabled = false;
        foreach (var c in configs)
            if (c.show) { anyEnabled = true; break; }
        if (!anyEnabled) return;

        int centerX = histRect.Left + histRect.Width / 2;
        int maxBarHalf = histRect.Width / 2 - 4;

        bool useVolume = _gexType == GexType.Volume;
        double maxAbsGex = data.Strikes.Max(s => Math.Abs(useVolume ? s.GexByVolume : s.GexByOi));
        if (maxAbsGex < 0.0001) return;

        foreach (var strike in data.Strikes)
        {
            decimal adjustedPrice = ApplyMultiplier(strike.Strike);
            int y = PriceToY(adjustedPrice);
            if (y < histRect.Top - 12 || y > histRect.Bottom + 12)
                continue;

            foreach (var (idx, show, color, size) in configs)
            {
                if (!show || idx >= strike.Priors.Count)
                    continue;

                double priorGex = strike.Priors[idx];
                int dotX = centerX + ScaleGexSigned(priorGex, maxAbsGex, maxBarHalf);
                int half = size / 2;

                var dotRect = new Rectangle(dotX - half, y - half, size, size);
                context.FillEllipse(color, dotRect);
            }
        }
    }

    #endregion

    #region Rendering - Strike Labels

    private void DrawStrikeLabels(RenderContext context, GexClassicData data, Rectangle histRect)
    {
        if (_strikeLabelFont == null || _gammaCallLabelFont == null || _gammaPutLabelFont == null)
            return;

        bool useVolume = _gexType == GexType.Volume;
        double maxAbsGex = data.Strikes.Max(s => Math.Abs(useVolume ? s.GexByVolume : s.GexByOi));
        if (maxAbsGex < 0.0001) maxAbsGex = 1;

        int centerX = histRect.Left + histRect.Width / 2;
        int maxBarHalf = histRect.Width / 2 - 4;

        foreach (var strike in data.Strikes)
        {
            decimal adjustedPrice = ApplyMultiplier(strike.Strike);
            int y = PriceToY(adjustedPrice);
            if (y < histRect.Top - 10 || y > histRect.Bottom + 10)
                continue;

            double gex = useVolume ? strike.GexByVolume : strike.GexByOi;
            bool isCall = gex >= 0;
            int barWidth = ScaleGex(Math.Abs(gex), maxAbsGex, maxBarHalf);
            if (barWidth < 1) barWidth = 1;

            int barTipX = isCall ? centerX + barWidth : centerX - barWidth;

            // ── Strike price label (raw ticker price, NOT chart-converted) ──
            int labelX = barTipX + _strikeLabelOffsetX;
            if (_showStrikeLabels)
            {
                string strikeText = $"{strike.Strike:F0}";
                var sz = context.MeasureString(strikeText, _strikeLabelFont);

                int lx = isCall ? labelX + 2 : labelX - sz.Width - 2;
                int ly = y - sz.Height / 2;
                var bgRect = new Rectangle(lx - 1, ly - 1, sz.Width + 2, sz.Height + 1);

                if (_strikeLabelBg.A > 0)
                    context.FillRectangle(_strikeLabelBg, bgRect);
                context.DrawString(strikeText, _strikeLabelFont, _strikeLabelColor, lx, ly);

                labelX = isCall ? lx + sz.Width : lx;
            }

            // ── Gamma value label (Call or Put, independent config) ──
            bool showGamma = isCall ? _showGammaCallLabels : _showGammaPutLabels;
            if (showGamma)
            {
                var font   = isCall ? _gammaCallLabelFont : _gammaPutLabelFont;
                var color  = isCall ? _gammaCallLabelColor : _gammaPutLabelColor;
                var bg     = isCall ? _gammaCallLabelBg : _gammaPutLabelBg;
                int offset = isCall ? _gammaCallLabelOffsetX : _gammaPutLabelOffsetX;

                string gammaText = FormatGexValue(gex);
                var gsz = context.MeasureString(gammaText, font);

                int gx = isCall
                    ? labelX + offset
                    : labelX - gsz.Width - offset;

                int gy = y - gsz.Height / 2;
                var gBgRect = new Rectangle(gx - 1, gy - 1, gsz.Width + 2, gsz.Height + 1);

                if (bg.A > 0)
                    context.FillRectangle(bg, gBgRect);
                context.DrawString(gammaText, font, color, gx, gy);
            }
        }
    }

    #endregion

    #region Rendering - Key Levels

    private void DrawKeyLevels(RenderContext context, GexClassicData data, Rectangle chartRect)
    {
        bool useVolume = _gexType == GexType.Volume;

        if (_showZeroGammaLine && _zeroGammaPen != null)
        {
            decimal zg = ApplyMultiplier(data.ZeroGamma);
            DrawHorizontalLine(context, zg, _zeroGammaPen, "Zero Γ", data.ZeroGamma, _zeroLineColor, chartRect);
        }

        double majorPos = useVolume ? data.MajorPosVol : data.MajorPosOi;
        double majorNeg = useVolume ? data.MajorNegVol : data.MajorNegOi;

        if (_showMajorPositive && _majorPosPen != null)
            DrawHorizontalLine(context, ApplyMultiplier(majorPos), _majorPosPen, "Major+", majorPos, _majorPosColor, chartRect);

        if (_showMajorNegative && _majorNegPen != null)
            DrawHorizontalLine(context, ApplyMultiplier(majorNeg), _majorNegPen, "Major−", majorNeg, _majorNegColor, chartRect);

        if (_showSpotLine && _spotPen != null)
            DrawHorizontalLine(context, ApplyMultiplier(data.Spot), _spotPen, "Spot", data.Spot, _spotLineColor, chartRect);
    }

    private void DrawHorizontalLine(RenderContext context, decimal chartPrice, RenderPen pen, string label, double tickerPrice, Color labelColor, Rectangle chartRect)
    {
        int y = PriceToY(chartPrice);
        if (y < chartRect.Top || y > chartRect.Bottom)
            return;

        context.DrawLine(pen, chartRect.Left, y, chartRect.Right, y);

        if (_labelFont != null)
        {
            string text = $"{label} {tickerPrice:F2}";
            var sz = context.MeasureString(text, _labelFont);
            var bgRect = new Rectangle(chartRect.Left + 4, y - sz.Height - 2, sz.Width + 6, sz.Height + 2);
            context.FillRectangle(Color.FromArgb(190, 0, 0, 0), bgRect);
            context.DrawString(text, _labelFont, labelColor, bgRect, new RenderStringFormat());
        }
    }

    #endregion

    #region Rendering - Horizontal Info Panel

    private void DrawInfoPanel(RenderContext context, GexClassicData data, Rectangle chartRect)
    {
        if (_headerFont == null || _valueFont == null || _infoPanelFont == null)
            return;

        int panelH = _infoPanelHeight;
        var panelRect = new Rectangle(chartRect.Left, chartRect.Top, chartRect.Width, panelH);
        context.FillRectangle(_infoPanelBg, panelRect);

        bool useVolume = _gexType == GexType.Volume;
        var apiTicker = GetApiTicker(_selectedTicker);
        string gexLabel = useVolume ? "volume" : "open interest";

        int pad = 10;
        int row = 14;
        int colCount = 4;
        int colWidth = (chartRect.Width - pad * 2) / colCount;

        int col0 = chartRect.Left + pad;
        int col1 = col0 + colWidth;
        int col2 = col1 + colWidth;
        int col3 = col2 + colWidth;

        int yStart = chartRect.Top + 6;

        // ── Column 0: Identity ──
        int cy = yStart;
        DrawInfoText(context, col0, ref cy, "GexBotA Classic", _infoPanelHeader, _headerFont, row);
        DrawInfoText(context, col0, ref cy, $"{apiTicker}  |  {_aggregationPeriod}", _infoPanelText, _infoPanelFont, row);
        DrawInfoText(context, col0, ref cy, $"DTE: {data.MinDte} / {data.SecMinDte}", _infoPanelText, _infoPanelFont, row);
        DrawInfoText(context, col0, ref cy, $"Mode: {gexLabel}", Color.FromArgb(255, 100, 180, 255), _infoPanelFont, row);
        if (_conversionMode != ConversionMode.None)
        {
            string convLabel = _conversionMode == ConversionMode.Auto ? "Auto ×" : "Manual ×";
            DrawInfoText(context, col0, ref cy, $"{convLabel}{GetActiveConversionFactor():F4}", Color.FromArgb(255, 255, 220, 100), _infoPanelFont, row);
        }

        // ── Column 1: Update info ──
        cy = yStart;
        DrawInfoText(context, col1, ref cy, "update", _infoPanelHeader, _headerFont, row);
        DrawInfoKv(context, col1, colWidth, ref cy, "date", $"{data.UpdateTime:d}", _infoPanelText, row);
        DrawInfoKv(context, col1, colWidth, ref cy, "time", $"{data.UpdateTime:T}", _infoPanelText, row);
        DrawInfoKv(context, col1, colWidth, ref cy, "spot", $"{ApplyMultiplier(data.Spot):F2}", _spotLineColor, row);

        // ── Column 2: Levels ──
        cy = yStart;
        DrawInfoText(context, col2, ref cy, gexLabel, _infoPanelHeader, _headerFont, row);

        double majorPos = useVolume ? data.MajorPosVol : data.MajorPosOi;
        double majorNeg = useVolume ? data.MajorNegVol : data.MajorNegOi;
        double netGex = useVolume ? data.SumGexVol : data.SumGexOi;

        DrawInfoKv(context, col2, colWidth, ref cy, "zero gamma", $"{ApplyMultiplier(data.ZeroGamma):F2}", _zeroLineColor, row);
        DrawInfoKv(context, col2, colWidth, ref cy, "major positive", $"{ApplyMultiplier(majorPos):F2}", _majorPosColor, row);
        DrawInfoKv(context, col2, colWidth, ref cy, "major negative", $"{ApplyMultiplier(majorNeg):F2}", _majorNegColor, row);
        DrawInfoKv(context, col2, colWidth, ref cy, "net gex", FormatGexValue(netGex), _infoPanelText, row);

        // ── Column 3: Max Change GEX ──
        cy = yStart;
        DrawInfoText(context, col3, ref cy, "max change gex", _infoPanelHeader, _headerFont, row);

        for (int i = 0; i < data.MaxPriors.Count && i < MaxChangeLabels.Length; i++)
        {
            var entry = data.MaxPriors[i];
            var entryColor = entry.GexChange >= 0 ? _majorPosColor : _majorNegColor;
            string line = $"{MaxChangeLabels[i],-5} {ApplyMultiplier(entry.Strike),8:F0}  {FormatGexValue(entry.GexChange)}";
            DrawInfoText(context, col3, ref cy, line, entryColor, _infoPanelFont, row);
        }
    }

    private void DrawInfoText(RenderContext context, int x, ref int y, string text, Color color, RenderFont font, int rowH)
    {
        context.DrawString(text, font, color, x, y);
        y += rowH;
    }

    private void DrawInfoKv(RenderContext context, int x, int colW, ref int y, string label, string val, Color valColor, int rowH)
    {
        if (_infoPanelFont == null) return;
        int halfCol = colW / 2;
        context.DrawString(label, _infoPanelFont, _infoPanelHeader, x, y);

        var rightFmt = new RenderStringFormat { Alignment = StringAlignment.Far };
        var valRect = new Rectangle(x, y, colW - 20, rowH);
        context.DrawString(val, _infoPanelFont, valColor, valRect, rightFmt);
        y += rowH;
    }

    #endregion

    #region Rendering - Alert Overlay

    /// <summary>
    /// Draws a floating alert message box at the top-right of the chart.
    /// Auto-fades after AlertDisplaySeconds.
    /// </summary>
    private void DrawAlertOverlay(RenderContext context, Rectangle chartRect)
    {
        if (string.IsNullOrEmpty(_activeAlertMessage))
            return;

        if (DateTime.UtcNow > _alertMessageExpiry)
        {
            _activeAlertMessage = string.Empty;
            return;
        }

        if (_headerFont == null || _infoPanelFont == null)
            return;

        // Calculate fade: full opacity for first 20s, then fade out
        double remaining = (_alertMessageExpiry - DateTime.UtcNow).TotalSeconds;
        int alpha = remaining > 10 ? 230 : (int)(230 * remaining / 10);
        alpha = Math.Clamp(alpha, 0, 230);

        // Background
        int maxW = Math.Min(550, chartRect.Width - 20);
        var sz = context.MeasureString(_activeAlertMessage, _infoPanelFont);
        int boxW = Math.Min(sz.Width + 24, maxW);
        int lines = (int)Math.Ceiling((double)sz.Width / (maxW - 24)) + 1;
        int boxH = 18 + lines * 14;

        int bx = chartRect.Right - boxW - 10;
        int by = chartRect.Top + (_showInfoPanel ? _infoPanelHeight + 8 : 8);

        var bgColor = Color.FromArgb(alpha, 40, 10, 10);
        var borderColor = Color.FromArgb(alpha, _activeAlertColor.R, _activeAlertColor.G, _activeAlertColor.B);

        var boxRect = new Rectangle(bx, by, boxW, boxH);
        context.FillRectangle(bgColor, boxRect);

        // Border (top accent line)
        context.FillRectangle(borderColor, new Rectangle(bx, by, boxW, 3));

        // Title
        var titleColor = Color.FromArgb(alpha, _activeAlertColor.R, _activeAlertColor.G, _activeAlertColor.B);
        context.DrawString("\u26a0 GexBotA Alert", _headerFont, titleColor, bx + 8, by + 5);

        // Message (word-wrapped via rectangle)
        var msgColor = Color.FromArgb(alpha, 255, 255, 255);
        var msgRect = new Rectangle(bx + 8, by + 20, boxW - 16, boxH - 24);
        context.DrawString(_activeAlertMessage, _infoPanelFont, msgColor, msgRect, new RenderStringFormat());
    }

    #endregion

    #region Rendering - Status

    private void DrawStatusMessage(RenderContext context, Rectangle chartRect)
    {
        if (_headerFont == null) return;

        string msg;
        if (string.IsNullOrWhiteSpace(_apiKey))
            msg = "GexBotA Classic — Enter your API Key in settings";
        else if (!string.IsNullOrEmpty(_lastError))
            msg = $"GexBotA Classic — Error: {_lastError}";
        else
            msg = "GexBotA Classic — Loading data...";

        var sz = context.MeasureString(msg, _headerFont);
        int x = chartRect.Left + (chartRect.Width - sz.Width) / 2;
        int y = chartRect.Top + 30;

        var bgRect = new Rectangle(x - 10, y - 4, sz.Width + 20, sz.Height + 8);
        context.FillRectangle(Color.FromArgb(210, 15, 15, 25), bgRect);
        context.DrawString(msg, _headerFont, Color.White, bgRect, new RenderStringFormat());
    }

    #endregion

    #region Data Fetching

    /// <summary>
    /// Signals the background loop to fetch immediately (e.g. ticker/agg change).
    /// </summary>
    private void TriggerFetch()
    {
        _fetchSignal.Set();
    }

    /// <summary>
    /// Long-running async loop that polls the API. Uses Task.Delay between iterations
    /// and ManualResetEventSlim for instant re-trigger on config changes.
    /// Never blocks the UI/OnCalculate/OnRender thread.
    /// </summary>
    private async Task BackgroundFetchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for the signal or timeout (refresh interval)
                // Signal is set on startup, config changes, and after each interval
                _fetchSignal.Wait(ct);
                _fetchSignal.Reset();

                if (string.IsNullOrWhiteSpace(_apiKey))
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }

                var apiTicker = GetApiTicker(_selectedTicker);
                string agg = _aggregationPeriod switch
                {
                    AggregationPeriod.Full => "full",
                    AggregationPeriod.Latest => "zero",
                    AggregationPeriod.Next => "one",
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
                            _lastError = string.Empty;
                        }
                    }

                    if (changed)
                    {
                        RecalcAutoFactor();
                        RedrawChart(new RedrawArg(ChartArea));
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                lock (_dataLock) { _lastError = ex.Message; }
                RedrawChart(new RedrawArg(ChartArea));
            }

            // Delay until next fetch; WaitHandle also wakes us on signal
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

    #region Alerts

    private void ProcessAlerts(GexClassicData data)
    {
        if (data.Spot == 0) return;

        bool useVolume = _gexType == GexType.Volume;
        decimal spot = ApplyMultiplier(data.Spot);
        decimal majorPos = ApplyMultiplier(useVolume ? data.MajorPosVol : data.MajorPosOi);
        decimal majorNeg = ApplyMultiplier(useVolume ? data.MajorNegVol : data.MajorNegOi);
        decimal tolerance = (InstrumentInfo != null ? InstrumentInfo.TickSize : 0.01m) * 5;

        // ── Spot touching Major Positive ──
        if (Math.Abs(spot - majorPos) <= tolerance)
        {
            if (!_alertedMajorPos || Math.Abs((double)spot - _lastAlertSpot) > (double)tolerance)
            {
                _alertedMajorPos = true;
                _lastAlertSpot = (double)spot;
                AddAlert("alert", $"GexBotA: Spot ({spot:F2}) touching Major+ ({majorPos:F2})");
            }
        }
        else _alertedMajorPos = false;

        // ── Spot touching Major Negative ──
        if (Math.Abs(spot - majorNeg) <= tolerance)
        {
            if (!_alertedMajorNeg || Math.Abs((double)spot - _lastAlertSpot) > (double)tolerance)
            {
                _alertedMajorNeg = true;
                _lastAlertSpot = (double)spot;
                AddAlert("alert", $"GexBotA: Spot ({spot:F2}) touching Major\u2212 ({majorNeg:F2})");
            }
        }
        else _alertedMajorNeg = false;

        // ── Alert 1: Bullish Exhaustion ──
        if (_enableBullishExhaustion)
            CheckBullishExhaustionAlert(data);

        // ── Alert 2: Magnet Effect (Efecto Imán) ──
        if (_enableMagnetAlert)
            CheckMagnetAlert(data);

        // ── Alert 3: Institutional Liquidation (Gamma Drop) ──
        if (_enableLiquidationAlert)
            CheckLiquidationAlert(data);

        // ── Alert 4: Negative Gamma Wall (Hard Support) ──
        if (_enableNegativeWallAlert)
            CheckNegativeWallAlert(data);
    }

    /// <summary>
    /// Alert 1 — Bullish Exhaustion (Agotamiento Alcista).
    /// Detects when price crosses above Major Positive Gamma but there is
    /// insufficient Gamma above to sustain the rally (liquidity void).
    ///
    /// Algorithm:
    ///   1. Find the Major Positive strike and its GEX value in the strikes array.
    ///   2. Check if Spot is above Major Positive strike (price crossed above).
    ///   3. Take the next N strikes above Spot and sum their GEX.
    ///   4. If sum &lt; threshold% of Major Positive GEX → fire exhaustion alert.
    ///   5. Anti-spam cooldown prevents re-firing within N minutes.
    /// </summary>
    private void CheckBullishExhaustionAlert(GexClassicData data)
    {
        // Cooldown check first (cheapest gate)
        if ((DateTime.UtcNow - _lastBullishExhaustionTime).TotalMinutes < _exhaustionCooldownMin)
            return;

        if (data.Strikes.Count < 3)
            return;

        bool useVolume = _gexType == GexType.Volume;

        // 1. Identify Major Positive strike price
        double majorPosStrike = useVolume ? data.MajorPosVol : data.MajorPosOi;
        if (majorPosStrike <= 0)
            return;

        // Find the GEX value at the Major Positive strike
        double majorPosGex = data.Strikes
            .Where(s => Math.Abs(s.Strike - majorPosStrike) < 0.01)
            .Select(s => useVolume ? s.GexByVolume : s.GexByOi)
            .FirstOrDefault();

        // Fallback: if exact strike not found, use the highest positive GEX
        if (majorPosGex <= 0)
        {
            var maxPositive = data.Strikes
                .Select(s => (s.Strike, Gex: useVolume ? s.GexByVolume : s.GexByOi))
                .Where(x => x.Gex > 0)
                .OrderByDescending(x => x.Gex)
                .FirstOrDefault();

            if (maxPositive.Gex <= 0)
                return;

            majorPosStrike = maxPositive.Strike;
            majorPosGex = maxPositive.Gex;
        }

        // 2. Check if Spot has crossed above the Major Positive
        if (data.Spot <= majorPosStrike)
            return;

        // 3. Scan the next N strikes immediately above the Spot price
        var strikesAboveSpot = data.Strikes
            .Where(s => s.Strike > data.Spot)
            .OrderBy(s => s.Strike)
            .Take(_exhaustionScanStrikes)
            .ToList();

        // If there are zero strikes above, that itself is total exhaustion

        // 4. Sum the GEX of those strikes
        double gammaAboveSum = strikesAboveSpot
            .Sum(s => useVolume ? s.GexByVolume : s.GexByOi);

        // 5. Exhaustion condition: sum < threshold% of Major Positive GEX
        double threshold = majorPosGex * (_exhaustionThresholdPct / 100.0);

        if (gammaAboveSum < threshold)
        {
            _lastBullishExhaustionTime = DateTime.UtcNow;

            string gammaAboveStr = FormatGexValue(gammaAboveSum);
            string majorPosStr = FormatGexValue(majorPosGex);
            double pct = majorPosGex > 0 ? (gammaAboveSum / majorPosGex * 100) : 0;
            int scanned = strikesAboveSpot.Count;

            string msg = $"\u26a0\ufe0f BULLISH EXHAUSTION: Spot ({data.Spot:F2}) above Major+ ({majorPosStrike:F0}). " +
                         $"Gamma above: {gammaAboveStr} ({pct:F1}% of Major+ {majorPosStr}, {scanned} strikes scanned). " +
                         $"Likely reversal (Fade).";

            // Fire ATAS alert (sound + alert window)
            AddAlert("alert", msg);

            // Set visual overlay on chart
            ShowAlertOverlay(msg, Color.FromArgb(255, 255, 80, 80));
        }
    }

    /// <summary>
    /// Alert 2 — Magnet Effect / Efecto Imán (Continuación Alcista).
    /// Detects when price breaks above Zero Gamma and a concentrated block of
    /// positive Gamma above acts as a magnet attracting the price upward.
    ///
    /// Algorithm:
    ///   1. Edge detection: Spot just crossed Zero Gamma upward (was below, now above).
    ///   2. Scan all strikes above Spot and sum their positive GEX.
    ///   3. Find the single strike with the highest GEX (the Target / magnet).
    ///   4. If Target GEX > concentration% of total GEX above → massive concentrated magnet.
    ///   5. Fire alert with the target strike.
    ///   6. Anti-spam: cooldown OR price falls back below Zero Gamma to re-arm.
    /// </summary>
    private void CheckMagnetAlert(GexClassicData data)
    {
        bool useVolume = _gexType == GexType.Volume;
        double zeroGamma = data.ZeroGamma;

        if (zeroGamma <= 0)
            return;

        bool isAboveZeroGamma = data.Spot > zeroGamma;

        // If price fell back below Zero Gamma, reset the crossing state (re-arm)
        if (!isAboveZeroGamma)
        {
            _wasAboveZeroGamma = false;
            return;
        }

        // 1. Edge detection: only fire on the transition from below to above
        bool justCrossedUp = isAboveZeroGamma && !_wasAboveZeroGamma;
        _wasAboveZeroGamma = true;

        // Cooldown gate (skip if not a fresh crossing AND cooldown hasn't expired)
        if (!justCrossedUp &&
            (DateTime.UtcNow - _lastMagnetAlertTime).TotalMinutes < _magnetCooldownMin)
            return;

        // Even on a fresh crossing, respect the cooldown
        if ((DateTime.UtcNow - _lastMagnetAlertTime).TotalMinutes < _magnetCooldownMin)
            return;

        if (data.Strikes.Count < 3)
            return;

        // 2. Scan all strikes above Spot with positive (Call) Gamma
        var strikesAbove = data.Strikes
            .Where(s => s.Strike > data.Spot)
            .Select(s => (s.Strike, Gex: useVolume ? s.GexByVolume : s.GexByOi))
            .Where(x => x.Gex > 0) // only positive Gamma acts as magnet
            .OrderByDescending(x => x.Gex)
            .ToList();

        if (strikesAbove.Count == 0)
            return;

        // 3. Total positive Gamma above and the Target (highest single strike)
        double totalGammaAbove = strikesAbove.Sum(x => x.Gex);
        var target = strikesAbove[0]; // already sorted descending

        if (totalGammaAbove <= 0)
            return;

        // 4. Concentration check: Target must represent > threshold% of total above
        double concentrationPct = target.Gex / totalGammaAbove * 100.0;

        if (concentrationPct < _magnetConcentrationPct)
            return;

        // 5. Fire the alert!
        _lastMagnetAlertTime = DateTime.UtcNow;

        string targetGexStr = FormatGexValue(target.Gex);
        string totalStr = FormatGexValue(totalGammaAbove);

        string msg = $"\ud83d\ude80 V\u00cdA LIBRE: Quiebre del Zero Gamma ({zeroGamma:F0}) confirmado. " +
                     $"Fuerte Gamma Positiva actuando como im\u00e1n en Strike {target.Strike:F0} " +
                     $"({targetGexStr}, {concentrationPct:F0}% de {totalStr} total arriba). " +
                     $"Continuaci\u00f3n alcista probable.";

        AddAlert("alert", msg);
        ShowAlertOverlay(msg, Color.FromArgb(255, 80, 200, 120));
    }

    /// <summary>
    /// Alert 3 — Institutional Liquidation (Gamma Drop).
    /// Detects when the Gamma at the Major Positive strike has dropped drastically
    /// compared to 15 minutes ago while the price is near that level.
    /// This signals that institutional players are closing their option positions,
    /// weakening the "wall" and making a reversal highly likely.
    ///
    /// Uses the existing Priors array from the API:
    ///   Priors[3] = GEX value 15 minutes ago for each strike.
    ///
    /// Algorithm:
    ///   1. Identify the Major Positive strike and its current GEX value.
    ///   2. Check if Spot is within N ticks of that strike (proximity).
    ///   3. Read the 15-min prior GEX from Priors[3] for that strike.
    ///   4. If current GEX &lt; (100 - dropPct)% of the 15-min value → liquidation detected.
    ///   5. Anti-spam: 15 min cooldown (structural alert).
    /// </summary>
    private void CheckLiquidationAlert(GexClassicData data)
    {
        // Cooldown check first
        if ((DateTime.UtcNow - _lastLiquidationAlertTime).TotalMinutes < _liquidationCooldownMin)
            return;

        if (data.Strikes.Count < 3)
            return;

        bool useVolume = _gexType == GexType.Volume;

        // 1. Identify Major Positive strike
        double majorPosStrike = useVolume ? data.MajorPosVol : data.MajorPosOi;
        if (majorPosStrike <= 0)
            return;

        // Find the StrikeData at the Major Positive
        var majorStrikeData = data.Strikes
            .FirstOrDefault(s => Math.Abs(s.Strike - majorPosStrike) < 0.01);

        if (majorStrikeData == null)
            return;

        double currentGex = useVolume ? majorStrikeData.GexByVolume : majorStrikeData.GexByOi;

        // Must have positive Gamma currently to be a valid wall
        if (currentGex <= 0)
            return;

        // 2. Proximity check: Spot must be near the Major Positive
        decimal tickSize = InstrumentInfo != null ? InstrumentInfo.TickSize : 0.01m;
        double proximityDistance = (double)(tickSize * _liquidationProximityTicks);

        // Apply conversion factor to Major Positive for chart comparison
        double majorPosConverted = (double)ApplyMultiplier(majorPosStrike);
        double spotConverted = (double)ApplyMultiplier(data.Spot);

        if (Math.Abs(spotConverted - majorPosConverted) > proximityDistance)
            return;

        // 3. Read the 15-min prior GEX (Priors[3] = 15 min lookback)
        const int Prior15MinIndex = 3;
        if (majorStrikeData.Priors.Count <= Prior15MinIndex)
            return;

        double gex15MinAgo = majorStrikeData.Priors[Prior15MinIndex];

        // Need valid positive historical data to compare against
        if (gex15MinAgo <= 0)
            return;

        // 4. Liquidation condition: current GEX dropped > dropPct% from 15 min ago
        double retainedPct = currentGex / gex15MinAgo * 100.0;
        double dropPct = 100.0 - retainedPct;

        if (dropPct < _liquidationDropPct)
            return;

        // 5. Fire the alert!
        _lastLiquidationAlertTime = DateTime.UtcNow;

        string currentStr = FormatGexValue(currentGex);
        string priorStr = FormatGexValue(gex15MinAgo);

        string msg = $"\ud83d\udcb8 LIQUIDACI\u00d3N INSTITUCIONAL: La Gamma en Major+ ({majorPosStrike:F0}) " +
                     $"ha ca\u00eddo un {dropPct:F0}% en 15 min ({priorStr} \u2192 {currentStr}). " +
                     $"Muro debilit\u00e1ndose, alta probabilidad de reversi\u00f3n.";

        AddAlert("alert", msg);
        ShowAlertOverlay(msg, Color.FromArgb(255, 255, 180, 50));
    }

    /// <summary>
    /// Alert 4 — Negative Gamma Wall / Muro de Soporte Duro.
    /// Detects when the price is falling towards the Major Negative Gamma and
    /// there is virtually no Gamma below that level (total vacuum), making it
    /// the last line of defense for market makers → extreme bounce probability.
    ///
    /// Algorithm:
    ///   1. Identify the Major Negative strike and its GEX value (most negative).
    ///   2. Check if Spot is within N ticks above Major Negative (approaching from above).
    ///   3. Scan the next N strikes below Major Negative.
    ///   4. Sum their absolute GEX. If it is &lt; vacuumPct% of |Major Negative GEX| → vacuum.
    ///   5. Fire alert with tight stop-loss implication.
    /// </summary>
    private void CheckNegativeWallAlert(GexClassicData data)
    {
        // Cooldown check
        if ((DateTime.UtcNow - _lastNegativeWallAlertTime).TotalMinutes < _negWallCooldownMin)
            return;

        if (data.Strikes.Count < 3)
            return;

        bool useVolume = _gexType == GexType.Volume;

        // 1. Identify Major Negative strike
        double majorNegStrike = useVolume ? data.MajorNegVol : data.MajorNegOi;
        if (majorNegStrike <= 0)
            return;

        // Find the StrikeData at the Major Negative and get its GEX value
        var majorNegData = data.Strikes
            .FirstOrDefault(s => Math.Abs(s.Strike - majorNegStrike) < 0.01);

        double majorNegGex;
        if (majorNegData != null)
        {
            majorNegGex = useVolume ? majorNegData.GexByVolume : majorNegData.GexByOi;
        }
        else
        {
            // Fallback: find the most negative GEX strike
            var mostNeg = data.Strikes
                .Select(s => (s.Strike, Gex: useVolume ? s.GexByVolume : s.GexByOi))
                .Where(x => x.Gex < 0)
                .OrderBy(x => x.Gex)
                .FirstOrDefault();

            if (mostNeg.Gex >= 0)
                return;

            majorNegStrike = mostNeg.Strike;
            majorNegGex = mostNeg.Gex;
        }

        // Must be negative Gamma for a valid put wall
        if (majorNegGex >= 0)
            return;

        double absMajorNegGex = Math.Abs(majorNegGex);

        // 2. Proximity check: Spot must be approaching from above
        //    (Spot is above Major Negative but within N ticks)
        decimal tickSize = InstrumentInfo != null ? InstrumentInfo.TickSize : 0.01m;
        double proximityDistance = (double)(tickSize * _negWallProximityTicks);

        double majorNegConverted = (double)ApplyMultiplier(majorNegStrike);
        double spotConverted = (double)ApplyMultiplier(data.Spot);

        double distanceAbove = spotConverted - majorNegConverted;

        // Spot must be above Major Negative (approaching) and within proximity
        if (distanceAbove < 0 || distanceAbove > proximityDistance)
            return;

        // 3. Scan the next N strikes BELOW the Major Negative
        var strikesBelow = data.Strikes
            .Where(s => s.Strike < majorNegStrike)
            .OrderByDescending(s => s.Strike)
            .Take(_negWallScanStrikes)
            .ToList();

        // If there are zero strikes below, that itself is total vacuum

        // 4. Sum the absolute GEX of those strikes below
        double absGammaBelow = strikesBelow
            .Sum(s => Math.Abs(useVolume ? s.GexByVolume : s.GexByOi));

        // Vacuum condition: absolute sum below < vacuumPct% of |Major Negative GEX|
        double threshold = absMajorNegGex * (_negWallVacuumPct / 100.0);

        if (absGammaBelow >= threshold)
            return;

        // 5. Fire the alert!
        _lastNegativeWallAlertTime = DateTime.UtcNow;

        string majorNegStr = FormatGexValue(majorNegGex);
        string belowStr = FormatGexValue(absGammaBelow);
        double vacuumPct = absMajorNegGex > 0 ? (absGammaBelow / absMajorNegGex * 100) : 0;
        int scanned = strikesBelow.Count;

        string msg = $"\ud83e\uddf1 MURO NEGATIVO: Precio en zona de Major\u2212 ({majorNegStrike:F0}, {majorNegStr}). " +
                     $"Vac\u00edo debajo: {belowStr} ({vacuumPct:F1}% de |Major\u2212|, {scanned} strikes). " +
                     $"Probabilidad extrema de soporte / rebote al tick.";

        AddAlert("alert", msg);
        ShowAlertOverlay(msg, Color.FromArgb(255, 100, 150, 255));
    }

    /// <summary>
    /// Shows a floating alert message on the chart for AlertDisplaySeconds.
    /// </summary>
    private void ShowAlertOverlay(string message, Color color)
    {
        _activeAlertMessage = message;
        _activeAlertColor = color;
        _alertMessageExpiry = DateTime.UtcNow.AddSeconds(AlertDisplaySeconds);
    }

    #endregion

    #region Helpers - Coordinates & Scaling

    /// <summary>
    /// Recalculates the auto conversion factor from chart price vs API spot.
    /// Called from OnCalculate (every bar), OnRender (fallback), and FetchDataAsync (on new data).
    /// </summary>
    private void RecalcAutoFactor()
    {
        if (_conversionMode != ConversionMode.Auto)
            return;

        if (_lastChartPrice <= 0)
            return;

        GexClassicData? data;
        lock (_dataLock) { data = _gexData; }

        if (data != null && data.Spot > 0)
            _autoFactor = (double)_lastChartPrice / data.Spot;
    }

    private int PriceToY(decimal price)
    {
        return ChartInfo.PriceChartContainer.GetYByPrice(price, true);
    }

    private decimal ApplyMultiplier(double price)
    {
        double factor = _conversionMode switch
        {
            ConversionMode.Auto => _autoFactor,
            ConversionMode.Manual => _manualFactor,
            _ => 1.0
        };
        return (decimal)(price * factor);
    }

    /// <summary>Returns the currently active conversion factor for display.</summary>
    private double GetActiveConversionFactor()
    {
        return _conversionMode switch
        {
            ConversionMode.Auto => _autoFactor,
            ConversionMode.Manual => _manualFactor,
            _ => 1.0
        };
    }

    private int ScaleGex(double absGex, double maxAbsGex, int maxPixels)
    {
        if (maxAbsGex < 0.0001) return 0;
        if (_logarithmicScaling && absGex > 0)
            return (int)(Math.Log(1 + absGex) / Math.Log(1 + maxAbsGex) * maxPixels);
        return (int)(absGex / maxAbsGex * maxPixels);
    }

    private int ScaleGexSigned(double gex, double maxAbsGex, int maxPixels)
    {
        int mag = ScaleGex(Math.Abs(gex), maxAbsGex, maxPixels);
        return gex >= 0 ? mag : -mag;
    }

    private Rectangle GetHistogramRect(Rectangle fullRect)
    {
        double fraction = _panelSize switch
        {
            PanelSizeOption.Eighth => 0.125,
            PanelSizeOption.Quarter => 0.25,
            PanelSizeOption.Third => 1.0 / 3.0,
            PanelSizeOption.Half => 0.5,
            _ => 0.125
        };

        int panelW = (int)(fullRect.Width * fraction);

        return _panelLocation == PanelLocation.Right
            ? new Rectangle(fullRect.Right - panelW, fullRect.Top, panelW, fullRect.Height)
            : new Rectangle(fullRect.Left, fullRect.Top, panelW, fullRect.Height);
    }

    #endregion

    #region Helpers - Ticker Config

    private static string GetApiTicker(GexTicker ticker)
    {
        return ticker switch
        {
            GexTicker.SPX_ES => "ES_SPX",
            GexTicker.NDX_NQ => "NQ_NDX",
            _ => ticker.ToString()
        };
    }

    #endregion

    #region Helpers - Resource Management

    private void RebuildPens()
    {
        _zeroGammaPen = new RenderPen(_zeroLineColor, _zeroLineWidth, _zeroLineStyle);
        _majorPosPen = new RenderPen(_majorPosColor, _majorLineWidth, _majorLineStyle);
        _majorNegPen = new RenderPen(_majorNegColor, _majorLineWidth, _majorLineStyle);
        _spotPen = new RenderPen(_spotLineColor, 1.5f);
        _centerLinePen = new RenderPen(_centerLineColor, _centerLineWidth, _centerLineStyle);
    }

    private void RebuildFonts()
    {
        _labelFont = new RenderFont("Segoe UI", 8.5f);
        _headerFont = new RenderFont("Segoe UI", 9.5f, FontStyle.Bold);
        _valueFont = new RenderFont("Segoe UI", 9f);
        _infoPanelFont = new RenderFont("Consolas", 9f);
        _strikeLabelFont = new RenderFont("Segoe UI", _strikeLabelFontSize);
        _gammaCallLabelFont = new RenderFont("Segoe UI", _gammaCallLabelFontSize);
        _gammaPutLabelFont = new RenderFont("Segoe UI", _gammaPutLabelFontSize);
    }

    #endregion

    #region Helpers - Formatting

    private static string FormatGexValue(double value)
    {
        double abs = Math.Abs(value);
        string sign = value < 0 ? "-" : "";

        if (abs >= 1_000_000_000) return $"{sign}{abs / 1_000_000_000:F2}B";
        if (abs >= 1_000_000) return $"{sign}{abs / 1_000_000:F2}MM";
        if (abs >= 1_000) return $"{sign}{abs / 1_000:F2}K";
        return $"{sign}{abs:F2}";
    }

    #endregion
}
