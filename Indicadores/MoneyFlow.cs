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
        private readonly ValueDataSeries _cashNetSeries = new("CashNet", "Cash Net") 
        { 
            VisualType = VisualMode.Histogram, 
            Color = System.Windows.Media.Colors.MediumSeaGreen 
        };

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
        private readonly ValueDataSeries _cashNetDeltaSeries = new("CashNetΔ", "Cash Net Δ")
        {
            VisualType = VisualMode.Histogram,
            Color = System.Windows.Media.Colors.LightGreen
        };

        // Líneas de niveles (porcentaje de máximos) para Call/Put Flow
        private readonly ValueDataSeries _callLevel100 = new("Call 100%", "Call 100%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.DimGray };
        private readonly ValueDataSeries _callLevel75 = new("Call 75%", "Call 75%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.Gray };
        private readonly ValueDataSeries _callLevel50 = new("Call 50%", "Call 50%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.DarkGray };
        private readonly ValueDataSeries _callLevel25 = new("Call 25%", "Call 25%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.LightGray };

        private readonly ValueDataSeries _putLevel100 = new("Put 100%", "Put 100%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.DimGray };
        private readonly ValueDataSeries _putLevel75 = new("Put 75%", "Put 75%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.Gray };
        private readonly ValueDataSeries _putLevel50 = new("Put 50%", "Put 50%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.DarkGray };
        private readonly ValueDataSeries _putLevel25 = new("Put 25%", "Put 25%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.LightGray };

        // Series de análisis Call/Put Flow
        private readonly ValueDataSeries _flowAnalysisSeries = new("FlowAnalysis", "Call/Put Analysis")
        {
            VisualType = VisualMode.Line,
            Color = System.Windows.Media.Colors.White,
            Width = 2
        };
        private readonly ValueDataSeries _flowDivergenceSeries = new("FlowDivergence", "Flow Divergence")
        {
            VisualType = VisualMode.Histogram,
            Color = System.Windows.Media.Colors.Yellow
        };

        // Series para análisis de sentimiento
        private readonly ValueDataSeries _sentimentSeries = new("Sentiment", "Market Sentiment")
        {
            VisualType = VisualMode.Histogram,
            Color = System.Windows.Media.Colors.Cyan
        };
        private readonly ValueDataSeries _momentumSeries = new("Momentum", "Flow Momentum")
        {
            VisualType = VisualMode.Histogram,
            Color = System.Windows.Media.Colors.Magenta
        };

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
        private ColumnType _series3Column = ColumnType.CashNet;

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
            set { _series1Column = value; LoadMoneyFlowData(); RecalculateValues(); }
        }

        [Display(GroupName = "1. Series Selection", Name = "Series 2 Column", Order = 20, Description = "Columna CSV para la serie 2")]
        public ColumnType Series2Column
        {
            get => _series2Column;
            set { _series2Column = value; LoadMoneyFlowData(); RecalculateValues(); }
        }

        [Display(GroupName = "1. Series Selection", Name = "Series 3 Column", Order = 30, Description = "Columna CSV para la serie 3")]
        public ColumnType Series3Column
        {
            get => _series3Column;
            set { _series3Column = value; LoadMoneyFlowData(); RecalculateValues(); }
        }

        // Big Trade settings
        private bool _showBigTradeMarkers = true;
        private decimal _callMoneyFlowThreshold = 1m;
        private decimal _putMoneyFlowThreshold = 1m;
        private decimal _cashNetThreshold = 1m;
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
        private Color _cashNetMarkerColor = Color.MediumSeaGreen;
        private Color _cashNetNegativeMarkerColor = Color.Red;
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

        [Display(GroupName = "2. Big Trade Filters", Name = "Cash Net Threshold (M)", Order = 40, Description = "En millones (ej: 1 = 1M)")]
        public decimal CashNetThreshold
        {
            get => _cashNetThreshold;
            set
            {
                _cashNetThreshold = value;
                _bigTrades.Clear();
                RecalculateValues();
            }
        }

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

        [Display(GroupName = "3. Big Trade Colors", Name = "Cash Net Color", Order = 30)]
        public Color CashNetMarkerColor
        {
            get => _cashNetMarkerColor;
            set { _cashNetMarkerColor = value; RecalculateValues(); }
        }

        [Display(GroupName = "3. Big Trade Colors", Name = "Cash Net Negative Color", Order = 40)]
        public Color CashNetNegativeMarkerColor
        {
            get => _cashNetNegativeMarkerColor;
            set { _cashNetNegativeMarkerColor = value; RecalculateValues(); }
        }

        // Flow Analysis Display Settings
        private bool _showFlowAnalysis = true;
        private bool _colorCandlesByFlow = true;

        [Display(GroupName = "4. Flow Analysis", Name = "Show Flow Info", Order = 10, Description = "Mostrar análisis de Sentiment y Momentum")]
        public bool ShowFlowAnalysis
        {
            get => _showFlowAnalysis;
            set { _showFlowAnalysis = value; RedrawChart(); }
        }

        [Display(GroupName = "4. Flow Analysis", Name = "Color Candles by Flow", Order = 20, Description = "Colorear velas según Sentiment/Momentum")]
        public bool ColorCandlesByFlow
        {
            get => _colorCandlesByFlow;
            set { _colorCandlesByFlow = value; RecalculateValues(); }
        }

        // Sentiment Color Settings
        private Color _bullishMomentumColor = Color.Lime;
        private Color _bullishColor = Color.LimeGreen;
        private Color _neutralColor = Color.Yellow;
        private Color _bearishColor = Color.Orange;
        private Color _bearishMomentumColor = Color.Red;

        // Máximos observados para niveles de porcentaje
        private decimal _maxCallFlow = 0m;
        private decimal _maxPutFlow = 0m;

        [Display(GroupName = "5. Sentiment Colors", Name = "Bullish Momentum (> 0.6)", Order = 10, Description = "Color para Calls muy fuertes")]
        public Color BullishMomentumColor
        {
            get => _bullishMomentumColor;
            set { _bullishMomentumColor = value; RecalculateValues(); }
        }

        [Display(GroupName = "5. Sentiment Colors", Name = "Bullish (0.2 - 0.6)", Order = 20, Description = "Color para Calls moderados")]
        public Color BullishColor
        {
            get => _bullishColor;
            set { _bullishColor = value; RecalculateValues(); }
        }

        [Display(GroupName = "5. Sentiment Colors", Name = "Neutral (-0.2 - 0.2)", Order = 30, Description = "Color para equilibrio")]
        public Color NeutralColor
        {
            get => _neutralColor;
            set { _neutralColor = value; RecalculateValues(); }
        }

        [Display(GroupName = "5. Sentiment Colors", Name = "Bearish (-0.6 - -0.2)", Order = 40, Description = "Color para Puts moderados")]
        public Color BearishColor
        {
            get => _bearishColor;
            set { _bearishColor = value; RecalculateValues(); }
        }

        [Display(GroupName = "5. Sentiment Colors", Name = "Bearish Momentum (< -0.6)", Order = 50, Description = "Color para Puts muy fuertes")]
        public Color BearishMomentumColor
        {
            get => _bearishMomentumColor;
            set { _bearishMomentumColor = value; RecalculateValues(); }
        }

        // Sentiment Ratio Thresholds
        private decimal _sentimentBullishMomentumThreshold = 0.6m;
        private decimal _sentimentBullishThreshold = 0.2m;
        private decimal _sentimentNeutralLowThreshold = -0.2m;
        private decimal _sentimentBearishThreshold = -0.6m;

        [Display(GroupName = "6. Sentiment Thresholds", Name = "Bullish Momentum Threshold", Order = 10, Description = "Valor > para BULLISH MOMENTUM (ej: 0.6)")]
        [Range(-1.0, 1.0)]
        public decimal SentimentBullishMomentumThreshold
        {
            get => _sentimentBullishMomentumThreshold;
            set { _sentimentBullishMomentumThreshold = Math.Clamp(value, -1m, 1m); RecalculateValues(); }
        }

        [Display(GroupName = "6. Sentiment Thresholds", Name = "Bullish Threshold", Order = 20, Description = "Valor > para BULLISH (ej: 0.2)")]
        [Range(-1.0, 1.0)]
        public decimal SentimentBullishThreshold
        {
            get => _sentimentBullishThreshold;
            set { _sentimentBullishThreshold = Math.Clamp(value, -1m, 1m); RecalculateValues(); }
        }

        [Display(GroupName = "6. Sentiment Thresholds", Name = "Neutral Low Threshold", Order = 30, Description = "Valor > para NEUTRAL (ej: -0.2)")]
        [Range(-1.0, 1.0)]
        public decimal SentimentNeutralLowThreshold
        {
            get => _sentimentNeutralLowThreshold;
            set { _sentimentNeutralLowThreshold = Math.Clamp(value, -1m, 1m); RecalculateValues(); }
        }

        [Display(GroupName = "6. Sentiment Thresholds", Name = "Bearish Threshold", Order = 40, Description = "Valor > para BEARISH (ej: -0.6)")]
        [Range(-1.0, 1.0)]
        public decimal SentimentBearishThreshold
        {
            get => _sentimentBearishThreshold;
            set { _sentimentBearishThreshold = Math.Clamp(value, -1m, 1m); RecalculateValues(); }
        }

        // Ratio alert backing fields
        private decimal _ratioUpperAlert = 1.2m;
        private decimal _ratioLowerAlert = 0.8m;

        public enum InfoPanelAlignment
        {
            [Display(Name = "Left")] Left,
            [Display(Name = "Center")] Center,
            [Display(Name = "Right")] Right
        }

        // Info Panel settings
        private bool _showInfoPanel = true;
        private InfoPanelAlignment _infoPanelAlign = InfoPanelAlignment.Right;

        [Display(GroupName = "4. Flow Analysis", Name = "Show Info Panel", Order = 5, Description = "Mostrar panel informativo superior en el gráfico de precio")]
        public bool ShowInfoPanel
        {
            get => _showInfoPanel;
            set { _showInfoPanel = value; RedrawChart(); }
        }

        [Display(GroupName = "4. Flow Analysis", Name = "Info Panel Alignment", Order = 6, Description = "Alineación del panel (Izq/Centro/Der)")]
        public InfoPanelAlignment InfoPanelAlign
        {
            get => _infoPanelAlign;
            set { _infoPanelAlign = value; RedrawChart(); }
        }

        public MoneyFlow() : base(true)
        {
            DenyToChangePanel = false;
            Panel = IndicatorDataProvider.NewPanel;

            DataSeries[0] = _callFlowSeries;
            DataSeries.Add(_putFlowSeries);
            DataSeries.Add(_cashNetSeries);
            DataSeries.Add(_callFlowDeltaSeries);
            DataSeries.Add(_putFlowDeltaSeries);
            DataSeries.Add(_cashNetDeltaSeries);
            DataSeries.Add(_callLevel100);
            DataSeries.Add(_callLevel75);
            DataSeries.Add(_callLevel50);
            DataSeries.Add(_callLevel25);
            DataSeries.Add(_putLevel100);
            DataSeries.Add(_putLevel75);
            DataSeries.Add(_putLevel50);
            DataSeries.Add(_putLevel25);
            DataSeries.Add(_flowAnalysisSeries);
            DataSeries.Add(_flowDivergenceSeries);
            DataSeries.Add(_sentimentSeries);
            DataSeries.Add(_momentumSeries);
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
                    if (netDiff != 0)
                    {
                        int y = yPrice + 80;
                        Color markerColor = netDiff > 0 ? _cashNetMarkerColor : _cashNetNegativeMarkerColor;
                        DrawBigTradeMarker(context, xBar, y, netDiff, markerColor, font);
                    }
                }
            }

            // Dibujar panel informativo superior
            if (_showInfoPanel)
            {
                DrawInfoPanel(context);
            }
        }

        private void DrawBigTradeMarker(RenderContext context, int x, int y, decimal value, Color baseColor, RenderFont font)
        {
            // Convertir thresholds de M a unidades reales para el cálculo del radio
            decimal minThreshold = Math.Min(_callMoneyFlowThreshold, Math.Min(_putMoneyFlowThreshold, _cashNetThreshold)) * 1_000_000m;
            
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

        private void DrawInfoPanel(RenderContext context)
        {
            if (ChartInfo?.PriceChartContainer == null)
                return;

            int lastBar = LastVisibleBarNumber;
            if (lastBar < 0)
                return;

            // Últimos valores de las series
            decimal sentiment = _sentimentSeries.Count > lastBar ? _sentimentSeries[lastBar] : 0m;
            decimal momentum = _momentumSeries.Count > lastBar ? _momentumSeries[lastBar] : 0m;
            decimal callFlow = _callFlowSeries.Count > lastBar ? _callFlowSeries[lastBar] : 0m;
            decimal putFlow = _putFlowSeries.Count > lastBar ? _putFlowSeries[lastBar] : 0m;
            decimal cashNet = _cashNetSeries.Count > lastBar ? _cashNetSeries[lastBar] : 0m;

            // Cálculos adicionales
            decimal totalFlow = callFlow + putFlow;
            decimal callPct = totalFlow != 0 ? callFlow / totalFlow : 0m;
            decimal putPct = totalFlow != 0 ? putFlow / totalFlow : 0m;

            // Ratio Serie1/Serie2 (Call/Put)
            decimal ratio = putFlow != 0 ? callFlow / putFlow : 0m;

            // Cambio de CashNet respecto a la barra anterior visible
            decimal cashNetChange = 0m;
            if (lastBar > FirstVisibleBarNumber)
            {
                int prevBar = lastBar - 1;
                decimal prevCash = _cashNetSeries.Count > prevBar ? _cashNetSeries[prevBar] : 0m;
                cashNetChange = cashNet - prevCash;
            }

            // Texto principal (sin ratio coloreado aún)
            string baseText =
                $"Sent {sentiment:F2}    |    Mom {momentum:F2}    |    Call {FormatCompactRounded(callFlow)} ({callPct:P0})    " +
                $"Put {FormatCompactRounded(putFlow)} ({putPct:P0})    |    Net {FormatCompactRounded(cashNet)}    ΔNet {FormatCompactRounded(cashNetChange)}";

            // Indicadores de alerta por ratio
            bool highAlert = ratio >= _ratioUpperAlert && _ratioUpperAlert > 0;
            bool lowAlert = ratio <= _ratioLowerAlert && _ratioLowerAlert > 0;

            string alertText = string.Empty;
            if (highAlert)
                alertText += "    [R↑]";
            if (lowAlert)
                alertText += "    [R↓]";

            string text = baseText + alertText;

            var font = new RenderFont("Segoe UI", 10, FontStyle.Regular);
            int textWidth = EstimateTextWidth(text, font);
            int paddingH = 10;
            int panelWidth = textWidth + paddingH * 2;
            int panelHeight = 22;

            var priceRegion = ChartInfo.PriceChartContainer.Region;

            int x;
            switch (_infoPanelAlign)
            {
                case InfoPanelAlignment.Left:
                    x = priceRegion.Left + 10;
                    break;
                case InfoPanelAlignment.Center:
                    x = priceRegion.Left + (priceRegion.Width - panelWidth) / 2;
                    break;
                case InfoPanelAlignment.Right:
                default:
                    x = priceRegion.Right - panelWidth - 10;
                    break;
            }

            int y = priceRegion.Top + 8;

            var rect = new System.Drawing.Rectangle(x, y, panelWidth, panelHeight);

            // Fondo semi-transparente minimalista
            var backColor = System.Drawing.Color.FromArgb(180, 20, 20, 20);
            var borderColor = System.Drawing.Color.FromArgb(220, 80, 80, 80);
            var textColor = System.Drawing.Color.White;

            context.FillRectangle(backColor, rect);
            context.DrawRectangle(new RenderPen(borderColor, 1), rect);

            int textX = x + paddingH;
            int textY = y + (panelHeight - (int)font.Size) / 2;

            // Dibujar texto base
            context.DrawString(text, font, textColor, textX, textY);

            // Dibujar ratio al final del panel, destacado y coloreado
            string ratioLabel = $"R {ratio:F2}";
            var ratioColor = ratio >= 1m ? System.Drawing.Color.LimeGreen : System.Drawing.Color.IndianRed;
            int ratioWidth = EstimateTextWidth(ratioLabel, font);
            int ratioX = rect.Right - ratioWidth - paddingH;

            context.DrawString(ratioLabel, font, ratioColor, ratioX, textY);
        }

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

            if (closestData != null)
            {
                _callFlowSeries[bar] = closestData.CallMoneyFlow;
                _putFlowSeries[bar] = closestData.PutMoneyFlow;
                _cashNetSeries[bar] = closestData.CashNet;

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
                            _cashNetDeltaSeries[bar] = closestData.CashNet - prevDataForDelta.CashNet;

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
                        }
                    }
                }

                // Análisis Call/Put Flow
                decimal callFlow = closestData.CallMoneyFlow;
                decimal putFlow = closestData.PutMoneyFlow;
                decimal totalFlow = callFlow + putFlow;

                // Actualizar máximos para líneas de porcentaje
                if (callFlow > _maxCallFlow)
                    _maxCallFlow = callFlow;
                if (putFlow > _maxPutFlow)
                    _maxPutFlow = putFlow;

                // Setear líneas de niveles por barra
                _callLevel100[bar] = _maxCallFlow;
                _callLevel75[bar] = _maxCallFlow * 0.75m;
                _callLevel50[bar] = _maxCallFlow * 0.50m;
                _callLevel25[bar] = _maxCallFlow * 0.25m;

                _putLevel100[bar] = _maxPutFlow;
                _putLevel75[bar] = _maxPutFlow * 0.75m;
                _putLevel50[bar] = _maxPutFlow * 0.50m;
                _putLevel25[bar] = _maxPutFlow * 0.25m;
                
                // Ratio Call/Put (0-1: dominance scale)
                if (totalFlow != 0)
                {
                    _flowAnalysisSeries[bar] = callFlow / totalFlow;
                }
                
                // Divergencia (diferencia normalizada)
                _flowDivergenceSeries[bar] = callFlow - putFlow;

                // Detectar Big Trades comparando con barra anterior
                if (_showBigTradeMarkers && bar > 0)
                {
                    var prevCandle = GetCandle(bar - 1);
                    if (prevCandle != null)
                    {
                        var prevBarTimeInCsvZone = prevCandle.Time.AddHours(instrumentTimeZoneOffset).AddHours(_gmtOffset);
                        var prevData = FindClosestDataByTime(prevBarTimeInCsvZone);

                        if (prevData != null)
                        {
                            decimal callDiff = closestData.CallMoneyFlow - prevData.CallMoneyFlow;
                            decimal putDiff = closestData.PutMoneyFlow - prevData.PutMoneyFlow;
                            decimal netDiff = closestData.CashNet - prevData.CashNet;

                            // Convertir thresholds de M a unidades reales (M = 1,000,000)
                            decimal callThreshold = _callMoneyFlowThreshold * 1_000_000m;
                            decimal putThreshold = _putMoneyFlowThreshold * 1_000_000m;
                            decimal netThreshold = _cashNetThreshold * 1_000_000m;

                            // Solo considerar incrementos positivos de flujo (no reducciones)
                            bool hasCallTrade = callDiff >= callThreshold;
                            bool hasPutTrade = putDiff >= putThreshold;
                            bool hasNetTrade = netDiff >= netThreshold;

                            if (hasCallTrade || hasPutTrade || hasNetTrade)
                            {
                                _bigTrades[bar] = (
                                    hasCallTrade ? callDiff : 0,
                                    hasPutTrade ? putDiff : 0,
                                    hasNetTrade ? netDiff : 0
                                );
                            }

                            // Análisis de Sentimiento y Momentum (4 escenarios)
                            decimal callRateChange = prevData.CallMoneyFlow != 0 ? (callDiff / prevData.CallMoneyFlow) : 0;
                            decimal putRateChange = prevData.PutMoneyFlow != 0 ? (putDiff / prevData.PutMoneyFlow) : 0;

                            bool callRising = callDiff > 0;
                            bool putRising = putDiff > 0;
                            decimal momentumStrength = Math.Abs(callRateChange) + Math.Abs(putRateChange);

                            // Sentiment: -1 (bajista) a +1 (alcista)
                            decimal sentiment = (callFlow - putFlow) / (totalFlow == 0 ? 1 : totalFlow);
                            _sentimentSeries[bar] = sentiment;

                            // Momentum: velocidad de cambio
                            _momentumSeries[bar] = momentumStrength;
                        }
                    }
                }

                // Colorear vela según sentimiento si está habilitado
                if (_colorCandlesByFlow)
                {
                    decimal sentiment = _sentimentSeries[bar];
                    Color candleColor = GetSentimentColor(sentiment);
                    // Convertir System.Drawing.Color a System.Windows.Media.Color
                    var mediaColor = System.Windows.Media.Color.FromArgb(
                        candleColor.A, 
                        candleColor.R, 
                        candleColor.G, 
                        candleColor.B
                    );
                    _candleColorSeries[bar] = mediaColor;
                }

            }
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
                
                // Buscar índices de tiempo y columnas seleccionadas
                int idxTime = FindIndex(headers, "iso_time");
                if (idxTime < 0)
                    idxTime = FindIndex(headers, "timestamp_ms");

                int idxSeries1 = _series1Column != ColumnType.None ? FindIndexByColumn(headers, _series1Column) : -1;
                int idxSeries2 = _series2Column != ColumnType.None ? FindIndexByColumn(headers, _series2Column) : -1;
                int idxSeries3 = _series3Column != ColumnType.None ? FindIndexByColumn(headers, _series3Column) : -1;

                if (idxTime < 0)
                {
                    _error = "Required time column not found in CSV";
                    return;
                }

                // Al menos una serie debe estar seleccionada
                if (idxSeries1 < 0 && idxSeries2 < 0 && idxSeries3 < 0)
                {
                    _error = "Select at least one column for the series";
                    return;
                }

                for (int i = 1; i < lines.Length; i++)
                {
                    var cols = SplitCsvLine(lines[i]);
                    if (cols.Length <= idxTime)
                        continue;

                    decimal series1Val = idxSeries1 >= 0 && idxSeries1 < cols.Length && TryParseDecimal(cols[idxSeries1], out var s1) ? s1 : 0;
                    decimal series2Val = idxSeries2 >= 0 && idxSeries2 < cols.Length && TryParseDecimal(cols[idxSeries2], out var s2) ? s2 : 0;
                    decimal series3Val = idxSeries3 >= 0 && idxSeries3 < cols.Length && TryParseDecimal(cols[idxSeries3], out var s3) ? s3 : 0;

                    DateTime timestamp = DateTime.Now;
                    if (!TryParseDateTime(cols[idxTime], out timestamp))
                    {
                        if (!TryParseTimestamp(cols[idxTime], out timestamp))
                            timestamp = DateTime.Now;
                    }

                    var data = new MoneyFlowData
                    {
                        Timestamp = timestamp,
                        LastPrice = series1Val,
                        CallMoneyFlow = series1Val,
                        PutMoneyFlow = series2Val,
                        CashNet = series3Val
                    };

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

        private Color GetSentimentColor(decimal sentiment)
        {
            // Sentiment: -1 (Bajista) a +1 (Alcista)
            if (sentiment > _sentimentBullishMomentumThreshold) return _bullishMomentumColor;
            if (sentiment > _sentimentBullishThreshold) return _bullishColor;
            if (sentiment > _sentimentNeutralLowThreshold) return _neutralColor;
            if (sentiment > _sentimentBearishThreshold) return _bearishColor;
            return _bearishMomentumColor;
        }
    }
}
