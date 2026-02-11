using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace ATAS.Indicators.Technical
{
    /// <summary>
    /// DTE Exposure Profile - Stacked Options Exposure by Expiration
    /// 
    /// Visualization: Horizontal stacked bars per strike, one color per expiration.
    /// Calls grow to the LEFT of center, Puts grow to the RIGHT.
    /// Each expiration is a stacked segment inside the bar.
    ///
    /// API: GET /api/v1/options/exposure?symbol=...&amp;dte=...
    /// Auth: Bearer Token
    /// </summary>
    [DisplayName("DTE Exposure Profile")]
    public class DteExposureProfile : Indicator
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
        private class StrikeData
        {
            public decimal Strike { get; set; }
            public decimal NetGamma { get; set; }
            public decimal CallAbsDelta { get; set; }
            public decimal CallAbsGamma { get; set; }
            public decimal CallOI { get; set; }
            public decimal CallVolume { get; set; }
            public decimal PutAbsDelta { get; set; }
            public decimal PutAbsGamma { get; set; }
            public decimal PutOI { get; set; }
            public decimal PutVolume { get; set; }
        }

        private class ExpirationData
        {
            public string Expiration { get; set; } = string.Empty;
            public int Dte { get; set; }
            public List<StrikeData> Strikes { get; set; } = new();
        }

        private class ExposureData
        {
            public string Symbol { get; set; } = string.Empty;
            public int? DteFilter { get; set; }
            public decimal SpotPrice { get; set; }
            public string Timestamp { get; set; } = string.Empty;
            public int TotalExpirations { get; set; }
            public List<ExpirationData> Expirations { get; set; } = new();
        }

        private class AggregatedStrike
        {
            public decimal Strike { get; set; }
            public decimal NetGamma { get; set; }
            public decimal CallDelta { get; set; }
            public decimal PutDelta { get; set; }
            public decimal CallGamma { get; set; }
            public decimal PutGamma { get; set; }
            public decimal CallOI { get; set; }
            public decimal PutOI { get; set; }
            public decimal CallVolume { get; set; }
            public decimal PutVolume { get; set; }
        }

        /// <summary>Per-expiration per-strike value used for stacked bar rendering.</summary>
        private class ExpirationStrikeValue
        {
            public string Expiration { get; set; } = string.Empty;
            public int Dte { get; set; }
            public decimal Strike { get; set; }
            public decimal CallValue { get; set; }
            public decimal PutValue { get; set; }
        }

        private class ApiDiagnostics
        {
            public DateTime? LastSuccessTime { get; set; }
            public DateTime? LastErrorTime { get; set; }
            public int SuccessCount { get; set; }
            public int ErrorCount { get; set; }
            public double LastLatencyMs { get; set; }
            public double AverageLatencyMs { get; set; }
            public string LastErrorMessage { get; set; } = string.Empty;
            public bool IsConnected { get; set; }
        }
        #endregion

        #region Fields
        private readonly object _sync = new();
        private readonly HttpClient _httpClient = new();
        private readonly System.Timers.Timer _refreshTimer = new(60000);
        private ExposureData? _rawData;
        private List<AggregatedStrike> _aggregatedStrikes = new();
        private string _error = string.Empty;
        private DateTime? _lastLoad;
        private decimal _lastChartPrice;
        private ApiDiagnostics _apiDiagnostics = new();
        private System.Diagnostics.Stopwatch _apiStopwatch = new();
        private readonly Queue<double> _latencyHistory = new();
        private const int MaxLatencyHistory = 20;

        // Expiration color palette (ordered for visual distinction)
        private static readonly Color[] ExpirationColors = new[]
        {
            Color.FromArgb(220, 230, 100, 50),   // warm orange
            Color.FromArgb(220, 220, 60, 60),     // red
            Color.FromArgb(220, 230, 130, 80),    // light orange
            Color.FromArgb(220, 200, 80, 120),    // pink-red
            Color.FromArgb(220, 180, 100, 160),   // magenta
            Color.FromArgb(220, 200, 180, 60),    // olive-yellow
            Color.FromArgb(220, 220, 200, 60),    // yellow
            Color.FromArgb(220, 100, 180, 100),   // green
            Color.FromArgb(220, 80, 160, 140),    // teal
            Color.FromArgb(220, 60, 140, 180),    // steel blue
            Color.FromArgb(220, 80, 120, 200),    // blue
            Color.FromArgb(220, 100, 200, 220),   // cyan
            Color.FromArgb(220, 150, 120, 200),   // purple
            Color.FromArgb(220, 180, 140, 200),   // lavender
            Color.FromArgb(220, 200, 160, 120),   // tan
            Color.FromArgb(220, 160, 200, 100),   // lime
        };

        // Export
        private DateTime? _lastExportTime;
        private string _lastExportFile = string.Empty;
        private string _lastExportError = string.Empty;
        private int _exportCount;
        #endregion

        #region 01. API Configuration
        private string _apiToken = string.Empty;
        [Display(GroupName = "01. 🔑 API Configuration", Name = "API Token (Bearer)", Description = "Bearer token for authentication", Order = 10)]
        public string ApiToken
        {
            get => _apiToken;
            set { _apiToken = value ?? string.Empty; ForceReload(); }
        }

        public enum SymbolType { SPX, QQQ, SPY, IWM, NDX, RUT }
        private SymbolType _symbol = SymbolType.SPX;
        [Display(GroupName = "01. 🔑 API Configuration", Name = "Symbol", Description = "Ticker symbol to fetch", Order = 20)]
        public SymbolType Symbol
        {
            get => _symbol;
            set { _symbol = value; ForceReload(); }
        }

        private int _dteFilter = 0;
        [Display(GroupName = "01. 🔑 API Configuration", Name = "DTE Filter", Description = "Max Days to Expiration (0 = 0DTE only, -1 = all)", Order = 30)]
        [Range(-1, 365)]
        public int DteFilter
        {
            get => _dteFilter;
            set { _dteFilter = Math.Clamp(value, -1, 365); ForceReload(); }
        }

        private int _refreshSeconds = 60;
        [Display(GroupName = "01. 🔑 API Configuration", Name = "Refresh Interval (sec)", Description = "Data refresh interval in seconds", Order = 40)]
        [Range(10, 3600)]
        public int RefreshSeconds
        {
            get => _refreshSeconds;
            set { _refreshSeconds = Math.Max(10, value); ResetTimer(); }
        }

        public enum DisplayMetric { DEX, GEX, OI, Volume }
        private DisplayMetric _displayMetric = DisplayMetric.DEX;
        [Display(GroupName = "01. 🔑 API Configuration", Name = "Display Metric", Description = "Which metric to show on bars (Call left / Put right)", Order = 50)]
        public DisplayMetric Metric
        {
            get => _displayMetric;
            set { _displayMetric = value; RequestRecalc(); }
        }
        #endregion

        #region 02. Price Conversion
        private bool _enableConversion = true;
        [Display(GroupName = "02. 🔄 Price Conversion", Name = "Enable Conversion", Description = "Convert spot prices to chart prices (for futures)", Order = 10)]
        public bool EnableConversion
        {
            get => _enableConversion;
            set { _enableConversion = value; RequestRecalc(); }
        }

        private decimal _conversionFactor = 1.0m;
        [Display(GroupName = "02. 🔄 Price Conversion", Name = "Conversion Factor", Description = "Manual factor: chart_price = spot * factor", Order = 20)]
        public decimal ConversionFactor
        {
            get => _conversionFactor;
            set { _conversionFactor = value <= 0 ? 1.0m : value; RequestRecalc(); }
        }

        private bool _autoCalculateFactor = true;
        [Display(GroupName = "02. 🔄 Price Conversion", Name = "Auto-Calculate Factor", Description = "Automatically calculate factor from current chart price", Order = 30)]
        public bool AutoCalculateFactor
        {
            get => _autoCalculateFactor;
            set { _autoCalculateFactor = value; RequestRecalc(); }
        }

        private decimal _priceStep = 0.25m;
        [Display(GroupName = "02. 🔄 Price Conversion", Name = "Price Step", Description = "Rounding step for converted prices", Order = 40)]
        public decimal PriceStep
        {
            get => _priceStep;
            set { _priceStep = value <= 0 ? 0.25m : value; RequestRecalc(); }
        }
        #endregion

        #region 03. Profile Position
        private int _centerOffsetPx = 0;
        [Display(GroupName = "03. 📍 Profile Position", Name = "Center Offset (px)", Description = "Horizontal offset from current bar", Order = 10)]
        [Range(-5000, 5000)]
        public int CenterOffsetPx
        {
            get => _centerOffsetPx;
            set { _centerOffsetPx = Math.Clamp(value, -5000, 5000); RequestRecalc(); }
        }

        private bool _showCenterLine = true;
        [Display(GroupName = "03. 📍 Profile Position", Name = "Show Center Line", Order = 20)]
        public bool ShowCenterLine
        {
            get => _showCenterLine;
            set { _showCenterLine = value; RequestRecalc(); }
        }

        private Color _centerLineColor = Color.FromArgb(140, Color.Yellow);
        [Display(GroupName = "03. 📍 Profile Position", Name = "Center Line Color", Order = 30)]
        public Color CenterLineColor
        {
            get => _centerLineColor;
            set { _centerLineColor = value; RequestRecalc(); }
        }
        #endregion

        #region 04. Bar Appearance
        private int _maxBarWidthPx = 300;
        [Display(GroupName = "04. 📊 Bar Appearance", Name = "Max Bar Width (px)", Description = "Max width of one side (calls or puts)", Order = 10)]
        [Range(50, 1500)]
        public int MaxBarWidthPx
        {
            get => _maxBarWidthPx;
            set { _maxBarWidthPx = Math.Clamp(value, 50, 1500); RequestRecalc(); }
        }

        private int _barThicknessPx = 8;
        [Display(GroupName = "04. 📊 Bar Appearance", Name = "Bar Thickness (px)", Order = 20)]
        [Range(2, 50)]
        public int BarThicknessPx
        {
            get => _barThicknessPx;
            set { _barThicknessPx = Math.Clamp(value, 2, 50); RequestRecalc(); }
        }

        private int _fillOpacity = 200;
        [Display(GroupName = "04. 📊 Bar Appearance", Name = "Fill Opacity", Order = 30)]
        [Range(0, 255)]
        public int FillOpacity
        {
            get => _fillOpacity;
            set { _fillOpacity = Math.Clamp(value, 0, 255); RequestRecalc(); }
        }

        private bool _showBarOutline = true;
        [Display(GroupName = "04. 📊 Bar Appearance", Name = "Show Bar Outline", Order = 40)]
        public bool ShowBarOutline
        {
            get => _showBarOutline;
            set { _showBarOutline = value; RequestRecalc(); }
        }

        private bool _showCallPutLabels = true;
        [Display(GroupName = "04. 📊 Bar Appearance", Name = "Show CALLS / PUTS Headers", Order = 50)]
        public bool ShowCallPutLabels
        {
            get => _showCallPutLabels;
            set { _showCallPutLabels = value; RequestRecalc(); }
        }
        #endregion

        #region 05. Labels & Values
        private bool _showValues = true;
        [Display(GroupName = "05. 🏷️ Labels & Values", Name = "Show Values", Description = "Display numeric values on bars", Order = 10)]
        public bool ShowValues
        {
            get => _showValues;
            set { _showValues = value; RequestRecalc(); }
        }

        private int _valueFontSize = 8;
        [Display(GroupName = "05. 🏷️ Labels & Values", Name = "Font Size", Order = 20)]
        [Range(6, 20)]
        public int ValueFontSize
        {
            get => _valueFontSize;
            set { _valueFontSize = Math.Clamp(value, 6, 20); RequestRecalc(); }
        }

        private Color _valueTextColor = Color.White;
        [Display(GroupName = "05. 🏷️ Labels & Values", Name = "Value Text Color", Order = 30)]
        public Color ValueTextColor
        {
            get => _valueTextColor;
            set { _valueTextColor = value; RequestRecalc(); }
        }

        private int _valueOffsetPx = 4;
        [Display(GroupName = "05. 🏷️ Labels & Values", Name = "Value Offset (px)", Description = "Distance between bar edge and value label", Order = 35)]
        [Range(0, 100)]
        public int ValueOffsetPx
        {
            get => _valueOffsetPx;
            set { _valueOffsetPx = Math.Clamp(value, 0, 100); RequestRecalc(); }
        }

        private bool _showStrikeLabels = true;
        [Display(GroupName = "05. 🏷️ Labels & Values", Name = "Show Strike Labels", Order = 50)]
        public bool ShowStrikeLabels
        {
            get => _showStrikeLabels;
            set { _showStrikeLabels = value; RequestRecalc(); }
        }

        private Color _strikeTextColor = Color.LightGray;
        [Display(GroupName = "05. 🏷️ Labels & Values", Name = "Strike Text Color", Order = 60)]
        public Color StrikeTextColor
        {
            get => _strikeTextColor;
            set { _strikeTextColor = value; RequestRecalc(); }
        }
        #endregion

        #region 06. Major Levels
        private bool _showMaxPosLine = true;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Show Max Positive Line", Description = "Strike with highest positive net gamma", Order = 10)]
        public bool ShowMaxPosLine
        {
            get => _showMaxPosLine;
            set { _showMaxPosLine = value; RequestRecalc(); }
        }

        private Color _maxPosColor = Color.LimeGreen;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Max Positive Color", Order = 20)]
        public Color MaxPosColor
        {
            get => _maxPosColor;
            set { _maxPosColor = value; RequestRecalc(); }
        }

        private bool _showMaxNegLine = true;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Show Max Negative Line", Description = "Strike with most negative net gamma", Order = 30)]
        public bool ShowMaxNegLine
        {
            get => _showMaxNegLine;
            set { _showMaxNegLine = value; RequestRecalc(); }
        }

        private Color _maxNegColor = Color.OrangeRed;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Max Negative Color", Order = 40)]
        public Color MaxNegColor
        {
            get => _maxNegColor;
            set { _maxNegColor = value; RequestRecalc(); }
        }

        private bool _showZeroGammaLine = true;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Show Zero Gamma Line", Description = "Interpolated price where net gamma crosses zero", Order = 50)]
        public bool ShowZeroGammaLine
        {
            get => _showZeroGammaLine;
            set { _showZeroGammaLine = value; RequestRecalc(); }
        }

        private Color _zeroGammaColor = Color.Yellow;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Zero Gamma Color", Order = 60)]
        public Color ZeroGammaColor
        {
            get => _zeroGammaColor;
            set { _zeroGammaColor = value; RequestRecalc(); }
        }

        private int _majorLinesThickness = 2;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Lines Thickness", Order = 70)]
        [Range(1, 10)]
        public int MajorLinesThickness
        {
            get => _majorLinesThickness;
            set { _majorLinesThickness = Math.Clamp(value, 1, 10); RequestRecalc(); }
        }

        private DashStyle _majorLinesDash = DashStyle.Solid;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Lines Dash Style", Order = 80)]
        public DashStyle MajorLinesDash
        {
            get => _majorLinesDash;
            set { _majorLinesDash = value; RequestRecalc(); }
        }

        private bool _showMajorLabels = true;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Show Level Labels", Order = 90)]
        public bool ShowMajorLabels
        {
            get => _showMajorLabels;
            set { _showMajorLabels = value; RequestRecalc(); }
        }

        // ── Call P1-P3 ──
        private bool _showCallP1P2P3 = false;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Show Call P1-P3", Description = "Show 2nd, 3rd, 4th highest positive gamma strikes", Order = 100)]
        public bool ShowCallP1P2P3
        {
            get => _showCallP1P2P3;
            set { _showCallP1P2P3 = value; RequestRecalc(); }
        }

        private Color _callP1Color = Color.FromArgb(200, 50, 205, 120);
        [Display(GroupName = "06. ➖ Major Levels", Name = "Call P1 Color", Order = 105)]
        public Color CallP1Color
        {
            get => _callP1Color;
            set { _callP1Color = value; RequestRecalc(); }
        }

        private Color _callP2Color = Color.FromArgb(180, 40, 180, 110);
        [Display(GroupName = "06. ➖ Major Levels", Name = "Call P2 Color", Order = 106)]
        public Color CallP2Color
        {
            get => _callP2Color;
            set { _callP2Color = value; RequestRecalc(); }
        }

        private Color _callP3Color = Color.FromArgb(160, 30, 155, 100);
        [Display(GroupName = "06. ➖ Major Levels", Name = "Call P3 Color", Order = 107)]
        public Color CallP3Color
        {
            get => _callP3Color;
            set { _callP3Color = value; RequestRecalc(); }
        }

        private int _callPLinesThickness = 1;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Call P1-P3 Thickness", Order = 108)]
        [Range(1, 10)]
        public int CallPLinesThickness
        {
            get => _callPLinesThickness;
            set { _callPLinesThickness = Math.Clamp(value, 1, 10); RequestRecalc(); }
        }

        private DashStyle _callPLinesDash = DashStyle.Dash;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Call P1-P3 Dash Style", Order = 109)]
        public DashStyle CallPLinesDash
        {
            get => _callPLinesDash;
            set { _callPLinesDash = value; RequestRecalc(); }
        }

        // ── Put P1-P3 ──
        private bool _showPutP1P2P3 = false;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Show Put P1-P3", Description = "Show 2nd, 3rd, 4th most negative gamma strikes", Order = 110)]
        public bool ShowPutP1P2P3
        {
            get => _showPutP1P2P3;
            set { _showPutP1P2P3 = value; RequestRecalc(); }
        }

        private Color _putP1Color = Color.FromArgb(200, 230, 130, 60);
        [Display(GroupName = "06. ➖ Major Levels", Name = "Put P1 Color", Order = 115)]
        public Color PutP1Color
        {
            get => _putP1Color;
            set { _putP1Color = value; RequestRecalc(); }
        }

        private Color _putP2Color = Color.FromArgb(180, 210, 110, 50);
        [Display(GroupName = "06. ➖ Major Levels", Name = "Put P2 Color", Order = 116)]
        public Color PutP2Color
        {
            get => _putP2Color;
            set { _putP2Color = value; RequestRecalc(); }
        }

        private Color _putP3Color = Color.FromArgb(160, 190, 90, 40);
        [Display(GroupName = "06. ➖ Major Levels", Name = "Put P3 Color", Order = 117)]
        public Color PutP3Color
        {
            get => _putP3Color;
            set { _putP3Color = value; RequestRecalc(); }
        }

        private int _putPLinesThickness = 1;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Put P1-P3 Thickness", Order = 118)]
        [Range(1, 10)]
        public int PutPLinesThickness
        {
            get => _putPLinesThickness;
            set { _putPLinesThickness = Math.Clamp(value, 1, 10); RequestRecalc(); }
        }

        private DashStyle _putPLinesDash = DashStyle.Dash;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Put P1-P3 Dash Style", Order = 119)]
        public DashStyle PutPLinesDash
        {
            get => _putPLinesDash;
            set { _putPLinesDash = value; RequestRecalc(); }
        }

        private bool _showP1P2P3Labels = true;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Show P1-P3 Labels", Order = 120)]
        public bool ShowP1P2P3Labels
        {
            get => _showP1P2P3Labels;
            set { _showP1P2P3Labels = value; RequestRecalc(); }
        }
        #endregion

        #region 07. Spot Price
        private bool _showSpotPriceLine = true;
        [Display(GroupName = "07. 🎯 Spot Price", Name = "Show Spot Price Line", Order = 10)]
        public bool ShowSpotPriceLine
        {
            get => _showSpotPriceLine;
            set { _showSpotPriceLine = value; RequestRecalc(); }
        }

        private Color _spotPriceLineColor = Color.FromArgb(200, 200, 60, 60);
        [Display(GroupName = "07. 🎯 Spot Price", Name = "Spot Line Color", Order = 20)]
        public Color SpotPriceLineColor
        {
            get => _spotPriceLineColor;
            set { _spotPriceLineColor = value; RequestRecalc(); }
        }

        private int _spotPriceLineThickness = 2;
        [Display(GroupName = "07. 🎯 Spot Price", Name = "Spot Line Thickness", Order = 30)]
        [Range(1, 5)]
        public int SpotPriceLineThickness
        {
            get => _spotPriceLineThickness;
            set { _spotPriceLineThickness = Math.Clamp(value, 1, 5); RequestRecalc(); }
        }

        private DashStyle _spotPriceLineDash = DashStyle.Dot;
        [Display(GroupName = "07. 🎯 Spot Price", Name = "Spot Line Dash Style", Order = 40)]
        public DashStyle SpotPriceLineDash
        {
            get => _spotPriceLineDash;
            set { _spotPriceLineDash = value; RequestRecalc(); }
        }

        private bool _showSpotPriceLabel = true;
        [Display(GroupName = "07. 🎯 Spot Price", Name = "Show Spot Price Label", Order = 50)]
        public bool ShowSpotPriceLabel
        {
            get => _showSpotPriceLabel;
            set { _showSpotPriceLabel = value; RequestRecalc(); }
        }
        #endregion

        #region 08. Info Panel
        private bool _showInfoPanel = true;
        [Display(GroupName = "08. 📋 Info Panel", Name = "Show Info Panel", Order = 10)]
        public bool ShowInfoPanel
        {
            get => _showInfoPanel;
            set { _showInfoPanel = value; RequestRecalc(); }
        }

        public enum PanelPosition
        {
            TopLeft, TopCenter, TopRight,
            MiddleLeft, MiddleCenter, MiddleRight,
            BottomLeft, BottomCenter, BottomRight
        }

        private PanelPosition _infoPanelPosition = PanelPosition.TopRight;
        [Display(GroupName = "08. 📋 Info Panel", Name = "Panel Position", Order = 15)]
        public PanelPosition InfoPanelPos
        {
            get => _infoPanelPosition;
            set { _infoPanelPosition = value; RequestRecalc(); }
        }

        private int _infoPanelX = 10;
        [Display(GroupName = "08. 📋 Info Panel", Name = "X Offset (px)", Order = 20)]
        [Range(0, 5000)]
        public int InfoPanelX
        {
            get => _infoPanelX;
            set { _infoPanelX = Math.Clamp(value, 0, 5000); RequestRecalc(); }
        }

        private int _infoPanelY = 10;
        [Display(GroupName = "08. 📋 Info Panel", Name = "Y Offset (px)", Order = 30)]
        [Range(0, 5000)]
        public int InfoPanelY
        {
            get => _infoPanelY;
            set { _infoPanelY = Math.Clamp(value, 0, 5000); RequestRecalc(); }
        }

        private int _infoPanelFontSize = 11;
        [Display(GroupName = "08. 📋 Info Panel", Name = "Font Size", Order = 40)]
        [Range(8, 24)]
        public int InfoPanelFontSize
        {
            get => _infoPanelFontSize;
            set { _infoPanelFontSize = Math.Clamp(value, 8, 24); RequestRecalc(); }
        }

        private Color _infoPanelTextColor = Color.White;
        [Display(GroupName = "08. 📋 Info Panel", Name = "Text Color", Order = 50)]
        public Color InfoPanelTextColor
        {
            get => _infoPanelTextColor;
            set { _infoPanelTextColor = value; RequestRecalc(); }
        }

        private Color _infoPanelBackColor = Color.FromArgb(220, 20, 20, 25);
        [Display(GroupName = "08. 📋 Info Panel", Name = "Background Color", Order = 60)]
        public Color InfoPanelBackColor
        {
            get => _infoPanelBackColor;
            set { _infoPanelBackColor = value; RequestRecalc(); }
        }

        private Color _infoPanelBorderColor = Color.FromArgb(180, 60, 60, 70);
        [Display(GroupName = "08. 📋 Info Panel", Name = "Border Color", Order = 65)]
        public Color InfoPanelBorderColor
        {
            get => _infoPanelBorderColor;
            set { _infoPanelBorderColor = value; RequestRecalc(); }
        }

        private Color _infoPanelHeaderColor = Color.FromArgb(255, 0, 180, 220);
        [Display(GroupName = "08. 📋 Info Panel", Name = "Header Color", Order = 70)]
        public Color InfoPanelHeaderColor
        {
            get => _infoPanelHeaderColor;
            set { _infoPanelHeaderColor = value; RequestRecalc(); }
        }

        private Color _infoPanelAccentPositive = Color.FromArgb(255, 0, 200, 100);
        [Display(GroupName = "08. 📋 Info Panel", Name = "Positive Accent Color", Order = 75)]
        public Color InfoPanelAccentPositive
        {
            get => _infoPanelAccentPositive;
            set { _infoPanelAccentPositive = value; RequestRecalc(); }
        }

        private Color _infoPanelAccentNegative = Color.FromArgb(255, 220, 80, 80);
        [Display(GroupName = "08. 📋 Info Panel", Name = "Negative Accent Color", Order = 80)]
        public Color InfoPanelAccentNegative
        {
            get => _infoPanelAccentNegative;
            set { _infoPanelAccentNegative = value; RequestRecalc(); }
        }
        #endregion

        #region 09. Strike Filter
        private bool _enableStrikeFilter = false;
        [Display(GroupName = "09. 🎯 Strike Filter", Name = "Enable Strike Filter", Order = 10)]
        public bool EnableStrikeFilter
        {
            get => _enableStrikeFilter;
            set { _enableStrikeFilter = value; RequestRecalc(); }
        }

        public enum StrikeFilterMode { PercentFromSpot, FixedRange, StrikeCount }
        private StrikeFilterMode _strikeFilterMode = StrikeFilterMode.PercentFromSpot;
        [Display(GroupName = "09. 🎯 Strike Filter", Name = "Filter Mode", Order = 20)]
        public StrikeFilterMode FilterMode
        {
            get => _strikeFilterMode;
            set { _strikeFilterMode = value; RequestRecalc(); }
        }

        private decimal _strikeFilterPercent = 5m;
        [Display(GroupName = "09. 🎯 Strike Filter", Name = "Range (% from Spot)", Order = 30)]
        [Range(0.5, 50)]
        public decimal StrikeFilterPercent
        {
            get => _strikeFilterPercent;
            set { _strikeFilterPercent = Math.Clamp(value, 0.5m, 50m); RequestRecalc(); }
        }

        private decimal _strikeFilterMinPrice = 0m;
        [Display(GroupName = "09. 🎯 Strike Filter", Name = "Min Strike Price (0=auto)", Order = 40)]
        public decimal StrikeFilterMinPrice
        {
            get => _strikeFilterMinPrice;
            set { _strikeFilterMinPrice = Math.Max(0, value); RequestRecalc(); }
        }

        private decimal _strikeFilterMaxPrice = 0m;
        [Display(GroupName = "09. 🎯 Strike Filter", Name = "Max Strike Price (0=auto)", Order = 50)]
        public decimal StrikeFilterMaxPrice
        {
            get => _strikeFilterMaxPrice;
            set { _strikeFilterMaxPrice = Math.Max(0, value); RequestRecalc(); }
        }

        private int _strikeFilterCount = 40;
        [Display(GroupName = "09. 🎯 Strike Filter", Name = "Max Strikes to Show", Order = 60)]
        [Range(5, 200)]
        public int StrikeFilterCount
        {
            get => _strikeFilterCount;
            set { _strikeFilterCount = Math.Clamp(value, 5, 200); RequestRecalc(); }
        }

        private decimal _minValueThreshold = 0m;
        [Display(GroupName = "09. 🎯 Strike Filter", Name = "Min Absolute Value", Description = "Hide strikes below this absolute value", Order = 70)]
        public decimal MinValueThreshold
        {
            get => _minValueThreshold;
            set { _minValueThreshold = Math.Max(0, value); RequestRecalc(); }
        }
        #endregion

        #region 10. Data Export
        private bool _enableDataExport = false;
        [Display(GroupName = "10. 💾 Data Export", Name = "Enable Data Export", Order = 10)]
        public bool EnableDataExport
        {
            get => _enableDataExport;
            set { _enableDataExport = value; RequestRecalc(); }
        }

        private string _exportDirectory = string.Empty;
        [Display(GroupName = "10. 💾 Data Export", Name = "Export Directory", Description = "Folder path (empty = Desktop)", Order = 20)]
        public string ExportDirectory
        {
            get => _exportDirectory;
            set { _exportDirectory = value ?? string.Empty; RequestRecalc(); }
        }

        private string _exportFilePrefix = "DTE_Exposure";
        [Display(GroupName = "10. 💾 Data Export", Name = "File Name Prefix", Order = 30)]
        public string ExportFilePrefix
        {
            get => _exportFilePrefix;
            set { _exportFilePrefix = string.IsNullOrWhiteSpace(value) ? "DTE_Exposure" : value; RequestRecalc(); }
        }

        private bool _exportOnRefresh = false;
        [Display(GroupName = "10. 💾 Data Export", Name = "Auto-Export on Refresh", Order = 40)]
        public bool ExportOnRefresh
        {
            get => _exportOnRefresh;
            set { _exportOnRefresh = value; RequestRecalc(); }
        }

        private int _exportMaxFileSizeMB = 50;
        [Display(GroupName = "10. 💾 Data Export", Name = "Max File Size (MB)", Order = 50)]
        [Range(1, 500)]
        public int ExportMaxFileSizeMB
        {
            get => _exportMaxFileSizeMB;
            set { _exportMaxFileSizeMB = Math.Clamp(value, 1, 500); RequestRecalc(); }
        }

        private bool _showExportStatus = true;
        [Display(GroupName = "10. 💾 Data Export", Name = "Show Export Status", Order = 60)]
        public bool ShowExportStatus
        {
            get => _showExportStatus;
            set { _showExportStatus = value; RequestRecalc(); }
        }
        #endregion

        #region 11. API Diagnostics
        private bool _showApiDiagnostics = false;
        [Display(GroupName = "11. 🔌 API Diagnostics", Name = "Show Diagnostics Panel", Order = 10)]
        public bool ShowApiDiagnostics
        {
            get => _showApiDiagnostics;
            set { _showApiDiagnostics = value; RequestRecalc(); }
        }
        #endregion

        #region 12. Legend
        private bool _showLegend = true;
        [Display(GroupName = "12. 🎨 Legend", Name = "Show Expiration Legend", Order = 10)]
        public bool ShowLegend
        {
            get => _showLegend;
            set { _showLegend = value; RequestRecalc(); }
        }

        public enum LegendPosition { TopRight, TopLeft, BottomRight, BottomLeft }
        private LegendPosition _legendPosition = LegendPosition.TopRight;
        [Display(GroupName = "12. 🎨 Legend", Name = "Legend Position", Order = 20)]
        public LegendPosition LegendPos
        {
            get => _legendPosition;
            set { _legendPosition = value; RequestRecalc(); }
        }
        #endregion

        #region Constructor
        public DteExposureProfile()
        {
            EnableCustomDrawing = true;
            DenyToChangePanel = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);

            _httpClient.DefaultRequestHeaders.Add("User-Agent", "ATAS-DteExposureProfile/1.0");
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
            _refreshTimer.Interval = Math.Max(10, _refreshSeconds) * 1000;
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

        private async Task LoadDataAsync()
        {
            if (string.IsNullOrWhiteSpace(_apiToken))
            {
                lock (_sync)
                {
                    _error = "API Token is required";
                    _rawData = null;
                    _aggregatedStrikes.Clear();
                    _apiDiagnostics.IsConnected = false;
                }
                RedrawChart();
                return;
            }

            try
            {
                var symbolStr = _symbol.ToString();
                var url = $"https://nel.tutela2.org/trading/api/v1/options/exposure?symbol={symbolStr}";
                if (_dteFilter >= 0)
                    url += $"&dte={_dteFilter}";

                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _apiToken);

                _apiStopwatch.Restart();

                var response = await _httpClient.GetAsync(url);

                _apiStopwatch.Stop();
                double latencyMs = _apiStopwatch.Elapsed.TotalMilliseconds;
                UpdateLatencyStats(latencyMs);

                if (!response.IsSuccessStatusCode)
                {
                    var body = string.Empty;
                    try { body = await response.Content.ReadAsStringAsync(); } catch { }
                    lock (_sync)
                    {
                        _error = $"API Error: {(int)response.StatusCode} {response.StatusCode}";
                        if (!string.IsNullOrWhiteSpace(body))
                            _error += $" — {body.Substring(0, Math.Min(200, body.Length))}";
                        _rawData = null;
                        _aggregatedStrikes.Clear();
                        _apiDiagnostics.IsConnected = false;
                        _apiDiagnostics.ErrorCount++;
                        _apiDiagnostics.LastErrorTime = DateTime.Now;
                        _apiDiagnostics.LastErrorMessage = _error;
                    }
                    RedrawChart();
                    return;
                }

                var json = await response.Content.ReadAsStringAsync();
                var parsed = ParseResponse(json);

                if (parsed == null)
                {
                    lock (_sync)
                    {
                        _error = "Failed to parse API response";
                        _rawData = null;
                        _aggregatedStrikes.Clear();
                        _apiDiagnostics.IsConnected = false;
                        _apiDiagnostics.ErrorCount++;
                        _apiDiagnostics.LastErrorTime = DateTime.Now;
                        _apiDiagnostics.LastErrorMessage = "Parse error";
                    }
                    RedrawChart();
                    return;
                }

                var aggregated = AggregateStrikes(parsed);

                lock (_sync)
                {
                    _rawData = parsed;
                    _aggregatedStrikes = aggregated;
                    _error = string.Empty;
                    _lastLoad = DateTime.Now;
                    _apiDiagnostics.IsConnected = true;
                    _apiDiagnostics.SuccessCount++;
                    _apiDiagnostics.LastSuccessTime = DateTime.Now;
                }

                if (_enableDataExport && _exportOnRefresh)
                {
                    ExportData(parsed, aggregated);
                }

                RedrawChart();
            }
            catch (Exception ex)
            {
                _apiStopwatch.Stop();
                lock (_sync)
                {
                    _error = $"Load error: {ex.Message}";
                    _rawData = null;
                    _aggregatedStrikes.Clear();
                    _apiDiagnostics.IsConnected = false;
                    _apiDiagnostics.ErrorCount++;
                    _apiDiagnostics.LastErrorTime = DateTime.Now;
                    _apiDiagnostics.LastErrorMessage = ex.Message;
                }
                RedrawChart();
            }
        }

        private ExposureData? ParseResponse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var data = new ExposureData
                {
                    Symbol = root.TryGetProperty("symbol", out var sym) ? sym.GetString() ?? string.Empty : string.Empty,
                    DteFilter = root.TryGetProperty("dte", out var dteEl) && dteEl.ValueKind == JsonValueKind.Number ? dteEl.GetInt32() : null,
                    SpotPrice = root.TryGetProperty("spotPrice", out var sp) ? (decimal)sp.GetDouble() : 0m,
                    Timestamp = root.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? string.Empty : string.Empty,
                    TotalExpirations = root.TryGetProperty("totalExpirations", out var te) ? te.GetInt32() : 0
                };

                if (root.TryGetProperty("expirations", out var expsArr))
                {
                    foreach (var expEl in expsArr.EnumerateArray())
                    {
                        var expData = new ExpirationData
                        {
                            Expiration = expEl.TryGetProperty("expiration", out var expStr) ? expStr.GetString() ?? string.Empty : string.Empty,
                            Dte = expEl.TryGetProperty("dte", out var dteVal) ? dteVal.GetInt32() : 0
                        };

                        var strikesArr = expEl.TryGetProperty("strikes", out var sArr) ? sArr : default;
                        var netGammaArr = expEl.TryGetProperty("netGamma", out var ngArr) ? ngArr : default;

                        JsonElement callAbsDelta = default, callAbsGamma = default, callOI = default, callVol = default;
                        if (expEl.TryGetProperty("call", out var callEl))
                        {
                            callEl.TryGetProperty("absDelta", out callAbsDelta);
                            callEl.TryGetProperty("absGamma", out callAbsGamma);
                            callEl.TryGetProperty("openInterest", out callOI);
                            callEl.TryGetProperty("volume", out callVol);
                        }

                        JsonElement putAbsDelta = default, putAbsGamma = default, putOI = default, putVol = default;
                        if (expEl.TryGetProperty("put", out var putEl))
                        {
                            putEl.TryGetProperty("absDelta", out putAbsDelta);
                            putEl.TryGetProperty("absGamma", out putAbsGamma);
                            putEl.TryGetProperty("openInterest", out putOI);
                            putEl.TryGetProperty("volume", out putVol);
                        }

                        if (strikesArr.ValueKind == JsonValueKind.Array)
                        {
                            var strikes = strikesArr.EnumerateArray().ToList();
                            int count = strikes.Count;

                            for (int i = 0; i < count; i++)
                            {
                                var sd = new StrikeData
                                {
                                    Strike = decimal.TryParse(strikes[i].GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var sv) ? sv : 0m,
                                    NetGamma = GetArrayDecimal(netGammaArr, i),
                                    CallAbsDelta = GetArrayDecimal(callAbsDelta, i),
                                    CallAbsGamma = GetArrayDecimal(callAbsGamma, i),
                                    CallOI = GetArrayDecimal(callOI, i),
                                    CallVolume = GetArrayDecimal(callVol, i),
                                    PutAbsDelta = GetArrayDecimal(putAbsDelta, i),
                                    PutAbsGamma = GetArrayDecimal(putAbsGamma, i),
                                    PutOI = GetArrayDecimal(putOI, i),
                                    PutVolume = GetArrayDecimal(putVol, i)
                                };
                                expData.Strikes.Add(sd);
                            }
                        }

                        data.Expirations.Add(expData);
                    }
                }

                return data;
            }
            catch
            {
                return null;
            }
        }

        private static decimal GetArrayDecimal(JsonElement arr, int index)
        {
            if (arr.ValueKind != JsonValueKind.Array) return 0m;
            int len = arr.GetArrayLength();
            if (index >= len) return 0m;
            var el = arr[index];
            return el.ValueKind == JsonValueKind.Number ? (decimal)el.GetDouble() : 0m;
        }

        private List<AggregatedStrike> AggregateStrikes(ExposureData data)
        {
            var dict = new Dictionary<decimal, AggregatedStrike>();

            foreach (var exp in data.Expirations)
            {
                foreach (var s in exp.Strikes)
                {
                    if (!dict.TryGetValue(s.Strike, out var agg))
                    {
                        agg = new AggregatedStrike { Strike = s.Strike };
                        dict[s.Strike] = agg;
                    }
                    agg.NetGamma += s.NetGamma;
                    agg.CallDelta += s.CallAbsDelta;
                    agg.PutDelta += s.PutAbsDelta;
                    agg.CallGamma += s.CallAbsGamma;
                    agg.PutGamma += s.PutAbsGamma;
                    agg.CallOI += s.CallOI;
                    agg.PutOI += s.PutOI;
                    agg.CallVolume += s.CallVolume;
                    agg.PutVolume += s.PutVolume;
                }
            }

            return dict.Values.OrderBy(s => s.Strike).ToList();
        }

        private void UpdateLatencyStats(double latencyMs)
        {
            lock (_sync)
            {
                _apiDiagnostics.LastLatencyMs = latencyMs;
                _latencyHistory.Enqueue(latencyMs);
                while (_latencyHistory.Count > MaxLatencyHistory)
                    _latencyHistory.Dequeue();
                _apiDiagnostics.AverageLatencyMs = _latencyHistory.Average();
            }
        }
        #endregion

        #region Metric Helpers
        /// <summary>Returns (callValue, putValue) for a given strike within one expiration based on selected metric.</summary>
        private (decimal callVal, decimal putVal) GetMetricPair(StrikeData s)
        {
            return _displayMetric switch
            {
                DisplayMetric.DEX => (s.CallAbsDelta, s.PutAbsDelta),
                DisplayMetric.GEX => (s.CallAbsGamma, s.PutAbsGamma),
                DisplayMetric.OI => (s.CallOI, s.PutOI),
                DisplayMetric.Volume => (s.CallVolume, s.PutVolume),
                _ => (s.CallAbsDelta, s.PutAbsDelta)
            };
        }

        private string MetricLabel => _displayMetric switch
        {
            DisplayMetric.DEX => "ABS Delta Exposure",
            DisplayMetric.GEX => "ABS Gamma Exposure",
            DisplayMetric.OI => "Open Interest",
            DisplayMetric.Volume => "Volume",
            _ => "Exposure"
        };
        #endregion

        #region Rendering
        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo?.PriceChartContainer == null)
            {
                context.DrawString("Chart not ready", new RenderFont("Arial", 10), Color.Red, 10, 10);
                return;
            }

            ExposureData? rawSnapshot;
            List<AggregatedStrike> aggStrikes;
            string error;
            DateTime? lastLoad;
            lock (_sync)
            {
                rawSnapshot = _rawData;
                aggStrikes = _aggregatedStrikes.ToList();
                error = _error;
                lastLoad = _lastLoad;
            }

            if (!string.IsNullOrEmpty(error))
            {
                var errorFont = new RenderFont("Arial", 10);
                context.DrawString(error, errorFont, Color.OrangeRed, 10, 10);
            }

            if (rawSnapshot == null || rawSnapshot.Expirations.Count == 0)
            {
                if (string.IsNullOrEmpty(error))
                    context.DrawString("No exposure data", new RenderFont("Arial", 10), Color.Gray, 10, 10);
                return;
            }

            var fullWidth = ChartInfo.Region.Width;
            var xBase = ChartInfo.PriceChartContainer.GetXByBar(CurrentBar, false);
            var xCenter = xBase + _centerOffsetPx;

            // Conversion factor
            decimal factor = _conversionFactor;
            if (_enableConversion && _autoCalculateFactor && rawSnapshot.SpotPrice > 0 && _lastChartPrice > 0)
            {
                factor = _lastChartPrice / rawSnapshot.SpotPrice;
            }

            // Collect all unique strikes in visible range
            var allStrikes = new SortedSet<decimal>();
            foreach (var exp in rawSnapshot.Expirations)
                foreach (var s in exp.Strikes)
                    allStrikes.Add(s.Strike);

            var visibleStrikes = allStrikes.ToList();

            // Apply strike filter
            if (_enableStrikeFilter && rawSnapshot.SpotPrice > 0)
            {
                visibleStrikes = ApplyStrikeFilterSimple(visibleStrikes, rawSnapshot.SpotPrice);
            }

            if (visibleStrikes.Count == 0) return;

            // Build per-expiration color map
            var expirations = rawSnapshot.Expirations.OrderBy(e => e.Dte).ToList();
            var expColorMap = new Dictionary<string, Color>();
            for (int i = 0; i < expirations.Count; i++)
            {
                expColorMap[expirations[i].Expiration] = ExpirationColors[i % ExpirationColors.Length];
            }

            // Build stacked data: for each strike, accumulate call/put values per expiration
            // callSegments[strike] = list of (expiration, callValue)
            // putSegments[strike] = list of (expiration, putValue)
            var callSegments = new Dictionary<decimal, List<(string exp, decimal val, Color color)>>();
            var putSegments = new Dictionary<decimal, List<(string exp, decimal val, Color color)>>();
            decimal globalMax = 0;

            foreach (var strike in visibleStrikes)
            {
                callSegments[strike] = new List<(string, decimal, Color)>();
                putSegments[strike] = new List<(string, decimal, Color)>();

                decimal callTotal = 0, putTotal = 0;
                foreach (var exp in expirations)
                {
                    var sd = exp.Strikes.FirstOrDefault(s => s.Strike == strike);
                    if (sd == null) continue;

                    var (cv, pv) = GetMetricPair(sd);
                    if (cv > 0)
                    {
                        callSegments[strike].Add((exp.Expiration, cv, expColorMap[exp.Expiration]));
                        callTotal += cv;
                    }
                    if (pv > 0)
                    {
                        putSegments[strike].Add((exp.Expiration, pv, expColorMap[exp.Expiration]));
                        putTotal += pv;
                    }
                }
                globalMax = Math.Max(globalMax, Math.Max(callTotal, putTotal));
            }

            if (globalMax <= 0) globalMax = 1;
            double scale = _maxBarWidthPx / (double)globalMax;

            // Center line
            if (_showCenterLine)
            {
                var pen = new RenderPen(_centerLineColor, 1);
                context.DrawLine(pen, xCenter, 0, xCenter, ChartInfo.Region.Height);
            }

            // CALLS / PUTS headers
            if (_showCallPutLabels)
            {
                var headerFont = new RenderFont("Arial", 11);
                context.DrawString("CALLS", headerFont, Color.FromArgb(180, 180, 180, 180), xCenter - 60, 5);
                context.DrawString("PUTS", headerFont, Color.FromArgb(180, 180, 180, 180), xCenter + 15, 5);
            }

            var valueFont = new RenderFont("Arial", _valueFontSize);

            // Draw stacked bars for each strike
            foreach (var strike in visibleStrikes)
            {
                decimal chartPrice = _enableConversion ? RoundToStep(strike * factor, _priceStep) : strike;
                int y = ChartInfo.PriceChartContainer.GetYByPrice(chartPrice, false);
                int top = y - _barThicknessPx / 2;

                // === CALLS (left of center) ===
                int callOffset = 0;
                decimal callTotal = 0;
                foreach (var (exp, val, color) in callSegments[strike])
                {
                    int segWidth = (int)Math.Round((double)val * scale);
                    if (segWidth <= 0) continue;

                    var fillColor = Color.FromArgb(_fillOpacity, color);
                    var rect = new Rectangle(xCenter - callOffset - segWidth, top, segWidth, _barThicknessPx);
                    context.FillRectangle(fillColor, rect);

                    if (_showBarOutline)
                        context.DrawRectangle(new RenderPen(Color.FromArgb(60, 0, 0, 0), 1), rect);

                    callOffset += segWidth;
                    callTotal += val;
                }

                // === PUTS (right of center) ===
                int putOffset = 0;
                decimal putTotal = 0;
                foreach (var (exp, val, color) in putSegments[strike])
                {
                    int segWidth = (int)Math.Round((double)val * scale);
                    if (segWidth <= 0) continue;

                    var fillColor = Color.FromArgb(_fillOpacity, color);
                    var rect = new Rectangle(xCenter + putOffset, top, segWidth, _barThicknessPx);
                    context.FillRectangle(fillColor, rect);

                    if (_showBarOutline)
                        context.DrawRectangle(new RenderPen(Color.FromArgb(60, 0, 0, 0), 1), rect);

                    putOffset += segWidth;
                    putTotal += val;
                }

                // Value labels (total call on left edge, total put on right edge)
                if (_showValues)
                {
                    if (callTotal > 0)
                    {
                        var callText = FormatCompact(callTotal);
                        int ctw = EstimateTextWidth(callText, valueFont);
                        int callValX = xCenter - callOffset - ctw - _valueOffsetPx;
                        context.DrawString(callText, valueFont, _valueTextColor, callValX, top);
                    }
                    if (putTotal > 0)
                    {
                        int putValX = xCenter + putOffset + _valueOffsetPx;
                        context.DrawString(FormatCompact(putTotal), valueFont, _valueTextColor, putValX, top);
                    }
                }

                // Strike label (left side, beyond value)
                if (_showStrikeLabels)
                {
                    var strikeText = $"${strike:0.00}";
                    int tw = EstimateTextWidth(strikeText, valueFont);
                    int valWidth = _showValues && callTotal > 0 ? EstimateTextWidth(FormatCompact(callTotal), valueFont) + _valueOffsetPx * 2 : 0;
                    int labelX = xCenter - callOffset - valWidth - tw - _valueOffsetPx;
                    context.DrawString(strikeText, valueFont, _strikeTextColor, labelX, top);
                }
            }

            // Spot price line
            if (_showSpotPriceLine && rawSnapshot.SpotPrice > 0)
            {
                DrawSpotPriceLine(context, rawSnapshot.SpotPrice, factor, fullWidth, xCenter);
            }

            // Major levels
            DrawMajorLevels(context, aggStrikes, rawSnapshot.SpotPrice, factor, fullWidth);

            // Legend
            if (_showLegend && expirations.Count > 0)
            {
                DrawLegend(context, expirations, expColorMap);
            }

            // Info panel
            if (_showInfoPanel)
            {
                DrawInfoPanel(context, rawSnapshot, aggStrikes, lastLoad);
            }

            // Diagnostics
            if (_showApiDiagnostics)
            {
                DrawDiagnosticsPanel(context);
            }

            // Export status
            if (_enableDataExport && _showExportStatus)
            {
                DrawExportStatus(context);
            }
        }
        #endregion

        #region Drawing Helpers
        private void DrawMajorLevels(RenderContext context, List<AggregatedStrike> strikes, decimal spotPrice, decimal factor, int fullWidth)
        {
            if (strikes.Count == 0) return;
            var labelFont = new RenderFont("Arial", 9);

            // Positive gamma ranked (calls)
            var posRanked = strikes.Where(s => s.NetGamma > 0).OrderByDescending(s => s.NetGamma).ToList();
            // Negative gamma ranked (puts)
            var negRanked = strikes.Where(s => s.NetGamma < 0).OrderBy(s => s.NetGamma).ToList();

            if (_showMaxPosLine && posRanked.Count > 0)
            {
                var maxPos = posRanked[0];
                var price = _enableConversion ? RoundToStep(maxPos.Strike * factor, _priceStep) : maxPos.Strike;
                var y = ChartInfo.PriceChartContainer.GetYByPrice(price, false);
                var pen = new RenderPen(_maxPosColor, _majorLinesThickness) { DashStyle = _majorLinesDash };
                context.DrawLine(pen, 0, y, fullWidth, y);
                if (_showMajorLabels)
                    context.DrawString($"MAX+: {maxPos.Strike:0}", labelFont, _maxPosColor, 5, y - 12);
            }

            // Call P1-P3 (2nd, 3rd, 4th highest positive gamma)
            if (_showCallP1P2P3)
            {
                Color[] callPColors = { _callP1Color, _callP2Color, _callP3Color };
                string[] callPLabels = { "P1+", "P2+", "P3+" };
                for (int i = 0; i < 3; i++)
                {
                    int rank = i + 1; // skip index 0 (MAX+)
                    if (rank >= posRanked.Count) break;
                    var s = posRanked[rank];
                    var price = _enableConversion ? RoundToStep(s.Strike * factor, _priceStep) : s.Strike;
                    var y = ChartInfo.PriceChartContainer.GetYByPrice(price, false);
                    var pen = new RenderPen(callPColors[i], _callPLinesThickness) { DashStyle = _callPLinesDash };
                    context.DrawLine(pen, 0, y, fullWidth, y);
                    if (_showP1P2P3Labels)
                        context.DrawString($"{callPLabels[i]}: {s.Strike:0}", labelFont, callPColors[i], 5, y - 12);
                }
            }

            if (_showMaxNegLine && negRanked.Count > 0)
            {
                var maxNeg = negRanked[0];
                var price = _enableConversion ? RoundToStep(maxNeg.Strike * factor, _priceStep) : maxNeg.Strike;
                var y = ChartInfo.PriceChartContainer.GetYByPrice(price, false);
                var pen = new RenderPen(_maxNegColor, _majorLinesThickness) { DashStyle = _majorLinesDash };
                context.DrawLine(pen, 0, y, fullWidth, y);
                if (_showMajorLabels)
                    context.DrawString($"MAX-: {maxNeg.Strike:0}", labelFont, _maxNegColor, 5, y - 12);
            }

            // Put P1-P3 (2nd, 3rd, 4th most negative gamma)
            if (_showPutP1P2P3)
            {
                Color[] putPColors = { _putP1Color, _putP2Color, _putP3Color };
                string[] putPLabels = { "P1-", "P2-", "P3-" };
                for (int i = 0; i < 3; i++)
                {
                    int rank = i + 1; // skip index 0 (MAX-)
                    if (rank >= negRanked.Count) break;
                    var s = negRanked[rank];
                    var price = _enableConversion ? RoundToStep(s.Strike * factor, _priceStep) : s.Strike;
                    var y = ChartInfo.PriceChartContainer.GetYByPrice(price, false);
                    var pen = new RenderPen(putPColors[i], _putPLinesThickness) { DashStyle = _putPLinesDash };
                    context.DrawLine(pen, 0, y, fullWidth, y);
                    if (_showP1P2P3Labels)
                        context.DrawString($"{putPLabels[i]}: {s.Strike:0}", labelFont, putPColors[i], 5, y - 12);
                }
            }

            if (_showZeroGammaLine)
            {
                decimal zeroGamma = CalculateZeroGamma(strikes, spotPrice);
                if (zeroGamma > 0)
                {
                    var price = _enableConversion ? RoundToStep(zeroGamma * factor, _priceStep) : zeroGamma;
                    var y = ChartInfo.PriceChartContainer.GetYByPrice(price, false);
                    var pen = new RenderPen(_zeroGammaColor, _majorLinesThickness) { DashStyle = DashStyle.Dash };
                    context.DrawLine(pen, 0, y, fullWidth, y);
                    if (_showMajorLabels)
                        context.DrawString($"ZG: {zeroGamma:0.00}", labelFont, _zeroGammaColor, 5, y - 12);
                }
            }
        }

        private decimal CalculateZeroGamma(List<AggregatedStrike> strikes, decimal spotPrice)
        {
            decimal bestCross = 0;
            decimal bestDist = decimal.MaxValue;

            for (int i = 0; i < strikes.Count - 1; i++)
            {
                var a = strikes[i];
                var b = strikes[i + 1];
                if ((a.NetGamma >= 0 && b.NetGamma < 0) || (a.NetGamma < 0 && b.NetGamma >= 0))
                {
                    decimal range = b.NetGamma - a.NetGamma;
                    if (range == 0) continue;
                    decimal t = -a.NetGamma / range;
                    decimal crossStrike = a.Strike + t * (b.Strike - a.Strike);
                    decimal dist = Math.Abs(crossStrike - spotPrice);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestCross = crossStrike;
                    }
                }
            }
            return bestCross;
        }

        private void DrawSpotPriceLine(RenderContext context, decimal spotPrice, decimal factor, int fullWidth, int xCenter)
        {
            decimal chartSpotPrice = _enableConversion ? RoundToStep(spotPrice * factor, _priceStep) : spotPrice;
            int spotY = ChartInfo.PriceChartContainer.GetYByPrice(chartSpotPrice, false);

            var spotPen = new RenderPen(_spotPriceLineColor, _spotPriceLineThickness) { DashStyle = _spotPriceLineDash };
            context.DrawLine(spotPen, 0, spotY, fullWidth, spotY);

            if (_showSpotPriceLabel)
            {
                var labelFont = new RenderFont("Arial", 9);
                var labelText = $"SPOT PRICE: ${spotPrice:0.00}";
                int labelWidth = EstimateTextWidth(labelText, labelFont) + 8;
                int labelX = xCenter - labelWidth / 2;
                int labelY = spotY - 14;

                var labelBgRect = new Rectangle(labelX - 2, labelY - 1, labelWidth + 4, 16);
                context.FillRectangle(Color.FromArgb(200, 20, 20, 25), labelBgRect);
                context.DrawRectangle(new RenderPen(_spotPriceLineColor, 1), labelBgRect);
                context.DrawString(labelText, labelFont, _spotPriceLineColor, labelX, labelY);
            }
        }

        private void DrawLegend(RenderContext context, List<ExpirationData> expirations, Dictionary<string, Color> colorMap)
        {
            var font = new RenderFont("Arial", 10);
            int lineHeight = 16;
            int boxSize = 12;
            int padding = 8;
            int legendWidth = 140;
            int legendHeight = expirations.Count * lineHeight + padding * 2;

            int x, y;
            switch (_legendPosition)
            {
                case LegendPosition.TopLeft:
                    x = 10; y = 25;
                    break;
                case LegendPosition.BottomLeft:
                    x = 10; y = ChartInfo.Region.Height - legendHeight - 10;
                    break;
                case LegendPosition.BottomRight:
                    x = ChartInfo.Region.Width - legendWidth - 10;
                    y = ChartInfo.Region.Height - legendHeight - 10;
                    break;
                default: // TopRight
                    x = ChartInfo.Region.Width - legendWidth - 10;
                    y = 25;
                    break;
            }

            var bgRect = new Rectangle(x, y, legendWidth, legendHeight);
            context.FillRectangle(Color.FromArgb(200, 20, 20, 25), bgRect);
            context.DrawRectangle(new RenderPen(Color.FromArgb(100, 80, 80, 90), 1), bgRect);

            int ty = y + padding;
            foreach (var exp in expirations)
            {
                var color = colorMap[exp.Expiration];

                // Color box
                var boxRect = new Rectangle(x + padding, ty + 1, boxSize, boxSize);
                context.FillRectangle(color, boxRect);

                // Label
                context.DrawString(exp.Expiration, font, color, x + padding + boxSize + 6, ty);

                ty += lineHeight;
            }
        }

        private void DrawInfoPanel(RenderContext context, ExposureData data, List<AggregatedStrike> strikes, DateTime? lastLoad)
        {
            var font = new RenderFont("Arial", _infoPanelFontSize);
            var fontBold = new RenderFont("Arial", _infoPanelFontSize + 1);
            var fontSmall = new RenderFont("Arial", _infoPanelFontSize - 1);
            var lineHeight = _infoPanelFontSize + 6;
            var sectionGap = 8;

            int panelWidth = 280;
            int col1 = 100;

            // Title line
            var titleText = $"${data.Symbol} {MetricLabel} ({(_dteFilter >= 0 ? $"{_dteFilter} DTE" : "All DTE")})";

            int numRows = 16;
            int panelHeight = lineHeight * numRows + sectionGap * 5 + 25;

            var (x, y) = CalcPanelXY(_infoPanelPosition, panelWidth, panelHeight, _infoPanelX, _infoPanelY);

            var backRect = new Rectangle(x, y, panelWidth, panelHeight);
            context.FillRectangle(_infoPanelBackColor, backRect);
            context.DrawRectangle(new RenderPen(_infoPanelBorderColor, 1), backRect);

            int ty = y + 8;
            int labelX = x + 10;
            int valueX = x + col1 + 15;

            // ═══ HEADER ═══
            DrawSectionHeader(context, titleText, x, ty, panelWidth, fontBold, _infoPanelHeaderColor);
            ty += lineHeight + 4;

            context.DrawString("symbol", font, Color.Gray, labelX, ty);
            context.DrawString(data.Symbol, font, _infoPanelTextColor, valueX, ty);
            ty += lineHeight;

            context.DrawString("updated", font, Color.Gray, labelX, ty);
            var dateStr = lastLoad.HasValue ? lastLoad.Value.ToString("M/d/yyyy h:mm:ss tt") : "-";
            context.DrawString(dateStr, font, _infoPanelTextColor, valueX, ty);
            ty += lineHeight;

            context.DrawString("spot", font, Color.Gray, labelX, ty);
            context.DrawString($"${data.SpotPrice:0.00}", font, _infoPanelAccentPositive, valueX, ty);
            ty += lineHeight;

            context.DrawString("expirations", font, Color.Gray, labelX, ty);
            context.DrawString($"{data.Expirations.Count}", font, _infoPanelTextColor, valueX, ty);
            ty += lineHeight;

            context.DrawString("metric", font, Color.Gray, labelX, ty);
            context.DrawString(MetricLabel, font, Color.Gold, valueX, ty);
            ty += lineHeight + sectionGap;

            // ═══ NET GAMMA ═══
            DrawSectionHeader(context, "net gamma", x, ty, panelWidth, fontBold, _infoPanelHeaderColor);
            ty += lineHeight + 4;

            decimal posSum = strikes.Where(s => s.NetGamma > 0).Sum(s => s.NetGamma);
            decimal negSum = strikes.Where(s => s.NetGamma < 0).Sum(s => s.NetGamma);
            decimal netSum = posSum + negSum;

            context.DrawString("net GEX", font, Color.Gray, labelX, ty);
            var netColor = netSum >= 0 ? _infoPanelAccentPositive : _infoPanelAccentNegative;
            context.DrawString(FormatCompact(netSum), font, netColor, valueX, ty);
            ty += lineHeight;

            var maxPosStrike = strikes.Where(s => s.NetGamma > 0).OrderByDescending(s => s.NetGamma).FirstOrDefault();
            var maxNegStrike = strikes.Where(s => s.NetGamma < 0).OrderBy(s => s.NetGamma).FirstOrDefault();

            if (maxPosStrike != null)
            {
                context.DrawString("max pos", font, _maxPosColor, labelX, ty);
                context.DrawString($"${maxPosStrike.Strike:0}  ({FormatCompact(maxPosStrike.NetGamma)})", font, _maxPosColor, valueX, ty);
                ty += lineHeight;
            }

            if (maxNegStrike != null)
            {
                context.DrawString("max neg", font, _maxNegColor, labelX, ty);
                context.DrawString($"${maxNegStrike.Strike:0}  ({FormatCompact(maxNegStrike.NetGamma)})", font, _maxNegColor, valueX, ty);
                ty += lineHeight;
            }

            decimal zeroGamma = CalculateZeroGamma(strikes, data.SpotPrice);
            if (zeroGamma > 0)
            {
                context.DrawString("zero gamma", font, _zeroGammaColor, labelX, ty);
                context.DrawString($"${zeroGamma:0.00}", font, _zeroGammaColor, valueX, ty);
                ty += lineHeight;
            }

            ty += sectionGap;

            // ═══ TOTALS ═══
            DrawSectionHeader(context, "totals", x, ty, panelWidth, fontBold, _infoPanelHeaderColor);
            ty += lineHeight + 4;

            decimal totalCallOI = strikes.Sum(s => s.CallOI);
            decimal totalPutOI = strikes.Sum(s => s.PutOI);
            decimal totalCallVol = strikes.Sum(s => s.CallVolume);
            decimal totalPutVol = strikes.Sum(s => s.PutVolume);

            context.DrawString("call OI", font, Color.Gray, labelX, ty);
            context.DrawString(FormatCompact(totalCallOI), font, _infoPanelAccentPositive, valueX, ty);
            ty += lineHeight;

            context.DrawString("put OI", font, Color.Gray, labelX, ty);
            context.DrawString(FormatCompact(totalPutOI), font, _infoPanelAccentNegative, valueX, ty);
            ty += lineHeight;

            context.DrawString("call vol", font, Color.Gray, labelX, ty);
            context.DrawString(FormatCompact(totalCallVol), font, _infoPanelAccentPositive, valueX, ty);
            ty += lineHeight;

            context.DrawString("put vol", font, Color.Gray, labelX, ty);
            context.DrawString(FormatCompact(totalPutVol), font, _infoPanelAccentNegative, valueX, ty);
            ty += lineHeight;

            decimal pcRatio = totalPutOI > 0 ? totalCallOI / totalPutOI : 0;
            context.DrawString("C/P ratio", font, Color.Gray, labelX, ty);
            context.DrawString($"{pcRatio:0.00}", font, Color.Yellow, valueX, ty);
        }

        private void DrawDiagnosticsPanel(RenderContext context)
        {
            ApiDiagnostics diag;
            lock (_sync) { diag = _apiDiagnostics; }

            var font = new RenderFont("Arial", 9);
            var fontBold = new RenderFont("Arial", 10);
            var lineHeight = 14;

            int panelWidth = 200;
            int panelHeight = lineHeight * 6 + 20;

            var (x, y) = CalcPanelXY(PanelPosition.BottomLeft, panelWidth, panelHeight, 10, 10);

            var backRect = new Rectangle(x, y, panelWidth, panelHeight);
            context.FillRectangle(Color.FromArgb(220, 20, 20, 30), backRect);
            context.DrawRectangle(new RenderPen(Color.FromArgb(150, 80, 80, 100), 1), backRect);

            int ty = y + 5;
            int labelX = x + 8;
            int valueX = x + 100;

            var headerColor = diag.IsConnected ? Color.FromArgb(255, 0, 200, 100) : Color.FromArgb(255, 200, 80, 80);
            context.DrawString("API Diagnostics", fontBold, headerColor, labelX, ty);
            ty += lineHeight + 4;

            var statusText = diag.IsConnected ? "Connected" : "Disconnected";
            context.DrawString("Status", font, Color.Gray, labelX, ty);
            context.DrawString(statusText, font, headerColor, valueX, ty);
            ty += lineHeight;

            context.DrawString("Latency", font, Color.Gray, labelX, ty);
            context.DrawString($"{diag.LastLatencyMs:0}ms", font, Color.LightGray, valueX, ty);
            ty += lineHeight;

            context.DrawString("Avg Latency", font, Color.Gray, labelX, ty);
            context.DrawString($"{diag.AverageLatencyMs:0}ms", font, Color.LightGray, valueX, ty);
            ty += lineHeight;

            context.DrawString("Success/Errors", font, Color.Gray, labelX, ty);
            var errColor = diag.ErrorCount > 0 ? Color.OrangeRed : Color.LightGray;
            context.DrawString($"{diag.SuccessCount}/{diag.ErrorCount}", font, errColor, valueX, ty);

            if (!string.IsNullOrEmpty(diag.LastErrorMessage))
            {
                ty += lineHeight;
                var msg = diag.LastErrorMessage.Length > 40 ? diag.LastErrorMessage.Substring(0, 40) + "…" : diag.LastErrorMessage;
                context.DrawString(msg, font, Color.OrangeRed, labelX, ty);
            }
        }

        private void DrawSectionHeader(RenderContext context, string title, int x, int y, int width, RenderFont font, Color color)
        {
            int headerHeight = (int)font.Size + 6;
            var headerRect = new Rectangle(x, y, width, headerHeight);
            context.FillRectangle(Color.FromArgb(40, color), headerRect);
            context.DrawString(title, font, color, x + 10, y + 2);

            int lineY = y + (int)font.Size + 5;
            var linePen = new RenderPen(Color.FromArgb(60, color), 1);
            context.DrawLine(linePen, x + 5, lineY, x + width - 5, lineY);
        }

        private void DrawExportStatus(RenderContext context)
        {
            var font = new RenderFont("Arial", 9);
            var fontSmall = new RenderFont("Arial", 8);
            int panelWidth = 180;
            int panelHeight = 50;
            int x = 10;
            int y = ChartInfo.Region.Height - panelHeight - 10;

            var bgRect = new Rectangle(x, y, panelWidth, panelHeight);
            context.FillRectangle(Color.FromArgb(200, 30, 30, 35), bgRect);
            context.DrawRectangle(new RenderPen(Color.FromArgb(150, 80, 80, 90), 1), bgRect);

            int ty = y + 5;
            int labelX = x + 8;

            var headerColor = !string.IsNullOrEmpty(_lastExportError) ? Color.OrangeRed : Color.FromArgb(255, 100, 200, 255);
            context.DrawString("Data Export", font, headerColor, labelX, ty);
            ty += 14;

            if (!string.IsNullOrEmpty(_lastExportError))
            {
                context.DrawString($"Error: {_lastExportError}", fontSmall, Color.OrangeRed, labelX, ty);
            }
            else if (_lastExportTime.HasValue)
            {
                var elapsed = (DateTime.Now - _lastExportTime.Value).TotalSeconds;
                var statusText = elapsed < 5 ? "Exported" : $"Last: {_lastExportTime.Value:HH:mm:ss}";
                var statusColor = elapsed < 5 ? Color.LimeGreen : Color.Gray;
                context.DrawString(statusText, fontSmall, statusColor, labelX, ty);
                ty += 12;
                context.DrawString($"Count: {_exportCount}", fontSmall, Color.DimGray, labelX, ty);
            }
            else
            {
                context.DrawString("Ready", fontSmall, Color.Gray, labelX, ty);
            }
        }

        private (int x, int y) CalcPanelXY(PanelPosition pos, int panelWidth, int panelHeight, int offsetX, int offsetY)
        {
            int chartW = ChartInfo.Region.Width;
            int chartH = ChartInfo.Region.Height;

            int x = pos switch
            {
                PanelPosition.TopLeft or PanelPosition.MiddleLeft or PanelPosition.BottomLeft => offsetX,
                PanelPosition.TopCenter or PanelPosition.MiddleCenter or PanelPosition.BottomCenter => (chartW - panelWidth) / 2 + offsetX,
                PanelPosition.TopRight or PanelPosition.MiddleRight or PanelPosition.BottomRight => chartW - panelWidth - offsetX,
                _ => offsetX
            };

            int y = pos switch
            {
                PanelPosition.TopLeft or PanelPosition.TopCenter or PanelPosition.TopRight => offsetY,
                PanelPosition.MiddleLeft or PanelPosition.MiddleCenter or PanelPosition.MiddleRight => (chartH - panelHeight) / 2 + offsetY,
                PanelPosition.BottomLeft or PanelPosition.BottomCenter or PanelPosition.BottomRight => chartH - panelHeight - offsetY,
                _ => offsetY
            };

            return (x, y);
        }
        #endregion

        #region Export
        private string GetExportDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_exportDirectory) && Directory.Exists(_exportDirectory))
                return _exportDirectory;
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        private void ExportData(ExposureData data, List<AggregatedStrike> strikes)
        {
            if (!_enableDataExport || data == null) return;

            try
            {
                var dir = GetExportDirectory();
                var dateStamp = DateTime.Now.ToString("yyyyMMdd");
                var fileName = $"{_exportFilePrefix}_{data.Symbol}_{dateStamp}.csv";
                var filePath = Path.Combine(dir, fileName);

                bool fileExists = File.Exists(filePath);

                if (fileExists)
                {
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.Length > _exportMaxFileSizeMB * 1024 * 1024)
                    {
                        var rotatedPath = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(filePath)}_rotated_{DateTime.Now:HHmmss}.csv");
                        File.Move(filePath, rotatedPath);
                        fileExists = false;
                    }
                }

                var sb = new System.Text.StringBuilder();
                var currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                if (!fileExists)
                {
                    sb.AppendLine("Timestamp,Symbol,Strike,Spot,NetGamma,CallDelta,PutDelta,CallGamma,PutGamma,CallOI,PutOI,CallVol,PutVol");
                }

                foreach (var s in strikes)
                {
                    sb.AppendLine(string.Join(",",
                        currentTime,
                        data.Symbol,
                        s.Strike.ToString("0", CultureInfo.InvariantCulture),
                        data.SpotPrice.ToString("0.00", CultureInfo.InvariantCulture),
                        s.NetGamma.ToString("0.00", CultureInfo.InvariantCulture),
                        s.CallDelta.ToString("0.00", CultureInfo.InvariantCulture),
                        s.PutDelta.ToString("0.00", CultureInfo.InvariantCulture),
                        s.CallGamma.ToString("0.00", CultureInfo.InvariantCulture),
                        s.PutGamma.ToString("0.00", CultureInfo.InvariantCulture),
                        s.CallOI.ToString("0", CultureInfo.InvariantCulture),
                        s.PutOI.ToString("0", CultureInfo.InvariantCulture),
                        s.CallVolume.ToString("0", CultureInfo.InvariantCulture),
                        s.PutVolume.ToString("0", CultureInfo.InvariantCulture)
                    ));
                }

                File.AppendAllText(filePath, sb.ToString());

                _lastExportTime = DateTime.Now;
                _lastExportFile = filePath;
                _lastExportError = string.Empty;
                _exportCount++;
            }
            catch (Exception ex)
            {
                _lastExportError = ex.Message;
            }
        }
        #endregion

        #region Strike Filter
        private List<decimal> ApplyStrikeFilterSimple(List<decimal> strikes, decimal spotPrice)
        {
            if (strikes.Count == 0 || spotPrice <= 0) return strikes;

            IEnumerable<decimal> filtered = strikes;

            switch (_strikeFilterMode)
            {
                case StrikeFilterMode.PercentFromSpot:
                    decimal minS = spotPrice * (1 - _strikeFilterPercent / 100m);
                    decimal maxS = spotPrice * (1 + _strikeFilterPercent / 100m);
                    filtered = filtered.Where(s => s >= minS && s <= maxS);
                    break;

                case StrikeFilterMode.FixedRange:
                    if (_strikeFilterMinPrice > 0)
                        filtered = filtered.Where(s => s >= _strikeFilterMinPrice);
                    if (_strikeFilterMaxPrice > 0)
                        filtered = filtered.Where(s => s <= _strikeFilterMaxPrice);
                    break;

                case StrikeFilterMode.StrikeCount:
                    filtered = filtered
                        .OrderBy(s => Math.Abs(s - spotPrice))
                        .Take(_strikeFilterCount);
                    break;
            }

            return filtered.OrderBy(s => s).ToList();
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
            if (abs >= 1_000_000_000m) { suffix = "B"; num = value / 1_000_000_000m; }
            else if (abs >= 1_000_000m) { suffix = "M"; num = value / 1_000_000m; }
            else if (abs >= 1_000m) { suffix = "K"; num = value / 1_000m; }
            else { return value.ToString("0.##", CultureInfo.CurrentCulture); }
            return num.ToString("0.##", CultureInfo.CurrentCulture) + suffix;
        }

        private static int EstimateTextWidth(string text, RenderFont font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return (int)Math.Ceiling(text.Length * (font.Size * 0.58));
        }
        #endregion
    }
}
