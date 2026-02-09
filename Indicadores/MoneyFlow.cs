using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace ATAS.Indicators.Technical
{
    [DisplayName("MoneyFlow NewFlow")]
    [Category("NewFlow")]
    public class MoneyFlow : Indicator
    {
        private sealed class RollingStats
        {
            private readonly Queue<decimal> _window = new();
            private decimal _sum;
            private decimal _sumSq;

            public int Count => _window.Count;

            public void Reset()
            {
                _window.Clear();
                _sum = 0m;
                _sumSq = 0m;
            }

            public void Push(decimal v, int max)
            {
                _window.Enqueue(v);
                _sum += v;
                _sumSq += v * v;
                while (_window.Count > max)
                {
                    var old = _window.Dequeue();
                    _sum -= old;
                    _sumSq -= old * old;
                }
            }

            public (decimal mean, decimal stdDev) GetMeanStdDev()
            {
                if (_window.Count < 2)
                    return (0m, 0m);

                var mean = _sum / _window.Count;
                var variance = (_sumSq / _window.Count) - (mean * mean);
                if (variance < 0)
                    variance = 0;

                var std = (decimal)Math.Sqrt((double)variance);
                return (mean, std);
            }
        }

        private int _lastStatsBar = -1;
        public enum ColumnType
        {
            [Display(Name = "None")]
            None,
            [Display(Name = "Last Price")]
            LastPrice,
            [Display(Name = "Call Money Flow")]
            CallMoneyFlow,
            [Display(Name = "Put Money Flow")]
            PutMoneyFlow,
            [Display(Name = "Money Flow Ratio")]
            MfRatio,
            [Display(Name = "Cash Net")]
            CashNet,
            [Display(Name = "Call Delta Flow")]
            CallDeltaFlow,
            [Display(Name = "Put Delta Flow")]
            PutDeltaFlow,
            [Display(Name = "Delta Flow Ratio")]
            DfRatio,
            [Display(Name = "Delta Call")]
            DeltaCall,
            [Display(Name = "Delta Put")]
            DeltaPut,
            [Display(Name = "Delta Net")]
            DeltaNet,
            [Display(Name = "Call IV Flow")]
            CallIvFlow,
            [Display(Name = "Put IV Flow")]
            PutIvFlow,
            [Display(Name = "IV Flow Ratio")]
            IvfRatio,
            [Display(Name = "Call OTM Impact")]
            CallOtmImpact,
            [Display(Name = "Put OTM Impact")]
            PutOtmImpact,
            [Display(Name = "Call ITM Impact")]
            CallItmImpact,
            [Display(Name = "Put ITM Impact")]
            PutItmImpact,
            [Display(Name = "Call GEX")]
            CallGex,
            [Display(Name = "Put GEX")]
            PutGex,
            [Display(Name = "Net GEX")]
            NetGex,
            [Display(Name = "Call Vanna Flow")]
            CallVannaFlow,
            [Display(Name = "Put Vanna Flow")]
            PutVannaFlow,
            [Display(Name = "Vanna Ratio")]
            VannaRatio,
            [Display(Name = "Call Charm")]
            CallCharm,
            [Display(Name = "Put Charm")]
            PutCharm,
            [Display(Name = "Charm Pressure")]
            CharmPressure,
            [Display(Name = "Call IV Flow ITM")]
            CallIvFlowItm,
            [Display(Name = "Put IV Flow ITM")]
            PutIvFlowItm,
            [Display(Name = "Call IV Flow OTM")]
            CallIvFlowOtm,
            [Display(Name = "Put IV Flow OTM")]
            PutIvFlowOtm,
            [Display(Name = "IV Net")]
            IvNet,
            [Display(Name = "Call Vol Imbalance")]
            CallVolImbalance,
            [Display(Name = "Put Vol Imbalance")]
            PutVolImbalance,
            [Display(Name = "Vol Imbalance Ratio")]
            VolImbalanceRatio,
            [Display(Name = "Call Smart Money")]
            CallSmartMoney,
            [Display(Name = "Put Smart Money")]
            PutSmartMoney,
            [Display(Name = "Smart Money Ratio")]
            SmartMoneyRatio,
            [Display(Name = "Call Hedge Pressure")]
            CallHedgePressure,
            [Display(Name = "Put Hedge Pressure")]
            PutHedgePressure,
            [Display(Name = "Net Hedge Pressure")]
            NetHedgePressure,
            [Display(Name = "Skew Pressure")]
            SkewPressure,
            [Display(Name = "Skew Intensity")]
            SkewIntensity,
            [Display(Name = "Premium Flow")]
            PremiumFlow,
            [Display(Name = "Call Premium")]
            CallPremium,
            [Display(Name = "Put Premium")]
            PutPremium,
            [Display(Name = "Volume")]
            Volume,
            [Display(Name = "Call Volume")]
            CallVolume,
            [Display(Name = "Put Volume")]
            PutVolume,
            [Display(Name = "Open Interest")]
            OpenInterest,
            [Display(Name = "Call Open Interest")]
            CallOpenInterest,
            [Display(Name = "Put Open Interest")]
            PutOpenInterest,
            [Display(Name = "Implied Volatility")]
            ImpliedVolatility,
            [Display(Name = "Call IV")]
            CallIv,
            [Display(Name = "Put IV")]
            PutIv
        }

        private class MoneyFlowData
        {
            public DateTime Timestamp { get; set; }
            public Dictionary<string, decimal> ColumnValues { get; set; } = new();
            
            // Legacy properties for backward compatibility
            public decimal LastPrice { get; set; }
            public decimal CallMoneyFlow { get; set; }
            public decimal PutMoneyFlow { get; set; }
            public decimal CashNet { get; set; }
            public decimal CallDeltaFlow { get; set; }
            public decimal PutDeltaFlow { get; set; }
            public decimal CallIvFlow { get; set; }
            public decimal PutIvFlow { get; set; }
            public decimal CallVannaFlow { get; set; }
            public decimal PutVannaFlow { get; set; }
            public decimal CallCharm { get; set; }
            public decimal PutCharm { get; set; }
            public decimal CallHedgePressure { get; set; }
            public decimal PutHedgePressure { get; set; }
            public decimal CallSmartMoney { get; set; }
            public decimal PutSmartMoney { get; set; }
        }

        private readonly List<MoneyFlowData> _data = new();
        private Dictionary<DateTime, MoneyFlowData> _dataByTimestamp = new();
        private List<string> _availableCsvFiles = new();
        private string _selectedCsvFile = string.Empty;
        private string _error = string.Empty;
        
        // Almacenar Big Trades detectados
        private Dictionary<int, (decimal callDiff, decimal putDiff, decimal netDiff)> _bigTrades = new();

        // Caches para acelerar el cálculo StdDev (por barra)
        private readonly Dictionary<int, MoneyFlowData?> _barDataCache = new();
        private readonly Dictionary<ColumnType, RollingStats> _incStats = new();

        // ValueDataSeries para renderizar en el panel
        private readonly ValueDataSeries _callFlowSeries = new("CallFlow", "Call Money Flow") 
        { 
            VisualType = VisualMode.Histogram, 
            Color = System.Windows.Media.Colors.DodgerBlue 
        };
        private readonly ValueDataSeries _putFlowSeries = new("PutFlow", "Put Money Flow") 
        { 
            VisualType = VisualMode.Histogram, 
            Color = System.Windows.Media.Colors.IndianRed 
        };
        // (Eliminado) Cash Net serie

        // Series de incrementos por barra
        private readonly ValueDataSeries _callFlowDeltaSeries = new("CallFlowΔ", "Call Money Flow Δ")
        {
            VisualType = VisualMode.Histogram,
            Color = System.Windows.Media.Colors.LightSkyBlue
        };
        private readonly ValueDataSeries _putFlowDeltaSeries = new("PutFlowΔ", "Put Money Flow Δ")
        {
            VisualType = VisualMode.Histogram,
            Color = System.Windows.Media.Colors.Salmon
        };
        // (Eliminado) Cash Net Δ

        // Líneas de niveles (porcentaje de máximos) para Call/Put Flow
        private readonly ValueDataSeries _callLevel100 = new("Call 100%", "Call 100%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.DimGray };
        private readonly ValueDataSeries _callLevel75 = new("Call 75%", "Call 75%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.Gray };
        private readonly ValueDataSeries _callLevel50 = new("Call 50%", "Call 50%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.DarkGray };
        private readonly ValueDataSeries _callLevel25 = new("Call 25%", "Call 25%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.LightGray };

        private readonly ValueDataSeries _putLevel100 = new("Put 100%", "Put 100%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.DimGray };
        private readonly ValueDataSeries _putLevel75 = new("Put 75%", "Put 75%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.Gray };
        private readonly ValueDataSeries _putLevel50 = new("Put 50%", "Put 50%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.DarkGray };
        private readonly ValueDataSeries _putLevel25 = new("Put 25%", "Put 25%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.LightGray };

        // (Eliminado) Series de análisis Call/Put Flow y Sentiment/Momentum

        // Series para colorear velas
        private readonly PaintbarsDataSeries _candleColorSeries = new("CandleColors", "Candle Colors")
        {
            IsHidden = true
        };

        // Propiedades configurables
        private string _csvFileName = string.Empty;
        private bool _autoRefreshData = true;
        private int _refreshIntervalSeconds = 60;
        private DateTime _lastRefreshTime = DateTime.MinValue;
        private decimal _priceMultiplier = 1m;
        private int _gmtOffset = 0;
        private int _timeToleranceMinutes = 10;
        private ColumnType _series1Column = ColumnType.CallMoneyFlow;
        private ColumnType _series2Column = ColumnType.PutMoneyFlow;
        // Series3 eliminado

        [Display(GroupName = "0. CSV File", Name = "CSV File Name", Order = 5, Description = "Nombre del archivo CSV (ej: data.csv, UnifiedInstrument.csv)")]
        public string CsvFileName
        {
            get => _csvFileName;
            set
            {
                _csvFileName = value;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _selectedCsvFile = value;
                    LoadMoneyFlowData();
                    RecalculateValues();
                }
            }
        }

        [Display(GroupName = "0. CSV File", Name = "Price Multiplier", Order = 6, Description = "Multiplicador de precio (ej: 4 para QQQ->NQ)")]
        [Range(0.01, 100)]
        public decimal PriceMultiplier
        {
            get => _priceMultiplier;
            set { _priceMultiplier = Math.Max(0.01m, value); }
        }

        [Display(GroupName = "0. CSV File", Name = "GMT Offset (hours)", Order = 7, Description = "Corrector horario manual si es necesario (normalmente 0, ATAS usa su propia zona)")]
        [Range(-12, 12)]
        public int GmtOffset
        {
            get => _gmtOffset;
            set 
            { 
                _gmtOffset = Math.Clamp(value, -12, 12); 
                LoadMoneyFlowData();
                RecalculateValues();
            }
        }

        [Display(GroupName = "0. CSV File", Name = "Time Tolerance (minutes)", Order = 8, Description = "Tolerancia para matcheo de timestamps (ej: 10 = ±10 minutos)")]
        [Range(1, 60)]
        public int TimeToleranceMinutes
        {
            get => _timeToleranceMinutes;
            set { _timeToleranceMinutes = Math.Clamp(value, 1, 60); }
        }

        [Display(GroupName = "0. CSV File", Name = "Auto Refresh Data", Order = 10)]
        public bool AutoRefreshData
        {
            get => _autoRefreshData;
            set { _autoRefreshData = value; }
        }

        [Display(GroupName = "0. CSV File", Name = "Refresh Interval (sec)", Order = 15)]
        [Range(1, 300)]
        public int RefreshIntervalSeconds
        {
            get => _refreshIntervalSeconds;
            set { _refreshIntervalSeconds = Math.Clamp(value, 1, 300); }
        }

        [Display(GroupName = "1. Series Selection", Name = "Series 1 Column", Order = 10, Description = "Columna CSV para la serie 1")]
        public ColumnType Series1Column
        {
            get => _series1Column;
            set { _series1Column = value; ResetStdDevCache(); LoadMoneyFlowData(); RecalculateValues(); }
        }

        [Display(GroupName = "1. Series Selection", Name = "Series 2 Column", Order = 20, Description = "Columna CSV para la serie 2")]
        public ColumnType Series2Column
        {
            get => _series2Column;
            set { _series2Column = value; ResetStdDevCache(); LoadMoneyFlowData(); RecalculateValues(); }
        }

        // Series3 eliminado

        // Big Trade settings
        private bool _showBigTradeMarkers = true;
        public enum BigTradeThresholdMode
        {
            [Display(Name = "Absolute (Millions)")] AbsoluteMillions,
            [Display(Name = "StdDev (σ)")] StdDev,
            [Display(Name = "Robust (MAD)")] RobustMad,
            [Display(Name = "Z-Score Modified")] ZScoreModified,
            [Display(Name = "IQR (Non-parametric)")] IQR
        }

        private BigTradeThresholdMode _bigTradeThresholdMode = BigTradeThresholdMode.AbsoluteMillions;
        private decimal _callMoneyFlowThreshold = 1m;
        private decimal _putMoneyFlowThreshold = 1m;
        // (Eliminado) Cash Net threshold
        private int _stdDevLookbackBars = 50;
        private decimal _stdDevMultiplier = 2m;
        private bool _stdDevUseNegative = true;
        private decimal _robustMultiplier = 4m;
        private decimal _zScoreThreshold = 3.5m;
        private decimal _iqrMultiplier = 1.5m;
        private decimal _callDeltaPositiveThreshold = 0m;
        private decimal _callDeltaNegativeThreshold = 0m;
        private decimal _putDeltaPositiveThreshold = 0m;
        private decimal _putDeltaNegativeThreshold = 0m;
        private int _bigTradeBaseRadius = 6;
        private int _bigTradeMaxRadius = 30;
        private decimal _bigTradeRadiusPerUnit = 0.0000001m;
        private int _bigTradeOffsetPx = 8;
        private bool _bigTradeShowValue = true;
        private Color _bigTradeTextColor = Color.Black;
        private int _bigTradeFontSize = 8;
        private Color _callMoneyFlowMarkerColor = Color.DodgerBlue;
        private Color _putMoneyFlowMarkerColor = Color.IndianRed;
        // (Eliminado) Cash Net marker colors
        private Color _callDeltaPositiveColor = Color.LightSkyBlue;
        private Color _callDeltaNegativeColor = Color.SteelBlue;
        private Color _putDeltaPositiveColor = Color.Salmon;
        private Color _putDeltaNegativeColor = Color.IndianRed;

        [Display(GroupName = "2. Big Trade Filters", Name = "Show Big Trade Markers", Order = 10)]
        public bool ShowBigTradeMarkers
        {
            get => _showBigTradeMarkers;
            set { _showBigTradeMarkers = value; RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "Threshold Mode", Order = 15, Description = "Absolute thresholds (millions) or StdDev-based thresholds")]
        public BigTradeThresholdMode ThresholdMode
        {
            get => _bigTradeThresholdMode;
            set { _bigTradeThresholdMode = value; ResetStdDevCache(); _bigTrades.Clear(); RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "StdDev Lookback (bars)", Order = 16, Description = "Número de barras para calcular μ/σ de los incrementos")]
        [Range(10, 500)]
        public int StdDevLookbackBars
        {
            get => _stdDevLookbackBars;
            set { _stdDevLookbackBars = Math.Clamp(value, 10, 500); ResetStdDevCache(); _bigTrades.Clear(); RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "StdDev Multiplier (σ)", Order = 17, Description = "2 = ±2σ, 3 = ±3σ")]
        [Range(1, 5)]
        public decimal StdDevMultiplier
        {
            get => _stdDevMultiplier;
            set { _stdDevMultiplier = Math.Clamp(value, 1m, 5m); ResetStdDevCache(); _bigTrades.Clear(); RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "StdDev Include Negative", Order = 18, Description = "Si está activo: detecta ±kσ. Si no: solo +kσ")]
        public bool StdDevIncludeNegative
        {
            get => _stdDevUseNegative;
            set { _stdDevUseNegative = value; ResetStdDevCache(); _bigTrades.Clear(); RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "Robust Multiplier (MAD)", Order = 19, Description = "Multiplicador para MAD (ej: 4 = sensible, 6 = estricto)")]
        [Range(1, 20)]
        public decimal RobustMultiplier
        {
            get => _robustMultiplier;
            set { _robustMultiplier = Math.Clamp(value, 1m, 20m); ResetStdDevCache(); _bigTrades.Clear(); RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "Z-Score Threshold", Order = 19, Description = "Umbral Z-Score modificado (típicamente 2.5-3.5, mayor = más estricto)")]
        [Range(1, 10)]
        public decimal ZScoreThreshold
        {
            get => _zScoreThreshold;
            set { _zScoreThreshold = Math.Clamp(value, 1m, 10m); ResetStdDevCache(); _bigTrades.Clear(); RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "IQR Multiplier", Order = 20, Description = "Multiplicador IQR (típicamente 1.5, mayor = menos sensible)")]
        [Range(0.5, 5)]
        public decimal IQRMultiplier
        {
            get => _iqrMultiplier;
            set { _iqrMultiplier = Math.Clamp(value, 0.5m, 5m); ResetStdDevCache(); _bigTrades.Clear(); RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "Call Money Flow Threshold (M)", Order = 20, Description = "En millones (ej: 1 = 1M)")]
        public decimal CallMoneyFlowThreshold
        {
            get => _callMoneyFlowThreshold;
            set
            {
                _callMoneyFlowThreshold = value;
                _bigTrades.Clear();
                RecalculateValues();
            }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "Put Money Flow Threshold (M)", Order = 30, Description = "En millones (ej: 1 = 1M)")]
        public decimal PutMoneyFlowThreshold
        {
            get => _putMoneyFlowThreshold;
            set
            {
                _putMoneyFlowThreshold = value;
                _bigTrades.Clear();
                RecalculateValues();
            }
        }

        // (Eliminado) Cash Net Threshold

        [Display(GroupName = "2. Delta Filters", Name = "Call Δ Positive Threshold", Order = 50, Description = "Mínimo incremento de Call Δ para mostrar")]
        public decimal CallDeltaPositiveThreshold
        {
            get => _callDeltaPositiveThreshold;
            set { _callDeltaPositiveThreshold = value; RecalculateValues(); }
        }

        [Display(GroupName = "2. Delta Filters", Name = "Call Δ Negative Threshold", Order = 60, Description = "Mínimo decremento de Call Δ (valor positivo)")]
        public decimal CallDeltaNegativeThreshold
        {
            get => _callDeltaNegativeThreshold;
            set { _callDeltaNegativeThreshold = value; RecalculateValues(); }
        }

        [Display(GroupName = "2. Delta Filters", Name = "Put Δ Positive Threshold", Order = 70, Description = "Mínimo incremento de Put Δ para mostrar")]
        public decimal PutDeltaPositiveThreshold
        {
            get => _putDeltaPositiveThreshold;
            set { _putDeltaPositiveThreshold = value; RecalculateValues(); }
        }

        [Display(GroupName = "2. Delta Filters", Name = "Put Δ Negative Threshold", Order = 80, Description = "Mínimo decremento de Put Δ (valor positivo)")]
        public decimal PutDeltaNegativeThreshold
        {
            get => _putDeltaNegativeThreshold;
            set { _putDeltaNegativeThreshold = value; RecalculateValues(); }
        }

        [Display(GroupName = "3. Delta Colors", Name = "Call Δ Positive Color", Order = 10)]
        public Color CallDeltaPositiveColor
        {
            get => _callDeltaPositiveColor;
            set { _callDeltaPositiveColor = value; RecalculateValues(); }
        }

        [Display(GroupName = "3. Delta Colors", Name = "Call Δ Negative Color", Order = 20)]
        public Color CallDeltaNegativeColor
        {
            get => _callDeltaNegativeColor;
            set { _callDeltaNegativeColor = value; RecalculateValues(); }
        }

        [Display(GroupName = "3. Delta Colors", Name = "Put Δ Positive Color", Order = 30)]
        public Color PutDeltaPositiveColor
        {
            get => _putDeltaPositiveColor;
            set { _putDeltaPositiveColor = value; RecalculateValues(); }
        }

        [Display(GroupName = "3. Delta Colors", Name = "Put Δ Negative Color", Order = 40)]
        public Color PutDeltaNegativeColor
        {
            get => _putDeltaNegativeColor;
            set { _putDeltaNegativeColor = value; RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "Base Radius (px)", Order = 50)]
        [Range(2, 200)]
        public int BigTradeBaseRadius
        {
            get => _bigTradeBaseRadius;
            set { _bigTradeBaseRadius = Math.Clamp(value, 2, 200); RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "Max Radius (px)", Order = 60)]
        [Range(2, 400)]
        public int BigTradeMaxRadius
        {
            get => _bigTradeMaxRadius;
            set { _bigTradeMaxRadius = Math.Clamp(value, 2, 400); RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "Radius Per Unit", Order = 70)]
        public decimal BigTradeRadiusPerUnit
        {
            get => _bigTradeRadiusPerUnit;
            set { _bigTradeRadiusPerUnit = value <= 0 ? 0.0000001m : value; RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "Offset from price (px)", Order = 80)]
        [Range(0, 500)]
        public int BigTradeOffsetPx
        {
            get => _bigTradeOffsetPx;
            set { _bigTradeOffsetPx = Math.Clamp(value, 0, 500); RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "Show Value Inside", Order = 90)]
        public bool BigTradeShowValue
        {
            get => _bigTradeShowValue;
            set { _bigTradeShowValue = value; RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "Value Text Color", Order = 100)]
        public Color BigTradeTextColor
        {
            get => _bigTradeTextColor;
            set { _bigTradeTextColor = value; RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "Font Size", Order = 110)]
        [Range(6, 20)]
        public int BigTradeFontSize
        {
            get => _bigTradeFontSize;
            set { _bigTradeFontSize = Math.Clamp(value, 6, 20); RecalculateValues(); }
        }

        [Display(GroupName = "3. Big Trade Colors", Name = "Call Money Flow Color", Order = 10)]
        public Color CallMoneyFlowMarkerColor
        {
            get => _callMoneyFlowMarkerColor;
            set { _callMoneyFlowMarkerColor = value; RecalculateValues(); }
        }

        [Display(GroupName = "3. Big Trade Colors", Name = "Put Money Flow Color", Order = 20)]
        public Color PutMoneyFlowMarkerColor
        {
            get => _putMoneyFlowMarkerColor;
            set { _putMoneyFlowMarkerColor = value; RecalculateValues(); }
        }

        // (Eliminado) Cash Net colors

        // Cross Candle Coloring
        private bool _colorCandlesByCross = true;
        private Color _crossUpColor = Color.LimeGreen;
        private Color _crossDownColor = Color.Red;

        // ITM Impact Dominance Candle Coloring
        public enum ItmDominancePaintMode
        {
            [Display(Name = "Off")]
            Off,
            [Display(Name = "Ratio only")]
            RatioOnly,
            [Display(Name = "Ratio + slope (short)")]
            RatioAndSlopeShort,
            [Display(Name = "Regime (multi-state)")]
            Regime
        }

        private bool _colorCandlesByItmDominance = true;
        private ItmDominancePaintMode _itmPaintMode = ItmDominancePaintMode.Regime;
        private int _itmSlopeWindowShort = 30;
        private int _itmSlopeWindowMedium = 90;
        private int _itmSlopeWindowLong = 300;
        private double _itmSlopeStrong = 0.005;
        private double _itmSlopeWeak = 0.001;
        private double _itmRatioSlopeThreshold = 0.002;
        private double _itmRatioStrong = 1.5;
        private double _itmRatioWeak = 1.15;
        private double _itmEpsIgnore = 1e-6;

        private Color _itmCallsStrongColor = Color.LimeGreen;
        private Color _itmCallsWeakColor = Color.FromArgb(255, 110, 220, 110);
        private Color _itmPutsStrongColor = Color.IndianRed;
        private Color _itmPutsWeakColor = Color.Salmon;
        private Color _itmNeutralColor = Color.Gray;
        private Color _itmCallsTakeoverColor = Color.Gold;
        private Color _itmPutsConsolidateColor = Color.DeepSkyBlue;

        private readonly List<double> _itmCallAbsSeries = new();
        private readonly List<double> _itmPutAbsSeries = new();
        private readonly List<double> _itmRatioSeries = new();

        [Display(GroupName = "4. Candle Coloring", Name = "Color Candles by Cross", Order = 10, Description = "Colorear velas cuando Series1 cruza Series2")]
        public bool ColorCandlesByCross
        {
            get => _colorCandlesByCross;
            set { _colorCandlesByCross = value; RecalculateValues(); }
        }

        [Display(GroupName = "4. Candle Coloring", Name = "Color candles by ITM dominance", Order = 11, Description = "Colorea velas según dominancia Call/Put en ITM impact")]
        public bool ColorCandlesByItmDominance
        {
            get => _colorCandlesByItmDominance;
            set { _colorCandlesByItmDominance = value; RecalculateValues(); }
        }

        [Display(GroupName = "4. Candle Coloring", Name = "ITM dominance mode", Order = 12)]
        public ItmDominancePaintMode ItmDominanceMode
        {
            get => _itmPaintMode;
            set { _itmPaintMode = value; RecalculateValues(); }
        }

        [Display(GroupName = "4. Candle Coloring", Name = "ITM ratio strong", Order = 13, Description = "Ratio |call|/|put| para dominancia fuerte (ej: 1.5)")]
        [Range(1.01, 100)]
        public double ItmRatioStrong
        {
            get => _itmRatioStrong;
            set { _itmRatioStrong = Math.Max(1.01, value); RecalculateValues(); }
        }

        [Display(GroupName = "4. Candle Coloring", Name = "ITM ratio weak", Order = 14, Description = "Ratio |call|/|put| para dominancia leve (ej: 1.15)")]
        [Range(1.01, 100)]
        public double ItmRatioWeak
        {
            get => _itmRatioWeak;
            set { _itmRatioWeak = Math.Max(1.01, value); RecalculateValues(); }
        }

        [Display(GroupName = "4. Candle Coloring", Name = "ITM slope window short", Order = 15)]
        [Range(5, 2000)]
        public int ItmSlopeWindowShort
        {
            get => _itmSlopeWindowShort;
            set { _itmSlopeWindowShort = Math.Clamp(value, 5, 2000); RecalculateValues(); }
        }

        [Display(GroupName = "4. Candle Coloring", Name = "ITM slope window medium", Order = 16)]
        [Range(5, 5000)]
        public int ItmSlopeWindowMedium
        {
            get => _itmSlopeWindowMedium;
            set { _itmSlopeWindowMedium = Math.Clamp(value, 5, 5000); RecalculateValues(); }
        }

        [Display(GroupName = "4. Candle Coloring", Name = "ITM slope window long", Order = 17)]
        [Range(5, 20000)]
        public int ItmSlopeWindowLong
        {
            get => _itmSlopeWindowLong;
            set { _itmSlopeWindowLong = Math.Clamp(value, 5, 20000); RecalculateValues(); }
        }

        [Display(GroupName = "4. Candle Coloring", Name = "ITM slope strong", Order = 18)]
        [Range(0.0001, 1)]
        public double ItmSlopeStrong
        {
            get => _itmSlopeStrong;
            set { _itmSlopeStrong = Math.Max(0.0001, value); RecalculateValues(); }
        }

        [Display(GroupName = "4. Candle Coloring", Name = "ITM slope weak", Order = 19)]
        [Range(0.0001, 1)]
        public double ItmSlopeWeak
        {
            get => _itmSlopeWeak;
            set { _itmSlopeWeak = Math.Max(0.0001, value); RecalculateValues(); }
        }

        [Display(GroupName = "4. Candle Coloring", Name = "ITM ratio slope threshold", Order = 20, Description = "Umbral slope del ratio a corto plazo (ej: 0.002)")]
        [Range(0.0001, 1)]
        public double ItmRatioSlopeThreshold
        {
            get => _itmRatioSlopeThreshold;
            set { _itmRatioSlopeThreshold = Math.Max(0.0001, value); RecalculateValues(); }
        }

        [Display(GroupName = "4. Candle Coloring", Name = "Cross Up Color", Order = 20, Description = "Color cuando Series1 cruza al alza sobre Series2")]
        public Color CrossUpColor
        {
            get => _crossUpColor;
            set { _crossUpColor = value; RecalculateValues(); }
        }

        [Display(GroupName = "4. Candle Coloring", Name = "Cross Down Color", Order = 30, Description = "Color cuando Series2 cruza al alza sobre Series1")]
        public Color CrossDownColor
        {
            get => _crossDownColor;
            set { _crossDownColor = value; RecalculateValues(); }
        }

        // Máximos observados para niveles de porcentaje
        private decimal _maxCallFlow = 0m;
        private decimal _maxPutFlow = 0m;

        // (Eliminado) Sentiment colors/thresholds

        // (Eliminado) Info Panel

        public MoneyFlow() : base(true)
        {
            DenyToChangePanel = false;
            Panel = IndicatorDataProvider.NewPanel;

            DataSeries[0] = _callFlowSeries;
            DataSeries.Add(_putFlowSeries);
            DataSeries.Add(_callFlowDeltaSeries);
            DataSeries.Add(_putFlowDeltaSeries);
            DataSeries.Add(_callLevel100);
            DataSeries.Add(_callLevel75);
            DataSeries.Add(_callLevel50);
            DataSeries.Add(_callLevel25);
            DataSeries.Add(_putLevel100);
            DataSeries.Add(_putLevel75);
            DataSeries.Add(_putLevel50);
            DataSeries.Add(_putLevel25);
            DataSeries.Add(_candleColorSeries);

            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);
            SubscribeToDrawingEvents(DrawingLayouts.Final);
            DrawAbovePrice = true;
        }

        protected override void OnInitialize()
        {
            // No auto-load, esperar a que el usuario escriba el nombre del CSV
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo?.PriceChartContainer == null)
                return;

            // Dibujar Big Trade Markers
            if (_showBigTradeMarkers && _bigTrades.Count > 0)
            {
                var font = new RenderFont("Arial", _bigTradeFontSize, FontStyle.Bold);

                foreach (var trade in _bigTrades)
                {
                    int bar = trade.Key;
                    var (callDiff, putDiff, netDiff) = trade.Value;

                    if (bar < FirstVisibleBarNumber || bar > LastVisibleBarNumber)
                        continue;

                    int xBar = ChartInfo.PriceChartContainer.GetXByBar(bar, false);
                    var candle = GetCandle(bar);
                    if (candle == null) continue;

                    // Posicionar Big Trades en el Low del candle
                    int yPrice = ChartInfo.PriceChartContainer.GetYByPrice(candle.Low, false);

                    // Dibujar Call Money Flow Big Trade
                    if (callDiff != 0)
                    {
                        int y = yPrice + 20;
                        DrawBigTradeMarker(context, xBar, y, callDiff, _callMoneyFlowMarkerColor, font);
                    }

                    // Dibujar Put Money Flow Big Trade
                    if (putDiff != 0)
                    {
                        int y = yPrice + 50;
                        DrawBigTradeMarker(context, xBar, y, putDiff, _putMoneyFlowMarkerColor, font);
                    }

                    // Dibujar Cash Net Big Trade
                    // (Eliminado) Cash Net Big Trade
                }
            }

            // (Eliminado) Info Panel
        }

        private void DrawBigTradeMarker(RenderContext context, int x, int y, decimal value, Color baseColor, RenderFont font)
        {
            // Calcular threshold según modo: en Absolute usa M × 1M, en StdDev/MAD usa directamente el diff
            decimal minThreshold = 0m;
            if (_bigTradeThresholdMode == BigTradeThresholdMode.AbsoluteMillions)
            {
                minThreshold = Math.Min(_callMoneyFlowThreshold, _putMoneyFlowThreshold) * 1_000_000m;
            }
            else
            {
                // En StdDev/MAD, el threshold ya está implícito en la detección, usar 0 para radio escala simple
                minThreshold = 0m;
            }
            
            int radius = _bigTradeBaseRadius + (int)Math.Round((double)((Math.Abs(value) - minThreshold) * _bigTradeRadiusPerUnit));
            radius = Math.Clamp(radius, _bigTradeBaseRadius, _bigTradeMaxRadius);

            var ellipseRect = new System.Drawing.Rectangle(x - radius, y - radius, radius * 2, radius * 2);

            try
            {
                context.FillEllipse(System.Drawing.Color.FromArgb(180, baseColor), ellipseRect);
                context.DrawEllipse(new RenderPen(baseColor, 2), ellipseRect);
            }
            catch
            {
                context.FillRectangle(System.Drawing.Color.FromArgb(180, baseColor), ellipseRect);
            }

            if (_bigTradeShowValue)
            {
                string txt = FormatCompactRounded(Math.Abs(value));
                int tw = EstimateTextWidth(txt, font);
                int tx = x - tw / 2;
                int ty = y - (_bigTradeFontSize / 2);
                context.DrawString(txt, font, _bigTradeTextColor, tx, ty);
            }
        }

        // (Eliminado) DrawInfoPanel

        private static int EstimateTextWidth(string text, RenderFont font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return (int)Math.Ceiling(text.Length * (font.Size * 0.58));
        }

        private static string FormatCompactRounded(decimal value)
        {
            var abs = Math.Abs(value);
            string sign = value < 0 ? "-" : "";
            
            if (abs >= 1_000_000_000m)
                return sign + Math.Round(abs / 1_000_000_000m, 0).ToString("0") + "B";
            if (abs >= 1_000_000m)
                return sign + Math.Round(abs / 1_000_000m, 0).ToString("0") + "M";
            if (abs >= 1_000m)
                return sign + Math.Round(abs / 1_000m, 0).ToString("0") + "K";
            return value.ToString("0", CultureInfo.CurrentCulture);
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            // Auto-refresh si está habilitado
            if (_autoRefreshData && (DateTime.Now - _lastRefreshTime).TotalSeconds >= _refreshIntervalSeconds)
            {
                LoadMoneyFlowData();
                _lastRefreshTime = DateTime.Now;
            }

            var candle = GetCandle(bar);
            if (candle == null) return;

            // Usar zona horaria de ATAS del instrumento
            int instrumentTimeZoneOffset = InstrumentInfo?.TimeZone ?? 0;
            var barTimeInCsvZone = candle.Time.AddHours(instrumentTimeZoneOffset).AddHours(_gmtOffset);

            // Buscar dato más cercano por timestamp usando tolerancia configurable
            var closestData = FindClosestDataByTime(barTimeInCsvZone);

            // Cache por barra para acelerar cálculos posteriores
            _barDataCache[bar] = closestData;

            if (closestData != null)
            {
                _callFlowSeries[bar] = closestData.CallMoneyFlow;
                _putFlowSeries[bar] = closestData.PutMoneyFlow;

                // Incrementos por barra (delta respecto a la barra anterior)
                if (bar > 0)
                {
                    var prevCandle = GetCandle(bar - 1);
                    if (prevCandle != null)
                    {
                        var prevBarTimeInCsvZoneForDelta = prevCandle.Time.AddHours(instrumentTimeZoneOffset).AddHours(_gmtOffset);
                        var prevDataForDelta = FindClosestDataByTime(prevBarTimeInCsvZoneForDelta);
                        if (prevDataForDelta != null)
                        {
                            var callDelta = closestData.CallMoneyFlow - prevDataForDelta.CallMoneyFlow;
                            var putDelta = closestData.PutMoneyFlow - prevDataForDelta.PutMoneyFlow;

                            // Filtros y colores para Call Δ
                            decimal filteredCallDelta = 0m;
                            if (callDelta >= _callDeltaPositiveThreshold && _callDeltaPositiveThreshold >= 0)
                                filteredCallDelta = callDelta;
                            else if (callDelta <= -_callDeltaNegativeThreshold && _callDeltaNegativeThreshold >= 0)
                                filteredCallDelta = callDelta;
                            _callFlowDeltaSeries[bar] = filteredCallDelta;
                            if (filteredCallDelta != 0)
                            {
                                var c = filteredCallDelta > 0 ? _callDeltaPositiveColor : _callDeltaNegativeColor;
                                _callFlowDeltaSeries.Colors[bar] = c;
                            }
                            else
                            {
                                _callFlowDeltaSeries.Colors[bar] = Color.Transparent;
                            }

                            // Filtros y colores para Put Δ
                            decimal filteredPutDelta = 0m;
                            if (putDelta >= _putDeltaPositiveThreshold && _putDeltaPositiveThreshold >= 0)
                                filteredPutDelta = putDelta;
                            else if (putDelta <= -_putDeltaNegativeThreshold && _putDeltaNegativeThreshold >= 0)
                                filteredPutDelta = putDelta;
                            _putFlowDeltaSeries[bar] = filteredPutDelta;
                            if (filteredPutDelta != 0)
                            {
                                var c = filteredPutDelta > 0 ? _putDeltaPositiveColor : _putDeltaNegativeColor;
                                _putFlowDeltaSeries.Colors[bar] = c;
                            }
                            else
                            {
                                _putFlowDeltaSeries.Colors[bar] = Color.Transparent;
                            }
                        }
                    }
                }

                // Series1 y Series2 (para niveles/cross) usan las columnas configuradas
                decimal series1 = _callFlowSeries[bar];
                decimal series2 = _putFlowSeries[bar];

                // Actualizar máximos para líneas de porcentaje (en base a Series1/Series2 actuales)
                if (series1 > _maxCallFlow)
                    _maxCallFlow = series1;
                if (series2 > _maxPutFlow)
                    _maxPutFlow = series2;

                _callLevel100[bar] = _maxCallFlow;
                _callLevel75[bar] = _maxCallFlow * 0.75m;
                _callLevel50[bar] = _maxCallFlow * 0.50m;
                _callLevel25[bar] = _maxCallFlow * 0.25m;

                _putLevel100[bar] = _maxPutFlow;
                _putLevel75[bar] = _maxPutFlow * 0.75m;
                _putLevel50[bar] = _maxPutFlow * 0.50m;
                _putLevel25[bar] = _maxPutFlow * 0.25m;

                // Detectar Big Trades comparando con barra anterior
                // Importante: StdDev usa rolling-stats incremental (rápido)
                // MAD usa mediana/MAD por barra (más costoso). Evitamos recalcular dos veces el mismo bar.
                if (_showBigTradeMarkers && bar > 0)
                {
                    var prevCandle = GetCandle(bar - 1);
                    if (prevCandle != null)
                    {
                        var prevBarTimeInCsvZone = prevCandle.Time.AddHours(instrumentTimeZoneOffset).AddHours(_gmtOffset);
                        var prevData = FindClosestDataByTime(prevBarTimeInCsvZone);

                        if (prevData != null)
                        {
                            // Para Absolute, usar siempre Call/Put Money Flow
                            // Para modos científicos, usar las Series seleccionadas
                            decimal callDiff = closestData.CallMoneyFlow - prevData.CallMoneyFlow;
                            decimal putDiff = closestData.PutMoneyFlow - prevData.PutMoneyFlow;

                            bool hasCallTrade;
                            bool hasPutTrade;
                            bool hasNetTrade = false;

                            if (_bigTradeThresholdMode == BigTradeThresholdMode.StdDev)
                            {
                                var k = _stdDevMultiplier;

                                // StdDev por columna seleccionada (Series1/Series2).
                                var col1 = _series1Column;
                                var col2 = _series2Column;

                                // Solo actualizar una vez por barra para no quedarse "pensando"
                                UpdateIncStatsIfNeeded(bar, instrumentTimeZoneOffset);

                                var (m1, s1) = GetIncMeanStd(col1);
                                var (m2, s2) = GetIncMeanStd(col2);

                                // diff = value(b) - value(b-1)
                                var d1 = CalcDiffForColumn(closestData, prevData, col1);
                                var d2 = CalcDiffForColumn(closestData, prevData, col2);

                                bool pass1;
                                bool pass2;

                                // Si todavía no hay σ suficiente (pocas observaciones) no marcamos.
                                if (_stdDevUseNegative)
                                {
                                    pass1 = s1 > 0 && (d1 >= m1 + k * s1 || d1 <= m1 - k * s1);
                                    pass2 = s2 > 0 && (d2 >= m2 + k * s2 || d2 <= m2 - k * s2);
                                }
                                else
                                {
                                    pass1 = d1 > 0 && (s1 > 0 ? d1 >= m1 + k * s1 : d1 > m1);
                                    pass2 = d2 > 0 && (s2 > 0 ? d2 >= m2 + k * s2 : d2 > m2);
                                }

                                // Se dibuja con los diffs Call/Put (slots existentes)
                                hasCallTrade = pass1;
                                hasPutTrade = pass2;
                            }
                            else if (_bigTradeThresholdMode == BigTradeThresholdMode.RobustMad)
                            {
                                // Alternativa robusta: MAD sobre incrementos (menos sensible a outliers que σ)
                                var col1 = _series1Column;
                                var col2 = _series2Column;
                                var k = _robustMultiplier;

                                var d1 = CalcDiffForColumn(closestData, prevData, col1);
                                var d2 = CalcDiffForColumn(closestData, prevData, col2);

                                var (med1, mad1) = CalcMedianMadIncrement(bar, col1, instrumentTimeZoneOffset);
                                var (med2, mad2) = CalcMedianMadIncrement(bar, col2, instrumentTimeZoneOffset);

                                // threshold = median + k * MAD (MAD=median(|x-median|))
                                // Si MAD=0 (serie muy plana) usamos fallback a "diff != med" para poder detectar spikes.
                                if (_stdDevUseNegative)
                                {
                                    hasCallTrade = mad1 > 0
                                        ? (d1 >= med1 + k * mad1 || d1 <= med1 - k * mad1)
                                        : (d1 != med1);
                                    hasPutTrade = mad2 > 0
                                        ? (d2 >= med2 + k * mad2 || d2 <= med2 - k * mad2)
                                        : (d2 != med2);
                                }
                                else
                                {
                                    hasCallTrade = d1 > 0 && (mad1 > 0 ? d1 >= med1 + k * mad1 : d1 > med1);
                                    hasPutTrade = d2 > 0 && (mad2 > 0 ? d2 >= med2 + k * mad2 : d2 > med2);
                                }
                            }
                            else if (_bigTradeThresholdMode == BigTradeThresholdMode.ZScoreModified)
                            {
                                // Z-Score modificado: mejor para datos con colas pesadas
                                // modZ = 0.6745 * |value - median| / MAD
                                var col1 = _series1Column;
                                var col2 = _series2Column;

                                var d1 = CalcDiffForColumn(closestData, prevData, col1);
                                var d2 = CalcDiffForColumn(closestData, prevData, col2);

                                var (med1, mad1) = CalcMedianMadIncrement(bar, col1, instrumentTimeZoneOffset);
                                var (med2, mad2) = CalcMedianMadIncrement(bar, col2, instrumentTimeZoneOffset);

                                // Factor de normalización MAD
                                const decimal K = 0.6745m;
                                
                                decimal modZ1 = mad1 > 0 ? K * Math.Abs(d1 - med1) / mad1 : 0m;
                                decimal modZ2 = mad2 > 0 ? K * Math.Abs(d2 - med2) / mad2 : 0m;

                                hasCallTrade = modZ1 > _zScoreThreshold;
                                hasPutTrade = modZ2 > _zScoreThreshold;
                            }
                            else if (_bigTradeThresholdMode == BigTradeThresholdMode.IQR)
                            {
                                // IQR (Interquartile Range): no paramétrico, robusto a outliers
                                var col1 = _series1Column;
                                var col2 = _series2Column;

                                var d1 = CalcDiffForColumn(closestData, prevData, col1);
                                var d2 = CalcDiffForColumn(closestData, prevData, col2);

                                var (q1_1, q3_1, iqr1) = CalcIQRIncrement(bar, col1, instrumentTimeZoneOffset);
                                var (q1_2, q3_2, iqr2) = CalcIQRIncrement(bar, col2, instrumentTimeZoneOffset);

                                decimal lower1 = q1_1 - _iqrMultiplier * iqr1;
                                decimal upper1 = q3_1 + _iqrMultiplier * iqr1;
                                decimal lower2 = q1_2 - _iqrMultiplier * iqr2;
                                decimal upper2 = q3_2 + _iqrMultiplier * iqr2;

                                hasCallTrade = d1 < lower1 || d1 > upper1;
                                hasPutTrade = d2 < lower2 || d2 > upper2;
                            }
                            else
                            {
                                // Convertir thresholds de M a unidades reales (M = 1,000,000)
                                decimal callThreshold = _callMoneyFlowThreshold * 1_000_000m;
                                decimal putThreshold = _putMoneyFlowThreshold * 1_000_000m;
                                // Solo considerar incrementos positivos de flujo (no reducciones)
                                hasCallTrade = callDiff >= callThreshold;
                                hasPutTrade = putDiff >= putThreshold;
                                hasNetTrade = false;
                            }

                            if (hasCallTrade || hasPutTrade)
                            {
                                // Para Absolute: usar callDiff/putDiff (Money Flow)
                                // Para científicos: usar los diffs de Series detectados
                                decimal storCallDiff = callDiff;
                                decimal storPutDiff = putDiff;

                                if (_bigTradeThresholdMode != BigTradeThresholdMode.AbsoluteMillions)
                                {
                                    // En modos científicos, recalcular para guardar los valores correctos
                                    var col1 = _series1Column;
                                    var col2 = _series2Column;
                                    storCallDiff = CalcDiffForColumn(closestData, prevData, col1);
                                    storPutDiff = CalcDiffForColumn(closestData, prevData, col2);
                                }

                                _bigTrades[bar] = (
                                    hasCallTrade ? storCallDiff : 0,
                                    hasPutTrade ? storPutDiff : 0,
                                    0
                                );
                            }

                            // (Eliminado) Sentiment/Momentum
                        }
                    }
                }

                // Colorear vela según relación Series1/Series2 (persistente)
                if (_colorCandlesByCross && bar > 0)
                {
                    var curS1 = _callFlowSeries[bar];
                    var curS2 = _putFlowSeries[bar];

                    if (curS1 > curS2)
                        _candleColorSeries[bar] = ToMediaColor(_crossUpColor);
                    else if (curS2 > curS1)
                        _candleColorSeries[bar] = ToMediaColor(_crossDownColor);
                }

                // ITM dominance candle coloring
                if (_colorCandlesByItmDominance && _itmPaintMode != ItmDominancePaintMode.Off)
                {
                    ApplyItmDominanceColor(bar, closestData);
                }

            }
        }

        private void ApplyItmDominanceColor(int bar, MoneyFlowData data)
        {
            // Read ITM impacts (absolute) from dynamic columns.
            // MoneyFlowData legacy properties do not include these fields, so we read ColumnValues.
            if (!data.ColumnValues.TryGetValue("call_itm_impact", out var callItm))
                callItm = 0m;
            if (!data.ColumnValues.TryGetValue("put_itm_impact", out var putItm))
                putItm = 0m;

            double c = Math.Abs((double)callItm);
            double p = Math.Abs((double)putItm);

            // 1) ignore near-zero rows
            if (c <= _itmEpsIgnore && p <= _itmEpsIgnore)
                return;

            // Maintain simple per-bar series to compute slopes.
            EnsureSeriesSize(_itmCallAbsSeries, bar);
            EnsureSeriesSize(_itmPutAbsSeries, bar);
            EnsureSeriesSize(_itmRatioSeries, bar);
            _itmCallAbsSeries[bar] = c;
            _itmPutAbsSeries[bar] = p;

            double ratio = p > _itmEpsIgnore ? (c / p) : (c > _itmEpsIgnore ? 999.0 : 1.0);
            _itmRatioSeries[bar] = ratio;

            // slopes
            double slopeCallS = CalculateSlope(_itmCallAbsSeries, _itmSlopeWindowShort);
            double slopePutS = CalculateSlope(_itmPutAbsSeries, _itmSlopeWindowShort);
            double slopeRatioS = CalculateSlope(_itmRatioSeries, _itmSlopeWindowShort);
            double slopeCallM = CalculateSlope(_itmCallAbsSeries, _itmSlopeWindowMedium);
            double slopePutM = CalculateSlope(_itmPutAbsSeries, _itmSlopeWindowMedium);
            double slopeCallL = CalculateSlope(_itmCallAbsSeries, _itmSlopeWindowLong);
            double slopePutL = CalculateSlope(_itmPutAbsSeries, _itmSlopeWindowLong);

            var callTrendS = ClassifyTrend(slopeCallS);
            var putTrendS = ClassifyTrend(slopePutS);

            Color? color = null;
            switch (_itmPaintMode)
            {
                case ItmDominancePaintMode.RatioOnly:
                    color = ColorForRatio(ratio);
                    break;
                case ItmDominancePaintMode.RatioAndSlopeShort:
                    color = ColorForRatioAndSlope(ratio, slopeRatioS);
                    break;
                case ItmDominancePaintMode.Regime:
                default:
                    // Combined classification (multi-state)
                    if (slopeRatioS > _itmRatioSlopeThreshold)
                    {
                        // Calls take recent advantage
                        color = _itmCallsTakeoverColor;
                    }
                    else if (slopeRatioS < -_itmRatioSlopeThreshold)
                    {
                        // Puts consolidate dominance
                        color = _itmPutsConsolidateColor;
                    }
                    else if (putTrendS == TrendClass.Flat && callTrendS == TrendClass.UpStrong)
                    {
                        // bullish scenario: calls rising, puts flat (often near highs)
                        color = _itmCallsTakeoverColor;
                    }
                    else if (ratio >= _itmRatioStrong)
                    {
                        color = _itmCallsStrongColor;
                    }
                    else if (ratio >= _itmRatioWeak)
                    {
                        color = _itmCallsWeakColor;
                    }
                    else if (ratio <= 1.0 / _itmRatioStrong)
                    {
                        color = _itmPutsStrongColor;
                    }
                    else if (ratio <= 1.0 / _itmRatioWeak)
                    {
                        color = _itmPutsWeakColor;
                    }
                    else
                    {
                        // If both moving same direction, tint based on medium/long dominance
                        bool bothUp = slopeCallM > _itmSlopeWeak && slopePutM > _itmSlopeWeak;
                        bool bothDown = slopeCallM < -_itmSlopeWeak && slopePutM < -_itmSlopeWeak;
                        if (bothUp || bothDown)
                        {
                            // prefer stronger long-term trend
                            if (Math.Abs(slopeCallL) > Math.Abs(slopePutL))
                                color = slopeCallL >= 0 ? _itmCallsWeakColor : _itmPutsWeakColor;
                            else
                                color = slopePutL >= 0 ? _itmPutsWeakColor : _itmCallsWeakColor;
                        }
                        else
                        {
                            color = _itmNeutralColor;
                        }
                    }
                    break;
            }

            if (color.HasValue)
                _candleColorSeries[bar] = ToMediaColor(color.Value);
        }

        private static void EnsureSeriesSize(List<double> list, int index)
        {
            while (list.Count <= index)
                list.Add(0);
        }

        private enum TrendClass { UpStrong, UpWeak, Flat, DownWeak, DownStrong }

        private TrendClass ClassifyTrend(double slope)
        {
            if (slope > _itmSlopeStrong) return TrendClass.UpStrong;
            if (slope > _itmSlopeWeak) return TrendClass.UpWeak;
            if (slope < -_itmSlopeStrong) return TrendClass.DownStrong;
            if (slope < -_itmSlopeWeak) return TrendClass.DownWeak;
            return TrendClass.Flat;
        }

        private Color ColorForRatio(double ratio)
        {
            if (ratio >= _itmRatioStrong) return _itmCallsStrongColor;
            if (ratio >= _itmRatioWeak) return _itmCallsWeakColor;
            if (ratio <= 1.0 / _itmRatioStrong) return _itmPutsStrongColor;
            if (ratio <= 1.0 / _itmRatioWeak) return _itmPutsWeakColor;
            return _itmNeutralColor;
        }

        private Color ColorForRatioAndSlope(double ratio, double slopeRatioShort)
        {
            if (slopeRatioShort > _itmRatioSlopeThreshold)
                return _itmCallsTakeoverColor;
            if (slopeRatioShort < -_itmRatioSlopeThreshold)
                return _itmPutsConsolidateColor;
            return ColorForRatio(ratio);
        }

        private double CalculateSlope(List<double> values, int window)
        {
            if (window <= 1 || values.Count < window)
                return 0;

            var recent = values.Skip(values.Count - window).ToList();
            var x = Enumerable.Range(0, window).Select(i => (double)i).ToList();
            var y = recent.Select(v => Math.Log(v + 1e-8)).ToList();

            double sumX = x.Sum();
            double sumY = y.Sum();
            double sumXY = x.Zip(y, (a, b) => a * b).Sum();
            double sumX2 = x.Sum(a => a * a);

            double n = window;
            double denom = (n * sumX2 - sumX * sumX);
            if (Math.Abs(denom) < 1e-12)
                return 0;
            double slope = (n * sumXY - sumX * sumY) / denom;
            return slope;
        }

        private MoneyFlowData FindClosestDataByTime(DateTime targetTime)
        {
            if (_dataByTimestamp.Count == 0)
                return null;

            // Buscar dentro de la tolerancia configurada
            var candidates = _dataByTimestamp
                .Where(kvp => Math.Abs((kvp.Key - targetTime).TotalMinutes) <= _timeToleranceMinutes)
                .ToList();

            if (candidates.Count == 0)
                return null;

            // Devolver el más cercano
            return candidates
                .OrderBy(kvp => Math.Abs((kvp.Key - targetTime).TotalMinutes))
                .First()
                .Value;
        }

        private void LoadMoneyFlowData()
        {
            try
            {
                _data.Clear();
                _dataByTimestamp.Clear();
                _error = string.Empty;
                ResetStdDevCache();

                if (string.IsNullOrWhiteSpace(_selectedCsvFile))
                {
                    _error = "No CSV file selected";
                    return;
                }

                string userDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string sessionDate = DateTime.Now.ToString("yyyy_MM_dd");
                string fullPath = Path.Combine(userDocuments, "dashcsv", "Dashdata", $"session_{sessionDate}", "UnifiedInstrument", _selectedCsvFile);

                if (!File.Exists(fullPath))
                {
                    _error = $"CSV file not found: {fullPath}";
                    return;
                }

                LoadCsvFile(fullPath);
            }
            catch (Exception ex)
            {
                _error = $"Error loading data: {ex.Message}";
            }
        }

        private void LoadCsvFile(string filePath)
        {
            try
            {
                var lines = File.ReadAllLines(filePath);
                if (lines.Length < 2)
                {
                    _error = "CSV file is empty";
                    return;
                }

                var headers = SplitCsvLine(lines[0]);
                
                // Buscar índice de tiempo
                int idxTime = FindIndex(headers, "iso_time");
                if (idxTime < 0)
                    idxTime = FindIndex(headers, "timestamp_ms");

                if (idxTime < 0)
                {
                    _error = "Required time column not found in CSV";
                    return;
                }

                // Crear mapeo de columnas (nombre normalizado -> índice)
                var columnMap = new Dictionary<string, int>();
                for (int i = 0; i < headers.Length; i++)
                {
                    string normalized = NormalizeColumnName(headers[i]);
                    columnMap[normalized] = i;
                }

                // Cargar todas las filas
                for (int i = 1; i < lines.Length; i++)
                {
                    var cols = SplitCsvLine(lines[i]);
                    if (cols.Length <= idxTime)
                        continue;

                    DateTime timestamp = DateTime.Now;
                    if (!TryParseDateTime(cols[idxTime], out timestamp))
                    {
                        if (!TryParseTimestamp(cols[idxTime], out timestamp))
                            timestamp = DateTime.Now;
                    }

                    var data = new MoneyFlowData
                    {
                        Timestamp = timestamp
                    };

                    // Cargar TODAS las columnas en el diccionario dinámico
                    foreach (var kvp in columnMap)
                    {
                        string columnName = kvp.Key;
                        int colIdx = kvp.Value;
                        if (colIdx < cols.Length && TryParseDecimal(cols[colIdx], out var value))
                        {
                            data.ColumnValues[columnName] = value;
                        }
                    }

                    // También rellenar propiedades legacy para compatibilidad hacia atrás
                    if (data.ColumnValues.TryGetValue("call_money_flow", out var cmf))
                        data.CallMoneyFlow = cmf;
                    if (data.ColumnValues.TryGetValue("put_money_flow", out var pmf))
                        data.PutMoneyFlow = pmf;
                    if (data.ColumnValues.TryGetValue("call_delta_flow", out var cdf))
                        data.CallDeltaFlow = cdf;
                    if (data.ColumnValues.TryGetValue("put_delta_flow", out var pdf))
                        data.PutDeltaFlow = pdf;
                    if (data.ColumnValues.TryGetValue("call_iv_flow", out var cif))
                        data.CallIvFlow = cif;
                    if (data.ColumnValues.TryGetValue("put_iv_flow", out var pif))
                        data.PutIvFlow = pif;
                    if (data.ColumnValues.TryGetValue("call_vanna_flow", out var cvf))
                        data.CallVannaFlow = cvf;
                    if (data.ColumnValues.TryGetValue("put_vanna_flow", out var pvf))
                        data.PutVannaFlow = pvf;
                    if (data.ColumnValues.TryGetValue("call_charm", out var cc))
                        data.CallCharm = cc;
                    if (data.ColumnValues.TryGetValue("put_charm", out var pc))
                        data.PutCharm = pc;
                    if (data.ColumnValues.TryGetValue("call_hedge_pressure", out var chp))
                        data.CallHedgePressure = chp;
                    if (data.ColumnValues.TryGetValue("put_hedge_pressure", out var php))
                        data.PutHedgePressure = php;
                    if (data.ColumnValues.TryGetValue("call_smart_money", out var csm))
                        data.CallSmartMoney = csm;
                    if (data.ColumnValues.TryGetValue("put_smart_money", out var psm))
                        data.PutSmartMoney = psm;
                    if (data.ColumnValues.TryGetValue("cash_net", out var cn))
                        data.CashNet = cn;
                    if (data.ColumnValues.TryGetValue("last_price", out var lp))
                        data.LastPrice = lp;

                    _data.Add(data);
                    _dataByTimestamp[timestamp] = data;
                }

                if (_data.Count == 0)
                    _error = "No valid data rows found";
            }
            catch (Exception ex)
            {
                _error = $"Error parsing CSV: {ex.Message}";
            }
        }

        private static string NormalizeColumnName(string columnName)
        {
            if (string.IsNullOrWhiteSpace(columnName))
                return "";
            
            // Convertir a minúsculas y reemplazar espacios/guiones con guiones bajos
            return new string(columnName
                .ToLowerInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')
                .Select(c => c == '-' ? '_' : c)
                .ToArray());
        }

        private void RefreshAvailableCsvFiles()
        {
            try
            {
                _availableCsvFiles.Clear();

                string userDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string sessionDate = DateTime.Now.ToString("yyyy_MM_dd");
                string fullPath = Path.Combine(userDocuments, "dashcsv", "Dashdata", $"session_{sessionDate}", "UnifiedInstrument");

                if (Directory.Exists(fullPath))
                {
                    var csvFiles = Directory.GetFiles(fullPath, "*.csv")
                        .OrderByDescending(f => File.GetLastWriteTime(f))
                        .Select(f => Path.GetFileName(f))
                        .ToList();

                    _availableCsvFiles.AddRange(csvFiles);
                }
            }
            catch (Exception ex)
            {
                _error = $"Error finding CSV files: {ex.Message}";
            }
        }

        // Helper methods
        private static int FindIndex(string[] headers, string key)
        {
            key = key ?? string.Empty;
            var knorm = new string(key.Where(ch => char.IsLetterOrDigit(ch)).ToArray()).ToLowerInvariant();
            for (int i = 0; i < headers.Length; i++)
            {
                var h = headers[i] ?? string.Empty;
                var norm = new string(h.Where(ch => char.IsLetterOrDigit(ch)).ToArray()).ToLowerInvariant();
                if (norm.Contains(knorm))
                    return i;
            }
            return -1;
        }

        private static int FindIndexByColumn(string[] headers, ColumnType columnType)
        {
            string columnName = columnType switch
            {
                ColumnType.None => "",
                ColumnType.LastPrice => "last_price",
                ColumnType.CallMoneyFlow => "call_money_flow",
                ColumnType.PutMoneyFlow => "put_money_flow",
                ColumnType.MfRatio => "mf_ratio",
                ColumnType.CashNet => "cash_net",
                ColumnType.CallDeltaFlow => "call_delta_flow",
                ColumnType.PutDeltaFlow => "put_delta_flow",
                ColumnType.DfRatio => "df_ratio",
                ColumnType.DeltaCall => "delta_call",
                ColumnType.DeltaPut => "delta_put",
                ColumnType.DeltaNet => "delta_net",
                ColumnType.CallIvFlow => "call_iv_flow",
                ColumnType.PutIvFlow => "put_iv_flow",
                ColumnType.IvfRatio => "ivf_ratio",
                ColumnType.CallOtmImpact => "call_otm_impact",
                ColumnType.PutOtmImpact => "put_otm_impact",
                ColumnType.CallItmImpact => "call_itm_impact",
                ColumnType.PutItmImpact => "put_itm_impact",
                ColumnType.CallGex => "call_gex",
                ColumnType.PutGex => "put_gex",
                ColumnType.NetGex => "net_gex",
                ColumnType.CallVannaFlow => "call_vanna_flow",
                ColumnType.PutVannaFlow => "put_vanna_flow",
                ColumnType.VannaRatio => "vanna_ratio",
                ColumnType.CallCharm => "call_charm",
                ColumnType.PutCharm => "put_charm",
                ColumnType.CharmPressure => "charm_pressure",
                ColumnType.CallIvFlowItm => "call_iv_flow_itm",
                ColumnType.PutIvFlowItm => "put_iv_flow_itm",
                ColumnType.CallIvFlowOtm => "call_iv_flow_otm",
                ColumnType.PutIvFlowOtm => "put_iv_flow_otm",
                ColumnType.IvNet => "iv_net",
                ColumnType.CallVolImbalance => "call_vol_imbalance",
                ColumnType.PutVolImbalance => "put_vol_imbalance",
                ColumnType.VolImbalanceRatio => "vol_imbalance_ratio",
                ColumnType.CallSmartMoney => "call_smart_money",
                ColumnType.PutSmartMoney => "put_smart_money",
                ColumnType.SmartMoneyRatio => "smart_money_ratio",
                ColumnType.CallHedgePressure => "call_hedge_pressure",
                ColumnType.PutHedgePressure => "put_hedge_pressure",
                ColumnType.NetHedgePressure => "net_hedge_pressure",
                ColumnType.SkewPressure => "skew_pressure",
                ColumnType.SkewIntensity => "skew_intensity",
                ColumnType.PremiumFlow => "premium_flow",
                ColumnType.CallPremium => "call_premium",
                ColumnType.PutPremium => "put_premium",
                ColumnType.Volume => "volume",
                ColumnType.CallVolume => "call_volume",
                ColumnType.PutVolume => "put_volume",
                ColumnType.OpenInterest => "open_interest",
                ColumnType.CallOpenInterest => "call_open_interest",
                ColumnType.PutOpenInterest => "put_open_interest",
                ColumnType.ImpliedVolatility => "implied_volatility",
                ColumnType.CallIv => "call_iv",
                ColumnType.PutIv => "put_iv",
                _ => ""
            };
            
            if (string.IsNullOrEmpty(columnName))
                return -1;
                
            return FindIndex(headers, columnName);
        }

        private static string[] SplitCsvLine(string line)
        {
            var list = new List<string>();
            bool inQuotes = false;
            var cur = new System.Text.StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        cur.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    list.Add(cur.ToString());
                    cur.Clear();
                }
                else
                {
                    cur.Append(c);
                }
            }
            list.Add(cur.ToString());
            return list.ToArray();
        }

        private static bool TryParseDecimal(string? s, out decimal value)
        {
            value = 0m;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().Trim('"').Replace("$", string.Empty).Replace(" ", string.Empty);
            return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseDateTime(string? s, out DateTime value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            return DateTime.TryParse(s.Trim('"', ' '), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out value);
        }

        private static bool TryParseTimestamp(string? s, out DateTime value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (long.TryParse(s.Trim(), out long ms))
            {
                value = new DateTime(1970, 1, 1).AddMilliseconds(ms);
                return true;
            }
            return false;
        }

        private System.Windows.Media.Color ToMediaColor(Color color)
        {
            return System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B);
        }

        private (decimal mean, decimal stdDev) CalcMeanStdDevIncrement(int bar, Func<MoneyFlowData, decimal> selector)
        {
            int instrumentTimeZoneOffset = InstrumentInfo?.TimeZone ?? 0;

            int startBar = Math.Max(1, bar - _stdDevLookbackBars);
            int n = 0;
            decimal sum = 0m;
            decimal sumSq = 0m;

            for (int b = startBar; b <= bar; b++)
            {
                var c0 = GetCandle(b);
                var c1 = GetCandle(b - 1);
                if (c0 == null || c1 == null)
                    continue;

                var t0 = c0.Time.AddHours(instrumentTimeZoneOffset).AddHours(_gmtOffset);
                var t1 = c1.Time.AddHours(instrumentTimeZoneOffset).AddHours(_gmtOffset);

                var d0 = FindClosestDataByTime(t0);
                var d1 = FindClosestDataByTime(t1);
                if (d0 == null || d1 == null)
                    continue;

                var diff = selector(d0) - selector(d1);
                n++;
                sum += diff;
                sumSq += diff * diff;
            }

            if (n < 2)
                return (0m, 0m);

            var mean = sum / n;
            var variance = (sumSq / n) - (mean * mean);
            if (variance < 0)
                variance = 0;

            var std = (decimal)Math.Sqrt((double)variance);
            return (mean, std);
        }

        private static Func<MoneyFlowData, decimal> GetSelectorForColumn(ColumnType column)
        {
            return column switch
            {
                ColumnType.LastPrice => d => d.LastPrice,
                ColumnType.CallMoneyFlow => d => d.CallMoneyFlow,
                ColumnType.PutMoneyFlow => d => d.PutMoneyFlow,
                ColumnType.CashNet => d => d.CashNet,
                ColumnType.CallDeltaFlow => d => d.CallDeltaFlow,
                ColumnType.PutDeltaFlow => d => d.PutDeltaFlow,
                ColumnType.CallIvFlow => d => d.CallIvFlow,
                ColumnType.PutIvFlow => d => d.PutIvFlow,
                ColumnType.CallVannaFlow => d => d.CallVannaFlow,
                ColumnType.PutVannaFlow => d => d.PutVannaFlow,
                ColumnType.CallCharm => d => d.CallCharm,
                ColumnType.PutCharm => d => d.PutCharm,
                ColumnType.CallHedgePressure => d => d.CallHedgePressure,
                ColumnType.PutHedgePressure => d => d.PutHedgePressure,
                ColumnType.CallSmartMoney => d => d.CallSmartMoney,
                ColumnType.PutSmartMoney => d => d.PutSmartMoney,
                _ => d => 0m
            };
        }

        private decimal GetColumnValue(MoneyFlowData data, ColumnType column)
        {
            // Primero intenta usar propiedades legacy (más rápido)
            var legacyValue = GetSelectorForColumn(column)(data);
            if (legacyValue != 0m)
                return legacyValue;

            // Si no hay valor legacy, busca en el diccionario dinámico
            string columnName = FindColumnNameByType(column);
            if (!string.IsNullOrEmpty(columnName) && data.ColumnValues.TryGetValue(columnName, out var value))
                return value;

            return 0m;
        }

        private string FindColumnNameByType(ColumnType column)
        {
            return column switch
            {
                ColumnType.None => "",
                ColumnType.LastPrice => "last_price",
                ColumnType.CallMoneyFlow => "call_money_flow",
                ColumnType.PutMoneyFlow => "put_money_flow",
                ColumnType.MfRatio => "mf_ratio",
                ColumnType.CashNet => "cash_net",
                ColumnType.CallDeltaFlow => "call_delta_flow",
                ColumnType.PutDeltaFlow => "put_delta_flow",
                ColumnType.DfRatio => "df_ratio",
                ColumnType.DeltaCall => "delta_call",
                ColumnType.DeltaPut => "delta_put",
                ColumnType.DeltaNet => "delta_net",
                ColumnType.CallIvFlow => "call_iv_flow",
                ColumnType.PutIvFlow => "put_iv_flow",
                ColumnType.IvfRatio => "ivf_ratio",
                ColumnType.CallOtmImpact => "call_otm_impact",
                ColumnType.PutOtmImpact => "put_otm_impact",
                ColumnType.CallItmImpact => "call_itm_impact",
                ColumnType.PutItmImpact => "put_itm_impact",
                ColumnType.CallGex => "call_gex",
                ColumnType.PutGex => "put_gex",
                ColumnType.NetGex => "net_gex",
                ColumnType.CallVannaFlow => "call_vanna_flow",
                ColumnType.PutVannaFlow => "put_vanna_flow",
                ColumnType.VannaRatio => "vanna_ratio",
                ColumnType.CallCharm => "call_charm",
                ColumnType.PutCharm => "put_charm",
                ColumnType.CharmPressure => "charm_pressure",
                ColumnType.CallIvFlowItm => "call_iv_flow_itm",
                ColumnType.PutIvFlowItm => "put_iv_flow_itm",
                ColumnType.CallIvFlowOtm => "call_iv_flow_otm",
                ColumnType.PutIvFlowOtm => "put_iv_flow_otm",
                ColumnType.IvNet => "iv_net",
                ColumnType.CallVolImbalance => "call_vol_imbalance",
                ColumnType.PutVolImbalance => "put_vol_imbalance",
                ColumnType.VolImbalanceRatio => "vol_imbalance_ratio",
                ColumnType.CallSmartMoney => "call_smart_money",
                ColumnType.PutSmartMoney => "put_smart_money",
                ColumnType.SmartMoneyRatio => "smart_money_ratio",
                ColumnType.CallHedgePressure => "call_hedge_pressure",
                ColumnType.PutHedgePressure => "put_hedge_pressure",
                ColumnType.NetHedgePressure => "net_hedge_pressure",
                ColumnType.SkewPressure => "skew_pressure",
                ColumnType.SkewIntensity => "skew_intensity",
                ColumnType.PremiumFlow => "premium_flow",
                ColumnType.CallPremium => "call_premium",
                ColumnType.PutPremium => "put_premium",
                ColumnType.Volume => "volume",
                ColumnType.CallVolume => "call_volume",
                ColumnType.PutVolume => "put_volume",
                ColumnType.OpenInterest => "open_interest",
                ColumnType.CallOpenInterest => "call_open_interest",
                ColumnType.PutOpenInterest => "put_open_interest",
                ColumnType.ImpliedVolatility => "implied_volatility",
                ColumnType.CallIv => "call_iv",
                ColumnType.PutIv => "put_iv",
                _ => ""
            };
        }

        private decimal CalcDiffForColumn(MoneyFlowData current, MoneyFlowData previous, ColumnType column)
        {
            string columnName = FindColumnNameByType(column);
            
            // Intentar obtener del diccionario dinámico primero
            if (!string.IsNullOrEmpty(columnName))
            {
                bool hasCurrent = current.ColumnValues.TryGetValue(columnName, out var currVal);
                bool hasPrevious = previous.ColumnValues.TryGetValue(columnName, out var prevVal);
                
                if (hasCurrent && hasPrevious)
                    return currVal - prevVal;
            }

            // Fallback a propiedades legacy para compatibilidad hacia atrás
            var selector = GetSelectorForColumn(column);
            return selector(current) - selector(previous);
        }

        private void ResetStdDevCache()
        {
            _incStats.Clear();
            _barDataCache.Clear();
            _lastStatsBar = -1;
        }

        private (decimal mean, decimal stdDev) GetIncMeanStd(ColumnType column)
        {
            if (!_incStats.TryGetValue(column, out var stats))
                return (0m, 0m);
            return stats.GetMeanStdDev();
        }

        private void UpdateIncStatsIfNeeded(int bar, int instrumentTimeZoneOffset)
        {
            if (bar <= 0)
                return;

            // Si retrocedieron barras (recalc), reiniciar para evitar bucles largos
            if (_lastStatsBar > bar)
                _lastStatsBar = -1;

            var from = Math.Max(1, _lastStatsBar + 1);
            if (from > bar)
                return;

            for (int b = from; b <= bar; b++)
            {
                var d0 = TryGetBarData(b, instrumentTimeZoneOffset);
                var d1 = TryGetBarData(b - 1, instrumentTimeZoneOffset);
                if (d0 == null || d1 == null)
                    continue;

                var c1 = _series1Column;
                var c2 = _series2Column;

                PushInc(c1, CalcDiffForColumn(d0, d1, c1));
                PushInc(c2, CalcDiffForColumn(d0, d1, c2));
            }

            _lastStatsBar = bar;
        }

        private void PushInc(ColumnType column, decimal inc)
        {
            if (!_incStats.TryGetValue(column, out var stats))
            {
                stats = new RollingStats();
                _incStats[column] = stats;
            }
            stats.Push(inc, _stdDevLookbackBars);
        }

        private MoneyFlowData? TryGetBarData(int bar, int instrumentTimeZoneOffset)
        {
            if (bar < 0)
                return null;

            if (_barDataCache.TryGetValue(bar, out var cached))
                return cached;

            var candle = GetCandle(bar);
            if (candle == null)
            {
                _barDataCache[bar] = null;
                return null;
            }

            var t = candle.Time.AddHours(instrumentTimeZoneOffset).AddHours(_gmtOffset);
            var data = FindClosestDataByTime(t);
            _barDataCache[bar] = data;
            return data;
        }

        private (decimal median, decimal mad) CalcMedianMadIncrement(int bar, ColumnType column, int instrumentTimeZoneOffset)
        {
            if (bar <= 0)
                return (0m, 0m);

            int startBar = Math.Max(1, bar - _stdDevLookbackBars);
            var inc = new List<decimal>(_stdDevLookbackBars);

            for (int b = startBar; b <= bar; b++)
            {
                var d0 = TryGetBarData(b, instrumentTimeZoneOffset);
                var d1 = TryGetBarData(b - 1, instrumentTimeZoneOffset);
                if (d0 == null || d1 == null)
                    continue;

                inc.Add(CalcDiffForColumn(d0, d1, column));
            }

            if (inc.Count < 3)
                return (0m, 0m);

            inc.Sort();
            var median = MedianOfSorted(inc);

            // MAD
            var dev = new List<decimal>(inc.Count);
            for (int i = 0; i < inc.Count; i++)
                dev.Add(Math.Abs(inc[i] - median));
            dev.Sort();
            var mad = MedianOfSorted(dev);

            return (median, mad);
        }

        private static decimal MedianOfSorted(List<decimal> sorted)
        {
            int n = sorted.Count;
            if (n == 0)
                return 0m;
            int mid = n / 2;
            if ((n & 1) == 1)
                return sorted[mid];
            return (sorted[mid - 1] + sorted[mid]) / 2m;
        }

        private (decimal q1, decimal q3, decimal iqr) CalcIQRIncrement(int bar, ColumnType column, int instrumentTimeZoneOffset)
        {
            if (bar <= 0)
                return (0m, 0m, 0m);

            int startBar = Math.Max(1, bar - _stdDevLookbackBars);
            var inc = new List<decimal>(_stdDevLookbackBars);

            for (int b = startBar; b <= bar; b++)
            {
                var d0 = TryGetBarData(b, instrumentTimeZoneOffset);
                var d1 = TryGetBarData(b - 1, instrumentTimeZoneOffset);
                if (d0 == null || d1 == null)
                    continue;

                inc.Add(CalcDiffForColumn(d0, d1, column));
            }

            if (inc.Count < 4)
                return (0m, 0m, 0m);

            inc.Sort();
            int q1_idx = inc.Count / 4;
            int q3_idx = (3 * inc.Count) / 4;
            var q1 = inc[q1_idx];
            var q3 = inc[q3_idx];
            var iqr = q3 - q1;

            return (q1, q3, iqr);
        }
    }
}
