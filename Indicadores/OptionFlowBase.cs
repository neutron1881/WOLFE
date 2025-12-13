using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using ATAS.Indicators;
using ATAS.Indicators.Drawing;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using CrossColor = System.Windows.Media.Color;

namespace ATAS.Indicators.Technical
{
    [DisplayName("Option Flow - Base")]
    [Category("Custom")]
    public class OptionFlowBase : Indicator
    {
        #region Enums

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
            [Display(Name = "Call Delta Flow")]
            CallDeltaFlow,
            [Display(Name = "Put Delta Flow")]
            PutDeltaFlow,
            [Display(Name = "Delta Flow Ratio")]
            DfRatio,
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
            [Display(Name = "Cash Net")]
            CashNet
        }

        private CsvDataRow? FindClosestRowBeforeOrAt(DateTime time)
        {
            lock (_sync)
            {
                if (_csvData.Count == 0) return null;
                return _csvData
                    .Where(r => r.IsoTime <= time)
                    .OrderByDescending(r => r.IsoTime)
                    .FirstOrDefault();
            }
        }

        private CsvDataRow? FindClosestRowByTime(DateTime targetTime)
        {
            lock (_sync)
            {
                if (_csvData.Count == 0) return null;

                var candidates = _csvData
                    .Where(r => Math.Abs((r.IsoTime - targetTime).TotalMinutes) <= _timeToleranceMinutes)
                    .ToList();

                if (candidates.Count == 0)
                    return null;

                return candidates.OrderBy(r => Math.Abs((r.IsoTime - targetTime).TotalMinutes)).First();
            }
        }

        private void DrawBigTradeMarker(RenderContext context, int x, int y, decimal value, Color baseColor, RenderFont font)
        {
            decimal minThreshold = Math.Min(_callMoneyFlowThreshold, Math.Min(_putMoneyFlowThreshold, _cashNetThreshold)) * 1_000_000m;
            int radius = _bigTradeBaseRadius + (int)Math.Round((double)((Math.Abs(value) - minThreshold) * _bigTradeRadiusPerUnit));
            radius = Math.Clamp(radius, _bigTradeBaseRadius, _bigTradeMaxRadius);

            var ellipseRect = new Rectangle(x - radius, y - radius, radius * 2, radius * 2);

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
                int tw = EstimateTextWidthCompact(txt, font);
                int tx = x - tw / 2;
                int ty = y - (_bigTradeFontSize / 2);
                context.DrawString(txt, font, _bigTradeTextColor, tx, ty);
            }
        }

        private static int EstimateTextWidthCompact(string text, RenderFont font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return (int)Math.Ceiling(text.Length * font.Size * 0.58);
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

        #endregion

        #region Private Fields

        private readonly object _sync = new();
        private readonly System.Timers.Timer _refreshTimer;
        private readonly List<CsvDataRow> _csvData = new();
        // Big trades storage: bar -> (callDiff, putDiff, netDiff)
        private Dictionary<int, (decimal callDiff, decimal putDiff, decimal netDiff)> _bigTrades = new();
        // Big trade display settings
        private bool _showBigTradeMarkers = true;
        private int _gmtOffset = 0;
        private int _timeToleranceMinutes = 10;
        private decimal _callMoneyFlowThreshold = 1m; // in millions
        private decimal _putMoneyFlowThreshold = 1m; // in millions
        private decimal _cashNetThreshold = 1m; // in millions
        private int _bigTradeBaseRadius = 6;
        private int _bigTradeMaxRadius = 30;
        private decimal _bigTradeRadiusPerUnit = 0.0000001m;
        private bool _bigTradeShowValue = true;
        private Color _bigTradeTextColor = Color.Black;
        private int _bigTradeFontSize = 8;
        private Color _callMoneyFlowMarkerColor = Color.DodgerBlue;
        private Color _putMoneyFlowMarkerColor = Color.IndianRed;
        private Color _cashNetMarkerColor = Color.MediumSeaGreen;
        private Color _cashNetNegativeMarkerColor = Color.Red;
        private string _error = string.Empty;
        private DateTime? _lastLoad;
        private DateTime? _currentTimestamp;

        // Valores actuales de las 4 columnas
        private decimal _currentValue1;
        private decimal _currentValue2;
        private decimal _currentValue3;
        private decimal _currentValue4;

        #endregion

        #region Data Structure

        private class CsvDataRow
        {
            public long TimestampMs { get; set; }
            public DateTime IsoTime { get; set; }
            public string Underlying { get; set; } = string.Empty;
            public DateTime Expiry { get; set; }
            public decimal LastPrice { get; set; }
            public decimal CallMoneyFlow { get; set; }
            public decimal PutMoneyFlow { get; set; }
            public decimal MfRatio { get; set; }
            public decimal CallDeltaFlow { get; set; }
            public decimal PutDeltaFlow { get; set; }
            public decimal DfRatio { get; set; }
            public decimal CallIvFlow { get; set; }
            public decimal PutIvFlow { get; set; }
            public decimal IvfRatio { get; set; }
            public decimal CallOtmImpact { get; set; }
            public decimal PutOtmImpact { get; set; }
            public decimal CallItmImpact { get; set; }
            public decimal PutItmImpact { get; set; }
            public decimal CallGex { get; set; }
            public decimal PutGex { get; set; }
            public decimal NetGex { get; set; }
            public decimal CallVannaFlow { get; set; }
            public decimal PutVannaFlow { get; set; }
            public decimal VannaRatio { get; set; }
            public decimal CallCharm { get; set; }
            public decimal PutCharm { get; set; }
            public decimal CharmPressure { get; set; }
            public decimal CallIvFlowItm { get; set; }
            public decimal PutIvFlowItm { get; set; }
            public decimal CallIvFlowOtm { get; set; }
            public decimal PutIvFlowOtm { get; set; }
            public decimal IvNet { get; set; }
            public decimal CallVolImbalance { get; set; }
            public decimal PutVolImbalance { get; set; }
            public decimal VolImbalanceRatio { get; set; }
            public decimal CallSmartMoney { get; set; }
            public decimal PutSmartMoney { get; set; }
            public decimal SmartMoneyRatio { get; set; }
            public decimal CallHedgePressure { get; set; }
            public decimal PutHedgePressure { get; set; }
            public decimal NetHedgePressure { get; set; }
            public decimal SkewPressure { get; set; }
            public decimal SkewIntensity { get; set; }
            public decimal CashNet { get; set; }
        }

        #endregion

        #region Settings

        private string _filePath = @"C:\Path\To\NDX.csv";
        [Display(GroupName = "1. Data Source", Name = "CSV File Path", Order = 10)]
        public string FilePath
        {
            get => _filePath;
            set
            {
                _filePath = value;
                ForceReload();
            }
        }

        private int _refreshSeconds = 5;
        [Display(GroupName = "1. Data Source", Name = "Refresh Interval (sec)", Order = 20)]
        [Range(1, 3600)]
        public int RefreshSeconds
        {
            get => _refreshSeconds;
            set
            {
                _refreshSeconds = Math.Max(1, value);
                ResetTimer();
            }
        }

        private bool _useLatestOnly = true;
        [Display(GroupName = "1. Data Source", Name = "Use Latest Row Only", Order = 30)]
        public bool UseLatestOnly
        {
            get => _useLatestOnly;
            set
            {
                _useLatestOnly = value;
                ForceReload();
            }
        }

        [Display(GroupName = "1. Data Source", Name = "GMT Offset (hours)", Order = 40, Description = "Corrector horario manual si es necesario (normalmente 0, ATAS usa su propia zona)")]
        [Range(-12, 12)]
        public int GmtOffset
        {
            get => _gmtOffset;
            set { _gmtOffset = Math.Clamp(value, -12, 12); }
        }

        [Display(GroupName = "1. Data Source", Name = "Time Tolerance (minutes)", Order = 50, Description = "Tolerancia para matcheo de timestamps (ej: 10 = ±10 minutos)")]
        [Range(1, 60)]
        public int TimeToleranceMinutes
        {
            get => _timeToleranceMinutes;
            set { _timeToleranceMinutes = Math.Clamp(value, 1, 60); }
        }

        #endregion

        // Big Trade settings exposed to UI
        [Display(GroupName = "2. Big Trade Filters", Name = "Show Big Trade Markers", Order = 10)]
        public bool ShowBigTradeMarkers
        {
            get => _showBigTradeMarkers;
            set { _showBigTradeMarkers = value; RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "Call Money Flow Threshold (M)", Order = 20, Description = "En millones (ej: 1 = 1M)")]
        [Range(0.01, 100)]
        public decimal CallMoneyFlowThreshold
        {
            get => _callMoneyFlowThreshold;
            set { _callMoneyFlowThreshold = Math.Clamp(value, 0.01m, 100m); RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "Put Money Flow Threshold (M)", Order = 30, Description = "En millones (ej: 1 = 1M)")]
        [Range(0.01, 100)]
        public decimal PutMoneyFlowThreshold
        {
            get => _putMoneyFlowThreshold;
            set { _putMoneyFlowThreshold = Math.Clamp(value, 0.01m, 100m); RecalculateValues(); }
        }

        [Display(GroupName = "2. Big Trade Filters", Name = "Cash Net Threshold (M)", Order = 40, Description = "En millones (ej: 1 = 1M)")]
        [Range(0.01, 100)]
        public decimal CashNetThreshold
        {
            get => _cashNetThreshold;
            set { _cashNetThreshold = Math.Clamp(value, 0.01m, 100m); RecalculateValues(); }
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

        #region Series 1 Settings

        private bool _showSeries1 = true;
        [Display(GroupName = "2. Series 1", Name = "Show Series 1", Order = 10)]
        public bool ShowSeries1
        {
            get => _showSeries1;
            set
            {
                _showSeries1 = value;
                UpdateSeriesVisibility(0, value);
                RecalculateValues();
            }
        }

        private ColumnType _selectedColumn1 = ColumnType.NetGex;
        [Display(GroupName = "2. Series 1", Name = "Column", Order = 20)]
        public ColumnType SelectedColumn1
        {
            get => _selectedColumn1;
            set
            {
                _selectedColumn1 = value;
                UpdateSeriesName(0);
                ForceReload();
            }
        }

        private string _customName1 = "";
        [Display(GroupName = "2. Series 1", Name = "Custom Name", Order = 30)]
        public string CustomName1
        {
            get => _customName1;
            set
            {
                _customName1 = value ?? "";
                UpdateSeriesName(0);
                RecalculateValues();
            }
        }

        private Color _color1 = Color.LimeGreen;
        [Display(GroupName = "2. Series 1", Name = "Color", Order = 40)]
        public Color Color1
        {
            get => _color1;
            set
            {
                _color1 = value;
                ((ValueDataSeries)DataSeries[0]).Color = CrossColor.FromRgb(value.R, value.G, value.B);
                RecalculateValues();
            }
        }

        private int _lineWidth1 = 2;
        [Display(GroupName = "2. Series 1", Name = "Line Width", Order = 50)]
        [Range(1, 10)]
        public int LineWidth1
        {
            get => _lineWidth1;
            set
            {
                _lineWidth1 = Math.Clamp(value, 1, 10);
                ((ValueDataSeries)DataSeries[0]).Width = _lineWidth1;
                RecalculateValues();
            }
        }

        private bool _showAsHistogram1 = true;
        [Display(GroupName = "2. Series 1", Name = "Show as Histogram", Order = 60)]
        public bool ShowAsHistogram1
        {
            get => _showAsHistogram1;
            set
            {
                _showAsHistogram1 = value;
                if (_showSeries1)
                    ((ValueDataSeries)DataSeries[0]).VisualType = value ? VisualMode.Histogram : VisualMode.Line;
                RecalculateValues();
            }
        }

        #endregion

        #region Series 2 Settings

        private bool _showSeries2 = true;
        [Display(GroupName = "3. Series 2", Name = "Show Series 2", Order = 10)]
        public bool ShowSeries2
        {
            get => _showSeries2;
            set
            {
                _showSeries2 = value;
                UpdateSeriesVisibility(1, value);
                RecalculateValues();
            }
        }

        private ColumnType _selectedColumn2 = ColumnType.CallGex;
        [Display(GroupName = "3. Series 2", Name = "Column", Order = 20)]
        public ColumnType SelectedColumn2
        {
            get => _selectedColumn2;
            set
            {
                _selectedColumn2 = value;
                UpdateSeriesName(1);
                ForceReload();
            }
        }

        private string _customName2 = "";
        [Display(GroupName = "3. Series 2", Name = "Custom Name", Order = 30)]
        public string CustomName2
        {
            get => _customName2;
            set
            {
                _customName2 = value ?? "";
                UpdateSeriesName(1);
                RecalculateValues();
            }
        }

        private Color _color2 = Color.Red;
        [Display(GroupName = "3. Series 2", Name = "Color", Order = 40)]
        public Color Color2
        {
            get => _color2;
            set
            {
                _color2 = value;
                ((ValueDataSeries)DataSeries[1]).Color = CrossColor.FromRgb(value.R, value.G, value.B);
                RecalculateValues();
            }
        }

        private int _lineWidth2 = 2;
        [Display(GroupName = "3. Series 2", Name = "Line Width", Order = 50)]
        [Range(1, 10)]
        public int LineWidth2
        {
            get => _lineWidth2;
            set
            {
                _lineWidth2 = Math.Clamp(value, 1, 10);
                ((ValueDataSeries)DataSeries[1]).Width = _lineWidth2;
                RecalculateValues();
            }
        }

        private bool _showAsHistogram2 = true;
        [Display(GroupName = "3. Series 2", Name = "Show as Histogram", Order = 60)]
        public bool ShowAsHistogram2
        {
            get => _showAsHistogram2;
            set
            {
                _showAsHistogram2 = value;
                if (_showSeries2)
                    ((ValueDataSeries)DataSeries[1]).VisualType = value ? VisualMode.Histogram : VisualMode.Line;
                RecalculateValues();
            }
        }

        #endregion

        #region Series 3 Settings

        private bool _showSeries3 = false;
        [Display(GroupName = "4. Series 3", Name = "Show Series 3", Order = 10)]
        public bool ShowSeries3
        {
            get => _showSeries3;
            set
            {
                _showSeries3 = value;
                UpdateSeriesVisibility(2, value);
                RecalculateValues();
            }
        }

        private ColumnType _selectedColumn3 = ColumnType.PutGex;
        [Display(GroupName = "4. Series 3", Name = "Column", Order = 20)]
        public ColumnType SelectedColumn3
        {
            get => _selectedColumn3;
            set
            {
                _selectedColumn3 = value;
                UpdateSeriesName(2);
                ForceReload();
            }
        }

        private string _customName3 = "";
        [Display(GroupName = "4. Series 3", Name = "Custom Name", Order = 30)]
        public string CustomName3
        {
            get => _customName3;
            set
            {
                _customName3 = value ?? "";
                UpdateSeriesName(2);
                RecalculateValues();
            }
        }

        private Color _color3 = Color.Yellow;
        [Display(GroupName = "4. Series 3", Name = "Color", Order = 40)]
        public Color Color3
        {
            get => _color3;
            set
            {
                _color3 = value;
                ((ValueDataSeries)DataSeries[2]).Color = CrossColor.FromRgb(value.R, value.G, value.B);
                RecalculateValues();
            }
        }

        private int _lineWidth3 = 2;
        [Display(GroupName = "4. Series 3", Name = "Line Width", Order = 50)]
        [Range(1, 10)]
        public int LineWidth3
        {
            get => _lineWidth3;
            set
            {
                _lineWidth3 = Math.Clamp(value, 1, 10);
                ((ValueDataSeries)DataSeries[2]).Width = _lineWidth3;
                RecalculateValues();
            }
        }

        private bool _showAsHistogram3 = false;
        [Display(GroupName = "4. Series 3", Name = "Show as Histogram", Order = 60)]
        public bool ShowAsHistogram3
        {
            get => _showAsHistogram3;
            set
            {
                _showAsHistogram3 = value;
                if (_showSeries3)
                    ((ValueDataSeries)DataSeries[2]).VisualType = value ? VisualMode.Histogram : VisualMode.Line;
                RecalculateValues();
            }
        }

        #endregion

        #region Series 4 Settings

        private bool _showSeries4 = false;
        [Display(GroupName = "5. Series 4", Name = "Show Series 4", Order = 10)]
        public bool ShowSeries4
        {
            get => _showSeries4;
            set
            {
                _showSeries4 = value;
                UpdateSeriesVisibility(3, value);
                RecalculateValues();
            }
        }

        private ColumnType _selectedColumn4 = ColumnType.None;
        [Display(GroupName = "5. Series 4", Name = "Column", Order = 20)]
        public ColumnType SelectedColumn4
        {
            get => _selectedColumn4;
            set
            {
                _selectedColumn4 = value;
                UpdateSeriesName(3);
                ForceReload();
            }
        }

        private string _customName4 = "";
        [Display(GroupName = "5. Series 4", Name = "Custom Name", Order = 30)]
        public string CustomName4
        {
            get => _customName4;
            set
            {
                _customName4 = value ?? "";
                UpdateSeriesName(3);
                RecalculateValues();
            }
        }

        private Color _color4 = Color.Cyan;
        [Display(GroupName = "5. Series 4", Name = "Color", Order = 40)]
        public Color Color4
        {
            get => _color4;
            set
            {
                _color4 = value;
                ((ValueDataSeries)DataSeries[3]).Color = CrossColor.FromRgb(value.R, value.G, value.B);
                RecalculateValues();
            }
        }

        private int _lineWidth4 = 2;
        [Display(GroupName = "5. Series 4", Name = "Line Width", Order = 50)]
        [Range(1, 10)]
        public int LineWidth4
        {
            get => _lineWidth4;
            set
            {
                _lineWidth4 = Math.Clamp(value, 1, 10);
                ((ValueDataSeries)DataSeries[3]).Width = _lineWidth4;
                RecalculateValues();
            }
        }

        private bool _showAsHistogram4 = false;
        [Display(GroupName = "5. Series 4", Name = "Show as Histogram", Order = 60)]
        public bool ShowAsHistogram4
        {
            get => _showAsHistogram4;
            set
            {
                _showAsHistogram4 = value;
                if (_showSeries4)
                    ((ValueDataSeries)DataSeries[3]).VisualType = value ? VisualMode.Histogram : VisualMode.Line;
                RecalculateValues();
            }
        }

        #endregion

        #region Zero Line Settings

        private bool _showZeroLine = true;
        [Display(GroupName = "6. Zero Line", Name = "Show Zero Line", Order = 10)]
        public bool ShowZeroLine
        {
            get => _showZeroLine;
            set
            {
                _showZeroLine = value;
                ((ValueDataSeries)DataSeries[4]).VisualType = value ? VisualMode.Line : VisualMode.Hide;
                RecalculateValues();
            }
        }

        private Color _zeroLineColor = Color.Gray;
        [Display(GroupName = "6. Zero Line", Name = "Color", Order = 20)]
        public Color ZeroLineColor
        {
            get => _zeroLineColor;
            set
            {
                _zeroLineColor = value;
                ((ValueDataSeries)DataSeries[4]).Color = CrossColor.FromRgb(value.R, value.G, value.B);
                RecalculateValues();
            }
        }

        #endregion

        #region Info Panel Settings

        private bool _showInfoPanel = true;
        [Display(GroupName = "7. Info Panel", Name = "Show Info Panel", Order = 10)]
        public bool ShowInfoPanel
        {
            get => _showInfoPanel;
            set
            {
                _showInfoPanel = value;
                RecalculateValues();
            }
        }

        private bool _showLegend = true;
        [Display(GroupName = "7. Info Panel", Name = "Show Legend", Order = 15)]
        public bool ShowLegend
        {
            get => _showLegend;
            set
            {
                _showLegend = value;
                RecalculateValues();
            }
        }

        private int _legendFontSize = 9;
        [Display(GroupName = "7. Info Panel", Name = "Legend Font Size", Order = 16)]
        [Range(6, 20)]
        public int LegendFontSize
        {
            get => _legendFontSize;
            set
            {
                _legendFontSize = Math.Clamp(value, 6, 20);
                RecalculateValues();
            }
        }

        private int _legendSpacing = 15;
        [Display(GroupName = "7. Info Panel", Name = "Legend Spacing (px)", Order = 17)]
        [Range(5, 50)]
        public int LegendSpacing
        {
            get => _legendSpacing;
            set
            {
                _legendSpacing = Math.Clamp(value, 5, 50);
                RecalculateValues();
            }
        }

        private int _legendOffsetY = 5;
        [Display(GroupName = "7. Info Panel", Name = "Legend Vertical Offset (px)", Order = 18)]
        [Range(0, 200)]
        public int LegendOffsetY
        {
            get => _legendOffsetY;
            set
            {
                _legendOffsetY = Math.Clamp(value, 0, 200);
                RecalculateValues();
            }
        }

        private int _infoPanelFontSize = 10;
        [Display(GroupName = "7. Info Panel", Name = "Font Size", Order = 20)]
        [Range(8, 24)]
        public int InfoPanelFontSize
        {
            get => _infoPanelFontSize;
            set
            {
                _infoPanelFontSize = Math.Clamp(value, 8, 24);
                RecalculateValues();
            }
        }

        private Color _infoPanelTextColor = Color.White;
        [Display(GroupName = "7. Info Panel", Name = "Text Color", Order = 30)]
        public Color InfoPanelTextColor
        {
            get => _infoPanelTextColor;
            set
            {
                _infoPanelTextColor = value;
                RecalculateValues();
            }
        }

        #endregion

        #region Constructor

        public OptionFlowBase()
        {
            // Crear nuevo panel debajo del gráfico de precios
            Panel = IndicatorDataProvider.NewPanel;

            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);

            // Serie 0: Series 1
            ((ValueDataSeries)DataSeries[0]).Name = "Series 1";
            ((ValueDataSeries)DataSeries[0]).Color = CrossColor.FromRgb(50, 205, 50); // LimeGreen
            ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Histogram;
            ((ValueDataSeries)DataSeries[0]).Width = 2;

            // Serie 1: Series 2
            DataSeries.Add(new ValueDataSeries("Series2", "Series 2")
            {
                Color = CrossColor.FromRgb(255, 0, 0), // Red
                VisualType = VisualMode.Histogram,
                Width = 2
            });

            // Serie 2: Series 3
            DataSeries.Add(new ValueDataSeries("Series3", "Series 3")
            {
                Color = CrossColor.FromRgb(255, 255, 0), // Yellow
                VisualType = VisualMode.Hide,
                Width = 2
            });

            // Serie 3: Series 4
            DataSeries.Add(new ValueDataSeries("Series4", "Series 4")
            {
                Color = CrossColor.FromRgb(0, 255, 255), // Cyan
                VisualType = VisualMode.Hide,
                Width = 2
            });

            // Serie 4: Línea de cero
            DataSeries.Add(new ValueDataSeries("ZeroLine", "Zero")
            {
                Color = CrossColor.FromRgb(128, 128, 128), // Gray
                VisualType = VisualMode.Line,
                Width = 1
            });

            _refreshTimer = new System.Timers.Timer(5000) { AutoReset = true };
            _refreshTimer.Elapsed += OnRefreshTimer;
        }

        #endregion

        #region Indicator Methods

        protected override void OnInitialize()
        {
            ResetTimer();
            LoadCsvData();
            
            // Inicializar nombres de series
            UpdateSeriesName(0);
            UpdateSeriesName(1);
            UpdateSeriesName(2);
            UpdateSeriesName(3);
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            // Línea de cero siempre en 0
            DataSeries[4][bar] = 0m;

            decimal value1, value2, value3, value4;

            if (bar == CurrentBar - 1)
            {
                // Asignar los valores actuales a la última barra
                lock (_sync)
                {
                    value1 = _currentValue1;
                    value2 = _currentValue2;
                    value3 = _currentValue3;
                    value4 = _currentValue4;
                }
            }
            else
            {
                // Para barras históricas, buscar datos por timestamp si están disponibles
                var candle = GetCandle(bar);
                if (candle != null)
                {
                    // Ajustar hora de la vela a la zona horaria del CSV usando zona del instrumento + offset manual
                    int instrumentTimeZoneOffset = InstrumentInfo?.TimeZone ?? 0;
                    var barTimeInCsvZone = candle.Time.AddHours(instrumentTimeZoneOffset).AddHours(_gmtOffset);
                    GetValuesForTime(barTimeInCsvZone, out value1, out value2, out value3, out value4);
                }
                else
                {
                    value1 = 0m;
                    value2 = 0m;
                    value3 = 0m;
                    value4 = 0m;
                }
            }

            // Asignar valores a las series
            DataSeries[0][bar] = value1;
            DataSeries[1][bar] = value2;
            DataSeries[2][bar] = value3;
            DataSeries[3][bar] = value4;

            // Detectar Big Trades comparando con barra anterior
            if (_showBigTradeMarkers && bar > 0)
            {
                var candle = GetCandle(bar);
                var prevCandle = GetCandle(bar - 1);
                if (candle != null && prevCandle != null)
                {
                    int instrumentTimeZoneOffset = InstrumentInfo?.TimeZone ?? 0;
                    var curBarTimeInCsvZone = candle.Time.AddHours(instrumentTimeZoneOffset).AddHours(_gmtOffset);
                    var prevBarTimeInCsvZone = prevCandle.Time.AddHours(instrumentTimeZoneOffset).AddHours(_gmtOffset);

                    var curRow = FindClosestRowByTime(curBarTimeInCsvZone);
                    var prevRow = FindClosestRowByTime(prevBarTimeInCsvZone);

                    if (curRow != null && prevRow != null)
                    {
                        decimal callDiff = curRow.CallMoneyFlow - prevRow.CallMoneyFlow;
                        decimal putDiff = curRow.PutMoneyFlow - prevRow.PutMoneyFlow;
                        decimal netDiff = curRow.CashNet - prevRow.CashNet;

                        decimal callThreshold = _callMoneyFlowThreshold * 1_000_000m;
                        decimal putThreshold = _putMoneyFlowThreshold * 1_000_000m;
                        decimal netThreshold = _cashNetThreshold * 1_000_000m;

                        bool hasCallTrade = Math.Abs(callDiff) >= callThreshold && callDiff != 0;
                        bool hasPutTrade = Math.Abs(putDiff) >= putThreshold && putDiff != 0;
                        bool hasNetTrade = Math.Abs(netDiff) >= netThreshold && netDiff != 0;

                        if (hasCallTrade || hasPutTrade || hasNetTrade)
                        {
                            _bigTrades[bar] = (
                                hasCallTrade ? callDiff : 0,
                                hasPutTrade ? putDiff : 0,
                                hasNetTrade ? netDiff : 0
                            );
                        }
                    }
                }
            }
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo?.PriceChartContainer == null)
                return;
            // Dibujar Big Trade Markers sobre el gráfico de precios
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

                    int yPrice = ChartInfo.PriceChartContainer.GetYByPrice(candle.Low, false);

                    if (callDiff != 0)
                    {
                        int y = yPrice + 20;
                        DrawBigTradeMarker(context, xBar, y, callDiff, _callMoneyFlowMarkerColor, font);
                    }

                    if (putDiff != 0)
                    {
                        int y = yPrice + 50;
                        DrawBigTradeMarker(context, xBar, y, putDiff, _putMoneyFlowMarkerColor, font);
                    }

                    if (netDiff != 0)
                    {
                        int y = yPrice + 80;
                        Color markerColor = netDiff > 0 ? _cashNetMarkerColor : _cashNetNegativeMarkerColor;
                        DrawBigTradeMarker(context, xBar, y, netDiff, markerColor, font);
                    }
                }
            }


            // Mostrar errores si existen
            if (!string.IsNullOrEmpty(_error))
            {
                var errorFont = new RenderFont("Arial", 10);
                context.DrawString(_error, errorFont, Color.Red, 10, 10);
            }

            // Leyenda en la parte superior del panel
            if (_showLegend)
            {
                DrawLegend(context);
            }

            // Panel de información
            if (_showInfoPanel)
            {
                DrawInfoPanel(context);
            }
        }

        protected override void OnDispose()
        {
            try
            {
                _refreshTimer.Stop();
                _refreshTimer.Elapsed -= OnRefreshTimer;
                _refreshTimer.Dispose();
            }
            catch { }
            base.OnDispose();
        }

        #endregion

        #region Private Methods

        private void UpdateSeriesVisibility(int seriesIndex, bool visible)
        {
            if (seriesIndex < 0 || seriesIndex >= 4) return;

            var showAsHistogram = seriesIndex switch
            {
                0 => _showAsHistogram1,
                1 => _showAsHistogram2,
                2 => _showAsHistogram3,
                3 => _showAsHistogram4,
                _ => false
            };

            ((ValueDataSeries)DataSeries[seriesIndex]).VisualType = visible
                ? (showAsHistogram ? VisualMode.Histogram : VisualMode.Line)
                : VisualMode.Hide;
        }

        private void UpdateSeriesName(int seriesIndex)
        {
            if (seriesIndex < 0 || seriesIndex >= 4) return;

            var customName = seriesIndex switch
            {
                0 => _customName1,
                1 => _customName2,
                2 => _customName3,
                3 => _customName4,
                _ => ""
            };

            var column = seriesIndex switch
            {
                0 => _selectedColumn1,
                1 => _selectedColumn2,
                2 => _selectedColumn3,
                3 => _selectedColumn4,
                _ => ColumnType.None
            };

            string displayName;
            if (!string.IsNullOrWhiteSpace(customName))
            {
                displayName = customName;
            }
            else
            {
                displayName = GetColumnDisplayName(column);
            }

            ((ValueDataSeries)DataSeries[seriesIndex]).Name = displayName;
        }

        private string GetColumnDisplayName(ColumnType column)
        {
            var memberInfo = typeof(ColumnType).GetMember(column.ToString()).FirstOrDefault();
            if (memberInfo != null)
            {
                var attr = memberInfo.GetCustomAttributes(typeof(DisplayAttribute), false).FirstOrDefault() as DisplayAttribute;
                if (attr != null)
                    return attr.Name ?? column.ToString();
            }
            return column.ToString();
        }

        private string GetSeriesDisplayName(int seriesIndex)
        {
            var customName = seriesIndex switch
            {
                0 => _customName1,
                1 => _customName2,
                2 => _customName3,
                3 => _customName4,
                _ => ""
            };

            if (!string.IsNullOrWhiteSpace(customName))
                return customName;

            var column = seriesIndex switch
            {
                0 => _selectedColumn1,
                1 => _selectedColumn2,
                2 => _selectedColumn3,
                3 => _selectedColumn4,
                _ => ColumnType.None
            };

            return GetColumnDisplayName(column);
        }

        private void ResetTimer()
        {
            _refreshTimer.Stop();
            _refreshTimer.Interval = Math.Max(1, _refreshSeconds) * 1000;
            _refreshTimer.Start();
        }

        private void OnRefreshTimer(object? sender, System.Timers.ElapsedEventArgs e)
        {
            LoadCsvData();
            RecalculateValues();
        }

        private void ForceReload()
        {
            LoadCsvData();
            RecalculateValues();
        }

        private void LoadCsvData()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_filePath) || !File.Exists(_filePath))
                {
                    _error = $"CSV not found: {_filePath}";
                    return;
                }

                var lines = File.ReadAllLines(_filePath);
                if (lines.Length < 2)
                {
                    _error = "CSV is empty or has no data rows";
                    return;
                }

                var headers = SplitCsvLine(lines[0]);
                var columnIndices = MapColumnIndices(headers);

                var newData = new List<CsvDataRow>();

                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var cols = SplitCsvLine(line);
                    var row = ParseRow(cols, columnIndices);
                    if (row != null)
                        newData.Add(row);
                }

                lock (_sync)
                {
                    _csvData.Clear();
                    _csvData.AddRange(newData);

                    // Clear big trades cache when loading new CSV
                    _bigTrades.Clear();

                    if (_csvData.Count > 0)
                    {
                        CsvDataRow latestRow;
                        if (_useLatestOnly)
                            latestRow = _csvData.OrderByDescending(r => r.TimestampMs).First();
                        else
                            latestRow = _csvData.Last();

                        _currentValue1 = GetColumnValue(latestRow, _selectedColumn1);
                        _currentValue2 = GetColumnValue(latestRow, _selectedColumn2);
                        _currentValue3 = GetColumnValue(latestRow, _selectedColumn3);
                        _currentValue4 = GetColumnValue(latestRow, _selectedColumn4);
                        _currentTimestamp = latestRow.IsoTime;
                    }
                }

                _error = string.Empty;
                _lastLoad = DateTime.Now;
            }
            catch (Exception ex)
            {
                _error = $"Load error: {ex.Message}";
            }
        }

        private void GetValuesForTime(DateTime time, out decimal value1, out decimal value2, out decimal value3, out decimal value4)
        {
            lock (_sync)
            {
                if (_csvData.Count == 0)
                {
                    value1 = 0m;
                    value2 = 0m;
                    value3 = 0m;
                    value4 = 0m;
                    return;
                }

                // Buscar la fila más cercana al tiempo dado
                var closest = _csvData
                    .Where(r => r.IsoTime <= time)
                    .OrderByDescending(r => r.IsoTime)
                    .FirstOrDefault();

                if (closest != null)
                {
                    value1 = GetColumnValue(closest, _selectedColumn1);
                    value2 = GetColumnValue(closest, _selectedColumn2);
                    value3 = GetColumnValue(closest, _selectedColumn3);
                    value4 = GetColumnValue(closest, _selectedColumn4);
                }
                else
                {
                    // Si no hay datos anteriores, usar el primero disponible
                    var first = _csvData.First();
                    value1 = GetColumnValue(first, _selectedColumn1);
                    value2 = GetColumnValue(first, _selectedColumn2);
                    value3 = GetColumnValue(first, _selectedColumn3);
                    value4 = GetColumnValue(first, _selectedColumn4);
                }
            }
        }

        private static Dictionary<string, int> MapColumnIndices(string[] headers)
        {
            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
            {
                var h = headers[i].Trim().ToLowerInvariant().Replace(" ", "_");
                dict[h] = i;
            }
            return dict;
        }

        private static CsvDataRow? ParseRow(string[] cols, Dictionary<string, int> indices)
        {
            var row = new CsvDataRow();

            if (indices.TryGetValue("timestamp_ms", out var idx) && idx < cols.Length)
            {
                if (long.TryParse(cols[idx], out var ts))
                    row.TimestampMs = ts;
            }

            if (indices.TryGetValue("iso_time", out idx) && idx < cols.Length)
            {
                if (DateTime.TryParse(cols[idx], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var isoTime))
                    row.IsoTime = isoTime;
            }

            if (indices.TryGetValue("underlying", out idx) && idx < cols.Length)
                row.Underlying = cols[idx];

            if (indices.TryGetValue("expiry", out idx) && idx < cols.Length)
            {
                if (DateTime.TryParse(cols[idx], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var expiry))
                    row.Expiry = expiry;
            }

            row.LastPrice = GetDecimal(cols, indices, "last_price");
            row.CallMoneyFlow = GetDecimal(cols, indices, "call_money_flow");
            row.PutMoneyFlow = GetDecimal(cols, indices, "put_money_flow");
            row.MfRatio = GetDecimal(cols, indices, "mf_ratio");
            row.CallDeltaFlow = GetDecimal(cols, indices, "call_delta_flow");
            row.PutDeltaFlow = GetDecimal(cols, indices, "put_delta_flow");
            row.DfRatio = GetDecimal(cols, indices, "df_ratio");
            row.CallIvFlow = GetDecimal(cols, indices, "call_iv_flow");
            row.PutIvFlow = GetDecimal(cols, indices, "put_iv_flow");
            row.IvfRatio = GetDecimal(cols, indices, "ivf_ratio");
            row.CallOtmImpact = GetDecimal(cols, indices, "call_otm_impact");
            row.PutOtmImpact = GetDecimal(cols, indices, "put_otm_impact");
            row.CallItmImpact = GetDecimal(cols, indices, "call_itm_impact");
            row.PutItmImpact = GetDecimal(cols, indices, "put_itm_impact");
            row.CallGex = GetDecimal(cols, indices, "call_gex");
            row.PutGex = GetDecimal(cols, indices, "put_gex");
            row.NetGex = GetDecimal(cols, indices, "net_gex");
            row.CallVannaFlow = GetDecimal(cols, indices, "call_vanna_flow");
            row.PutVannaFlow = GetDecimal(cols, indices, "put_vanna_flow");
            row.VannaRatio = GetDecimal(cols, indices, "vanna_ratio");
            row.CallCharm = GetDecimal(cols, indices, "call_charm");
            row.PutCharm = GetDecimal(cols, indices, "put_charm");
            row.CharmPressure = GetDecimal(cols, indices, "charm_pressure");
            row.CallIvFlowItm = GetDecimal(cols, indices, "call_iv_flow_itm");
            row.PutIvFlowItm = GetDecimal(cols, indices, "put_iv_flow_itm");
            row.CallIvFlowOtm = GetDecimal(cols, indices, "call_iv_flow_otm");
            row.PutIvFlowOtm = GetDecimal(cols, indices, "put_iv_flow_otm");
            row.IvNet = GetDecimal(cols, indices, "iv_net");
            row.CallVolImbalance = GetDecimal(cols, indices, "call_vol_imbalance");
            row.PutVolImbalance = GetDecimal(cols, indices, "put_vol_imbalance");
            row.VolImbalanceRatio = GetDecimal(cols, indices, "vol_imbalance_ratio");
            row.CallSmartMoney = GetDecimal(cols, indices, "call_smart_money");
            row.PutSmartMoney = GetDecimal(cols, indices, "put_smart_money");
            row.SmartMoneyRatio = GetDecimal(cols, indices, "smart_money_ratio");
            row.CallHedgePressure = GetDecimal(cols, indices, "call_hedge_pressure");
            row.PutHedgePressure = GetDecimal(cols, indices, "put_hedge_pressure");
            row.NetHedgePressure = GetDecimal(cols, indices, "net_hedge_pressure");
            row.SkewPressure = GetDecimal(cols, indices, "skew_pressure");
            row.SkewIntensity = GetDecimal(cols, indices, "skew_intensity");
            row.CashNet = GetDecimal(cols, indices, "cash_net");

            return row;
        }

        private static decimal GetDecimal(string[] cols, Dictionary<string, int> indices, string key)
        {
            if (indices.TryGetValue(key, out var idx) && idx < cols.Length)
            {
                if (TryParseDecimal(cols[idx], out var val))
                    return val;
            }
            return 0m;
        }

        private static decimal GetColumnValue(CsvDataRow row, ColumnType column)
        {
            return column switch
            {
                ColumnType.None => 0m,
                ColumnType.LastPrice => row.LastPrice,
                ColumnType.CallMoneyFlow => row.CallMoneyFlow,
                ColumnType.PutMoneyFlow => row.PutMoneyFlow,
                ColumnType.MfRatio => row.MfRatio,
                ColumnType.CallDeltaFlow => row.CallDeltaFlow,
                ColumnType.PutDeltaFlow => row.PutDeltaFlow,
                ColumnType.DfRatio => row.DfRatio,
                ColumnType.CallIvFlow => row.CallIvFlow,
                ColumnType.PutIvFlow => row.PutIvFlow,
                ColumnType.IvfRatio => row.IvfRatio,
                ColumnType.CallOtmImpact => row.CallOtmImpact,
                ColumnType.PutOtmImpact => row.PutOtmImpact,
                ColumnType.CallItmImpact => row.CallItmImpact,
                ColumnType.PutItmImpact => row.PutItmImpact,
                ColumnType.CallGex => row.CallGex,
                ColumnType.PutGex => row.PutGex,
                ColumnType.NetGex => row.NetGex,
                ColumnType.CallVannaFlow => row.CallVannaFlow,
                ColumnType.PutVannaFlow => row.PutVannaFlow,
                ColumnType.VannaRatio => row.VannaRatio,
                ColumnType.CallCharm => row.CallCharm,
                ColumnType.PutCharm => row.PutCharm,
                ColumnType.CharmPressure => row.CharmPressure,
                ColumnType.CallIvFlowItm => row.CallIvFlowItm,
                ColumnType.PutIvFlowItm => row.PutIvFlowItm,
                ColumnType.CallIvFlowOtm => row.CallIvFlowOtm,
                ColumnType.PutIvFlowOtm => row.PutIvFlowOtm,
                ColumnType.IvNet => row.IvNet,
                ColumnType.CallVolImbalance => row.CallVolImbalance,
                ColumnType.PutVolImbalance => row.PutVolImbalance,
                ColumnType.VolImbalanceRatio => row.VolImbalanceRatio,
                ColumnType.CallSmartMoney => row.CallSmartMoney,
                ColumnType.PutSmartMoney => row.PutSmartMoney,
                ColumnType.SmartMoneyRatio => row.SmartMoneyRatio,
                ColumnType.CallHedgePressure => row.CallHedgePressure,
                ColumnType.PutHedgePressure => row.PutHedgePressure,
                ColumnType.NetHedgePressure => row.NetHedgePressure,
                ColumnType.SkewPressure => row.SkewPressure,
                ColumnType.SkewIntensity => row.SkewIntensity,
                ColumnType.CashNet => row.CashNet,
                _ => 0m
            };
        }

        private void DrawLegend(RenderContext context)
        {
            var font = new RenderFont("Arial", _legendFontSize);
            int x = 10;
            int y = _legendOffsetY;
            int boxSize = _legendFontSize;
            int spacing = _legendSpacing;

            // Construir lista de leyendas activas
            var legends = new List<(string name, Color color, decimal value)>();

            decimal val1, val2, val3, val4;
            lock (_sync)
            {
                val1 = _currentValue1;
                val2 = _currentValue2;
                val3 = _currentValue3;
                val4 = _currentValue4;
            }

            if (_showSeries1 && _selectedColumn1 != ColumnType.None)
                legends.Add((GetSeriesDisplayName(0), _color1, val1));

            if (_showSeries2 && _selectedColumn2 != ColumnType.None)
                legends.Add((GetSeriesDisplayName(1), _color2, val2));

            if (_showSeries3 && _selectedColumn3 != ColumnType.None)
                legends.Add((GetSeriesDisplayName(2), _color3, val3));

            if (_showSeries4 && _selectedColumn4 != ColumnType.None)
                legends.Add((GetSeriesDisplayName(3), _color4, val4));

            // Dibujar cada leyenda horizontalmente
            foreach (var legend in legends)
            {
                // Cuadro de color
                var colorBox = new Rectangle(x, y + 2, boxSize, boxSize);
                context.FillRectangle(legend.color, colorBox);

                // Nombre y valor
                string text = $"{legend.name}: {FormatValue(legend.value)}";
                int textX = x + boxSize + 4;
                context.DrawString(text, font, legend.color, textX, y);

                // Calcular ancho para la siguiente leyenda
                int textWidth = EstimateTextWidthCompact(text, font);
                x = textX + textWidth + spacing;
            }
        }

        private void DrawInfoPanel(RenderContext context)
        {
            var font = new RenderFont("Arial", _infoPanelFontSize);
            int x = 10;
            int y = _showLegend ? _legendOffsetY + _legendFontSize + 10 : 10; // Ajustar posición si hay leyenda
            int lineHeight = _infoPanelFontSize + 4;

            // Valores actuales
            decimal val1, val2, val3, val4;
            DateTime? currentTs;
            lock (_sync)
            {
                val1 = _currentValue1;
                val2 = _currentValue2;
                val3 = _currentValue3;
                val4 = _currentValue4;
                currentTs = _currentTimestamp;
            }

            // Si la leyenda está activa, solo mostrar timestamp y última actualización
            if (!_showLegend)
            {
                // Series 1
                if (_showSeries1 && _selectedColumn1 != ColumnType.None)
                {
                    var name = GetSeriesDisplayName(0);
                    context.DrawString($"{name}: ", font, _infoPanelTextColor, x, y);
                    var labelWidth = EstimateTextWidthCompact($"{name}: ", font);
                    context.DrawString(FormatValue(val1), font, _color1, x + labelWidth, y);
                    y += lineHeight;
                }

                // Series 2
                if (_showSeries2 && _selectedColumn2 != ColumnType.None)
                {
                    var name = GetSeriesDisplayName(1);
                    context.DrawString($"{name}: ", font, _infoPanelTextColor, x, y);
                    var labelWidth = EstimateTextWidthCompact($"{name}: ", font);
                    context.DrawString(FormatValue(val2), font, _color2, x + labelWidth, y);
                    y += lineHeight;
                }

                // Series 3
                if (_showSeries3 && _selectedColumn3 != ColumnType.None)
                {
                    var name = GetSeriesDisplayName(2);
                    context.DrawString($"{name}: ", font, _infoPanelTextColor, x, y);
                    var labelWidth = EstimateTextWidthCompact($"{name}: ", font);
                    context.DrawString(FormatValue(val3), font, _color3, x + labelWidth, y);
                    y += lineHeight;
                }

                // Series 4
                if (_showSeries4 && _selectedColumn4 != ColumnType.None)
                {
                    var name = GetSeriesDisplayName(3);
                    context.DrawString($"{name}: ", font, _infoPanelTextColor, x, y);
                    var labelWidth = EstimateTextWidthCompact($"{name}: ", font);
                    context.DrawString(FormatValue(val4), font, _color4, x + labelWidth, y);
                    y += lineHeight;
                }
            }

            // Timestamp
            if (currentTs.HasValue)
            {
                context.DrawString($"Time: {currentTs.Value:HH:mm:ss}", font, _infoPanelTextColor, x, y);
                y += lineHeight;
            }

            // Última carga
            if (_lastLoad.HasValue)
            {
                context.DrawString($"Last update: {_lastLoad.Value:HH:mm:ss}", font, Color.Gray, x, y);
            }
        }

        private string FormatValue(decimal value)
        {
            try
            {
                var abs = Math.Abs(value);
                if (abs >= 1_000_000_000m)
                    return (value / 1_000_000_000m).ToString("N2") + "B";
                if (abs >= 1_000_000m)
                    return (value / 1_000_000m).ToString("N2") + "M";
                if (abs >= 1_000m)
                    return (value / 1_000m).ToString("N2") + "K";

                return value.ToString("N2", CultureInfo.InvariantCulture);
            }
            catch
            {
                return value.ToString();
            }
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

            if (s.Contains(',') && s.Contains('.'))
                s = s.Replace(",", string.Empty);
            else if (s.Count(ch => ch == ',') == 1 && !s.Contains('.'))
                s = s.Replace(',', '.');

            return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        #endregion
    }
}

