using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace ATAS.Indicators.Technical
{
    /// <summary>
    /// GexBot Classic Profile - Advanced Gamma Exposure (GEX) Visualization Indicator
    /// 
    /// This indicator displays real-time GEX data from the GexBot API, providing:
    /// • Visual GEX profile with positive/negative bars at each strike
    /// • Key levels: Zero Gamma, Major Positive, Major Negative strikes
    /// • Multi-timeframe comparison (1/5/10/15/30 min)
    /// • Alerts for significant GEX changes with sound notifications
    /// • GEX clusters detection for Support/Resistance identification
    /// • Statistical analysis with GEX Score (-100 to +100)
    /// • Price targets based on gamma levels
    /// • Session awareness with 0DTE highlighting
    /// • Data export to CSV/TXT
    /// 
    /// Configuration is organized into 23 sections across 5 categories:
    /// 📁 CONFIGURATION: API, Price Conversion
    /// 📁 VISUALIZATION: Position, Bars, Labels, Levels, Panels, Grid
    /// 📁 ANALYSIS: Alerts, Arrows, Zones, Sparklines, History
    /// 📁 TOOLS: Sound, Filter, MTF, Export, Clusters, Statistics, Targets
    /// 📁 ADVANCED: Session, Themes, Diagnostics
    /// 
    /// Requires a valid GexBot API key. See README.md for detailed documentation.
    /// </summary>
    /// <remarks>
    /// Version: 2.0 Beta 2
    /// Compatible with: QQQ, SPY, SPX, NDX, NQ, ES, and major tech stocks
    /// </remarks>
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

        private class AlertInfo
        {
            public decimal Strike { get; set; }
            public decimal CurrentGex { get; set; }
            public decimal PriorGex { get; set; }
            public decimal Change { get; set; }
            public decimal ChangePercent { get; set; }
            public bool IsIncrease { get; set; }
            public string TimePeriod { get; set; } = string.Empty;
        }

        private class AlertHistoryEntry
        {
            public DateTime Timestamp { get; set; }
            public decimal Strike { get; set; }
            public decimal Change { get; set; }
            public decimal ChangePercent { get; set; }
            public bool IsIncrease { get; set; }
            public string TimePeriod { get; set; } = string.Empty;
        }

        private class GexCluster
        {
            public decimal CenterStrike { get; set; }
            public decimal StartStrike { get; set; }
            public decimal EndStrike { get; set; }
            public decimal TotalGex { get; set; }
            public decimal AverageGex { get; set; }
            public int StrikeCount { get; set; }
            public bool IsPositive { get; set; }
            public decimal Strength { get; set; } // 0-100 normalized strength
        }

        private class GexStatistics
        {
            public decimal Mean { get; set; }
            public decimal StdDev { get; set; }
            public decimal Min { get; set; }
            public decimal Max { get; set; }
            public decimal P25 { get; set; }
            public decimal P50 { get; set; }
            public decimal P75 { get; set; }
            public decimal PositiveSum { get; set; }
            public decimal NegativeSum { get; set; }
            public int PositiveCount { get; set; }
            public int NegativeCount { get; set; }
            public decimal PosNegRatio { get; set; }
            public decimal GexScore { get; set; } // -100 to +100 score
        }

        private class PriceTarget
        {
            public decimal Price { get; set; }
            public string Name { get; set; } = string.Empty;
            public decimal Distance { get; set; }
            public decimal DistancePercent { get; set; }
            public decimal Attraction { get; set; } // Strength of attraction 0-100
            public Color Color { get; set; }
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
            public List<(DateTime Time, string Message)> ActivityLog { get; set; } = new();
            public bool IsConnected { get; set; }
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
        private List<AlertInfo> _activeAlerts = new();
        private List<AlertHistoryEntry> _alertHistory = new();
        private HashSet<string> _alertHistoryKeys = new(); // To avoid duplicates
        private DateTime _lastPulseTime = DateTime.Now;
        private int _totalStrikesCount;
        private int _filteredStrikesCount;

        // Section 18: GEX Clusters
        private List<GexCluster> _gexClusters = new();

        // Section 19: Statistics
        private GexStatistics? _gexStatistics;

        // Section 20: Price Targets
        private List<PriceTarget> _priceTargets = new();

        // Section 23: API Diagnostics
        private ApiDiagnostics _apiDiagnostics = new();
        private System.Diagnostics.Stopwatch _apiStopwatch = new();
        private readonly Queue<double> _latencyHistory = new();
        private const int MaxLatencyHistory = 20;
        #endregion

        #region API Settings
        private string _apiKey = string.Empty;
        /// <summary>API Key for GexBot service authentication</summary>
        [Display(GroupName = "01. 🔑 API Configuration", Name = "API Key", Description = "Your GexBot API key for authentication", Order = 10)]
        public string ApiKey
        {
            get => _apiKey;
            set { _apiKey = value ?? string.Empty; ForceReload(); }
        }

        public enum TickerType { QQQ, SPY, SPX, NDX, NQ_NDX, ES_SPX, AAPL, NVDA, TSLA, AMD, AMZN, META, MSFT, GOOGL }
        private TickerType _ticker = TickerType.QQQ;
        [Display(GroupName = "01. 🔑 API Configuration", Name = "Ticker", Description = "Symbol to fetch GEX data for", Order = 20)]
        public TickerType Ticker
        {
            get => _ticker;
            set { _ticker = value; ForceReload(); }
        }

        public enum AggregationPeriod { full, zero, one }
        private AggregationPeriod _aggregation = AggregationPeriod.zero;
        [Display(GroupName = "01. 🔑 API Configuration", Name = "Aggregation (DTE)", Description = "Days to expiration filter: full=all, zero=0DTE, one=1DTE", Order = 30)]
        public AggregationPeriod Aggregation
        {
            get => _aggregation;
            set { _aggregation = value; ForceReload(); }
        }

        private int _refreshSeconds = 60;
        [Display(GroupName = "01. 🔑 API Configuration", Name = "Refresh Interval (sec)", Description = "Data refresh interval in seconds", Order = 40)]
        [Range(1, 3600)]
        public int RefreshSeconds
        {
            get => _refreshSeconds;
            set { _refreshSeconds = Math.Max(1, value); ResetTimer(); }
        }

        public enum GexDataSource { Volume, OpenInterest }
        private GexDataSource _dataSource = GexDataSource.Volume;
        [Display(GroupName = "01. 🔑 API Configuration", Name = "GEX Data Source", Description = "Use Volume-based or Open Interest-based GEX", Order = 50)]
        public GexDataSource DataSource
        {
            get => _dataSource;
            set { _dataSource = value; RequestRecalc(); }
        }
        #endregion

        #region Conversion Settings
        private bool _enableConversion = true;
        /// <summary>Enable price conversion for futures/ETF mapping</summary>
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
        [Display(GroupName = "02. 🔄 Price Conversion", Name = "Price Step", Description = "Rounding step for converted prices (e.g., 0.25)", Order = 40)]
        public decimal PriceStep
        {
            get => _priceStep;
            set { _priceStep = value <= 0 ? 0.25m : value; RequestRecalc(); }
        }
        #endregion

        #region Profile Position
        private int _centerOffsetPx = 0;
        /// <summary>Horizontal offset for GEX profile center</summary>
        [Display(GroupName = "03. 📍 Profile Position", Name = "Center Offset (px)", Description = "Horizontal offset from current bar in pixels", Order = 10)]
        [Range(-5000, 5000)]
        public int CenterOffsetPx
        {
            get => _centerOffsetPx;
            set { _centerOffsetPx = Math.Clamp(value, -5000, 5000); RequestRecalc(); }
        }

        private bool _showCenterLine = true;
        [Display(GroupName = "03. 📍 Profile Position", Name = "Show Center Line", Description = "Display vertical center line", Order = 20)]
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

        private int _centerLineThickness = 1;
        [Display(GroupName = "03. 📍 Profile Position", Name = "Center Line Thickness", Order = 40)]
        [Range(1, 10)]
        public int CenterLineThickness
        {
            get => _centerLineThickness;
            set { _centerLineThickness = Math.Clamp(value, 1, 10); RequestRecalc(); }
        }
        #endregion

        #region Profile Appearance
        private int _maxBarWidthPx = 300;
        /// <summary>Maximum bar width in pixels</summary>
        [Display(GroupName = "04. 📊 Bar Appearance", Name = "Max Bar Width (px)", Description = "Maximum width for GEX bars", Order = 10)]
        [Range(50, 1500)]
        public int MaxBarWidthPx
        {
            get => _maxBarWidthPx;
            set { _maxBarWidthPx = Math.Clamp(value, 50, 1500); RequestRecalc(); }
        }

        private int _barThicknessPx = 8;
        [Display(GroupName = "04. 📊 Bar Appearance", Name = "Bar Thickness (px)", Description = "Height of each GEX bar", Order = 20)]
        [Range(2, 50)]
        public int BarThicknessPx
        {
            get => _barThicknessPx;
            set { _barThicknessPx = Math.Clamp(value, 2, 50); RequestRecalc(); }
        }

        private Color _positiveGexColor = Color.FromArgb(200, 0, 200, 100);
        [Display(GroupName = "04. 📊 Bar Appearance", Name = "Positive GEX Color", Description = "Color for positive gamma exposure bars", Order = 30)]
        public Color PositiveGexColor
        {
            get => _positiveGexColor;
            set { _positiveGexColor = value; RequestRecalc(); }
        }

        private Color _negativeGexColor = Color.FromArgb(200, 200, 60, 60);
        [Display(GroupName = "04. 📊 Bar Appearance", Name = "Negative GEX Color", Description = "Color for negative gamma exposure bars", Order = 40)]
        public Color NegativeGexColor
        {
            get => _negativeGexColor;
            set { _negativeGexColor = value; RequestRecalc(); }
        }

        private int _fillOpacity = 180;
        [Display(GroupName = "04. 📊 Bar Appearance", Name = "Fill Opacity", Description = "Transparency of bar fill (0-255)", Order = 50)]
        [Range(0, 255)]
        public int FillOpacity
        {
            get => _fillOpacity;
            set { _fillOpacity = Math.Clamp(value, 0, 255); RequestRecalc(); }
        }

        private bool _showBarOutline = true;
        [Display(GroupName = "04. 📊 Bar Appearance", Name = "Show Bar Outline", Order = 60)]
        public bool ShowBarOutline
        {
            get => _showBarOutline;
            set { _showBarOutline = value; RequestRecalc(); }
        }

        private int _outlineThickness = 1;
        [Display(GroupName = "04. 📊 Bar Appearance", Name = "Outline Thickness", Order = 70)]
        [Range(1, 5)]
        public int OutlineThickness
        {
            get => _outlineThickness;
            set { _outlineThickness = Math.Clamp(value, 1, 5); RequestRecalc(); }
        }
        #endregion

        #region Value Labels
        private bool _showValues = true;
        /// <summary>Show GEX values next to bars</summary>
        [Display(GroupName = "05. 🏷️ Labels & Values", Name = "Show GEX Values", Description = "Display numeric GEX values on bars", Order = 10)]
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
        [Display(GroupName = "05. 🏷️ Labels & Values", Name = "Value Offset (px)", Order = 40)]
        [Range(0, 100)]
        public int ValueOffsetPx
        {
            get => _valueOffsetPx;
            set { _valueOffsetPx = Math.Clamp(value, 0, 100); RequestRecalc(); }
        }

        private bool _showStrikeLabels = true;
        [Display(GroupName = "05. 🏷️ Labels & Values", Name = "Show Strike Labels", Description = "Display strike prices on bars", Order = 50)]
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

        #region Major Levels
        private bool _showZeroGammaLine = true;
        /// <summary>Show Zero Gamma (ZG) horizontal line</summary>
        [Display(GroupName = "06. ➖ Major Levels", Name = "Show Zero Gamma Line", Description = "Key level where net gamma = 0", Order = 10)]
        public bool ShowZeroGammaLine
        {
            get => _showZeroGammaLine;
            set { _showZeroGammaLine = value; RequestRecalc(); }
        }

        private Color _zeroGammaColor = Color.Yellow;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Zero Gamma Color", Order = 20)]
        public Color ZeroGammaColor
        {
            get => _zeroGammaColor;
            set { _zeroGammaColor = value; RequestRecalc(); }
        }

        private int _zeroGammaThickness = 2;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Zero Gamma Thickness", Order = 30)]
        [Range(1, 10)]
        public int ZeroGammaThickness
        {
            get => _zeroGammaThickness;
            set { _zeroGammaThickness = Math.Clamp(value, 1, 10); RequestRecalc(); }
        }

        private DashStyle _zeroGammaDash = DashStyle.Dash;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Zero Gamma Dash Style", Order = 40)]
        public DashStyle ZeroGammaDash
        {
            get => _zeroGammaDash;
            set { _zeroGammaDash = value; RequestRecalc(); }
        }

        private bool _showMajorPosLine = true;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Show Major Positive Line", Description = "Highest positive GEX strike", Order = 50)]
        public bool ShowMajorPosLine
        {
            get => _showMajorPosLine;
            set { _showMajorPosLine = value; RequestRecalc(); }
        }

        private Color _majorPosColor = Color.LimeGreen;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Major Positive Color", Order = 60)]
        public Color MajorPosColor
        {
            get => _majorPosColor;
            set { _majorPosColor = value; RequestRecalc(); }
        }

        private bool _showMajorNegLine = true;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Show Major Negative Line", Description = "Lowest negative GEX strike", Order = 70)]
        public bool ShowMajorNegLine
        {
            get => _showMajorNegLine;
            set { _showMajorNegLine = value; RequestRecalc(); }
        }

        private Color _majorNegColor = Color.OrangeRed;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Major Negative Color", Order = 80)]
        public Color MajorNegColor
        {
            get => _majorNegColor;
            set { _majorNegColor = value; RequestRecalc(); }
        }

        private int _majorLinesThickness = 2;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Major Lines Thickness", Order = 90)]
        [Range(1, 10)]
        public int MajorLinesThickness
        {
            get => _majorLinesThickness;
            set { _majorLinesThickness = Math.Clamp(value, 1, 10); RequestRecalc(); }
        }

        private DashStyle _majorLinesDash = DashStyle.Solid;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Major Lines Dash Style", Order = 100)]
        public DashStyle MajorLinesDash
        {
            get => _majorLinesDash;
            set { _majorLinesDash = value; RequestRecalc(); }
        }

        private bool _showMajorLabels = true;
        [Display(GroupName = "06. ➖ Major Levels", Name = "Show Level Labels", Order = 110)]
        public bool ShowMajorLabels
        {
            get => _showMajorLabels;
            set { _showMajorLabels = value; RequestRecalc(); }
        }
        #endregion

        #region Info Panel
        private bool _showInfoPanel = true;
        /// <summary>Show main information panel with GEX summary</summary>
        [Display(GroupName = "07. 📋 Info Panel", Name = "Show Info Panel", Description = "Display GEX data summary panel", Order = 10)]
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

        private (int x, int y) CalcPanelXY(PanelPosition pos, int panelWidth, int panelHeight, int offsetX, int offsetY)
        {
            int chartW = ChartInfo.Region.Width;
            int chartH = ChartInfo.Region.Height;

            int x = pos switch
            {
                PanelPosition.TopLeft or PanelPosition.MiddleLeft or PanelPosition.BottomLeft
                    => offsetX,
                PanelPosition.TopCenter or PanelPosition.MiddleCenter or PanelPosition.BottomCenter
                    => (chartW - panelWidth) / 2 + offsetX,
                PanelPosition.TopRight or PanelPosition.MiddleRight or PanelPosition.BottomRight
                    => chartW - panelWidth - offsetX,
                _ => offsetX
            };

            int y = pos switch
            {
                PanelPosition.TopLeft or PanelPosition.TopCenter or PanelPosition.TopRight
                    => offsetY,
                PanelPosition.MiddleLeft or PanelPosition.MiddleCenter or PanelPosition.MiddleRight
                    => (chartH - panelHeight) / 2 + offsetY,
                PanelPosition.BottomLeft or PanelPosition.BottomCenter or PanelPosition.BottomRight
                    => chartH - panelHeight - offsetY,
                _ => offsetY
            };

            return (x, y);
        }

        private PanelPosition _infoPanelPosition = PanelPosition.TopRight;
        [Display(GroupName = "07. 📋 Info Panel", Name = "Panel Position", Order = 15)]
        public PanelPosition InfoPanelPosition
        {
            get => _infoPanelPosition;
            set { _infoPanelPosition = value; RequestRecalc(); }
        }

        private int _infoPanelX = 10;
        [Display(GroupName = "07. 📋 Info Panel", Name = "X Offset (px)", Order = 20)]
        [Range(0, 5000)]
        public int InfoPanelX
        {
            get => _infoPanelX;
            set { _infoPanelX = Math.Clamp(value, 0, 5000); RequestRecalc(); }
        }

        private int _infoPanelY = 10;
        [Display(GroupName = "07. 📋 Info Panel", Name = "Y Offset (px)", Order = 30)]
        [Range(0, 5000)]
        public int InfoPanelY
        {
            get => _infoPanelY;
            set { _infoPanelY = Math.Clamp(value, 0, 5000); RequestRecalc(); }
        }

        private int _infoPanelFontSize = 11;
        [Display(GroupName = "07. 📋 Info Panel", Name = "Font Size", Order = 40)]
        [Range(8, 24)]
        public int InfoPanelFontSize
        {
            get => _infoPanelFontSize;
            set { _infoPanelFontSize = Math.Clamp(value, 8, 24); RequestRecalc(); }
        }

        private Color _infoPanelTextColor = Color.White;
        [Display(GroupName = "07. 📋 Info Panel", Name = "Text Color", Order = 50)]
        public Color InfoPanelTextColor
        {
            get => _infoPanelTextColor;
            set { _infoPanelTextColor = value; RequestRecalc(); }
        }

        private Color _infoPanelBackColor = Color.FromArgb(220, 20, 20, 25);
        [Display(GroupName = "07. 📋 Info Panel", Name = "Background Color", Order = 60)]
        public Color InfoPanelBackColor
        {
            get => _infoPanelBackColor;
            set { _infoPanelBackColor = value; RequestRecalc(); }
        }

        private Color _infoPanelBorderColor = Color.FromArgb(180, 60, 60, 70);
        [Display(GroupName = "07. 📋 Info Panel", Name = "Border Color", Order = 65)]
        public Color InfoPanelBorderColor
        {
            get => _infoPanelBorderColor;
            set { _infoPanelBorderColor = value; RequestRecalc(); }
        }

        private Color _infoPanelHeaderColor = Color.FromArgb(255, 0, 180, 220);
        [Display(GroupName = "07. 📋 Info Panel", Name = "Header Color", Order = 70)]
        public Color InfoPanelHeaderColor
        {
            get => _infoPanelHeaderColor;
            set { _infoPanelHeaderColor = value; RequestRecalc(); }
        }

        private Color _infoPanelAccentPositive = Color.FromArgb(255, 0, 200, 100);
        [Display(GroupName = "07. 📋 Info Panel", Name = "Positive Accent Color", Order = 75)]
        public Color InfoPanelAccentPositive
        {
            get => _infoPanelAccentPositive;
            set { _infoPanelAccentPositive = value; RequestRecalc(); }
        }

        private Color _infoPanelAccentNegative = Color.FromArgb(255, 220, 80, 80);
        [Display(GroupName = "07. 📋 Info Panel", Name = "Negative Accent Color", Order = 80)]
        public Color InfoPanelAccentNegative
        {
            get => _infoPanelAccentNegative;
            set { _infoPanelAccentNegative = value; RequestRecalc(); }
        }

        private int _infoPanelBarWidth = 120;
        [Display(GroupName = "07. 📋 Info Panel", Name = "Bar Width (px)", Order = 85)]
        [Range(50, 300)]
        public int InfoPanelBarWidth
        {
            get => _infoPanelBarWidth;
            set { _infoPanelBarWidth = Math.Clamp(value, 50, 300); RequestRecalc(); }
        }

        private bool _showInfoPanelBars = true;
        [Display(GroupName = "07. 📋 Info Panel", Name = "Show Visual Bars", Order = 90)]
        public bool ShowInfoPanelBars
        {
            get => _showInfoPanelBars;
            set { _showInfoPanelBars = value; RequestRecalc(); }
        }

        private bool _compactMode = false;
        [Display(GroupName = "07. 📋 Info Panel", Name = "Compact Mode", Description = "Smaller panel with essential info only", Order = 95)]
        public bool CompactMode
        {
            get => _compactMode;
            set { _compactMode = value; RequestRecalc(); }
        }
        #endregion

        #region Strike Grid
        private bool _showStrikeGrid = false;
        /// <summary>Show horizontal grid lines at strike prices</summary>
        [Display(GroupName = "08. 📐 Strike Grid", Name = "Show Strike Grid", Description = "Display horizontal lines at strike intervals", Order = 10)]
        public bool ShowStrikeGrid
        {
            get => _showStrikeGrid;
            set { _showStrikeGrid = value; RequestRecalc(); }
        }

        private decimal _strikeGridStep = 1m;
        [Display(GroupName = "08. 📐 Strike Grid", Name = "Strike Step", Description = "Interval between grid lines in underlying points", Order = 20)]
        public decimal StrikeGridStep
        {
            get => _strikeGridStep;
            set { _strikeGridStep = value <= 0 ? 1m : value; RequestRecalc(); }
        }

        private decimal _strikeGridStart = 0m;
        [Display(GroupName = "08. 📐 Strike Grid", Name = "Start Strike (0=auto)", Order = 25)]
        public decimal StrikeGridStart
        {
            get => _strikeGridStart;
            set { _strikeGridStart = Math.Max(0, value); RequestRecalc(); }
        }

        private decimal _strikeGridEnd = 0m;
        [Display(GroupName = "08. 📐 Strike Grid", Name = "End Strike (0=auto)", Order = 26)]
        public decimal StrikeGridEnd
        {
            get => _strikeGridEnd;
            set { _strikeGridEnd = Math.Max(0, value); RequestRecalc(); }
        }

        private Color _strikeGridColor = Color.FromArgb(40, 150, 150, 150);
        [Display(GroupName = "08. 📐 Strike Grid", Name = "Grid Color", Order = 30)]
        public Color StrikeGridColor
        {
            get => _strikeGridColor;
            set { _strikeGridColor = value; RequestRecalc(); }
        }

        private int _strikeGridThickness = 1;
        [Display(GroupName = "08. 📐 Strike Grid", Name = "Grid Thickness", Order = 40)]
        [Range(1, 5)]
        public int StrikeGridThickness
        {
            get => _strikeGridThickness;
            set { _strikeGridThickness = Math.Clamp(value, 1, 5); RequestRecalc(); }
        }

        private DashStyle _strikeGridDash = DashStyle.Dot;
        [Display(GroupName = "08. 📐 Strike Grid", Name = "Grid Dash Style", Order = 50)]
        public DashStyle StrikeGridDash
        {
            get => _strikeGridDash;
            set { _strikeGridDash = value; RequestRecalc(); }
        }

        private bool _showStrikeGridLabels = true;
        [Display(GroupName = "08. 📐 Strike Grid", Name = "Show Strike Labels", Order = 60)]
        public bool ShowStrikeGridLabels
        {
            get => _showStrikeGridLabels;
            set { _showStrikeGridLabels = value; RequestRecalc(); }
        }

        private Color _strikeGridLabelColor = Color.FromArgb(180, 180, 180, 180);
        [Display(GroupName = "08. 📐 Strike Grid", Name = "Label Color", Order = 70)]
        public Color StrikeGridLabelColor
        {
            get => _strikeGridLabelColor;
            set { _strikeGridLabelColor = value; RequestRecalc(); }
        }

        private int _strikeGridLabelFontSize = 8;
        [Display(GroupName = "08. 📐 Strike Grid", Name = "Label Font Size", Order = 80)]
        [Range(6, 16)]
        public int StrikeGridLabelFontSize
        {
            get => _strikeGridLabelFontSize;
            set { _strikeGridLabelFontSize = Math.Clamp(value, 6, 16); RequestRecalc(); }
        }

        public enum GridLabelPosition { Left, Right }
        private GridLabelPosition _strikeGridLabelPos = GridLabelPosition.Right;
        [Display(GroupName = "08. 📐 Strike Grid", Name = "Label Position", Order = 90)]
        public GridLabelPosition StrikeGridLabelPosition
        {
            get => _strikeGridLabelPos;
            set { _strikeGridLabelPos = value; RequestRecalc(); }
        }

        private Color _strikeGridMajorColor = Color.FromArgb(80, 200, 200, 200);
        [Display(GroupName = "08. 📐 Strike Grid", Name = "Major Strike Color", Order = 100)]
        public Color StrikeGridMajorColor
        {
            get => _strikeGridMajorColor;
            set { _strikeGridMajorColor = value; RequestRecalc(); }
        }

        private int _strikeGridMajorEvery = 5;
        [Display(GroupName = "08. 📐 Strike Grid", Name = "Major Line Every N", Order = 110)]
        [Range(0, 50)]
        public int StrikeGridMajorEvery
        {
            get => _strikeGridMajorEvery;
            set { _strikeGridMajorEvery = Math.Clamp(value, 0, 50); RequestRecalc(); }
        }

        private int _strikeGridMajorThickness = 2;
        [Display(GroupName = "08. 📐 Strike Grid", Name = "Major Line Thickness", Order = 120)]
        [Range(1, 8)]
        public int StrikeGridMajorThickness
        {
            get => _strikeGridMajorThickness;
            set { _strikeGridMajorThickness = Math.Clamp(value, 1, 8); RequestRecalc(); }
        }
        #endregion

        #region Alerts
        private bool _enableAlerts = true;
        /// <summary>Enable GEX change alerts</summary>
        [Display(GroupName = "09. 🔔 Alerts", Name = "Enable Alerts", Description = "Detect significant GEX changes", Order = 10)]
        public bool EnableAlerts
        {
            get => _enableAlerts;
            set { _enableAlerts = value; RequestRecalc(); }
        }

        public enum AlertThresholdType { Percentage, AbsoluteValue, TopN }
        private AlertThresholdType _alertThresholdType = AlertThresholdType.Percentage;
        [Display(GroupName = "09. 🔔 Alerts", Name = "Threshold Type", Description = "Method to determine alert trigger", Order = 20)]
        public AlertThresholdType AlertThreshold
        {
            get => _alertThresholdType;
            set { _alertThresholdType = value; RequestRecalc(); }
        }

        private decimal _alertThresholdValue = 50m;
        [Display(GroupName = "09. 🔔 Alerts", Name = "Threshold Value", Description = "Value for selected threshold type", Order = 30)]
        [Range(0, 10000)]
        public decimal AlertThresholdValue
        {
            get => _alertThresholdValue;
            set { _alertThresholdValue = Math.Max(0, value); RequestRecalc(); }
        }

        public enum AlertTimePeriod { OneMin, FiveMin, TenMin, FifteenMin, ThirtyMin }
        private AlertTimePeriod _alertTimePeriod = AlertTimePeriod.FiveMin;
        [Display(GroupName = "09. 🔔 Alerts", Name = "Comparison Period", Description = "Time period to compare against", Order = 40)]
        public AlertTimePeriod AlertPeriod
        {
            get => _alertTimePeriod;
            set { _alertTimePeriod = value; RequestRecalc(); }
        }

        private bool _alertOnIncrease = true;
        [Display(GroupName = "09. 🔔 Alerts", Name = "Alert on Increase", Order = 50)]
        public bool AlertOnIncrease
        {
            get => _alertOnIncrease;
            set { _alertOnIncrease = value; RequestRecalc(); }
        }

        private bool _alertOnDecrease = true;
        [Display(GroupName = "09. 🔔 Alerts", Name = "Alert on Decrease", Order = 60)]
        public bool AlertOnDecrease
        {
            get => _alertOnDecrease;
            set { _alertOnDecrease = value; RequestRecalc(); }
        }

        private Color _alertIncreaseColor = Color.FromArgb(255, 0, 255, 150);
        [Display(GroupName = "09. 🔔 Alerts", Name = "Increase Alert Color", Order = 70)]
        public Color AlertIncreaseColor
        {
            get => _alertIncreaseColor;
            set { _alertIncreaseColor = value; RequestRecalc(); }
        }

        private Color _alertDecreaseColor = Color.FromArgb(255, 255, 100, 0);
        [Display(GroupName = "09. 🔔 Alerts", Name = "Decrease Alert Color", Order = 80)]
        public Color AlertDecreaseColor
        {
            get => _alertDecreaseColor;
            set { _alertDecreaseColor = value; RequestRecalc(); }
        }

        private int _alertOutlineThickness = 3;
        [Display(GroupName = "09. 🔔 Alerts", Name = "Alert Outline Thickness", Order = 90)]
        [Range(1, 10)]
        public int AlertOutlineThickness
        {
            get => _alertOutlineThickness;
            set { _alertOutlineThickness = Math.Clamp(value, 1, 10); RequestRecalc(); }
        }

        private bool _alertShowInPanel = true;
        [Display(GroupName = "09. 🔔 Alerts", Name = "Show in Info Panel", Order = 100)]
        public bool AlertShowInPanel
        {
            get => _alertShowInPanel;
            set { _alertShowInPanel = value; RequestRecalc(); }
        }

        private bool _alertPulseEffect = true;
        [Display(GroupName = "09. 🔔 Alerts", Name = "Pulse Animation", Description = "Animated pulsing effect on alert bars", Order = 110)]
        public bool AlertPulseEffect
        {
            get => _alertPulseEffect;
            set { _alertPulseEffect = value; RequestRecalc(); }
        }
        #endregion

        #region Direction Arrows
        private bool _showDirectionArrows = true;
        /// <summary>Show trend direction arrows on bars</summary>
        [Display(GroupName = "10. ➡️ Direction Arrows", Name = "Show Direction Arrows", Description = "Display trend arrows based on GEX changes", Order = 10)]
        public bool ShowDirectionArrows
        {
            get => _showDirectionArrows;
            set { _showDirectionArrows = value; RequestRecalc(); }
        }

        public enum ArrowTimePeriod { OneMin, FiveMin, TenMin, FifteenMin, ThirtyMin }
        private ArrowTimePeriod _arrowTimePeriod = ArrowTimePeriod.FiveMin;
        [Display(GroupName = "10. ➡️ Direction Arrows", Name = "Comparison Period", Order = 20)]
        public ArrowTimePeriod ArrowPeriod
        {
            get => _arrowTimePeriod;
            set { _arrowTimePeriod = value; RequestRecalc(); }
        }

        private int _arrowFontSize = 10;
        [Display(GroupName = "10. ➡️ Direction Arrows", Name = "Arrow Size", Order = 30)]
        [Range(6, 24)]
        public int ArrowFontSize
        {
            get => _arrowFontSize;
            set { _arrowFontSize = Math.Clamp(value, 6, 24); RequestRecalc(); }
        }

        private Color _arrowUpColor = Color.FromArgb(255, 0, 255, 120);
        [Display(GroupName = "10. ➡️ Direction Arrows", Name = "Up Arrow Color", Order = 40)]
        public Color ArrowUpColor
        {
            get => _arrowUpColor;
            set { _arrowUpColor = value; RequestRecalc(); }
        }

        private Color _arrowDownColor = Color.FromArgb(255, 255, 80, 80);
        [Display(GroupName = "10. ➡️ Direction Arrows", Name = "Down Arrow Color", Order = 50)]
        public Color ArrowDownColor
        {
            get => _arrowDownColor;
            set { _arrowDownColor = value; RequestRecalc(); }
        }

        private int _arrowOffsetPx = 2;
        [Display(GroupName = "10. ➡️ Direction Arrows", Name = "Arrow Offset (px)", Order = 60)]
        [Range(0, 50)]
        public int ArrowOffsetPx
        {
            get => _arrowOffsetPx;
            set { _arrowOffsetPx = Math.Clamp(value, 0, 50); RequestRecalc(); }
        }

        private decimal _arrowMinChange = 0m;
        [Display(GroupName = "10. ➡️ Direction Arrows", Name = "Min Change to Show", Description = "Minimum GEX change to display arrow", Order = 70)]
        public decimal ArrowMinChange
        {
            get => _arrowMinChange;
            set { _arrowMinChange = Math.Max(0, value); RequestRecalc(); }
        }
        #endregion

        #region Gamma Zones
        private bool _showGammaZones = false;
        /// <summary>Color chart background by gamma zone</summary>
        [Display(GroupName = "11. 🌈 Gamma Zones", Name = "Show Gamma Zones", Description = "Color background above/below Zero Gamma", Order = 10)]
        public bool ShowGammaZones
        {
            get => _showGammaZones;
            set { _showGammaZones = value; RequestRecalc(); }
        }

        private Color _positiveZoneColor = Color.FromArgb(15, 0, 200, 100);
        [Display(GroupName = "11. 🌈 Gamma Zones", Name = "Positive Zone Color", Description = "Background for positive gamma area", Order = 20)]
        public Color PositiveZoneColor
        {
            get => _positiveZoneColor;
            set { _positiveZoneColor = value; RequestRecalc(); }
        }

        private Color _negativeZoneColor = Color.FromArgb(15, 200, 60, 60);
        [Display(GroupName = "11. 🌈 Gamma Zones", Name = "Negative Zone Color", Description = "Background for negative gamma area", Order = 30)]
        public Color NegativeZoneColor
        {
            get => _negativeZoneColor;
            set { _negativeZoneColor = value; RequestRecalc(); }
        }

        private bool _showZoneBoundary = true;
        [Display(GroupName = "11. 🌈 Gamma Zones", Name = "Show Zone Boundary", Order = 40)]
        public bool ShowZoneBoundary
        {
            get => _showZoneBoundary;
            set { _showZoneBoundary = value; RequestRecalc(); }
        }

        private Color _zoneBoundaryColor = Color.FromArgb(100, 255, 255, 0);
        [Display(GroupName = "11. 🌈 Gamma Zones", Name = "Zone Boundary Color", Order = 50)]
        public Color ZoneBoundaryColor
        {
            get => _zoneBoundaryColor;
            set { _zoneBoundaryColor = value; RequestRecalc(); }
        }

        private bool _showZoneLabels = true;
        [Display(GroupName = "11. 🌈 Gamma Zones", Name = "Show Zone Labels", Order = 60)]
        public bool ShowZoneLabels
        {
            get => _showZoneLabels;
            set { _showZoneLabels = value; RequestRecalc(); }
        }

        public enum ZoneLabelAlign { Left, Right, Center }
        private ZoneLabelAlign _zoneLabelPos = ZoneLabelAlign.Right;
        [Display(GroupName = "11. 🌈 Gamma Zones", Name = "Zone Label Position", Order = 70)]
        public ZoneLabelAlign ZoneLabelPosition
        {
            get => _zoneLabelPos;
            set { _zoneLabelPos = value; RequestRecalc(); }
        }
        #endregion

        #region Sparklines
        private bool _showSparklines = false;
        /// <summary>Show mini trend charts for each strike</summary>
        [Display(GroupName = "12. 📈 Sparklines", Name = "Show Sparklines", Description = "Mini trend charts showing GEX history", Order = 10)]
        public bool ShowSparklines
        {
            get => _showSparklines;
            set { _showSparklines = value; RequestRecalc(); }
        }

        private int _sparklineWidth = 40;
        [Display(GroupName = "12. 📈 Sparklines", Name = "Width (px)", Order = 20)]
        [Range(20, 100)]
        public int SparklineWidth
        {
            get => _sparklineWidth;
            set { _sparklineWidth = Math.Clamp(value, 20, 100); RequestRecalc(); }
        }

        private int _sparklineHeight = 12;
        [Display(GroupName = "12. 📈 Sparklines", Name = "Height (px)", Order = 30)]
        [Range(6, 30)]
        public int SparklineHeight
        {
            get => _sparklineHeight;
            set { _sparklineHeight = Math.Clamp(value, 6, 30); RequestRecalc(); }
        }

        private int _sparklineOffsetPx = 5;
        [Display(GroupName = "12. 📈 Sparklines", Name = "Offset from Bar (px)", Order = 40)]
        [Range(0, 50)]
        public int SparklineOffsetPx
        {
            get => _sparklineOffsetPx;
            set { _sparklineOffsetPx = Math.Clamp(value, 0, 50); RequestRecalc(); }
        }

        private Color _sparklineUpColor = Color.FromArgb(255, 0, 200, 120);
        [Display(GroupName = "12. 📈 Sparklines", Name = "Uptrend Color", Order = 50)]
        public Color SparklineUpColor
        {
            get => _sparklineUpColor;
            set { _sparklineUpColor = value; RequestRecalc(); }
        }

        private Color _sparklineDownColor = Color.FromArgb(255, 255, 80, 80);
        [Display(GroupName = "12. 📈 Sparklines", Name = "Downtrend Color", Order = 60)]
        public Color SparklineDownColor
        {
            get => _sparklineDownColor;
            set { _sparklineDownColor = value; RequestRecalc(); }
        }

        private Color _sparklineNeutralColor = Color.FromArgb(255, 150, 150, 150);
        [Display(GroupName = "12. 📈 Sparklines", Name = "Neutral Color", Order = 70)]
        public Color SparklineNeutralColor
        {
            get => _sparklineNeutralColor;
            set { _sparklineNeutralColor = value; RequestRecalc(); }
        }

        private int _sparklineThickness = 1;
        [Display(GroupName = "12. 📈 Sparklines", Name = "Line Thickness", Order = 80)]
        [Range(1, 4)]
        public int SparklineThickness
        {
            get => _sparklineThickness;
            set { _sparklineThickness = Math.Clamp(value, 1, 4); RequestRecalc(); }
        }

        private bool _sparklineShowBackground = true;
        [Display(GroupName = "12. 📈 Sparklines", Name = "Show Background", Order = 90)]
        public bool SparklineShowBackground
        {
            get => _sparklineShowBackground;
            set { _sparklineShowBackground = value; RequestRecalc(); }
        }

        private Color _sparklineBackColor = Color.FromArgb(120, 30, 30, 35);
        [Display(GroupName = "12. 📈 Sparklines", Name = "Background Color", Order = 100)]
        public Color SparklineBackColor
        {
            get => _sparklineBackColor;
            set { _sparklineBackColor = value; RequestRecalc(); }
        }

        private bool _sparklineShowDots = true;
        [Display(GroupName = "12. 📈 Sparklines", Name = "Show Data Points", Order = 110)]
        public bool SparklineShowDots
        {
            get => _sparklineShowDots;
            set { _sparklineShowDots = value; RequestRecalc(); }
        }

        private bool _sparklineShowZeroLine = true;
        [Display(GroupName = "12. 📈 Sparklines", Name = "Show Zero Line", Order = 120)]
        public bool SparklineShowZeroLine
        {
            get => _sparklineShowZeroLine;
            set { _sparklineShowZeroLine = value; RequestRecalc(); }
        }
        #endregion

        #region Alert History
        private bool _enableAlertHistory = true;
        /// <summary>Keep history of triggered alerts</summary>
        [Display(GroupName = "13. 📜 Alert History", Name = "Enable Alert History", Description = "Store and display past alerts", Order = 10)]
        public bool EnableAlertHistory
        {
            get => _enableAlertHistory;
            set { _enableAlertHistory = value; RequestRecalc(); }
        }

        private int _alertHistoryMaxItems = 20;
        [Display(GroupName = "13. 📜 Alert History", Name = "Max History Items", Order = 20)]
        [Range(5, 100)]
        public int AlertHistoryMaxItems
        {
            get => _alertHistoryMaxItems;
            set { _alertHistoryMaxItems = Math.Clamp(value, 5, 100); TrimAlertHistory(); RequestRecalc(); }
        }

        private bool _showAlertHistoryPanel = true;
        [Display(GroupName = "13. 📜 Alert History", Name = "Show History Panel", Order = 30)]
        public bool ShowAlertHistoryPanel
        {
            get => _showAlertHistoryPanel;
            set { _showAlertHistoryPanel = value; RequestRecalc(); }
        }

        private int _alertHistoryDisplayCount = 8;
        [Display(GroupName = "13. 📜 Alert History", Name = "Display Count in Panel", Order = 40)]
        [Range(3, 20)]
        public int AlertHistoryDisplayCount
        {
            get => _alertHistoryDisplayCount;
            set { _alertHistoryDisplayCount = Math.Clamp(value, 3, 20); RequestRecalc(); }
        }

        private PanelPosition _alertHistoryPosition = PanelPosition.BottomRight;
        [Display(GroupName = "13. 📜 Alert History", Name = "Panel Position", Order = 50)]
        public PanelPosition AlertHistoryPosition
        {
            get => _alertHistoryPosition;
            set { _alertHistoryPosition = value; RequestRecalc(); }
        }

        private int _alertHistoryPanelX = 10;
        [Display(GroupName = "13. 📜 Alert History", Name = "X Offset (px)", Order = 60)]
        [Range(0, 5000)]
        public int AlertHistoryPanelX
        {
            get => _alertHistoryPanelX;
            set { _alertHistoryPanelX = Math.Clamp(value, 0, 5000); RequestRecalc(); }
        }

        private int _alertHistoryPanelY = 10;
        [Display(GroupName = "13. 📜 Alert History", Name = "Y Offset (px)", Order = 70)]
        [Range(0, 5000)]
        public int AlertHistoryPanelY
        {
            get => _alertHistoryPanelY;
            set { _alertHistoryPanelY = Math.Clamp(value, 0, 5000); RequestRecalc(); }
        }

        private Color _alertHistoryBackColor = Color.FromArgb(220, 25, 20, 30);
        [Display(GroupName = "13. 📜 Alert History", Name = "Background Color", Order = 80)]
        public Color AlertHistoryBackColor
        {
            get => _alertHistoryBackColor;
            set { _alertHistoryBackColor = value; RequestRecalc(); }
        }

        private Color _alertHistoryBorderColor = Color.FromArgb(180, 80, 60, 70);
        [Display(GroupName = "13. 📜 Alert History", Name = "Border Color", Order = 85)]
        public Color AlertHistoryBorderColor
        {
            get => _alertHistoryBorderColor;
            set { _alertHistoryBorderColor = value; RequestRecalc(); }
        }

        private int _alertHistoryFontSize = 9;
        [Display(GroupName = "13. 📜 Alert History", Name = "Font Size", Order = 90)]
        [Range(7, 14)]
        public int AlertHistoryFontSize
        {
            get => _alertHistoryFontSize;
            set { _alertHistoryFontSize = Math.Clamp(value, 7, 14); RequestRecalc(); }
        }

        private int _alertHistoryDuplicateWindowSec = 60;
        [Display(GroupName = "13. 📜 Alert History", Name = "Duplicate Filter (sec)", Description = "Time window to filter duplicate alerts", Order = 100)]
        [Range(0, 300)]
        public int AlertHistoryDuplicateWindowSec
        {
            get => _alertHistoryDuplicateWindowSec;
            set { _alertHistoryDuplicateWindowSec = Math.Clamp(value, 0, 300); RequestRecalc(); }
        }

        private void TrimAlertHistory()
        {
            lock (_sync)
            {
                while (_alertHistory.Count > _alertHistoryMaxItems)
                {
                    var oldest = _alertHistory[^1];
                    _alertHistory.RemoveAt(_alertHistory.Count - 1);
                    _alertHistoryKeys.Remove(GetAlertKey(oldest));
                }
            }
        }

        private void ClearAlertHistory()
        {
            lock (_sync)
            {
                _alertHistory.Clear();
                _alertHistoryKeys.Clear();
            }
        }

        private string GetAlertKey(AlertHistoryEntry entry)
        {
            // Key based on strike + direction + minute (to avoid duplicates within same minute)
            return $"{entry.Strike}_{entry.IsIncrease}_{entry.Timestamp:yyyyMMddHHmm}";
        }
        #endregion

        #region Sound Alerts
        private DateTime _lastSoundTime = DateTime.MinValue;
        private SoundPlayer? _soundPlayer;

        private bool _enableSoundAlerts = false;
        /// <summary>Enable audio notifications for alerts</summary>
        [Display(GroupName = "14. 🔊 Sound Alerts", Name = "Enable Sound Alerts", Description = "Play sound when alerts trigger", Order = 10)]
        public bool EnableSoundAlerts
        {
            get => _enableSoundAlerts;
            set { _enableSoundAlerts = value; RequestRecalc(); }
        }

        public enum SoundAlertType { SystemBeep, CustomWav }
        private SoundAlertType _soundType = SoundAlertType.SystemBeep;
        [Display(GroupName = "14. 🔊 Sound Alerts", Name = "Sound Type", Description = "System beep or custom WAV file", Order = 20)]
        public SoundAlertType SoundType
        {
            get => _soundType;
            set { _soundType = value; InitializeSoundPlayer(); RequestRecalc(); }
        }

        private bool _soundOnIncrease = true;
        [Display(GroupName = "14. 🔊 Sound Alerts", Name = "Sound on Increase", Order = 30)]
        public bool SoundOnIncrease
        {
            get => _soundOnIncrease;
            set { _soundOnIncrease = value; RequestRecalc(); }
        }

        private bool _soundOnDecrease = true;
        [Display(GroupName = "14. 🔊 Sound Alerts", Name = "Sound on Decrease", Order = 40)]
        public bool SoundOnDecrease
        {
            get => _soundOnDecrease;
            set { _soundOnDecrease = value; RequestRecalc(); }
        }

        private int _beepFrequencyIncrease = 1200;
        [Display(GroupName = "14. 🔊 Sound Alerts", Name = "Beep Frequency - Increase (Hz)", Order = 50)]
        [Range(200, 5000)]
        public int BeepFrequencyIncrease
        {
            get => _beepFrequencyIncrease;
            set { _beepFrequencyIncrease = Math.Clamp(value, 200, 5000); RequestRecalc(); }
        }

        private int _beepFrequencyDecrease = 600;
        [Display(GroupName = "14. 🔊 Sound Alerts", Name = "Beep Frequency - Decrease (Hz)", Order = 60)]
        [Range(200, 5000)]
        public int BeepFrequencyDecrease
        {
            get => _beepFrequencyDecrease;
            set { _beepFrequencyDecrease = Math.Clamp(value, 200, 5000); RequestRecalc(); }
        }

        private int _beepDuration = 150;
        [Display(GroupName = "14. 🔊 Sound Alerts", Name = "Beep Duration (ms)", Order = 70)]
        [Range(50, 1000)]
        public int BeepDuration
        {
            get => _beepDuration;
            set { _beepDuration = Math.Clamp(value, 50, 1000); RequestRecalc(); }
        }

        private string _customWavPath = string.Empty;
        [Display(GroupName = "14. 🔊 Sound Alerts", Name = "Custom WAV Path", Description = "Full path to custom WAV file", Order = 80)]
        public string CustomWavPath
        {
            get => _customWavPath;
            set { _customWavPath = value ?? string.Empty; InitializeSoundPlayer(); RequestRecalc(); }
        }

        private int _soundCooldownSec = 5;
        [Display(GroupName = "14. 🔊 Sound Alerts", Name = "Sound Cooldown (sec)", Order = 90)]
        [Range(0, 300)]
        public int SoundCooldownSec
        {
            get => _soundCooldownSec;
            set { _soundCooldownSec = Math.Clamp(value, 0, 300); RequestRecalc(); }
        }

        private int _soundRepeatCount = 1;
        [Display(GroupName = "14. 🔊 Sound Alerts", Name = "Repeat Count", Order = 100)]
        [Range(1, 5)]
        public int SoundRepeatCount
        {
            get => _soundRepeatCount;
            set { _soundRepeatCount = Math.Clamp(value, 1, 5); RequestRecalc(); }
        }

        private bool _soundOnlyFirstAlert = true;
        [Display(GroupName = "14. 🔊 Sound Alerts", Name = "Only First Alert", Description = "Sound only for first alert in batch", Order = 110)]
        public bool SoundOnlyFirstAlert
        {
            get => _soundOnlyFirstAlert;
            set { _soundOnlyFirstAlert = value; RequestRecalc(); }
        }

        private void InitializeSoundPlayer()
        {
            try
            {
                _soundPlayer?.Dispose();
                _soundPlayer = null;

                if (_soundType == SoundAlertType.CustomWav && !string.IsNullOrWhiteSpace(_customWavPath))
                {
                    if (File.Exists(_customWavPath))
                    {
                        _soundPlayer = new SoundPlayer(_customWavPath);
                        _soundPlayer.Load();
                    }
                }
            }
            catch
            {
                _soundPlayer = null;
            }
        }

        private void PlayAlertSound(bool isIncrease)
        {
            if (!_enableSoundAlerts) return;

            // Check cooldown
            var now = DateTime.Now;
            if (_soundCooldownSec > 0 && (now - _lastSoundTime).TotalSeconds < _soundCooldownSec)
                return;

            // Check if we should play for this direction
            if (isIncrease && !_soundOnIncrease) return;
            if (!isIncrease && !_soundOnDecrease) return;

            _lastSoundTime = now;

            // Play sound asynchronously to not block rendering
            Task.Run(() =>
            {
                try
                {
                    if (_soundType == SoundAlertType.CustomWav && _soundPlayer != null)
                    {
                        for (int i = 0; i < _soundRepeatCount; i++)
                        {
                            _soundPlayer.PlaySync();
                            if (i < _soundRepeatCount - 1)
                                System.Threading.Thread.Sleep(100);
                        }
                    }
                    else
                    {
                        // System beep with different frequencies for increase/decrease
                        int frequency = isIncrease ? _beepFrequencyIncrease : _beepFrequencyDecrease;
                        for (int i = 0; i < _soundRepeatCount; i++)
                        {
                            Console.Beep(frequency, _beepDuration);
                            if (i < _soundRepeatCount - 1)
                                System.Threading.Thread.Sleep(50);
                        }
                    }
                }
                catch
                {
                    // Ignore sound errors
                }
            });
        }

        private void DisposeSoundPlayer()
        {
            try
            {
                _soundPlayer?.Dispose();
                _soundPlayer = null;
            }
            catch { }
        }
        #endregion

        #region Strike Range Filter
        private bool _enableStrikeFilter = false;
        /// <summary>Filter displayed strikes by range or count</summary>
        [Display(GroupName = "15. 🎯 Strike Filter", Name = "Enable Strike Filter", Description = "Limit displayed strikes", Order = 10)]
        public bool EnableStrikeFilter
        {
            get => _enableStrikeFilter;
            set { _enableStrikeFilter = value; RequestRecalc(); }
        }

        public enum StrikeFilterMode { PercentFromSpot, FixedRange, StrikeCount }
        private StrikeFilterMode _strikeFilterMode = StrikeFilterMode.PercentFromSpot;
        [Display(GroupName = "15. 🎯 Strike Filter", Name = "Filter Mode", Description = "Method to filter strikes", Order = 20)]
        public StrikeFilterMode FilterMode
        {
            get => _strikeFilterMode;
            set { _strikeFilterMode = value; RequestRecalc(); }
        }

        private decimal _strikeFilterPercent = 5m;
        [Display(GroupName = "15. 🎯 Strike Filter", Name = "Range (% from Spot)", Order = 30)]
        [Range(0.5, 50)]
        public decimal StrikeFilterPercent
        {
            get => _strikeFilterPercent;
            set { _strikeFilterPercent = Math.Clamp(value, 0.5m, 50m); RequestRecalc(); }
        }

        private decimal _strikeFilterMinPrice = 0m;
        [Display(GroupName = "15. 🎯 Strike Filter", Name = "Min Strike Price (0=auto)", Order = 40)]
        public decimal StrikeFilterMinPrice
        {
            get => _strikeFilterMinPrice;
            set { _strikeFilterMinPrice = Math.Max(0, value); RequestRecalc(); }
        }

        private decimal _strikeFilterMaxPrice = 0m;
        [Display(GroupName = "15. 🎯 Strike Filter", Name = "Max Strike Price (0=auto)", Order = 50)]
        public decimal StrikeFilterMaxPrice
        {
            get => _strikeFilterMaxPrice;
            set { _strikeFilterMaxPrice = Math.Max(0, value); RequestRecalc(); }
        }

        private int _strikeFilterCount = 20;
        [Display(GroupName = "15. 🎯 Strike Filter", Name = "Max Strikes to Show", Order = 60)]
        [Range(5, 100)]
        public int StrikeFilterCount
        {
            get => _strikeFilterCount;
            set { _strikeFilterCount = Math.Clamp(value, 5, 100); RequestRecalc(); }
        }

        private decimal _minGexThreshold = 0m;
        [Display(GroupName = "15. 🎯 Strike Filter", Name = "Min GEX Value", Description = "Hide strikes below this GEX value", Order = 70)]
        public decimal MinGexThreshold
        {
            get => _minGexThreshold;
            set { _minGexThreshold = Math.Max(0, value); RequestRecalc(); }
        }

        private bool _showSpotPriceLine = true;
        [Display(GroupName = "15. 🎯 Strike Filter", Name = "Show Spot Price Line", Order = 80)]
        public bool ShowSpotPriceLine
        {
            get => _showSpotPriceLine;
            set { _showSpotPriceLine = value; RequestRecalc(); }
        }

        private Color _spotPriceLineColor = Color.FromArgb(200, 0, 180, 255);
        [Display(GroupName = "15. 🎯 Strike Filter", Name = "Spot Line Color", Order = 90)]
        public Color SpotPriceLineColor
        {
            get => _spotPriceLineColor;
            set { _spotPriceLineColor = value; RequestRecalc(); }
        }

        private int _spotPriceLineThickness = 2;
        [Display(GroupName = "15. 🎯 Strike Filter", Name = "Spot Line Thickness", Order = 100)]
        [Range(1, 5)]
        public int SpotPriceLineThickness
        {
            get => _spotPriceLineThickness;
            set { _spotPriceLineThickness = Math.Clamp(value, 1, 5); RequestRecalc(); }
        }

        private DashStyle _spotPriceLineDash = DashStyle.Solid;
        [Display(GroupName = "15. 🎯 Strike Filter", Name = "Spot Line Dash Style", Order = 110)]
        public DashStyle SpotPriceLineDash
        {
            get => _spotPriceLineDash;
            set { _spotPriceLineDash = value; RequestRecalc(); }
        }

        private bool _showSpotPriceLabel = true;
        [Display(GroupName = "15. 🎯 Strike Filter", Name = "Show Spot Price Label", Order = 120)]
        public bool ShowSpotPriceLabel
        {
            get => _showSpotPriceLabel;
            set { _showSpotPriceLabel = value; RequestRecalc(); }
        }

        private bool _highlightNearSpotStrikes = true;
        [Display(GroupName = "15. 🎯 Strike Filter", Name = "Highlight Near Spot", Description = "Highlight strikes close to spot price", Order = 130)]
        public bool HighlightNearSpotStrikes
        {
            get => _highlightNearSpotStrikes;
            set { _highlightNearSpotStrikes = value; RequestRecalc(); }
        }

        private decimal _nearSpotRange = 1m;
        [Display(GroupName = "15. 🎯 Strike Filter", Name = "Near Spot Range (%)", Order = 140)]
        [Range(0.1, 10)]
        public decimal NearSpotRange
        {
            get => _nearSpotRange;
            set { _nearSpotRange = Math.Clamp(value, 0.1m, 10m); RequestRecalc(); }
        }

        private Color _nearSpotHighlightColor = Color.FromArgb(100, 0, 200, 255);
        [Display(GroupName = "15. 🎯 Strike Filter", Name = "Highlight Color", Order = 150)]
        public Color NearSpotHighlightColor
        {
            get => _nearSpotHighlightColor;
            set { _nearSpotHighlightColor = value; RequestRecalc(); }
        }

        private bool _showFilteredCount = true;
        [Display(GroupName = "15. 🎯 Strike Filter", Name = "Show Filtered Count", Description = "Display strike count in info panel", Order = 160)]
        public bool ShowFilteredCount
        {
            get => _showFilteredCount;
            set { _showFilteredCount = value; RequestRecalc(); }
        }
        #endregion

        #region Multi-Timeframe Panel
        private bool _showMultiTimeframePanel = false;
        /// <summary>Show comparison panel across multiple timeframes</summary>
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "Show MTF Panel", Description = "Compare GEX across 1/5/10/15/30 min", Order = 10)]
        public bool ShowMultiTimeframePanel
        {
            get => _showMultiTimeframePanel;
            set { _showMultiTimeframePanel = value; RequestRecalc(); }
        }

        private PanelPosition _mtfPanelPosition = PanelPosition.BottomLeft;
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "Panel Position", Order = 20)]
        public PanelPosition MtfPanelPosition
        {
            get => _mtfPanelPosition;
            set { _mtfPanelPosition = value; RequestRecalc(); }
        }

        private int _mtfPanelX = 10;
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "X Offset (px)", Order = 30)]
        [Range(0, 5000)]
        public int MtfPanelX
        {
            get => _mtfPanelX;
            set { _mtfPanelX = Math.Clamp(value, 0, 5000); RequestRecalc(); }
        }

        private int _mtfPanelY = 10;
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "Y Offset (px)", Order = 40)]
        [Range(0, 5000)]
        public int MtfPanelY
        {
            get => _mtfPanelY;
            set { _mtfPanelY = Math.Clamp(value, 0, 5000); RequestRecalc(); }
        }

        private int _mtfPanelFontSize = 10;
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "Font Size", Order = 50)]
        [Range(8, 16)]
        public int MtfPanelFontSize
        {
            get => _mtfPanelFontSize;
            set { _mtfPanelFontSize = Math.Clamp(value, 8, 16); RequestRecalc(); }
        }

        private Color _mtfPanelBackColor = Color.FromArgb(220, 20, 25, 30);
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "Background Color", Order = 60)]
        public Color MtfPanelBackColor
        {
            get => _mtfPanelBackColor;
            set { _mtfPanelBackColor = value; RequestRecalc(); }
        }

        private Color _mtfPanelBorderColor = Color.FromArgb(180, 70, 70, 80);
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "Border Color", Order = 65)]
        public Color MtfPanelBorderColor
        {
            get => _mtfPanelBorderColor;
            set { _mtfPanelBorderColor = value; RequestRecalc(); }
        }

        private Color _mtfPanelHeaderColor = Color.FromArgb(255, 180, 100, 255);
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "Header Color", Order = 70)]
        public Color MtfPanelHeaderColor
        {
            get => _mtfPanelHeaderColor;
            set { _mtfPanelHeaderColor = value; RequestRecalc(); }
        }

        private bool _showMtfNetGex = true;
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "Show Net GEX Change", Order = 80)]
        public bool ShowMtfNetGex
        {
            get => _showMtfNetGex;
            set { _showMtfNetGex = value; RequestRecalc(); }
        }

        private bool _showMtfTopStrikes = true;
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "Show Top Movers", Order = 90)]
        public bool ShowMtfTopStrikes
        {
            get => _showMtfTopStrikes;
            set { _showMtfTopStrikes = value; RequestRecalc(); }
        }

        private int _mtfTopStrikesCount = 3;
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "Top Movers Count", Order = 100)]
        [Range(1, 10)]
        public int MtfTopStrikesCount
        {
            get => _mtfTopStrikesCount;
            set { _mtfTopStrikesCount = Math.Clamp(value, 1, 10); RequestRecalc(); }
        }

        private bool _showMtfTrendBars = true;
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "Show Trend Bars", Order = 110)]
        public bool ShowMtfTrendBars
        {
            get => _showMtfTrendBars;
            set { _showMtfTrendBars = value; RequestRecalc(); }
        }

        private int _mtfTrendBarWidth = 60;
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "Trend Bar Width", Order = 120)]
        [Range(30, 150)]
        public int MtfTrendBarWidth
        {
            get => _mtfTrendBarWidth;
            set { _mtfTrendBarWidth = Math.Clamp(value, 30, 150); RequestRecalc(); }
        }

        private Color _mtfPositiveColor = Color.FromArgb(255, 0, 200, 100);
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "Positive Change Color", Order = 130)]
        public Color MtfPositiveColor
        {
            get => _mtfPositiveColor;
            set { _mtfPositiveColor = value; RequestRecalc(); }
        }

        private Color _mtfNegativeColor = Color.FromArgb(255, 220, 80, 80);
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "Negative Change Color", Order = 140)]
        public Color MtfNegativeColor
        {
            get => _mtfNegativeColor;
            set { _mtfNegativeColor = value; RequestRecalc(); }
        }

        private bool _showMtfPercentChange = true;
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "Show % Change", Order = 150)]
        public bool ShowMtfPercentChange
        {
            get => _showMtfPercentChange;
            set { _showMtfPercentChange = value; RequestRecalc(); }
        }

        private bool _mtfCompactMode = false;
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "Compact Mode", Order = 160)]
        public bool MtfCompactMode
        {
            get => _mtfCompactMode;
            set { _mtfCompactMode = value; RequestRecalc(); }
        }

        private bool _showMtfHeatmap = true;
        [Display(GroupName = "16. ⏱️ Multi-Timeframe", Name = "Show Heatmap Colors", Description = "Color intensity by change magnitude", Order = 170)]
        public bool ShowMtfHeatmap
        {
            get => _showMtfHeatmap;
            set { _showMtfHeatmap = value; RequestRecalc(); }
        }
        #endregion

        #region Data Export
        private bool _enableDataExport = false;
        /// <summary>Enable exporting GEX data to files</summary>
        [Display(GroupName = "17. 💾 Data Export", Name = "Enable Data Export", Description = "Export GEX data to CSV/TXT files", Order = 10)]
        public bool EnableDataExport
        {
            get => _enableDataExport;
            set { _enableDataExport = value; RequestRecalc(); }
        }

        private string _exportDirectory = string.Empty;
        [Display(GroupName = "17. 💾 Data Export", Name = "Export Directory", Description = "Folder path (empty = Desktop)", Order = 20)]
        public string ExportDirectory
        {
            get => _exportDirectory;
            set { _exportDirectory = value ?? string.Empty; RequestRecalc(); }
        }

        private string _exportFilePrefix = "GexBot";
        [Display(GroupName = "17. 💾 Data Export", Name = "File Name Prefix", Order = 30)]
        public string ExportFilePrefix
        {
            get => _exportFilePrefix;
            set { _exportFilePrefix = string.IsNullOrWhiteSpace(value) ? "GexBot" : value; RequestRecalc(); }
        }

        public enum ExportFormat { CSV, TXT }
        private ExportFormat _exportFormat = ExportFormat.CSV;
        [Display(GroupName = "17. 💾 Data Export", Name = "Export Format", Order = 40)]
        public ExportFormat DataExportFormat
        {
            get => _exportFormat;
            set { _exportFormat = value; RequestRecalc(); }
        }

        private bool _exportOnRefresh = false;
        [Display(GroupName = "17. 💾 Data Export", Name = "Auto-Export on Refresh", Order = 50)]
        public bool ExportOnRefresh
        {
            get => _exportOnRefresh;
            set { _exportOnRefresh = value; RequestRecalc(); }
        }

        private bool _exportIncludeTimestamp = true;
        [Display(GroupName = "17. 💾 Data Export", Name = "Include Timestamp", Description = "Add timestamp to filename", Order = 60)]
        public bool ExportIncludeTimestamp
        {
            get => _exportIncludeTimestamp;
            set { _exportIncludeTimestamp = value; RequestRecalc(); }
        }

        private bool _exportIncludePriors = true;
        [Display(GroupName = "17. 💾 Data Export", Name = "Include Priors Data", Order = 70)]
        public bool ExportIncludePriors
        {
            get => _exportIncludePriors;
            set { _exportIncludePriors = value; RequestRecalc(); }
        }

        private bool _exportIncludeAlerts = true;
        [Display(GroupName = "17. 💾 Data Export", Name = "Include Active Alerts", Order = 80)]
        public bool ExportIncludeAlerts
        {
            get => _exportIncludeAlerts;
            set { _exportIncludeAlerts = value; RequestRecalc(); }
        }

        private bool _exportAppendMode = false;
        [Display(GroupName = "17. 💾 Data Export", Name = "Append Mode", Description = "Append to existing file", Order = 90)]
        public bool ExportAppendMode
        {
            get => _exportAppendMode;
            set { _exportAppendMode = value; RequestRecalc(); }
        }

        private int _exportMaxFileSizeMB = 50;
        [Display(GroupName = "17. 💾 Data Export", Name = "Max File Size (MB)", Order = 100)]
        [Range(1, 500)]
        public int ExportMaxFileSizeMB
        {
            get => _exportMaxFileSizeMB;
            set { _exportMaxFileSizeMB = Math.Clamp(value, 1, 500); RequestRecalc(); }
        }

        private bool _showExportStatus = true;
        [Display(GroupName = "17. 💾 Data Export", Name = "Show Export Status", Description = "Display export status on chart", Order = 110)]
        public bool ShowExportStatus
        {
            get => _showExportStatus;
            set { _showExportStatus = value; RequestRecalc(); }
        }

        private DateTime? _lastExportTime;
        private string _lastExportFile = string.Empty;
        private string _lastExportError = string.Empty;
        private int _exportCount;

        private string GetExportDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_exportDirectory) && Directory.Exists(_exportDirectory))
            {
                return _exportDirectory;
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        private string GetExportFilePath()
        {
            var dir = GetExportDirectory();
            var ticker = GetTickerString();
            var ext = _exportFormat == ExportFormat.CSV ? "csv" : "txt";

            string fileName;
            if (_exportIncludeTimestamp)
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                fileName = $"{_exportFilePrefix}_{ticker}_{timestamp}.{ext}";
            }
            else
            {
                fileName = $"{_exportFilePrefix}_{ticker}.{ext}";
            }

            return Path.Combine(dir, fileName);
        }

        private void ExportData(GexClassicData data)
        {
            if (!_enableDataExport || data == null) return;

            try
            {
                var filePath = GetExportFilePath();

                // Check if appending to existing file
                if (_exportAppendMode && !_exportIncludeTimestamp)
                {
                    // Check file size limit
                    if (File.Exists(filePath))
                    {
                        var fileInfo = new FileInfo(filePath);
                        if (fileInfo.Length > _exportMaxFileSizeMB * 1024 * 1024)
                        {
                            // Rotate file - add timestamp
                            var dir = Path.GetDirectoryName(filePath) ?? GetExportDirectory();
                            var name = Path.GetFileNameWithoutExtension(filePath);
                            var ext = Path.GetExtension(filePath);
                            var rotatedPath = Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                            File.Move(filePath, rotatedPath);
                        }
                    }
                }

                var sb = new System.Text.StringBuilder();
                var separator = _exportFormat == ExportFormat.CSV ? "," : "\t";

                // Header (only for new files or non-append mode)
                bool writeHeader = !_exportAppendMode || !File.Exists(filePath);

                if (writeHeader)
                {
                    var headers = new List<string>
                    {
                        "ExportTime", "Ticker", "Spot", "ZeroGamma",
                        "MajorPosVol", "MajorNegVol", "MajorPosOI", "MajorNegOI",
                        "SumGexVol", "SumGexOI", "MinDTE", "SecMinDTE"
                    };
                    sb.AppendLine(string.Join(separator, headers));

                    // Strike headers
                    var strikeHeaders = new List<string> { "Strike", "GexByVolume", "GexByOI" };
                    if (_exportIncludePriors)
                    {
                        strikeHeaders.AddRange(new[] { "Prior1Min", "Prior5Min", "Prior10Min", "Prior15Min", "Prior30Min" });
                    }
                    sb.AppendLine("--- STRIKES ---");
                    sb.AppendLine(string.Join(separator, strikeHeaders));
                }

                // Summary row
                var summaryRow = new List<string>
                {
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    data.Ticker,
                    data.Spot.ToString("0.00", CultureInfo.InvariantCulture),
                    data.ZeroGamma.ToString("0.00", CultureInfo.InvariantCulture),
                    data.MajorPosVol.ToString("0.00", CultureInfo.InvariantCulture),
                    data.MajorNegVol.ToString("0.00", CultureInfo.InvariantCulture),
                    data.MajorPosOI.ToString("0.00", CultureInfo.InvariantCulture),
                    data.MajorNegOI.ToString("0.00", CultureInfo.InvariantCulture),
                    data.SumGexVol.ToString("0.00", CultureInfo.InvariantCulture),
                    data.SumGexOI.ToString("0.00", CultureInfo.InvariantCulture),
                    data.MinDte.ToString(),
                    data.SecMinDte.ToString()
                };
                sb.AppendLine(string.Join(separator, summaryRow));
                sb.AppendLine();

                // Strike data
                foreach (var strike in data.Strikes)
                {
                    var strikeRow = new List<string>
                    {
                        strike.Strike.ToString("0", CultureInfo.InvariantCulture),
                        strike.GexByVolume.ToString("0.00", CultureInfo.InvariantCulture),
                        strike.GexByOI.ToString("0.00", CultureInfo.InvariantCulture)
                    };

                    if (_exportIncludePriors)
                    {
                        for (int i = 0; i < 5; i++)
                        {
                            var priorVal = strike.Priors.Length > i ? strike.Priors[i] : 0m;
                            strikeRow.Add(priorVal.ToString("0.00", CultureInfo.InvariantCulture));
                        }
                    }

                    sb.AppendLine(string.Join(separator, strikeRow));
                }

                // Export alerts if enabled
                if (_exportIncludeAlerts && _activeAlerts.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("--- ACTIVE ALERTS ---");
                    sb.AppendLine(string.Join(separator, new[] { "Strike", "Change", "ChangePercent", "Direction", "Period" }));

                    foreach (var alert in _activeAlerts)
                    {
                        var alertRow = new List<string>
                        {
                            alert.Strike.ToString("0", CultureInfo.InvariantCulture),
                            alert.Change.ToString("0.00", CultureInfo.InvariantCulture),
                            alert.ChangePercent.ToString("0.00", CultureInfo.InvariantCulture),
                            alert.IsIncrease ? "UP" : "DOWN",
                            alert.TimePeriod
                        };
                        sb.AppendLine(string.Join(separator, alertRow));
                    }
                }

                // Write to file
                if (_exportAppendMode && File.Exists(filePath))
                {
                    File.AppendAllText(filePath, sb.ToString());
                }
                else
                {
                    File.WriteAllText(filePath, sb.ToString());
                }

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

        private void DrawExportStatus(RenderContext context)
        {
            if (!_enableDataExport || !_showExportStatus) return;

            var font = new RenderFont("Arial", 9);
            var fontSmall = new RenderFont("Arial", 8);
            int panelWidth = 180;
            int panelHeight = 50;
            int x = 10;
            int y = ChartInfo.Region.Height - panelHeight - 10;

            // Background
            var bgRect = new Rectangle(x, y, panelWidth, panelHeight);
            context.FillRectangle(Color.FromArgb(200, 30, 30, 35), bgRect);
            context.DrawRectangle(new RenderPen(Color.FromArgb(150, 80, 80, 90), 1), bgRect);

            int ty = y + 5;
            int labelX = x + 8;

            // Header
            var headerColor = !string.IsNullOrEmpty(_lastExportError) ? Color.OrangeRed : Color.FromArgb(255, 100, 200, 255);
            context.DrawString("📁 Data Export", font, headerColor, labelX, ty);
            ty += 14;

            // Status
            if (!string.IsNullOrEmpty(_lastExportError))
            {
                context.DrawString($"Error: {_lastExportError}", fontSmall, Color.OrangeRed, labelX, ty);
            }
            else if (_lastExportTime.HasValue)
            {
                var elapsed = (DateTime.Now - _lastExportTime.Value).TotalSeconds;
                var statusText = elapsed < 5 ? "✓ Exported" : $"Last: {_lastExportTime.Value:HH:mm:ss}";
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
        #endregion

        #region GEX Cluster Detection (Section 18)
        private bool _enableClusterDetection = false;
        /// <summary>Detect clusters of high GEX concentration</summary>
        [Display(GroupName = "18. 🧲 GEX Clusters", Name = "Enable Cluster Detection", Description = "Identify zones of concentrated GEX", Order = 10)]
        public bool EnableClusterDetection
        {
            get => _enableClusterDetection;
            set { _enableClusterDetection = value; RequestRecalc(); }
        }

        private int _clusterMinStrikes = 3;
        [Display(GroupName = "18. 🧲 GEX Clusters", Name = "Min Strikes for Cluster", Order = 20)]
        [Range(2, 10)]
        public int ClusterMinStrikes
        {
            get => _clusterMinStrikes;
            set { _clusterMinStrikes = Math.Clamp(value, 2, 10); RequestRecalc(); }
        }

        private decimal _clusterGexThreshold = 0.5m;
        [Display(GroupName = "18. 🧲 GEX Clusters", Name = "Min GEX % of Max", Description = "Threshold as percentage of max GEX", Order = 30)]
        [Range(0.1, 1.0)]
        public decimal ClusterGexThreshold
        {
            get => _clusterGexThreshold;
            set { _clusterGexThreshold = Math.Clamp(value, 0.1m, 1.0m); RequestRecalc(); }
        }

        private bool _showClusterZones = true;
        [Display(GroupName = "18. 🧲 GEX Clusters", Name = "Show Cluster Zones", Order = 40)]
        public bool ShowClusterZones
        {
            get => _showClusterZones;
            set { _showClusterZones = value; RequestRecalc(); }
        }

        private Color _clusterPositiveColor = Color.FromArgb(40, 0, 255, 120);
        [Display(GroupName = "18. 🧲 GEX Clusters", Name = "Positive Cluster Color", Order = 50)]
        public Color ClusterPositiveColor
        {
            get => _clusterPositiveColor;
            set { _clusterPositiveColor = value; RequestRecalc(); }
        }

        private Color _clusterNegativeColor = Color.FromArgb(40, 255, 80, 80);
        [Display(GroupName = "18. 🧲 GEX Clusters", Name = "Negative Cluster Color", Order = 60)]
        public Color ClusterNegativeColor
        {
            get => _clusterNegativeColor;
            set { _clusterNegativeColor = value; RequestRecalc(); }
        }

        private bool _showClusterBoundaries = true;
        [Display(GroupName = "18. 🧲 GEX Clusters", Name = "Show Cluster Boundaries", Order = 70)]
        public bool ShowClusterBoundaries
        {
            get => _showClusterBoundaries;
            set { _showClusterBoundaries = value; RequestRecalc(); }
        }

        private int _clusterBoundaryThickness = 2;
        [Display(GroupName = "18. 🧲 GEX Clusters", Name = "Boundary Thickness", Order = 80)]
        [Range(1, 5)]
        public int ClusterBoundaryThickness
        {
            get => _clusterBoundaryThickness;
            set { _clusterBoundaryThickness = Math.Clamp(value, 1, 5); RequestRecalc(); }
        }

        private bool _showClusterLabels = true;
        [Display(GroupName = "18. 🧲 GEX Clusters", Name = "Show Cluster Labels", Order = 90)]
        public bool ShowClusterLabels
        {
            get => _showClusterLabels;
            set { _showClusterLabels = value; RequestRecalc(); }
        }

        private bool _showClusterStrength = true;
        [Display(GroupName = "18. 🧲 GEX Clusters", Name = "Show Strength Indicator", Order = 100)]
        public bool ShowClusterStrength
        {
            get => _showClusterStrength;
            set { _showClusterStrength = value; RequestRecalc(); }
        }

        private bool _clusterAsSupportResistance = true;
        [Display(GroupName = "18. 🧲 GEX Clusters", Name = "Mark as S/R Levels", Description = "Label clusters as Support/Resistance", Order = 110)]
        public bool ClusterAsSupportResistance
        {
            get => _clusterAsSupportResistance;
            set { _clusterAsSupportResistance = value; RequestRecalc(); }
        }

        private int _maxClustersToShow = 5;
        [Display(GroupName = "18. 🧲 GEX Clusters", Name = "Max Clusters to Show", Order = 120)]
        [Range(1, 20)]
        public int MaxClustersToShow
        {
            get => _maxClustersToShow;
            set { _maxClustersToShow = Math.Clamp(value, 1, 20); RequestRecalc(); }
        }
        #endregion

        #region GEX Statistics Panel (Section 19)
        private bool _showStatisticsPanel = false;
        /// <summary>Show statistical analysis panel</summary>
        [Display(GroupName = "19. 📊 Statistics Panel", Name = "Show Statistics Panel", Description = "Display GEX statistical metrics", Order = 10)]
        public bool ShowStatisticsPanel
        {
            get => _showStatisticsPanel;
            set { _showStatisticsPanel = value; RequestRecalc(); }
        }

        private PanelPosition _statsPanelPosition = PanelPosition.TopLeft;
        [Display(GroupName = "19. 📊 Statistics Panel", Name = "Panel Position", Order = 20)]
        public PanelPosition StatsPanelPosition
        {
            get => _statsPanelPosition;
            set { _statsPanelPosition = value; RequestRecalc(); }
        }

        private int _statsPanelX = 10;
        [Display(GroupName = "19. 📊 Statistics Panel", Name = "X Offset (px)", Order = 30)]
        [Range(0, 5000)]
        public int StatsPanelX
        {
            get => _statsPanelX;
            set { _statsPanelX = Math.Clamp(value, 0, 5000); RequestRecalc(); }
        }

        private int _statsPanelY = 200;
        [Display(GroupName = "19. 📊 Statistics Panel", Name = "Y Offset (px)", Order = 40)]
        [Range(0, 5000)]
        public int StatsPanelY
        {
            get => _statsPanelY;
            set { _statsPanelY = Math.Clamp(value, 0, 5000); RequestRecalc(); }
        }

        private int _statsPanelFontSize = 9;
        [Display(GroupName = "19. 📊 Statistics Panel", Name = "Font Size", Order = 50)]
        [Range(7, 14)]
        public int StatsPanelFontSize
        {
            get => _statsPanelFontSize;
            set { _statsPanelFontSize = Math.Clamp(value, 7, 14); RequestRecalc(); }
        }

        private Color _statsPanelBackColor = Color.FromArgb(220, 25, 25, 35);
        [Display(GroupName = "19. 📊 Statistics Panel", Name = "Background Color", Order = 60)]
        public Color StatsPanelBackColor
        {
            get => _statsPanelBackColor;
            set { _statsPanelBackColor = value; RequestRecalc(); }
        }

        private Color _statsPanelBorderColor = Color.FromArgb(180, 80, 80, 100);
        [Display(GroupName = "19. 📊 Statistics Panel", Name = "Border Color", Order = 65)]
        public Color StatsPanelBorderColor
        {
            get => _statsPanelBorderColor;
            set { _statsPanelBorderColor = value; RequestRecalc(); }
        }

        private Color _statsPanelHeaderColor = Color.FromArgb(255, 100, 180, 255);
        [Display(GroupName = "19. 📊 Statistics Panel", Name = "Header Color", Order = 70)]
        public Color StatsPanelHeaderColor
        {
            get => _statsPanelHeaderColor;
            set { _statsPanelHeaderColor = value; RequestRecalc(); }
        }

        private bool _showStatsPercentiles = true;
        [Display(GroupName = "19. 📊 Statistics Panel", Name = "Show Percentiles", Description = "Display P25, P50, P75 values", Order = 80)]
        public bool ShowStatsPercentiles
        {
            get => _showStatsPercentiles;
            set { _showStatsPercentiles = value; RequestRecalc(); }
        }

        private bool _showStatsRatio = true;
        [Display(GroupName = "19. 📊 Statistics Panel", Name = "Show Pos/Neg Ratio", Order = 90)]
        public bool ShowStatsRatio
        {
            get => _showStatsRatio;
            set { _showStatsRatio = value; RequestRecalc(); }
        }

        private bool _showGexScore = true;
        [Display(GroupName = "19. 📊 Statistics Panel", Name = "Show GEX Score", Description = "Sentiment score -100 to +100", Order = 100)]
        public bool ShowGexScore
        {
            get => _showGexScore;
            set { _showGexScore = value; RequestRecalc(); }
        }

        private bool _showScoreGauge = true;
        [Display(GroupName = "19. 📊 Statistics Panel", Name = "Show Score Gauge", Order = 110)]
        public bool ShowScoreGauge
        {
            get => _showScoreGauge;
            set { _showScoreGauge = value; RequestRecalc(); }
        }

        private int _scoreGaugeWidth = 100;
        [Display(GroupName = "19. 📊 Statistics Panel", Name = "Gauge Width (px)", Order = 120)]
        [Range(50, 200)]
        public int ScoreGaugeWidth
        {
            get => _scoreGaugeWidth;
            set { _scoreGaugeWidth = Math.Clamp(value, 50, 200); RequestRecalc(); }
        }
        #endregion

        #region Price Target Projections (Section 20)
        private bool _showPriceTargets = false;
        /// <summary>Show price targets based on GEX levels</summary>
        [Display(GroupName = "20. 🎯 Price Targets", Name = "Show Price Targets", Description = "Project targets from major GEX levels", Order = 10)]
        public bool ShowPriceTargets
        {
            get => _showPriceTargets;
            set { _showPriceTargets = value; RequestRecalc(); }
        }

        private bool _showDistanceToZeroGamma = true;
        [Display(GroupName = "20. 🎯 Price Targets", Name = "Distance to Zero Gamma", Order = 20)]
        public bool ShowDistanceToZeroGamma
        {
            get => _showDistanceToZeroGamma;
            set { _showDistanceToZeroGamma = value; RequestRecalc(); }
        }

        private bool _showDistanceToMajorLevels = true;
        [Display(GroupName = "20. 🎯 Price Targets", Name = "Distance to Major Levels", Order = 30)]
        public bool ShowDistanceToMajorLevels
        {
            get => _showDistanceToMajorLevels;
            set { _showDistanceToMajorLevels = value; RequestRecalc(); }
        }

        private bool _showAttractionIndicator = true;
        [Display(GroupName = "20. 🎯 Price Targets", Name = "Show Attraction Indicator", Description = "Price attraction strength", Order = 40)]
        public bool ShowAttractionIndicator
        {
            get => _showAttractionIndicator;
            set { _showAttractionIndicator = value; RequestRecalc(); }
        }

        private bool _showTargetArrows = true;
        [Display(GroupName = "20. 🎯 Price Targets", Name = "Show Target Arrows", Order = 50)]
        public bool ShowTargetArrows
        {
            get => _showTargetArrows;
            set { _showTargetArrows = value; RequestRecalc(); }
        }

        private Color _targetArrowUpColor = Color.FromArgb(255, 0, 200, 100);
        [Display(GroupName = "20. 🎯 Price Targets", Name = "Upward Target Color", Order = 60)]
        public Color TargetArrowUpColor
        {
            get => _targetArrowUpColor;
            set { _targetArrowUpColor = value; RequestRecalc(); }
        }

        private Color _targetArrowDownColor = Color.FromArgb(255, 200, 80, 80);
        [Display(GroupName = "20. 🎯 Price Targets", Name = "Downward Target Color", Order = 70)]
        public Color TargetArrowDownColor
        {
            get => _targetArrowDownColor;
            set { _targetArrowDownColor = value; RequestRecalc(); }
        }

        private bool _showTargetPanel = true;
        [Display(GroupName = "20. 🎯 Price Targets", Name = "Show Targets Panel", Order = 80)]
        public bool ShowTargetPanel
        {
            get => _showTargetPanel;
            set { _showTargetPanel = value; RequestRecalc(); }
        }

        private PanelPosition _targetPanelPosition = PanelPosition.BottomLeft;
        [Display(GroupName = "20. 🎯 Price Targets", Name = "Panel Position", Order = 90)]
        public PanelPosition TargetPanelPosition
        {
            get => _targetPanelPosition;
            set { _targetPanelPosition = value; RequestRecalc(); }
        }

        private int _targetPanelX = 200;
        [Display(GroupName = "20. 🎯 Price Targets", Name = "X Offset (px)", Order = 100)]
        [Range(0, 5000)]
        public int TargetPanelX
        {
            get => _targetPanelX;
            set { _targetPanelX = Math.Clamp(value, 0, 5000); RequestRecalc(); }
        }

        private int _targetPanelY = 10;
        [Display(GroupName = "20. 🎯 Price Targets", Name = "Y Offset (px)", Order = 110)]
        [Range(0, 5000)]
        public int TargetPanelY
        {
            get => _targetPanelY;
            set { _targetPanelY = Math.Clamp(value, 0, 5000); RequestRecalc(); }
        }

        private bool _showAttractionBars = true;
        [Display(GroupName = "20. 🎯 Price Targets", Name = "Show Attraction Bars", Order = 120)]
        public bool ShowAttractionBars
        {
            get => _showAttractionBars;
            set { _showAttractionBars = value; RequestRecalc(); }
        }
        #endregion

        #region Session Time Filter (Section 21)
        private bool _enableSessionFilter = false;
        /// <summary>Filter and highlight by market session</summary>
        [Display(GroupName = "21. ⏰ Session Filter", Name = "Enable Session Filter", Description = "Market hours awareness and 0DTE tracking", Order = 10)]
        public bool EnableSessionFilter
        {
            get => _enableSessionFilter;
            set { _enableSessionFilter = value; RequestRecalc(); }
        }

        private TimeSpan _marketOpenTime = new TimeSpan(9, 30, 0);
        [Display(GroupName = "21. ⏰ Session Filter", Name = "Market Open Time", Order = 20)]
        public TimeSpan MarketOpenTime
        {
            get => _marketOpenTime;
            set { _marketOpenTime = value; RequestRecalc(); }
        }

        private TimeSpan _marketCloseTime = new TimeSpan(16, 0, 0);
        [Display(GroupName = "21. ⏰ Session Filter", Name = "Market Close Time", Order = 30)]
        public TimeSpan MarketCloseTime
        {
            get => _marketCloseTime;
            set { _marketCloseTime = value; RequestRecalc(); }
        }

        private bool _showSessionIndicator = true;
        [Display(GroupName = "21. ⏰ Session Filter", Name = "Show Session Indicator", Order = 40)]
        public bool ShowSessionIndicator
        {
            get => _showSessionIndicator;
            set { _showSessionIndicator = value; RequestRecalc(); }
        }

        private bool _dimOutsideSession = true;
        [Display(GroupName = "21. ⏰ Session Filter", Name = "Dim Outside Session", Order = 50)]
        public bool DimOutsideSession
        {
            get => _dimOutsideSession;
            set { _dimOutsideSession = value; RequestRecalc(); }
        }

        private int _dimOpacity = 100;
        [Display(GroupName = "21. ⏰ Session Filter", Name = "Dim Opacity", Order = 60)]
        [Range(0, 200)]
        public int DimOpacity
        {
            get => _dimOpacity;
            set { _dimOpacity = Math.Clamp(value, 0, 200); RequestRecalc(); }
        }

        private bool _highlight0DTE = true;
        [Display(GroupName = "21. ⏰ Session Filter", Name = "Highlight 0DTE Data", Description = "Special badge for zero days to expiry", Order = 70)]
        public bool Highlight0DTE
        {
            get => _highlight0DTE;
            set { _highlight0DTE = value; RequestRecalc(); }
        }

        private Color _zeroDteBadgeColor = Color.FromArgb(255, 255, 200, 0);
        [Display(GroupName = "21. ⏰ Session Filter", Name = "0DTE Badge Color", Order = 80)]
        public Color ZeroDteBadgeColor
        {
            get => _zeroDteBadgeColor;
            set { _zeroDteBadgeColor = value; RequestRecalc(); }
        }

        private bool _showTimeToExpiry = true;
        [Display(GroupName = "21. ⏰ Session Filter", Name = "Show Time to Expiry", Order = 90)]
        public bool ShowTimeToExpiry
        {
            get => _showTimeToExpiry;
            set { _showTimeToExpiry = value; RequestRecalc(); }
        }

        private bool _showSessionProgress = true;
        [Display(GroupName = "21. ⏰ Session Filter", Name = "Show Session Progress", Order = 100)]
        public bool ShowSessionProgress
        {
            get => _showSessionProgress;
            set { _showSessionProgress = value; RequestRecalc(); }
        }

        private Color _sessionActiveColor = Color.FromArgb(255, 0, 200, 100);
        [Display(GroupName = "21. ⏰ Session Filter", Name = "Active Session Color", Order = 110)]
        public Color SessionActiveColor
        {
            get => _sessionActiveColor;
            set { _sessionActiveColor = value; RequestRecalc(); }
        }

        private Color _sessionClosedColor = Color.FromArgb(255, 150, 150, 150);
        [Display(GroupName = "21. ⏰ Session Filter", Name = "Closed Session Color", Order = 120)]
        public Color SessionClosedColor
        {
            get => _sessionClosedColor;
            set { _sessionClosedColor = value; RequestRecalc(); }
        }
        #endregion

        #region Theme Presets (Section 22)
        /// <summary>Visual theme preset</summary>
        public enum ThemePreset { Custom, Dark, Light, HighContrast, Classic, Neon }
        private ThemePreset _themePreset = ThemePreset.Custom;
        [Display(GroupName = "22. 🎨 Theme Presets", Name = "Theme Preset", Description = "Quick apply color theme", Order = 10)]
        public ThemePreset ActiveTheme
        {
            get => _themePreset;
            set { _themePreset = value; ApplyTheme(value); RequestRecalc(); }
        }

        private bool _autoSaveTheme = false;
        [Display(GroupName = "22. 🎨 Theme Presets", Name = "Auto-Save Custom Theme", Order = 20)]
        public bool AutoSaveTheme
        {
            get => _autoSaveTheme;
            set { _autoSaveTheme = value; RequestRecalc(); }
        }

        private void ApplyTheme(ThemePreset theme)
        {
            switch (theme)
            {
                case ThemePreset.Dark:
                    _positiveGexColor = Color.FromArgb(200, 0, 180, 80);
                    _negativeGexColor = Color.FromArgb(200, 180, 50, 50);
                    _infoPanelBackColor = Color.FromArgb(230, 15, 15, 20);
                    _infoPanelTextColor = Color.FromArgb(255, 200, 200, 200);
                    _infoPanelBorderColor = Color.FromArgb(150, 50, 50, 60);
                    _infoPanelHeaderColor = Color.FromArgb(255, 0, 150, 200);
                    _zeroGammaColor = Color.FromArgb(255, 255, 200, 0);
                    _majorPosColor = Color.FromArgb(255, 0, 200, 80);
                    _majorNegColor = Color.FromArgb(255, 200, 60, 60);
                    break;

                case ThemePreset.Light:
                    _positiveGexColor = Color.FromArgb(180, 0, 150, 70);
                    _negativeGexColor = Color.FromArgb(180, 180, 40, 40);
                    _infoPanelBackColor = Color.FromArgb(240, 240, 240, 245);
                    _infoPanelTextColor = Color.FromArgb(255, 30, 30, 40);
                    _infoPanelBorderColor = Color.FromArgb(180, 180, 180, 190);
                    _infoPanelHeaderColor = Color.FromArgb(255, 0, 100, 180);
                    _zeroGammaColor = Color.FromArgb(255, 200, 150, 0);
                    _majorPosColor = Color.FromArgb(255, 0, 150, 60);
                    _majorNegColor = Color.FromArgb(255, 180, 40, 40);
                    break;

                case ThemePreset.HighContrast:
                    _positiveGexColor = Color.FromArgb(255, 0, 255, 0);
                    _negativeGexColor = Color.FromArgb(255, 255, 0, 0);
                    _infoPanelBackColor = Color.FromArgb(255, 0, 0, 0);
                    _infoPanelTextColor = Color.FromArgb(255, 255, 255, 255);
                    _infoPanelBorderColor = Color.FromArgb(255, 255, 255, 255);
                    _infoPanelHeaderColor = Color.FromArgb(255, 0, 255, 255);
                    _zeroGammaColor = Color.FromArgb(255, 255, 255, 0);
                    _majorPosColor = Color.FromArgb(255, 0, 255, 0);
                    _majorNegColor = Color.FromArgb(255, 255, 0, 0);
                    break;

                case ThemePreset.Classic:
                    _positiveGexColor = Color.FromArgb(180, 34, 139, 34);
                    _negativeGexColor = Color.FromArgb(180, 178, 34, 34);
                    _infoPanelBackColor = Color.FromArgb(220, 25, 25, 30);
                    _infoPanelTextColor = Color.White;
                    _infoPanelBorderColor = Color.FromArgb(150, 80, 80, 90);
                    _infoPanelHeaderColor = Color.FromArgb(255, 70, 130, 180);
                    _zeroGammaColor = Color.Gold;
                    _majorPosColor = Color.LimeGreen;
                    _majorNegColor = Color.OrangeRed;
                    break;

                case ThemePreset.Neon:
                    _positiveGexColor = Color.FromArgb(200, 0, 255, 150);
                    _negativeGexColor = Color.FromArgb(200, 255, 0, 100);
                    _infoPanelBackColor = Color.FromArgb(230, 10, 5, 20);
                    _infoPanelTextColor = Color.FromArgb(255, 0, 255, 255);
                    _infoPanelBorderColor = Color.FromArgb(180, 150, 0, 255);
                    _infoPanelHeaderColor = Color.FromArgb(255, 255, 0, 255);
                    _zeroGammaColor = Color.FromArgb(255, 255, 255, 0);
                    _majorPosColor = Color.FromArgb(255, 0, 255, 100);
                    _majorNegColor = Color.FromArgb(255, 255, 50, 150);
                    break;

                case ThemePreset.Custom:
                default:
                    // Keep current settings
                    break;
            }
        }
        #endregion

        #region API Status & Diagnostics (Section 23)
        private bool _showApiDiagnostics = false;
        /// <summary>Show API connection diagnostics</summary>
        [Display(GroupName = "23. 🔌 API Diagnostics", Name = "Show API Diagnostics", Description = "Display connection status and latency", Order = 10)]
        public bool ShowApiDiagnostics
        {
            get => _showApiDiagnostics;
            set { _showApiDiagnostics = value; RequestRecalc(); }
        }

        private PanelPosition _diagPanelPosition = PanelPosition.BottomLeft;
        [Display(GroupName = "23. 🔌 API Diagnostics", Name = "Panel Position", Order = 20)]
        public PanelPosition DiagPanelPosition
        {
            get => _diagPanelPosition;
            set { _diagPanelPosition = value; RequestRecalc(); }
        }

        private int _diagPanelX = 200;
        [Display(GroupName = "23. 🔌 API Diagnostics", Name = "X Offset (px)", Order = 30)]
        [Range(0, 5000)]
        public int DiagPanelX
        {
            get => _diagPanelX;
            set { _diagPanelX = Math.Clamp(value, 0, 5000); RequestRecalc(); }
        }

        private int _diagPanelY = 70;
        [Display(GroupName = "23. 🔌 API Diagnostics", Name = "Y Offset (px)", Order = 40)]
        [Range(0, 5000)]
        public int DiagPanelY
        {
            get => _diagPanelY;
            set { _diagPanelY = Math.Clamp(value, 0, 5000); RequestRecalc(); }
        }

        private int _diagPanelFontSize = 9;
        [Display(GroupName = "23. 🔌 API Diagnostics", Name = "Font Size", Order = 50)]
        [Range(7, 14)]
        public int DiagPanelFontSize
        {
            get => _diagPanelFontSize;
            set { _diagPanelFontSize = Math.Clamp(value, 7, 14); RequestRecalc(); }
        }

        private bool _showConnectionStatus = true;
        [Display(GroupName = "23. 🔌 API Diagnostics", Name = "Show Connection Status", Order = 60)]
        public bool ShowConnectionStatus
        {
            get => _showConnectionStatus;
            set { _showConnectionStatus = value; RequestRecalc(); }
        }

        private bool _showLatency = true;
        [Display(GroupName = "23. 🔌 API Diagnostics", Name = "Show Latency", Order = 70)]
        public bool ShowLatency
        {
            get => _showLatency;
            set { _showLatency = value; RequestRecalc(); }
        }

        private bool _showErrorCount = true;
        [Display(GroupName = "23. 🔌 API Diagnostics", Name = "Show Error Count", Order = 80)]
        public bool ShowErrorCount
        {
            get => _showErrorCount;
            set { _showErrorCount = value; RequestRecalc(); }
        }

        private bool _showActivityLog = true;
        [Display(GroupName = "23. 🔌 API Diagnostics", Name = "Show Activity Log", Order = 90)]
        public bool ShowActivityLog
        {
            get => _showActivityLog;
            set { _showActivityLog = value; RequestRecalc(); }
        }

        private int _activityLogMaxItems = 5;
        [Display(GroupName = "23. 🔌 API Diagnostics", Name = "Activity Log Max Items", Order = 100)]
        [Range(3, 15)]
        public int ActivityLogMaxItems
        {
            get => _activityLogMaxItems;
            set { _activityLogMaxItems = Math.Clamp(value, 3, 15); RequestRecalc(); }
        }

        private Color _diagPanelBackColor = Color.FromArgb(220, 20, 20, 30);
        [Display(GroupName = "23. 🔌 API Diagnostics", Name = "Background Color", Order = 110)]
        public Color DiagPanelBackColor
        {
            get => _diagPanelBackColor;
            set { _diagPanelBackColor = value; RequestRecalc(); }
        }

        private Color _connectedColor = Color.FromArgb(255, 0, 200, 100);
        [Display(GroupName = "23. 🔌 API Diagnostics", Name = "Connected Color", Order = 120)]
        public Color ConnectedColor
        {
            get => _connectedColor;
            set { _connectedColor = value; RequestRecalc(); }
        }

        private Color _disconnectedColor = Color.FromArgb(255, 200, 80, 80);
        [Display(GroupName = "23. 🔌 API Diagnostics", Name = "Disconnected Color", Order = 130)]
        public Color DisconnectedColor
        {
            get => _disconnectedColor;
            set { _disconnectedColor = value; RequestRecalc(); }
        }

        private void AddActivityLog(string message)
        {
            lock (_sync)
            {
                _apiDiagnostics.ActivityLog.Insert(0, (DateTime.Now, message));
                while (_apiDiagnostics.ActivityLog.Count > _activityLogMaxItems * 2)
                {
                    _apiDiagnostics.ActivityLog.RemoveAt(_apiDiagnostics.ActivityLog.Count - 1);
                }
            }
        }

        private void UpdateLatencyStats(double latencyMs)
        {
            lock (_sync)
            {
                _apiDiagnostics.LastLatencyMs = latencyMs;
                _latencyHistory.Enqueue(latencyMs);
                while (_latencyHistory.Count > MaxLatencyHistory)
                {
                    _latencyHistory.Dequeue();
                }
                _apiDiagnostics.AverageLatencyMs = _latencyHistory.Average();
            }
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
                DisposeSoundPlayer();
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
                    _apiDiagnostics.IsConnected = false;
                }
                AddActivityLog("⚠ API Key missing");
                RedrawChart();
                return;
            }

            try
            {
                var tickerStr = GetTickerString();
                var url = $"https://api.gexbot.com/{tickerStr}/classic/{_aggregation}?key={_apiKey}";

                // Start timing for latency
                _apiStopwatch.Restart();
                AddActivityLog($"→ Requesting {tickerStr}...");

                var response = await _httpClient.GetAsync(url);

                // Stop timing
                _apiStopwatch.Stop();
                double latencyMs = _apiStopwatch.Elapsed.TotalMilliseconds;
                UpdateLatencyStats(latencyMs);

                if (!response.IsSuccessStatusCode)
                {
                    lock (_sync)
                    {
                        _error = $"API Error: {response.StatusCode}";
                        _data = null;
                        _apiDiagnostics.IsConnected = false;
                        _apiDiagnostics.ErrorCount++;
                        _apiDiagnostics.LastErrorTime = DateTime.Now;
                        _apiDiagnostics.LastErrorMessage = $"HTTP {(int)response.StatusCode}";
                    }
                    AddActivityLog($"✗ Error: {response.StatusCode}");
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
                    _apiDiagnostics.IsConnected = true;
                    _apiDiagnostics.SuccessCount++;
                    _apiDiagnostics.LastSuccessTime = DateTime.Now;
                }

                AddActivityLog($"✓ Loaded ({latencyMs:0}ms)");

                // Calculate statistics if enabled
                if (parsed != null && _showStatisticsPanel)
                {
                    CalculateGexStatistics(parsed);
                }

                // Detect clusters if enabled
                if (parsed != null && _enableClusterDetection)
                {
                    DetectGexClusters(parsed);
                }

                // Calculate price targets if enabled
                if (parsed != null && _showPriceTargets)
                {
                    CalculatePriceTargets(parsed);
                }

                // Auto-export if enabled
                if (_enableDataExport && _exportOnRefresh && parsed != null)
                {
                    ExportData(parsed);
                }

                RedrawChart();
            }
            catch (Exception ex)
            {
                _apiStopwatch.Stop();
                lock (_sync)
                {
                    _error = $"Load error: {ex.Message}";
                    _data = null;
                    _apiDiagnostics.IsConnected = false;
                    _apiDiagnostics.ErrorCount++;
                    _apiDiagnostics.LastErrorTime = DateTime.Now;
                    _apiDiagnostics.LastErrorMessage = ex.Message;
                }
                AddActivityLog($"✗ Exception: {ex.Message.Substring(0, Math.Min(30, ex.Message.Length))}");
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

            // Draw gamma zones first (behind everything)
            if (_showGammaZones && snapshot.ZeroGamma > 0)
            {
                DrawGammaZones(context, snapshot, factor, fullWidth);
            }

            // Draw strike grid (behind bars)
            DrawStrikeGrid(context, factor, fullWidth);

            // Convert strikes to chart price
            var convertedStrikes = snapshot.Strikes.Select(s => new
            {
                OriginalStrike = s.Strike,
                ChartStrike = _enableConversion ? RoundToStep(s.Strike * factor, _priceStep) : s.Strike,
                GexValue = _dataSource == GexDataSource.Volume ? s.GexByVolume : s.GexByOI,
                s.Priors
            }).ToList();

            // Apply strike filter if enabled
            _totalStrikesCount = convertedStrikes.Count;
            if (_enableStrikeFilter)
            {
                convertedStrikes = ApplyStrikeFilter(convertedStrikes, snapshot.Spot);
            }
            _filteredStrikesCount = convertedStrikes.Count;

            // Draw spot price line
            if (_showSpotPriceLine && snapshot.Spot > 0)
            {
                DrawSpotPriceLine(context, snapshot.Spot, factor, fullWidth);
            }

            // Detect alerts for unusual gamma changes
            var alertedStrikes = new HashSet<decimal>();
            if (_enableAlerts)
            {
                _activeAlerts = DetectAlerts(snapshot.Strikes);
                foreach (var alert in _activeAlerts)
                {
                    alertedStrikes.Add(alert.Strike);
                }
            }
            else
            {
                _activeAlerts.Clear();
            }

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

            // Pulse effect for alerts
            double pulseAlpha = 1.0;
            if (_alertPulseEffect && _activeAlerts.Count > 0)
            {
                var elapsed = (DateTime.Now - _lastPulseTime).TotalMilliseconds;
                pulseAlpha = 0.5 + 0.5 * Math.Sin(elapsed / 200.0);
            }

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

                // Check if this strike is near spot price
                bool isNearSpot = IsStrikeNearSpot(strike.OriginalStrike, snapshot.Spot);

                // Check if this strike has an alert
                var alertInfo = _activeAlerts.FirstOrDefault(a => a.Strike == strike.OriginalStrike);
                bool hasAlert = alertInfo != null;

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

                // Draw near-spot highlight background
                if (isNearSpot)
                {
                    var highlightRect = new Rectangle(0, top - 1, fullWidth, _barThicknessPx + 2);
                    context.FillRectangle(_nearSpotHighlightColor, highlightRect);
                }

                context.FillRectangle(fillColor, barRect);

                // Draw alert highlight or normal outline
                if (hasAlert)
                {
                    var alertColor = alertInfo!.IsIncrease ? _alertIncreaseColor : _alertDecreaseColor;
                    int alpha = _alertPulseEffect ? (int)(255 * pulseAlpha) : 255;
                    var alertPen = new RenderPen(Color.FromArgb(alpha, alertColor), _alertOutlineThickness);
                    context.DrawRectangle(alertPen, barRect);

                    // Draw glow effect
                    var glowRect = new Rectangle(barRect.X - 2, barRect.Y - 2, barRect.Width + 4, barRect.Height + 4);
                    var glowPen = new RenderPen(Color.FromArgb(alpha / 3, alertColor), 1);
                    context.DrawRectangle(glowPen, glowRect);
                }
                else if (_showBarOutline)
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

                // Draw direction arrow
                if (_showDirectionArrows && strike.Priors.Length > 0)
                {
                    int periodIdx = _arrowTimePeriod switch
                    {
                        ArrowTimePeriod.OneMin => 0,
                        ArrowTimePeriod.FiveMin => 1,
                        ArrowTimePeriod.TenMin => 2,
                        ArrowTimePeriod.FifteenMin => 3,
                        ArrowTimePeriod.ThirtyMin => 4,
                        _ => 1
                    };

                    if (strike.Priors.Length > periodIdx)
                    {
                        var priorGex = strike.Priors[periodIdx];
                        var change = strike.GexValue - priorGex;

                        if (Math.Abs(change) >= _arrowMinChange)
                        {
                            var arrowFont = new RenderFont("Arial", _arrowFontSize);
                            var arrow = change > 0 ? "▲" : "▼";
                            var arrowColor = change > 0 ? _arrowUpColor : _arrowDownColor;

                            // Position arrow at the end of the bar
                            int arrowX;
                            if (isPositive)
                            {
                                arrowX = xCenter + barWidth + _arrowOffsetPx;
                            }
                            else
                            {
                                arrowX = xCenter - barWidth - _arrowFontSize - _arrowOffsetPx;
                            }
                            int arrowY = top + (_barThicknessPx - _arrowFontSize) / 2;

                            context.DrawString(arrow, arrowFont, arrowColor, arrowX, arrowY);
                        }
                    }
                }

                // Draw sparkline (mini trend chart)
                if (_showSparklines && strike.Priors.Length >= 2)
                {
                    // Build data points: current + priors (reversed so oldest is first)
                    var dataPoints = new List<decimal> { strike.GexValue };
                    dataPoints.AddRange(strike.Priors.Take(5));
                    dataPoints.Reverse(); // Now: oldest → newest (left → right)

                    // Calculate sparkline position (after the bar, offset by labels/arrows)
                    int sparkX;
                    int labelOffset = 0;
                    if (_showValues) labelOffset += 55;
                    if (_showStrikeLabels) labelOffset += 45;
                    if (_showDirectionArrows) labelOffset += _arrowFontSize + 5;

                    if (isPositive)
                    {
                        sparkX = xCenter + barWidth + _sparklineOffsetPx + labelOffset;
                    }
                    else
                    {
                        sparkX = xCenter - barWidth - _sparklineWidth - _sparklineOffsetPx - labelOffset;
                    }

                    int sparkY = top + (_barThicknessPx - _sparklineHeight) / 2;
                    DrawSparkline(context, sparkX, sparkY, dataPoints);
                }
            }

            // Draw major levels
            DrawMajorLevels(context, snapshot, factor, fullWidth);

            // Draw GEX cluster zones (Section 18)
            if (_enableClusterDetection && _showClusterZones)
            {
                DrawGexClusters(context, snapshot, factor, fullWidth);
            }

            // Draw info panel
            if (_showInfoPanel)
            {
                DrawInfoPanel(context, snapshot, lastLoad);
            }

            // Draw alert history panel
            if (_enableAlertHistory && _showAlertHistoryPanel && _alertHistory.Count > 0)
            {
                DrawAlertHistoryPanel(context);
            }

            // Draw multi-timeframe comparison panel
            if (_showMultiTimeframePanel)
            {
                DrawMultiTimeframePanel(context, snapshot);
            }

            // Draw statistics panel (Section 19)
            if (_showStatisticsPanel && _gexStatistics != null)
            {
                DrawStatisticsPanel(context);
            }

            // Draw price targets panel (Section 20)
            if (_showPriceTargets && _priceTargets.Count > 0)
            {
                DrawPriceTargetsPanel(context, snapshot, factor);
            }

            // Draw session indicator (Section 21)
            if (_enableSessionFilter && _showSessionIndicator)
            {
                DrawSessionIndicator(context, snapshot);
            }

            // Draw API diagnostics panel (Section 23)
            if (_showApiDiagnostics)
            {
                DrawApiDiagnosticsPanel(context);
            }

            // Draw export status
            DrawExportStatus(context);
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
            // Use compact mode if enabled
            if (_compactMode)
            {
                DrawInfoPanelCompact(context, data, lastLoad);
                return;
            }

            var font = new RenderFont("Arial", _infoPanelFontSize);
            var fontBold = new RenderFont("Arial", _infoPanelFontSize + 1);
            var fontSmall = new RenderFont("Arial", _infoPanelFontSize - 1);
            var lineHeight = _infoPanelFontSize + 6;
            var sectionGap = 8;

            // Calculate panel dimensions - larger to fit all sections
            int panelWidth = Math.Max(280, _infoPanelBarWidth + 140);
            int col1 = 85;  // Label column width
            int col2 = 70;  // Strike column width
            int col3 = panelWidth - col1 - col2 - 30; // Value column

            // Calculate total height based on content (including alerts section if enabled)
            int numRows = 22; // Approximate rows including headers and spacing
            int alertRows = (_alertShowInPanel && _enableAlerts && _activeAlerts.Count > 0) 
                ? Math.Min(_activeAlerts.Count, 5) + 2 // header + alerts + spacing
                : 0;
            int panelHeight = lineHeight * (numRows + alertRows) + sectionGap * 6 + 30;

            // Calculate position based on alignment
            var (x, y) = CalcPanelXY(_infoPanelPosition, panelWidth, panelHeight, _infoPanelX, _infoPanelY);

            // Draw background
            var backRect = new Rectangle(x, y, panelWidth, panelHeight);
            context.FillRectangle(_infoPanelBackColor, backRect);
            context.DrawRectangle(new RenderPen(_infoPanelBorderColor, 1), backRect);

            int ty = y + 8;
            int labelX = x + 10;
            int strikeX = x + col1 + 10;
            int valueX = x + col1 + col2 + 15;

            // ═══════════════════════════════════════════════════════════
            // SECTION: UPDATE
            // ═══════════════════════════════════════════════════════════
            DrawSectionHeader(context, "update", x, ty, panelWidth, fontBold, _infoPanelHeaderColor);
            ty += lineHeight + 4;

            // Date
            context.DrawString("date", font, Color.Gray, labelX, ty);
            var dateStr = lastLoad.HasValue ? lastLoad.Value.ToString("M/d/yyyy") : "-";
            context.DrawString(dateStr, font, _infoPanelTextColor, valueX, ty);
            ty += lineHeight;

            // Time
            context.DrawString("time", font, Color.Gray, labelX, ty);
            var timeStr = lastLoad.HasValue ? lastLoad.Value.ToString("h:mm:ss tt") : "-";
            context.DrawString(timeStr, font, _infoPanelTextColor, valueX, ty);
            ty += lineHeight;

            // Spot
            context.DrawString("spot", font, Color.Gray, labelX, ty);
            context.DrawString($"{data.Spot:0.00}", font, _infoPanelAccentPositive, valueX, ty);
            ty += lineHeight + sectionGap;

            // ═══════════════════════════════════════════════════════════
            // SECTION: VOLUME
            // ═══════════════════════════════════════════════════════════
            DrawSectionHeader(context, "volume", x, ty, panelWidth, fontBold, _infoPanelHeaderColor);
            ty += lineHeight + 4;

            // Zero Gamma
            context.DrawString("zero gamma", font, _zeroGammaColor, labelX, ty);
            context.DrawString($"{data.ZeroGamma:0.00}", font, _zeroGammaColor, valueX, ty);
            ty += lineHeight;

            // Major Positive (Vol)
            context.DrawString("major positive", font, _majorPosColor, labelX, ty);
            context.DrawString($"{data.MajorPosVol:0.00}", font, _majorPosColor, valueX, ty);
            ty += lineHeight;

            // Major Negative (Vol)
            context.DrawString("major negative", font, _majorNegColor, labelX, ty);
            context.DrawString($"{data.MajorNegVol:0.00}", font, _majorNegColor, valueX, ty);
            ty += lineHeight;

            // Net GEX (Vol)
            context.DrawString("net gex", font, Color.Gray, labelX, ty);
            var gexVolColor = data.SumGexVol >= 0 ? _infoPanelAccentPositive : _infoPanelAccentNegative;
            context.DrawString(FormatCompact(data.SumGexVol), font, gexVolColor, valueX, ty);
            if (_showInfoPanelBars)
            {
                ty += lineHeight;
                DrawGexBar(context, labelX, ty, panelWidth - 30, 5, data.SumGexVol, data.SumGexVol, data.SumGexOI);
                ty += 8;
            }
            else
            {
                ty += lineHeight;
            }
            ty += sectionGap;

            // ═══════════════════════════════════════════════════════════
            // SECTION: OPEN INTEREST
            // ═══════════════════════════════════════════════════════════
            DrawSectionHeader(context, "open interest", x, ty, panelWidth, fontBold, _infoPanelHeaderColor);
            ty += lineHeight + 4;

            // Major Positive (OI)
            context.DrawString("major positive", font, _majorPosColor, labelX, ty);
            context.DrawString($"{data.MajorPosOI:0.00}", font, _majorPosColor, valueX, ty);
            ty += lineHeight;

            // Major Negative (OI)
            context.DrawString("major negative", font, _majorNegColor, labelX, ty);
            context.DrawString($"{data.MajorNegOI:0.00}", font, _majorNegColor, valueX, ty);
            ty += lineHeight;

            // Net GEX (OI)
            context.DrawString("net gex", font, Color.Gray, labelX, ty);
            var gexOiColor = data.SumGexOI >= 0 ? _infoPanelAccentPositive : _infoPanelAccentNegative;
            context.DrawString(FormatCompact(data.SumGexOI), font, gexOiColor, valueX, ty);
            if (_showInfoPanelBars)
            {
                ty += lineHeight;
                DrawGexBar(context, labelX, ty, panelWidth - 30, 5, data.SumGexOI, data.SumGexVol, data.SumGexOI);
                ty += 8;
            }
            else
            {
                ty += lineHeight;
            }
            ty += sectionGap;

            // ═══════════════════════════════════════════════════════════
            // SECTION: MAX CHANGE GEX (using priors data)
            // ═══════════════════════════════════════════════════════════
            DrawSectionHeader(context, "max change gex", x, ty, panelWidth, fontBold, _infoPanelHeaderColor);
            ty += lineHeight + 4;

            // For each time period, find the strike with the maximum GEX change
            string[] priorLabels = { "1 min", "5 min", "10 min", "15 min", "30 min" };

            // Check if any strike has priors data
            bool hasPriors = data.Strikes.Any(s => s.Priors.Length > 0);

            if (hasPriors)
            {
                for (int periodIndex = 0; periodIndex < priorLabels.Length; periodIndex++)
                {
                    // Find the strike with maximum absolute change for this time period
                    GexStrike? maxChangeStrike = null;
                    decimal maxChange = 0;

                    foreach (var strike in data.Strikes)
                    {
                        if (strike.Priors.Length > periodIndex)
                        {
                            var currentGex = _dataSource == GexDataSource.Volume ? strike.GexByVolume : strike.GexByOI;
                            var priorGex = strike.Priors[periodIndex];
                            var change = currentGex - priorGex;

                            if (Math.Abs(change) > Math.Abs(maxChange))
                            {
                                maxChange = change;
                                maxChangeStrike = strike;
                            }
                        }
                    }

                    if (maxChangeStrike != null)
                    {
                        context.DrawString(priorLabels[periodIndex], font, Color.Gray, labelX, ty);

                        // Strike color matches the change direction
                        var changeColor = maxChange >= 0 ? _infoPanelAccentPositive : _infoPanelAccentNegative;
                        context.DrawString($"{maxChangeStrike.Strike:0}", font, changeColor, strikeX, ty);
                        context.DrawString(FormatCompact(maxChange), font, changeColor, valueX, ty);
                        ty += lineHeight;
                    }
                }
            }
            else
            {
                // No priors data available - show current top strikes instead
                var topStrikes = data.Strikes
                    .OrderByDescending(s => Math.Abs(_dataSource == GexDataSource.Volume ? s.GexByVolume : s.GexByOI))
                    .Take(5)
                    .ToList();

                for (int i = 0; i < topStrikes.Count; i++)
                {
                    var strike = topStrikes[i];
                    var gexVal = _dataSource == GexDataSource.Volume ? strike.GexByVolume : strike.GexByOI;
                    var changeColor = gexVal >= 0 ? _infoPanelAccentPositive : _infoPanelAccentNegative;

                    context.DrawString($"#{i + 1}", font, Color.Gray, labelX, ty);
                    context.DrawString($"{strike.Strike:0}", font, Color.Cyan, strikeX, ty);
                    context.DrawString(FormatCompact(gexVal), font, changeColor, valueX, ty);
                    ty += lineHeight;
                }
            }

            // DTE badge at bottom
            ty += 4;
            var dteText = data.MinDte == 0 ? "0DTE" : $"{data.MinDte}DTE";
            var dteColor = data.MinDte == 0 ? Color.Gold : Color.White;
            if (data.MinDte == 0)
            {
                var badgeRect = new Rectangle(labelX - 2, ty - 2, 45, lineHeight);
                context.FillRectangle(Color.FromArgb(100, Color.Gold), badgeRect);
            }
            context.DrawString(dteText, fontSmall, dteColor, labelX, ty);
            context.DrawString($"/ {data.SecMinDte}DTE", fontSmall, Color.Gray, labelX + 50, ty);
            ty += lineHeight + sectionGap;

            // ═══════════════════════════════════════════════════════════
            // SECTION: ALERTS (if enabled and there are active alerts)
            // ═══════════════════════════════════════════════════════════
            if (_alertShowInPanel && _enableAlerts && _activeAlerts.Count > 0)
            {
                var alertHeaderColor = Color.FromArgb(255, 255, 150, 50);
                DrawSectionHeader(context, $"⚠ alerts ({_activeAlerts.Count})", x, ty, panelWidth, fontBold, alertHeaderColor);
                ty += lineHeight + 4;

                // Show up to 5 alerts
                var displayAlerts = _activeAlerts.Take(5).ToList();
                foreach (var alert in displayAlerts)
                {
                    var alertColor = alert.IsIncrease ? _alertIncreaseColor : _alertDecreaseColor;
                    var arrow = alert.IsIncrease ? "▲" : "▼";

                    // Strike with arrow
                    context.DrawString($"{arrow} {alert.Strike:0}", font, alertColor, labelX, ty);

                    // Change value
                    context.DrawString(FormatCompact(alert.Change), font, alertColor, strikeX, ty);

                    // Percentage
                    var pctText = $"{(alert.ChangePercent >= 0 ? "+" : "")}{alert.ChangePercent:0.0}%";
                    context.DrawString(pctText, fontSmall, alertColor, valueX, ty);

                    ty += lineHeight;
                }

                // Show "more..." if there are more alerts
                if (_activeAlerts.Count > 5)
                {
                    context.DrawString($"+{_activeAlerts.Count - 5} more...", fontSmall, Color.Gray, labelX, ty);
                    ty += lineHeight;
                }
            }
        }

        private void DrawInfoPanelCompact(RenderContext context, GexClassicData data, DateTime? lastLoad)
        {
            var font = new RenderFont("Arial", _infoPanelFontSize);
            var fontBold = new RenderFont("Arial", _infoPanelFontSize + 2);
            var fontSmall = new RenderFont("Arial", _infoPanelFontSize - 1);
            var lineHeight = _infoPanelFontSize + 5;

            // Compact panel dimensions - add extra row if filter or alerts are shown
            int panelWidth = 160;
            int extraRows = 0;
            if (_enableAlerts && _activeAlerts.Count > 0) extraRows++;
            if (_enableStrikeFilter && _showFilteredCount) extraRows++;
            int panelHeight = lineHeight * (5 + extraRows) + 20;

            // Calculate position based on alignment
            var (x, y) = CalcPanelXY(_infoPanelPosition, panelWidth, panelHeight, _infoPanelX, _infoPanelY);

            // Draw background
            var backRect = new Rectangle(x, y, panelWidth, panelHeight);
            context.FillRectangle(_infoPanelBackColor, backRect);
            context.DrawRectangle(new RenderPen(_infoPanelBorderColor, 1), backRect);

            int ty = y + 6;
            int labelX = x + 8;
            int valueX = x + 70;

            // Header: Ticker + DTE badge
            var tickerText = data.Ticker;
            context.DrawString(tickerText, fontBold, _infoPanelHeaderColor, labelX, ty);

            // DTE badge inline
            var dteText = data.MinDte == 0 ? "0DTE" : $"{data.MinDte}DTE";
            var dteColor = data.MinDte == 0 ? Color.Gold : Color.LightGray;
            int dteX = x + panelWidth - 45;
            if (data.MinDte == 0)
            {
                var badgeRect = new Rectangle(dteX - 3, ty - 1, 40, lineHeight - 2);
                context.FillRectangle(Color.FromArgb(80, Color.Gold), badgeRect);
            }
            context.DrawString(dteText, fontSmall, dteColor, dteX, ty + 2);
            ty += lineHeight + 2;

            // Spot
            context.DrawString("spot", font, Color.Gray, labelX, ty);
            context.DrawString($"{data.Spot:0.00}", font, _infoPanelAccentPositive, valueX, ty);
            ty += lineHeight;

            // Zero Gamma
            context.DrawString("zero γ", font, _zeroGammaColor, labelX, ty);
            context.DrawString($"{data.ZeroGamma:0.00}", font, _zeroGammaColor, valueX, ty);
            ty += lineHeight;

            // Net GEX (based on data source)
            var netGex = _dataSource == GexDataSource.Volume ? data.SumGexVol : data.SumGexOI;
            var gexColor = netGex >= 0 ? _infoPanelAccentPositive : _infoPanelAccentNegative;
            var gexLabel = _dataSource == GexDataSource.Volume ? "net γ vol" : "net γ oi";
            context.DrawString(gexLabel, font, Color.Gray, labelX, ty);
            context.DrawString(FormatCompact(netGex), font, gexColor, valueX, ty);
            ty += lineHeight;

            // Alerts count (if any)
            if (_enableAlerts && _activeAlerts.Count > 0)
            {
                var alertColor = Color.FromArgb(255, 255, 150, 50);
                context.DrawString("alerts", font, alertColor, labelX, ty);
                context.DrawString($"⚠ {_activeAlerts.Count}", font, alertColor, valueX, ty);
                ty += lineHeight;
            }

            // Filtered strikes count (if filter enabled)
            if (_enableStrikeFilter && _showFilteredCount)
            {
                context.DrawString("strikes", font, Color.Gray, labelX, ty);
                context.DrawString($"{_filteredStrikesCount}/{_totalStrikesCount}", font, _spotPriceLineColor, valueX, ty);
                ty += lineHeight;
            }

            // Last update time (small, at bottom)
            ty += 2;
            var timeStr = lastLoad.HasValue ? lastLoad.Value.ToString("HH:mm:ss") : "-";
            context.DrawString(timeStr, fontSmall, Color.DimGray, labelX, ty);
        }

        private void DrawAlertHistoryPanel(RenderContext context)
        {
            List<AlertHistoryEntry> historySnapshot;
            lock (_sync)
            {
                historySnapshot = _alertHistory.Take(_alertHistoryDisplayCount).ToList();
            }

            if (historySnapshot.Count == 0) return;

            var font = new RenderFont("Arial", _alertHistoryFontSize);
            var fontBold = new RenderFont("Arial", _alertHistoryFontSize + 1);
            var fontSmall = new RenderFont("Arial", _alertHistoryFontSize - 1);
            var lineHeight = _alertHistoryFontSize + 4;

            // Panel dimensions
            int panelWidth = 200;
            int headerHeight = lineHeight + 6;
            int panelHeight = headerHeight + (lineHeight * historySnapshot.Count) + 12;

            // Calculate position based on alignment
            var (x, y) = CalcPanelXY(_alertHistoryPosition, panelWidth, panelHeight, _alertHistoryPanelX, _alertHistoryPanelY);

            // Draw background
            var backRect = new Rectangle(x, y, panelWidth, panelHeight);
            context.FillRectangle(_alertHistoryBackColor, backRect);
            context.DrawRectangle(new RenderPen(_alertHistoryBorderColor, 1), backRect);

            int ty = y + 4;
            int labelX = x + 8;

            // Header
            var headerColor = Color.FromArgb(255, 255, 180, 80);
            var headerRect = new Rectangle(x, ty - 2, panelWidth, headerHeight);
            context.FillRectangle(Color.FromArgb(40, headerColor), headerRect);
            context.DrawString($"📋 Alert History ({_alertHistory.Count})", fontBold, headerColor, labelX, ty);
            ty += headerHeight + 2;

            // Column positions
            int timeCol = labelX;
            int strikeCol = x + 55;
            int changeCol = x + 105;
            int pctCol = x + 155;

            // Draw each history entry
            foreach (var entry in historySnapshot)
            {
                var alertColor = entry.IsIncrease ? _alertIncreaseColor : _alertDecreaseColor;
                var arrow = entry.IsIncrease ? "▲" : "▼";

                // Time
                var entryTime = entry.Timestamp.ToString("HH:mm:ss");
                context.DrawString(entryTime, fontSmall, Color.Gray, timeCol, ty);

                // Strike with arrow
                context.DrawString($"{arrow}{entry.Strike:0}", font, alertColor, strikeCol, ty);

                // Change value
                context.DrawString(FormatCompact(entry.Change), font, alertColor, changeCol, ty);

                // Percentage (compact)
                var pctText = $"{(entry.ChangePercent >= 0 ? "+" : "")}{entry.ChangePercent:0}%";
                context.DrawString(pctText, fontSmall, alertColor, pctCol, ty);

                ty += lineHeight;
            }

            // Show if there are more items
            if (_alertHistory.Count > _alertHistoryDisplayCount)
            {
                var moreText = $"...+{_alertHistory.Count - _alertHistoryDisplayCount} more";
                context.DrawString(moreText, fontSmall, Color.DimGray, labelX, ty);
            }
        }

        private void DrawMultiTimeframePanel(RenderContext context, GexClassicData data)
        {
            if (!_showMultiTimeframePanel || data.Strikes.Count == 0) return;

            // Check if any strike has priors data
            bool hasPriors = data.Strikes.Any(s => s.Priors.Length > 0);
            if (!hasPriors) return;

            var font = new RenderFont("Arial", _mtfPanelFontSize);
            var fontBold = new RenderFont("Arial", _mtfPanelFontSize + 1);
            var fontSmall = new RenderFont("Arial", _mtfPanelFontSize - 1);
            var lineHeight = _mtfPanelFontSize + 5;
            var sectionGap = 6;

            // Period labels and indices
            string[] periodLabels = { "1 min", "5 min", "10 min", "15 min", "30 min" };
            int periodCount = 5;

            // Calculate panel dimensions based on content
            int panelWidth = _mtfCompactMode ? 180 : (_showMtfTrendBars ? 320 : 260);
            int headerHeight = lineHeight + 4;

            // Calculate content rows
            int netGexRows = _showMtfNetGex ? periodCount : 0;
            int topStrikesRows = _showMtfTopStrikes ? (_mtfCompactMode ? periodCount : periodCount * (_mtfTopStrikesCount + 1)) : 0;
            int separatorRows = (_showMtfNetGex && _showMtfTopStrikes) ? 1 : 0;

            int totalRows = netGexRows + topStrikesRows + separatorRows;
            int panelHeight = headerHeight + (lineHeight * totalRows) + sectionGap * 3 + 15;

            // Calculate position based on alignment
            var (x, y) = CalcPanelXY(_mtfPanelPosition, panelWidth, panelHeight, _mtfPanelX, _mtfPanelY);

            // Draw background
            var backRect = new Rectangle(x, y, panelWidth, panelHeight);
            context.FillRectangle(_mtfPanelBackColor, backRect);
            context.DrawRectangle(new RenderPen(_mtfPanelBorderColor, 1), backRect);

            int ty = y + 4;
            int labelX = x + 10;

            // Header
            var headerRect = new Rectangle(x, ty - 2, panelWidth, headerHeight);
            context.FillRectangle(Color.FromArgb(40, _mtfPanelHeaderColor), headerRect);
            context.DrawString("⏱ Multi-Timeframe GEX", fontBold, _mtfPanelHeaderColor, labelX, ty);
            ty += headerHeight + sectionGap;

            // Calculate GEX changes for all periods
            var periodChanges = new List<(int Period, decimal NetChange, decimal NetChangePercent, List<(decimal Strike, decimal Change, decimal Pct)> TopMovers)>();

            for (int periodIdx = 0; periodIdx < periodCount; periodIdx++)
            {
                decimal totalCurrentGex = 0;
                decimal totalPriorGex = 0;
                var strikeChanges = new List<(decimal Strike, decimal Change, decimal Pct)>();

                foreach (var strike in data.Strikes)
                {
                    if (strike.Priors.Length <= periodIdx) continue;

                    var currentGex = _dataSource == GexDataSource.Volume ? strike.GexByVolume : strike.GexByOI;
                    var priorGex = strike.Priors[periodIdx];
                    var change = currentGex - priorGex;

                    totalCurrentGex += currentGex;
                    totalPriorGex += priorGex;

                    decimal pct = priorGex != 0 ? (change / Math.Abs(priorGex)) * 100m : (currentGex != 0 ? 100m : 0m);
                    strikeChanges.Add((strike.Strike, change, pct));
                }

                decimal netChange = totalCurrentGex - totalPriorGex;
                decimal netPct = totalPriorGex != 0 ? (netChange / Math.Abs(totalPriorGex)) * 100m : 0m;

                // Get top movers (sorted by absolute change)
                var topMovers = strikeChanges
                    .OrderByDescending(sc => Math.Abs(sc.Change))
                    .Take(_mtfTopStrikesCount)
                    .ToList();

                periodChanges.Add((periodIdx, netChange, netPct, topMovers));
            }

            // Find max absolute change for heatmap scaling
            decimal maxAbsChange = periodChanges.Max(p => Math.Abs(p.NetChange));
            if (maxAbsChange <= 0) maxAbsChange = 1;

            // Column positions
            int periodCol = labelX;
            int changeCol = x + 60;
            int pctCol = x + 130;
            int barCol = x + 180;

            // Draw Net GEX section
            if (_showMtfNetGex)
            {
                context.DrawString("Net GEX Change", fontSmall, Color.Gray, labelX, ty);
                ty += lineHeight;

                foreach (var pc in periodChanges)
                {
                    var changeColor = pc.NetChange >= 0 ? _mtfPositiveColor : _mtfNegativeColor;
                    var arrow = pc.NetChange >= 0 ? "▲" : "▼";

                    // Heatmap background if enabled
                    if (_showMtfHeatmap)
                    {
                        int intensity = (int)(Math.Abs(pc.NetChange) / maxAbsChange * 80);
                        var heatColor = pc.NetChange >= 0
                            ? Color.FromArgb(intensity, 0, 200, 100)
                            : Color.FromArgb(intensity, 200, 60, 60);
                        var rowRect = new Rectangle(x + 2, ty - 1, panelWidth - 4, lineHeight);
                        context.FillRectangle(heatColor, rowRect);
                    }

                    // Period label
                    context.DrawString(periodLabels[pc.Period], font, Color.LightGray, periodCol, ty);

                    // Change value with arrow
                    context.DrawString($"{arrow} {FormatCompact(pc.NetChange)}", font, changeColor, changeCol, ty);

                    // Percentage
                    if (_showMtfPercentChange)
                    {
                        var pctText = $"{(pc.NetChangePercent >= 0 ? "+" : "")}{pc.NetChangePercent:0.0}%";
                        context.DrawString(pctText, fontSmall, changeColor, pctCol, ty);
                    }

                    // Trend bar
                    if (_showMtfTrendBars && !_mtfCompactMode)
                    {
                        DrawMtfTrendBar(context, barCol, ty + 2, _mtfTrendBarWidth, lineHeight - 4,
                            pc.NetChange, maxAbsChange, changeColor);
                    }

                    ty += lineHeight;
                }

                ty += sectionGap;
            }

            // Draw separator
            if (_showMtfNetGex && _showMtfTopStrikes)
            {
                var sepPen = new RenderPen(Color.FromArgb(60, 150, 150, 150), 1);
                context.DrawLine(sepPen, x + 5, ty, x + panelWidth - 5, ty);
                ty += sectionGap;
            }

            // Draw Top Movers section
            if (_showMtfTopStrikes)
            {
                context.DrawString("Top Movers by Period", fontSmall, Color.Gray, labelX, ty);
                ty += lineHeight;

                if (_mtfCompactMode)
                {
                    // Compact: just show top mover per period
                    foreach (var pc in periodChanges)
                    {
                        if (pc.TopMovers.Count == 0) continue;

                        var top = pc.TopMovers[0];
                        var changeColor = top.Change >= 0 ? _mtfPositiveColor : _mtfNegativeColor;
                        var arrow = top.Change >= 0 ? "▲" : "▼";

                        // Period
                        context.DrawString(periodLabels[pc.Period], fontSmall, Color.Gray, periodCol, ty);

                        // Strike
                        context.DrawString($"{top.Strike:0}", font, Color.Cyan, changeCol, ty);

                        // Change
                        context.DrawString($"{arrow}{FormatCompact(top.Change)}", font, changeColor, pctCol, ty);

                        ty += lineHeight;
                    }
                }
                else
                {
                    // Full: show header for each period with top movers
                    foreach (var pc in periodChanges)
                    {
                        // Period header
                        var periodHeaderColor = Color.FromArgb(180, _mtfPanelHeaderColor);
                        context.DrawString($"• {periodLabels[pc.Period]}", fontSmall, periodHeaderColor, periodCol, ty);
                        ty += lineHeight;

                        int strikeCol = labelX + 15;
                        int valCol = x + 80;
                        int movPctCol = x + 140;

                        foreach (var mover in pc.TopMovers)
                        {
                            var changeColor = mover.Change >= 0 ? _mtfPositiveColor : _mtfNegativeColor;
                            var arrow = mover.Change >= 0 ? "▲" : "▼";

                            // Strike
                            context.DrawString($"{mover.Strike:0}", font, Color.Cyan, strikeCol, ty);

                            // Change value
                            context.DrawString($"{arrow}{FormatCompact(mover.Change)}", font, changeColor, valCol, ty);

                            // Percentage
                            if (_showMtfPercentChange)
                            {
                                var pctText = $"{(mover.Pct >= 0 ? "+" : "")}{mover.Pct:0}%";
                                context.DrawString(pctText, fontSmall, changeColor, movPctCol, ty);
                            }

                            ty += lineHeight;
                        }
                    }
                }
            }
        }

        private void DrawMtfTrendBar(RenderContext context, int x, int y, int maxWidth, int height, decimal value, decimal maxValue, Color color)
        {
            // Background
            context.FillRectangle(Color.FromArgb(40, 100, 100, 100), new Rectangle(x, y, maxWidth, height));

            // Calculate bar width
            double ratio = maxValue != 0 ? (double)Math.Abs(value) / (double)maxValue : 0;
            int barWidth = (int)(maxWidth * ratio * 0.9);
            barWidth = Math.Clamp(barWidth, 0, maxWidth);

            if (barWidth > 0)
            {
                // Gradient effect using two rectangles
                int halfBar = barWidth / 2;
                var lightColor = Color.FromArgb(200, color);
                var darkColor = Color.FromArgb(120, color);

                context.FillRectangle(lightColor, new Rectangle(x, y, halfBar, height));
                context.FillRectangle(darkColor, new Rectangle(x + halfBar, y, barWidth - halfBar, height));

                // Highlight at start
                context.FillRectangle(Color.FromArgb(100, Color.White), new Rectangle(x, y, 2, height));
            }
        }

        private void DrawSectionHeader(RenderContext context, string title, int x, int y, int width, RenderFont font, Color color)
        {
            // Draw section header with underline effect
            int headerHeight = (int)font.Size + 6;
            var headerRect = new Rectangle(x, y, width, headerHeight);
            context.FillRectangle(Color.FromArgb(40, color), headerRect);
            context.DrawString(title, font, color, x + 10, y + 2);

            // Subtle line under header
            int lineY = y + (int)font.Size + 5;
            var linePen = new RenderPen(Color.FromArgb(60, color), 1);
            context.DrawLine(linePen, x + 5, lineY, x + width - 5, lineY);
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

        private void DrawSparkline(RenderContext context, int x, int y, List<decimal> dataPoints)
        {
            if (dataPoints.Count < 2) return;

            int width = _sparklineWidth;
            int height = _sparklineHeight;

            // Draw background
            if (_sparklineShowBackground)
            {
                var bgRect = new Rectangle(x, y, width, height);
                context.FillRectangle(_sparklineBackColor, bgRect);
            }

            // Find min/max for scaling
            decimal minVal = dataPoints.Min();
            decimal maxVal = dataPoints.Max();
            decimal range = maxVal - minVal;

            // Handle flat line case
            if (range == 0)
            {
                range = Math.Abs(maxVal) > 0 ? Math.Abs(maxVal) * 0.1m : 1m;
                minVal -= range / 2;
                maxVal += range / 2;
                range = maxVal - minVal;
            }

            // Add padding to range
            decimal padding = range * 0.1m;
            minVal -= padding;
            maxVal += padding;
            range = maxVal - minVal;

            // Calculate point positions
            int numPoints = dataPoints.Count;
            float xStep = (float)(width - 2) / (numPoints - 1);
            var points = new List<(int X, int Y)>();

            for (int i = 0; i < numPoints; i++)
            {
                int px = x + 1 + (int)(i * xStep);
                // Invert Y: higher values should be at top
                float ratio = (float)((dataPoints[i] - minVal) / range);
                int py = y + height - 1 - (int)(ratio * (height - 2));
                py = Math.Clamp(py, y + 1, y + height - 2);
                points.Add((px, py));
            }

            // Draw zero line if enabled and zero is in range
            if (_sparklineShowZeroLine && minVal < 0 && maxVal > 0)
            {
                float zeroRatio = (float)((0 - minVal) / range);
                int zeroY = y + height - 1 - (int)(zeroRatio * (height - 2));
                var zeroPen = new RenderPen(Color.FromArgb(80, 200, 200, 200), 1) { DashStyle = DashStyle.Dot };
                context.DrawLine(zeroPen, x + 1, zeroY, x + width - 1, zeroY);
            }

            // Determine line color based on trend (first vs last point)
            Color lineColor;
            decimal firstVal = dataPoints[0];
            decimal lastVal = dataPoints[^1];
            if (lastVal > firstVal)
            {
                lineColor = _sparklineUpColor;
            }
            else if (lastVal < firstVal)
            {
                lineColor = _sparklineDownColor;
            }
            else
            {
                lineColor = _sparklineNeutralColor;
            }

            var linePen = new RenderPen(lineColor, _sparklineThickness);

            // Draw line segments
            for (int i = 0; i < points.Count - 1; i++)
            {
                context.DrawLine(linePen, points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y);
            }

            // Draw data points (dots)
            if (_sparklineShowDots)
            {
                int dotSize = Math.Max(2, _sparklineThickness + 1);
                foreach (var pt in points)
                {
                    var dotRect = new Rectangle(pt.X - dotSize / 2, pt.Y - dotSize / 2, dotSize, dotSize);
                    context.FillRectangle(lineColor, dotRect);
                }

                // Highlight the last (current) point
                var lastPt = points[^1];
                int highlightSize = dotSize + 2;
                var highlightRect = new Rectangle(lastPt.X - highlightSize / 2, lastPt.Y - highlightSize / 2, highlightSize, highlightSize);
                context.FillRectangle(Color.White, highlightRect);
                var innerRect = new Rectangle(lastPt.X - dotSize / 2, lastPt.Y - dotSize / 2, dotSize, dotSize);
                context.FillRectangle(lineColor, innerRect);
            }
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

        private void DrawGammaZones(RenderContext context, GexClassicData data, decimal factor, int fullWidth)
        {
            // Calculate the Zero Gamma price in chart coordinates
            decimal zeroGammaPrice = _enableConversion ? RoundToStep(data.ZeroGamma * factor, _priceStep) : data.ZeroGamma;
            int zeroGammaY = ChartInfo.PriceChartContainer.GetYByPrice(zeroGammaPrice, false);

            int chartHeight = ChartInfo.Region.Height;

            // Positive gamma zone is ABOVE the Zero Gamma line (price > ZG)
            // In chart coordinates, Y=0 is at top, so above ZG means Y < zeroGammaY
            var positiveZoneRect = new Rectangle(0, 0, fullWidth, zeroGammaY);
            context.FillRectangle(_positiveZoneColor, positiveZoneRect);

            // Negative gamma zone is BELOW the Zero Gamma line (price < ZG)
            var negativeZoneRect = new Rectangle(0, zeroGammaY, fullWidth, chartHeight - zeroGammaY);
            context.FillRectangle(_negativeZoneColor, negativeZoneRect);

            // Draw zone boundary
            if (_showZoneBoundary)
            {
                var boundaryPen = new RenderPen(_zoneBoundaryColor, 2);
                context.DrawLine(boundaryPen, 0, zeroGammaY, fullWidth, zeroGammaY);
            }

            // Draw zone labels
            if (_showZoneLabels)
            {
                var labelFont = new RenderFont("Arial", 10);

                int labelX;
                switch (_zoneLabelPos)
                {
                    case ZoneLabelAlign.Left:
                        labelX = 10;
                        break;
                    case ZoneLabelAlign.Center:
                        labelX = fullWidth / 2 - 50;
                        break;
                    case ZoneLabelAlign.Right:
                    default:
                        labelX = fullWidth - 120;
                        break;
                }

                // Positive zone label (dealer long gamma = supportive)
                var posLabel = "▲ POSITIVE GAMMA";
                var posLabelY = Math.Max(20, zeroGammaY - 30);
                context.DrawString(posLabel, labelFont, Color.FromArgb(120, _positiveGexColor), labelX, posLabelY);

                // Negative zone label (dealer short gamma = volatile)
                var negLabel = "▼ NEGATIVE GAMMA";
                var negLabelY = Math.Min(chartHeight - 20, zeroGammaY + 15);
                context.DrawString(negLabel, labelFont, Color.FromArgb(120, _negativeGexColor), labelX, negLabelY);
            }
        }

        private void DrawSpotPriceLine(RenderContext context, decimal spotPrice, decimal factor, int fullWidth)
        {
            // Convert spot price to chart coordinates
            decimal chartSpotPrice = _enableConversion ? RoundToStep(spotPrice * factor, _priceStep) : spotPrice;
            int spotY = ChartInfo.PriceChartContainer.GetYByPrice(chartSpotPrice, false);

            // Draw the spot price line
            var spotPen = new RenderPen(_spotPriceLineColor, _spotPriceLineThickness) { DashStyle = _spotPriceLineDash };
            context.DrawLine(spotPen, 0, spotY, fullWidth, spotY);

            // Draw spot price label
            if (_showSpotPriceLabel)
            {
                var labelFont = new RenderFont("Arial", 9);
                var labelText = $"SPOT: {spotPrice:0.00}";

                // Draw label background
                int labelWidth = EstimateTextWidth(labelText, labelFont) + 8;
                int labelHeight = 14;
                int labelX = fullWidth - labelWidth - 5;
                int labelY = spotY - labelHeight / 2;

                var labelBgRect = new Rectangle(labelX - 2, labelY - 1, labelWidth + 4, labelHeight + 2);
                context.FillRectangle(Color.FromArgb(200, 20, 20, 25), labelBgRect);
                context.DrawRectangle(new RenderPen(_spotPriceLineColor, 1), labelBgRect);

                context.DrawString(labelText, labelFont, _spotPriceLineColor, labelX, labelY);
            }
        }

        private List<T> ApplyStrikeFilter<T>(List<T> strikes, decimal spotPrice) where T : class
        {
            if (strikes.Count == 0 || spotPrice <= 0) return strikes;

            // Use reflection to get OriginalStrike and GexValue properties (since we're using anonymous type)
            var strikeType = strikes[0].GetType();
            var originalStrikeProp = strikeType.GetProperty("OriginalStrike");
            var gexValueProp = strikeType.GetProperty("GexValue");

            if (originalStrikeProp == null || gexValueProp == null) return strikes;

            var filtered = strikes.AsEnumerable();

            // Apply minimum GEX threshold filter
            if (_minGexThreshold > 0)
            {
                filtered = filtered.Where(s =>
                {
                    var gex = (decimal)(gexValueProp.GetValue(s) ?? 0m);
                    return Math.Abs(gex) >= _minGexThreshold;
                });
            }

            // Apply range filter based on mode
            switch (_strikeFilterMode)
            {
                case StrikeFilterMode.PercentFromSpot:
                    decimal minStrike = spotPrice * (1 - _strikeFilterPercent / 100m);
                    decimal maxStrike = spotPrice * (1 + _strikeFilterPercent / 100m);

                    filtered = filtered.Where(s =>
                    {
                        var strike = (decimal)(originalStrikeProp.GetValue(s) ?? 0m);
                        return strike >= minStrike && strike <= maxStrike;
                    });
                    break;

                case StrikeFilterMode.FixedRange:
                    if (_strikeFilterMinPrice > 0)
                    {
                        filtered = filtered.Where(s =>
                        {
                            var strike = (decimal)(originalStrikeProp.GetValue(s) ?? 0m);
                            return strike >= _strikeFilterMinPrice;
                        });
                    }
                    if (_strikeFilterMaxPrice > 0)
                    {
                        filtered = filtered.Where(s =>
                        {
                            var strike = (decimal)(originalStrikeProp.GetValue(s) ?? 0m);
                            return strike <= _strikeFilterMaxPrice;
                        });
                    }
                    break;

                case StrikeFilterMode.StrikeCount:
                    // Get strikes closest to spot price
                    filtered = filtered
                        .OrderBy(s =>
                        {
                            var strike = (decimal)(originalStrikeProp.GetValue(s) ?? 0m);
                            return Math.Abs(strike - spotPrice);
                        })
                        .Take(_strikeFilterCount);
                    break;
            }

            return filtered.ToList();
        }

        private bool IsStrikeNearSpot(decimal strike, decimal spotPrice)
        {
            if (!_highlightNearSpotStrikes || spotPrice <= 0) return false;
            decimal range = spotPrice * (_nearSpotRange / 100m);
            return Math.Abs(strike - spotPrice) <= range;
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

        private List<AlertInfo> DetectAlerts(List<GexStrike> strikes)
        {
            var alerts = new List<AlertInfo>();

            // Get the priors index based on selected time period
            int periodIndex = _alertTimePeriod switch
            {
                AlertTimePeriod.OneMin => 0,
                AlertTimePeriod.FiveMin => 1,
                AlertTimePeriod.TenMin => 2,
                AlertTimePeriod.FifteenMin => 3,
                AlertTimePeriod.ThirtyMin => 4,
                _ => 1
            };

            string periodLabel = _alertTimePeriod switch
            {
                AlertTimePeriod.OneMin => "1min",
                AlertTimePeriod.FiveMin => "5min",
                AlertTimePeriod.TenMin => "10min",
                AlertTimePeriod.FifteenMin => "15min",
                AlertTimePeriod.ThirtyMin => "30min",
                _ => "5min"
            };

            // Calculate changes for all strikes
            var strikeChanges = new List<(GexStrike Strike, decimal CurrentGex, decimal PriorGex, decimal Change, decimal ChangePercent)>();

            foreach (var strike in strikes)
            {
                if (strike.Priors.Length <= periodIndex) continue;

                var currentGex = _dataSource == GexDataSource.Volume ? strike.GexByVolume : strike.GexByOI;
                var priorGex = strike.Priors[periodIndex];
                var change = currentGex - priorGex;

                // Calculate percentage change (avoid division by zero)
                decimal changePercent = 0;
                if (priorGex != 0)
                {
                    changePercent = (change / Math.Abs(priorGex)) * 100m;
                }
                else if (currentGex != 0)
                {
                    changePercent = 100m; // 100% if went from 0 to something
                }

                strikeChanges.Add((strike, currentGex, priorGex, change, changePercent));
            }

            if (strikeChanges.Count == 0) return alerts;

            // Apply threshold based on type
            switch (_alertThresholdType)
            {
                case AlertThresholdType.Percentage:
                    foreach (var sc in strikeChanges)
                    {
                        if (Math.Abs(sc.ChangePercent) >= _alertThresholdValue)
                        {
                            bool isIncrease = sc.Change > 0;
                            if ((isIncrease && _alertOnIncrease) || (!isIncrease && _alertOnDecrease))
                            {
                                alerts.Add(new AlertInfo
                                {
                                    Strike = sc.Strike.Strike,
                                    CurrentGex = sc.CurrentGex,
                                    PriorGex = sc.PriorGex,
                                    Change = sc.Change,
                                    ChangePercent = sc.ChangePercent,
                                    IsIncrease = isIncrease,
                                    TimePeriod = periodLabel
                                });
                            }
                        }
                    }
                    break;

                case AlertThresholdType.AbsoluteValue:
                    foreach (var sc in strikeChanges)
                    {
                        if (Math.Abs(sc.Change) >= _alertThresholdValue)
                        {
                            bool isIncrease = sc.Change > 0;
                            if ((isIncrease && _alertOnIncrease) || (!isIncrease && _alertOnDecrease))
                            {
                                alerts.Add(new AlertInfo
                                {
                                    Strike = sc.Strike.Strike,
                                    CurrentGex = sc.CurrentGex,
                                    PriorGex = sc.PriorGex,
                                    Change = sc.Change,
                                    ChangePercent = sc.ChangePercent,
                                    IsIncrease = isIncrease,
                                    TimePeriod = periodLabel
                                });
                            }
                        }
                    }
                    break;

                case AlertThresholdType.TopN:
                    int topN = (int)_alertThresholdValue;
                    if (topN <= 0) topN = 3;

                    // Get top N increases
                    if (_alertOnIncrease)
                    {
                        var topIncreases = strikeChanges
                            .Where(sc => sc.Change > 0)
                            .OrderByDescending(sc => sc.Change)
                            .Take(topN);

                        foreach (var sc in topIncreases)
                        {
                            alerts.Add(new AlertInfo
                            {
                                Strike = sc.Strike.Strike,
                                CurrentGex = sc.CurrentGex,
                                PriorGex = sc.PriorGex,
                                Change = sc.Change,
                                ChangePercent = sc.ChangePercent,
                                IsIncrease = true,
                                TimePeriod = periodLabel
                            });
                        }
                    }

                    // Get top N decreases
                    if (_alertOnDecrease)
                    {
                        var topDecreases = strikeChanges
                            .Where(sc => sc.Change < 0)
                            .OrderBy(sc => sc.Change)
                            .Take(topN);

                        foreach (var sc in topDecreases)
                        {
                            alerts.Add(new AlertInfo
                            {
                                Strike = sc.Strike.Strike,
                                CurrentGex = sc.CurrentGex,
                                PriorGex = sc.PriorGex,
                                Change = sc.Change,
                                ChangePercent = sc.ChangePercent,
                                IsIncrease = false,
                                TimePeriod = periodLabel
                            });
                        }
                    }
                    break;
            }

            // Sort alerts by absolute change (most significant first)
            var sortedAlerts = alerts.OrderByDescending(a => Math.Abs(a.Change)).ToList();

            // Add to alert history if enabled
            if (_enableAlertHistory && sortedAlerts.Count > 0)
            {
                AddAlertsToHistory(sortedAlerts);
            }

            return sortedAlerts;
        }

        private void AddAlertsToHistory(List<AlertInfo> alerts)
        {
            var now = DateTime.Now;
            var cutoffTime = now.AddSeconds(-_alertHistoryDuplicateWindowSec);
            bool soundPlayed = false;

            lock (_sync)
            {
                foreach (var alert in alerts)
                {
                    // Create history entry
                    var entry = new AlertHistoryEntry
                    {
                        Timestamp = now,
                        Strike = alert.Strike,
                        Change = alert.Change,
                        ChangePercent = alert.ChangePercent,
                        IsIncrease = alert.IsIncrease,
                        TimePeriod = alert.TimePeriod
                    };

                    // Check for duplicates within the time window
                    if (_alertHistoryDuplicateWindowSec > 0)
                    {
                        bool isDuplicate = _alertHistory.Any(h =>
                            h.Strike == entry.Strike &&
                            h.IsIncrease == entry.IsIncrease &&
                            h.Timestamp > cutoffTime);

                        if (isDuplicate) continue;
                    }

                    // Add to history (newest first)
                    _alertHistory.Insert(0, entry);

                    // Play sound for new alert (respecting soundOnlyFirstAlert setting)
                    if (_enableSoundAlerts && (!_soundOnlyFirstAlert || !soundPlayed))
                    {
                        PlayAlertSound(alert.IsIncrease);
                        soundPlayed = true;
                    }

                    // Trim if exceeded max
                    while (_alertHistory.Count > _alertHistoryMaxItems)
                    {
                        _alertHistory.RemoveAt(_alertHistory.Count - 1);
                    }
                }
            }
        }
        #endregion

        #region GEX Cluster Detection Methods (Section 18)
        private void DetectGexClusters(GexClassicData data)
        {
            lock (_sync)
            {
                _gexClusters.Clear();

                if (data?.Strikes == null || data.Strikes.Count < _clusterMinStrikes)
                    return;

                // Sort strikes by strike price
                var sortedStrikes = data.Strikes
                    .Select(s => new { Strike = s.Strike, Gex = _dataSource == GexDataSource.Volume ? s.GexByVolume : s.GexByOI })
                    .OrderBy(s => s.Strike)
                    .ToList();

                // Find max GEX for threshold calculation
                decimal maxAbsGex = sortedStrikes.Max(s => Math.Abs(s.Gex));
                decimal threshold = maxAbsGex * _clusterGexThreshold;

                // Detect clusters of consecutive strikes with significant GEX
                var positiveClusters = new List<GexCluster>();
                var negativeClusters = new List<GexCluster>();

                // Detect positive clusters
                int i = 0;
                while (i < sortedStrikes.Count)
                {
                    if (sortedStrikes[i].Gex >= threshold)
                    {
                        int start = i;
                        decimal totalGex = 0;
                        while (i < sortedStrikes.Count && sortedStrikes[i].Gex >= threshold * 0.5m)
                        {
                            totalGex += sortedStrikes[i].Gex;
                            i++;
                        }
                        int count = i - start;
                        if (count >= _clusterMinStrikes)
                        {
                            var cluster = new GexCluster
                            {
                                StartStrike = sortedStrikes[start].Strike,
                                EndStrike = sortedStrikes[i - 1].Strike,
                                CenterStrike = (sortedStrikes[start].Strike + sortedStrikes[i - 1].Strike) / 2,
                                TotalGex = totalGex,
                                AverageGex = totalGex / count,
                                StrikeCount = count,
                                IsPositive = true,
                                Strength = Math.Min(100, (totalGex / maxAbsGex) * 100)
                            };
                            positiveClusters.Add(cluster);
                        }
                    }
                    else
                    {
                        i++;
                    }
                }

                // Detect negative clusters
                i = 0;
                while (i < sortedStrikes.Count)
                {
                    if (sortedStrikes[i].Gex <= -threshold)
                    {
                        int start = i;
                        decimal totalGex = 0;
                        while (i < sortedStrikes.Count && sortedStrikes[i].Gex <= -threshold * 0.5m)
                        {
                            totalGex += sortedStrikes[i].Gex;
                            i++;
                        }
                        int count = i - start;
                        if (count >= _clusterMinStrikes)
                        {
                            var cluster = new GexCluster
                            {
                                StartStrike = sortedStrikes[start].Strike,
                                EndStrike = sortedStrikes[i - 1].Strike,
                                CenterStrike = (sortedStrikes[start].Strike + sortedStrikes[i - 1].Strike) / 2,
                                TotalGex = totalGex,
                                AverageGex = totalGex / count,
                                StrikeCount = count,
                                IsPositive = false,
                                Strength = Math.Min(100, (Math.Abs(totalGex) / maxAbsGex) * 100)
                            };
                            negativeClusters.Add(cluster);
                        }
                    }
                    else
                    {
                        i++;
                    }
                }

                // Combine and sort by strength, take top N
                var allClusters = positiveClusters.Concat(negativeClusters)
                    .OrderByDescending(c => c.Strength)
                    .Take(_maxClustersToShow)
                    .ToList();

                _gexClusters = allClusters;
            }
        }

        private void DrawGexClusters(RenderContext context, GexClassicData data, decimal factor, int fullWidth)
        {
            List<GexCluster> clusters;
            lock (_sync)
            {
                clusters = _gexClusters.ToList();
            }

            if (clusters.Count == 0) return;

            var font = new RenderFont("Arial", 9);
            var fontSmall = new RenderFont("Arial", 8);

            foreach (var cluster in clusters)
            {
                // Convert strikes to chart prices
                decimal startPrice = _enableConversion ? RoundToStep(cluster.StartStrike * factor, _priceStep) : cluster.StartStrike;
                decimal endPrice = _enableConversion ? RoundToStep(cluster.EndStrike * factor, _priceStep) : cluster.EndStrike;
                decimal centerPrice = _enableConversion ? RoundToStep(cluster.CenterStrike * factor, _priceStep) : cluster.CenterStrike;

                int yStart = ChartInfo.PriceChartContainer.GetYByPrice(startPrice, false);
                int yEnd = ChartInfo.PriceChartContainer.GetYByPrice(endPrice, false);
                int yCenter = ChartInfo.PriceChartContainer.GetYByPrice(centerPrice, false);

                // Ensure proper ordering (y increases downward in screen coords)
                int yTop = Math.Min(yStart, yEnd);
                int yBottom = Math.Max(yStart, yEnd);
                int height = Math.Max(10, yBottom - yTop);

                // Draw cluster zone
                var zoneColor = cluster.IsPositive ? _clusterPositiveColor : _clusterNegativeColor;
                var zoneRect = new Rectangle(0, yTop, fullWidth, height);
                context.FillRectangle(zoneColor, zoneRect);

                // Draw boundaries
                if (_showClusterBoundaries)
                {
                    var borderColor = cluster.IsPositive
                        ? Color.FromArgb(150, _clusterPositiveColor)
                        : Color.FromArgb(150, _clusterNegativeColor);
                    var boundaryPen = new RenderPen(borderColor, _clusterBoundaryThickness);
                    context.DrawLine(boundaryPen, 0, yTop, fullWidth, yTop);
                    context.DrawLine(boundaryPen, 0, yBottom, fullWidth, yBottom);
                }

                // Draw labels
                if (_showClusterLabels)
                {
                    var labelColor = cluster.IsPositive ? _majorPosColor : _majorNegColor;
                    var labelText = cluster.IsPositive ? "CLUSTER +" : "CLUSTER -";

                    if (_clusterAsSupportResistance)
                    {
                        labelText = cluster.IsPositive ? "RESISTANCE" : "SUPPORT";
                    }

                    context.DrawString(labelText, font, labelColor, 5, yCenter - 6);

                    if (_showClusterStrength)
                    {
                        var strengthText = $"Str: {cluster.Strength:0}%";
                        context.DrawString(strengthText, fontSmall, Color.Gray, 5, yCenter + 6);
                    }
                }
            }
        }
        #endregion

        #region GEX Statistics Methods (Section 19)
        private void CalculateGexStatistics(GexClassicData data)
        {
            lock (_sync)
            {
                if (data?.Strikes == null || data.Strikes.Count == 0)
                {
                    _gexStatistics = null;
                    return;
                }

                var gexValues = data.Strikes
                    .Select(s => _dataSource == GexDataSource.Volume ? s.GexByVolume : s.GexByOI)
                    .ToList();

                var positiveValues = gexValues.Where(v => v > 0).ToList();
                var negativeValues = gexValues.Where(v => v < 0).ToList();

                // Calculate basic statistics
                decimal mean = gexValues.Average();
                decimal min = gexValues.Min();
                decimal max = gexValues.Max();

                // Calculate standard deviation
                double variance = gexValues.Select(v => Math.Pow((double)(v - mean), 2)).Average();
                decimal stdDev = (decimal)Math.Sqrt(variance);

                // Calculate percentiles
                var sortedValues = gexValues.OrderBy(v => v).ToList();
                decimal p25 = GetPercentile(sortedValues, 25);
                decimal p50 = GetPercentile(sortedValues, 50);
                decimal p75 = GetPercentile(sortedValues, 75);

                // Calculate positive/negative sums and ratio
                decimal positiveSum = positiveValues.Sum();
                decimal negativeSum = Math.Abs(negativeValues.Sum());
                decimal ratio = negativeSum != 0 ? positiveSum / negativeSum : (positiveSum > 0 ? 100 : 0);

                // Calculate GEX Score (-100 to +100)
                decimal totalAbs = positiveSum + negativeSum;
                decimal gexScore = totalAbs != 0 ? ((positiveSum - negativeSum) / totalAbs) * 100 : 0;

                _gexStatistics = new GexStatistics
                {
                    Mean = mean,
                    StdDev = stdDev,
                    Min = min,
                    Max = max,
                    P25 = p25,
                    P50 = p50,
                    P75 = p75,
                    PositiveSum = positiveSum,
                    NegativeSum = negativeSum,
                    PositiveCount = positiveValues.Count,
                    NegativeCount = negativeValues.Count,
                    PosNegRatio = ratio,
                    GexScore = gexScore
                };
            }
        }

        private static decimal GetPercentile(List<decimal> sortedValues, int percentile)
        {
            if (sortedValues.Count == 0) return 0;
            int index = (int)Math.Ceiling(percentile / 100.0 * sortedValues.Count) - 1;
            index = Math.Clamp(index, 0, sortedValues.Count - 1);
            return sortedValues[index];
        }

        private void DrawStatisticsPanel(RenderContext context)
        {
            GexStatistics? stats;
            lock (_sync)
            {
                stats = _gexStatistics;
            }

            if (stats == null) return;

            var font = new RenderFont("Arial", _statsPanelFontSize);
            var fontBold = new RenderFont("Arial", _statsPanelFontSize + 1);
            var fontSmall = new RenderFont("Arial", _statsPanelFontSize - 1);
            var lineHeight = _statsPanelFontSize + 5;

            // Calculate panel dimensions
            int panelWidth = 180;
            int rowCount = 5; // Base rows
            if (_showStatsPercentiles) rowCount += 3;
            if (_showStatsRatio) rowCount += 2;
            if (_showGexScore) rowCount += 2;
            if (_showScoreGauge) rowCount += 1;
            int panelHeight = lineHeight * rowCount + 25;

            // Calculate position
            var (x, y) = CalcPanelXY(_statsPanelPosition, panelWidth, panelHeight, _statsPanelX, _statsPanelY);

            // Draw background
            var backRect = new Rectangle(x, y, panelWidth, panelHeight);
            context.FillRectangle(_statsPanelBackColor, backRect);
            context.DrawRectangle(new RenderPen(_statsPanelBorderColor, 1), backRect);

            int ty = y + 5;
            int labelX = x + 8;
            int valueX = x + 90;

            // Header
            context.DrawString("📊 GEX Statistics", fontBold, _statsPanelHeaderColor, labelX, ty);
            ty += lineHeight + 4;

            // Mean
            context.DrawString("Mean", font, Color.Gray, labelX, ty);
            var meanColor = stats.Mean >= 0 ? _infoPanelAccentPositive : _infoPanelAccentNegative;
            context.DrawString(FormatCompact(stats.Mean), font, meanColor, valueX, ty);
            ty += lineHeight;

            // Std Dev
            context.DrawString("Std Dev", font, Color.Gray, labelX, ty);
            context.DrawString(FormatCompact(stats.StdDev), font, Color.LightGray, valueX, ty);
            ty += lineHeight;

            // Min / Max
            context.DrawString("Min", font, Color.Gray, labelX, ty);
            context.DrawString(FormatCompact(stats.Min), font, _infoPanelAccentNegative, valueX, ty);
            ty += lineHeight;

            context.DrawString("Max", font, Color.Gray, labelX, ty);
            context.DrawString(FormatCompact(stats.Max), font, _infoPanelAccentPositive, valueX, ty);
            ty += lineHeight;

            // Percentiles
            if (_showStatsPercentiles)
            {
                ty += 3;
                context.DrawString("P25", font, Color.Gray, labelX, ty);
                context.DrawString(FormatCompact(stats.P25), font, Color.LightGray, valueX, ty);
                ty += lineHeight;

                context.DrawString("P50 (Med)", font, Color.Gray, labelX, ty);
                context.DrawString(FormatCompact(stats.P50), font, Color.Cyan, valueX, ty);
                ty += lineHeight;

                context.DrawString("P75", font, Color.Gray, labelX, ty);
                context.DrawString(FormatCompact(stats.P75), font, Color.LightGray, valueX, ty);
                ty += lineHeight;
            }

            // Ratio
            if (_showStatsRatio)
            {
                ty += 3;
                context.DrawString("Pos Count", font, _infoPanelAccentPositive, labelX, ty);
                context.DrawString($"{stats.PositiveCount}", font, Color.White, valueX, ty);
                ty += lineHeight;

                context.DrawString("Neg Count", font, _infoPanelAccentNegative, labelX, ty);
                context.DrawString($"{stats.NegativeCount}", font, Color.White, valueX, ty);
                ty += lineHeight;

                context.DrawString("Pos/Neg", font, Color.Gray, labelX, ty);
                context.DrawString($"{stats.PosNegRatio:0.00}", font, Color.Yellow, valueX, ty);
                ty += lineHeight;
            }

            // GEX Score
            if (_showGexScore)
            {
                ty += 5;
                var scoreColor = stats.GexScore >= 0 ? _infoPanelAccentPositive : _infoPanelAccentNegative;
                context.DrawString("GEX Score", fontBold, Color.White, labelX, ty);
                context.DrawString($"{stats.GexScore:+0.0;-0.0}", fontBold, scoreColor, valueX, ty);
                ty += lineHeight;

                // Draw gauge
                if (_showScoreGauge)
                {
                    ty += 2;
                    DrawScoreGauge(context, labelX, ty, _scoreGaugeWidth, 10, stats.GexScore);
                    ty += 14;
                }
            }
        }

        private void DrawScoreGauge(RenderContext context, int x, int y, int width, int height, decimal score)
        {
            // Background
            context.FillRectangle(Color.FromArgb(100, 50, 50, 50), new Rectangle(x, y, width, height));

            // Center line (zero point)
            int centerX = x + width / 2;
            context.DrawLine(new RenderPen(Color.FromArgb(150, 255, 255, 255), 1), centerX, y, centerX, y + height);

            // Score bar
            decimal normalizedScore = Math.Clamp(score, -100, 100);
            int barWidth = (int)(Math.Abs(normalizedScore) / 100m * (width / 2));
            var barColor = score >= 0 ? _infoPanelAccentPositive : _infoPanelAccentNegative;

            if (score >= 0)
            {
                context.FillRectangle(barColor, new Rectangle(centerX, y + 1, barWidth, height - 2));
            }
            else
            {
                context.FillRectangle(barColor, new Rectangle(centerX - barWidth, y + 1, barWidth, height - 2));
            }

            // Border
            context.DrawRectangle(new RenderPen(Color.FromArgb(100, 150, 150, 150), 1), new Rectangle(x, y, width, height));
        }
        #endregion

        #region Price Target Methods (Section 20)
        private void CalculatePriceTargets(GexClassicData data)
        {
            lock (_sync)
            {
                _priceTargets.Clear();

                if (data == null || data.Spot <= 0) return;

                decimal spot = data.Spot;

                // Zero Gamma target
                if (data.ZeroGamma > 0)
                {
                    decimal distance = data.ZeroGamma - spot;
                    decimal distPct = (distance / spot) * 100;
                    decimal attraction = CalculateAttraction(Math.Abs(distance), spot, 50); // Base attraction

                    _priceTargets.Add(new PriceTarget
                    {
                        Price = data.ZeroGamma,
                        Name = "Zero Gamma",
                        Distance = distance,
                        DistancePercent = distPct,
                        Attraction = attraction,
                        Color = _zeroGammaColor
                    });
                }

                // Major Positive target
                var majorPos = _dataSource == GexDataSource.Volume ? data.MajorPosVol : data.MajorPosOI;
                if (majorPos > 0)
                {
                    decimal distance = majorPos - spot;
                    decimal distPct = (distance / spot) * 100;
                    decimal attraction = CalculateAttraction(Math.Abs(distance), spot, 80);

                    _priceTargets.Add(new PriceTarget
                    {
                        Price = majorPos,
                        Name = "Major Pos",
                        Distance = distance,
                        DistancePercent = distPct,
                        Attraction = attraction,
                        Color = _majorPosColor
                    });
                }

                // Major Negative target
                var majorNeg = _dataSource == GexDataSource.Volume ? data.MajorNegVol : data.MajorNegOI;
                if (majorNeg > 0)
                {
                    decimal distance = majorNeg - spot;
                    decimal distPct = (distance / spot) * 100;
                    decimal attraction = CalculateAttraction(Math.Abs(distance), spot, 80);

                    _priceTargets.Add(new PriceTarget
                    {
                        Price = majorNeg,
                        Name = "Major Neg",
                        Distance = distance,
                        DistancePercent = distPct,
                        Attraction = attraction,
                        Color = _majorNegColor
                    });
                }

                // Sort by absolute distance
                _priceTargets = _priceTargets.OrderBy(t => Math.Abs(t.Distance)).ToList();
            }
        }

        private static decimal CalculateAttraction(decimal distance, decimal spot, decimal baseStrength)
        {
            // Attraction decreases with distance (inverse relationship)
            decimal distPercent = (distance / spot) * 100;
            decimal attraction = baseStrength * (1 - Math.Min(1, distPercent / 10m));
            return Math.Clamp(attraction, 0, 100);
        }

        private void DrawPriceTargetsPanel(RenderContext context, GexClassicData data, decimal factor)
        {
            List<PriceTarget> targets;
            lock (_sync)
            {
                targets = _priceTargets.ToList();
            }

            if (!_showTargetPanel || targets.Count == 0) return;

            var font = new RenderFont("Arial", 9);
            var fontBold = new RenderFont("Arial", 10);
            var fontSmall = new RenderFont("Arial", 8);
            var lineHeight = 14;

            // Panel dimensions
            int panelWidth = 200;
            int rowCount = targets.Count + 2;
            if (_showAttractionBars) rowCount += targets.Count;
            int panelHeight = lineHeight * rowCount + 20;

            // Calculate position
            var (x, y) = CalcPanelXY(_targetPanelPosition, panelWidth, panelHeight, _targetPanelX, _targetPanelY);

            // Draw background
            var backRect = new Rectangle(x, y, panelWidth, panelHeight);
            context.FillRectangle(Color.FromArgb(220, 25, 25, 35), backRect);
            context.DrawRectangle(new RenderPen(Color.FromArgb(150, 80, 80, 100), 1), backRect);

            int ty = y + 5;
            int labelX = x + 8;

            // Header
            context.DrawString("🎯 Price Targets", fontBold, Color.FromArgb(255, 255, 180, 100), labelX, ty);
            ty += lineHeight + 4;

            // Columns
            int nameCol = labelX;
            int priceCol = x + 70;
            int distCol = x + 130;

            foreach (var target in targets)
            {
                var arrow = target.Distance > 0 ? "▲" : "▼";
                var distColor = target.Distance > 0 ? _targetArrowUpColor : _targetArrowDownColor;

                // Target name
                context.DrawString(target.Name, font, target.Color, nameCol, ty);

                // Price
                context.DrawString($"{target.Price:0.00}", font, Color.White, priceCol, ty);

                // Distance with arrow
                if (_showTargetArrows)
                {
                    context.DrawString($"{arrow}{target.DistancePercent:+0.0;-0.0}%", font, distColor, distCol, ty);
                }
                ty += lineHeight;

                // Attraction bar
                if (_showAttractionBars && _showAttractionIndicator)
                {
                    int barWidth = (int)(target.Attraction / 100m * 120);
                    var barColor = Color.FromArgb(150, target.Color);
                    context.FillRectangle(Color.FromArgb(40, 100, 100, 100), new Rectangle(nameCol, ty, 120, 6));
                    context.FillRectangle(barColor, new Rectangle(nameCol, ty, barWidth, 6));
                    context.DrawString($"{target.Attraction:0}%", fontSmall, Color.Gray, nameCol + 125, ty - 2);
                    ty += 10;
                }
            }
        }
        #endregion

        #region Session Indicator Methods (Section 21)
        private void DrawSessionIndicator(RenderContext context, GexClassicData data)
        {
            var font = new RenderFont("Arial", 9);
            var fontBold = new RenderFont("Arial", 10);
            var fontSmall = new RenderFont("Arial", 8);

            var now = DateTime.Now;
            var currentTime = now.TimeOfDay;
            bool isMarketOpen = currentTime >= _marketOpenTime && currentTime < _marketCloseTime;

            // Check if weekend
            bool isWeekend = now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday;
            if (isWeekend) isMarketOpen = false;

            // Panel dimensions
            int panelWidth = 160;
            int panelHeight = 70;
            if (_showSessionProgress) panelHeight += 15;
            if (_showTimeToExpiry && data.MinDte == 0) panelHeight += 15;

            // Position (top center)
            int x = (ChartInfo.Region.Width - panelWidth) / 2;
            int y = 10;

            // Draw background
            var backRect = new Rectangle(x, y, panelWidth, panelHeight);
            context.FillRectangle(Color.FromArgb(200, 20, 20, 30), backRect);

            var borderColor = isMarketOpen ? _sessionActiveColor : _sessionClosedColor;
            context.DrawRectangle(new RenderPen(borderColor, 2), backRect);

            int ty = y + 5;
            int labelX = x + 8;

            // Status indicator
            var statusColor = isMarketOpen ? _sessionActiveColor : _sessionClosedColor;
            var statusIcon = isMarketOpen ? "🟢" : "🔴";
            var statusText = isMarketOpen ? "MARKET OPEN" : (isWeekend ? "WEEKEND" : "MARKET CLOSED");
            context.DrawString($"{statusIcon} {statusText}", fontBold, statusColor, labelX, ty);
            ty += 18;

            // Current time
            context.DrawString($"Time: {now:HH:mm:ss}", font, Color.LightGray, labelX, ty);
            ty += 14;

            // Session progress
            if (_showSessionProgress && isMarketOpen)
            {
                var sessionDuration = _marketCloseTime - _marketOpenTime;
                var elapsed = currentTime - _marketOpenTime;
                double progress = elapsed.TotalSeconds / sessionDuration.TotalSeconds;
                progress = Math.Clamp(progress, 0, 1);

                int progressWidth = panelWidth - 20;
                int progressX = labelX;

                // Background
                context.FillRectangle(Color.FromArgb(60, 100, 100, 100), new Rectangle(progressX, ty, progressWidth, 8));

                // Progress bar
                int filledWidth = (int)(progressWidth * progress);
                context.FillRectangle(_sessionActiveColor, new Rectangle(progressX, ty, filledWidth, 8));

                // Percentage
                context.DrawString($"{progress * 100:0}%", fontSmall, Color.Gray, progressX + progressWidth + 5, ty - 2);
                ty += 14;
            }

            // 0DTE highlight
            if (_highlight0DTE && data.MinDte == 0)
            {
                var badgeRect = new Rectangle(labelX, ty, 50, 14);
                context.FillRectangle(Color.FromArgb(100, _zeroDteBadgeColor), badgeRect);
                context.DrawString("0DTE", fontBold, _zeroDteBadgeColor, labelX + 5, ty);

                if (_showTimeToExpiry && isMarketOpen)
                {
                    var timeToClose = _marketCloseTime - currentTime;
                    if (timeToClose.TotalSeconds > 0)
                    {
                        var expiryText = $"{(int)timeToClose.TotalHours}h {timeToClose.Minutes}m left";
                        context.DrawString(expiryText, fontSmall, Color.Orange, labelX + 55, ty + 2);
                    }
                }
                ty += 16;
            }
        }
        #endregion

        #region API Diagnostics Methods (Section 23)
        private void DrawApiDiagnosticsPanel(RenderContext context)
        {
            ApiDiagnostics diag;
            lock (_sync)
            {
                diag = _apiDiagnostics;
            }

            var font = new RenderFont("Arial", _diagPanelFontSize);
            var fontBold = new RenderFont("Arial", _diagPanelFontSize + 1);
            var fontSmall = new RenderFont("Arial", _diagPanelFontSize - 1);
            var lineHeight = _diagPanelFontSize + 5;

            // Calculate panel height based on content
            int rowCount = 2; // Header + status
            if (_showConnectionStatus) rowCount += 1;
            if (_showLatency) rowCount += 2;
            if (_showErrorCount) rowCount += 1;
            if (_showActivityLog) rowCount += Math.Min(diag.ActivityLog.Count, _activityLogMaxItems) + 1;
            int panelHeight = lineHeight * rowCount + 20;
            int panelWidth = 200;

            // Calculate position
            var (x, y) = CalcPanelXY(_diagPanelPosition, panelWidth, panelHeight, _diagPanelX, _diagPanelY);

            // Draw background
            var backRect = new Rectangle(x, y, panelWidth, panelHeight);
            context.FillRectangle(_diagPanelBackColor, backRect);
            context.DrawRectangle(new RenderPen(Color.FromArgb(150, 80, 80, 100), 1), backRect);

            int ty = y + 5;
            int labelX = x + 8;
            int valueX = x + 100;

            // Header
            var headerColor = diag.IsConnected ? _connectedColor : _disconnectedColor;
            context.DrawString("🔌 API Diagnostics", fontBold, headerColor, labelX, ty);
            ty += lineHeight + 4;

            // Connection status
            if (_showConnectionStatus)
            {
                var statusIcon = diag.IsConnected ? "●" : "○";
                var statusText = diag.IsConnected ? "Connected" : "Disconnected";
                var statusColor = diag.IsConnected ? _connectedColor : _disconnectedColor;
                context.DrawString("Status", font, Color.Gray, labelX, ty);
                context.DrawString($"{statusIcon} {statusText}", font, statusColor, valueX, ty);
                ty += lineHeight;
            }

            // Latency
            if (_showLatency)
            {
                context.DrawString("Last Latency", font, Color.Gray, labelX, ty);
                var latencyColor = diag.LastLatencyMs < 500 ? _connectedColor :
                                   diag.LastLatencyMs < 1500 ? Color.Yellow : _disconnectedColor;
                context.DrawString($"{diag.LastLatencyMs:0}ms", font, latencyColor, valueX, ty);
                ty += lineHeight;

                context.DrawString("Avg Latency", font, Color.Gray, labelX, ty);
                context.DrawString($"{diag.AverageLatencyMs:0}ms", font, Color.LightGray, valueX, ty);
                ty += lineHeight;
            }

            // Error count
            if (_showErrorCount)
            {
                context.DrawString("Success/Errors", font, Color.Gray, labelX, ty);
                var errColor = diag.ErrorCount > 0 ? _disconnectedColor : Color.LightGray;
                context.DrawString($"{diag.SuccessCount}/{diag.ErrorCount}", font, errColor, valueX, ty);
                ty += lineHeight;
            }

            // Activity log
            if (_showActivityLog && diag.ActivityLog.Count > 0)
            {
                ty += 3;
                context.DrawString("Activity Log", fontSmall, Color.DimGray, labelX, ty);
                ty += lineHeight - 2;

                var logEntries = diag.ActivityLog.Take(_activityLogMaxItems);
                foreach (var entry in logEntries)
                {
                    var timeText = entry.Time.ToString("HH:mm:ss");
                    var logColor = entry.Message.StartsWith("✓") ? _connectedColor :
                                   entry.Message.StartsWith("✗") ? _disconnectedColor : Color.Gray;
                    context.DrawString($"{timeText} {entry.Message}", fontSmall, logColor, labelX, ty);
                    ty += lineHeight - 2;
                }
            }
        }
        #endregion
    }
}
