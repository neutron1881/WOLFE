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
        private DateTime _lastPulseTime = DateTime.Now;
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

        #region Alerts
        private bool _enableAlerts = true;
        [Display(GroupName = "9. Alerts", Name = "Enable alerts", Order = 10)]
        public bool EnableAlerts
        {
            get => _enableAlerts;
            set { _enableAlerts = value; RequestRecalc(); }
        }

        public enum AlertThresholdType { Percentage, AbsoluteValue, TopN }
        private AlertThresholdType _alertThresholdType = AlertThresholdType.Percentage;
        [Display(GroupName = "9. Alerts", Name = "Threshold type", Order = 20)]
        public AlertThresholdType AlertThreshold
        {
            get => _alertThresholdType;
            set { _alertThresholdType = value; RequestRecalc(); }
        }

        private decimal _alertThresholdValue = 50m;
        [Display(GroupName = "9. Alerts", Name = "Threshold value (%/Value/TopN)", Order = 30)]
        [Range(0, 10000)]
        public decimal AlertThresholdValue
        {
            get => _alertThresholdValue;
            set { _alertThresholdValue = Math.Max(0, value); RequestRecalc(); }
        }

        public enum AlertTimePeriod { OneMin, FiveMin, TenMin, FifteenMin, ThirtyMin }
        private AlertTimePeriod _alertTimePeriod = AlertTimePeriod.FiveMin;
        [Display(GroupName = "9. Alerts", Name = "Time period", Order = 40)]
        public AlertTimePeriod AlertPeriod
        {
            get => _alertTimePeriod;
            set { _alertTimePeriod = value; RequestRecalc(); }
        }

        private bool _alertOnIncrease = true;
        [Display(GroupName = "9. Alerts", Name = "Alert on GEX increase", Order = 50)]
        public bool AlertOnIncrease
        {
            get => _alertOnIncrease;
            set { _alertOnIncrease = value; RequestRecalc(); }
        }

        private bool _alertOnDecrease = true;
        [Display(GroupName = "9. Alerts", Name = "Alert on GEX decrease", Order = 60)]
        public bool AlertOnDecrease
        {
            get => _alertOnDecrease;
            set { _alertOnDecrease = value; RequestRecalc(); }
        }

        private Color _alertIncreaseColor = Color.FromArgb(255, 0, 255, 150);
        [Display(GroupName = "9. Alerts", Name = "Increase alert color", Order = 70)]
        public Color AlertIncreaseColor
        {
            get => _alertIncreaseColor;
            set { _alertIncreaseColor = value; RequestRecalc(); }
        }

        private Color _alertDecreaseColor = Color.FromArgb(255, 255, 100, 0);
        [Display(GroupName = "9. Alerts", Name = "Decrease alert color", Order = 80)]
        public Color AlertDecreaseColor
        {
            get => _alertDecreaseColor;
            set { _alertDecreaseColor = value; RequestRecalc(); }
        }

        private int _alertOutlineThickness = 3;
        [Display(GroupName = "9. Alerts", Name = "Alert outline thickness", Order = 90)]
        [Range(1, 10)]
        public int AlertOutlineThickness
        {
            get => _alertOutlineThickness;
            set { _alertOutlineThickness = Math.Clamp(value, 1, 10); RequestRecalc(); }
        }

        private bool _alertShowInPanel = true;
        [Display(GroupName = "9. Alerts", Name = "Show alerts in panel", Order = 100)]
        public bool AlertShowInPanel
        {
            get => _alertShowInPanel;
            set { _alertShowInPanel = value; RequestRecalc(); }
        }

        private bool _alertPulseEffect = true;
        [Display(GroupName = "9. Alerts", Name = "Pulse effect (animation)", Order = 110)]
        public bool AlertPulseEffect
        {
            get => _alertPulseEffect;
            set { _alertPulseEffect = value; RequestRecalc(); }
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
            return alerts.OrderByDescending(a => Math.Abs(a.Change)).ToList();
        }
        #endregion
    }
}
