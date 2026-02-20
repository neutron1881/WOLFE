namespace ATAS.Indicators.Technical
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.ComponentModel.DataAnnotations;
    using System.Drawing;
    using System.Linq;
    using ATAS.Indicators.Drawing;
    using OFT.Rendering.Context;
    using OFT.Rendering.Settings;
    using OFT.Rendering.Tools;
    using CrossColor = System.Windows.Media.Color;
    using CrossColors = System.Windows.Media.Colors;

    [DisplayName("Wolfe Waves Pro")]
    [Category("Custom")]
    public class WolfeWaves : Indicator
    {
        #region Enums

        public enum WolfeType { Bullish = 1, Bearish = -1 }
        public enum WaveQuality { Fat, LongNeck }
        public enum CandleSignal { None, PinBar, Engulfing }

        #endregion

        #region Nested Types

        private sealed class Pivot
        {
            public int Bar { get; set; }
            public decimal Price { get; set; }
            public int Type { get; set; } // 1=high, -1=low
        }

        private sealed class WolfePattern
        {
            public WolfeType Direction { get; set; }
            public Pivot P1 { get; set; } = null!;
            public Pivot P2 { get; set; } = null!;
            public Pivot P3 { get; set; } = null!;
            public Pivot P4 { get; set; } = null!;
            public Pivot P5 { get; set; } = null!;

            // Proyecciones EPA
            public decimal EpaSlope { get; set; }
            public decimal EpaIntercept { get; set; }

            // Apex (ETA)
            public int ApexBar { get; set; }
            public decimal ApexPrice { get; set; }

            // Sweet Zone
            public decimal SweetUpperAtP5 { get; set; }
            public decimal SweetLowerAtP5 { get; set; }

            // Take Profits escalonados (Fib 2→3)
            public decimal Tp1Price { get; set; } // 23.6%
            public decimal Tp2Price { get; set; } // 61.8%
            public decimal Tp3Price { get; set; } // 100%
            public bool Tp1Reached { get; set; }
            public bool Tp2Reached { get; set; }
            public bool Tp3Reached { get; set; }

            // Clasificación de calidad
            public WaveQuality Quality { get; set; }
            public decimal QualityScore { get; set; } // 0-100

            // Stop & Exit
            public decimal StopPrice { get; set; }
            public decimal ExitPrice { get; set; }
            public decimal PnlTicks { get; set; }

            // Confirmaciones avanzadas
            public bool HasDivergence { get; set; }
            public bool EmaCrossConfirmed { get; set; }
            public CandleSignal EntrySignal { get; set; }

            // Baby wave (onda interna contraria)
            public bool BabyWaveDetected { get; set; }
            public decimal BabyWaveTarget { get; set; }
            public int BabyWaveBar { get; set; }

            // Post-P5 volume monitoring
            public bool DangerAlertFired { get; set; }
            public bool StrongAlertFired { get; set; }

            // Estado
            public bool Invalidated { get; set; }
            public bool TargetReached { get; set; }
            public bool StopHit { get; set; }
            public bool Resolved => Invalidated || TargetReached || StopHit;
            public bool SweetZoneAlertFired { get; set; }
            public bool ConfirmationAlertFired { get; set; }
            public string Id => $"{(int)Direction}_{P1.Bar}_{P5.Bar}";
        }

        #endregion

        #region Fields

        // ZigZag internals
        private int _direction;
        private int _lastHighBar, _lastLowBar, _lastBar = -1;
        private decimal _lastHigh, _lastLow;

        private readonly List<Pivot> _pivots = new();
        private readonly List<WolfePattern> _patterns = new();
        private readonly HashSet<string> _detectedIds = new();

        // Internal indicator buffers (per-bar)
        private readonly List<decimal> _ema50 = new();
        private readonly List<decimal> _atr = new();
        private readonly List<decimal> _macdLine = new();
        private readonly List<decimal> _macdSignal = new();
        private readonly List<decimal> _macdHist = new();
        private readonly List<decimal> _rsi = new();

        // EMA helpers
        private decimal _emaFast9, _emaSlow18, _emaSignal9, _ema50Val;
        // ATR helper
        private decimal _atrSmoothed;
        // RSI helpers
        private decimal _rsiAvgGain, _rsiAvgLoss;

        // Backing fields — ZigZag
        private int _zigZagDepth = 7;
        private int _zigZagDeviation = 5;

        // Backing fields — Fib filter
        private bool _enableFibFilter;
        private decimal _fibMinExtension = 1.272m, _fibMaxExtension = 1.618m;

        // Backing fields — Symmetry
        private bool _enableSymmetryFilter;
        private decimal _maxSymmetryRatio = 2.5m;

        // Backing fields — Volume
        private bool _enableVolumeFilter;
        private int _volumeLookback = 10;
        private decimal _volumeSpikeMultiplier = 1.5m;

        // Backing fields — EMA / Trend
        private int _emaPeriod = 50;
        private bool _enableEmaFilter = true;
        private CrossColor _emaColor = CrossColors.Yellow;
        private bool _showEmaLine = true;

        // Backing fields — ATR Stop
        private int _atrPeriod = 14;
        private decimal _atrMultiplier = 2.5m;

        // Backing fields — MACD Divergence
        private bool _enableDivergence = true;
        private int _macdFast = 9, _macdSlow = 18, _macdSmooth = 9;
        private int _rsiPeriod = 7;

        // Backing fields — Candle trigger
        private bool _enableCandleTrigger = true;

        // Backing fields — Post-P5 volume
        private bool _enablePostP5Volume = true;

        // Backing fields — Baby waves
        private bool _enableBabyWaves = true;
        private int _babyZigZagDepth = 3;

        // Backing fields — Visual
        private CrossColor _bullishLineColor = CrossColors.Lime;
        private CrossColor _bearishLineColor = CrossColors.OrangeRed;
        private CrossColor _epaLineColor = CrossColors.Gold;
        private CrossColor _sweetZoneColor = CrossColor.FromArgb(60, 0, 200, 255);
        private CrossColor _invalidatedColor = CrossColors.Red;
        private int _lineWidth = 2, _epaLineWidth = 2;
        private bool _showLabels = true;
        private int _labelFontSize = 12;
        private CrossColor _labelColor = CrossColors.White;
        private bool _showSweetZone = true, _showEpaLine = true, _showEtaApex = true;
        private bool _showFibP2P3 = true;
        private CrossColor _fibLineColor = CrossColors.DodgerBlue;
        private int _fibLineWidth = 1;
        private bool _showFibLabels = true;
        private int _fibLabelFontSize = 9;
        private bool _showStopLine = true;
        private CrossColor _stopLineColor = CrossColors.Red;
        private int _stopLineWidth = 2;
        private bool _enableAlerts = true;
        private string _alertFile = "alert1";
        private CrossColor _alertColor = CrossColors.Black;
        private int _maxPatterns = 10;
        private decimal _pointValue = 20m;

        // Backing fields — Stats Panel
        private bool _showStatsPanel = true;
        private CrossColor _statsBgColor = CrossColor.FromArgb(180, 20, 20, 30);
        private CrossColor _statsTextColor = CrossColors.White;
        private float _statsFontSize = 11f;
        private int _statsPanelX = 10, _statsPanelY = 10;

        // Quality classification threshold
        private decimal _fatSlopeThreshold = 0.3m;

        // Fibonacci TP levels
        private static readonly decimal[] FibLevels = { 0.236m, 0.382m, 0.500m, 0.618m, 0.786m, 1.000m, 1.272m, 1.618m };
        private static readonly string[] FibNames = { "23.6%", "38.2%", "50.0%", "61.8%", "78.6%", "100%", "127.2%", "161.8%" };

        #endregion

        #region Properties — ZigZag

        [Display(Name = "ZigZag Depth", GroupName = "1. ZigZag", Order = 10)]
        [Range(2, 50)]
        public int ZigZagDepth { get => _zigZagDepth; set { _zigZagDepth = value; RecalculateValues(); } }

        [Display(Name = "ZigZag Deviation (ticks)", GroupName = "1. ZigZag", Order = 20)]
        [Range(0, 500)]
        public int ZigZagDeviation { get => _zigZagDeviation; set { _zigZagDeviation = value; RecalculateValues(); } }

        #endregion

        #region Properties — Pattern Filters

        [Display(Name = "Enable Fib Filter", GroupName = "2. Pattern Filters", Order = 100)]
        public bool EnableFibFilter { get => _enableFibFilter; set { _enableFibFilter = value; RecalculateValues(); } }

        [Display(Name = "Fib Min Extension", GroupName = "2. Pattern Filters", Order = 110)]
        [Range(1.0, 3.0)]
        public decimal FibMinExtension { get => _fibMinExtension; set { _fibMinExtension = value; RecalculateValues(); } }

        [Display(Name = "Fib Max Extension", GroupName = "2. Pattern Filters", Order = 120)]
        [Range(1.0, 3.0)]
        public decimal FibMaxExtension { get => _fibMaxExtension; set { _fibMaxExtension = value; RecalculateValues(); } }

        [Display(Name = "Enable Symmetry Filter", GroupName = "2. Pattern Filters", Order = 130)]
        public bool EnableSymmetryFilter { get => _enableSymmetryFilter; set { _enableSymmetryFilter = value; RecalculateValues(); } }

        [Display(Name = "Max Symmetry Ratio", GroupName = "2. Pattern Filters", Order = 140)]
        [Range(1.0, 10.0)]
        public decimal MaxSymmetryRatio { get => _maxSymmetryRatio; set { _maxSymmetryRatio = value; RecalculateValues(); } }

        [Display(Name = "Enable Volume Filter", GroupName = "2. Pattern Filters", Order = 150)]
        public bool EnableVolumeFilter { get => _enableVolumeFilter; set { _enableVolumeFilter = value; RecalculateValues(); } }

        [Display(Name = "Volume Lookback", GroupName = "2. Pattern Filters", Order = 160)]
        [Range(3, 50)]
        public int VolumeLookback { get => _volumeLookback; set { _volumeLookback = value; RecalculateValues(); } }

        [Display(Name = "Volume Spike Multiplier", GroupName = "2. Pattern Filters", Order = 170)]
        [Range(1.0, 10.0)]
        public decimal VolumeSpikeMultiplier { get => _volumeSpikeMultiplier; set { _volumeSpikeMultiplier = value; RecalculateValues(); } }

        #endregion

        #region Properties — EMA Trend Filter

        [Display(Name = "Enable EMA Filter", GroupName = "3. EMA Trend", Order = 200)]
        [Description("Confirma entrada cuando precio cruza EMA a favor tras P5.")]
        public bool EnableEmaFilter { get => _enableEmaFilter; set { _enableEmaFilter = value; RecalculateValues(); } }

        [Display(Name = "EMA Period", GroupName = "3. EMA Trend", Order = 210)]
        [Range(5, 200)]
        public int EmaPeriod { get => _emaPeriod; set { _emaPeriod = value; RecalculateValues(); } }

        [Display(Name = "Show EMA Line", GroupName = "3. EMA Trend", Order = 220)]
        public bool ShowEmaLine { get => _showEmaLine; set { _showEmaLine = value; RecalculateValues(); } }

        [Display(Name = "EMA Color", GroupName = "3. EMA Trend", Order = 230)]
        public CrossColor EmaColor { get => _emaColor; set { _emaColor = value; RecalculateValues(); } }

        #endregion

        #region Properties — MACD/RSI Divergence

        [Display(Name = "Enable Divergence", GroupName = "4. Divergence", Order = 300)]
        [Description("Detecta divergencias MACD/RSI en P5.")]
        public bool EnableDivergence { get => _enableDivergence; set { _enableDivergence = value; RecalculateValues(); } }

        [Display(Name = "MACD Fast", GroupName = "4. Divergence", Order = 310)]
        [Range(2, 50)]
        public int MacdFast { get => _macdFast; set { _macdFast = value; RecalculateValues(); } }

        [Display(Name = "MACD Slow", GroupName = "4. Divergence", Order = 320)]
        [Range(5, 100)]
        public int MacdSlow { get => _macdSlow; set { _macdSlow = value; RecalculateValues(); } }

        [Display(Name = "MACD Signal", GroupName = "4. Divergence", Order = 330)]
        [Range(2, 50)]
        public int MacdSmooth { get => _macdSmooth; set { _macdSmooth = value; RecalculateValues(); } }

        [Display(Name = "RSI Period", GroupName = "4. Divergence", Order = 340)]
        [Range(2, 50)]
        public int RsiPeriod { get => _rsiPeriod; set { _rsiPeriod = value; RecalculateValues(); } }

        #endregion

        #region Properties — ATR Stop

        [Display(Name = "ATR Period", GroupName = "5. ATR Stop", Order = 400)]
        [Range(5, 50)]
        public int AtrPeriod { get => _atrPeriod; set { _atrPeriod = value; RecalculateValues(); } }

        [Display(Name = "ATR Multiplier", GroupName = "5. ATR Stop", Order = 410)]
        [Description("Stop = P5 ± ATR × multiplier.")]
        [Range(0.5, 10.0)]
        public decimal AtrMultiplier { get => _atrMultiplier; set { _atrMultiplier = value; RecalculateValues(); } }

        #endregion

        #region Properties — Candle Trigger

        [Display(Name = "Enable Candle Trigger", GroupName = "6. Candle Trigger", Order = 500)]
        [Description("Requiere Pin Bar o Engulfing en Sweet Zone para confirmar entrada.")]
        public bool EnableCandleTrigger { get => _enableCandleTrigger; set { _enableCandleTrigger = value; RecalculateValues(); } }

        #endregion

        #region Properties — Post-P5 Volume

        [Display(Name = "Enable Post-P5 Volume", GroupName = "7. Post-P5 Volume", Order = 550)]
        [Description("Monitorea volumen posterior para alertas de peligro/fuerza.")]
        public bool EnablePostP5Volume { get => _enablePostP5Volume; set { _enablePostP5Volume = value; RecalculateValues(); } }

        #endregion

        #region Properties — Baby Waves

        [Display(Name = "Enable Baby Waves", GroupName = "8. Baby Waves", Order = 560)]
        [Description("Detecta mini ondas contrarias dentro de una onda activa.")]
        public bool EnableBabyWaves { get => _enableBabyWaves; set { _enableBabyWaves = value; RecalculateValues(); } }

        [Display(Name = "Baby ZigZag Depth", GroupName = "8. Baby Waves", Order = 565)]
        [Range(2, 10)]
        public int BabyZigZagDepth { get => _babyZigZagDepth; set { _babyZigZagDepth = value; RecalculateValues(); } }

        #endregion

        #region Properties — Visual

        [Display(Name = "Bullish Color", GroupName = "9. Visual", Order = 600)]
        public CrossColor BullishLineColor { get => _bullishLineColor; set { _bullishLineColor = value; RecalculateValues(); } }

        [Display(Name = "Bearish Color", GroupName = "9. Visual", Order = 610)]
        public CrossColor BearishLineColor { get => _bearishLineColor; set { _bearishLineColor = value; RecalculateValues(); } }

        [Display(Name = "EPA/Target Color", GroupName = "9. Visual", Order = 620)]
        public CrossColor EpaLineColor { get => _epaLineColor; set { _epaLineColor = value; RecalculateValues(); } }

        [Display(Name = "Sweet Zone Color", GroupName = "9. Visual", Order = 630)]
        public CrossColor SweetZoneColor { get => _sweetZoneColor; set { _sweetZoneColor = value; RecalculateValues(); } }

        [Display(Name = "Invalidated Color", GroupName = "9. Visual", Order = 640)]
        public CrossColor InvalidatedColor { get => _invalidatedColor; set { _invalidatedColor = value; RecalculateValues(); } }

        [Display(Name = "Line Width", GroupName = "9. Visual", Order = 650)]
        [Range(1, 10)]
        public int LineWidth { get => _lineWidth; set { _lineWidth = value; RecalculateValues(); } }

        [Display(Name = "EPA Line Width", GroupName = "9. Visual", Order = 660)]
        [Range(1, 10)]
        public int EpaLineWidth { get => _epaLineWidth; set { _epaLineWidth = value; RecalculateValues(); } }

        [Display(Name = "Show Labels", GroupName = "9. Visual", Order = 670)]
        public bool ShowLabels { get => _showLabels; set { _showLabels = value; RecalculateValues(); } }

        [Display(Name = "Label Font Size", GroupName = "9. Visual", Order = 675)]
        [Range(6, 30)]
        public int LabelFontSize { get => _labelFontSize; set { _labelFontSize = value; RecalculateValues(); } }

        [Display(Name = "Label Color", GroupName = "9. Visual", Order = 680)]
        public CrossColor LabelColor { get => _labelColor; set { _labelColor = value; RecalculateValues(); } }

        [Display(Name = "Show Sweet Zone", GroupName = "9. Visual", Order = 685)]
        public bool ShowSweetZone { get => _showSweetZone; set { _showSweetZone = value; RecalculateValues(); } }

        [Display(Name = "Show EPA Line", GroupName = "9. Visual", Order = 690)]
        public bool ShowEpaLine { get => _showEpaLine; set { _showEpaLine = value; RecalculateValues(); } }

        [Display(Name = "Show ETA Apex", GroupName = "9. Visual", Order = 695)]
        public bool ShowEtaApex { get => _showEtaApex; set { _showEtaApex = value; RecalculateValues(); } }

        [Display(Name = "Show Fib P2→P3", GroupName = "9. Visual", Order = 700)]
        public bool ShowFibP2P3 { get => _showFibP2P3; set { _showFibP2P3 = value; RecalculateValues(); } }

        [Display(Name = "Fib Line Color", GroupName = "9. Visual", Order = 710)]
        public CrossColor FibLineColor { get => _fibLineColor; set { _fibLineColor = value; RecalculateValues(); } }

        [Display(Name = "Show Stop Line", GroupName = "9. Visual", Order = 730)]
        public bool ShowStopLine { get => _showStopLine; set { _showStopLine = value; RecalculateValues(); } }

        [Display(Name = "Stop Color", GroupName = "9. Visual", Order = 740)]
        public CrossColor StopLineColor { get => _stopLineColor; set { _stopLineColor = value; RecalculateValues(); } }

        #endregion

        #region Properties — Alerts

        [Display(Name = "Enable Alerts", GroupName = "10. Alerts", Order = 800)]
        public bool EnableAlerts { get => _enableAlerts; set { _enableAlerts = value; RecalculateValues(); } }

        [Display(Name = "Alert File", GroupName = "10. Alerts", Order = 810)]
        public string AlertFile { get => _alertFile; set { _alertFile = value; RecalculateValues(); } }

        [Display(Name = "Alert Color", GroupName = "10. Alerts", Order = 820)]
        public CrossColor AlertColor { get => _alertColor; set { _alertColor = value; RecalculateValues(); } }

        #endregion

        #region Properties — P&L / Stats

        [Display(Name = "Point Value ($)", GroupName = "11. P&L", Order = 850)]
        [Range(0.01, 10000)]
        public decimal PointValue { get => _pointValue; set { _pointValue = value; RecalculateValues(); } }

        [Display(Name = "Max Patterns", GroupName = "11. P&L", Order = 860)]
        [Range(1, 50)]
        public int MaxPatterns { get => _maxPatterns; set { _maxPatterns = value; RecalculateValues(); } }

        [Display(Name = "Show Stats Panel", GroupName = "11. P&L", Order = 870)]
        public bool ShowStatsPanel { get => _showStatsPanel; set { _showStatsPanel = value; RecalculateValues(); } }

        [Display(Name = "Stats X", GroupName = "11. P&L", Order = 880)]
        [Range(0, 500)]
        public int StatsPanelX { get => _statsPanelX; set { _statsPanelX = value; RecalculateValues(); } }

        [Display(Name = "Stats Y", GroupName = "11. P&L", Order = 890)]
        [Range(0, 500)]
        public int StatsPanelY { get => _statsPanelY; set { _statsPanelY = value; RecalculateValues(); } }

        [Display(Name = "Stats Font Size", GroupName = "11. P&L", Order = 895)]
        [Range(8, 24)]
        public float StatsFontSize { get => _statsFontSize; set { _statsFontSize = value; RecalculateValues(); } }

        #endregion

        #region Constructor

        public WolfeWaves() : base(true)
        {
            EnableCustomDrawing = true;
            DenyToChangePanel = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);
        }

        #endregion

        #region OnCalculate

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar == 0)
            {
                _direction = 0; _lastHighBar = 0; _lastLowBar = 0; _lastBar = -1;
                _lastHigh = 0; _lastLow = 0;
                _pivots.Clear(); _patterns.Clear(); _detectedIds.Clear();
                _ema50.Clear(); _atr.Clear();
                _macdLine.Clear(); _macdSignal.Clear(); _macdHist.Clear(); _rsi.Clear();
                _emaFast9 = 0; _emaSlow18 = 0; _emaSignal9 = 0; _ema50Val = 0;
                _atrSmoothed = 0; _rsiAvgGain = 0; _rsiAvgLoss = 0;
            }

            var candle = GetCandle(bar);
            decimal close = candle.Close;

            // === Calcular indicadores internos ===
            CalcEma50(bar, close);
            CalcAtr(bar);
            CalcMacd(bar, close);
            CalcRsi(bar, close);

            if (bar < ZigZagDepth * 2 + 1)
                return;

            ProcessZigZag(bar);

            if (_pivots.Count >= 5)
                DetectWolfePatterns(bar);

            ValidatePatterns(bar);
            MonitorPostP5(bar);

            if (EnableBabyWaves)
                DetectBabyWaves(bar);

            if (EnableAlerts)
                CheckAlerts(bar);
        }

        #endregion

        #region Internal Indicators (EMA, ATR, MACD, RSI)

        private void CalcEma50(int bar, decimal close)
        {
            if (bar == 0) { _ema50Val = close; }
            else
            {
                decimal k = 2m / (EmaPeriod + 1);
                _ema50Val = close * k + _ema50Val * (1 - k);
            }
            _ema50.Add(_ema50Val);
        }

        private void CalcAtr(int bar)
        {
            var c = GetCandle(bar);
            decimal tr;
            if (bar == 0)
            {
                tr = c.High - c.Low;
                _atrSmoothed = tr;
            }
            else
            {
                decimal prevClose = GetCandle(bar - 1).Close;
                tr = Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose)));
                decimal k = 2m / (AtrPeriod + 1);
                _atrSmoothed = tr * k + _atrSmoothed * (1 - k);
            }
            _atr.Add(_atrSmoothed);
        }

        private void CalcMacd(int bar, decimal close)
        {
            if (bar == 0) { _emaFast9 = close; _emaSlow18 = close; _emaSignal9 = 0; }
            else
            {
                decimal kf = 2m / (MacdFast + 1);
                decimal ks = 2m / (MacdSlow + 1);
                _emaFast9 = close * kf + _emaFast9 * (1 - kf);
                _emaSlow18 = close * ks + _emaSlow18 * (1 - ks);
            }
            decimal macd = _emaFast9 - _emaSlow18;
            _macdLine.Add(macd);

            if (bar == 0) { _emaSignal9 = macd; }
            else
            {
                decimal ksm = 2m / (MacdSmooth + 1);
                _emaSignal9 = macd * ksm + _emaSignal9 * (1 - ksm);
            }
            _macdSignal.Add(_emaSignal9);
            _macdHist.Add(macd - _emaSignal9);
        }

        private void CalcRsi(int bar, decimal close)
        {
            if (bar == 0) { _rsiAvgGain = 0; _rsiAvgLoss = 0; _rsi.Add(50); return; }

            decimal change = close - GetCandle(bar - 1).Close;
            decimal gain = change > 0 ? change : 0;
            decimal loss = change < 0 ? -change : 0;

            if (bar <= RsiPeriod)
            {
                _rsiAvgGain = (_rsiAvgGain * (bar - 1) + gain) / bar;
                _rsiAvgLoss = (_rsiAvgLoss * (bar - 1) + loss) / bar;
            }
            else
            {
                _rsiAvgGain = (_rsiAvgGain * (RsiPeriod - 1) + gain) / RsiPeriod;
                _rsiAvgLoss = (_rsiAvgLoss * (RsiPeriod - 1) + loss) / RsiPeriod;
            }

            decimal rs = _rsiAvgLoss > 0 ? _rsiAvgGain / _rsiAvgLoss : 100;
            _rsi.Add(100 - 100 / (1 + rs));
        }

        #endregion

        #region ZigZag

        private void ProcessZigZag(int bar)
        {
            int confirmBar = bar - ZigZagDepth;
            if (confirmBar < ZigZagDepth || confirmBar <= _lastBar) return;
            _lastBar = confirmBar;

            decimal high = GetCandle(confirmBar).High;
            decimal low = GetCandle(confirmBar).Low;
            decimal tickSize = InstrumentInfo?.TickSize ?? 0.01m;
            decimal devPrice = ZigZagDeviation * tickSize;

            if (IsPivotHigh(confirmBar, ZigZagDepth))
            {
                if (_direction != 1 || high > _lastHigh + devPrice)
                {
                    if (_direction == 1 && _pivots.Count > 0 && _pivots[^1].Type == 1)
                    { if (high > _pivots[^1].Price) { _pivots[^1].Price = high; _pivots[^1].Bar = confirmBar; } }
                    else _pivots.Add(new Pivot { Bar = confirmBar, Price = high, Type = 1 });
                    _direction = 1; _lastHigh = high; _lastHighBar = confirmBar;
                }
            }

            if (IsPivotLow(confirmBar, ZigZagDepth))
            {
                if (_direction != -1 || low < _lastLow - devPrice)
                {
                    if (_direction == -1 && _pivots.Count > 0 && _pivots[^1].Type == -1)
                    { if (low < _pivots[^1].Price) { _pivots[^1].Price = low; _pivots[^1].Bar = confirmBar; } }
                    else _pivots.Add(new Pivot { Bar = confirmBar, Price = low, Type = -1 });
                    _direction = -1; _lastLow = low; _lastLowBar = confirmBar;
                }
            }

            while (_pivots.Count > 200) _pivots.RemoveAt(0);
        }

        private bool IsPivotHigh(int bar, int depth)
        {
            decimal h = GetCandle(bar).High;
            for (int i = 1; i <= depth; i++)
            {
                if (bar - i < 0 || bar + i > CurrentBar) return false;
                if (GetCandle(bar - i).High >= h || GetCandle(bar + i).High >= h) return false;
            }
            return true;
        }

        private bool IsPivotLow(int bar, int depth)
        {
            decimal l = GetCandle(bar).Low;
            for (int i = 1; i <= depth; i++)
            {
                if (bar - i < 0 || bar + i > CurrentBar) return false;
                if (GetCandle(bar - i).Low <= l || GetCandle(bar + i).Low <= l) return false;
            }
            return true;
        }

        #endregion

        #region Wolfe Detection

        private void DetectWolfePatterns(int bar)
        {
            int scanCount = Math.Min(_pivots.Count, 30);
            int startIdx = _pivots.Count - scanCount;

            for (int i = startIdx; i <= _pivots.Count - 5; i++)
            {
                var p1 = _pivots[i]; var p2 = _pivots[i + 1]; var p3 = _pivots[i + 2];
                var p4 = _pivots[i + 3]; var p5 = _pivots[i + 4];

                if (p1.Type == p2.Type || p2.Type == p3.Type || p3.Type == p4.Type || p4.Type == p5.Type)
                    continue;

                WolfeType? dir = null;
                if (p1.Type == -1 && p5.Type == -1 && ValidateBullish(p1, p2, p3, p4, p5))
                    dir = WolfeType.Bullish;
                else if (p1.Type == 1 && p5.Type == 1 && ValidateBearish(p1, p2, p3, p4, p5))
                    dir = WolfeType.Bearish;

                if (dir.HasValue)
                {
                    var pattern = BuildPattern(dir.Value, p1, p2, p3, p4, p5);
                    if (pattern != null && !_detectedIds.Contains(pattern.Id))
                    {
                        _detectedIds.Add(pattern.Id);
                        _patterns.Add(pattern);
                        TrimPatterns();
                    }
                }
            }
        }

        private bool ValidateBullish(Pivot p1, Pivot p2, Pivot p3, Pivot p4, Pivot p5)
        {
            if (p2.Price <= p1.Price) return false;
            if (p3.Price >= p1.Price) return false;
            if (p4.Price <= p1.Price || p4.Price >= p2.Price) return false;
            if (p5.Price >= p3.Price) return false;
            if (!AreLinesConvergent(p1, p3, p2, p4)) return false;
            if (!PassesFibonacciFilter(p1, p2, p3, p4, p5)) return false;
            if (!PassesSymmetryFilter(p1, p2, p3, p4)) return false;
            if (!PassesVolumeFilter(p5)) return false;
            return true;
        }

        private bool ValidateBearish(Pivot p1, Pivot p2, Pivot p3, Pivot p4, Pivot p5)
        {
            if (p2.Price >= p1.Price) return false;
            if (p3.Price <= p1.Price) return false;
            if (p4.Price >= p1.Price || p4.Price <= p2.Price) return false;
            if (p5.Price <= p3.Price) return false;
            if (!AreLinesConvergent(p1, p3, p2, p4)) return false;
            if (!PassesFibonacciFilter(p1, p2, p3, p4, p5)) return false;
            if (!PassesSymmetryFilter(p1, p2, p3, p4)) return false;
            if (!PassesVolumeFilter(p5)) return false;
            return true;
        }

        #endregion

        #region Geometry

        private bool AreLinesConvergent(Pivot pA1, Pivot pA2, Pivot pB1, Pivot pB2)
        {
            decimal dxA = pA2.Bar - pA1.Bar; decimal dxB = pB2.Bar - pB1.Bar;
            if (dxA == 0 || dxB == 0) return false;
            decimal sA = (pA2.Price - pA1.Price) / dxA;
            decimal sB = (pB2.Price - pB1.Price) / dxB;
            if (sA == sB) return false;
            decimal iA = pA1.Price - sA * pA1.Bar;
            decimal iB = pB1.Price - sB * pB1.Bar;
            decimal xInt = (iB - iA) / (sA - sB);
            return xInt > Math.Max(pA2.Bar, pB2.Bar);
        }

        private (int bar, decimal price) CalcApex(Pivot p1, Pivot p3, Pivot p2, Pivot p4)
        {
            decimal dxA = p3.Bar - p1.Bar; decimal dxB = p4.Bar - p2.Bar;
            if (dxA == 0 || dxB == 0) return (0, 0);
            decimal sA = (p3.Price - p1.Price) / dxA;
            decimal sB = (p4.Price - p2.Price) / dxB;
            if (sA == sB) return (0, 0);
            decimal iA = p1.Price - sA * p1.Bar;
            decimal iB = p2.Price - sB * p2.Bar;
            decimal xBar = (iB - iA) / (sA - sB);
            return ((int)Math.Round(xBar), sA * xBar + iA);
        }

        private (decimal slope, decimal intercept) CalcEpaLine(Pivot p1, Pivot p4)
        {
            decimal dx = p4.Bar - p1.Bar;
            if (dx == 0) return (0, p1.Price);
            decimal s = (p4.Price - p1.Price) / dx;
            return (s, p1.Price - s * p1.Bar);
        }

        private (decimal upper, decimal lower) CalcSweetZone(Pivot p1, Pivot p2, Pivot p3, Pivot p4, Pivot p5)
        {
            decimal dx13 = p3.Bar - p1.Bar; decimal dx24 = p4.Bar - p2.Bar;
            if (dx13 == 0 || dx24 == 0) return (p5.Price, p5.Price);
            decimal s13 = (p3.Price - p1.Price) / dx13;
            decimal s24 = (p4.Price - p2.Price) / dx24;
            decimal l13 = p1.Price + s13 * (p5.Bar - p1.Bar);
            decimal par = p3.Price + s24 * (p5.Bar - p3.Bar);
            return (Math.Max(l13, par), Math.Min(l13, par));
        }

        #endregion

        #region Filters

        private bool PassesFibonacciFilter(Pivot p1, Pivot p2, Pivot p3, Pivot p4, Pivot p5)
        {
            if (!EnableFibFilter) return true;
            decimal w12 = Math.Abs(p2.Price - p1.Price);
            decimal w34 = Math.Abs(p4.Price - p3.Price);
            if (w12 == 0 || w34 == 0) return false;
            decimal e3 = Math.Abs(p3.Price - p2.Price) / w12;
            decimal e5 = Math.Abs(p5.Price - p4.Price) / w34;
            return e3 >= FibMinExtension && e3 <= FibMaxExtension && e5 >= FibMinExtension && e5 <= FibMaxExtension;
        }

        private bool PassesSymmetryFilter(Pivot p1, Pivot p2, Pivot p3, Pivot p4)
        {
            if (!EnableSymmetryFilter) return true;
            int b12 = Math.Abs(p2.Bar - p1.Bar); int b34 = Math.Abs(p4.Bar - p3.Bar);
            if (b12 == 0 || b34 == 0) return false;
            decimal r = b12 > b34 ? (decimal)b12 / b34 : (decimal)b34 / b12;
            return r <= MaxSymmetryRatio;
        }

        private bool PassesVolumeFilter(Pivot p5)
        {
            if (!EnableVolumeFilter) return true;
            if (p5.Bar < VolumeLookback + 1) return true;
            decimal total = 0;
            for (int i = 1; i <= VolumeLookback; i++) { int idx = p5.Bar - i; if (idx >= 0) total += GetCandle(idx).Volume; }
            decimal avg = total / VolumeLookback;
            return avg == 0 || GetCandle(p5.Bar).Volume >= avg * VolumeSpikeMultiplier;
        }

        #endregion

        #region Pattern Building

        private WolfePattern? BuildPattern(WolfeType dir, Pivot p1, Pivot p2, Pivot p3, Pivot p4, Pivot p5)
        {
            var (apexBar, apexPrice) = CalcApex(p1, p3, p2, p4);
            var (epaSlope, epaIntercept) = CalcEpaLine(p1, p4);
            var (sweetUp, sweetLo) = CalcSweetZone(p1, p2, p3, p4, p5);

            // ATR-based stop (Feature 5)
            decimal atrVal = p5.Bar < _atr.Count ? _atr[p5.Bar] : 0;
            decimal stopPrice = dir == WolfeType.Bullish
                ? p5.Price - AtrMultiplier * atrVal
                : p5.Price + AtrMultiplier * atrVal;

            // Take Profit escalonados desde Fib 2→3 (Feature 1)
            decimal fibRange = Math.Abs(p2.Price - p3.Price);
            decimal tp1, tp2, tp3;
            if (dir == WolfeType.Bullish)
            {
                // Retroceso de la caída P2→P3: subida desde P5
                tp1 = p5.Price + Math.Abs(fibRange) * 0.236m;
                tp2 = p5.Price + Math.Abs(fibRange) * 0.618m;
                tp3 = p5.Price + Math.Abs(fibRange) * 1.000m;
            }
            else
            {
                tp1 = p5.Price - Math.Abs(fibRange) * 0.236m;
                tp2 = p5.Price - Math.Abs(fibRange) * 0.618m;
                tp3 = p5.Price - Math.Abs(fibRange) * 1.000m;
            }

            // Clasificación de calidad (Feature 2)
            var quality = ClassifyWave(p1, p2, p3, p4, p5);

            // Divergencia MACD en P5 (Feature 4)
            bool hasDivergence = EnableDivergence && CheckDivergenceAtP5(p3, p5, dir);

            // Candle trigger en P5 (Feature 6)
            CandleSignal candleSig = EnableCandleTrigger ? DetectCandleSignal(p5.Bar, dir) : CandleSignal.None;

            return new WolfePattern
            {
                Direction = dir, P1 = p1, P2 = p2, P3 = p3, P4 = p4, P5 = p5,
                ApexBar = apexBar, ApexPrice = apexPrice,
                EpaSlope = epaSlope, EpaIntercept = epaIntercept,
                SweetUpperAtP5 = sweetUp, SweetLowerAtP5 = sweetLo,
                StopPrice = stopPrice,
                Tp1Price = tp1, Tp2Price = tp2, Tp3Price = tp3,
                Quality = quality.q, QualityScore = quality.score,
                HasDivergence = hasDivergence,
                EntrySignal = candleSig
            };
        }

        private (WaveQuality q, decimal score) ClassifyWave(Pivot p1, Pivot p2, Pivot p3, Pivot p4, Pivot p5)
        {
            // Medir inclinación normalizada de las líneas 1-3 y 2-4
            decimal tickSize = InstrumentInfo?.TickSize ?? 0.01m;
            if (tickSize == 0) tickSize = 0.01m;

            decimal dx13 = Math.Max(1, Math.Abs(p3.Bar - p1.Bar));
            decimal dx24 = Math.Max(1, Math.Abs(p4.Bar - p2.Bar));

            // Pendiente en ticks por barra
            decimal slope13 = Math.Abs(p3.Price - p1.Price) / tickSize / dx13;
            decimal slope24 = Math.Abs(p4.Price - p2.Price) / tickSize / dx24;

            decimal avgSlope = (slope13 + slope24) / 2;

            // Simetría temporal
            int bars12 = Math.Abs(p2.Bar - p1.Bar);
            int bars34 = Math.Abs(p4.Bar - p3.Bar);
            decimal symRatio = bars12 > 0 && bars34 > 0
                ? Math.Min((decimal)bars12 / bars34, (decimal)bars34 / bars12)
                : 0.5m;

            // Score: 100 = perfecto (plano y simétrico)
            decimal slopeScore = Math.Max(0, 100 - avgSlope * 100);
            decimal symScore = symRatio * 100;
            decimal score = Math.Round((slopeScore * 0.6m + symScore * 0.4m), 0);
            score = Math.Clamp(score, 0, 100);

            // Fat = canal lateral (M/W), LongNeck = inclinación pronunciada
            WaveQuality q = avgSlope < _fatSlopeThreshold ? WaveQuality.Fat : WaveQuality.LongNeck;
            return (q, score);
        }

        private bool CheckDivergenceAtP5(Pivot p3, Pivot p5, WolfeType dir)
        {
            if (p3.Bar >= _macdHist.Count || p5.Bar >= _macdHist.Count) return false;

            decimal macdP3 = _macdHist[p3.Bar];
            decimal macdP5 = _macdHist[p5.Bar];

            if (dir == WolfeType.Bullish)
            {
                // Precio: P5 < P3 (nuevo mínimo). Divergencia alcista si MACD P5 > MACD P3
                return p5.Price < p3.Price && macdP5 > macdP3;
            }
            else
            {
                // Precio: P5 > P3 (nuevo máximo). Divergencia bajista si MACD P5 < MACD P3
                return p5.Price > p3.Price && macdP5 < macdP3;
            }
        }

        private CandleSignal DetectCandleSignal(int bar, WolfeType dir)
        {
            if (bar < 1 || bar > CurrentBar) return CandleSignal.None;

            var c = GetCandle(bar);
            var prev = GetCandle(bar - 1);
            decimal range = c.High - c.Low;
            if (range == 0) return CandleSignal.None;

            decimal body = Math.Abs(c.Close - c.Open);
            decimal bodyRatio = body / range;

            if (dir == WolfeType.Bullish)
            {
                // Pin Bar alcista: cuerpo pequeño arriba, mecha larga abajo
                decimal lowerWick = Math.Min(c.Open, c.Close) - c.Low;
                if (bodyRatio < 0.35m && lowerWick / range > 0.55m)
                    return CandleSignal.PinBar;

                // Engulfing alcista: cuerpo actual envuelve al anterior, cierre > open
                if (c.Close > c.Open && c.Close > Math.Max(prev.Open, prev.Close) && c.Open < Math.Min(prev.Open, prev.Close))
                    return CandleSignal.Engulfing;
            }
            else
            {
                // Pin Bar bajista: cuerpo pequeño abajo, mecha larga arriba
                decimal upperWick = c.High - Math.Max(c.Open, c.Close);
                if (bodyRatio < 0.35m && upperWick / range > 0.55m)
                    return CandleSignal.PinBar;

                // Engulfing bajista
                if (c.Close < c.Open && c.Close < Math.Min(prev.Open, prev.Close) && c.Open > Math.Max(prev.Open, prev.Close))
                    return CandleSignal.Engulfing;
            }

            return CandleSignal.None;
        }

        private void TrimPatterns()
        {
            while (_patterns.Count > MaxPatterns)
            { _detectedIds.Remove(_patterns[0].Id); _patterns.RemoveAt(0); }
        }

        #endregion

        #region Validation & Monitoring

        private void ValidatePatterns(int bar)
        {
            foreach (var p in _patterns)
            {
                if (bar <= p.P5.Bar || p.Resolved) continue;

                var candle = GetCandle(bar);
                decimal tickSize = InstrumentInfo?.TickSize ?? 0.01m;
                decimal epaAtBar = p.EpaSlope * bar + p.EpaIntercept;

                // EMA cross confirmation (Feature 3)
                if (EnableEmaFilter && !p.EmaCrossConfirmed && bar < _ema50.Count)
                {
                    decimal ema = _ema50[bar];
                    if ((p.Direction == WolfeType.Bullish && candle.Close > ema) ||
                        (p.Direction == WolfeType.Bearish && candle.Close < ema))
                        p.EmaCrossConfirmed = true;
                }

                // Check TPs escalonados
                if (p.Direction == WolfeType.Bullish)
                {
                    if (!p.Tp1Reached && candle.High >= p.Tp1Price) p.Tp1Reached = true;
                    if (!p.Tp2Reached && candle.High >= p.Tp2Price) p.Tp2Reached = true;
                    if (!p.Tp3Reached && candle.High >= p.Tp3Price) p.Tp3Reached = true;

                    // Target final (EPA)
                    if (candle.High >= epaAtBar)
                    {
                        p.TargetReached = true;
                        p.ExitPrice = epaAtBar;
                        p.PnlTicks = tickSize > 0 ? (epaAtBar - p.P5.Price) / tickSize : 0;
                        continue;
                    }

                    // Stop hit
                    if (candle.Low <= p.StopPrice)
                    {
                        p.StopHit = true; p.Invalidated = true;
                        p.ExitPrice = p.StopPrice;
                        p.PnlTicks = tickSize > 0 ? (p.StopPrice - p.P5.Price) / tickSize : 0;
                    }
                }
                else
                {
                    if (!p.Tp1Reached && candle.Low <= p.Tp1Price) p.Tp1Reached = true;
                    if (!p.Tp2Reached && candle.Low <= p.Tp2Price) p.Tp2Reached = true;
                    if (!p.Tp3Reached && candle.Low <= p.Tp3Price) p.Tp3Reached = true;

                    if (candle.Low <= epaAtBar)
                    {
                        p.TargetReached = true;
                        p.ExitPrice = epaAtBar;
                        p.PnlTicks = tickSize > 0 ? (p.P5.Price - epaAtBar) / tickSize : 0;
                        continue;
                    }

                    if (candle.High >= p.StopPrice)
                    {
                        p.StopHit = true; p.Invalidated = true;
                        p.ExitPrice = p.StopPrice;
                        p.PnlTicks = tickSize > 0 ? (p.P5.Price - p.StopPrice) / tickSize : 0;
                    }
                }
            }
        }

        /// <summary>Feature 7: Post-P5 volume monitoring</summary>
        private void MonitorPostP5(int bar)
        {
            if (!EnablePostP5Volume || !EnableAlerts) return;

            foreach (var p in _patterns)
            {
                if (p.Resolved || bar <= p.P5.Bar) continue;

                var candle = GetCandle(bar);
                decimal p5Vol = GetCandle(p.P5.Bar).Volume;
                if (p5Vol == 0) continue;

                bool bigVolume = candle.Volume > p5Vol;
                if (!bigVolume) continue;

                bool against = (p.Direction == WolfeType.Bullish && candle.Close < candle.Open) ||
                               (p.Direction == WolfeType.Bearish && candle.Close > candle.Open);

                bool inFavor = (p.Direction == WolfeType.Bullish && candle.Close > candle.Open) ||
                               (p.Direction == WolfeType.Bearish && candle.Close < candle.Open);

                // Also check EMA cross against
                bool emaAgainst = false;
                if (EnableEmaFilter && bar < _ema50.Count)
                {
                    decimal ema = _ema50[bar];
                    emaAgainst = (p.Direction == WolfeType.Bullish && candle.Close < ema) ||
                                 (p.Direction == WolfeType.Bearish && candle.Close > ema);
                }

                if (against && (emaAgainst || !EnableEmaFilter) && !p.DangerAlertFired)
                {
                    string dir = p.Direction == WolfeType.Bullish ? "BULL" : "BEAR";
                    AddAlert(AlertFile, InstrumentInfo?.Instrument ?? "",
                        $"⚠ Wolfe {dir}: DANGER - Volumen alto en contra + EMA cruzada", AlertColor, AlertColor);
                    p.DangerAlertFired = true;
                }
                else if (inFavor && !p.StrongAlertFired)
                {
                    string dir = p.Direction == WolfeType.Bullish ? "BULL" : "BEAR";
                    AddAlert(AlertFile, InstrumentInfo?.Instrument ?? "",
                        $"💪 Wolfe {dir}: Posición FUERTE - Volumen alto a favor", AlertColor, AlertColor);
                    p.StrongAlertFired = true;
                }
            }
        }

        /// <summary>Feature 8: Baby waves — mini counter-waves within an active pattern</summary>
        private void DetectBabyWaves(int bar)
        {
            foreach (var p in _patterns)
            {
                if (p.Resolved || p.BabyWaveDetected || bar <= p.P5.Bar + BabyZigZagDepth * 5)
                    continue;

                // Buscar pivotes mini entre P5 y barra actual
                var miniPivots = new List<Pivot>();
                for (int b = p.P5.Bar; b <= Math.Min(bar, p.P5.Bar + 100); b++)
                {
                    if (IsPivotHigh(b, BabyZigZagDepth))
                        miniPivots.Add(new Pivot { Bar = b, Price = GetCandle(b).High, Type = 1 });
                    if (IsPivotLow(b, BabyZigZagDepth))
                        miniPivots.Add(new Pivot { Bar = b, Price = GetCandle(b).Low, Type = -1 });
                }

                miniPivots = miniPivots.OrderBy(x => x.Bar).ToList();

                // Need at least 5 alternating pivots in opposite direction
                WolfeType babyDir = p.Direction == WolfeType.Bullish ? WolfeType.Bearish : WolfeType.Bullish;
                int startType = babyDir == WolfeType.Bullish ? -1 : 1;

                for (int i = 0; i <= miniPivots.Count - 5; i++)
                {
                    if (miniPivots[i].Type != startType) continue;

                    var bp = new Pivot[5];
                    bp[0] = miniPivots[i];
                    int found = 1;
                    int lastType = bp[0].Type;
                    for (int j = i + 1; j < miniPivots.Count && found < 5; j++)
                    {
                        if (miniPivots[j].Type != lastType) { bp[found++] = miniPivots[j]; lastType = miniPivots[j].Type; }
                    }

                    if (found < 5) continue;

                    // Simple validation: check convergence
                    if (AreLinesConvergent(bp[0], bp[2], bp[1], bp[3]))
                    {
                        // Baby wave detected! Target = 61.8% of baby wave range
                        decimal babyRange = Math.Abs(bp[1].Price - bp[2].Price);
                        decimal target = babyDir == WolfeType.Bullish
                            ? bp[4].Price + babyRange * 0.618m
                            : bp[4].Price - babyRange * 0.618m;

                        p.BabyWaveDetected = true;
                        p.BabyWaveTarget = target;
                        p.BabyWaveBar = bp[4].Bar;

                        if (EnableAlerts)
                        {
                            string dir = p.Direction == WolfeType.Bullish ? "BULL" : "BEAR";
                            AddAlert(AlertFile, InstrumentInfo?.Instrument ?? "",
                                $"👶 Wolfe {dir}: Baby wave contraria detectada → pausa temporal", AlertColor, AlertColor);
                        }
                        break;
                    }
                }
            }
        }

        #endregion

        #region Alerts

        private void CheckAlerts(int bar)
        {
            foreach (var p in _patterns)
            {
                if (p.Invalidated) continue;
                var candle = GetCandle(bar);

                if (!p.SweetZoneAlertFired && bar >= p.P5.Bar)
                {
                    bool inSZ = candle.Close >= p.SweetLowerAtP5 && candle.Close <= p.SweetUpperAtP5;
                    if (inSZ)
                    {
                        string dir = p.Direction == WolfeType.Bullish ? "BULL" : "BEAR";
                        string qual = p.Quality == WaveQuality.Fat ? "Alta Prob" : "Media Prob";
                        string conf = "";
                        if (p.HasDivergence) conf += " +Div";
                        if (p.EntrySignal != CandleSignal.None) conf += $" +{p.EntrySignal}";
                        AddAlert(AlertFile, InstrumentInfo?.Instrument ?? "",
                            $"Wolfe {dir} [{qual}]: P5 en Sweet Zone{conf}", AlertColor, AlertColor);
                        p.SweetZoneAlertFired = true;
                    }
                }

                if (!p.ConfirmationAlertFired && bar > p.P5.Bar)
                {
                    decimal dx13 = p.P3.Bar - p.P1.Bar;
                    if (dx13 == 0) continue;
                    decimal s13 = (p.P3.Price - p.P1.Price) / dx13;
                    decimal l13 = p.P1.Price + s13 * (bar - p.P1.Bar);

                    bool confirmed = (p.Direction == WolfeType.Bullish && candle.Close > l13) ||
                                     (p.Direction == WolfeType.Bearish && candle.Close < l13);
                    if (confirmed)
                    {
                        string dir = p.Direction == WolfeType.Bullish ? "BULL" : "BEAR";
                        string emaStr = p.EmaCrossConfirmed ? " ✓EMA" : "";
                        AddAlert(AlertFile, InstrumentInfo?.Instrument ?? "",
                            $"Wolfe {dir}: Confirmación de entrada{emaStr}", AlertColor, AlertColor);
                        p.ConfirmationAlertFired = true;
                    }
                }
            }
        }

        #endregion

        #region Rendering

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo?.PriceChartContainer == null) return;
            var container = ChartInfo.PriceChartContainer;

            // Dibujar EMA 50 (Feature 3)
            if (ShowEmaLine && _ema50.Count > 1)
                DrawEmaLine(context, container);

            foreach (var pattern in _patterns)
                DrawPattern(context, pattern, container);

            if (ShowStatsPanel)
                DrawStatsPanel(context);
        }

        private void DrawEmaLine(RenderContext context, dynamic container)
        {
            var emaPen = new RenderPen(ConvertColor(EmaColor), 1);
            int from = Math.Max(FirstVisibleBarNumber, 1);
            int to = Math.Min(LastVisibleBarNumber, _ema50.Count - 1);

            for (int i = from; i <= to; i++)
            {
                int x1 = container.GetXByBar(i - 1, false);
                int y1 = container.GetYByPrice(_ema50[i - 1], false);
                int x2 = container.GetXByBar(i, false);
                int y2 = container.GetYByPrice(_ema50[i], false);
                context.DrawLine(emaPen, x1, y1, x2, y2);
            }
        }

        private void DrawPattern(RenderContext context, WolfePattern p, dynamic ct)
        {
            Color lineColor;
            if (p.Invalidated) lineColor = ConvertColor(InvalidatedColor);
            else if (p.TargetReached) lineColor = Color.FromArgb(200, 0, 255, 100);
            else lineColor = p.Direction == WolfeType.Bullish ? ConvertColor(BullishLineColor) : ConvertColor(BearishLineColor);

            var pen = new RenderPen(lineColor, LineWidth);
            var dashPen = new RenderPen(lineColor, 1);

            int x1 = ct.GetXByBar(p.P1.Bar, false), y1 = ct.GetYByPrice(p.P1.Price, false);
            int x2 = ct.GetXByBar(p.P2.Bar, false), y2 = ct.GetYByPrice(p.P2.Price, false);
            int x3 = ct.GetXByBar(p.P3.Bar, false), y3 = ct.GetYByPrice(p.P3.Price, false);
            int x4 = ct.GetXByBar(p.P4.Bar, false), y4 = ct.GetYByPrice(p.P4.Price, false);
            int x5 = ct.GetXByBar(p.P5.Bar, false), y5 = ct.GetYByPrice(p.P5.Price, false);

            // Wave segments
            context.DrawLine(pen, x1, y1, x2, y2);
            context.DrawLine(pen, x2, y2, x3, y3);
            context.DrawLine(pen, x3, y3, x4, y4);
            context.DrawLine(pen, x4, y4, x5, y5);

            // Convergence lines to Apex
            if (p.ApexBar > 0 && p.ApexBar < CurrentBar + 200)
            {
                int xA = ct.GetXByBar(Math.Min(p.ApexBar, CurrentBar), false);
                int yA = ct.GetYByPrice(p.ApexPrice, false);
                context.DrawLine(dashPen, x3, y3, xA, yA);
                context.DrawLine(dashPen, x4, y4, xA, yA);
                if (ShowEtaApex)
                    context.DrawString("ETA", new RenderFont("Arial", 9), lineColor, xA - 10, yA - 15);
            }

            // EPA Target line
            if (ShowEpaLine)
            {
                var epaPen = new RenderPen(ConvertColor(EpaLineColor), EpaLineWidth);
                int endBar = Math.Max(p.ApexBar > 0 ? p.ApexBar : p.P5.Bar + 40, p.P5.Bar + 20);
                endBar = Math.Min(endBar, CurrentBar + 100);
                decimal epaEnd = p.EpaSlope * endBar + p.EpaIntercept;
                decimal epaP5 = p.EpaSlope * p.P5.Bar + p.EpaIntercept;
                context.DrawLine(epaPen, x5, ct.GetYByPrice(epaP5, false),
                    ct.GetXByBar(Math.Min(endBar, CurrentBar), false), ct.GetYByPrice(epaEnd, false));
                context.DrawLine(dashPen, x1, y1, x4, y4);
                context.DrawString($"Target ({epaEnd:F2})", new RenderFont("Arial", 9),
                    ConvertColor(EpaLineColor), ct.GetXByBar(Math.Min(endBar, CurrentBar), false) + 5,
                    ct.GetYByPrice(epaEnd, false) - 5);
            }

            // TP lines (Feature 1)
            DrawTpLine(context, ct, p, p.Tp1Price, "TP1 23.6%", p.Tp1Reached);
            DrawTpLine(context, ct, p, p.Tp2Price, "TP2 61.8%", p.Tp2Reached);
            if (p.Quality == WaveQuality.Fat)
                DrawTpLine(context, ct, p, p.Tp3Price, "TP3 100%", p.Tp3Reached);

            // Stop line
            if (ShowStopLine)
            {
                var stopPen = new RenderPen(ConvertColor(StopLineColor), _stopLineWidth);
                int yStop = ct.GetYByPrice(p.StopPrice, false);
                int xSE = ct.GetXByBar(Math.Min(p.P5.Bar + 30, CurrentBar), false);
                context.DrawLine(stopPen, x5, yStop, xSE, yStop);
                context.DrawString($"Stop ATR ({p.StopPrice:F2})", new RenderFont("Arial", 9),
                    ConvertColor(StopLineColor), xSE + 4, yStop - 7);
            }

            // Sweet Zone
            if (ShowSweetZone) DrawSweetZone(context, p);

            // Fib P2→P3
            if (ShowFibP2P3) DrawFibP2P3(context, p, ct);

            // Labels with quality, divergence, candle signal info
            if (ShowLabels)
            {
                var lf = new RenderFont("Arial", LabelFontSize, FontStyle.Bold);
                Color lc = ConvertColor(LabelColor);
                int off = 15;
                DrawLbl(context, "1", x1, y1, p.P1.Type, lf, lc, off);
                DrawLbl(context, "2", x2, y2, p.P2.Type, lf, lc, off);
                DrawLbl(context, "3", x3, y3, p.P3.Type, lf, lc, off);
                DrawLbl(context, "4", x4, y4, p.P4.Type, lf, lc, off);

                // P5 label with extra info
                string p5Label = "5";
                if (p.HasDivergence) p5Label += " ⚡Div";
                if (p.EntrySignal == CandleSignal.PinBar) p5Label += " 📌Pin";
                else if (p.EntrySignal == CandleSignal.Engulfing) p5Label += " 🔄Eng";
                DrawLbl(context, p5Label, x5, y5, p.P5.Type, lf, lc, off);

                // Quality label near P3
                string qLabel = p.Quality == WaveQuality.Fat
                    ? $"★ Alta Prob ({p.QualityScore}%)"
                    : $"▲ Media Prob ({p.QualityScore}%)";
                Color qColor = p.Quality == WaveQuality.Fat ? Color.FromArgb(255, 0, 255, 120) : Color.FromArgb(255, 255, 180, 0);
                context.DrawString(qLabel, new RenderFont("Arial", 9, FontStyle.Bold), qColor,
                    (x2 + x4) / 2, (y2 + y4) / 2 - 20);

                // EMA cross mark
                if (p.EmaCrossConfirmed)
                    context.DrawString("✓EMA", new RenderFont("Arial", 8), ConvertColor(EmaColor), x5 + 20, y5);

                // Baby wave mark
                if (p.BabyWaveDetected)
                {
                    int xB = ct.GetXByBar(Math.Min(p.BabyWaveBar, CurrentBar), false);
                    int yB = ct.GetYByPrice(p.BabyWaveTarget, false);
                    context.DrawString($"👶 Baby TP ({p.BabyWaveTarget:F2})", new RenderFont("Arial", 8),
                        Color.FromArgb(255, 180, 130, 255), xB, yB - 12);
                }
            }
        }

        private void DrawTpLine(RenderContext ctx, dynamic ct, WolfePattern p, decimal price, string label, bool reached)
        {
            Color c = reached ? Color.FromArgb(150, 0, 200, 80) : Color.FromArgb(180, 0, 180, 255);
            var tpPen = new RenderPen(c, 1);
            int y = ct.GetYByPrice(price, false);
            int xs = ct.GetXByBar(p.P5.Bar, false);
            int xe = ct.GetXByBar(Math.Min(p.P5.Bar + 25, CurrentBar), false);
            ctx.DrawLine(tpPen, xs, y, xe, y);
            string txt = reached ? $"✓ {label} ({price:F2})" : $"{label} ({price:F2})";
            ctx.DrawString(txt, new RenderFont("Arial", 8), c, xe + 4, y - 7);
        }

        private void DrawSweetZone(RenderContext context, WolfePattern p)
        {
            var pc = ChartInfo?.PriceChartContainer;
            if (pc == null) return;
            decimal dx13 = p.P3.Bar - p.P1.Bar; decimal dx24 = p.P4.Bar - p.P2.Bar;
            if (dx13 == 0 || dx24 == 0) return;
            decimal s13 = (p.P3.Price - p.P1.Price) / dx13;
            decimal s24 = (p.P4.Price - p.P2.Price) / dx24;
            int sB = Math.Max(p.P3.Bar, p.P5.Bar - 5);
            int eB = Math.Min(p.P5.Bar + 10, CurrentBar);
            if (eB <= sB) return;
            var up = new List<Point>(); var lo = new List<Point>();
            for (int b = sB; b <= eB; b++)
            {
                decimal l = p.P1.Price + s13 * (b - p.P1.Bar);
                decimal r = p.P3.Price + s24 * (b - p.P3.Bar);
                int x = pc.GetXByBar(b, false);
                up.Add(new Point(x, pc.GetYByPrice(Math.Max(l, r), false)));
                lo.Add(new Point(x, pc.GetYByPrice(Math.Min(l, r), false)));
            }
            lo.Reverse(); var poly = new List<Point>(); poly.AddRange(up); poly.AddRange(lo);
            if (poly.Count >= 3) context.FillPolygon(ConvertColor(SweetZoneColor), poly.ToArray());
        }

        private void DrawFibP2P3(RenderContext ctx, WolfePattern p, dynamic ct)
        {
            decimal range = p.P3.Price - p.P2.Price;
            if (range == 0) return;
            var fp = new RenderPen(ConvertColor(FibLineColor), _fibLineWidth);
            var ff = new RenderFont("Arial", _fibLabelFontSize);
            Color fc = ConvertColor(FibLineColor);
            int xs = ct.GetXByBar(p.P3.Bar, false);
            int xe = ct.GetXByBar(Math.Min(p.P5.Bar + 20, CurrentBar), false);
            for (int i = 0; i < FibLevels.Length; i++)
            {
                decimal price = p.P2.Price + range * FibLevels[i];
                int y = ct.GetYByPrice(price, false);
                ctx.DrawLine(fp, xs, y, xe, y);
                ctx.DrawString($"{FibNames[i]} ({price:F2})", ff, fc, xe + 4, y - 7);
            }
        }

        private void DrawLbl(RenderContext ctx, string t, int x, int y, int type, RenderFont f, Color c, int off)
        {
            ctx.DrawString(t, f, c, x - 5, y + (type == 1 ? -off : off));
        }

        #endregion

        #region Stats Panel

        private void DrawStatsPanel(RenderContext context)
        {
            int total = _patterns.Count;
            int bullTgt = 0, bullStop = 0, bullAct = 0;
            int bearTgt = 0, bearStop = 0, bearAct = 0;
            int fatCount = 0, neckCount = 0, divCount = 0, candleCount = 0;
            decimal wonTk = 0, lostTk = 0;
            decimal tickSize = InstrumentInfo?.TickSize ?? 0.01m;
            decimal tickVal = PointValue * tickSize;

            foreach (var p in _patterns)
            {
                if (p.Quality == WaveQuality.Fat) fatCount++; else neckCount++;
                if (p.HasDivergence) divCount++;
                if (p.EntrySignal != CandleSignal.None) candleCount++;

                bool bull = p.Direction == WolfeType.Bullish;
                if (p.TargetReached) { if (bull) bullTgt++; else bearTgt++; wonTk += p.PnlTicks; }
                else if (p.StopHit) { if (bull) bullStop++; else bearStop++; lostTk += p.PnlTicks; }
                else if (p.Invalidated) { if (bull) bullStop++; else bearStop++; }
                else { if (bull) bullAct++; else bearAct++; }
            }

            int tgtTot = bullTgt + bearTgt, stpTot = bullStop + bearStop, actTot = bullAct + bearAct;
            int resolved = tgtTot + stpTot;
            decimal wr = resolved > 0 ? Math.Round((decimal)tgtTot / resolved * 100, 1) : 0;
            decimal netTk = wonTk + lostTk;
            decimal wonD = wonTk * tickVal, lostD = lostTk * tickVal, netD = netTk * tickVal;

            var rows = new List<(string t, int c)>
            {
                ("══ WOLFE WAVES PRO ══", 0),
                ($"Total: {total}  Active: {actTot}  Fat: {fatCount}  Neck: {neckCount}", 1),
                ($"Divergencias: {divCount}  Candle Signals: {candleCount}", 1),
                ("", 1),
                ($"✓ Target: {tgtTot}  (B:{bullTgt} S:{bearTgt})", 2),
                ($"✗ Stop:   {stpTot}  (B:{bullStop} S:{bearStop})", 3),
                ($"Win Rate: {wr}%", 4),
                ("", 1),
                ("── P&L ──", 0),
                ($"Won:  +{wonTk:F1}tk (${wonD:F2})", 2),
                ($"Lost: {lostTk:F1}tk (${lostD:F2})", 3),
                ($"NET:  {netTk:F1}tk (${netD:F2})", netTk >= 0 ? 2 : 3),
            };

            var font = new RenderFont("Arial", StatsFontSize);
            var bold = new RenderFont("Arial", StatsFontSize, FontStyle.Bold);
            int lh = (int)(StatsFontSize + 5);
            int mw = 0;
            foreach (var (t, _) in rows) { int w = (int)(t.Length * StatsFontSize * 0.58f); if (w > mw) mw = w; }
            mw = Math.Max(mw, 280);
            int pad = 8, x = StatsPanelX, y = StatsPanelY;
            var bg = new Rectangle(x, y, mw + pad * 2, rows.Count * lh + pad * 2);
            context.FillRectangle(ConvertColor(_statsBgColor), bg);
            context.DrawRectangle(new RenderPen(Color.FromArgb(100, 255, 255, 255), 1), bg);

            Color[] colors = {
                ConvertColor(CrossColors.Gold), ConvertColor(_statsTextColor),
                ConvertColor(CrossColors.Lime), ConvertColor(CrossColors.OrangeRed),
                wr >= 50 ? ConvertColor(CrossColors.Lime) : ConvertColor(CrossColors.OrangeRed)
            };

            int tx = x + pad, ty = y + pad;
            foreach (var (t, ci) in rows)
            {
                if (t.Length > 0)
                {
                    bool b = ci == 0 || t.StartsWith("Win") || t.StartsWith("NET");
                    context.DrawString(t, b ? bold : font, colors[Math.Min(ci, colors.Length - 1)], tx, ty);
                }
                ty += lh;
            }
        }

        #endregion

        #region Helpers

        private static Color ConvertColor(CrossColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

        #endregion
    }
}
