namespace ATAS.Indicators.Technical
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.ComponentModel.DataAnnotations;
    using System.Drawing;
    using System.Linq;
    using System.Windows.Input;
    using System.Xml.Linq;
    using ATAS.Indicators.Drawing;
    using OFT.Attributes;
    using OFT.Localization;
    using OFT.Rendering.Context;
    using OFT.Rendering.Settings;
    using OFT.Rendering.Tools;
    using CrossColor = System.Windows.Media.Color;
    using CrossColors = System.Windows.Media.Colors;

    [DisplayName("Zig Zag MOD")]
    [Display(ResourceType = typeof(Strings), Description = nameof(Strings.ZigzagIndDescription))]
    public class ZigZagMod : Indicator
    {
        #region Enums / Nested types
        public enum Mode { [Display(ResourceType = typeof(Strings), Name = nameof(Strings.RelativeInPercent))] Relative, [Display(ResourceType = typeof(Strings), Name = nameof(Strings.AbsolutePrice))] Absolute, [Display(ResourceType = typeof(Strings), Name = nameof(Strings.Ticks))] Ticks }
        public enum TimeFormat { [Display(ResourceType = typeof(Strings), Name = nameof(Strings.None))] None, [Display(ResourceType = typeof(Strings), Name = nameof(Strings.Days))] Days, [Display(ResourceType = typeof(Strings), Name = nameof(Strings.Exact))] Exact }
        public enum StatsAlignment { [Display(Name = "Left")] Left, [Display(Name = "Center")] Center, [Display(Name = "Right")] Right }
        public enum ColorScaleMode { ZScore, Range, Percentile }

        private sealed class HeatmapSegment
        {
            public int FromBar;
            public int ToBar;
            public decimal FromPrice;
            public decimal ToPrice;
            public Color Color;
        }

        public sealed class ImpulseInfo
        {
            public int StartBar { get; init; }
            public int EndBar { get; init; }
            public int Direction { get; init; } // 1 bull, -1 bear
            public decimal Ticks { get; init; }
            public decimal Z { get; init; }
            public decimal Volume { get; init; }
            public decimal Delta { get; init; }
            public int Bars { get; init; }
            public TimeSpan Duration { get; init; }
            public DateTime EndTime { get; init; }
        }
        #endregion

        #region Fields
        private readonly ValueDataSeries _data = new("Data", Strings.Data)
        {
            Color = DefaultColors.Red.Convert(),
            LineDashStyle = LineDashStyle.Dot,
            VisualType = VisualMode.Line,
            Width = 2,
            DescriptionKey = nameof(Strings.BaseLineSettingsDescription)
        };

        private Mode _calcMode = Mode.Ticks;
        private int _days = 20;

        // Wave detection
        private int _direction;
        private bool _ignoreWicks = true;
        private int _lastBar = -1;
        private int _lastHighBar;
        private int _lastLowBar;
        private int _targetBar;
        private decimal _percentage = 30m;

        // Aggregated wave stats (for pivot label)
        private decimal _cumulativeVolume;
        private decimal _cumulativeDelta;
        private decimal _cumulativeTicks;
        private int _cumulativeBars;
        private TimeSpan _trendDuration;

        // Text settings
        private bool _showDelta = true;
        private bool _showVolume = true;
        private bool _showTicks = true;
        private bool _showBars = true;
        private TimeFormat _showTime = TimeFormat.Exact;
        private CrossColor _textColor = DefaultColors.Red.Convert();
        private float _textSize = 15f;
        private int _verticalOffset = 1;

        // Lines till touch
        private bool _enableLineTillTouch = true;
        private CrossColor _highLineColor = CrossColors.Gold;
        private CrossColor _lowLineColor = CrossColors.DeepSkyBlue;
        private int _lineTillTouchWidth = 2;
        private bool _hideTouchedLines;
        private Pen _highPen;
        private Pen _lowPen;
        private readonly System.Collections.Generic.List<LineTillTouch> _hiddenTouchedLines = new();

        // HUD / Stats
        private bool _showStatsHud = true;
        private int _sessionStartHour = 9;
        private int _sessionStartMinute = 30;
        private int _sessionEndHour = 17;
        private int _sessionEndMinute = 0;
        private StatsAlignment _statsAlign = StatsAlignment.Left;
        private CrossColor _statsTextColor = CrossColors.White;
        private float _statsFontSize = 12f;
        private int _hudPaddingX = 10;
        private int _hudPaddingY = 8;
        private int _statsAnchorBar = -1;
        private string _statsText = string.Empty;
        private bool _statsCompactMode;
        private bool _showHudBackground = true;
        private CrossColor _hudBackgroundColor = CrossColors.Black;

        // Impulse statistics (bull/bear)
        private int _minImpulseTicks = 0;
        private decimal _bullSumTicks;
        private int _bullCount;
        private decimal _bullM2;
        private decimal? _bullMinTicks;
        private decimal? _bullMaxTicks;

        private decimal _bearSumTicks;
        private int _bearCount;
        private decimal _bearM2;
        private decimal? _bearMinTicks;
        private decimal? _bearMaxTicks;

        private int _currentStreakDir;
        private int _currentStreakLen;
        private int _bullMaxStreak;
        private int _bearMaxStreak;

        private DateTime? _currentSessionDate;
        private DateTime? _lastWaveEndTime;

        private decimal _lastImpulseTicks;
        private decimal _lastImpulseZ;
        private int _lastImpulseDir;

        // Live label
        private const string LiveLabelId = "ZZ_LIVE";
        private bool _showLiveWaveLabel = true;
        private Color _liveLabelCurrentColor;
        private bool _liveWaveDynamicColor = true;

        // Outliers
        private bool _enableOutlierHighlight = true;
        private decimal _outlierZThreshold = 2m;
        private CrossColor _outlierBullColor = CrossColors.Lime;
        private CrossColor _outlierBearColor = CrossColors.OrangeRed;

        // Color scaling (unificado)
        private ColorScaleMode _colorMode = ColorScaleMode.ZScore;
        private decimal _heatmapZMax = 3m;

        // Heatmap colors
        private bool _enableHeatmap;
        private CrossColor _heatmapBullLowColor = CrossColors.LightGreen;
        private CrossColor _heatmapBullHighColor = CrossColors.DarkGreen;
        private CrossColor _heatmapBearLowColor = CrossColors.LightCoral;
        private CrossColor _heatmapBearHighColor = CrossColors.DarkRed;

        // Live wave gradient colors
        private CrossColor _liveWaveBullLowColor = CrossColors.LightGreen;
        private CrossColor _liveWaveBullHighColor = CrossColors.DarkGreen;
        private CrossColor _liveWaveBearLowColor = CrossColors.LightCoral;
        private CrossColor _liveWaveBearHighColor = CrossColors.DarkRed;

        // Legend
        private bool _showHeatmapLegend = true;
        private int _heatmapLegendOffsetX = 8;
        private int _heatmapLegendOffsetY = 8;
        private int _heatmapLegendWidth = 120;
        private int _heatmapLegendHeight = 46;
        private int _heatmapLegendSteps = 60;
        private bool _heatmapLegendShowStats = true;
        private bool _heatmapLegendShowHistogram = true;
        private int _histBuckets = 10;
        private int[] _bullHist = Array.Empty<int>();
        private int[] _bearHist = Array.Empty<int>();
        private Rectangle _legendRect = Rectangle.Empty;

        // Robustez
        private double _stdDevEps = 1e-6;

        // Impulses export
        private readonly System.Collections.Generic.List<ImpulseInfo> _impulses = new();
        // Heatmap runtime collections
        private readonly List<HeatmapSegment> _heatmapSegments = new();
        private readonly Dictionary<int, RenderPen> _heatmapPens = new();
        #endregion

        #region Properties (config)
        [Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Calculation), Name = nameof(Strings.DaysLookBack), Order = int.MaxValue)]
        [Range(0, 1000)]
        public int Days { get; set; } = 20;

        [Display(ResourceType = typeof(Strings), Name = nameof(Strings.CalculationMode), GroupName = nameof(Strings.CalculationSettings), Order = 100)]
        public Mode CalcMode { get => _calcMode; set { _calcMode = value; RecalculateValues(); } }

        [Display(ResourceType = typeof(Strings), Name = nameof(Strings.IgnoreWicks), GroupName = nameof(Strings.CalculationSettings), Order = 110)]
        public bool IgnoreWicks { get => _ignoreWicks; set { _ignoreWicks = value; RecalculateValues(); } }

        [Range(0, int.MaxValue)]
        [Display(ResourceType = typeof(Strings), Name = nameof(Strings.RequiredChange), GroupName = nameof(Strings.CalculationSettings), Order = 120)]
        public decimal Percentage { get => _percentage; set { _percentage = value; RecalculateValues(); } }

        [Range(1, 100)]
        [Display(ResourceType = typeof(Strings), Name = nameof(Strings.TextSize), GroupName = nameof(Strings.TextSettings), Order = 200)]
        public float TextSize { get => _textSize; set { _textSize = value; RecalculateValues(); } }

        [Display(ResourceType = typeof(Strings), Name = nameof(Strings.TextColor), GroupName = nameof(Strings.TextSettings), Order = 210)]
        public CrossColor TextColor { get => _textColor; set { _textColor = value; RecalculateValues(); } }

        [Display(ResourceType = typeof(Strings), Name = nameof(Strings.ShowDelta), GroupName = nameof(Strings.TextSettings), Order = 220)]
        public bool ShowDelta { get => _showDelta; set { _showDelta = value; RedrawChart(); } }

        [Display(ResourceType = typeof(Strings), Name = nameof(Strings.ShowVolume), GroupName = nameof(Strings.TextSettings), Order = 230)]
        public bool ShowVolume { get => _showVolume; set { _showVolume = value; RedrawChart(); } }

        [Display(ResourceType = typeof(Strings), Name = nameof(Strings.ShowTicks), GroupName = nameof(Strings.TextSettings), Order = 240)]
        public bool ShowTicks { get => _showTicks; set { _showTicks = value; RedrawChart(); } }

        [Display(ResourceType = typeof(Strings), Name = nameof(Strings.ShowBars), GroupName = nameof(Strings.TextSettings), Order = 250)]
        public bool ShowBars { get => _showBars; set { _showBars = value; RedrawChart(); } }

        [Display(ResourceType = typeof(Strings), Name = nameof(Strings.ShowTime), GroupName = nameof(Strings.TextSettings), Order = 260)]
        public TimeFormat ShowTime { get => _showTime; set { _showTime = value; RedrawChart(); } }

        [Display(Name = "Show Live Wave Label", GroupName = "TextSettings", Order = 265)]
        public bool ShowLiveWaveLabel
        {
            get => _showLiveWaveLabel;
            set
            {
                _showLiveWaveLabel = value;
                if (!_showLiveWaveLabel && Labels.ContainsKey(LiveLabelId))
                    Labels[LiveLabelId].Text = string.Empty;
                RedrawChart();
            }
        }

        [Display(Name = "Live Wave Dynamic Color", GroupName = "TextSettings", Order = 266)]
        public bool LiveWaveDynamicColor { get => _liveWaveDynamicColor; set { _liveWaveDynamicColor = value; RedrawChart(); } }

        // Enum de escala unificada
        [Display(Name = "Color Scale Mode", GroupName = "Heatmap", Order = 805)]
        public ColorScaleMode ColorMode { get => _colorMode; set { _colorMode = value; RedrawChart(); } }

        [Display(Name = "Z Max", GroupName = "Heatmap", Order = 820)]
        [Range(0.1, 20)]
        public decimal HeatmapZMax { get => _heatmapZMax; set { _heatmapZMax = Math.Clamp(value, 0.1m, 20m); RedrawChart(); } }

        [Display(Name = "Bull Low Color", GroupName = "Heatmap", Order = 830)]
        public CrossColor HeatmapBullLowColor { get => _heatmapBullLowColor; set { _heatmapBullLowColor = value; RedrawChart(); } }

        [Display(Name = "Bull High Color", GroupName = "Heatmap", Order = 840)]
        public CrossColor HeatmapBullHighColor { get => _heatmapBullHighColor; set { _heatmapBullHighColor = value; RedrawChart(); } }

        [Display(Name = "Bear Low Color", GroupName = "Heatmap", Order = 850)]
        public CrossColor HeatmapBearLowColor { get => _heatmapBearLowColor; set { _heatmapBearLowColor = value; RedrawChart(); } }

        [Display(Name = "Bear High Color", GroupName = "Heatmap", Order = 860)]
        public CrossColor HeatmapBearHighColor { get => _heatmapBearHighColor; set { _heatmapBearHighColor = value; RedrawChart(); } }

        [Display(Name = "Show Heatmap Legend", GroupName = "Heatmap", Order = 865)]
        public bool ShowHeatmapLegend { get => _showHeatmapLegend; set { _showHeatmapLegend = value; RedrawChart(); } }

        [Display(Name = "Legend Offset X", GroupName = "Heatmap", Order = 866)]
        [Range(0, 2000)]
        public int HeatmapLegendOffsetX { get => _heatmapLegendOffsetX; set { _heatmapLegendOffsetX = Math.Clamp(value, 0, 2000); RedrawChart(); } }

        [Display(Name = "Legend Offset Y", GroupName = "Heatmap", Order = 867)]
        [Range(0, 2000)]
        public int HeatmapLegendOffsetY { get => _heatmapLegendOffsetY; set { _heatmapLegendOffsetY = Math.Clamp(value, 0, 2000); RedrawChart(); } }

        [Display(Name = "Legend Width", GroupName = "Heatmap", Order = 868)]
        [Range(20, 1000)]
        public int HeatmapLegendWidth { get => _heatmapLegendWidth; set { _heatmapLegendWidth = Math.Clamp(value, 20, 1000); RedrawChart(); } }

        [Display(Name = "Legend Height", GroupName = "Heatmap", Order = 869)]
        [Range(20, 400)]
        public int HeatmapLegendHeight { get => _heatmapLegendHeight; set { _heatmapLegendHeight = Math.Clamp(value, 20, 400); RedrawChart(); } }

        [Display(Name = "Legend Steps", GroupName = "Heatmap", Order = 870)]
        [Range(2, 500)]
        public int HeatmapLegendSteps { get => _heatmapLegendSteps; set { _heatmapLegendSteps = Math.Clamp(value, 2, 500); RedrawChart(); } }

        [Display(Name = "Legend Show Stats", GroupName = "Heatmap", Order = 871)]
        public bool HeatmapLegendShowStats { get => _heatmapLegendShowStats; set { _heatmapLegendShowStats = value; RedrawChart(); } }

        [Display(Name = "Legend Show Histogram", GroupName = "Heatmap", Order = 872)]
        public bool HeatmapLegendShowHistogram { get => _heatmapLegendShowHistogram; set { _heatmapLegendShowHistogram = value; RedrawChart(); } }

        [Display(Name = "Legend Histogram Buckets", GroupName = "Heatmap", Order = 873)]
        [Range(2, 50)]
        public int HeatmapLegendHistogramBuckets
        {
            get => _histBuckets;
            set
            {
                var v = Math.Clamp(value, 2, 50);
                if (_histBuckets == v) return;
                _histBuckets = v;
                RebuildHistArrays();
                _legendRect = Rectangle.Empty;
                RedrawChart();
            }
        }

        [Display(Name = "Enable LineTillTouch", GroupName = "LineTillTouch", Order = 300)]
        public bool EnableLineTillTouch { get => _enableLineTillTouch; set { _enableLineTillTouch = value; RecalculateValues(); } }

        [Display(Name = "High Line Color", GroupName = "LineTillTouch", Order = 310)]
        public CrossColor HighLineColor { get => _highLineColor; set { _highLineColor = value; RecalculateValues(); } }

        [Display(Name = "Low Line Color", GroupName = "LineTillTouch", Order = 320)]
        public CrossColor LowLineColor { get => _lowLineColor; set { _lowLineColor = value; RecalculateValues(); } }

        [Range(1, 50)]
        [Display(Name = "Line Width", GroupName = "LineTillTouch", Order = 330)]
        public int LineTillTouchWidth { get => _lineTillTouchWidth; set { _lineTillTouchWidth = Math.Clamp(value, 1, 50); RecalculateValues(); } }

        [Display(Name = "Hide touched lines", GroupName = "LineTillTouch", Order = 340)]
        public bool HideTouchedLines
        {
            get => _hideTouchedLines;
            set
            {
                if (_hideTouchedLines == value) return;
                _hideTouchedLines = value;
                if (_hideTouchedLines) RemoveTouchedLines(CurrentBar - 1);
                else RestoreTouchedLines();
                RedrawChart();
            }
        }

        [Display(Name = "Toggle hide hotkey", GroupName = "LineTillTouch", Order = 350)]
        public Key ToggleHideTouchedHotkey { get; set; } = Key.F8;

        [Display(Name = "Show Stats HUD", GroupName = "Stats", Order = 500)]
        public bool ShowStatsHud { get => _showStatsHud; set { _showStatsHud = value; RedrawChart(); } }

        [Display(Name = "Compact Mode", GroupName = "Stats", Order = 505)]
        public bool StatsCompactMode { get => _statsCompactMode; set { _statsCompactMode = value; RedrawChart(); } }

        [Display(Name = "Show HUD Background", GroupName = "Stats", Order = 506)]
        public bool ShowHudBackground { get => _showHudBackground; set { _showHudBackground = value; RedrawChart(); } }

        [Display(Name = "HUD Background Color", GroupName = "Stats", Order = 507)]
        public CrossColor HudBackgroundColor { get => _hudBackgroundColor; set { _hudBackgroundColor = value; RedrawChart(); } }

        [Display(Name = "Session Start Hour", GroupName = "Stats", Order = 510)]
        [Range(0, 23)]
        public int SessionStartHour { get => _sessionStartHour; set { _sessionStartHour = Math.Clamp(value, 0, 23); ResetSessionDate(); } }

        [Display(Name = "Session Start Minute", GroupName = "Stats", Order = 520)]
        [Range(0, 59)]
        public int SessionStartMinute { get => _sessionStartMinute; set { _sessionStartMinute = Math.Clamp(value, 0, 59); ResetSessionDate(); } }

        [Display(Name = "Session End Hour", GroupName = "Stats", Order = 530)]
        [Range(0, 23)]
        public int SessionEndHour { get => _sessionEndHour; set { _sessionEndHour = Math.Clamp(value, 0, 23); RedrawChart(); } }

        [Display(Name = "Session End Minute", GroupName = "Stats", Order = 540)]
        [Range(0, 59)]
        public int SessionEndMinute { get => _sessionEndMinute; set { _sessionEndMinute = Math.Clamp(value, 0, 59); RedrawChart(); } }

        [Display(Name = "Stats Alignment", GroupName = "Stats", Order = 550)]
        public StatsAlignment StatsHudAlignment { get => _statsAlign; set { _statsAlign = value; RedrawChart(); } }

        [Display(Name = "Stats Text Color", GroupName = "Stats", Order = 560)]
        public CrossColor StatsTextColor { get => _statsTextColor; set { _statsTextColor = value; RedrawChart(); } }

        [Display(Name = "Stats Font Size", GroupName = "Stats", Order = 570)]
        [Range(6, 48)]
        public float StatsFontSize { get => _statsFontSize; set { _statsFontSize = Math.Clamp(value, 6f, 48f); RedrawChart(); } }

        [Display(Name = "HUD Padding X", GroupName = "Stats", Order = 580)]
        [Range(0, 500)]
        public int HudPaddingX { get => _hudPaddingX; set { _hudPaddingX = Math.Clamp(value, 0, 500); RedrawChart(); } }

        [Display(Name = "HUD Padding Y", GroupName = "Stats", Order = 590)]
        [Range(0, 500)]
        public int HudPaddingY { get => _hudPaddingY; set { _hudPaddingY = Math.Clamp(value, 0, 500); RedrawChart(); } }

        [Display(Name = "Stats Anchor Bar (-1 = last)", GroupName = "Stats", Order = 595)]
        [Range(-1, 1000000)]
        public int StatsAnchorBar { get => _statsAnchorBar; set { _statsAnchorBar = value; RedrawChart(); } }

        [Display(Name = "Min Impulse Ticks", GroupName = "Stats", Order = 600)]
        [Range(0, 100000)]
        public int MinImpulseTicks { get => _minImpulseTicks; set { _minImpulseTicks = Math.Max(0, value); RedrawChart(); } }

        [Display(Name = "Enable Outlier Highlight", GroupName = "Outliers", Order = 700)]
        public bool EnableOutlierHighlight { get => _enableOutlierHighlight; set { _enableOutlierHighlight = value; RecalculateValues(); } }

        [Display(Name = "Outlier Z Threshold", GroupName = "Outliers", Order = 710)]
        [Range(0, 100)]
        public decimal OutlierZThreshold { get => _outlierZThreshold; set { _outlierZThreshold = Math.Clamp(value, 0m, 100m); RecalculateValues(); } }

        [Display(Name = "Outlier Bull Color", GroupName = "Outliers", Order = 720)]
        public CrossColor OutlierBullColor { get => _outlierBullColor; set { _outlierBullColor = value; RecalculateValues(); } }

        [Display(Name = "Outlier Bear Color", GroupName = "Outliers", Order = 730)]
        public CrossColor OutlierBearColor { get => _outlierBearColor; set { _outlierBearColor = value; RecalculateValues(); } }

        [Display(Name = "Enable Heatmap", GroupName = "Heatmap", Order = 799)]
        public bool EnableHeatmap { get => _enableHeatmap; set { _enableHeatmap = value; RecalculateValues(); } }

        // Robustez
        [Display(Name = "StdDev Epsilon", GroupName = "Robustness", Order = 900)]
        [Range(1e-8, 1e-2)]
        public double StdDevEpsilon { get => _stdDevEps; set { _stdDevEps = Math.Clamp(value, 1e-8, 1e-2); RedrawChart(); } }

        // Compatibilidad (ocultar legacy booleans)
        [Browsable(false)]
        public bool HeatmapUseZScore { get => _colorMode == ColorScaleMode.ZScore; set { _colorMode = value ? ColorScaleMode.ZScore : ColorScaleMode.Range; RedrawChart(); } }

        [Browsable(false)]
        public bool LiveWaveUseZScore { get => _colorMode == ColorScaleMode.ZScore; set { _colorMode = value ? ColorScaleMode.ZScore : ColorScaleMode.Range; RedrawChart(); } }

        [Browsable(false)]
        public System.Collections.Generic.IReadOnlyList<ImpulseInfo> Impulses => _impulses;
        #endregion

        #region ctor
        public ZigZagMod() : base(true)
        {
            DataSeries[0].IsHidden = true;
            DenyToChangePanel = true;
            DataSeries.Add(_data);
            EnableCustomDrawing = true;

            _highPen = new Pen(ConvertColor(_highLineColor), _lineTillTouchWidth);
            _lowPen = new Pen(ConvertColor(_lowLineColor), _lineTillTouchWidth);
        }
        #endregion

        #region Input Handling
        public override bool ProcessKeyDown(KeyEventArgs e)
        {
            if (e.Key == ToggleHideTouchedHotkey)
            {
                HideTouchedLines = !HideTouchedLines;
                return true;
            }
            return false;
        }
        #endregion

        #region Wave Detection
        private bool HasValidTickSize => InstrumentInfo != null && InstrumentInfo.TickSize > 0m;

        protected override void OnRecalculate()
        {
            _direction = 0;

            _highPen?.Dispose();
            _lowPen?.Dispose();
            _highPen = new Pen(ConvertColor(_highLineColor), _lineTillTouchWidth);
            _lowPen = new Pen(ConvertColor(_lowLineColor), _lineTillTouchWidth);

            HorizontalLinesTillTouch.Clear();
            _hiddenTouchedLines.Clear();
            _heatmapSegments.Clear();
            _impulses.Clear();

            ResetSessionStats();
            _statsText = string.Empty;

            if (_showLiveWaveLabel && HasValidTickSize && CurrentBar > 0 && !Labels.ContainsKey(LiveLabelId))
            {
                _liveLabelCurrentColor = ConvertColor(_textColor);
                AddText(LiveLabelId, string.Empty, true, 0, 0,
                    _liveLabelCurrentColor, Color.Transparent, Color.Transparent,
                    _textSize, DrawingText.TextAlign.Left);
            }

            RebuildHistArrays();
            _legendRect = Rectangle.Empty;

            base.OnRecalculate();
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            if (!HasValidTickSize)
                return;

            if (bar == 0)
            {
                _targetBar = 0;
                if (_days > 0)
                {
                    var days = 0;
                    for (var i = CurrentBar - 1; i >= 0; i--)
                    {
                        _targetBar = i;
                        if (!IsNewSession(i))
                            continue;
                        if (++days == _days)
                            break;
                    }
                }

                _lastHighBar = _targetBar;
                _lastLowBar = _targetBar;
                if (_targetBar > 0)
                    _data.SetPointOfEndLine(_targetBar - 1);

                if (_showLiveWaveLabel && !Labels.ContainsKey(LiveLabelId))
                    AddText(LiveLabelId, string.Empty, true, 0, 0,
                        ConvertColor(_textColor), Color.Transparent, Color.Transparent,
                        _textSize, DrawingText.TextAlign.Left);
                return;
            }

            if (bar < _targetBar || bar < Math.Min(_lastHighBar, _lastLowBar))
                return;

            var calcBar = bar - 1;
            var candleHighBody = GetHigh(calcBar);
            var candleLowBody = GetLow(calcBar);
            var lastHighBody = GetHigh(_lastHighBar);
            var lastLowBody = GetLow(_lastLowBar);

            var currentTime = GetCandle(calcBar).Time;
            CheckSessionBoundary(currentTime);

            if (bar != _lastBar)
            {
                var requiredChange = 0m;

                if (_direction == 0)
                {
                    var zHigh = GetHigh(0);
                    var zLow = GetLow(0);
                    if (candleHighBody > zHigh && candleLowBody > zLow)
                    {
                        _direction = 1;
                        _lastHighBar = calcBar;
                    }
                    else if (candleLowBody < zLow && candleHighBody < zLow)
                    {
                        _direction = -1;
                        _lastLowBar = calcBar;
                    }
                }
                else if (_direction == 1)
                {
                    requiredChange = RequiredChange(lastHighBody);
                    if (candleHighBody > lastHighBody)
                        _lastHighBar = calcBar;
                    else if (candleHighBody < lastHighBody && lastHighBody - requiredChange >= candleLowBody)
                    {
                        var startBar = _lastLowBar;
                        var endBar = _lastHighBar;
                        var rawStart = GetCandle(startBar).Low;
                        var rawEnd = GetCandle(endBar).High;

                        RecordWaveImpulse(startBar, endBar, rawStart, rawEnd, true);
                        AggregateWave(_lastLowBar, _lastHighBar, lastLowBody, lastHighBody);
                        RegisterHeatmapSegment(startBar, endBar, rawStart, rawEnd, true);
                        DrawPivotLabel(endBar, rawEnd, true);

                        if (EnableLineTillTouch)
                            HorizontalLinesTillTouch.Add(new LineTillTouch(_lastHighBar, GetCandle(_lastHighBar).High, _highPen));

                        _direction = -1;
                        _lastLowBar = calcBar;
                    }
                }
                else if (_direction == -1)
                {
                    requiredChange = RequiredChange(lastLowBody);
                    if (candleLowBody < lastLowBody)
                        _lastLowBar = calcBar;
                    else if (candleLowBody > lastLowBody && lastLowBody + requiredChange <= candleHighBody)
                    {
                        var startBar = _lastHighBar;
                        var endBar = _lastLowBar;
                        var rawStart = GetCandle(startBar).High;
                        var rawEnd = GetCandle(endBar).Low;

                        RecordWaveImpulse(startBar, endBar, rawStart, rawEnd, false);
                        AggregateWave(_lastHighBar, _lastLowBar, lastHighBody, lastLowBody);
                        RegisterHeatmapSegment(startBar, endBar, rawStart, rawEnd, false);
                        DrawPivotLabel(endBar, rawEnd, false);

                        if (EnableLineTillTouch)
                            HorizontalLinesTillTouch.Add(new LineTillTouch(_lastLowBar, GetCandle(_lastLowBar).Low, _lowPen));

                        _direction = 1;
                        _lastHighBar = calcBar;
                    }
                }
            }

            if (HideTouchedLines)
                RemoveTouchedLines(bar);

            if (bar == SourceDataSeries.Count - 1)
                DrawLastWave(bar);

            UpdateStatsInternal();
            UpdateLiveWaveLabel();
            _lastBar = bar;
        }
        #endregion

        #region Rendering
        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            base.OnRender(context, layout);

            if (_enableHeatmap && ChartInfo?.PriceChartContainer != null && _heatmapSegments.Count > 0)
            {
                foreach (var seg in _heatmapSegments)
                {
                    if (seg.FromBar < 0 || seg.ToBar < 0 || seg.FromBar >= CurrentBar || seg.ToBar >= CurrentBar)
                        continue;

                    int x1, x2, y1, y2;
                    try
                    {
                        x1 = ChartInfo.PriceChartContainer.GetXByBar(seg.FromBar, false);
                        x2 = ChartInfo.PriceChartContainer.GetXByBar(seg.ToBar, false);
                        y1 = ChartInfo.PriceChartContainer.GetYByPrice(seg.FromPrice, false);
                        y2 = ChartInfo.PriceChartContainer.GetYByPrice(seg.ToPrice, false);
                    }
                    catch
                    {
                        continue;
                    }

                    var pen = GetHeatmapPen(seg.Color);
                    context.DrawLine(pen, x1, y1, x2, y2);
                }
            }

            if (_enableHeatmap && _showHeatmapLegend)
                DrawHeatmapLegend(context);

            if (!_showStatsHud || string.IsNullOrEmpty(_statsText) || ChartInfo?.PriceChartContainer == null)
                return;

            string[] lines;
            if (_statsCompactMode)
            {
                var compact = _statsText.Replace('\n', ' ');
                while (compact.Contains("  "))
                    compact = compact.Replace("  ", " ");
                lines = new[] { compact.Trim() };
            }
            else
                lines = _statsText.Split('\n');

            var font = new RenderFont("Arial", _statsFontSize);
            int anchorBar = _statsAnchorBar >= 0 && _statsAnchorBar < CurrentBar ? _statsAnchorBar : CurrentBar - 1;
            if (anchorBar < 0) return;

            int anchorX;
            try { anchorX = ChartInfo.PriceChartContainer.GetXByBar(anchorBar, false); }
            catch { return; }

            int maxChars = 0;
            foreach (var l in lines)
                maxChars = Math.Max(maxChars, l.Length);
            int estWidth = (int)(maxChars * (_statsFontSize * 0.55f));
            int totalHeight = (int)(lines.Length * (_statsFontSize + 3));

            int x = anchorX;
            switch (_statsAlign)
            {
                case StatsAlignment.Center: x = anchorX - estWidth / 2; break;
                case StatsAlignment.Right: x = anchorX - estWidth - _hudPaddingX; break;
                default: x = anchorX + _hudPaddingX; break;
            }

            int y = _hudPaddingY;

            if (_showHudBackground)
            {
                try
                {
                    int pad = 6;
                    var rect = new Rectangle(x - pad, y - pad, estWidth + pad * 2, totalHeight + pad * 2 - 3);
                    context.FillRectangle(ConvertColor(_hudBackgroundColor), rect);
                }
                catch { }
            }

            var textColor = ConvertColor(_statsTextColor);
            foreach (var line in lines)
            {
                context.DrawString(line, font, textColor, x, y);
                y += (int)(_statsFontSize + 3);
            }
        }
        #endregion

        #region Stats
        private void RecordWaveImpulse(int startBar, int endBar, decimal startPrice, decimal endPrice, bool bullish)
        {
            if (!HasValidTickSize) return;
            var pivotTime = GetCandle(endBar).Time;
            if (!IsInRth(pivotTime)) return;

            var diff = endPrice - startPrice;
            var ticks = Math.Abs(diff / InstrumentInfo.TickSize);
            if (ticks <= 0 || ticks < _minImpulseTicks)
                return;

            // Side update
            if (bullish && diff > 0)
            {
                _bullCount++;
                var prevMean = _bullCount > 1 ? _bullSumTicks / (_bullCount - 1) : 0;
                _bullSumTicks += ticks;
                var mean = _bullSumTicks / _bullCount;
                _bullM2 += (ticks - prevMean) * (ticks - mean);
                if (!_bullMinTicks.HasValue || ticks < _bullMinTicks.Value) _bullMinTicks = ticks;
                if (!_bullMaxTicks.HasValue || ticks > _bullMaxTicks.Value) _bullMaxTicks = ticks;
                UpdateStreak(1);
                var sd = ComputeStdev(_bullCount, _bullM2);
                _lastImpulseTicks = ticks;
                _lastImpulseDir = 1;
                _lastImpulseZ = (sd > (decimal)_stdDevEps && sd != 0) ? (ticks - mean) / sd : 0;
            }
            else if (!bullish && diff < 0)
            {
                _bearCount++;
                var prevMean = _bearCount > 1 ? _bearSumTicks / (_bearCount - 1) : 0;
                _bearSumTicks += ticks;
                var mean = _bearSumTicks / _bearCount;
                _bearM2 += (ticks - prevMean) * (ticks - mean);
                if (!_bearMinTicks.HasValue || ticks < _bearMinTicks.Value) _bearMinTicks = ticks;
                if (!_bearMaxTicks.HasValue || ticks > _bearMaxTicks.Value) _bearMaxTicks = ticks;
                UpdateStreak(-1);
                var sd = ComputeStdev(_bearCount, _bearM2);
                _lastImpulseTicks = ticks;
                _lastImpulseDir = -1;
                _lastImpulseZ = (sd > (decimal)_stdDevEps && sd != 0) ? (ticks - mean) / sd : 0;
            }
            else
                return;

            // Saturación de z
            var sat = HeatmapZMax * 2m;
            if (_lastImpulseZ > sat) _lastImpulseZ = sat;
            else if (_lastImpulseZ < -sat) _lastImpulseZ = -sat;

            // Histogram
            if (bullish) UpdateHistogram(true, ticks); else UpdateHistogram(false, ticks);

            // Guardar impulso
            var bars = Math.Abs(endBar - startBar) + 1;
            var duration = GetCandle(endBar).Time - GetCandle(startBar).Time;
            decimal vol = 0;
            decimal delta = 0;
            for (int i = startBar; i <= endBar; i++)
            {
                var c = GetCandle(i);
                vol += c.Volume;
                delta += c.Delta;
            }

            _impulses.Add(new ImpulseInfo
            {
                StartBar = startBar,
                EndBar = endBar,
                Direction = bullish ? 1 : -1,
                Ticks = ticks,
                Z = _lastImpulseZ,
                Volume = vol,
                Delta = delta,
                Bars = bars,
                Duration = duration,
                EndTime = GetCandle(endBar).Time
            });

            _lastWaveEndTime = pivotTime;
        }

        private void UpdateStreak(int dir)
        {
            if (_currentStreakDir == dir) _currentStreakLen++;
            else { _currentStreakDir = dir; _currentStreakLen = 1; }
            if (dir == 1) _bullMaxStreak = Math.Max(_bullMaxStreak, _currentStreakLen);
            else if (dir == -1) _bearMaxStreak = Math.Max(_bearMaxStreak, _currentStreakLen);
        }

        private decimal ComputeStdev(int count, decimal m2)
        {
            if (count <= 1) return 0;
            var var = m2 / (count - 1);
            if (var <= (decimal)_stdDevEps * (decimal)_stdDevEps) return 0;
            return (decimal)Math.Sqrt((double)var);
        }

        private void UpdateStatsInternal()
        {
            if (!_showStatsHud) { _statsText = string.Empty; return; }

            var bullAvg = _bullCount > 0 ? _bullSumTicks / _bullCount : 0;
            var bearAvg = _bearCount > 0 ? _bearSumTicks / _bearCount : 0;
            var bullSd = ComputeStdev(_bullCount, _bullM2);
            var bearSd = ComputeStdev(_bearCount, _bearM2);
            var bullCv = bullAvg > 0 ? bullSd / bullAvg : 0;
            var bearCv = bearAvg > 0 ? bearSd / bearAvg : 0;
            int total = _bullCount + _bearCount;
            var bullRatio = total > 0 ? (decimal)_bullCount / total * 100m : 0;
            var dominance = (bullAvg + bearAvg) > 0 ? (bullAvg - bearAvg) / (bullAvg + bearAvg) * 100m : 0;

            string bullMinStr = _bullMinTicks?.ToString("0.#") ?? "-";
            string bullMaxStr = _bullMaxTicks?.ToString("0.#") ?? "-";
            string bearMinStr = _bearMinTicks?.ToString("0.#") ?? "-";
            string bearMaxStr = _bearMaxTicks?.ToString("0.#") ?? "-";
            string streakBull = $"{(_currentStreakDir == 1 ? _currentStreakLen : 0)}/{_bullMaxStreak}";
            string streakBear = $"{(_currentStreakDir == -1 ? _currentStreakLen : 0)}/{_bearMaxStreak}";
            string lastImpulseStr = _lastImpulseTicks > 0
                ? $"{_lastImpulseTicks:0.#}t Z:{_lastImpulseZ:0.##}{(_lastImpulseDir == 1 ? "B" : _lastImpulseDir == -1 ? "S" : "")}"
                : "-";

            _statsText =
                $"RTH ({_sessionStartHour:00}:{_sessionStartMinute:00}-{_sessionEndHour:00}:{_sessionEndMinute:00})\n" +
                $"Bull Avg:{bullAvg:0.#} σ:{bullSd:0.#} Min:{bullMinStr} Max:{bullMaxStr} Stk:{streakBull}\n" +
                $"Bear Avg:{bearAvg:0.#} σ:{bearSd:0.#} Min:{bearMinStr} Max:{bearMaxStr} Stk:{streakBear}\n" +
                $"Bull%:{bullRatio:0.#}% Dom:{dominance:0.#}% CVB:{bullCv:0.00} CVS:{bearCv:0.00} Last:{lastImpulseStr}";
        }

        private void CheckSessionBoundary(DateTime currentTime)
        {
            if (_currentSessionDate == null || _currentSessionDate.Value.Date != currentTime.Date)
            {
                if (IsInRth(currentTime))
                    StartNewSession(currentTime);
                else
                    _currentSessionDate = currentTime.Date;
            }
            else
            {
                if (_lastWaveEndTime == null && IsInRth(currentTime))
                    StartNewSession(currentTime);
            }
        }

        private void StartNewSession(DateTime t)
        {
            _currentSessionDate = t.Date;
            ResetSessionStats();
            _heatmapSegments.Clear();
            _impulses.Clear();
            RebuildHistArrays();
        }

        private bool IsInRth(DateTime t)
        {
            var start = new TimeSpan(_sessionStartHour, _sessionStartMinute, 0);
            var end = new TimeSpan(_sessionEndHour, _sessionEndMinute, 0);
            var tod = t.TimeOfDay;
            return tod >= start && tod <= end;
        }

        private void ResetSessionStats()
        {
            _bullSumTicks = 0; _bullCount = 0; _bullM2 = 0; _bullMinTicks = null; _bullMaxTicks = null;
            _bearSumTicks = 0; _bearCount = 0; _bearM2 = 0; _bearMinTicks = null; _bearMaxTicks = null;
            _currentStreakDir = 0; _currentStreakLen = 0; _bullMaxStreak = 0; _bearMaxStreak = 0;
            _lastImpulseTicks = 0; _lastImpulseZ = 0; _lastImpulseDir = 0;
            _lastWaveEndTime = null;
            _statsText = string.Empty;
        }

        private void ResetSessionDate()
        {
            _currentSessionDate = null;
            ResetSessionStats();
            _heatmapSegments.Clear();
            _impulses.Clear();
            RebuildHistArrays();
            RedrawChart();
        }
        #endregion

        #region Color & Scaling
        private void UpdateLiveWaveLabel()
        {
            if (!_showLiveWaveLabel || !HasValidTickSize)
                return;

            if (!Labels.ContainsKey(LiveLabelId))
            {
                _liveLabelCurrentColor = ConvertColor(_textColor);
                AddText(LiveLabelId, string.Empty, true, 0, 0,
                    _liveLabelCurrentColor, Color.Transparent, Color.Transparent,
                    _textSize, DrawingText.TextAlign.Left);
            }

            var label = Labels[LiveLabelId];

            if (_direction == 0 || CurrentBar < 2)
            {
                label.Text = string.Empty;
                return;
            }

            int lastBar = CurrentBar - 1;
            int startBar;
            decimal startPrice;
            bool bullishLive = _direction == 1;

            if (bullishLive)
            {
                startBar = _lastLowBar;
                if (startBar < 0 || startBar > lastBar) { label.Text = string.Empty; return; }
                startPrice = GetCandle(startBar).Low;
            }
            else
            {
                startBar = _lastHighBar;
                if (startBar < 0 || startBar > lastBar) { label.Text = string.Empty; return; }
                startPrice = GetCandle(startBar).High;
            }

            decimal liveTicks = Math.Abs((GetCandle(lastBar).Close - startPrice) / InstrumentInfo.TickSize);
            int bars = Math.Abs(lastBar - startBar) + 1;
            decimal liveDelta = 0;
            decimal liveVol = 0;

            if (_showDelta || _showVolume)
            {
                for (int i = startBar; i <= lastBar; i++)
                {
                    var c = GetCandle(i);
                    if (_showDelta) liveDelta += c.Delta;
                    if (_showVolume) liveVol += c.Volume;
                }
            }

            var duration = GetCandle(lastBar).Time - GetCandle(startBar).Time;

            var sb = new System.Text.StringBuilder();
            if (_showTicks) sb.AppendLine($"{liveTicks:0.#} T");
            if (_showDelta) sb.AppendLine($"{liveDelta:0.#}Δ");
            if (_showVolume) sb.AppendLine($"{liveVol:0.#}");
            if (_showBars) sb.AppendLine($"{bars}B");
            if (_showTime != TimeFormat.None)
            {
                if (_showTime == TimeFormat.Days)
                    sb.AppendLine(duration.ToString(@"d\d\a\y\s"));
                else
                    sb.AppendLine(duration.Days > 0
                        ? duration.ToString(@"d\d\a\y\s\ hh\:mm\:ss")
                        : duration.ToString(@"hh\:mm\:ss"));
            }

            label.Text = sb.ToString().TrimEnd();
            label.IsAbovePrice = bullishLive;
            label.Bar = lastBar;
            label.TextPrice = GetCandle(lastBar).Close + (bullishLive ? 1 : -1) * InstrumentInfo.TickSize * _verticalOffset;

            if (_liveWaveDynamicColor)
            {
                var newColor = ComputeLiveWaveColor(bullishLive, liveTicks);
                if (newColor.ToArgb() != _liveLabelCurrentColor.ToArgb())
                {
                    try { Labels.Remove(LiveLabelId); } catch { }
                    _liveLabelCurrentColor = newColor;
                    AddText(LiveLabelId, label.Text, label.IsAbovePrice, label.Bar, label.TextPrice,
                        newColor, Color.Transparent, Color.Transparent,
                        _textSize, DrawingText.TextAlign.Left);
                }
            }
            else
            {
                var baseColor = ConvertColor(_textColor);
                if (_liveLabelCurrentColor.ToArgb() != baseColor.ToArgb())
                {
                    try { Labels.Remove(LiveLabelId); } catch { }
                    _liveLabelCurrentColor = baseColor;
                    AddText(LiveLabelId, label.Text, label.IsAbovePrice, label.Bar, label.TextPrice,
                        baseColor, Color.Transparent, Color.Transparent,
                        _textSize, DrawingText.TextAlign.Left);
                }
            }
        }

        private Color ComputeLiveWaveColor(bool bullish, decimal liveTicks)
        {
            float t = 0f;

            if (_colorMode == ColorScaleMode.ZScore)
            {
                decimal mean = 0;
                decimal sd = 0;
                if (bullish)
                {
                    if (_bullCount > 0) mean = _bullSumTicks / _bullCount;
                    sd = ComputeStdev(_bullCount, _bullM2);
                }
                else
                {
                    if (_bearCount > 0) mean = _bearSumTicks / _bearCount;
                    sd = ComputeStdev(_bearCount, _bearM2);
                }

                if (sd > (decimal)_stdDevEps)
                {
                    var z = (liveTicks - mean) / (sd == 0 ? 1 : sd);
                    var zAbs = Math.Abs(z);
                    double zAbsD = (double)zAbs;
                    double zMax = (double)HeatmapZMax;
                    if (zMax <= 0.0001) zMax = 1;
                    t = (float)Math.Min(zAbsD / zMax, 1.0);
                }
            }
            else if (_colorMode == ColorScaleMode.Range)
            {
                decimal? min = bullish ? _bullMinTicks : _bearMinTicks;
                decimal? max = bullish ? _bullMaxTicks : _bearMaxTicks;
                if (min.HasValue && max.HasValue && max.Value > min.Value)
                    t = (float)((liveTicks - min.Value) / (max.Value - min.Value));
            }
            else // Percentile (stub -> fallback Range)
            {
                decimal? min = bullish ? _bullMinTicks : _bearMinTicks;
                decimal? max = bullish ? _bullMaxTicks : _bearMaxTicks;
                if (min.HasValue && max.HasValue && max.Value > min.Value)
                    t = (float)((liveTicks - min.Value) / (max.Value - min.Value));
            }

            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;

            return InterpolateColor(
                bullish ? _liveWaveBullLowColor : _liveWaveBearLowColor,
                bullish ? _liveWaveBullHighColor : _liveWaveBearHighColor,
                t);
        }
        #endregion

        #region Export
        public void ExportImpulsesToXml(string path)
        {
            try
            {
                var root = new XElement("Impulses",
                    new XAttribute("SessionDate", _currentSessionDate?.ToString("yyyy-MM-dd") ?? ""),
                    new XAttribute("Generated", DateTime.UtcNow.ToString("o")),
                    _impulses.Select(i =>
                        new XElement("Impulse",
                            new XAttribute("StartBar", i.StartBar),
                            new XAttribute("EndBar", i.EndBar),
                            new XAttribute("Dir", i.Direction),
                            new XAttribute("Ticks", i.Ticks),
                            new XAttribute("Z", i.Z),
                            new XAttribute("Volume", i.Volume),
                            new XAttribute("Delta", i.Delta),
                            new XAttribute("Bars", i.Bars),
                            new XAttribute("DurationSec", i.Duration.TotalSeconds),
                            new XAttribute("EndTime", i.EndTime.ToString("o"))
                        )
                    )
                );
                var doc = new XDocument(root);
                doc.Save(path);
            }
            catch
            {
                // Silencioso: en entorno indicador no lanzar excepción
            }
        }
        #endregion

        #region Pivot Labels
        private void DrawPivotLabel(int pivotBar, decimal pivotPrice, bool isHigh)
        {
            var txt = BuildPivotLabelText();
            if (string.IsNullOrEmpty(txt))
                return;

            var id = "ZZ_PV_" + pivotBar;
            if (Labels.ContainsKey(id))
                return;

            var y = pivotPrice + (isHigh ? 1 : -1) * InstrumentInfo.TickSize * _verticalOffset;
            Color baseColor = ConvertColor(_textColor);
            if (_enableOutlierHighlight)
            {
                if (isHigh && _lastImpulseDir == 1 && Math.Abs(_lastImpulseZ) >= _outlierZThreshold)
                    baseColor = ConvertColor(_outlierBullColor);
                else if (!isHigh && _lastImpulseDir == -1 && Math.Abs(_lastImpulseZ) >= _outlierZThreshold)
                    baseColor = ConvertColor(_outlierBearColor);
            }

            AddText(id, txt, isHigh, pivotBar, y, baseColor,
                Color.Transparent, Color.Transparent, _textSize,
                DrawingText.TextAlign.Center);
        }

        private string BuildPivotLabelText()
        {
            var parts = new System.Text.StringBuilder();
            if (_showTicks) parts.AppendLine($"{_cumulativeTicks:0.#} T");
            if (_showDelta) parts.AppendLine($"{_cumulativeDelta:0.#}Δ");
            if (_showVolume) parts.AppendLine($"{_cumulativeVolume:0.#}");
            if (_showBars) parts.AppendLine($"{_cumulativeBars}B");
            if (_showTime != TimeFormat.None)
            {
                if (_showTime == TimeFormat.Days)
                    parts.AppendLine(_trendDuration.ToString(@"d\d\a\y\s"));
                else
                    parts.AppendLine(_trendDuration.Days > 0
                        ? _trendDuration.ToString(@"d\d\a\y\s\ hh\:mm\:ss")
                        : _trendDuration.ToString(@"hh\:mm\:ss"));
            }
            return parts.ToString().TrimEnd();
        }
        #endregion

        #region Heatmap legend / histogram
        private void RegisterHeatmapSegment(int fromBar, int toBar, decimal fromPrice, decimal toPrice, bool bullish)
        {
            if (!_enableHeatmap || fromBar >= toBar) return;
            var color = ComputeHeatmapColor(bullish);
            _heatmapSegments.Add(new HeatmapSegment
            {
                FromBar = fromBar,
                ToBar = toBar,
                FromPrice = fromPrice,
                ToPrice = toPrice,
                Color = color
            });
        }

        private Color ComputeHeatmapColor(bool bullish)
        {
            float t = 0f;
            if (_colorMode == ColorScaleMode.ZScore)
            {
                double zAbs = (double)Math.Abs(_lastImpulseZ);
                double zMax = (double)HeatmapZMax;
                if (zMax <= 0.0001) zMax = 1;
                t = (float)Math.Min(zAbs / zMax, 1.0);
            }
            else if (_colorMode == ColorScaleMode.Range)
            {
                if (bullish && _bullMaxTicks.HasValue && _bullMinTicks.HasValue && _bullMaxTicks > _bullMinTicks)
                    t = (float)((_lastImpulseTicks - _bullMinTicks.Value) / (_bullMaxTicks.Value - _bullMinTicks.Value));
                else if (!bullish && _bearMaxTicks.HasValue && _bearMinTicks.HasValue && _bearMaxTicks > _bearMinTicks)
                    t = (float)((_lastImpulseTicks - _bearMinTicks.Value) / (_bearMaxTicks.Value - _bearMinTicks.Value));
            }
            else // Percentile stub -> fallback range
            {
                if (bullish && _bullMaxTicks.HasValue && _bullMinTicks.HasValue && _bullMaxTicks > _bullMinTicks)
                    t = (float)((_lastImpulseTicks - _bullMinTicks.Value) / (_bullMaxTicks.Value - _bullMinTicks.Value));
                else if (!bullish && _bearMaxTicks.HasValue && _bearMinTicks.HasValue && _bearMaxTicks > _bearMinTicks)
                    t = (float)((_lastImpulseTicks - _bearMinTicks.Value) / (_bearMaxTicks.Value - _bearMinTicks.Value));
            }

            t = Math.Clamp(t, 0f, 1f);

            return InterpolateColor(
                bullish ? _heatmapBullLowColor : _heatmapBearLowColor,
                bullish ? _heatmapBullHighColor : _heatmapBearHighColor,
                t);
        }

        private Color InterpolateColor(CrossColor a, CrossColor b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            byte lerp(byte x, byte y) => (byte)(x + (y - x) * t);
            return Color.FromArgb(lerp(a.A, b.A), lerp(a.R, b.R), lerp(a.G, b.G), lerp(a.B, b.B));
        }

        private RenderPen GetHeatmapPen(Color c)
        {
            int key = HashCode.Combine(c.ToArgb(), _data.Width);
            if (!_heatmapPens.TryGetValue(key, out var pen))
            {
                pen = new RenderPen(c, _data.Width);
                _heatmapPens[key] = pen;
            }
            return pen;
        }

        private void DrawHeatmapLegend(RenderContext context)
        {
            try
            {
                int x = _heatmapLegendOffsetX;
                int y = _heatmapLegendOffsetY;
                int w = _heatmapLegendWidth;
                int h = _heatmapLegendHeight;
                if (w <= 10 || h <= 10) return;

                _legendRect = new Rectangle(x - 4, y - 4, w + 8, h + 8);
                context.FillRectangle(Color.FromArgb(140, 25, 25, 25), _legendRect);

                int half = h / 2 - 1;
                if (half < 2) half = h / 2;

                for (int i = 0; i < _heatmapLegendSteps; i++)
                {
                    float t = (float)i / (_heatmapLegendSteps - 1);
                    var cBull = InterpolateColor(_heatmapBullLowColor, _heatmapBullHighColor, t);
                    var cBear = InterpolateColor(_heatmapBearLowColor, _heatmapBearHighColor, t);
                    int sx = x + (int)(t * (w - 1));
                    context.DrawLine(GetHeatmapPen(cBull), sx, y, sx, y + half);
                    int bearTop = y + half + 2;
                    context.DrawLine(GetHeatmapPen(cBear), sx, bearTop, sx, bearTop + half);
                }

                var font = new RenderFont("Arial", Math.Max(8f, Math.Min(half - 2, 12f)));

                string leftLabel;
                string rightLabel;
                if (_colorMode == ColorScaleMode.ZScore)
                {
                    leftLabel = "0";
                    rightLabel = HeatmapZMax.ToString("0.##") + "Z";
                }
                else
                {
                    decimal? globalMin = null;
                    decimal? globalMax = null;
                    if (_bullMinTicks.HasValue) globalMin = globalMin.HasValue ? Math.Min(globalMin.Value, _bullMinTicks.Value) : _bullMinTicks.Value;
                    if (_bearMinTicks.HasValue) globalMin = globalMin.HasValue ? Math.Min(globalMin.Value, _bearMinTicks.Value) : _bearMinTicks.Value;
                    if (_bullMaxTicks.HasValue) globalMax = globalMax.HasValue ? Math.Max(globalMax.Value, _bullMaxTicks.Value) : _bullMaxTicks.Value;
                    if (_bearMaxTicks.HasValue) globalMax = globalMax.HasValue ? Math.Max(globalMax.Value, _bearMaxTicks.Value) : _bearMaxTicks.Value;
                    leftLabel = globalMin.HasValue ? globalMin.Value.ToString("0.#") : "Min";
                    rightLabel = globalMax.HasValue ? globalMax.Value.ToString("0.#") : "Max";
                }

                context.DrawString(leftLabel, font, Color.White, x, y - 2);
                context.DrawString(rightLabel, font, Color.White, x + w - (int)(rightLabel.Length * font.Size * 0.55f), y - 2);

                context.DrawString("Bull", font, Color.White, x + 2, y + 2 + half / 4);
                context.DrawString("Bear", font, Color.White, x + 2, y + half + 2 + 2 + half / 4);

                DrawLegendStats(context, x, y, w, half, font);

                if (_heatmapLegendShowHistogram && _histBuckets > 0)
                    DrawEmbeddedHistogram(context, x, y, w, half);
            }
            catch
            {
            }
        }

        private void DrawEmbeddedHistogram(RenderContext context, int x, int y, int w, int half)
        {
            if (_bullHist.Length != _histBuckets || _bearHist.Length != _histBuckets) return;
            if (_histBuckets < 2) return;

            int bullAreaTop = y + 2;
            int bullAreaHeight = half - 4;
            int bearAreaTop = y + half + 4;
            int bearAreaHeight = half - 6;

            int bullMax = 1;
            foreach (var v in _bullHist) if (v > bullMax) bullMax = v;
            int bearMax = 1;
            foreach (var v in _bearHist) if (v > bearMax) bearMax = v;

            int barW = Math.Max(1, w / _histBuckets);

            for (int i = 0; i < _histBuckets; i++)
            {
                float t = (float)i / (_histBuckets - 1);
                int bx = x + i * barW;

                int bv = _bullHist[i];
                int bh = bullMax > 0 ? (int)(bv / (float)bullMax * bullAreaHeight) : 0;
                if (bh > 0)
                {
                    var c = InterpolateColor(_heatmapBullLowColor, _heatmapBullHighColor, t);
                    context.FillRectangle(c, new Rectangle(bx, bullAreaTop + (bullAreaHeight - bh), barW - 1, bh));
                }

                int sv = _bearHist[i];
                int sh = bearMax > 0 ? (int)(sv / (float)bearMax * bearAreaHeight) : 0;
                if (sh > 0)
                {
                    var c2 = InterpolateColor(_heatmapBearLowColor, _heatmapBearHighColor, t);
                    context.FillRectangle(c2, new Rectangle(bx, bearAreaTop, barW - 1, sh));
                }
            }
        }

        private void DrawLegendStats(RenderContext context, int x, int y, int w, int half, RenderFont font)
        {
            if (!_heatmapLegendShowStats)
                return;

            decimal bullAvg = _bullCount > 0 ? _bullSumTicks / _bullCount : 0;
            decimal bullSd = ComputeStdev(_bullCount, _bullM2);
            string bullLine = $"B Min:{_bullMinTicks?.ToString("0.#") ?? "-"} Max:{_bullMaxTicks?.ToString("0.#") ?? "-"} Avg:{bullAvg:0.#} σ:{bullSd:0.#}";
            decimal bearAvg = _bearCount > 0 ? _bearSumTicks / _bearCount : 0;
            decimal bearSd = ComputeStdev(_bearCount, _bearM2);
            string bearLine = $"S Min:{_bearMinTicks?.ToString("0.#") ?? "-"} Max:{_bearMaxTicks?.ToString("0.#") ?? "-"} Avg:{bearAvg:0.#} σ:{bearSd:0.#}";

            int textY = y + half - ((int)font.Size) - 2;
            context.DrawString(bullLine, font, Color.White, x + 4, textY);
            context.DrawString(bearLine, font, Color.White, x + 4, y + half + 2 + 2);
        }
        #endregion

        #region Helpers
        private decimal GetHigh(int bar)
        {
            var c = GetCandle(bar);
            return _ignoreWicks ? Math.Max(c.Open, c.Close) : c.High;
        }

        private decimal GetLow(int bar)
        {
            var c = GetCandle(bar);
            return _ignoreWicks ? Math.Min(c.Open, c.Close) : c.Low;
        }

        private decimal RequiredChange(decimal refPrice) =>
            _calcMode switch
            {
                Mode.Relative => refPrice * _percentage / 100m,
                Mode.Absolute => _percentage,
                Mode.Ticks => _percentage * (HasValidTickSize ? InstrumentInfo.TickSize : 0m),
                _ => 0m
            };

        private void AggregateWave(int fromBar, int toBar, decimal startPrice, decimal endPrice)
        {
            _cumulativeVolume = 0;
            _cumulativeDelta = 0;
            _cumulativeTicks = 0;

            for (var i = fromBar; i <= toBar; i++)
            {
                var c = GetCandle(i);
                _cumulativeVolume += c.Volume;
                _cumulativeDelta += c.Delta;
                _cumulativeTicks += c.Ticks;
                _data[i] = Linear(startPrice, endPrice, toBar - fromBar + 1, i - fromBar);
            }

            _trendDuration = GetCandle(toBar).Time - GetCandle(fromBar).Time;
            _cumulativeTicks = HasValidTickSize
                ? Math.Abs((endPrice - startPrice) / InstrumentInfo.TickSize)
                : 0;
            _cumulativeBars = Math.Abs(toBar - fromBar) + 1;
        }

        private void DrawLastWave(int bar)
        {
            if (!HasValidTickSize) return;

            var candle = GetCandle(bar);
            _cumulativeVolume = 0;
            _cumulativeDelta = 0;

            if (_direction == 1)
            {
                for (var i = _lastLowBar; i <= bar; i++)
                {
                    var c = GetCandle(i);
                    _cumulativeVolume += c.Volume;
                    _cumulativeDelta += c.Delta;
                    _data[i] = Linear(GetLow(_lastLowBar), candle.Close, bar - _lastLowBar + 1, i - _lastLowBar);
                }
            }
            else if (_direction == -1)
            {
                for (var i = _lastHighBar; i <= bar; i++)
                {
                    var c = GetCandle(i);
                    _cumulativeVolume += c.Volume;
                    _cumulativeDelta += c.Delta;
                    _data[i] = Linear(GetHigh(_lastHighBar), candle.Close, bar - _lastHighBar + 1, i - _lastHighBar);
                }
            }
        }

        private decimal Linear(decimal start, decimal stop, int steps, int position) =>
            steps > 1 ? start + (stop - start) * position / (steps - 1) : start + (stop - start) * position / steps;

        private Color ConvertColor(CrossColor c) =>
            Color.FromArgb(c.A, c.R, c.G, c.B);

        private void RemoveTouchedLines(int currentBar)
        {
            for (var i = HorizontalLinesTillTouch.Count - 1; i >= 0; i--)
            {
                var line = HorizontalLinesTillTouch[i];
                if (line.SecondBar < currentBar)
                {
                    _hiddenTouchedLines.Add(line);
                    HorizontalLinesTillTouch.RemoveAt(i);
                }
            }
        }

        private void RestoreTouchedLines()
        {
            if (_hiddenTouchedLines.Count == 0)
                return;
            foreach (var l in _hiddenTouchedLines)
                HorizontalLinesTillTouch.Add(l);
            _hiddenTouchedLines.Clear();
        }

        private void RebuildHistArrays()
        {
            _bullHist = new int[_histBuckets];
            _bearHist = new int[_histBuckets];
        }

        private void UpdateHistogram(bool bullish, decimal ticks)
        {
            if (_histBuckets <= 0) return;

            decimal? min = bullish ? _bullMinTicks : _bearMinTicks;
            decimal? max = bullish ? _bullMaxTicks : _bearMaxTicks;
            if (!min.HasValue || !max.HasValue || max.Value <= min.Value)
                return;

            var range = max.Value - min.Value;
            var pos = (ticks - min.Value) / range;
            if (pos < 0) pos = 0;
            if (pos > 1) pos = 1;
            var idx = (int)Math.Floor(pos * (_histBuckets - 1));
            if (idx < 0) idx = 0;
            if (idx >= _histBuckets) idx = _histBuckets - 1;
            if (bullish) _bullHist[idx]++; else _bearHist[idx]++;
        }
        #endregion

        private void ClearHeatmapPens()
        {
            if (_heatmapPens.Count == 0)
                return;
            // RenderPen no es IDisposable: solo limpiar referencias
            _heatmapPens.Clear();
        }

        protected override void OnDispose()
        {
            base.OnDispose();
            _highPen?.Dispose();
            _lowPen?.Dispose();
            ClearHeatmapPens(); // solo limpia el diccionario
        }
    }
}