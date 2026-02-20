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

    /// <summary>
    /// Indicador avanzado de Ondas de Wolfe (Wolfe Waves) para ATAS.
    /// Detecta y dibuja automáticamente patrones de Wolfe Waves alcistas y bajistas
    /// utilizando un ZigZag interno para la detección de pivotes.
    /// </summary>
    [DisplayName("Wolfe Waves")]
    [Category("Custom")]
    public class WolfeWaves : Indicator
    {
        #region Enums

        public enum WolfeType
        {
            Bullish = 1,
            Bearish = -1
        }

        #endregion

        #region Nested Types

        /// <summary>
        /// Representa un pivote detectado por el ZigZag.
        /// </summary>
        private sealed class Pivot
        {
            public int Bar { get; set; }
            public decimal Price { get; set; }
            /// <summary>1 = máximo, -1 = mínimo</summary>
            public int Type { get; set; }
        }

        /// <summary>
        /// Representa un patrón completo de Wolfe Wave validado.
        /// </summary>
        private sealed class WolfePattern
        {
            public WolfeType Direction { get; set; }
            public Pivot P1 { get; set; } = null!;
            public Pivot P2 { get; set; } = null!;
            public Pivot P3 { get; set; } = null!;
            public Pivot P4 { get; set; } = null!;
            public Pivot P5 { get; set; } = null!;

            // Proyecciones
            public decimal EpaSlope { get; set; }
            public decimal EpaIntercept { get; set; }

            // Apex (ETA)
            public int ApexBar { get; set; }
            public decimal ApexPrice { get; set; }

            // Sweet Zone bounds (línea 1-3 extendida y paralela a 2-4 desde P3)
            public decimal SweetUpperAtP5 { get; set; }
            public decimal SweetLowerAtP5 { get; set; }

            // Stop & Exit
            /// <summary>Precio del Stop Loss (nivel de P5 + offset configurable).</summary>
            public decimal StopPrice { get; set; }
            /// <summary>Precio al que el patrón se resolvió (target o stop).</summary>
            public decimal ExitPrice { get; set; }
            /// <summary>P&L en ticks: positivo = ganancia, negativo = pérdida.</summary>
            public decimal PnlTicks { get; set; }

            // Estado
            public bool Invalidated { get; set; }
            public bool TargetReached { get; set; }
            public bool StopHit { get; set; }
            public bool Resolved => Invalidated || TargetReached || StopHit;
            public bool SweetZoneAlertFired { get; set; }
            public bool ConfirmationAlertFired { get; set; }
            /// <summary>Unique ID for dedup</summary>
            public string Id => $"{(int)Direction}_{P1.Bar}_{P5.Bar}";
        }

        #endregion

        #region Fields

        // ZigZag internals
        private int _direction;
        private int _lastHighBar;
        private int _lastLowBar;
        private int _lastBar = -1;
        private decimal _lastHigh;
        private decimal _lastLow;

        private readonly List<Pivot> _pivots = new();
        private readonly List<WolfePattern> _patterns = new();
        private readonly HashSet<string> _detectedIds = new();

        // Backing fields for properties
        private int _zigZagDepth = 7;
        private int _zigZagDeviation = 5;
        private bool _enableFibFilter;
        private decimal _fibMinExtension = 1.272m;
        private decimal _fibMaxExtension = 1.618m;
        private bool _enableSymmetryFilter;
        private decimal _maxSymmetryRatio = 2.5m;
        private bool _enableVolumeFilter;
        private int _volumeLookback = 10;
        private decimal _volumeSpikeMultiplier = 1.5m;
        private CrossColor _bullishLineColor = CrossColors.Lime;
        private CrossColor _bearishLineColor = CrossColors.OrangeRed;
        private CrossColor _epaLineColor = CrossColors.Gold;
        private CrossColor _sweetZoneColor = CrossColor.FromArgb(60, 0, 200, 255);
        private CrossColor _invalidatedColor = CrossColors.Red;
        private int _lineWidth = 2;
        private int _epaLineWidth = 2;
        private bool _showLabels = true;
        private int _labelFontSize = 12;
        private CrossColor _labelColor = CrossColors.White;
        private bool _showSweetZone = true;
        private bool _showEpaLine = true;
        private bool _showEtaApex = true;
        private bool _enableAlerts = true;
        private string _alertFile = "alert1";
        private CrossColor _alertColor = CrossColors.Black;
        private int _maxPatterns = 10;

        // Fibonacci P2→P3 display
        private bool _showFibP2P3 = true;
        private CrossColor _fibLineColor = CrossColors.DodgerBlue;
        private int _fibLineWidth = 1;
        private bool _showFibLabels = true;
        private int _fibLabelFontSize = 9;
        private bool _showFib236 = true;
        private bool _showFib382 = true;
        private bool _showFib500 = true;
        private bool _showFib618 = true;
        private bool _showFib786 = true;
        private bool _showFib1000 = true;
        private bool _showFib1272 = true;
        private bool _showFib1618 = true;

        // Niveles Fibonacci predefinidos
        private static readonly decimal[] FibLevels = { 0.236m, 0.382m, 0.500m, 0.618m, 0.786m, 1.000m, 1.272m, 1.618m };
        private static readonly string[] FibNames = { "23.6%", "38.2%", "50.0%", "61.8%", "78.6%", "100%", "127.2%", "161.8%" };

        // Stop Loss
        private decimal _stopOffsetTicks = 5m;
        private bool _showStopLine = true;
        private CrossColor _stopLineColor = CrossColors.Red;
        private int _stopLineWidth = 2;

        // Point Value (valor del punto del activo para P&L en $)
        private decimal _pointValue = 20m; // NQ default = $20/pt

        // HUD Stats Panel
        private bool _showStatsPanel = true;
        private CrossColor _statsBgColor = CrossColor.FromArgb(180, 20, 20, 30);
        private CrossColor _statsTextColor = CrossColors.White;
        private float _statsFontSize = 11f;
        private int _statsPanelX = 10;
        private int _statsPanelY = 10;

        #endregion

        #region Properties — ZigZag

        [Display(Name = "ZigZag Depth", GroupName = "ZigZag", Order = 10)]
        [Description("Número de barras a cada lado para confirmar un pivote.")]
        [Range(2, 50)]
        public int ZigZagDepth { get => _zigZagDepth; set { _zigZagDepth = value; RecalculateValues(); } }

        [Display(Name = "ZigZag Deviation (ticks)", GroupName = "ZigZag", Order = 20)]
        [Description("Desviación mínima en ticks para registrar un nuevo pivote.")]
        [Range(0, 500)]
        public int ZigZagDeviation { get => _zigZagDeviation; set { _zigZagDeviation = value; RecalculateValues(); } }

        #endregion

        #region Properties — Fibonacci Filters

        [Display(Name = "Enable Fibonacci Filter", GroupName = "Fibonacci", Order = 100)]
        [Description("Filtrar patrones por extensiones de Fibonacci.")]
        public bool EnableFibFilter { get => _enableFibFilter; set { _enableFibFilter = value; RecalculateValues(); } }

        [Display(Name = "Fib Min Extension", GroupName = "Fibonacci", Order = 110)]
        [Description("Extensión mínima de Fibonacci permitida (ej. 1.272).")]
        [Range(1.0, 3.0)]
        public decimal FibMinExtension { get => _fibMinExtension; set { _fibMinExtension = value; RecalculateValues(); } }

        [Display(Name = "Fib Max Extension", GroupName = "Fibonacci", Order = 120)]
        [Description("Extensión máxima de Fibonacci permitida (ej. 1.618).")]
        [Range(1.0, 3.0)]
        public decimal FibMaxExtension { get => _fibMaxExtension; set { _fibMaxExtension = value; RecalculateValues(); } }

        #endregion

        #region Properties — Symmetry Filter

        [Display(Name = "Enable Symmetry Filter", GroupName = "Symmetry", Order = 130)]
        [Description("Filtrar por simetría temporal entre ondas 1-2 y 3-4.")]
        public bool EnableSymmetryFilter { get => _enableSymmetryFilter; set { _enableSymmetryFilter = value; RecalculateValues(); } }

        [Display(Name = "Max Symmetry Ratio", GroupName = "Symmetry", Order = 140)]
        [Description("Ratio máximo de asimetría temporal permitido (ej. 2.0 = onda 3-4 puede ser hasta 2x la onda 1-2).")]
        [Range(1.0, 10.0)]
        public decimal MaxSymmetryRatio { get => _maxSymmetryRatio; set { _maxSymmetryRatio = value; RecalculateValues(); } }

        #endregion

        #region Properties — Volume Filter

        [Display(Name = "Enable Volume Filter", GroupName = "Volume", Order = 150)]
        [Description("Filtrar patrones por comportamiento de volumen (secado + spike).")]
        public bool EnableVolumeFilter { get => _enableVolumeFilter; set { _enableVolumeFilter = value; RecalculateValues(); } }

        [Display(Name = "Volume Lookback", GroupName = "Volume", Order = 160)]
        [Description("Barras previas al P5 para medir el secado de volumen.")]
        [Range(3, 50)]
        public int VolumeLookback { get => _volumeLookback; set { _volumeLookback = value; RecalculateValues(); } }

        [Display(Name = "Volume Spike Multiplier", GroupName = "Volume", Order = 170)]
        [Description("Multiplicador sobre el volumen promedio para considerar spike.")]
        [Range(1.0, 10.0)]
        public decimal VolumeSpikeMultiplier { get => _volumeSpikeMultiplier; set { _volumeSpikeMultiplier = value; RecalculateValues(); } }

        #endregion

        #region Properties — Visual: Lines

        [Display(Name = "Bullish Line Color", GroupName = "Lines", Order = 200)]
        public CrossColor BullishLineColor { get => _bullishLineColor; set { _bullishLineColor = value; RecalculateValues(); } }

        [Display(Name = "Bearish Line Color", GroupName = "Lines", Order = 210)]
        public CrossColor BearishLineColor { get => _bearishLineColor; set { _bearishLineColor = value; RecalculateValues(); } }

        [Display(Name = "EPA Line Color", GroupName = "Lines", Order = 220)]
        public CrossColor EpaLineColor { get => _epaLineColor; set { _epaLineColor = value; RecalculateValues(); } }

        [Display(Name = "Sweet Zone Color", GroupName = "Lines", Order = 230)]
        public CrossColor SweetZoneColor { get => _sweetZoneColor; set { _sweetZoneColor = value; RecalculateValues(); } }

        [Display(Name = "Invalidated Color", GroupName = "Lines", Order = 240)]
        public CrossColor InvalidatedColor { get => _invalidatedColor; set { _invalidatedColor = value; RecalculateValues(); } }

        [Display(Name = "Line Width", GroupName = "Lines", Order = 250)]
        [Range(1, 10)]
        public int LineWidth { get => _lineWidth; set { _lineWidth = value; RecalculateValues(); } }

        [Display(Name = "EPA Line Width", GroupName = "Lines", Order = 260)]
        [Range(1, 10)]
        public int EpaLineWidth { get => _epaLineWidth; set { _epaLineWidth = value; RecalculateValues(); } }

        #endregion

        #region Properties — Visual: Labels

        [Display(Name = "Show Labels", GroupName = "Labels", Order = 300)]
        public bool ShowLabels { get => _showLabels; set { _showLabels = value; RecalculateValues(); } }

        [Display(Name = "Label Font Size", GroupName = "Labels", Order = 310)]
        [Range(6, 30)]
        public int LabelFontSize { get => _labelFontSize; set { _labelFontSize = value; RecalculateValues(); } }

        [Display(Name = "Label Color", GroupName = "Labels", Order = 320)]
        public CrossColor LabelColor { get => _labelColor; set { _labelColor = value; RecalculateValues(); } }

        #endregion

        #region Properties — Visual: Sweet Zone

        [Display(Name = "Show Sweet Zone", GroupName = "Sweet Zone", Order = 350)]
        public bool ShowSweetZone { get => _showSweetZone; set { _showSweetZone = value; RecalculateValues(); } }

        [Display(Name = "Show EPA Line", GroupName = "Sweet Zone", Order = 360)]
        public bool ShowEpaLine { get => _showEpaLine; set { _showEpaLine = value; RecalculateValues(); } }

        [Display(Name = "Show ETA Apex", GroupName = "Sweet Zone", Order = 370)]
        public bool ShowEtaApex { get => _showEtaApex; set { _showEtaApex = value; RecalculateValues(); } }

        #endregion

        #region Properties — Alerts

        [Display(Name = "Enable Alerts", GroupName = "Alerts", Order = 400)]
        public bool EnableAlerts { get => _enableAlerts; set { _enableAlerts = value; RecalculateValues(); } }

        [Display(Name = "Alert Sound File", GroupName = "Alerts", Order = 410)]
        public string AlertFile { get => _alertFile; set { _alertFile = value; RecalculateValues(); } }

        [Display(Name = "Alert Background", GroupName = "Alerts", Order = 420)]
        public CrossColor AlertColor { get => _alertColor; set { _alertColor = value; RecalculateValues(); } }

        #endregion

        #region Properties — Fibonacci P2→P3

        [Display(Name = "Show Fib P2→P3", GroupName = "Fib Target (P2-P3)", Order = 600)]
        [Description("Dibujar niveles de Fibonacci entre P2 y P3.")]
        public bool ShowFibP2P3 { get => _showFibP2P3; set { _showFibP2P3 = value; RecalculateValues(); } }

        [Display(Name = "Fib Line Color", GroupName = "Fib Target (P2-P3)", Order = 610)]
        public CrossColor FibLineColor { get => _fibLineColor; set { _fibLineColor = value; RecalculateValues(); } }

        [Display(Name = "Fib Line Width", GroupName = "Fib Target (P2-P3)", Order = 620)]
        [Range(1, 5)]
        public int FibLineWidth { get => _fibLineWidth; set { _fibLineWidth = value; RecalculateValues(); } }

        [Display(Name = "Show Fib Labels", GroupName = "Fib Target (P2-P3)", Order = 625)]
        public bool ShowFibLabels { get => _showFibLabels; set { _showFibLabels = value; RecalculateValues(); } }

        [Display(Name = "Fib Label Font Size", GroupName = "Fib Target (P2-P3)", Order = 626)]
        [Range(6, 20)]
        public int FibLabelFontSize { get => _fibLabelFontSize; set { _fibLabelFontSize = value; RecalculateValues(); } }

        [Display(Name = "23.6%", GroupName = "Fib Target (P2-P3)", Order = 630)]
        public bool ShowFib236 { get => _showFib236; set { _showFib236 = value; RecalculateValues(); } }

        [Display(Name = "38.2%", GroupName = "Fib Target (P2-P3)", Order = 640)]
        public bool ShowFib382 { get => _showFib382; set { _showFib382 = value; RecalculateValues(); } }

        [Display(Name = "50.0%", GroupName = "Fib Target (P2-P3)", Order = 650)]
        public bool ShowFib500 { get => _showFib500; set { _showFib500 = value; RecalculateValues(); } }

        [Display(Name = "61.8%", GroupName = "Fib Target (P2-P3)", Order = 660)]
        public bool ShowFib618 { get => _showFib618; set { _showFib618 = value; RecalculateValues(); } }

        [Display(Name = "78.6%", GroupName = "Fib Target (P2-P3)", Order = 670)]
        public bool ShowFib786 { get => _showFib786; set { _showFib786 = value; RecalculateValues(); } }

        [Display(Name = "100%", GroupName = "Fib Target (P2-P3)", Order = 680)]
        public bool ShowFib1000 { get => _showFib1000; set { _showFib1000 = value; RecalculateValues(); } }

        [Display(Name = "127.2%", GroupName = "Fib Target (P2-P3)", Order = 690)]
        public bool ShowFib1272 { get => _showFib1272; set { _showFib1272 = value; RecalculateValues(); } }

        [Display(Name = "161.8%", GroupName = "Fib Target (P2-P3)", Order = 700)]
        public bool ShowFib1618 { get => _showFib1618; set { _showFib1618 = value; RecalculateValues(); } }

        #endregion

        #region Properties — Stop Loss

        [Display(Name = "Stop Offset (ticks)", GroupName = "Stop Loss", Order = 710)]
        [Description("Ticks de margen por debajo/encima de P5 para colocar el Stop Loss.")]
        [Range(0, 200)]
        public decimal StopOffsetTicks { get => _stopOffsetTicks; set { _stopOffsetTicks = value; RecalculateValues(); } }

        [Display(Name = "Show Stop Line", GroupName = "Stop Loss", Order = 720)]
        [Description("Dibujar la línea de Stop Loss en el gráfico.")]
        public bool ShowStopLine { get => _showStopLine; set { _showStopLine = value; RecalculateValues(); } }

        [Display(Name = "Stop Line Color", GroupName = "Stop Loss", Order = 730)]
        public CrossColor StopLineColor { get => _stopLineColor; set { _stopLineColor = value; RecalculateValues(); } }

        [Display(Name = "Stop Line Width", GroupName = "Stop Loss", Order = 740)]
        [Range(1, 5)]
        public int StopLineWidth { get => _stopLineWidth; set { _stopLineWidth = value; RecalculateValues(); } }

        #endregion

        #region Properties — P&&L

        [Display(Name = "Point Value ($)", GroupName = "P&L", Order = 745)]
        [Description("Valor en dólares de 1 punto del activo (ej. NQ=20, ES=50, MNQ=2, MES=5).")]
        [Range(0.01, 10000)]
        public decimal PointValue { get => _pointValue; set { _pointValue = value; RecalculateValues(); } }

        #endregion

        #region Properties — Stats Panel

        [Display(Name = "Show Stats Panel", GroupName = "Stats Panel", Order = 750)]
        [Description("Mostrar panel de estadísticas de ondas.")]
        public bool ShowStatsPanel { get => _showStatsPanel; set { _showStatsPanel = value; RecalculateValues(); } }

        [Display(Name = "Background Color", GroupName = "Stats Panel", Order = 760)]
        public CrossColor StatsBgColor { get => _statsBgColor; set { _statsBgColor = value; RecalculateValues(); } }

        [Display(Name = "Text Color", GroupName = "Stats Panel", Order = 770)]
        public CrossColor StatsTextColor { get => _statsTextColor; set { _statsTextColor = value; RecalculateValues(); } }

        [Display(Name = "Font Size", GroupName = "Stats Panel", Order = 780)]
        [Range(8, 24)]
        public float StatsFontSize { get => _statsFontSize; set { _statsFontSize = value; RecalculateValues(); } }

        [Display(Name = "Panel X Offset", GroupName = "Stats Panel", Order = 790)]
        [Range(0, 500)]
        public int StatsPanelX { get => _statsPanelX; set { _statsPanelX = value; RecalculateValues(); } }

        [Display(Name = "Panel Y Offset", GroupName = "Stats Panel", Order = 800)]
        [Range(0, 500)]
        public int StatsPanelY { get => _statsPanelY; set { _statsPanelY = value; RecalculateValues(); } }

        #endregion

        #region Properties — General

        [Display(Name = "Max Patterns on Chart", GroupName = "General", Order = 900)]
        [Description("Número máximo de patrones a mantener visibles simultáneamente.")]
        [Range(1, 50)]
        public int MaxPatterns { get => _maxPatterns; set { _maxPatterns = value; RecalculateValues(); } }

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
            // Limpiar estado al inicio de cada recálculo completo
            if (bar == 0)
            {
                _direction = 0;
                _lastHighBar = 0;
                _lastLowBar = 0;
                _lastBar = -1;
                _lastHigh = 0;
                _lastLow = 0;
                _pivots.Clear();
                _patterns.Clear();
                _detectedIds.Clear();
            }

            if (bar < ZigZagDepth * 2 + 1)
                return;

            // Ejecutar el ZigZag para detectar pivotes
            ProcessZigZag(bar);

            // Solo buscar patrones cuando hay suficientes pivotes
            if (_pivots.Count >= 5)
                DetectWolfePatterns(bar);

            // Validar patrones existentes
            ValidatePatterns(bar);

            // Generar alertas
            if (EnableAlerts)
                CheckAlerts(bar);
        }

        #endregion

        #region ZigZag Pivot Detection

        private void ProcessZigZag(int bar)
        {
            // Solo podemos confirmar pivotes con ZigZagDepth barras de retraso
            int confirmBar = bar - ZigZagDepth;
            if (confirmBar < ZigZagDepth)
                return;

            // Evitar reprocesamiento
            if (confirmBar <= _lastBar)
                return;
            _lastBar = confirmBar;

            var candle = GetCandle(confirmBar);
            decimal high = candle.High;
            decimal low = candle.Low;

            // Verificar pivote alto
            if (IsPivotHigh(confirmBar))
            {
                decimal tickSize = InstrumentInfo?.TickSize ?? 0.01m;
                decimal devPrice = ZigZagDeviation * tickSize;

                if (_direction != 1 || high > _lastHigh + devPrice)
                {
                    if (_direction == 1 && _pivots.Count > 0 && _pivots[^1].Type == 1)
                    {
                        // Actualizar el último pivote alto si este es más alto
                        if (high > _pivots[^1].Price)
                        {
                            _pivots[^1].Price = high;
                            _pivots[^1].Bar = confirmBar;
                        }
                    }
                    else
                    {
                        _pivots.Add(new Pivot { Bar = confirmBar, Price = high, Type = 1 });
                    }
                    _direction = 1;
                    _lastHigh = high;
                    _lastHighBar = confirmBar;
                }
            }

            // Verificar pivote bajo
            if (IsPivotLow(confirmBar))
            {
                decimal tickSize = InstrumentInfo?.TickSize ?? 0.01m;
                decimal devPrice = ZigZagDeviation * tickSize;

                if (_direction != -1 || low < _lastLow - devPrice)
                {
                    if (_direction == -1 && _pivots.Count > 0 && _pivots[^1].Type == -1)
                    {
                        // Actualizar el último pivote bajo si este es más bajo
                        if (low < _pivots[^1].Price)
                        {
                            _pivots[^1].Price = low;
                            _pivots[^1].Bar = confirmBar;
                        }
                    }
                    else
                    {
                        _pivots.Add(new Pivot { Bar = confirmBar, Price = low, Type = -1 });
                    }
                    _direction = -1;
                    _lastLow = low;
                    _lastLowBar = confirmBar;
                }
            }

            // Limitar la memoria de pivotes
            while (_pivots.Count > 200)
                _pivots.RemoveAt(0);
        }

        private bool IsPivotHigh(int bar)
        {
            decimal h = GetCandle(bar).High;
            for (int i = 1; i <= ZigZagDepth; i++)
            {
                if (bar - i < 0 || bar + i > CurrentBar)
                    return false;
                if (GetCandle(bar - i).High >= h || GetCandle(bar + i).High >= h)
                    return false;
            }
            return true;
        }

        private bool IsPivotLow(int bar)
        {
            decimal l = GetCandle(bar).Low;
            for (int i = 1; i <= ZigZagDepth; i++)
            {
                if (bar - i < 0 || bar + i > CurrentBar)
                    return false;
                if (GetCandle(bar - i).Low <= l || GetCandle(bar + i).Low <= l)
                    return false;
            }
            return true;
        }

        #endregion

        #region Wolfe Wave Detection

        /// <summary>
        /// Escanea los últimos pivotes buscando secuencias de 5 puntos que formen
        /// un patrón de Wolfe Wave válido.
        /// </summary>
        private void DetectWolfePatterns(int bar)
        {
            // Escanear combinaciones de los últimos pivotes (ventana deslizante)
            int scanCount = Math.Min(_pivots.Count, 30);
            int startIdx = _pivots.Count - scanCount;

            for (int i = startIdx; i <= _pivots.Count - 5; i++)
            {
                // Tomar 5 pivotes consecutivos
                var p1 = _pivots[i];
                var p2 = _pivots[i + 1];
                var p3 = _pivots[i + 2];
                var p4 = _pivots[i + 3];
                var p5 = _pivots[i + 4];

                // Los pivotes deben alternar en tipo (alto-bajo-alto-bajo-alto o bajo-alto-bajo-alto-bajo)
                if (p1.Type == p2.Type || p2.Type == p3.Type || p3.Type == p4.Type || p4.Type == p5.Type)
                    continue;

                // Intentar patrón alcista (bullish): P1=low, P2=high, P3=low, P4=high, P5=low
                if (p1.Type == -1 && p2.Type == 1 && p3.Type == -1 && p4.Type == 1 && p5.Type == -1)
                {
                    if (ValidateBullish(p1, p2, p3, p4, p5))
                    {
                        var pattern = BuildPattern(WolfeType.Bullish, p1, p2, p3, p4, p5);
                        if (pattern != null && !_detectedIds.Contains(pattern.Id))
                        {
                            _detectedIds.Add(pattern.Id);
                            _patterns.Add(pattern);
                            TrimPatterns();
                        }
                    }
                }

                // Intentar patrón bajista (bearish): P1=high, P2=low, P3=high, P4=low, P5=high
                if (p1.Type == 1 && p2.Type == -1 && p3.Type == 1 && p4.Type == -1 && p5.Type == 1)
                {
                    if (ValidateBearish(p1, p2, p3, p4, p5))
                    {
                        var pattern = BuildPattern(WolfeType.Bearish, p1, p2, p3, p4, p5);
                        if (pattern != null && !_detectedIds.Contains(pattern.Id))
                        {
                            _detectedIds.Add(pattern.Id);
                            _patterns.Add(pattern);
                            TrimPatterns();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Valida las reglas estructurales del patrón alcista.
        /// P1=min, P2=max, P3=min, P4=max, P5=min.
        /// </summary>
        private bool ValidateBullish(Pivot p1, Pivot p2, Pivot p3, Pivot p4, Pivot p5)
        {
            // P2 > P1 (máximo significativo superior al punto 1)
            if (p2.Price <= p1.Price) return false;

            // P3 < P1 (mínimo inferior al punto 1)
            if (p3.Price >= p1.Price) return false;

            // P4 > P1 pero P4 < P2 (máximo entre P1 y P2)
            if (p4.Price <= p1.Price || p4.Price >= p2.Price) return false;

            // P5 < P3 (mínimo inferior al punto 3 — falsa ruptura)
            if (p5.Price >= p3.Price) return false;

            // Las líneas 1-3 y 2-4 deben ser convergentes
            if (!AreLinesConvergent(p1, p3, p2, p4))
                return false;

            // Filtros opcionales
            if (!PassesFibonacciFilter(p1, p2, p3, p4, p5, WolfeType.Bullish))
                return false;

            if (!PassesSymmetryFilter(p1, p2, p3, p4))
                return false;

            if (!PassesVolumeFilter(p3, p5))
                return false;

            return true;
        }

        /// <summary>
        /// Valida las reglas estructurales del patrón bajista.
        /// P1=max, P2=min, P3=max, P4=min, P5=max.
        /// </summary>
        private bool ValidateBearish(Pivot p1, Pivot p2, Pivot p3, Pivot p4, Pivot p5)
        {
            // P2 < P1 (mínimo significativo inferior al punto 1)
            if (p2.Price >= p1.Price) return false;

            // P3 > P1 (máximo superior al punto 1)
            if (p3.Price <= p1.Price) return false;

            // P4 < P1 pero P4 > P2 (mínimo entre P2 y P1)
            if (p4.Price >= p1.Price || p4.Price <= p2.Price) return false;

            // P5 > P3 (máximo superior al punto 3)
            if (p5.Price <= p3.Price) return false;

            // Las líneas 1-3 y 2-4 deben ser convergentes
            if (!AreLinesConvergent(p1, p3, p2, p4))
                return false;

            // Filtros opcionales
            if (!PassesFibonacciFilter(p1, p2, p3, p4, p5, WolfeType.Bearish))
                return false;

            if (!PassesSymmetryFilter(p1, p2, p3, p4))
                return false;

            if (!PassesVolumeFilter(p3, p5))
                return false;

            return true;
        }

        #endregion

        #region Geometric Validations

        /// <summary>
        /// Verifica que las líneas que unen P1-P3 y P2-P4 sean convergentes.
        /// Dos líneas son convergentes si se intersectan hacia la derecha del P4/P5.
        /// </summary>
        private bool AreLinesConvergent(Pivot pA1, Pivot pA2, Pivot pB1, Pivot pB2)
        {
            // Línea A: pA1 -> pA2 (puntos 1-3)
            // Línea B: pB1 -> pB2 (puntos 2-4)
            decimal dxA = pA2.Bar - pA1.Bar;
            decimal dxB = pB2.Bar - pB1.Bar;

            if (dxA == 0 || dxB == 0)
                return false;

            decimal slopeA = (pA2.Price - pA1.Price) / dxA;
            decimal slopeB = (pB2.Price - pB1.Price) / dxB;

            // Si pendientes son iguales, las líneas son paralelas (no convergentes)
            if (slopeA == slopeB)
                return false;

            // Calcular punto de intersección
            decimal interceptA = pA1.Price - slopeA * pA1.Bar;
            decimal interceptB = pB1.Price - slopeB * pB1.Bar;

            decimal intersectBar = (interceptB - interceptA) / (slopeA - slopeB);

            // La intersección debe estar a la derecha del último punto del patrón
            decimal lastBar = Math.Max(pA2.Bar, pB2.Bar);
            return intersectBar > lastBar;
        }

        /// <summary>
        /// Calcula el punto de intersección (Apex) de las líneas 1-3 y 2-4.
        /// </summary>
        private (int apexBar, decimal apexPrice) CalculateApex(Pivot p1, Pivot p3, Pivot p2, Pivot p4)
        {
            decimal dxA = p3.Bar - p1.Bar;
            decimal dxB = p4.Bar - p2.Bar;

            if (dxA == 0 || dxB == 0)
                return (0, 0);

            decimal slopeA = (p3.Price - p1.Price) / dxA;
            decimal slopeB = (p4.Price - p2.Price) / dxB;

            if (slopeA == slopeB)
                return (0, 0);

            decimal interceptA = p1.Price - slopeA * p1.Bar;
            decimal interceptB = p2.Price - slopeB * p2.Bar;

            decimal intersectBar = (interceptB - interceptA) / (slopeA - slopeB);
            decimal intersectPrice = slopeA * intersectBar + interceptA;

            return ((int)Math.Round(intersectBar), intersectPrice);
        }

        /// <summary>
        /// Calcula los parámetros de la línea EPA (Punto 1 -> Punto 4 extendida).
        /// </summary>
        private (decimal slope, decimal intercept) CalculateEpaLine(Pivot p1, Pivot p4)
        {
            decimal dx = p4.Bar - p1.Bar;
            if (dx == 0)
                return (0, p1.Price);

            decimal slope = (p4.Price - p1.Price) / dx;
            decimal intercept = p1.Price - slope * p1.Bar;
            return (slope, intercept);
        }

        /// <summary>
        /// Calcula la Sweet Zone en la barra del P5.
        /// La Sweet Zone está delimitada por:
        ///   - Línea 1-3 extendida (un borde)
        ///   - Línea paralela a 2-4 trazada desde P3 (otro borde)
        /// </summary>
        private (decimal upper, decimal lower) CalculateSweetZone(Pivot p1, Pivot p2, Pivot p3, Pivot p4, Pivot p5)
        {
            // Línea 1-3 en la barra de P5
            decimal dx13 = p3.Bar - p1.Bar;
            if (dx13 == 0) return (p5.Price, p5.Price);
            decimal slope13 = (p3.Price - p1.Price) / dx13;
            decimal line13AtP5 = p1.Price + slope13 * (p5.Bar - p1.Bar);

            // Línea paralela a 2-4, partiendo desde P3
            decimal dx24 = p4.Bar - p2.Bar;
            if (dx24 == 0) return (p5.Price, p5.Price);
            decimal slope24 = (p4.Price - p2.Price) / dx24;
            decimal parallelAtP5 = p3.Price + slope24 * (p5.Bar - p3.Bar);

            decimal upper = Math.Max(line13AtP5, parallelAtP5);
            decimal lower = Math.Min(line13AtP5, parallelAtP5);

            return (upper, lower);
        }

        #endregion

        #region Filters

        /// <summary>
        /// Filtro de Fibonacci: verifica que P3 y P5 caigan en extensiones válidas.
        /// P3 debe estar en extensión 127.2%-161.8% de la onda 1-2.
        /// P5 debe estar en extensión 127.2%-161.8% de la onda 3-4.
        /// </summary>
        private bool PassesFibonacciFilter(Pivot p1, Pivot p2, Pivot p3, Pivot p4, Pivot p5, WolfeType direction)
        {
            if (!EnableFibFilter)
                return true;

            // Extensión de P3 respecto a onda 1-2
            decimal wave12 = Math.Abs(p2.Price - p1.Price);
            if (wave12 == 0) return false;

            decimal p3Extension = Math.Abs(p3.Price - p2.Price) / wave12;
            if (p3Extension < FibMinExtension || p3Extension > FibMaxExtension)
                return false;

            // Extensión de P5 respecto a onda 3-4
            decimal wave34 = Math.Abs(p4.Price - p3.Price);
            if (wave34 == 0) return false;

            decimal p5Extension = Math.Abs(p5.Price - p4.Price) / wave34;
            if (p5Extension < FibMinExtension || p5Extension > FibMaxExtension)
                return false;

            return true;
        }

        /// <summary>
        /// Filtro de simetría: la onda 1-2 y la onda 3-4 deben ser relativamente
        /// simétricas en tiempo.
        /// </summary>
        private bool PassesSymmetryFilter(Pivot p1, Pivot p2, Pivot p3, Pivot p4)
        {
            if (!EnableSymmetryFilter)
                return true;

            int bars12 = Math.Abs(p2.Bar - p1.Bar);
            int bars34 = Math.Abs(p4.Bar - p3.Bar);

            if (bars12 == 0 || bars34 == 0)
                return false;

            decimal ratio = bars12 > bars34
                ? (decimal)bars12 / bars34
                : (decimal)bars34 / bars12;

            return ratio <= MaxSymmetryRatio;
        }

        /// <summary>
        /// Filtro de volumen: verifica secado de volumen hacia P5 y spike en P5.
        /// </summary>
        private bool PassesVolumeFilter(Pivot p3, Pivot p5)
        {
            if (!EnableVolumeFilter)
                return true;

            if (p5.Bar < VolumeLookback + 1)
                return true; // No hay suficientes datos

            // Calcular volumen promedio en las barras previas al P5
            decimal totalVol = 0;
            for (int i = 1; i <= VolumeLookback; i++)
            {
                int idx = p5.Bar - i;
                if (idx >= 0)
                    totalVol += GetCandle(idx).Volume;
            }
            decimal avgVol = totalVol / VolumeLookback;

            if (avgVol == 0)
                return true;

            // Verificar que el volumen en P5 sea un spike
            decimal p5Vol = GetCandle(p5.Bar).Volume;
            return p5Vol >= avgVol * VolumeSpikeMultiplier;
        }

        #endregion

        #region Pattern Building

        private WolfePattern? BuildPattern(WolfeType direction, Pivot p1, Pivot p2, Pivot p3, Pivot p4, Pivot p5)
        {
            var (apexBar, apexPrice) = CalculateApex(p1, p3, p2, p4);
            var (epaSlope, epaIntercept) = CalculateEpaLine(p1, p4);
            var (sweetUpper, sweetLower) = CalculateSweetZone(p1, p2, p3, p4, p5);

            // Calcular el precio del Stop: por debajo de P5 (bullish) o encima de P5 (bearish)
            decimal tickSize = InstrumentInfo?.TickSize ?? 0.01m;
            decimal stopOffset = StopOffsetTicks * tickSize;
            decimal stopPrice = direction == WolfeType.Bullish
                ? p5.Price - stopOffset
                : p5.Price + stopOffset;

            return new WolfePattern
            {
                Direction = direction,
                P1 = p1,
                P2 = p2,
                P3 = p3,
                P4 = p4,
                P5 = p5,
                ApexBar = apexBar,
                ApexPrice = apexPrice,
                EpaSlope = epaSlope,
                EpaIntercept = epaIntercept,
                SweetUpperAtP5 = sweetUpper,
                SweetLowerAtP5 = sweetLower,
                StopPrice = stopPrice
            };
        }

        private void TrimPatterns()
        {
            while (_patterns.Count > MaxPatterns)
            {
                var oldest = _patterns[0];
                _detectedIds.Remove(oldest.Id);
                _patterns.RemoveAt(0);
            }
        }

        #endregion

        #region Pattern Validation (Invalidation)

        /// <summary>
        /// Invalida el patrón solo si el precio se mueve contundentemente
        /// en contra de la dirección esperada tras P5.
        /// Bullish: invalida si el precio cierra muy por debajo de P5 (nueva caída significativa).
        /// Bearish: invalida si el precio cierra muy por encima de P5 (nueva subida significativa).
        /// </summary>
        private void ValidatePatterns(int bar)
        {
            foreach (var p in _patterns)
            {
                // Solo validar después de P5
                if (bar <= p.P5.Bar)
                    continue;

                // Si ya está resuelto (target, stop o invalidado), no seguir evaluando
                if (p.Resolved)
                    continue;

                var candle = GetCandle(bar);
                decimal tickSize = InstrumentInfo?.TickSize ?? 0.01m;

                // === Verificar si el precio alcanzó la línea EPA (Target) ===
                decimal epaAtBar = p.EpaSlope * bar + p.EpaIntercept;

                if (p.Direction == WolfeType.Bullish)
                {
                    // Target alcanzado si el precio (high) toca o supera la línea EPA
                    if (candle.High >= epaAtBar)
                    {
                        p.TargetReached = true;
                        p.ExitPrice = epaAtBar;
                        p.PnlTicks = tickSize > 0 ? (p.ExitPrice - p.P5.Price) / tickSize : 0;
                        continue;
                    }

                    // Stop hit si el precio (low) toca o cae por debajo del Stop
                    if (candle.Low <= p.StopPrice)
                    {
                        p.StopHit = true;
                        p.Invalidated = true;
                        p.ExitPrice = p.StopPrice;
                        p.PnlTicks = tickSize > 0 ? (p.ExitPrice - p.P5.Price) / tickSize : 0;
                        continue;
                    }
                }
                else
                {
                    // Target alcanzado si el precio (low) toca o cae por debajo de la línea EPA
                    if (candle.Low <= epaAtBar)
                    {
                        p.TargetReached = true;
                        p.ExitPrice = epaAtBar;
                        p.PnlTicks = tickSize > 0 ? (p.P5.Price - p.ExitPrice) / tickSize : 0;
                        continue;
                    }

                    // Stop hit si el precio (high) toca o supera el Stop
                    if (candle.High >= p.StopPrice)
                    {
                        p.StopHit = true;
                        p.Invalidated = true;
                        p.ExitPrice = p.StopPrice;
                        p.PnlTicks = tickSize > 0 ? (p.P5.Price - p.ExitPrice) / tickSize : 0;
                        continue;
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
                if (p.Invalidated)
                    continue;

                var candle = GetCandle(bar);

                // Alerta 1: P5 toca la Sweet Zone (preparación)
                if (!p.SweetZoneAlertFired && bar >= p.P5.Bar)
                {
                    bool inSweetZone = candle.Close >= p.SweetLowerAtP5 && candle.Close <= p.SweetUpperAtP5;
                    if (inSweetZone)
                    {
                        string dir = p.Direction == WolfeType.Bullish ? "BULL" : "BEAR";
                        AddAlert(AlertFile, InstrumentInfo?.Instrument ?? "",
                            $"Wolfe Wave {dir}: P5 en Sweet Zone", AlertColor, AlertColor);
                        p.SweetZoneAlertFired = true;
                    }
                }

                // Alerta 2: Confirmación — cierre más allá de la línea 1-3
                if (!p.ConfirmationAlertFired && bar > p.P5.Bar)
                {
                    // Calcular línea 1-3 en la barra actual
                    decimal dx13 = p.P3.Bar - p.P1.Bar;
                    if (dx13 == 0) continue;
                    decimal slope13 = (p.P3.Price - p.P1.Price) / dx13;
                    decimal line13AtBar = p.P1.Price + slope13 * (bar - p.P1.Bar);

                    if (p.Direction == WolfeType.Bullish && candle.Close > line13AtBar)
                    {
                        AddAlert(AlertFile, InstrumentInfo?.Instrument ?? "",
                            "Wolfe Wave BULL: Confirmación de entrada (cierre > línea 1-3)", AlertColor, AlertColor);
                        p.ConfirmationAlertFired = true;
                    }
                    else if (p.Direction == WolfeType.Bearish && candle.Close < line13AtBar)
                    {
                        AddAlert(AlertFile, InstrumentInfo?.Instrument ?? "",
                            "Wolfe Wave BEAR: Confirmación de entrada (cierre < línea 1-3)", AlertColor, AlertColor);
                        p.ConfirmationAlertFired = true;
                    }
                }
            }
        }

        #endregion

        #region Rendering

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo?.PriceChartContainer == null)
                return;

            foreach (var pattern in _patterns)
            {
                DrawPattern(context, pattern);
            }

            // === Panel de Estadísticas ===
            if (ShowStatsPanel)
                DrawStatsPanel(context);
        }

        private void DrawPattern(RenderContext context, WolfePattern p)
        {
            var container = ChartInfo?.PriceChartContainer;
            if (container == null) return;

            // Determinar colores según estado
            Color lineColor;
            if (p.Invalidated)
                lineColor = ConvertColor(InvalidatedColor);
            else if (p.TargetReached)
                lineColor = Color.FromArgb(200, 0, 255, 100); // Verde brillante para target alcanzado
            else
                lineColor = p.Direction == WolfeType.Bullish
                    ? ConvertColor(BullishLineColor)
                    : ConvertColor(BearishLineColor);

            var pen = new RenderPen(lineColor, LineWidth);
            var epaPen = new RenderPen(ConvertColor(EpaLineColor), EpaLineWidth);

            // Obtener coordenadas de pantalla para los 5 puntos
            int x1 = container.GetXByBar(p.P1.Bar, false);
            int y1 = container.GetYByPrice(p.P1.Price, false);
            int x2 = container.GetXByBar(p.P2.Bar, false);
            int y2 = container.GetYByPrice(p.P2.Price, false);
            int x3 = container.GetXByBar(p.P3.Bar, false);
            int y3 = container.GetYByPrice(p.P3.Price, false);
            int x4 = container.GetXByBar(p.P4.Bar, false);
            int y4 = container.GetYByPrice(p.P4.Price, false);
            int x5 = container.GetXByBar(p.P5.Bar, false);
            int y5 = container.GetYByPrice(p.P5.Price, false);

            // === Dibujar segmentos 1-2-3-4-5 ===
            context.DrawLine(pen, x1, y1, x2, y2);
            context.DrawLine(pen, x2, y2, x3, y3);
            context.DrawLine(pen, x3, y3, x4, y4);
            context.DrawLine(pen, x4, y4, x5, y5);

            // === Dibujar líneas de convergencia (1-3 y 2-4) extendidas ===
            var dashPen = new RenderPen(lineColor, 1);

            // Extender línea 1-3 hasta el Apex
            if (p.ApexBar > 0 && p.ApexBar < CurrentBar + 200)
            {
                int xApex = container.GetXByBar(Math.Min(p.ApexBar, CurrentBar), false);
                int yApex = container.GetYByPrice(p.ApexPrice, false);
                context.DrawLine(dashPen, x3, y3, xApex, yApex);
                context.DrawLine(dashPen, x4, y4, xApex, yApex);

                // Etiqueta del Apex (ETA)
                if (ShowEtaApex)
                {
                    var font = new RenderFont("Arial", 9);
                    context.DrawString("ETA", font, lineColor, xApex - 10, yApex - 15);
                }
            }

            // === Línea EPA / Target (P1 -> P4 extendida) ===
            if (ShowEpaLine)
            {
                // Extender la línea EPA desde P5 hasta el Apex o más allá
                int epaEndBar = p.ApexBar > 0 ? p.ApexBar : p.P5.Bar + (p.P5.Bar - p.P1.Bar);
                epaEndBar = Math.Max(epaEndBar, p.P5.Bar + 20);
                epaEndBar = Math.Min(epaEndBar, CurrentBar + 100);
                decimal epaEndPrice = p.EpaSlope * epaEndBar + p.EpaIntercept;

                // Dibujar desde P5 en adelante para que la línea target sea visible
                decimal epaAtP5 = p.EpaSlope * p.P5.Bar + p.EpaIntercept;
                int xEpaStart = container.GetXByBar(p.P5.Bar, false);
                int yEpaStart = container.GetYByPrice(epaAtP5, false);
                int xEpaEnd = container.GetXByBar(Math.Min(epaEndBar, CurrentBar), false);
                int yEpaEnd = container.GetYByPrice(epaEndPrice, false);

                context.DrawLine(epaPen, xEpaStart, yEpaStart, xEpaEnd, yEpaEnd);

                // También dibujar el segmento P1-P4 como referencia
                context.DrawLine(dashPen, x1, y1, x4, y4);

                // Etiqueta EPA con precio objetivo
                var epaFont = new RenderFont("Arial", 9);
                context.DrawString($"Target ({epaEndPrice:F2})", epaFont, ConvertColor(EpaLineColor), xEpaEnd + 5, yEpaEnd - 5);
            }

            // === Stop Loss Line ===
            if (ShowStopLine)
            {
                var stopPen = new RenderPen(ConvertColor(StopLineColor), StopLineWidth);
                int yStop = container.GetYByPrice(p.StopPrice, false);

                // Dibujar línea horizontal del Stop desde P5 hacia la derecha
                int xStopStart = container.GetXByBar(p.P5.Bar, false);
                int stopEndBar = Math.Min(p.P5.Bar + 30, CurrentBar);
                int xStopEnd = container.GetXByBar(stopEndBar, false);
                context.DrawLine(stopPen, xStopStart, yStop, xStopEnd, yStop);

                // Etiqueta Stop
                var stopFont = new RenderFont("Arial", 9);
                context.DrawString($"Stop ({p.StopPrice:F2})", stopFont, ConvertColor(StopLineColor), xStopEnd + 4, yStop - 7);
            }

            // === Sweet Zone ===
            if (ShowSweetZone)
                DrawSweetZone(context, p);

            // === Fibonacci P2→P3 ===
            if (ShowFibP2P3)
                DrawFibonacciP2P3(context, p, container);

            // === Etiquetas de los puntos ===
            if (ShowLabels)
            {
                var labelFont = new RenderFont("Arial", LabelFontSize, FontStyle.Bold);
                Color lblColor = ConvertColor(LabelColor);
                int offsetY = 15;

                DrawLabel(context, "1", x1, y1, p.P1.Type, labelFont, lblColor, offsetY);
                DrawLabel(context, "2", x2, y2, p.P2.Type, labelFont, lblColor, offsetY);
                DrawLabel(context, "3", x3, y3, p.P3.Type, labelFont, lblColor, offsetY);
                DrawLabel(context, "4", x4, y4, p.P4.Type, labelFont, lblColor, offsetY);
                DrawLabel(context, "5", x5, y5, p.P5.Type, labelFont, lblColor, offsetY);
            }
        }

        /// <summary>
        /// Dibuja la Sweet Zone como un polígono semitransparente entre
        /// la línea 1-3 extendida y la línea paralela a 2-4 desde P3.
        /// </summary>
        private void DrawSweetZone(RenderContext context, WolfePattern p)
        {
            var priceContainer = ChartInfo?.PriceChartContainer;
            if (priceContainer == null) return;

            // Calcular los bordes de la Sweet Zone en varias barras
            decimal dx13 = p.P3.Bar - p.P1.Bar;
            decimal dx24 = p.P4.Bar - p.P2.Bar;
            if (dx13 == 0 || dx24 == 0) return;

            decimal slope13 = (p.P3.Price - p.P1.Price) / dx13;
            decimal slope24 = (p.P4.Price - p.P2.Price) / dx24;

            // La Sweet Zone se extiende desde P3 (o P5) hasta un poco más allá
            int startBar = Math.Max(p.P3.Bar, p.P5.Bar - 5);
            int endBar = Math.Min(p.P5.Bar + 10, CurrentBar);

            if (endBar <= startBar) return;

            // Construir los puntos del polígono
            var upperPoints = new List<Point>();
            var lowerPoints = new List<Point>();

            for (int bar = startBar; bar <= endBar; bar++)
            {
                // Línea 1-3 extendida
                decimal line13Price = p.P1.Price + slope13 * (bar - p.P1.Bar);
                // Paralela a 2-4 desde P3
                decimal parallelPrice = p.P3.Price + slope24 * (bar - p.P3.Bar);

                decimal upper = Math.Max(line13Price, parallelPrice);
                decimal lower = Math.Min(line13Price, parallelPrice);

                int x = priceContainer.GetXByBar(bar, false);
                upperPoints.Add(new Point(x, priceContainer.GetYByPrice(upper, false)));
                lowerPoints.Add(new Point(x, priceContainer.GetYByPrice(lower, false)));
            }

            // Crear polígono: upper de izquierda a derecha, luego lower de derecha a izquierda
            lowerPoints.Reverse();
            var polygon = new List<Point>();
            polygon.AddRange(upperPoints);
            polygon.AddRange(lowerPoints);

            if (polygon.Count >= 3)
            {
                context.FillPolygon(ConvertColor(SweetZoneColor), polygon.ToArray());
            }
        }

        /// <summary>
        /// Dibuja los niveles de Fibonacci entre P2 y P3.
        /// P2 es el origen (0%) y P3 es el 100%. Los niveles de extensión
        /// (127.2%, 161.8%) se proyectan más allá de P3 en dirección P2→P3.
        /// Las líneas se extienden horizontalmente desde P3 hacia la derecha.
        /// </summary>
        private void DrawFibonacciP2P3(RenderContext context, WolfePattern p, dynamic container)
        {
            decimal range = p.P3.Price - p.P2.Price; // positivo si P3>P2, negativo si P3<P2
            if (range == 0) return;

            var fibPen = new RenderPen(ConvertColor(FibLineColor), FibLineWidth);
            var fibFont = ShowFibLabels ? new RenderFont("Arial", FibLabelFontSize) : null;
            Color fibTextColor = ConvertColor(FibLineColor);

            // Determinar qué niveles están activados
            bool[] enabled = {
                ShowFib236, ShowFib382, ShowFib500, ShowFib618,
                ShowFib786, ShowFib1000, ShowFib1272, ShowFib1618
            };

            // Las líneas horizontales van desde P3 (bar) hacia la derecha
            int xStart = container.GetXByBar(p.P3.Bar, false);
            int xEnd = container.GetXByBar(Math.Min(p.P5.Bar + 20, CurrentBar), false);

            for (int i = 0; i < FibLevels.Length; i++)
            {
                if (!enabled[i]) continue;

                // Precio del nivel Fib: partiendo de P2, avanzando hacia P3
                decimal fibPrice = p.P2.Price + range * FibLevels[i];

                int yFib = container.GetYByPrice(fibPrice, false);

                // Línea horizontal en el nivel Fib
                context.DrawLine(fibPen, xStart, yFib, xEnd, yFib);

                // Etiqueta con el porcentaje y el precio
                if (fibFont != null)
                {
                    string label = $"{FibNames[i]}  ({fibPrice:F2})";
                    context.DrawString(label, fibFont, fibTextColor, xEnd + 4, yFib - 7);
                }
            }
        }

        private void DrawLabel(RenderContext context, string text, int x, int y, int pivotType, RenderFont font, Color color, int offset)
        {
            // Dibujar etiqueta encima de máximos, debajo de mínimos
            int yOffset = pivotType == 1 ? -offset : offset;
            context.DrawString(text, font, color, x - 5, y + yOffset);
        }

        #endregion

        #region Stats Panel

        /// <summary>
        /// Dibuja el panel HUD con estadísticas de ondas:
        /// targets, stops, win rate y P&L total.
        /// </summary>
        private void DrawStatsPanel(RenderContext context)
        {
            // Contar estadísticas y P&L
            int totalPatterns = _patterns.Count;
            int bullTarget = 0, bullStopped = 0, bullActive = 0;
            int bearTarget = 0, bearStopped = 0, bearActive = 0;
            decimal totalPnlTicks = 0;
            decimal wonPnlTicks = 0;
            decimal lostPnlTicks = 0;

            decimal tickSize = InstrumentInfo?.TickSize ?? 0.01m;
            // Valor en $ de 1 tick = PointValue * TickSize
            decimal tickValue = PointValue * tickSize;

            foreach (var p in _patterns)
            {
                if (p.Direction == WolfeType.Bullish)
                {
                    if (p.TargetReached) { bullTarget++; wonPnlTicks += p.PnlTicks; }
                    else if (p.StopHit) { bullStopped++; lostPnlTicks += p.PnlTicks; }
                    else if (p.Invalidated) { bullStopped++; }
                    else bullActive++;
                }
                else
                {
                    if (p.TargetReached) { bearTarget++; wonPnlTicks += p.PnlTicks; }
                    else if (p.StopHit) { bearStopped++; lostPnlTicks += p.PnlTicks; }
                    else if (p.Invalidated) { bearStopped++; }
                    else bearActive++;
                }
            }

            totalPnlTicks = wonPnlTicks + lostPnlTicks;
            int targetTotal = bullTarget + bearTarget;
            int stoppedTotal = bullStopped + bearStopped;
            int activeTotal = bullActive + bearActive;
            int resolvedTotal = targetTotal + stoppedTotal;
            decimal winRate = resolvedTotal > 0 ? Math.Round((decimal)targetTotal / resolvedTotal * 100, 1) : 0;

            // Convertir ticks a dólares
            decimal wonDollar = wonPnlTicks * tickValue;
            decimal lostDollar = lostPnlTicks * tickValue;
            decimal totalDollar = totalPnlTicks * tickValue;

            // Construir líneas de texto con colores asignados
            // (text, color_index): 0=gold, 1=white, 2=green, 3=red, 4=dynamic
            var rows = new List<(string text, int colorIdx)>
            {
                ("══ WOLFE WAVES ══", 0),
                ($"Total: {totalPatterns}   Active: {activeTotal}", 1),
                ("", 1),
                ($"✓ Target:  {targetTotal}   (Bull: {bullTarget}  Bear: {bearTarget})", 2),
                ($"✗ Stop:    {stoppedTotal}   (Bull: {bullStopped}  Bear: {bearStopped})", 3),
                ("", 1),
                ($"Win Rate:  {winRate}%", 4),
                ("", 1),
                ("── P&L ──", 0),
                ($"Tick Value: ${tickValue:F2}   (${PointValue}/pt)", 1),
                ($"Won:   +{wonPnlTicks:F1} ticks  (${wonDollar:F2})", 2),
                ($"Lost:  {lostPnlTicks:F1} ticks  (${lostDollar:F2})", 3),
                ("", 1),
                ($"NET:  {totalPnlTicks:F1} ticks  (${totalDollar:F2})", totalPnlTicks >= 0 ? 2 : 3),
            };

            // Medir dimensiones del panel
            var font = new RenderFont("Arial", StatsFontSize);
            var boldFont = new RenderFont("Arial", StatsFontSize, FontStyle.Bold);
            int lineHeight = (int)(StatsFontSize + 5);
            int totalHeight = rows.Count * lineHeight;

            int maxWidth = 0;
            foreach (var (text, _) in rows)
            {
                int w = (int)(text.Length * StatsFontSize * 0.58f);
                if (w > maxWidth) maxWidth = w;
            }

            maxWidth = Math.Max(maxWidth, 220);

            int pad = 8;
            int x = StatsPanelX;
            int y = StatsPanelY;

            // Fondo del panel
            var bgRect = new Rectangle(x, y, maxWidth + pad * 2, totalHeight + pad * 2);
            context.FillRectangle(ConvertColor(StatsBgColor), bgRect);

            // Borde sutil
            var borderPen = new RenderPen(Color.FromArgb(100, 255, 255, 255), 1);
            context.DrawRectangle(borderPen, bgRect);

            // Colores
            Color[] colors = {
                ConvertColor(CrossColors.Gold),       // 0 = gold (títulos)
                ConvertColor(StatsTextColor),         // 1 = white (texto normal)
                ConvertColor(CrossColors.Lime),       // 2 = green (ganancias)
                ConvertColor(CrossColors.OrangeRed),  // 3 = red (pérdidas)
                winRate >= 50                         // 4 = dynamic (win rate)
                    ? ConvertColor(CrossColors.Lime)
                    : ConvertColor(CrossColors.OrangeRed)
            };

            int tx = x + pad;
            int ty = y + pad;

            foreach (var (text, colorIdx) in rows)
            {
                if (text.Length > 0)
                {
                    bool isBold = colorIdx == 0 || text.StartsWith("Win") || text.StartsWith("NET");
                    var f = isBold ? boldFont : font;
                    context.DrawString(text, f, colors[Math.Min(colorIdx, colors.Length - 1)], tx, ty);
                }
                ty += lineHeight;
            }
        }

        #endregion

        #region Helpers

        private static Color ConvertColor(CrossColor c)
        {
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        #endregion
    }
}
