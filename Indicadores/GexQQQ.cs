using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace ATAS.Indicators.Technical
{
    [DisplayName("GexBot Classic Profile")]
    public class GexBotClassicProfile : Indicator
    {
        #region Helper Methods
        private void RequestRecalc([System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
        {
            RaisePropertyChanged(propertyName);
            RecalculateValues();
            RedrawChart();
        }
        #endregion

        #region Data Classes
        private class GexStrike
        {
            public decimal Strike { get; set; }
            public decimal GexByVolume { get; set; }
            public decimal GexByOI { get; set; }
            public decimal[] Priors { get; set; } = Array.Empty<decimal>();
        }

        private class GexClassicData
        {
            public long Timestamp { get; set; }
            public string Ticker { get; set; } = string.Empty;
            public int MinDte { get; set; }
            public int SecMinDte { get; set; }
            public decimal Spot { get; set; }
            public decimal ZeroGamma { get; set; }
            public decimal MajorPosVol { get; set; }
            public decimal MajorPosOI { get; set; }
            public decimal MajorNegVol { get; set; }
            public decimal MajorNegOI { get; set; }
            public List<GexStrike> Strikes { get; set; } = new();
            public decimal SumGexVol { get; set; }
            public decimal SumGexOI { get; set; }
        }
        #endregion

        #region Fields
        private readonly object _sync = new();
        private readonly HttpClient _httpClient = new();
        private readonly System.Timers.Timer _refreshTimer = new(60000);
        private GexClassicData? _data;
        private string _error = string.Empty;
        private DateTime? _lastLoad;
        private decimal _lastChartPrice;
        #endregion

        #region API Settings
        private string _apiKey = string.Empty;
        [Display(GroupName = "1. API Settings", Name = "API Key", Order = 10)]
        public string ApiKey
        {
            get => _apiKey;
            set { _apiKey = value ?? string.Empty; ForceReload(); }
        }

        public enum TickerType { QQQ, SPY, SPX, NDX, NQ_NDX, ES_SPX, AAPL, NVDA, TSLA, AMD, AMZN, META, MSFT, GOOGL }
        private TickerType _ticker = TickerType.QQQ;
        [Display(GroupName = "1. API Settings", Name = "Ticker", Order = 20)]
        public TickerType Ticker
        {
            get => _ticker;
            set { _ticker = value; ForceReload(); }
        }

        public enum AggregationPeriod { full, zero, one }
        private AggregationPeriod _aggregation = AggregationPeriod.zero;
        [Display(GroupName = "1. API Settings", Name = "Aggregation (DTE)", Order = 30)]
        public AggregationPeriod Aggregation
        {
            get => _aggregation;
            set { _aggregation = value; ForceReload(); }
        }

        private int _refreshSeconds = 60;
        [Display(GroupName = "1. API Settings", Name = "Refresh (sec)", Order = 40)]
        [Range(1, 3600)]
        public int RefreshSeconds
        {
            get => _refreshSeconds;
            set { _refreshSeconds = Math.Max(1, value); ResetTimer(); }
        }

        public enum GexDataSource { Volume, OpenInterest }
        private GexDataSource _dataSource = GexDataSource.Volume;
        [Display(GroupName = "1. API Settings", Name = "GEX Data Source", Order = 50)]
        public GexDataSource DataSource
        {
            get => _dataSource;
            set { _dataSource = value; RequestRecalc(); }
        }
        #endregion

        #region Conversion Settings
        private bool _enableConversion = true;
        [Display(GroupName = "2. Conversion", Name = "Enable price conversion", Order = 10)]
        public bool EnableConversion
        {
            get => _enableConversion;
            set { _enableConversion = value; RequestRecalc(); }
        }

        private decimal _conversionFactor = 1.0m;
        [Display(GroupName = "2. Conversion", Name = "Conversion factor (chart/spot)", Order = 20)]
        public decimal ConversionFactor
        {
            get => _conversionFactor;
            set { _conversionFactor = value <= 0 ? 1.0m : value; RequestRecalc(); }
        }

        private bool _autoCalculateFactor = true;
        [Display(GroupName = "2. Conversion", Name = "Auto-calculate factor from chart", Order = 30)]
        public bool AutoCalculateFactor
        {
            get => _autoCalculateFactor;
            set { _autoCalculateFactor = value; RequestRecalc(); }
        }

        private decimal _priceStep = 0.25m;
        [Display(GroupName = "2. Conversion", Name = "Price step (rounding)", Order = 40)]
        public decimal PriceStep
        {
            get => _priceStep;
            set { _priceStep = value <= 0 ? 0.25m : value; RequestRecalc(); }
        }
        #endregion

        #region Profile Position
        private int _centerOffsetPx = 0;
        [Display(GroupName = "3. Position", Name = "Center offset (px)", Order = 10)]
        [Range(-5000, 5000)]
        public int CenterOffsetPx
        {
            get => _centerOffsetPx;
            set { _centerOffsetPx = Math.Clamp(value, -5000, 5000); RequestRecalc(); }
        }

        private bool _showCenterLine = true;
        [Display(GroupName = "3. Position", Name = "Show center line", Order = 20)]
        public bool ShowCenterLine
        {
            get => _showCenterLine;
            set { _showCenterLine = value; RequestRecalc(); }
        }

        private Color _centerLineColor = Color.FromArgb(140, Color.Yellow);
        [Display(GroupName = "3. Position", Name = "Center line color", Order = 30)]
        public Color CenterLineColor
        {
            get => _centerLineColor;
            set { _centerLineColor = value; RequestRecalc(); }
        }

        private int _centerLineThickness = 1;
        [Display(GroupName = "3. Position", Name = "Center line thickness", Order = 40)]
        [Range(1, 10)]
        public int CenterLineThickness
        {
            get => _centerLineThickness;
            set { _centerLineThickness = Math.Clamp(value, 1, 10); RequestRecalc(); }
        }
        #endregion

        #region Profile Appearance
        private int _maxBarWidthPx = 300;
        [Display(GroupName = "4. Appearance", Name = "Max bar width (px)", Order = 10)]
        [Range(50, 1500)]
        public int MaxBarWidthPx
        {
            get => _maxBarWidthPx;
            set { _maxBarWidthPx = Math.Clamp(value, 50, 1500); RequestRecalc(); }
        }

        private int _barThicknessPx = 8;
        [Display(GroupName = "4. Appearance", Name = "Bar thickness (px)", Order = 20)]
        [Range(2, 50)]
        public int BarThicknessPx
        {
            get => _barThicknessPx;
            set { _barThicknessPx = Math.Clamp(value, 2, 50); RequestRecalc(); }
        }

        private Color _positiveGexColor = Color.FromArgb(200, 0, 200, 100);
        [Display(GroupName = "4. Appearance", Name = "Positive GEX color", Order = 30)]
        public Color PositiveGexColor
        {
            get => _positiveGexColor;
            set { _positiveGexColor = value; RequestRecalc(); }
        }

        private Color _negativeGexColor = Color.FromArgb(200, 200, 60, 60);
        [Display(GroupName = "4. Appearance", Name = "Negative GEX color", Order = 40)]
        public Color NegativeGexColor
        {
            get => _negativeGexColor;
            set { _negativeGexColor = value; RequestRecalc(); }
        }

        private int _fillOpacity = 180;
        [Display(GroupName = "4. Appearance", Name = "Fill opacity", Order = 50)]
        [Range(0, 255)]
        public int FillOpacity
        {
            get => _fillOpacity;
            set { _fillOpacity = Math.Clamp(value, 0, 255); RequestRecalc(); }
        }

        private bool _showBarOutline = true;
        [Display(GroupName = "4. Appearance", Name = "Show bar outline", Order = 60)]
        public bool ShowBarOutline
        {
            get => _showBarOutline;
            set { _showBarOutline = value; RequestRecalc(); }
        }

        private int _outlineThickness = 1;
        [Display(GroupName = "4. Appearance", Name = "Outline thickness", Order = 70)]
        [Range(1, 5)]
        public int OutlineThickness
        {
            get => _outlineThickness;
            set { _outlineThickness = Math.Clamp(value, 1, 5); RequestRecalc(); }
        }
        #endregion

        #region Value Labels
        private bool _showValues = true;
        [Display(GroupName = "5. Value Labels", Name = "Show GEX values", Order = 10)]
        public bool ShowValues
        {
            get => _showValues;
            set { _showValues = value; RequestRecalc(); }
        }

        private int _valueFontSize = 8;
        [Display(GroupName = "5. Value Labels", Name = "Font size", Order = 20)]
        [Range(6, 20)]
        public int ValueFontSize
        {
            get => _valueFontSize;
            set { _valueFontSize = Math.Clamp(value, 6, 20); RequestRecalc(); }
        }

        private Color _valueTextColor = Color.White;
        [Display(GroupName = "5. Value Labels", Name = "Text color", Order = 30)]
        public Color ValueTextColor
        {
            get => _valueTextColor;
            set { _valueTextColor = value; RequestRecalc(); }
        }

        private int _valueOffsetPx = 4;
        [Display(GroupName = "5. Value Labels", Name = "Value offset (px)", Order = 40)]
        [Range(0, 100)]
        public int ValueOffsetPx
        {
            get => _valueOffsetPx;
            set { _valueOffsetPx = Math.Clamp(value, 0, 100); RequestRecalc(); }
        }

        private bool _showStrikeLabels = true;
        [Display(GroupName = "5. Value Labels", Name = "Show strike labels", Order = 50)]
        public bool ShowStrikeLabels
        {
            get => _showStrikeLabels;
            set { _showStrikeLabels = value; RequestRecalc(); }
        }

        private Color _strikeTextColor = Color.LightGray;
        [Display(GroupName = "5. Value Labels", Name = "Strike text color", Order = 60)]
        public Color StrikeTextColor
        {
            get => _strikeTextColor;
            set { _strikeTextColor = value; RequestRecalc(); }
        }
        #endregion

        #region Major Levels
        private bool _showZeroGammaLine = true;
        [Display(GroupName = "6. Major Levels", Name = "Show Zero Gamma line", Order = 10)]
        public bool ShowZeroGammaLine
        {
            get => _showZeroGammaLine;
            set { _showZeroGammaLine = value; RequestRecalc(); }
        }

        private Color _zeroGammaColor = Color.Yellow;
        [Display(GroupName = "6. Major Levels", Name = "Zero Gamma color", Order = 20)]
        public Color ZeroGammaColor
        {
            get => _zeroGammaColor;
            set { _zeroGammaColor = value; RequestRecalc(); }
        }

        private int _zeroGammaThickness = 2;
        [Display(GroupName = "6. Major Levels", Name = "Zero Gamma thickness", Order = 30)]
        [Range(1, 10)]
        public int ZeroGammaThickness
        {
            get => _zeroGammaThickness;
            set { _zeroGammaThickness = Math.Clamp(value, 1, 10); RequestRecalc(); }
        }

        private DashStyle _zeroGammaDash = DashStyle.Dash;
        [Display(GroupName = "6. Major Levels", Name = "Zero Gamma dash style", Order = 40)]
        public DashStyle ZeroGammaDash
        {
            get => _zeroGammaDash;
            set { _zeroGammaDash = value; RequestRecalc(); }
        }

        private bool _showMajorPosLine = true;
        [Display(GroupName = "6. Major Levels", Name = "Show Major Positive line", Order = 50)]
        public bool ShowMajorPosLine
        {
            get => _showMajorPosLine;
            set { _showMajorPosLine = value; RequestRecalc(); }
        }

        private Color _majorPosColor = Color.LimeGreen;
        [Display(GroupName = "6. Major Levels", Name = "Major Positive color", Order = 60)]
        public Color MajorPosColor
        {
            get => _majorPosColor;
            set { _majorPosColor = value; RequestRecalc(); }
        }

        private bool _showMajorNegLine = true;
        [Display(GroupName = "6. Major Levels", Name = "Show Major Negative line", Order = 70)]
        public bool ShowMajorNegLine
        {
            get => _showMajorNegLine;
            set { _showMajorNegLine = value; RequestRecalc(); }
        }

        private Color _majorNegColor = Color.OrangeRed;
        [Display(GroupName = "6. Major Levels", Name = "Major Negative color", Order = 80)]
        public Color MajorNegColor
        {
            get => _majorNegColor;
            set { _majorNegColor = value; RequestRecalc(); }
        }

        private int _majorLinesThickness = 2;
        [Display(GroupName = "6. Major Levels", Name = "Major lines thickness", Order = 90)]
        [Range(1, 10)]
        public int MajorLinesThickness
        {
            get => _majorLinesThickness;
            set { _majorLinesThickness = Math.Clamp(value, 1, 10); RequestRecalc(); }
        }

        private DashStyle _majorLinesDash = DashStyle.Solid;
        [Display(GroupName = "6. Major Levels", Name = "Major lines dash style", Order = 100)]
        public DashStyle MajorLinesDash
        {
            get => _majorLinesDash;
            set { _majorLinesDash = value; RequestRecalc(); }
        }

        private bool _showMajorLabels = true;
        [Display(GroupName = "6. Major Levels", Name = "Show level labels", Order = 110)]
        public bool ShowMajorLabels
        {
            get => _showMajorLabels;
            set { _showMajorLabels = value; RequestRecalc(); }
        }
        #endregion

        #region Info Panel
        private bool _showInfoPanel = true;
        [Display(GroupName = "7. Info Panel", Name = "Show info panel", Order = 10)]
        public bool ShowInfoPanel
        {
            get => _showInfoPanel;
            set { _showInfoPanel = value; RequestRecalc(); }
        }

        public enum InfoPanelAlign { TopLeft, TopRight, BottomLeft, BottomRight }
        private InfoPanelAlign _infoPanelPosition = InfoPanelAlign.TopRight;
        [Display(GroupName = "7. Info Panel", Name = "Panel position", Order = 15)]
        public InfoPanelAlign InfoPanelPosition
        {
            get => _infoPanelPosition;
            set { _infoPanelPosition = value; RequestRecalc(); }
        }

        private int _infoPanelX = 10;
        [Display(GroupName = "7. Info Panel", Name = "Panel X offset", Order = 20)]
        [Range(0, 5000)]
        public int InfoPanelX
        {
            get => _infoPanelX;
            set { _infoPanelX = Math.Clamp(value, 0, 5000); RequestRecalc(); }
        }

        private int _infoPanelY = 10;
        [Display(GroupName = "7. Info Panel", Name = "Panel Y offset", Order = 30)]
        [Range(0, 5000)]
        public int InfoPanelY
        {
            get => _infoPanelY;
            set { _infoPanelY = Math.Clamp(value, 0, 5000); RequestRecalc(); }
        }

        private int _infoPanelFontSize = 11;
        [Display(GroupName = "7. Info Panel", Name = "Font size", Order = 40)]
        [Range(8, 24)]
        public int InfoPanelFontSize
        {
            get => _infoPanelFontSize;
            set { _infoPanelFontSize = Math.Clamp(value, 8, 24); RequestRecalc(); }
        }

        private Color _infoPanelTextColor = Color.White;
        [Display(GroupName = "7. Info Panel", Name = "Text color", Order = 50)]
        public Color InfoPanelTextColor
        {
            get => _infoPanelTextColor;
            set { _infoPanelTextColor = value; RequestRecalc(); }
        }

        private Color _infoPanelBackColor = Color.FromArgb(220, 20, 20, 25);
        [Display(GroupName = "7. Info Panel", Name = "Background color", Order = 60)]
        public Color InfoPanelBackColor
        {
            get => _infoPanelBackColor;
            set { _infoPanelBackColor = value; RequestRecalc(); }
        }

        private Color _infoPanelBorderColor = Color.FromArgb(180, 60, 60, 70);
        [Display(GroupName = "7. Info Panel", Name = "Border color", Order = 65)]
        public Color InfoPanelBorderColor
        {
            get => _infoPanelBorderColor;
            set { _infoPanelBorderColor = value; RequestRecalc(); }
        }

        private Color _infoPanelHeaderColor = Color.FromArgb(255, 0, 180, 220);
        [Display(GroupName = "7. Info Panel", Name = "Header color", Order = 70)]
        public Color InfoPanelHeaderColor
        {
            get => _infoPanelHeaderColor;
            set { _infoPanelHeaderColor = value; RequestRecalc(); }
        }

        private Color _infoPanelAccentPositive = Color.FromArgb(255, 0, 200, 100);
        [Display(GroupName = "7. Info Panel", Name = "Positive accent color", Order = 75)]
        public Color InfoPanelAccentPositive
        {
            get => _infoPanelAccentPositive;
            set { _infoPanelAccentPositive = value; RequestRecalc(); }
        }

        private Color _infoPanelAccentNegative = Color.FromArgb(255, 220, 80, 80);
        [Display(GroupName = "7. Info Panel", Name = "Negative accent color", Order = 80)]
        public Color InfoPanelAccentNegative
        {
            get => _infoPanelAccentNegative;
            set { _infoPanelAccentNegative = value; RequestRecalc(); }
        }

        private int _infoPanelBarWidth = 120;
        [Display(GroupName = "7. Info Panel", Name = "Bar width (px)", Order = 85)]
        [Range(50, 300)]
        public int InfoPanelBarWidth
        {
            get => _infoPanelBarWidth;
            set { _infoPanelBarWidth = Math.Clamp(value, 50, 300); RequestRecalc(); }
        }

        private bool _showInfoPanelBars = true;
        [Display(GroupName = "7. Info Panel", Name = "Show visual bars", Order = 90)]
        public bool ShowInfoPanelBars
        {
            get => _showInfoPanelBars;
            set { _showInfoPanelBars = value; RequestRecalc(); }
        }
        #endregion

        #region Strike Grid
        private bool _showStrikeGrid = false;
        [Display(GroupName = "8. Strike Grid", Name = "Show strike grid", Order = 10)]
        public bool ShowStrikeGrid
        {
            get => _showStrikeGrid;
            set { _showStrikeGrid = value; RequestRecalc(); }
        }

        private decimal _strikeGridStep = 1m;
        [Display(GroupName = "8. Strike Grid", Name = "Strike step (underlying)", Order = 20)]
        public decimal StrikeGridStep
        {
            get => _strikeGridStep;
            set { _strikeGridStep = value <= 0 ? 1m : value; RequestRecalc(); }
        }

        private decimal _strikeGridStart = 0m;
        [Display(GroupName = "8. Strike Grid", Name = "Start strike (0=auto)", Order = 25)]
        public decimal StrikeGridStart
        {
            get => _strikeGridStart;
            set { _strikeGridStart = Math.Max(0, value); RequestRecalc(); }
        }

        private decimal _strikeGridEnd = 0m;
        [Display(GroupName = "8. Strike Grid", Name = "End strike (0=auto)", Order = 26)]
        public decimal StrikeGridEnd
        {
            get => _strikeGridEnd;
            set { _strikeGridEnd = Math.Max(0, value); RequestRecalc(); }
        }

        private Color _strikeGridColor = Color.FromArgb(40, 150, 150, 150);
        [Display(GroupName = "8. Strike Grid", Name = "Grid color", Order = 30)]
        public Color StrikeGridColor
        {
            get => _strikeGridColor;
            set { _strikeGridColor = value; RequestRecalc(); }
        }

        private int _strikeGridThickness = 1;
        [Display(GroupName = "8. Strike Grid", Name = "Grid thickness", Order = 40)]
        [Range(1, 5)]
        public int StrikeGridThickness
        {
            get => _strikeGridThickness;
            set { _strikeGridThickness = Math.Clamp(value, 1, 5); RequestRecalc(); }
        }

        private DashStyle _strikeGridDash = DashStyle.Dot;
        [Display(GroupName = "8. Strike Grid", Name = "Grid dash style", Order = 50)]
        public DashStyle StrikeGridDash
        {
            get => _strikeGridDash;
            set { _strikeGridDash = value; RequestRecalc(); }
        }

        private bool _showStrikeGridLabels = true;
        [Display(GroupName = "8. Strike Grid", Name = "Show strike labels", Order = 60)]
        public bool ShowStrikeGridLabels
        {
            get => _showStrikeGridLabels;
            set { _showStrikeGridLabels = value; RequestRecalc(); }
        }

        private Color _strikeGridLabelColor = Color.FromArgb(180, 180, 180, 180);
        [Display(GroupName = "8. Strike Grid", Name = "Label color", Order = 70)]
        public Color StrikeGridLabelColor
        {
            get => _strikeGridLabelColor;
            set { _strikeGridLabelColor = value; RequestRecalc(); }
        }

        private int _strikeGridLabelFontSize = 8;
        [Display(GroupName = "8. Strike Grid", Name = "Label font size", Order = 80)]
        [Range(6, 16)]
        public int StrikeGridLabelFontSize
        {
            get => _strikeGridLabelFontSize;
            set { _strikeGridLabelFontSize = Math.Clamp(value, 6, 16); RequestRecalc(); }
        }

        public enum GridLabelPosition { Left, Right }
        private GridLabelPosition _strikeGridLabelPos = GridLabelPosition.Right;
        [Display(GroupName = "8. Strike Grid", Name = "Label position", Order = 90)]
        public GridLabelPosition StrikeGridLabelPosition
        {
            get => _strikeGridLabelPos;
            set { _strikeGridLabelPos = value; RequestRecalc(); }
        }

        private Color _strikeGridMajorColor = Color.FromArgb(80, 200, 200, 200);
        [Display(GroupName = "8. Strike Grid", Name = "Major strike color", Order = 100)]
        public Color StrikeGridMajorColor
        {
            get => _strikeGridMajorColor;
            set { _strikeGridMajorColor = value; RequestRecalc(); }
        }

        private int _strikeGridMajorEvery = 5;
        [Display(GroupName = "8. Strike Grid", Name = "Major line every N strikes", Order = 110)]
        [Range(0, 50)]
        public int StrikeGridMajorEvery
        {
            get => _strikeGridMajorEvery;
            set { _strikeGridMajorEvery = Math.Clamp(value, 0, 50); RequestRecalc(); }
        }

        private int _strikeGridMajorThickness = 2;
        [Display(GroupName = "8. Strike Grid", Name = "Major line thickness", Order = 120)]
        [Range(1, 8)]
        public int StrikeGridMajorThickness
        {
            get => _strikeGridMajorThickness;
            set { _strikeGridMajorThickness = Math.Clamp(value, 1, 8); RequestRecalc(); }
        }
        #endregion

        #region Constructor
        public GexBotClassicProfile()
        {
            EnableCustomDrawing = true;
            DenyToChangePanel = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);

            _httpClient.DefaultRequestHeaders.Add("User-Agent", "ATAS-GexBotIndicator/1.0");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

            _refreshTimer.AutoReset = true;
            _refreshTimer.Elapsed += OnRefreshTimer;
            _refreshTimer.Start();
        }
        #endregion

        #region Lifecycle
        protected override void OnInitialize()
        {
            ResetTimer();
            _ = LoadDataAsync();
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            _lastChartPrice = value;
        }

        protected override void OnDispose()
        {
            try
            {
                _refreshTimer.Stop();
                _refreshTimer.Dispose();
                _httpClient.Dispose();
            }
            catch { }
            base.OnDispose();
        }
        #endregion

        #region Data Loading
        private void ResetTimer()
        {
            _refreshTimer.Stop();
            _refreshTimer.Interval = Math.Max(1, _refreshSeconds) * 1000;
            _refreshTimer.Start();
        }

        private void OnRefreshTimer(object? sender, System.Timers.ElapsedEventArgs e)
        {
            _ = LoadDataAsync();
        }

        private void ForceReload()
        {
            _ = LoadDataAsync();
            RedrawChart();
        }

        private string GetTickerString()
        {
            return _ticker switch
            {
                TickerType.NQ_NDX => "NQ_NDX",
                TickerType.ES_SPX => "ES_SPX",
                _ => _ticker.ToString()
            };
        }

        private async Task LoadDataAsync()
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                lock (_sync)
                {
                    _error = "API Key is required";
                    _data = null;
                }
                RedrawChart();
                return;
            }

            try
            {
                var tickerStr = GetTickerString();
                var url = $"https://api.gexbot.com/{tickerStr}/classic/{_aggregation}?key={_apiKey}";

                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    lock (_sync)
                    {
                        _error = $"API Error: {response.StatusCode}";
                        _data = null;
                    }
                    RedrawChart();
                    return;
                }

                var json = await response.Content.ReadAsStringAsync();
                var parsed = ParseGexClassicResponse(json);

                lock (_sync)
                {
                    _data = parsed;
                    _error = string.Empty;
                    _lastLoad = DateTime.Now;
                }

                RedrawChart();
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _error = $"Load error: {ex.Message}";
                    _data = null;
                }
                RedrawChart();
            }
        }

        private GexClassicData? ParseGexClassicResponse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var data = new GexClassicData
                {
                    Timestamp = root.GetProperty("timestamp").GetInt64(),
                    Ticker = root.GetProperty("ticker").GetString() ?? string.Empty,
                    MinDte = root.TryGetProperty("min_dte", out var minDte) ? minDte.GetInt32() : 0,
                    SecMinDte = root.TryGetProperty("sec_min_dte", out var secMinDte) ? secMinDte.GetInt32() : 0,
                    Spot = root.TryGetProperty("spot", out var spot) ? (decimal)spot.GetDouble() : 0m,
                    ZeroGamma = root.TryGetProperty("zero_gamma", out var zg) ? (decimal)zg.GetDouble() : 0m,
                    MajorPosVol = root.TryGetProperty("major_pos_vol", out var mpv) ? (decimal)mpv.GetDouble() : 0m,
                    MajorPosOI = root.TryGetProperty("major_pos_oi", out var mpo) ? (decimal)mpo.GetDouble() : 0m,
                    MajorNegVol = root.TryGetProperty("major_neg_vol", out var mnv) ? (decimal)mnv.GetDouble() : 0m,
                    MajorNegOI = root.TryGetProperty("major_neg_oi", out var mno) ? (decimal)mno.GetDouble() : 0m,
                    SumGexVol = root.TryGetProperty("sum_gex_vol", out var sgv) ? (decimal)sgv.GetDouble() : 0m,
                    SumGexOI = root.TryGetProperty("sum_gex_oi", out var sgo) ? (decimal)sgo.GetDouble() : 0m
                };

                if (root.TryGetProperty("strikes", out var strikesArr))
                {
                    foreach (var strikeItem in strikesArr.EnumerateArray())
                    {
                        if (strikeItem.ValueKind == JsonValueKind.Array)
                        {
                            var arr = strikeItem.EnumerateArray().ToList();
                            if (arr.Count >= 3)
                            {
                                var strike = new GexStrike
                                {
                                    Strike = (decimal)arr[0].GetDouble(),
                                    GexByVolume = (decimal)arr[1].GetDouble(),
                                    GexByOI = (decimal)arr[2].GetDouble()
                                };

                                if (arr.Count >= 4 && arr[3].ValueKind == JsonValueKind.Array)
                                {
                                    strike.Priors = arr[3].EnumerateArray()
                                        .Select(p => (decimal)p.GetDouble())
                                        .ToArray();
                                }

                                data.Strikes.Add(strike);
                            }
                        }
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

        #region Rendering
        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo?.PriceChartContainer == null)
            {
                context.DrawString("Chart not ready", new RenderFont("Arial", 10), Color.Red, 10, 10);
                return;
            }

            GexClassicData? snapshot;
            string error;
            DateTime? lastLoad;
            lock (_sync)
            {
                snapshot = _data;
                error = _error;
                lastLoad = _lastLoad;
            }

            // Draw error message if any
            if (!string.IsNullOrEmpty(error))
            {
                context.DrawString(error, new RenderFont("Arial", 10), Color.Red, 10, 10);
            }

            if (snapshot == null || snapshot.Strikes.Count == 0)
            {
                if (string.IsNullOrEmpty(error))
                    context.DrawString("No GEX data", new RenderFont("Arial", 10), Color.Gray, 10, 10);
                return;
            }

            var xBase = ChartInfo.PriceChartContainer.GetXByBar(CurrentBar, false);
            var xCenter = xBase + _centerOffsetPx;
            var fullWidth = ChartInfo.Region.Width;

            // Calculate conversion factor
            decimal factor = _conversionFactor;
            if (_enableConversion && _autoCalculateFactor && snapshot.Spot > 0 && _lastChartPrice > 0)
            {
                factor = _lastChartPrice / snapshot.Spot;
            }

            // Draw strike grid first (behind everything)
            DrawStrikeGrid(context, factor, fullWidth);

            // Convert strikes to chart price
            var convertedStrikes = snapshot.Strikes.Select(s => new
            {
                OriginalStrike = s.Strike,
                ChartStrike = _enableConversion ? RoundToStep(s.Strike * factor, _priceStep) : s.Strike,
                GexValue = _dataSource == GexDataSource.Volume ? s.GexByVolume : s.GexByOI,
                s.Priors
            }).ToList();

            // Calculate scale
            double maxGex = convertedStrikes.Max(s => Math.Abs((double)s.GexValue));
            if (maxGex <= 0) maxGex = 1;
            var scale = _maxBarWidthPx / maxGex;

            // Draw center line
            if (_showCenterLine)
            {
                var pen = new RenderPen(_centerLineColor, _centerLineThickness);
                context.DrawLine(pen, xCenter, 0, xCenter, ChartInfo.Region.Height);
            }

            var valueFont = new RenderFont("Arial", _valueFontSize);

            // Draw GEX bars
            foreach (var strike in convertedStrikes)
            {
                var y = ChartInfo.PriceChartContainer.GetYByPrice(strike.ChartStrike, false);
                var top = y - _barThicknessPx / 2;

                var gexVal = (double)strike.GexValue;
                var barWidth = (int)Math.Round(Math.Abs(gexVal) * scale);

                if (barWidth <= 0) continue;

                var isPositive = gexVal >= 0;
                var barColor = isPositive ? _positiveGexColor : _negativeGexColor;
                var fillColor = Color.FromArgb(_fillOpacity, barColor);

                Rectangle barRect;
                if (isPositive)
                {
                    // Positive GEX: draw to the right
                    barRect = new Rectangle(xCenter, top, barWidth, _barThicknessPx);
                }
                else
                {
                    // Negative GEX: draw to the left
                    barRect = new Rectangle(xCenter - barWidth, top, barWidth, _barThicknessPx);
                }

                context.FillRectangle(fillColor, barRect);

                if (_showBarOutline)
                {
                    var outlinePen = new RenderPen(barColor, _outlineThickness);
                    context.DrawRectangle(outlinePen, barRect);
                }

                // Draw value label
                if (_showValues && barWidth > 20)
                {
                    var valText = FormatCompact(strike.GexValue);
                    int textX;
                    if (isPositive)
                    {
                        textX = xCenter + barWidth + _valueOffsetPx;
                    }
                    else
                    {
                        var tw = EstimateTextWidth(valText, valueFont);
                        textX = xCenter - barWidth - tw - _valueOffsetPx;
                    }
                    context.DrawString(valText, valueFont, _valueTextColor, textX, top);
                }

                // Draw strike label
                if (_showStrikeLabels)
                {
                    var strikeText = $"({strike.OriginalStrike:0})";
                    var sw = EstimateTextWidth(strikeText, valueFont);
                    int strikeX;
                    if (isPositive)
                    {
                        strikeX = xCenter + barWidth + _valueOffsetPx + (_showValues ? 50 : 0);
                    }
                    else
                    {
                        strikeX = xCenter - barWidth - sw - _valueOffsetPx - (_showValues ? 50 : 0);
                    }
                    context.DrawString(strikeText, valueFont, _strikeTextColor, strikeX, top);
                }
            }

            // Draw major levels
            DrawMajorLevels(context, snapshot, factor, fullWidth);

            // Draw info panel
            if (_showInfoPanel)
            {
                DrawInfoPanel(context, snapshot, lastLoad);
            }
        }

        private void DrawMajorLevels(RenderContext context, GexClassicData data, decimal factor, int fullWidth)
        {
            var labelFont = new RenderFont("Arial", 9);

            // Zero Gamma line
            if (_showZeroGammaLine && data.ZeroGamma > 0)
            {
                var zeroGammaPrice = _enableConversion ? RoundToStep(data.ZeroGamma * factor, _priceStep) : data.ZeroGamma;
                var y = ChartInfo.PriceChartContainer.GetYByPrice(zeroGammaPrice, false);
                var pen = new RenderPen(_zeroGammaColor, _zeroGammaThickness) { DashStyle = _zeroGammaDash };
                context.DrawLine(pen, 0, y, fullWidth, y);

                if (_showMajorLabels)
                {
                    context.DrawString($"ZG: {data.ZeroGamma:0.00}", labelFont, _zeroGammaColor, 5, y - 12);
                }
            }

            // Major Positive line
            if (_showMajorPosLine)
            {
                var majorPos = _dataSource == GexDataSource.Volume ? data.MajorPosVol : data.MajorPosOI;
                if (majorPos > 0)
                {
                    var majorPosPrice = _enableConversion ? RoundToStep(majorPos * factor, _priceStep) : majorPos;
                    var y = ChartInfo.PriceChartContainer.GetYByPrice(majorPosPrice, false);
                    var pen = new RenderPen(_majorPosColor, _majorLinesThickness) { DashStyle = _majorLinesDash };
                    context.DrawLine(pen, 0, y, fullWidth, y);

                    if (_showMajorLabels)
                    {
                        context.DrawString($"MAX+: {majorPos:0}", labelFont, _majorPosColor, 5, y - 12);
                    }
                }
            }

            // Major Negative line
            if (_showMajorNegLine)
            {
                var majorNeg = _dataSource == GexDataSource.Volume ? data.MajorNegVol : data.MajorNegOI;
                if (majorNeg > 0)
                {
                    var majorNegPrice = _enableConversion ? RoundToStep(majorNeg * factor, _priceStep) : majorNeg;
                    var y = ChartInfo.PriceChartContainer.GetYByPrice(majorNegPrice, false);
                    var pen = new RenderPen(_majorNegColor, _majorLinesThickness) { DashStyle = _majorLinesDash };
                    context.DrawLine(pen, 0, y, fullWidth, y);

                    if (_showMajorLabels)
                    {
                        context.DrawString($"MAX-: {majorNeg:0}", labelFont, _majorNegColor, 5, y - 12);
                    }
                }
            }
        }

        private void DrawInfoPanel(RenderContext context, GexClassicData data, DateTime? lastLoad)
        {
            var font = new RenderFont("Arial", _infoPanelFontSize);
            var fontBold = new RenderFont("Arial", _infoPanelFontSize + 1);
            var fontSmall = new RenderFont("Arial", _infoPanelFontSize - 1);
            var lineHeight = _infoPanelFontSize + 8;
            var rowHeight = _infoPanelFontSize + 12;

            // Calculate panel dimensions
            int panelWidth = Math.Max(200, _infoPanelBarWidth + 100);
            int panelHeight = rowHeight * 8 + 20;

            // Calculate position based on alignment
            int x, y;
            switch (_infoPanelPosition)
            {
                case InfoPanelAlign.TopRight:
                    x = ChartInfo.Region.Width - panelWidth - _infoPanelX;
                    y = _infoPanelY;
                    break;
                case InfoPanelAlign.BottomLeft:
                    x = _infoPanelX;
                    y = ChartInfo.Region.Height - panelHeight - _infoPanelY;
                    break;
                case InfoPanelAlign.BottomRight:
                    x = ChartInfo.Region.Width - panelWidth - _infoPanelX;
                    y = ChartInfo.Region.Height - panelHeight - _infoPanelY;
                    break;
                case InfoPanelAlign.TopLeft:
                default:
                    x = _infoPanelX;
                    y = _infoPanelY;
                    break;
            }

            // Draw background with rounded effect (solid rectangle + border)
            var backRect = new Rectangle(x, y, panelWidth, panelHeight);
            context.FillRectangle(_infoPanelBackColor, backRect);
            context.DrawRectangle(new RenderPen(_infoPanelBorderColor, 1), backRect);

            // Header bar
            var headerRect = new Rectangle(x, y, panelWidth, lineHeight + 4);
            context.FillRectangle(Color.FromArgb(80, _infoPanelHeaderColor), headerRect);
            context.DrawString($"⚡ GexBot Classic - {data.Ticker}", fontBold, _infoPanelHeaderColor, x + 8, y + 4);

            int ty = y + lineHeight + 10;
            int labelX = x + 10;
            int valueX = x + 90;
            int barX = x + 10;
            int barMaxW = _infoPanelBarWidth;

            // Spot price row with indicator
            var spotColor = data.Spot > 0 ? _infoPanelAccentPositive : _infoPanelTextColor;
            context.DrawString("Spot:", font, Color.Gray, labelX, ty);
            context.DrawString($"{data.Spot:0.00}", font, spotColor, valueX, ty);
            ty += lineHeight;

            // Zero Gamma row with visual indicator
            var zgColor = _zeroGammaColor;
            context.DrawString("Zero Gamma:", font, Color.Gray, labelX, ty);
            context.DrawString($"{data.ZeroGamma:0.00}", font, zgColor, valueX, ty);
            // Small indicator dot
            context.FillRectangle(zgColor, new Rectangle(x + panelWidth - 20, ty + 4, 8, 8));
            ty += lineHeight;

            // Net GEX Volume with visual bar
            var gexVolColor = data.SumGexVol >= 0 ? _infoPanelAccentPositive : _infoPanelAccentNegative;
            context.DrawString("Net GEX (Vol):", font, Color.Gray, labelX, ty);
            context.DrawString(FormatCompact(data.SumGexVol), font, gexVolColor, valueX + 60, ty);
            if (_showInfoPanelBars)
            {
                DrawGexBar(context, barX, ty + lineHeight - 2, barMaxW, 6, data.SumGexVol, data.SumGexVol, data.SumGexOI);
                ty += 10;
            }
            ty += lineHeight;

            // Net GEX OI with visual bar
            var gexOiColor = data.SumGexOI >= 0 ? _infoPanelAccentPositive : _infoPanelAccentNegative;
            context.DrawString("Net GEX (OI):", font, Color.Gray, labelX, ty);
            context.DrawString(FormatCompact(data.SumGexOI), font, gexOiColor, valueX + 60, ty);
            if (_showInfoPanelBars)
            {
                DrawGexBar(context, barX, ty + lineHeight - 2, barMaxW, 6, data.SumGexOI, data.SumGexVol, data.SumGexOI);
                ty += 10;
            }
            ty += lineHeight;

            // DTE info with badge style
            var dteText = data.MinDte == 0 ? "0DTE" : $"{data.MinDte}DTE";
            var dteColor = data.MinDte == 0 ? Color.Gold : _infoPanelTextColor;
            context.DrawString("Expiry:", font, Color.Gray, labelX, ty);
            // Badge background for 0DTE
            if (data.MinDte == 0)
            {
                var badgeRect = new Rectangle(valueX - 2, ty - 2, 40, lineHeight - 2);
                context.FillRectangle(Color.FromArgb(100, Color.Gold), badgeRect);
            }
            context.DrawString(dteText, font, dteColor, valueX, ty);
            context.DrawString($"/ {data.SecMinDte}DTE", fontSmall, Color.Gray, valueX + 45, ty + 2);
            ty += lineHeight;

            // Timestamp row
            if (lastLoad.HasValue)
            {
                context.DrawString("Updated:", font, Color.Gray, labelX, ty);
                context.DrawString($"{lastLoad.Value:HH:mm:ss}", fontSmall, Color.FromArgb(180, 150, 150, 150), valueX, ty);
            }
        }

        private void DrawGexBar(RenderContext context, int x, int y, int maxWidth, int height, decimal value, decimal maxVol, decimal maxOi)
        {
            // Background bar
            context.FillRectangle(Color.FromArgb(60, 80, 80, 80), new Rectangle(x, y, maxWidth, height));

            // Calculate bar width based on max of vol and oi
            var maxVal = Math.Max(Math.Abs(maxVol), Math.Abs(maxOi));
            if (maxVal <= 0) maxVal = 1;
            var ratio = Math.Abs(value) / maxVal;
            var barWidth = (int)(maxWidth * (double)ratio);
            barWidth = Math.Clamp(barWidth, 0, maxWidth);

            var color = value >= 0 ? _infoPanelAccentPositive : _infoPanelAccentNegative;
            context.FillRectangle(Color.FromArgb(200, color), new Rectangle(x, y, barWidth, height));
        }

        private void DrawStrikeGrid(RenderContext context, decimal conversionFactor, int fullWidth)
        {
            if (!_showStrikeGrid || _strikeGridStep <= 0)
                return;

            var pen = new RenderPen(_strikeGridColor, _strikeGridThickness) { DashStyle = _strikeGridDash };
            var majorPen = new RenderPen(_strikeGridMajorColor, _strikeGridMajorThickness) { DashStyle = _strikeGridDash };
            var labelFont = new RenderFont("Arial", _strikeGridLabelFontSize);

            // Determine visible price range
            decimal minPrice, maxPrice;
            try
            {
                minPrice = ChartInfo.PriceChartContainer.Low;
                maxPrice = ChartInfo.PriceChartContainer.High;
            }
            catch
            {
                return;
            }

            // Convert chart prices to underlying strikes if conversion is enabled
            decimal startStrike, endStrike;
            if (_enableConversion && conversionFactor > 0)
            {
                startStrike = _strikeGridStart > 0 ? _strikeGridStart : Math.Floor(minPrice / conversionFactor);
                endStrike = _strikeGridEnd > 0 ? _strikeGridEnd : Math.Ceiling(maxPrice / conversionFactor);
            }
            else
            {
                startStrike = _strikeGridStart > 0 ? _strikeGridStart : Math.Floor(minPrice);
                endStrike = _strikeGridEnd > 0 ? _strikeGridEnd : Math.Ceiling(maxPrice);
            }

            // Align to step
            startStrike = Math.Floor(startStrike / _strikeGridStep) * _strikeGridStep;

            int strikeIndex = 0;
            for (decimal strike = startStrike; strike <= endStrike + _strikeGridStep; strike += _strikeGridStep)
            {
                // Convert strike to chart price
                decimal chartPrice = _enableConversion ? strike * conversionFactor : strike;
                chartPrice = RoundToStep(chartPrice, _priceStep);

                var yPos = ChartInfo.PriceChartContainer.GetYByPrice(chartPrice, false);

                // Check if this is a major strike
                bool isMajor = _strikeGridMajorEvery > 0 && (strikeIndex % _strikeGridMajorEvery == 0);
                var currentPen = isMajor ? majorPen : pen;

                // Draw the horizontal line
                context.DrawLine(currentPen, 0, yPos, fullWidth, yPos);

                // Draw strike label
                if (_showStrikeGridLabels)
                {
                    var labelText = strike.ToString("0");
                    var labelWidth = EstimateTextWidth(labelText, labelFont);
                    int labelX = _strikeGridLabelPos == GridLabelPosition.Right
                        ? fullWidth - labelWidth - 5
                        : 5;

                    // Background for better visibility
                    var labelColor = isMajor ? _strikeGridMajorColor : _strikeGridLabelColor;
                    context.DrawString(labelText, labelFont, labelColor, labelX, yPos - _strikeGridLabelFontSize / 2 - 1);
                }

                strikeIndex++;
            }
        }
        #endregion

        #region Utility Methods
        private static decimal RoundToStep(decimal price, decimal step)
        {
            if (step <= 0) return price;
            var q = price / step;
            var rounded = Math.Round(q, 0, MidpointRounding.AwayFromZero);
            return rounded * step;
        }

        private static string FormatCompact(decimal value)
        {
            var abs = Math.Abs(value);
            string suffix;
            decimal num;
            if (abs >= 1_000_000_000m)
            {
                suffix = "B";
                num = value / 1_000_000_000m;
            }
            else if (abs >= 1_000_000m)
            {
                suffix = "M";
                num = value / 1_000_000m;
            }
            else if (abs >= 1_000m)
            {
                suffix = "K";
                num = value / 1_000m;
            }
            else
            {
                return value.ToString("0.##", CultureInfo.CurrentCulture);
            }

            return num.ToString("0.##", CultureInfo.CurrentCulture) + suffix;
        }

        private static int EstimateTextWidth(string text, RenderFont font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            double factor = 0.58;
            return (int)Math.Ceiling(text.Length * (font.Size * factor));
        }
        #endregion
    }
}
