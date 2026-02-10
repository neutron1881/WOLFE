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
    /// GexBot State Profile - Compact Gamma State Dashboard Indicator
    /// 
    /// This indicator displays real-time gamma market state from the GexBot API:
    /// • Gamma State: Positive/Negative/Neutral based on spot vs zero gamma
    /// • Key levels: Zero Gamma, Major Positive, Major Negative
    /// • GEX Score: Sentiment score from -100 to +100
    /// • Market regime detection (Mean-Reverting vs Trending)
    /// • State change alerts with sound notifications
    /// • Historical state tracking
    /// 
    /// Configuration is organized into 15 sections:
    /// 📁 CONFIGURATION: API, Price Conversion
    /// 📁 PANEL: Position, State Display, Metrics, Balance, Labels
    /// 📁 LEVELS: Major Levels, Alerts, Price Range
    /// 📁 ADVANCED: Themes, Trend, History, Export, Diagnostics
    /// 
    /// Uses the /majors endpoint for efficient data retrieval.
    /// Requires a valid GexBot API key.
    /// </summary>
    /// <remarks>
    /// Version: 1.0 Beta 1
    /// Compatible with: QQQ, SPY, SPX, NDX, NQ, ES, and major tech stocks
    /// </remarks>
    [DisplayName("GexBot State Profile")]
    public class GexBotStateProfile : Indicator
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
        public enum GammaState { Positive, Negative, Neutral }
        public enum MarketRegime { MeanReverting, Trending, Unknown }

        /// <summary>Greek type for State API endpoint</summary>
        public enum GreekType { Gamma, Delta }

        private class GexStrike
        {
            public decimal Strike { get; set; }
            public decimal CallIVol { get; set; }
            public decimal PutIVol { get; set; }
            public decimal GreekValue { get; set; }
            public decimal[] Priors { get; set; } = Array.Empty<decimal>();
        }

        private class StateData
        {
            public long Timestamp { get; set; }
            public string Ticker { get; set; } = string.Empty;
            public decimal Spot { get; set; }
            public int MinDte { get; set; }
            public int SecMinDte { get; set; }
            public decimal MajorPositive { get; set; }
            public decimal MajorNegative { get; set; }
            public decimal MajorLongGamma { get; set; }
            public decimal MajorShortGamma { get; set; }
            public List<GexStrike> MiniContracts { get; set; } = new();
        }

        private class StateSnapshot
        {
            public DateTime Time { get; set; }
            public GammaState State { get; set; }
            public decimal Spot { get; set; }
            public decimal ZeroGamma { get; set; }
            public decimal GexScore { get; set; }
            public MarketRegime Regime { get; set; }
        }

        private class StateAlert
        {
            public DateTime Time { get; set; }
            public GammaState OldState { get; set; }
            public GammaState NewState { get; set; }
            public decimal Spot { get; set; }
            public string Message { get; set; } = string.Empty;
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
        private StateData? _data;
        private string _error = string.Empty;
        private DateTime? _lastLoad;
        private decimal _lastChartPrice;

        // State tracking
        private GammaState _currentState = GammaState.Neutral;
        private GammaState _previousState = GammaState.Neutral;
        private decimal _gexScore = 0;
        private MarketRegime _currentRegime = MarketRegime.Unknown;
        private List<StateSnapshot> _stateHistory = new();
        private List<StateAlert> _stateAlerts = new();

        // API Diagnostics
        private ApiDiagnostics _apiDiagnostics = new();
        private System.Diagnostics.Stopwatch _apiStopwatch = new();
        private readonly Queue<double> _latencyHistory = new();
        private const int MaxLatencyHistory = 20;
        #endregion

        #region Section 01: API Configuration
        private string _apiKey = string.Empty;
        /// <summary>API Key for GexBot service authentication</summary>
        [Display(GroupName = "01. 🔑 API Configuration", Name = "API Key", Description = "Your GexBot API key for authentication", Order = 10)]
        public string ApiKey
        {
            get => _apiKey;
            set { _apiKey = value ?? string.Empty; ForceReload(); }
        }

        public enum TickerType { QQQ, SPY, SPX, NDX, NQ_NDX, ES_SPX, AAPL, NVDA, TSLA, AMD, AMZN, META, MSFT, GOOGL, IWM, RUT }
        private TickerType _ticker = TickerType.SPY;
        [Display(GroupName = "01. 🔑 API Configuration", Name = "Ticker", Description = "Symbol to fetch GEX data for", Order = 20)]
        public TickerType Ticker
        {
            get => _ticker;
            set { _ticker = value; ForceReload(); }
        }

        public enum DteType { zero, one }
        private DteType _dte = DteType.zero;
        [Display(GroupName = "01. 🔑 API Configuration", Name = "DTE", Description = "Days to expiration: zero=0DTE, one=1DTE", Order = 30)]
        public DteType Dte
        {
            get => _dte;
            set { _dte = value; ForceReload(); }
        }

        private GreekType _greek = GreekType.Gamma;
        [Display(GroupName = "01. 🔑 API Configuration", Name = "Greek", Description = "Greek type to display: Gamma or Delta", Order = 40)]
        public GreekType Greek
        {
            get => _greek;
            set { _greek = value; ForceReload(); }
        }

        private int _refreshSeconds = 60;
        [Display(GroupName = "01. 🔑 API Configuration", Name = "Refresh Interval (sec)", Description = "Data refresh interval in seconds", Order = 50)]
        [Range(10, 3600)]
        public int RefreshSeconds
        {
            get => _refreshSeconds;
            set { _refreshSeconds = Math.Max(10, value); ResetTimer(); }
        }
        #endregion

        #region Section 02: Price Conversion
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

        #region Section 03: Panel Position
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

        private PanelPosition _panelPosition = PanelPosition.TopRight;
        [Display(GroupName = "03. 📍 Panel Position", Name = "Position", Description = "Dashboard panel location", Order = 10)]
        public PanelPosition DashboardPosition
        {
            get => _panelPosition;
            set { _panelPosition = value; RequestRecalc(); }
        }

        private int _panelOffsetX = 10;
        [Display(GroupName = "03. 📍 Panel Position", Name = "X Offset (px)", Order = 20)]
        [Range(0, 2000)]
        public int PanelOffsetX
        {
            get => _panelOffsetX;
            set { _panelOffsetX = Math.Clamp(value, 0, 2000); RequestRecalc(); }
        }

        private int _panelOffsetY = 10;
        [Display(GroupName = "03. 📍 Panel Position", Name = "Y Offset (px)", Order = 30)]
        [Range(0, 2000)]
        public int PanelOffsetY
        {
            get => _panelOffsetY;
            set { _panelOffsetY = Math.Clamp(value, 0, 2000); RequestRecalc(); }
        }

        private int _panelWidth = 280;
        [Display(GroupName = "03. 📍 Panel Position", Name = "Panel Width (px)", Order = 40)]
        [Range(200, 500)]
        public int PanelWidth
        {
            get => _panelWidth;
            set { _panelWidth = Math.Clamp(value, 200, 500); RequestRecalc(); }
        }

        private bool _panelCollapsible = true;
        [Display(GroupName = "03. 📍 Panel Position", Name = "Collapsible Panel", Description = "Allow panel to be minimized", Order = 50)]
        public bool PanelCollapsible
        {
            get => _panelCollapsible;
            set { _panelCollapsible = value; RequestRecalc(); }
        }

        private bool _panelCollapsed = false;
        [Display(GroupName = "03. 📍 Panel Position", Name = "Start Collapsed", Order = 60)]
        public bool PanelCollapsed
        {
            get => _panelCollapsed;
            set { _panelCollapsed = value; RequestRecalc(); }
        }
        #endregion

        #region Section 04: Gamma State Display
        private bool _showGammaState = true;
        [Display(GroupName = "04. 🎯 Gamma State Display", Name = "Show State Header", Description = "Display main gamma state indicator", Order = 10)]
        public bool ShowGammaState
        {
            get => _showGammaState;
            set { _showGammaState = value; RequestRecalc(); }
        }

        public enum StateDisplayFormat { Text, IconText, FullBar }
        private StateDisplayFormat _stateFormat = StateDisplayFormat.FullBar;
        [Display(GroupName = "04. 🎯 Gamma State Display", Name = "Display Format", Order = 20)]
        public StateDisplayFormat StateFormat
        {
            get => _stateFormat;
            set { _stateFormat = value; RequestRecalc(); }
        }

        private Color _positiveStateColor = Color.FromArgb(255, 0, 180, 80);
        [Display(GroupName = "04. 🎯 Gamma State Display", Name = "Positive State Color", Order = 30)]
        public Color PositiveStateColor
        {
            get => _positiveStateColor;
            set { _positiveStateColor = value; RequestRecalc(); }
        }

        private Color _negativeStateColor = Color.FromArgb(255, 220, 60, 60);
        [Display(GroupName = "04. 🎯 Gamma State Display", Name = "Negative State Color", Order = 40)]
        public Color NegativeStateColor
        {
            get => _negativeStateColor;
            set { _negativeStateColor = value; RequestRecalc(); }
        }

        private Color _neutralStateColor = Color.FromArgb(255, 180, 180, 60);
        [Display(GroupName = "04. 🎯 Gamma State Display", Name = "Neutral State Color", Order = 50)]
        public Color NeutralStateColor
        {
            get => _neutralStateColor;
            set { _neutralStateColor = value; RequestRecalc(); }
        }

        private int _stateFontSize = 14;
        [Display(GroupName = "04. 🎯 Gamma State Display", Name = "State Font Size", Order = 60)]
        [Range(10, 24)]
        public int StateFontSize
        {
            get => _stateFontSize;
            set { _stateFontSize = Math.Clamp(value, 10, 24); RequestRecalc(); }
        }

        private decimal _neutralZonePoints = 5m;
        [Display(GroupName = "04. 🎯 Gamma State Display", Name = "Neutral Zone (points)", Description = "Distance from Zero Gamma to consider neutral", Order = 70)]
        [Range(0, 50)]
        public decimal NeutralZonePoints
        {
            get => _neutralZonePoints;
            set { _neutralZonePoints = Math.Clamp(value, 0, 50); RequestRecalc(); }
        }
        #endregion

        #region Section 05: Market Metrics
        private bool _showSpotPrice = true;
        [Display(GroupName = "05. 📊 Market Metrics", Name = "Show Spot Price", Order = 10)]
        public bool ShowSpotPrice
        {
            get => _showSpotPrice;
            set { _showSpotPrice = value; RequestRecalc(); }
        }

        private bool _showZeroGamma = true;
        [Display(GroupName = "05. 📊 Market Metrics", Name = "Show Zero Gamma", Order = 20)]
        public bool ShowZeroGamma
        {
            get => _showZeroGamma;
            set { _showZeroGamma = value; RequestRecalc(); }
        }

        private bool _showNetGex = true;
        [Display(GroupName = "05. 📊 Market Metrics", Name = "Show Net GEX", Order = 30)]
        public bool ShowNetGex
        {
            get => _showNetGex;
            set { _showNetGex = value; RequestRecalc(); }
        }

        private bool _showMajorLevelsInPanel = true;
        [Display(GroupName = "05. 📊 Market Metrics", Name = "Show Major Levels", Description = "Display Major +/- levels in panel", Order = 40)]
        public bool ShowMajorLevelsInPanel
        {
            get => _showMajorLevelsInPanel;
            set { _showMajorLevelsInPanel = value; RequestRecalc(); }
        }

        private bool _showDistanceToZG = true;
        [Display(GroupName = "05. 📊 Market Metrics", Name = "Show Distance to ZG", Description = "Display distance from spot to zero gamma", Order = 50)]
        public bool ShowDistanceToZG
        {
            get => _showDistanceToZG;
            set { _showDistanceToZG = value; RequestRecalc(); }
        }

        private bool _showTimestamp = true;
        [Display(GroupName = "05. 📊 Market Metrics", Name = "Show Last Update", Order = 60)]
        public bool ShowTimestamp
        {
            get => _showTimestamp;
            set { _showTimestamp = value; RequestRecalc(); }
        }

        private int _metricsFontSize = 10;
        [Display(GroupName = "05. 📊 Market Metrics", Name = "Metrics Font Size", Order = 70)]
        [Range(8, 16)]
        public int MetricsFontSize
        {
            get => _metricsFontSize;
            set { _metricsFontSize = Math.Clamp(value, 8, 16); RequestRecalc(); }
        }
        #endregion

        #region Section 06: GEX Balance & Score
        private bool _showGexScore = true;
        [Display(GroupName = "06. ⚖️ GEX Balance", Name = "Show GEX Score", Description = "Display sentiment score -100 to +100", Order = 10)]
        public bool ShowGexScore
        {
            get => _showGexScore;
            set { _showGexScore = value; RequestRecalc(); }
        }

        private bool _showScoreGauge = true;
        [Display(GroupName = "06. ⚖️ GEX Balance", Name = "Show Score Gauge", Description = "Visual gauge for GEX score", Order = 20)]
        public bool ShowScoreGauge
        {
            get => _showScoreGauge;
            set { _showScoreGauge = value; RequestRecalc(); }
        }

        private int _gaugeHeight = 12;
        [Display(GroupName = "06. ⚖️ GEX Balance", Name = "Gauge Height (px)", Order = 30)]
        [Range(6, 24)]
        public int GaugeHeight
        {
            get => _gaugeHeight;
            set { _gaugeHeight = Math.Clamp(value, 6, 24); RequestRecalc(); }
        }

        private Color _gaugePositiveColor = Color.FromArgb(255, 0, 200, 100);
        [Display(GroupName = "06. ⚖️ GEX Balance", Name = "Gauge Positive Color", Order = 40)]
        public Color GaugePositiveColor
        {
            get => _gaugePositiveColor;
            set { _gaugePositiveColor = value; RequestRecalc(); }
        }

        private Color _gaugeNegativeColor = Color.FromArgb(255, 255, 80, 80);
        [Display(GroupName = "06. ⚖️ GEX Balance", Name = "Gauge Negative Color", Order = 50)]
        public Color GaugeNegativeColor
        {
            get => _gaugeNegativeColor;
            set { _gaugeNegativeColor = value; RequestRecalc(); }
        }

        private Color _gaugeBackgroundColor = Color.FromArgb(100, 60, 60, 70);
        [Display(GroupName = "06. ⚖️ GEX Balance", Name = "Gauge Background", Order = 60)]
        public Color GaugeBackgroundColor
        {
            get => _gaugeBackgroundColor;
            set { _gaugeBackgroundColor = value; RequestRecalc(); }
        }

        private bool _showMarketRegime = true;
        [Display(GroupName = "06. ⚖️ GEX Balance", Name = "Show Market Regime", Description = "Display Mean-Reverting/Trending status", Order = 70)]
        public bool ShowMarketRegime
        {
            get => _showMarketRegime;
            set { _showMarketRegime = value; RequestRecalc(); }
        }
        #endregion

        #region Section 07: Panel Labels & Colors
        private Color _panelBackgroundColor = Color.FromArgb(230, 25, 25, 35);
        [Display(GroupName = "07. 🏷️ Panel Appearance", Name = "Background Color", Order = 10)]
        public Color PanelBackgroundColor
        {
            get => _panelBackgroundColor;
            set { _panelBackgroundColor = value; RequestRecalc(); }
        }

        private Color _panelBorderColor = Color.FromArgb(200, 80, 80, 100);
        [Display(GroupName = "07. 🏷️ Panel Appearance", Name = "Border Color", Order = 20)]
        public Color PanelBorderColor
        {
            get => _panelBorderColor;
            set { _panelBorderColor = value; RequestRecalc(); }
        }

        private int _panelBorderThickness = 1;
        [Display(GroupName = "07. 🏷️ Panel Appearance", Name = "Border Thickness", Order = 30)]
        [Range(0, 5)]
        public int PanelBorderThickness
        {
            get => _panelBorderThickness;
            set { _panelBorderThickness = Math.Clamp(value, 0, 5); RequestRecalc(); }
        }

        private Color _labelColor = Color.FromArgb(255, 180, 180, 190);
        [Display(GroupName = "07. 🏷️ Panel Appearance", Name = "Label Color", Order = 40)]
        public Color LabelColor
        {
            get => _labelColor;
            set { _labelColor = value; RequestRecalc(); }
        }

        private Color _valueColor = Color.White;
        [Display(GroupName = "07. 🏷️ Panel Appearance", Name = "Value Color", Order = 50)]
        public Color ValueColor
        {
            get => _valueColor;
            set { _valueColor = value; RequestRecalc(); }
        }

        private Color _headerColor = Color.FromArgb(255, 100, 180, 255);
        [Display(GroupName = "07. 🏷️ Panel Appearance", Name = "Header Color", Order = 60)]
        public Color HeaderColor
        {
            get => _headerColor;
            set { _headerColor = value; RequestRecalc(); }
        }

        private int _panelPadding = 8;
        [Display(GroupName = "07. 🏷️ Panel Appearance", Name = "Padding (px)", Order = 70)]
        [Range(4, 20)]
        public int PanelPadding
        {
            get => _panelPadding;
            set { _panelPadding = Math.Clamp(value, 4, 20); RequestRecalc(); }
        }
        #endregion

        #region Section 08: Major Levels on Chart
        private bool _showZeroGammaLine = true;
        [Display(GroupName = "08. ➖ Chart Levels", Name = "Show Zero Gamma Line", Order = 10)]
        public bool ShowZeroGammaLine
        {
            get => _showZeroGammaLine;
            set { _showZeroGammaLine = value; RequestRecalc(); }
        }

        private Color _zeroGammaLineColor = Color.Yellow;
        [Display(GroupName = "08. ➖ Chart Levels", Name = "Zero Gamma Color", Order = 20)]
        public Color ZeroGammaLineColor
        {
            get => _zeroGammaLineColor;
            set { _zeroGammaLineColor = value; RequestRecalc(); }
        }

        private int _zeroGammaThickness = 2;
        [Display(GroupName = "08. ➖ Chart Levels", Name = "Zero Gamma Thickness", Order = 30)]
        [Range(1, 8)]
        public int ZeroGammaThickness
        {
            get => _zeroGammaThickness;
            set { _zeroGammaThickness = Math.Clamp(value, 1, 8); RequestRecalc(); }
        }

        private DashStyle _zeroGammaDash = DashStyle.Dash;
        [Display(GroupName = "08. ➖ Chart Levels", Name = "Zero Gamma Style", Order = 40)]
        public DashStyle ZeroGammaDash
        {
            get => _zeroGammaDash;
            set { _zeroGammaDash = value; RequestRecalc(); }
        }

        private bool _showMajorPosLine = true;
        [Display(GroupName = "08. ➖ Chart Levels", Name = "Show Major Positive Line", Order = 50)]
        public bool ShowMajorPosLine
        {
            get => _showMajorPosLine;
            set { _showMajorPosLine = value; RequestRecalc(); }
        }

        private Color _majorPosLineColor = Color.LimeGreen;
        [Display(GroupName = "08. ➖ Chart Levels", Name = "Major Positive Color", Order = 60)]
        public Color MajorPosLineColor
        {
            get => _majorPosLineColor;
            set { _majorPosLineColor = value; RequestRecalc(); }
        }

        private bool _showMajorNegLine = true;
        [Display(GroupName = "08. ➖ Chart Levels", Name = "Show Major Negative Line", Order = 70)]
        public bool ShowMajorNegLine
        {
            get => _showMajorNegLine;
            set { _showMajorNegLine = value; RequestRecalc(); }
        }

        private Color _majorNegLineColor = Color.OrangeRed;
        [Display(GroupName = "08. ➖ Chart Levels", Name = "Major Negative Color", Order = 80)]
        public Color MajorNegLineColor
        {
            get => _majorNegLineColor;
            set { _majorNegLineColor = value; RequestRecalc(); }
        }

        private int _majorLinesThickness = 2;
        [Display(GroupName = "08. ➖ Chart Levels", Name = "Major Lines Thickness", Order = 90)]
        [Range(1, 8)]
        public int MajorLinesThickness
        {
            get => _majorLinesThickness;
            set { _majorLinesThickness = Math.Clamp(value, 1, 8); RequestRecalc(); }
        }

        private bool _showLevelLabels = true;
        [Display(GroupName = "08. ➖ Chart Levels", Name = "Show Level Labels", Order = 100)]
        public bool ShowLevelLabels
        {
            get => _showLevelLabels;
            set { _showLevelLabels = value; RequestRecalc(); }
        }

        private int _levelLabelFontSize = 9;
        [Display(GroupName = "08. ➖ Chart Levels", Name = "Label Font Size", Order = 110)]
        [Range(7, 14)]
        public int LevelLabelFontSize
        {
            get => _levelLabelFontSize;
            set { _levelLabelFontSize = Math.Clamp(value, 7, 14); RequestRecalc(); }
        }
        #endregion

        #region Section 09: State Alerts
        private bool _enableStateAlerts = true;
        [Display(GroupName = "09. 🔔 State Alerts", Name = "Enable State Alerts", Description = "Alert when gamma state changes", Order = 10)]
        public bool EnableStateAlerts
        {
            get => _enableStateAlerts;
            set { _enableStateAlerts = value; RequestRecalc(); }
        }

        private bool _alertOnPositive = true;
        [Display(GroupName = "09. 🔔 State Alerts", Name = "Alert on Positive State", Order = 20)]
        public bool AlertOnPositive
        {
            get => _alertOnPositive;
            set { _alertOnPositive = value; RequestRecalc(); }
        }

        private bool _alertOnNegative = true;
        [Display(GroupName = "09. 🔔 State Alerts", Name = "Alert on Negative State", Order = 30)]
        public bool AlertOnNegative
        {
            get => _alertOnNegative;
            set { _alertOnNegative = value; RequestRecalc(); }
        }

        private bool _alertOnNeutral = false;
        [Display(GroupName = "09. 🔔 State Alerts", Name = "Alert on Neutral State", Order = 40)]
        public bool AlertOnNeutral
        {
            get => _alertOnNeutral;
            set { _alertOnNeutral = value; RequestRecalc(); }
        }

        private bool _enableSoundAlerts = true;
        [Display(GroupName = "09. 🔔 State Alerts", Name = "Enable Sound Alerts", Order = 50)]
        public bool EnableSoundAlerts
        {
            get => _enableSoundAlerts;
            set { _enableSoundAlerts = value; RequestRecalc(); }
        }

        private string _positiveAlertSound = "C:\\Windows\\Media\\chimes.wav";
        [Display(GroupName = "09. 🔔 State Alerts", Name = "Positive Alert Sound", Order = 60)]
        public string PositiveAlertSound
        {
            get => _positiveAlertSound;
            set { _positiveAlertSound = value ?? string.Empty; }
        }

        private string _negativeAlertSound = "C:\\Windows\\Media\\chord.wav";
        [Display(GroupName = "09. 🔔 State Alerts", Name = "Negative Alert Sound", Order = 70)]
        public string NegativeAlertSound
        {
            get => _negativeAlertSound;
            set { _negativeAlertSound = value ?? string.Empty; }
        }

        private bool _showAlertPopup = true;
        [Display(GroupName = "09. 🔔 State Alerts", Name = "Show Alert Popup", Order = 80)]
        public bool ShowAlertPopup
        {
            get => _showAlertPopup;
            set { _showAlertPopup = value; RequestRecalc(); }
        }

        private int _alertPopupDurationSec = 5;
        [Display(GroupName = "09. 🔔 State Alerts", Name = "Popup Duration (sec)", Order = 90)]
        [Range(1, 30)]
        public int AlertPopupDurationSec
        {
            get => _alertPopupDurationSec;
            set { _alertPopupDurationSec = Math.Clamp(value, 1, 30); RequestRecalc(); }
        }
        #endregion

        #region Section 10: Price Range Zone
        private bool _showGammaZone = true;
        [Display(GroupName = "10. 📏 Gamma Zone", Name = "Show Gamma Zone", Description = "Highlight positive/negative gamma area", Order = 10)]
        public bool ShowGammaZone
        {
            get => _showGammaZone;
            set { _showGammaZone = value; RequestRecalc(); }
        }

        private Color _positiveZoneColor = Color.FromArgb(25, 0, 200, 100);
        [Display(GroupName = "10. 📏 Gamma Zone", Name = "Positive Zone Color", Order = 20)]
        public Color PositiveZoneColor
        {
            get => _positiveZoneColor;
            set { _positiveZoneColor = value; RequestRecalc(); }
        }

        private Color _negativeZoneColor = Color.FromArgb(25, 255, 80, 80);
        [Display(GroupName = "10. 📏 Gamma Zone", Name = "Negative Zone Color", Order = 30)]
        public Color NegativeZoneColor
        {
            get => _negativeZoneColor;
            set { _negativeZoneColor = value; RequestRecalc(); }
        }

        private bool _showNeutralZone = true;
        [Display(GroupName = "10. 📏 Gamma Zone", Name = "Show Neutral Zone", Order = 40)]
        public bool ShowNeutralZone
        {
            get => _showNeutralZone;
            set { _showNeutralZone = value; RequestRecalc(); }
        }

        private Color _neutralZoneColor = Color.FromArgb(20, 200, 200, 100);
        [Display(GroupName = "10. 📏 Gamma Zone", Name = "Neutral Zone Color", Order = 50)]
        public Color NeutralZoneColor
        {
            get => _neutralZoneColor;
            set { _neutralZoneColor = value; RequestRecalc(); }
        }
        #endregion

        #region Section 11: Themes
        public enum ThemeType { Dark, Light, GreenRed, BlueOrange, Custom }
        private ThemeType _theme = ThemeType.Dark;
        [Display(GroupName = "11. 🎨 Themes", Name = "Color Theme", Order = 10)]
        public ThemeType Theme
        {
            get => _theme;
            set { _theme = value; ApplyTheme(); RequestRecalc(); }
        }

        private void ApplyTheme()
        {
            switch (_theme)
            {
                case ThemeType.Dark:
                    _panelBackgroundColor = Color.FromArgb(230, 25, 25, 35);
                    _panelBorderColor = Color.FromArgb(200, 80, 80, 100);
                    _labelColor = Color.FromArgb(255, 180, 180, 190);
                    _valueColor = Color.White;
                    _positiveStateColor = Color.FromArgb(255, 0, 180, 80);
                    _negativeStateColor = Color.FromArgb(255, 220, 60, 60);
                    break;
                case ThemeType.Light:
                    _panelBackgroundColor = Color.FromArgb(240, 245, 245, 250);
                    _panelBorderColor = Color.FromArgb(200, 150, 150, 160);
                    _labelColor = Color.FromArgb(255, 80, 80, 90);
                    _valueColor = Color.FromArgb(255, 30, 30, 40);
                    _positiveStateColor = Color.FromArgb(255, 0, 150, 60);
                    _negativeStateColor = Color.FromArgb(255, 200, 40, 40);
                    break;
                case ThemeType.GreenRed:
                    _panelBackgroundColor = Color.FromArgb(230, 20, 30, 25);
                    _panelBorderColor = Color.FromArgb(200, 60, 100, 80);
                    _positiveStateColor = Color.FromArgb(255, 0, 255, 100);
                    _negativeStateColor = Color.FromArgb(255, 255, 50, 50);
                    break;
                case ThemeType.BlueOrange:
                    _panelBackgroundColor = Color.FromArgb(230, 20, 25, 40);
                    _panelBorderColor = Color.FromArgb(200, 60, 80, 120);
                    _positiveStateColor = Color.FromArgb(255, 50, 150, 255);
                    _negativeStateColor = Color.FromArgb(255, 255, 140, 50);
                    break;
            }
        }
        #endregion

        #region Section 12: Trend Indicator
        private bool _showTrendIndicator = true;
        [Display(GroupName = "12. 📈 Trend Indicator", Name = "Show Trend Arrow", Description = "Display trend direction based on state history", Order = 10)]
        public bool ShowTrendIndicator
        {
            get => _showTrendIndicator;
            set { _showTrendIndicator = value; RequestRecalc(); }
        }

        private int _trendLookbackCount = 5;
        [Display(GroupName = "12. 📈 Trend Indicator", Name = "Lookback Count", Description = "Number of state snapshots for trend", Order = 20)]
        [Range(3, 20)]
        public int TrendLookbackCount
        {
            get => _trendLookbackCount;
            set { _trendLookbackCount = Math.Clamp(value, 3, 20); RequestRecalc(); }
        }

        private Color _trendUpColor = Color.FromArgb(255, 0, 220, 120);
        [Display(GroupName = "12. 📈 Trend Indicator", Name = "Uptrend Color", Order = 30)]
        public Color TrendUpColor
        {
            get => _trendUpColor;
            set { _trendUpColor = value; RequestRecalc(); }
        }

        private Color _trendDownColor = Color.FromArgb(255, 255, 80, 80);
        [Display(GroupName = "12. 📈 Trend Indicator", Name = "Downtrend Color", Order = 40)]
        public Color TrendDownColor
        {
            get => _trendDownColor;
            set { _trendDownColor = value; RequestRecalc(); }
        }

        private Color _trendNeutralColor = Color.Gray;
        [Display(GroupName = "12. 📈 Trend Indicator", Name = "Neutral Trend Color", Order = 50)]
        public Color TrendNeutralColor
        {
            get => _trendNeutralColor;
            set { _trendNeutralColor = value; RequestRecalc(); }
        }
        #endregion

        #region Section 13: State History
        private bool _enableStateHistory = true;
        [Display(GroupName = "13. 📜 State History", Name = "Enable State History", Order = 10)]
        public bool EnableStateHistory
        {
            get => _enableStateHistory;
            set { _enableStateHistory = value; RequestRecalc(); }
        }

        private int _maxHistoryItems = 50;
        [Display(GroupName = "13. 📜 State History", Name = "Max History Items", Order = 20)]
        [Range(10, 200)]
        public int MaxHistoryItems
        {
            get => _maxHistoryItems;
            set { _maxHistoryItems = Math.Clamp(value, 10, 200); TrimHistory(); RequestRecalc(); }
        }

        private bool _showHistoryPanel = false;
        [Display(GroupName = "13. 📜 State History", Name = "Show History Panel", Order = 30)]
        public bool ShowHistoryPanel
        {
            get => _showHistoryPanel;
            set { _showHistoryPanel = value; RequestRecalc(); }
        }

        private int _historyDisplayCount = 8;
        [Display(GroupName = "13. 📜 State History", Name = "Display Count", Order = 40)]
        [Range(3, 20)]
        public int HistoryDisplayCount
        {
            get => _historyDisplayCount;
            set { _historyDisplayCount = Math.Clamp(value, 3, 20); RequestRecalc(); }
        }

        private PanelPosition _historyPanelPosition = PanelPosition.BottomLeft;
        [Display(GroupName = "13. 📜 State History", Name = "History Panel Position", Order = 50)]
        public PanelPosition HistoryPanelPos
        {
            get => _historyPanelPosition;
            set { _historyPanelPosition = value; RequestRecalc(); }
        }

        private int _historyPanelOffsetX = 10;
        [Display(GroupName = "13. 📜 State History", Name = "History X Offset (px)", Order = 55)]
        [Range(0, 2000)]
        public int HistoryPanelOffsetX
        {
            get => _historyPanelOffsetX;
            set { _historyPanelOffsetX = Math.Clamp(value, 0, 2000); RequestRecalc(); }
        }

        private int _historyPanelOffsetY = 10;
        [Display(GroupName = "13. 📜 State History", Name = "History Y Offset (px)", Order = 56)]
        [Range(0, 2000)]
        public int HistoryPanelOffsetY
        {
            get => _historyPanelOffsetY;
            set { _historyPanelOffsetY = Math.Clamp(value, 0, 2000); RequestRecalc(); }
        }

        private void TrimHistory()
        {
            lock (_sync)
            {
                while (_stateHistory.Count > _maxHistoryItems)
                    _stateHistory.RemoveAt(_stateHistory.Count - 1);
                while (_stateAlerts.Count > _maxHistoryItems)
                    _stateAlerts.RemoveAt(_stateAlerts.Count - 1);
            }
        }
        #endregion

        #region Section 14: Export
        private bool _enableExport = false;
        [Display(GroupName = "14. 💾 Data Export", Name = "Enable Export", Order = 10)]
        public bool EnableExport
        {
            get => _enableExport;
            set { _enableExport = value; RequestRecalc(); }
        }

        public enum ExportFormat { CSV, TXT }
        private ExportFormat _exportFormat = ExportFormat.CSV;
        [Display(GroupName = "14. 💾 Data Export", Name = "Export Format", Order = 20)]
        public ExportFormat ExportFileFormat
        {
            get => _exportFormat;
            set { _exportFormat = value; }
        }

        private string _exportPath = "";
        [Display(GroupName = "14. 💾 Data Export", Name = "Export Path", Description = "Leave empty for default (Documents)", Order = 30)]
        public string ExportPath
        {
            get => _exportPath;
            set { _exportPath = value ?? string.Empty; }
        }

        private int _exportIntervalMin = 5;
        [Display(GroupName = "14. 💾 Data Export", Name = "Export Interval (min)", Order = 40)]
        [Range(1, 60)]
        public int ExportIntervalMin
        {
            get => _exportIntervalMin;
            set { _exportIntervalMin = Math.Clamp(value, 1, 60); }
        }

        private DateTime? _lastExportTime;
        private string _lastExportFile = string.Empty;
        #endregion

        #region Section 15: API Diagnostics
        private bool _showDiagnostics = false;
        [Display(GroupName = "15. 🔌 API Diagnostics", Name = "Show Diagnostics Panel", Order = 10)]
        public bool ShowDiagnostics
        {
            get => _showDiagnostics;
            set { _showDiagnostics = value; RequestRecalc(); }
        }

        private PanelPosition _diagPosition = PanelPosition.BottomRight;
        [Display(GroupName = "15. 🔌 API Diagnostics", Name = "Panel Position", Order = 15)]
        public PanelPosition DiagPosition
        {
            get => _diagPosition;
            set { _diagPosition = value; RequestRecalc(); }
        }

        private bool _showLatency = true;
        [Display(GroupName = "15. 🔌 API Diagnostics", Name = "Show Latency", Order = 20)]
        public bool ShowLatency
        {
            get => _showLatency;
            set { _showLatency = value; RequestRecalc(); }
        }

        private bool _showRequestCount = true;
        [Display(GroupName = "15. 🔌 API Diagnostics", Name = "Show Request Count", Order = 30)]
        public bool ShowRequestCount
        {
            get => _showRequestCount;
            set { _showRequestCount = value; RequestRecalc(); }
        }

        private int _diagnosticsPanelX = 10;
        [Display(GroupName = "15. 🔌 API Diagnostics", Name = "Panel X Offset", Order = 40)]
        [Range(0, 2000)]
        public int DiagnosticsPanelX
        {
            get => _diagnosticsPanelX;
            set { _diagnosticsPanelX = Math.Clamp(value, 0, 2000); RequestRecalc(); }
        }

        private int _diagnosticsPanelY = 10;
        [Display(GroupName = "15. 🔌 API Diagnostics", Name = "Panel Y Offset", Order = 50)]
        [Range(0, 2000)]
        public int DiagnosticsPanelY
        {
            get => _diagnosticsPanelY;
            set { _diagnosticsPanelY = Math.Clamp(value, 0, 2000); RequestRecalc(); }
        }
        #endregion

        #region Section 16: GEX Profile Bars
        private bool _showGexProfile = true;
        [Display(GroupName = "16. 📊 GEX Profile", Name = "Show GEX Profile", Description = "Display horizontal GEX bars at each strike", Order = 10)]
        public bool ShowGexProfile
        {
            get => _showGexProfile;
            set { _showGexProfile = value; RequestRecalc(); }
        }

        public enum ProfilePosition { Left, Right, Center }
        private ProfilePosition _profilePosition = ProfilePosition.Left;
        [Display(GroupName = "16. 📊 GEX Profile", Name = "Profile Position", Description = "Position of profile bars on chart", Order = 20)]
        public ProfilePosition GexProfilePosition
        {
            get => _profilePosition;
            set { _profilePosition = value; RequestRecalc(); }
        }

        private int _profileOffsetPx = 50;
        [Display(GroupName = "16. 📊 GEX Profile", Name = "Profile Offset (px)", Description = "Offset from edge of chart", Order = 30)]
        [Range(0, 500)]
        public int ProfileOffsetPx
        {
            get => _profileOffsetPx;
            set { _profileOffsetPx = Math.Clamp(value, 0, 500); RequestRecalc(); }
        }

        private int _maxBarWidthPx = 150;
        [Display(GroupName = "16. 📊 GEX Profile", Name = "Max Bar Width (px)", Order = 40)]
        [Range(50, 400)]
        public int MaxBarWidthPx
        {
            get => _maxBarWidthPx;
            set { _maxBarWidthPx = Math.Clamp(value, 50, 400); RequestRecalc(); }
        }

        private int _barThicknessPx = 6;
        [Display(GroupName = "16. 📊 GEX Profile", Name = "Bar Thickness (px)", Order = 50)]
        [Range(2, 20)]
        public int BarThicknessPx
        {
            get => _barThicknessPx;
            set { _barThicknessPx = Math.Clamp(value, 2, 20); RequestRecalc(); }
        }

        private Color _profilePosColor = Color.FromArgb(200, 255, 0, 255);
        [Display(GroupName = "16. 📊 GEX Profile", Name = "Positive Bar Color", Order = 60)]
        public Color ProfilePosColor
        {
            get => _profilePosColor;
            set { _profilePosColor = value; RequestRecalc(); }
        }

        private Color _profileNegColor = Color.FromArgb(200, 0, 200, 255);
        [Display(GroupName = "16. 📊 GEX Profile", Name = "Negative Bar Color", Order = 70)]
        public Color ProfileNegColor
        {
            get => _profileNegColor;
            set { _profileNegColor = value; RequestRecalc(); }
        }

        private int _barFillOpacity = 180;
        [Display(GroupName = "16. 📊 GEX Profile", Name = "Bar Fill Opacity", Order = 80)]
        [Range(50, 255)]
        public int BarFillOpacity
        {
            get => _barFillOpacity;
            set { _barFillOpacity = Math.Clamp(value, 50, 255); RequestRecalc(); }
        }

        private bool _showBarOutline = true;
        [Display(GroupName = "16. 📊 GEX Profile", Name = "Show Bar Outline", Order = 90)]
        public bool ShowBarOutline
        {
            get => _showBarOutline;
            set { _showBarOutline = value; RequestRecalc(); }
        }

        private bool _showCenterLine = true;
        [Display(GroupName = "16. 📊 GEX Profile", Name = "Show Center Line", Order = 100)]
        public bool ShowCenterLine
        {
            get => _showCenterLine;
            set { _showCenterLine = value; RequestRecalc(); }
        }

        private Color _centerLineColor = Color.FromArgb(150, 255, 200, 50);
        [Display(GroupName = "16. 📊 GEX Profile", Name = "Center Line Color", Order = 110)]
        public Color CenterLineColor
        {
            get => _centerLineColor;
            set { _centerLineColor = value; RequestRecalc(); }
        }

        private bool _showBarValues = false;
        [Display(GroupName = "16. 📊 GEX Profile", Name = "Show Bar Values", Order = 120)]
        public bool ShowBarValues
        {
            get => _showBarValues;
            set { _showBarValues = value; RequestRecalc(); }
        }

        private int _strikeFilterPercent = 5;
        [Display(GroupName = "16. 📊 GEX Profile", Name = "Strike Range (%)", Description = "Percentage range around spot to display strikes", Order = 130)]
        [Range(1, 20)]
        public int StrikeFilterPercent
        {
            get => _strikeFilterPercent;
            set { _strikeFilterPercent = Math.Clamp(value, 1, 20); RequestRecalc(); }
        }
        #endregion

        #region Core Methods
        public GexBotStateProfile()
        {
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
            DataSeries[0].IsHidden = true;

            _httpClient.DefaultRequestHeaders.Add("User-Agent", "GexBotStateProfile/1.0");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

            _refreshTimer.Elapsed += (s, e) => Task.Run(() => LoadDataAsync());
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar == CurrentBar - 1)
            {
                _lastChartPrice = GetCandle(bar).Close;
            }
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();
            ResetTimer();
            Task.Run(() => LoadDataAsync());
        }

        protected override void OnDispose()
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _httpClient.Dispose();
            base.OnDispose();
        }

        private void ResetTimer()
        {
            _refreshTimer.Stop();
            _refreshTimer.Interval = _refreshSeconds * 1000;
            _refreshTimer.Start();
        }

        private void ForceReload()
        {
            Task.Run(() => LoadDataAsync());
        }
        #endregion

        #region API Loading
        private async Task LoadDataAsync()
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                lock (_sync)
                {
                    _error = "API Key not configured";
                    _apiDiagnostics.LastErrorMessage = _error;
                    _apiDiagnostics.LastErrorTime = DateTime.Now;
                }
                RedrawChart();
                return;
            }

            var tickerStr = _ticker.ToString().Replace("_", "/");
            var greekStr = _greek.ToString().ToLower();
            var dteStr = _dte.ToString();
            // State endpoint format: /state/{greek}_{dte}
            var url = $"https://api.gexbot.com/{tickerStr}/state/{greekStr}_{dteStr}?key={_apiKey}";

            try
            {
                _apiStopwatch.Restart();
                LogActivity($"Requesting: {_ticker}/state/{greekStr}_{dteStr}");

                var response = await _httpClient.GetAsync(url);

                _apiStopwatch.Stop();
                var latency = _apiStopwatch.Elapsed.TotalMilliseconds;
                UpdateLatencyStats(latency);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    lock (_sync)
                    {
                        _error = $"API Error {(int)response.StatusCode}: {response.ReasonPhrase}";
                        _apiDiagnostics.LastErrorMessage = _error;
                        _apiDiagnostics.LastErrorTime = DateTime.Now;
                        _apiDiagnostics.ErrorCount++;
                    }
                    LogActivity($"Error: {_error}");
                    RedrawChart();
                    return;
                }

                var json = await response.Content.ReadAsStringAsync();
                var parsed = ParseStateResponse(json);

                if (parsed == null)
                {
                    lock (_sync)
                    {
                        _error = "Failed to parse API response";
                        _apiDiagnostics.LastErrorMessage = _error;
                        _apiDiagnostics.LastErrorTime = DateTime.Now;
                        _apiDiagnostics.ErrorCount++;
                    }
                    LogActivity("Parse error");
                    RedrawChart();
                    return;
                }

                lock (_sync)
                {
                    _data = parsed;
                    _error = string.Empty;
                    _lastLoad = DateTime.Now;
                    _apiDiagnostics.LastSuccessTime = DateTime.Now;
                    _apiDiagnostics.SuccessCount++;
                    _apiDiagnostics.IsConnected = true;

                    // Calculate state
                    UpdateGammaState();
                }

                LogActivity($"Success: Spot={parsed.Spot:F2}, M+={parsed.MajorPositive:F2}, M-={parsed.MajorNegative:F2}");

                // Export if enabled
                if (_enableExport)
                {
                    CheckAndExport();
                }

                RedrawChart();
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _error = $"Connection error: {ex.Message}";
                    _apiDiagnostics.LastErrorMessage = _error;
                    _apiDiagnostics.LastErrorTime = DateTime.Now;
                    _apiDiagnostics.ErrorCount++;
                    _apiDiagnostics.IsConnected = false;
                }
                LogActivity($"Exception: {ex.Message}");
                RedrawChart();
            }
        }

        private StateData? ParseStateResponse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var data = new StateData
                {
                    Timestamp = root.GetProperty("timestamp").GetInt64(),
                    Ticker = root.GetProperty("ticker").GetString() ?? string.Empty,
                    Spot = root.TryGetProperty("spot", out var spot) ? (decimal)spot.GetDouble() : 0m,
                    MinDte = root.TryGetProperty("min_dte", out var minDte) ? minDte.GetInt32() : 0,
                    SecMinDte = root.TryGetProperty("sec_min_dte", out var secMinDte) ? secMinDte.GetInt32() : 0,
                    MajorPositive = root.TryGetProperty("major_positive", out var mp) ? (decimal)mp.GetDouble() : 0m,
                    MajorNegative = root.TryGetProperty("major_negative", out var mn) ? (decimal)mn.GetDouble() : 0m,
                    MajorLongGamma = root.TryGetProperty("major_long_gamma", out var mlg) ? (decimal)mlg.GetDouble() : 0m,
                    MajorShortGamma = root.TryGetProperty("major_short_gamma", out var msg) ? (decimal)msg.GetDouble() : 0m
                };

                // Parse mini_contracts array: [strike, call_ivol, put_ivol, greek_value, priors[], null, null]
                if (root.TryGetProperty("mini_contracts", out var contractsArr))
                {
                    foreach (var contractItem in contractsArr.EnumerateArray())
                    {
                        if (contractItem.ValueKind == JsonValueKind.Array)
                        {
                            var arr = contractItem.EnumerateArray().ToList();
                            if (arr.Count >= 4)
                            {
                                var strike = new GexStrike
                                {
                                    Strike = (decimal)arr[0].GetDouble(),
                                    CallIVol = arr[1].ValueKind == JsonValueKind.Number ? (decimal)arr[1].GetDouble() : 0m,
                                    PutIVol = arr[2].ValueKind == JsonValueKind.Number ? (decimal)arr[2].GetDouble() : 0m,
                                    GreekValue = arr[3].ValueKind == JsonValueKind.Number ? (decimal)arr[3].GetDouble() : 0m
                                };

                                // Parse priors array if present (index 4)
                                if (arr.Count >= 5 && arr[4].ValueKind == JsonValueKind.Array)
                                {
                                    var priorsList = arr[4].EnumerateArray()
                                        .Where(p => p.ValueKind == JsonValueKind.Number)
                                        .Select(p => (decimal)p.GetDouble())
                                        .ToArray();
                                    strike.Priors = priorsList;
                                }

                                data.MiniContracts.Add(strike);
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

        private void UpdateLatencyStats(double latency)
        {
            _apiDiagnostics.LastLatencyMs = latency;
            _latencyHistory.Enqueue(latency);
            while (_latencyHistory.Count > MaxLatencyHistory)
                _latencyHistory.Dequeue();
            _apiDiagnostics.AverageLatencyMs = _latencyHistory.Average();
        }

        private void LogActivity(string message)
        {
            lock (_sync)
            {
                _apiDiagnostics.ActivityLog.Insert(0, (DateTime.Now, message));
                while (_apiDiagnostics.ActivityLog.Count > 20)
                    _apiDiagnostics.ActivityLog.RemoveAt(_apiDiagnostics.ActivityLog.Count - 1);
            }
        }
        #endregion

        #region State Calculation
        private void UpdateGammaState()
        {
            if (_data == null) return;

            _previousState = _currentState;

            // Determine gamma state based on spot position relative to major levels
            // Positive gamma: spot above major negative (more dealer hedging support)
            // Negative gamma: spot below major negative (less support, trending)
            var majorPos = _data.MajorPositive;
            var majorNeg = _data.MajorNegative;

            // Calculate midpoint between major levels as reference
            var midpoint = (majorPos + majorNeg) / 2;
            var distance = _data.Spot - midpoint;

            if (Math.Abs(distance) <= _neutralZonePoints)
            {
                _currentState = GammaState.Neutral;
            }
            else if (_data.Spot > midpoint)
            {
                _currentState = GammaState.Positive;
            }
            else
            {
                _currentState = GammaState.Negative;
            }

            // Calculate GEX Score (-100 to +100) based on position between major levels
            if (majorPos > 0 && majorNeg > 0 && majorPos != majorNeg)
            {
                var range = majorPos - majorNeg;
                if (range != 0)
                {
                    var position = (_data.Spot - majorNeg) / range;
                    _gexScore = Math.Clamp((position * 200) - 100, -100, 100);
                }
            }

            // Determine market regime
            if (_currentState == GammaState.Positive)
            {
                _currentRegime = MarketRegime.MeanReverting;
            }
            else if (_currentState == GammaState.Negative)
            {
                _currentRegime = MarketRegime.Trending;
            }
            else
            {
                _currentRegime = MarketRegime.Unknown;
            }

            // Record history
            if (_enableStateHistory)
            {
                var snapshot = new StateSnapshot
                {
                    Time = DateTime.Now,
                    State = _currentState,
                    Spot = _data.Spot,
                    ZeroGamma = midpoint, // Use midpoint as reference
                    GexScore = _gexScore,
                    Regime = _currentRegime
                };
                _stateHistory.Insert(0, snapshot);
                TrimHistory();
            }

            // Check for state change alert
            if (_enableStateAlerts && _previousState != _currentState)
            {
                TriggerStateAlert();
            }
        }

        private void TriggerStateAlert()
        {
            bool shouldAlert = _currentState switch
            {
                GammaState.Positive => _alertOnPositive,
                GammaState.Negative => _alertOnNegative,
                GammaState.Neutral => _alertOnNeutral,
                _ => false
            };

            if (!shouldAlert) return;

            var alert = new StateAlert
            {
                Time = DateTime.Now,
                OldState = _previousState,
                NewState = _currentState,
                Spot = _data?.Spot ?? 0,
                Message = $"Gamma State: {_previousState} → {_currentState}"
            };

            _stateAlerts.Insert(0, alert);
            TrimHistory();

            // Play sound
            if (_enableSoundAlerts)
            {
                try
                {
                    var soundFile = _currentState == GammaState.Positive ? _positiveAlertSound : _negativeAlertSound;
                    if (File.Exists(soundFile))
                    {
                        using var player = new SoundPlayer(soundFile);
                        player.Play();
                    }
                    else
                    {
                        SystemSounds.Exclamation.Play();
                    }
                }
                catch { }
            }
        }
        #endregion

        #region Rendering
        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo?.PriceChartContainer == null) return;

            StateData? snapshot;
            string error;
            lock (_sync)
            {
                snapshot = _data;
                error = _error;
            }

            // Draw error if any
            if (!string.IsNullOrEmpty(error))
            {
                context.DrawString(error, new RenderFont("Arial", 10), Color.Red, 10, 10);
            }

            // Calculate conversion factor
            decimal factor = _conversionFactor;
            if (_enableConversion && _autoCalculateFactor && snapshot != null && snapshot.Spot > 0 && _lastChartPrice > 0)
            {
                factor = _lastChartPrice / snapshot.Spot;
            }

            // Draw gamma zones on chart
            if (snapshot != null && _showGammaZone)
            {
                DrawGammaZones(context, snapshot, factor);
            }

            // Draw GEX profile bars
            if (snapshot != null && _showGexProfile && snapshot.MiniContracts.Count > 0)
            {
                DrawGexProfile(context, snapshot, factor);
            }

            // Draw level lines on chart
            if (snapshot != null)
            {
                DrawChartLevels(context, snapshot, factor);
            }

            // Draw main dashboard panel
            DrawDashboardPanel(context, snapshot, factor);

            // Draw history panel if enabled
            if (_showHistoryPanel)
            {
                DrawHistoryPanel(context);
            }

            // Draw diagnostics panel if enabled
            if (_showDiagnostics)
            {
                DrawDiagnosticsPanel(context);
            }

            // Draw alert popup if recent
            DrawAlertPopup(context);
        }

        private void DrawGammaZones(RenderContext context, StateData data, decimal factor)
        {
            // Use midpoint between major levels as reference
            var midpoint = (data.MajorPositive + data.MajorNegative) / 2;
            var zg = _enableConversion ? RoundToStep(midpoint * factor, _priceStep) : midpoint;
            var zgY = ChartInfo.PriceChartContainer.GetYByPrice(zg, false);
            var width = ChartInfo.Region.Width;

            if (_showNeutralZone && _neutralZonePoints > 0)
            {
                var neutralTop = ChartInfo.PriceChartContainer.GetYByPrice(zg + _neutralZonePoints * factor, false);
                var neutralBottom = ChartInfo.PriceChartContainer.GetYByPrice(zg - _neutralZonePoints * factor, false);
                var neutralRect = new Rectangle(0, Math.Min(neutralTop, neutralBottom), width, Math.Abs(neutralBottom - neutralTop));
                context.FillRectangle(_neutralZoneColor, neutralRect);
            }

            // Positive zone (above midpoint)
            var positiveRect = new Rectangle(0, 0, width, zgY);
            context.FillRectangle(_positiveZoneColor, positiveRect);

            // Negative zone (below midpoint)
            var negativeRect = new Rectangle(0, zgY, width, ChartInfo.Region.Height - zgY);
            context.FillRectangle(_negativeZoneColor, negativeRect);
        }

        private void DrawGexProfile(RenderContext context, StateData data, decimal factor)
        {
            if (data.MiniContracts.Count == 0) return;

            var chartWidth = ChartInfo.Region.Width;
            var chartHeight = ChartInfo.Region.Height;

            // Filter strikes by range around spot
            var minStrike = data.Spot * (1 - _strikeFilterPercent / 100m);
            var maxStrike = data.Spot * (1 + _strikeFilterPercent / 100m);

            var filteredStrikes = data.MiniContracts
                .Where(s => s.Strike >= minStrike && s.Strike <= maxStrike)
                .Select(s => new
                {
                    OriginalStrike = s.Strike,
                    ChartStrike = _enableConversion ? RoundToStep(s.Strike * factor, _priceStep) : s.Strike,
                    GexValue = s.GreekValue
                })
                .ToList();

            if (filteredStrikes.Count == 0) return;

            // Calculate scale
            double maxGex = filteredStrikes.Max(s => Math.Abs((double)s.GexValue));
            if (maxGex <= 0) maxGex = 1;
            var scale = _maxBarWidthPx / maxGex;

            // Determine center line X position
            int xCenter;
            switch (_profilePosition)
            {
                case ProfilePosition.Left:
                    xCenter = _profileOffsetPx + _maxBarWidthPx;
                    break;
                case ProfilePosition.Right:
                    xCenter = chartWidth - _profileOffsetPx - _maxBarWidthPx;
                    break;
                default: // Center
                    xCenter = chartWidth / 2;
                    break;
            }

            // Draw center line
            if (_showCenterLine)
            {
                var centerPen = new RenderPen(_centerLineColor, 1);
                context.DrawLine(centerPen, xCenter, 0, xCenter, chartHeight);
            }

            var valueFont = new RenderFont("Arial", 8);

            // Draw bars
            foreach (var strike in filteredStrikes)
            {
                var y = ChartInfo.PriceChartContainer.GetYByPrice(strike.ChartStrike, false);
                var top = y - _barThicknessPx / 2;

                var gexVal = (double)strike.GexValue;
                var barWidth = (int)Math.Round(Math.Abs(gexVal) * scale);

                if (barWidth <= 0) continue;

                var isPositive = gexVal >= 0;
                var barColor = isPositive ? _profilePosColor : _profileNegColor;
                var fillColor = Color.FromArgb(_barFillOpacity, barColor);

                Rectangle barRect;
                if (isPositive)
                {
                    // Positive GEX: draw to the right of center
                    barRect = new Rectangle(xCenter, top, barWidth, _barThicknessPx);
                }
                else
                {
                    // Negative GEX: draw to the left of center
                    barRect = new Rectangle(xCenter - barWidth, top, barWidth, _barThicknessPx);
                }

                // Fill bar
                context.FillRectangle(fillColor, barRect);

                // Draw outline
                if (_showBarOutline)
                {
                    var outlinePen = new RenderPen(barColor, 1);
                    context.DrawRectangle(outlinePen, barRect);
                }

                // Draw value label
                if (_showBarValues && barWidth > 15)
                {
                    var valText = FormatCompact(strike.GexValue);
                    if (isPositive)
                    {
                        context.DrawString(valText, valueFont, barColor, xCenter + barWidth + 3, top);
                    }
                    else
                    {
                        context.DrawString(valText, valueFont, barColor, xCenter - barWidth - 30, top);
                    }
                }
            }
        }

        private void DrawChartLevels(RenderContext context, StateData data, decimal factor)
        {
            var width = ChartInfo.Region.Width;
            var labelFont = new RenderFont("Arial", _levelLabelFontSize);

            // Midpoint line (replaces Zero Gamma)
            if (_showZeroGammaLine)
            {
                var midpoint = (data.MajorPositive + data.MajorNegative) / 2;
                if (midpoint > 0)
                {
                    var zg = _enableConversion ? RoundToStep(midpoint * factor, _priceStep) : midpoint;
                    var y = ChartInfo.PriceChartContainer.GetYByPrice(zg, false);
                    var pen = new RenderPen(_zeroGammaLineColor, _zeroGammaThickness, _zeroGammaDash);
                    context.DrawLine(pen, 0, y, width, y);

                    if (_showLevelLabels)
                    {
                        var label = $"Mid {midpoint:F2}";
                        context.DrawString(label, labelFont, _zeroGammaLineColor, 5, y - 14);
                    }
                }
            }

            // Major Positive line
            if (_showMajorPosLine && data.MajorPositive > 0)
            {
                var mpConverted = _enableConversion ? RoundToStep(data.MajorPositive * factor, _priceStep) : data.MajorPositive;
                var y = ChartInfo.PriceChartContainer.GetYByPrice(mpConverted, false);
                var pen = new RenderPen(_majorPosLineColor, _majorLinesThickness, DashStyle.Solid);
                context.DrawLine(pen, 0, y, width, y);

                if (_showLevelLabels)
                {
                    var label = $"M+ {data.MajorPositive:F2}";
                    context.DrawString(label, labelFont, _majorPosLineColor, 5, y - 14);
                }
            }

            // Major Negative line
            if (_showMajorNegLine && data.MajorNegative > 0)
            {
                var mnConverted = _enableConversion ? RoundToStep(data.MajorNegative * factor, _priceStep) : data.MajorNegative;
                var y = ChartInfo.PriceChartContainer.GetYByPrice(mnConverted, false);
                var pen = new RenderPen(_majorNegLineColor, _majorLinesThickness, DashStyle.Solid);
                context.DrawLine(pen, 0, y, width, y);

                if (_showLevelLabels)
                {
                    var label = $"M- {data.MajorNegative:F2}";
                    context.DrawString(label, labelFont, _majorNegLineColor, 5, y - 14);
                }
            }
        }

        private void DrawDashboardPanel(RenderContext context, StateData? data, decimal factor)
        {
            var headerFont = new RenderFont("Arial", _stateFontSize, FontStyle.Bold);
            var metricsFont = new RenderFont("Arial", _metricsFontSize);
            var smallFont = new RenderFont("Arial", _metricsFontSize - 1);

            int panelHeight = CalculatePanelHeight();
            int x, y;
            CalculatePanelPosition(out x, out y, _panelWidth, panelHeight);

            // Draw panel background
            var panelRect = new Rectangle(x, y, _panelWidth, _panelCollapsed ? 30 : panelHeight);
            context.FillRectangle(_panelBackgroundColor, panelRect);
            if (_panelBorderThickness > 0)
            {
                context.DrawRectangle(new RenderPen(_panelBorderColor, _panelBorderThickness), panelRect);
            }

            int contentX = x + _panelPadding;
            int contentY = y + _panelPadding;
            int lineHeight = _metricsFontSize + 6;

            // Header with state
            if (_showGammaState)
            {
                var stateColor = _currentState switch
                {
                    GammaState.Positive => _positiveStateColor,
                    GammaState.Negative => _negativeStateColor,
                    _ => _neutralStateColor
                };

                var stateIcon = _currentState switch
                {
                    GammaState.Positive => "🟢",
                    GammaState.Negative => "🔴",
                    _ => "🟡"
                };

                var greekName = _greek == GreekType.Gamma ? "GAMMA" : "DELTA";
                var stateText = $"{greekName} {_currentState.ToString().ToUpper()}";

                if (_stateFormat == StateDisplayFormat.FullBar)
                {
                    var headerRect = new Rectangle(x, y, _panelWidth, 28);
                    context.FillRectangle(Color.FromArgb(200, stateColor), headerRect);
                    context.DrawString($"{stateIcon} {stateText}", headerFont, Color.White, contentX, contentY);
                }
                else if (_stateFormat == StateDisplayFormat.IconText)
                {
                    context.DrawString($"{stateIcon} {stateText}", headerFont, stateColor, contentX, contentY);
                }
                else
                {
                    context.DrawString(stateText, headerFont, stateColor, contentX, contentY);
                }
                contentY += 28;
            }

            if (_panelCollapsed) return;

            // Separator
            context.DrawLine(new RenderPen(_panelBorderColor, 1), x, contentY, x + _panelWidth, contentY);
            contentY += 6;

            if (data == null)
            {
                context.DrawString("Loading data...", metricsFont, _labelColor, contentX, contentY);
                return;
            }

            // Metrics section
            int labelWidth = 90;
            int valueX = contentX + labelWidth;

            if (_showSpotPrice)
            {
                context.DrawString("Spot:", metricsFont, _labelColor, contentX, contentY);
                context.DrawString($"{data.Spot:F2}", metricsFont, _valueColor, valueX, contentY);
                contentY += lineHeight;
            }

            if (_showZeroGamma)
            {
                // Show midpoint between major levels
                var midpoint = (data.MajorPositive + data.MajorNegative) / 2;
                context.DrawString("Midpoint:", metricsFont, _labelColor, contentX, contentY);
                context.DrawString($"{midpoint:F2}", metricsFont, _zeroGammaLineColor, valueX, contentY);
                contentY += lineHeight;
            }

            if (_showDistanceToZG)
            {
                var midpoint = (data.MajorPositive + data.MajorNegative) / 2;
                var dist = data.Spot - midpoint;
                var distColor = dist > 0 ? _positiveStateColor : (dist < 0 ? _negativeStateColor : _neutralStateColor);
                context.DrawString("Distance:", metricsFont, _labelColor, contentX, contentY);
                context.DrawString($"{dist:+0.00;-0.00} pts", metricsFont, distColor, valueX, contentY);
                contentY += lineHeight;
            }

            if (_showMajorLevelsInPanel)
            {
                context.DrawString("Major +:", metricsFont, _labelColor, contentX, contentY);
                context.DrawString($"{data.MajorPositive:F2}", metricsFont, _majorPosLineColor, valueX, contentY);
                contentY += lineHeight;

                context.DrawString("Major -:", metricsFont, _labelColor, contentX, contentY);
                context.DrawString($"{data.MajorNegative:F2}", metricsFont, _majorNegLineColor, valueX, contentY);
                contentY += lineHeight;
            }

            // Net Greek is sum of all mini_contracts greek values
            if (_showNetGex)
            {
                var netGreek = data.MiniContracts.Sum(c => c.GreekValue);
                var netColor = netGreek > 0 ? _positiveStateColor : (netGreek < 0 ? _negativeStateColor : _labelColor);
                var greekLabel = _greek == GreekType.Gamma ? "Net Gamma:" : "Net Delta:";
                context.DrawString(greekLabel, metricsFont, _labelColor, contentX, contentY);
                context.DrawString(FormatCompact(netGreek), metricsFont, netColor, valueX, contentY);
                contentY += lineHeight;
            }

            // GEX Score section
            if (_showGexScore)
            {
                contentY += 4;
                context.DrawLine(new RenderPen(_panelBorderColor, 1), x + 5, contentY, x + _panelWidth - 5, contentY);
                contentY += 8;

                var scoreColor = _gexScore > 20 ? _positiveStateColor : (_gexScore < -20 ? _negativeStateColor : _neutralStateColor);
                context.DrawString("Score:", metricsFont, _labelColor, contentX, contentY);
                context.DrawString($"{_gexScore:+0;-0}", metricsFont, scoreColor, valueX, contentY);
                contentY += lineHeight;

                if (_showScoreGauge)
                {
                    DrawScoreGauge(context, contentX, contentY, _panelWidth - _panelPadding * 2, _gaugeHeight);
                    contentY += _gaugeHeight + 8;
                }
            }

            // Market Regime
            if (_showMarketRegime)
            {
                var regimeText = _currentRegime switch
                {
                    MarketRegime.MeanReverting => "📊 Mean-Reverting",
                    MarketRegime.Trending => "📈 Trending",
                    _ => "❓ Unknown"
                };
                var regimeColor = _currentRegime switch
                {
                    MarketRegime.MeanReverting => _positiveStateColor,
                    MarketRegime.Trending => _negativeStateColor,
                    _ => _labelColor
                };
                context.DrawString(regimeText, metricsFont, regimeColor, contentX, contentY);
                contentY += lineHeight;
            }

            // Trend indicator
            if (_showTrendIndicator && _stateHistory.Count >= 2)
            {
                var trend = CalculateTrend();
                var trendArrow = trend > 0 ? "▲" : (trend < 0 ? "▼" : "◆");
                var trendText = trend > 0 ? "Bullish" : (trend < 0 ? "Bearish" : "Neutral");
                var trendColor = trend > 0 ? _trendUpColor : (trend < 0 ? _trendDownColor : _trendNeutralColor);
                context.DrawString($"Trend: {trendArrow} {trendText}", metricsFont, trendColor, contentX, contentY);
                contentY += lineHeight;
            }

            // Timestamp
            if (_showTimestamp && _lastLoad.HasValue)
            {
                contentY += 4;
                context.DrawString($"Updated: {_lastLoad.Value:HH:mm:ss}", smallFont, Color.Gray, contentX, contentY);
            }
        }

        private void DrawScoreGauge(RenderContext context, int x, int y, int width, int height)
        {
            // Background
            var bgRect = new Rectangle(x, y, width, height);
            context.FillRectangle(_gaugeBackgroundColor, bgRect);

            // Center marker
            int centerX = x + width / 2;
            context.DrawLine(new RenderPen(Color.White, 1), centerX, y, centerX, y + height);

            // Fill based on score
            var fillWidth = (int)((double)Math.Abs(_gexScore) / 100.0 * (width / 2));
            var fillColor = _gexScore > 0 ? _gaugePositiveColor : _gaugeNegativeColor;

            if (_gexScore > 0)
            {
                var fillRect = new Rectangle(centerX, y + 1, fillWidth, height - 2);
                context.FillRectangle(fillColor, fillRect);
            }
            else if (_gexScore < 0)
            {
                var fillRect = new Rectangle(centerX - fillWidth, y + 1, fillWidth, height - 2);
                context.FillRectangle(fillColor, fillRect);
            }

            // Border
            context.DrawRectangle(new RenderPen(_panelBorderColor, 1), bgRect);
        }

        private void DrawHistoryPanel(RenderContext context)
        {
            if (_stateHistory.Count == 0) return;

            var font = new RenderFont("Arial", 9);
            int panelWidth = 200;
            int lineHeight = 14;
            int panelHeight = Math.Min(_historyDisplayCount, _stateHistory.Count) * lineHeight + 30;

            var (x, y) = CalcPanelXY(_historyPanelPosition, panelWidth, panelHeight, _historyPanelOffsetX, _historyPanelOffsetY);

            // Background
            var rect = new Rectangle(x, y, panelWidth, panelHeight);
            context.FillRectangle(Color.FromArgb(220, 30, 30, 40), rect);
            context.DrawRectangle(new RenderPen(Color.FromArgb(150, 80, 80, 100), 1), rect);

            // Header
            context.DrawString("📜 State History", font, _headerColor, x + 5, y + 3);
            int contentY = y + 20;

            // History items
            int count = Math.Min(_historyDisplayCount, _stateHistory.Count);
            for (int i = 0; i < count; i++)
            {
                var item = _stateHistory[i];
                var icon = item.State switch
                {
                    GammaState.Positive => "🟢",
                    GammaState.Negative => "🔴",
                    _ => "🟡"
                };
                var text = $"{icon} {item.Time:HH:mm:ss} - {item.State} ({item.GexScore:+0;-0})";
                context.DrawString(text, font, Color.LightGray, x + 5, contentY);
                contentY += lineHeight;
            }
        }

        private void DrawDiagnosticsPanel(RenderContext context)
        {
            var font = new RenderFont("Arial", 9);
            int panelWidth = 180;
            int panelHeight = 80;

            var (x, y) = CalcPanelXY(_diagPosition, panelWidth, panelHeight, _diagnosticsPanelX, _diagnosticsPanelY);

            // Background
            var rect = new Rectangle(x, y, panelWidth, panelHeight);
            context.FillRectangle(Color.FromArgb(200, 25, 25, 35), rect);
            context.DrawRectangle(new RenderPen(Color.FromArgb(150, 60, 60, 80), 1), rect);

            int contentX = x + 5;
            int contentY = y + 5;
            int lineHeight = 14;

            // Status
            var statusColor = _apiDiagnostics.IsConnected ? Color.LimeGreen : Color.OrangeRed;
            var statusText = _apiDiagnostics.IsConnected ? "● Connected" : "○ Disconnected";
            context.DrawString($"🔌 {statusText}", font, statusColor, contentX, contentY);
            contentY += lineHeight;

            if (_showLatency)
            {
                context.DrawString($"Latency: {_apiDiagnostics.LastLatencyMs:F0}ms (avg: {_apiDiagnostics.AverageLatencyMs:F0}ms)", font, Color.Gray, contentX, contentY);
                contentY += lineHeight;
            }

            if (_showRequestCount)
            {
                context.DrawString($"Requests: {_apiDiagnostics.SuccessCount} OK / {_apiDiagnostics.ErrorCount} ERR", font, Color.Gray, contentX, contentY);
                contentY += lineHeight;
            }

            if (_apiDiagnostics.LastSuccessTime.HasValue)
            {
                context.DrawString($"Last: {_apiDiagnostics.LastSuccessTime.Value:HH:mm:ss}", font, Color.DimGray, contentX, contentY);
            }
        }

        private void DrawAlertPopup(RenderContext context)
        {
            if (!_showAlertPopup || _stateAlerts.Count == 0) return;

            var lastAlert = _stateAlerts[0];
            var elapsed = (DateTime.Now - lastAlert.Time).TotalSeconds;
            if (elapsed > _alertPopupDurationSec) return;

            var font = new RenderFont("Arial", 12, FontStyle.Bold);
            var smallFont = new RenderFont("Arial", 10);

            int width = 250;
            int height = 60;
            int x = (ChartInfo.Region.Width - width) / 2;
            int y = 80;

            // Fade effect
            int alpha = (int)(255 * (1 - elapsed / _alertPopupDurationSec));
            var stateColor = lastAlert.NewState switch
            {
                GammaState.Positive => Color.FromArgb(alpha, _positiveStateColor),
                GammaState.Negative => Color.FromArgb(alpha, _negativeStateColor),
                _ => Color.FromArgb(alpha, _neutralStateColor)
            };

            // Background
            var rect = new Rectangle(x, y, width, height);
            context.FillRectangle(Color.FromArgb(alpha * 220 / 255, 20, 20, 30), rect);
            context.DrawRectangle(new RenderPen(stateColor, 2), rect);

            // Content
            var icon = lastAlert.NewState switch
            {
                GammaState.Positive => "🟢",
                GammaState.Negative => "🔴",
                _ => "🟡"
            };
            context.DrawString($"{icon} STATE CHANGE", font, Color.FromArgb(alpha, Color.White), x + 10, y + 10);
            context.DrawString(lastAlert.Message, smallFont, Color.FromArgb(alpha, Color.LightGray), x + 10, y + 35);
        }
        #endregion

        #region Helper Methods
        private int CalculatePanelHeight()
        {
            int height = 40; // Header + padding
            if (_showSpotPrice) height += 20;
            if (_showZeroGamma) height += 20;
            if (_showDistanceToZG) height += 20;
            if (_showMajorLevelsInPanel) height += 40;
            if (_showNetGex) height += 20;
            if (_showGexScore) height += 30 + (_showScoreGauge ? _gaugeHeight + 8 : 0);
            if (_showMarketRegime) height += 20;
            if (_showTrendIndicator) height += 20;
            if (_showTimestamp) height += 20;
            return height;
        }

        private void CalculatePanelPosition(out int x, out int y, int width, int height)
        {
            (x, y) = CalcPanelXY(_panelPosition, width, height, _panelOffsetX, _panelOffsetY);
        }

        private int CalculateTrend()
        {
            if (_stateHistory.Count < 2) return 0;

            int lookback = Math.Min(_trendLookbackCount, _stateHistory.Count);
            var recent = _stateHistory.Take(lookback).ToList();

            var avgRecent = recent.Take(lookback / 2).Average(s => (int)s.GexScore);
            var avgOlder = recent.Skip(lookback / 2).Average(s => (int)s.GexScore);

            var diff = avgRecent - avgOlder;
            if (diff > 10) return 1;
            if (diff < -10) return -1;
            return 0;
        }

        private decimal RoundToStep(decimal value, decimal step)
        {
            if (step <= 0) return value;
            return Math.Round(value / step) * step;
        }

        private string FormatCompact(decimal value)
        {
            var abs = Math.Abs(value);
            if (abs >= 1_000_000) return $"{value / 1_000_000:0.##}M";
            if (abs >= 1_000) return $"{value / 1_000:0.##}K";
            return value.ToString("0.##");
        }

        private void CheckAndExport()
        {
            if (!_enableExport) return;
            if (_lastExportTime.HasValue && (DateTime.Now - _lastExportTime.Value).TotalMinutes < _exportIntervalMin) return;

            try
            {
                var folder = string.IsNullOrEmpty(_exportPath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    : _exportPath;

                var ext = _exportFormat == ExportFormat.CSV ? "csv" : "txt";
                var fileName = $"GexState_{_ticker}_{DateTime.Now:yyyyMMdd}.{ext}";
                var filePath = Path.Combine(folder, fileName);

                var separator = _exportFormat == ExportFormat.CSV ? "," : "\t";
                var header = string.Join(separator, "Timestamp", "Ticker", "Greek", "DTE", "State", "Spot", "MajorPos", "MajorNeg", "Score", "Regime");

                bool fileExists = File.Exists(filePath);
                using var writer = new StreamWriter(filePath, append: true);

                if (!fileExists)
                {
                    writer.WriteLine(header);
                }

                if (_data != null)
                {
                    var line = string.Join(separator,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        _data.Ticker,
                        _greek.ToString(),
                        _dte.ToString(),
                        _currentState.ToString(),
                        _data.Spot.ToString("F2", CultureInfo.InvariantCulture),
                        _data.MajorPositive.ToString("F2", CultureInfo.InvariantCulture),
                        _data.MajorNegative.ToString("F2", CultureInfo.InvariantCulture),
                        _gexScore.ToString("F0", CultureInfo.InvariantCulture),
                        _currentRegime.ToString()
                    );
                    writer.WriteLine(line);
                }

                _lastExportTime = DateTime.Now;
                _lastExportFile = filePath;
            }
            catch { }
        }
        #endregion
    }
}
