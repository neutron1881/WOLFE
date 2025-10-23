using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace ATAS.Indicators.Technical
{
    [DisplayName("CashProfile")]
    public class CashProfile : Indicator
    {
        private class StrikeRow
        {
            // Strike final usado para posicionar (ES si conversión activada, SPY si no)
            public decimal Strike { get; set; }
            // Strike original del CSV (SPY), para etiquetar al lado de los valores
            public decimal StrikeSpy { get; set; }
            public decimal Calls { get; set; }
            public decimal Puts { get; set; }
        }

        private readonly object _sync = new();
        private readonly List<StrikeRow> _rows = new();
        private readonly System.Timers.Timer _timer = new(60000);
        private string _error = string.Empty;
        private DateTime? _lastLoad;

        // Guardar valores previos por strike para dibujar marcadores
        private Dictionary<decimal, (decimal calls, decimal puts)> _prevBySpy = new();
        private Dictionary<decimal, (decimal calls, decimal puts)> _prevByStrike = new();

        // Precio ES en tiempo real (desde el gráfico)
        private decimal _lastEsPrice;

        // Settings
        private string _filePath = @"C:\\Path\\To\\SPY_Cash.csv";
        [Display(GroupName = "1. Settings", Name = "CSV File Path", Order =0)]
        public string FilePath
        {
            get => _filePath;
            set
            {
                _filePath = value;
                ForceReload();
            }
        }

        private int _refreshSeconds =60;
        [Display(GroupName = "1. Settings", Name = "Refresh (sec)", Order =1)]
        [Range(5,3600)]
        public int RefreshSeconds
        {
            get => _refreshSeconds;
            set
            {
                _refreshSeconds = Math.Max(5, value);
                ResetTimer();
            }
        }

        private bool _useLatestTimestamp = true;
        [Display(GroupName = "1. Settings", Name = "Use latest timestamp only", Order =2)]
        public bool UseLatestTimestamp
        {
            get => _useLatestTimestamp;
            set
            {
                _useLatestTimestamp = value;
                ForceReload();
            }
        }

        // Conversion Settings
        private bool _enableConversion;
        [Display(GroupName = "1. Settings", Name = "Convert strike to ES", Order =3)]
        public bool EnableConversion
        {
            get => _enableConversion;
            set { _enableConversion = value; ForceReload(); }
        }

        private string _quotesCsvPath = string.Empty;
        [Display(GroupName = "1. Settings", Name = "Quotes CSV (Timestamp, SPY, ES)", Order =4)]
        public string QuotesCsvPath
        {
            get => _quotesCsvPath;
            set { _quotesCsvPath = value ?? string.Empty; ForceReload(); }
        }

        private decimal _manualSpyPrice;
        [Display(GroupName = "1. Settings", Name = "Manual SPY price", Order =5)]
        public decimal ManualSpyPrice
        {
            get => _manualSpyPrice;
            set { _manualSpyPrice = Math.Max(0, value); ForceReload(); }
        }

        private decimal _manualEsPrice;
        [Display(GroupName = "1. Settings", Name = "Manual ES price", Order =6)]
        public decimal ManualEsPrice
        {
            get => _manualEsPrice;
            set { _manualEsPrice = Math.Max(0, value); ForceReload(); }
        }

        private decimal _priceStep =0.25m;
        [Display(GroupName = "1. Settings", Name = "Price step (rounding)", Order =7)]
        public decimal PriceStep
        {
            get => _priceStep;
            set { _priceStep = value <=0 ?0.25m : value; ForceReload(); }
        }

        // Positioning
        private int _centerOffsetPx =0;
        [Display(GroupName = "2. Position", Name = "Center offset (px)", Order =20)]
        [Range(-5000,5000)]
        public int CenterOffsetPx
        {
            get => _centerOffsetPx;
            set { _centerOffsetPx = Math.Clamp(value, -5000,5000); RedrawChart(); }
        }

        private bool _callsOnRight = true;
        [Display(GroupName = "2. Position", Name = "Calls on right side", Order =21)]
        public bool CallsOnRight
        {
            get => _callsOnRight;
            set { _callsOnRight = value; RedrawChart(); }
        }

        private bool _showCenterLine = true;
        [Display(GroupName = "2. Position", Name = "Show center line", Order =22)]
        public bool ShowCenterLine
        {
            get => _showCenterLine;
            set { _showCenterLine = value; RedrawChart(); }
        }

        private Color _centerLineColor = Color.FromArgb(140, Color.Red);
        [Display(GroupName = "2. Position", Name = "Center line color", Order =23)]
        public Color CenterLineColor
        {
            get => _centerLineColor;
            set { _centerLineColor = value; RedrawChart(); }
        }

        private int _centerLineThickness =1;
        [Display(GroupName = "2. Position", Name = "Center line thickness", Order =24)]
        [Range(1,10)]
        public int CenterLineThickness
        {
            get => _centerLineThickness;
            set { _centerLineThickness = Math.Clamp(value,1,10); RedrawChart(); }
        }

        // Appearance
        private int _maxBarWidthPx =220;
        [Display(GroupName = "3. Appearance", Name = "Max side width (px)", Order =30)]
        [Range(20,1000)]
        public int MaxBarWidthPx
        {
            get => _maxBarWidthPx;
            set { _maxBarWidthPx = Math.Clamp(value,20,1000); RedrawChart(); }
        }

        private int _barThicknessPx =7;
        [Display(GroupName = "3. Appearance", Name = "Bar thickness (px)", Order =31)]
        [Range(2,50)]
        public int BarThicknessPx
        {
            get => _barThicknessPx;
            set { _barThicknessPx = Math.Clamp(value,2,50); RedrawChart(); }
        }

        private Color _callsColor = Color.DodgerBlue;
        [Display(GroupName = "3. Appearance", Name = "Calls color", Order =32)]
        public Color CallsColor
        {
            get => _callsColor;
            set { _callsColor = value; RedrawChart(); }
        }

        private Color _putsColor = Color.IndianRed;
        [Display(GroupName = "3. Appearance", Name = "Puts color", Order =33)]
        public Color PutsColor
        {
            get => _putsColor;
            set { _putsColor = value; RedrawChart(); }
        }

        private int _fillOpacity =140; //0..255
        [Display(GroupName = "3. Appearance", Name = "Fill opacity", Order =34)]
        [Range(0,255)]
        public int FillOpacity
        {
            get => _fillOpacity;
            set { _fillOpacity = Math.Clamp(value,0,255); RedrawChart(); }
        }

        private bool _showValues;
        [Display(GroupName = "3. Appearance", Name = "Show side values", Order =35)]
        public bool ShowValues
        {
            get => _showValues;
            set { _showValues = value; RedrawChart(); }
        }

        private string _valueFormat = "0,0";
        [Display(GroupName = "3. Appearance", Name = "Side value format", Order =36)]
        public string ValueFormat
        {
            get => _valueFormat;
            set { _valueFormat = value ?? string.Empty; RedrawChart(); }
        }

        // Strike labels next to side values (SPY)
        private bool _showCenterStrikes = true;
        [Display(GroupName = "4. Strike labels", Name = "Show SPY strike next to values", Order =40)]
        public bool ShowCenterStrikes
        {
            get => _showCenterStrikes;
            set { _showCenterStrikes = value; RedrawChart(); }
        }

        private string _strikeFormat = "0"; // entero por defecto para formato (619)
        [Display(GroupName = "4. Strike labels", Name = "SPY strike number format", Order =41)]
        public string StrikeFormat
        {
            get => _strikeFormat;
            set { _strikeFormat = value ?? string.Empty; RedrawChart(); }
        }

        private int _strikeFontSize =8;
        [Display(GroupName = "4. Strike labels", Name = "Font size", Order =42)]
        [Range(6,40)]
        public int StrikeFontSize
        {
            get => _strikeFontSize;
            set { _strikeFontSize = Math.Clamp(value,6,40); RedrawChart(); }
        }

        private Color _strikeColor = Color.LightSteelBlue;
        [Display(GroupName = "4. Strike labels", Name = "Color", Order =43)]
        public Color StrikeColor
        {
            get => _strikeColor;
            set { _strikeColor = value; RedrawChart(); }
        }

        private int _strikeLeftMarginPx =10;
        [Display(GroupName = "4. Strike labels", Name = "Left strike extra margin (px)", Order =44)]
        [Range(0,300)]
        public int StrikeLeftMarginPx
        {
            get => _strikeLeftMarginPx;
            set { _strikeLeftMarginPx = Math.Clamp(value,0,300); RedrawChart(); }
        }

        private int _strikeRightMarginPx =10;
        [Display(GroupName = "4. Strike labels", Name = "Right strike extra margin (px)", Order =45)]
        [Range(0,300)]
        public int StrikeRightMarginPx
        {
            get => _strikeRightMarginPx;
            set { _strikeRightMarginPx = Math.Clamp(value,0,300); RedrawChart(); }
        }

        // Top summary panel (series style)
        private bool _showTopSummary = true;
        [Display(GroupName = "5. Top summary", Name = "Show top summary", Order =50)]
        public bool ShowTopSummary
        {
            get => _showTopSummary;
            set { _showTopSummary = value; RedrawChart(); }
        }

        private int _topFontSize =11;
        [Display(GroupName = "5. Top summary", Name = "Font size", Order =51)]
        [Range(6,60)]
        public int TopFontSize
        {
            get => _topFontSize;
            set { _topFontSize = Math.Clamp(value,6,60); RedrawChart(); }
        }

        private int _topMarginPx =6;
        [Display(GroupName = "5. Top summary", Name = "Top margin (px)", Order =52)]
        [Range(0,200)]
        public int TopMarginPx
        {
            get => _topMarginPx;
            set { _topMarginPx = Math.Clamp(value,0,200); RedrawChart(); }
        }

        private int _summaryBarWidthPx =240;
        [Display(GroupName = "5. Top summary", Name = "Bar width (px)", Order =53)]
        [Range(60,1000)]
        public int SummaryBarWidthPx
        {
            get => _summaryBarWidthPx;
            set { _summaryBarWidthPx = Math.Clamp(value,60,1000); RedrawChart(); }
        }

        private int _summaryRowHeightPx =12;
        [Display(GroupName = "5. Top summary", Name = "Row height (px)", Order =54)]
        [Range(8,40)]
        public int SummaryRowHeightPx
        {
            get => _summaryRowHeightPx;
            set { _summaryRowHeightPx = Math.Clamp(value,8,40); RedrawChart(); }
        }

        private int _summaryRowSpacingPx =4;
        [Display(GroupName = "5. Top summary", Name = "Row spacing (px)", Order =55)]
        [Range(0,40)]
        public int SummaryRowSpacingPx
        {
            get => _summaryRowSpacingPx;
            set { _summaryRowSpacingPx = Math.Clamp(value,0,40); RedrawChart(); }
        }

        private int _summaryLabelWidthPx =110;
        [Display(GroupName = "5. Top summary", Name = "Label width (px)", Order =56)]
        [Range(50,300)]
        public int SummaryLabelWidthPx
        {
            get => _summaryLabelWidthPx;
            set { _summaryLabelWidthPx = Math.Clamp(value,50,300); RedrawChart(); }
        }

        private Color _summaryBackBar = Color.FromArgb(80,120,120,120);
        [Display(GroupName = "5. Top summary", Name = "Back bar color", Order =57)]
        public Color SummaryBackBar
        {
            get => _summaryBackBar;
            set { _summaryBackBar = value; RedrawChart(); }
        }

        private Color _summaryCallsColor = Color.DodgerBlue;
        [Display(GroupName = "5. Top summary", Name = "Calls bar color", Order =58)]
        public Color SummaryCallsColor
        {
            get => _summaryCallsColor;
            set { _summaryCallsColor = value; RedrawChart(); }
        }

        private Color _summaryPutsColor = Color.IndianRed;
        [Display(GroupName = "5. Top summary", Name = "Puts bar color", Order =59)]
        public Color SummaryPutsColor
        {
            get => _summaryPutsColor;
            set { _summaryPutsColor = value; RedrawChart(); }
        }

        private Color _summaryTotalColor = Color.SteelBlue;
        [Display(GroupName = "5. Top summary", Name = "Total bar color (fallback)", Order =60)]
        public Color SummaryTotalColor
        {
            get => _summaryTotalColor;
            set { _summaryTotalColor = value; RedrawChart(); }
        }

        private Color _summaryTextColor = Color.White;
        [Display(GroupName = "5. Top summary", Name = "Text color", Order =61)]
        public Color SummaryTextColor
        {
            get => _summaryTextColor;
            set { _summaryTextColor = value; RedrawChart(); }
        }

        // Mostrar SPY implícado centrado
        private bool _showSpyCurrent = true;
        [Display(GroupName = "5. Top summary", Name = "Show implied SPY below", Order =62)]
        public bool ShowSpyCurrent
        {
            get => _showSpyCurrent;
            set { _showSpyCurrent = value; RedrawChart(); }
        }

        private string _spyValueFormat = "0.00";
        [Display(GroupName = "5. Top summary", Name = "SPY value format", Order =63)]
        public string SpyValueFormat
        {
            get => _spyValueFormat;
            set { _spyValueFormat = value ?? string.Empty; RedrawChart(); }
        }

        private bool _useChartEs = true;
        [Display(GroupName = "5. Top summary", Name = "Use chart price as ES", Order =64)]
        public bool UseChartPriceAsES
        {
            get => _useChartEs;
            set { _useChartEs = value; }
        }

        // Strike horizontal lines
        private bool _showStrikeLines = false;
        [Display(GroupName = "6. Strike lines", Name = "Show strike lines", Order =70)]
        public bool ShowStrikeLines
        {
            get => _showStrikeLines;
            set { _showStrikeLines = value; RedrawChart(); }
        }

        private Color _strikeLineColor = Color.FromArgb(60,200,200,200);
        [Display(GroupName = "6. Strike lines", Name = "Line color", Order =71)]
        public Color StrikeLineColor
        {
            get => _strikeLineColor;
            set { _strikeLineColor = value; RedrawChart(); }
        }

        private int _strikeLineThickness =1;
        [Display(GroupName = "6. Strike lines", Name = "Thickness", Order =72)]
        [Range(1,10)]
        public int StrikeLineThickness
        {
            get => _strikeLineThickness;
            set { _strikeLineThickness = Math.Clamp(value,1,10); RedrawChart(); }
        }

        private DashStyle _strikeLineDash = DashStyle.Solid;
        [Display(GroupName = "6. Strike lines", Name = "Dash style", Order =73)]
        public DashStyle StrikeLineDash
        {
            get => _strikeLineDash;
            set { _strikeLineDash = value; RedrawChart(); }
        }

        // Max lines (dominant strikes)
        private bool _showMaxCallsLine = true;
        [Display(GroupName = "7. Max lines", Name = "Show Max Calls line", Order =80)]
        public bool ShowMaxCallsLine
        {
            get => _showMaxCallsLine;
            set { _showMaxCallsLine = value; RedrawChart(); }
        }

        private bool _showMaxPutsLine = true;
        [Display(GroupName = "7. Max lines", Name = "Show Max Puts line", Order =81)]
        public bool ShowMaxPutsLine
        {
            get => _showMaxPutsLine;
            set { _showMaxPutsLine = value; RedrawChart(); }
        }

        private Color _maxCallsLineColor = Color.DodgerBlue;
        [Display(GroupName = "7. Max lines", Name = "Max Calls color", Order =82)]
        public Color MaxCallsLineColor
        {
            get => _maxCallsLineColor;
            set { _maxCallsLineColor = value; RedrawChart(); }
        }

        private Color _maxPutsLineColor = Color.IndianRed;
        [Display(GroupName = "7. Max lines", Name = "Max Puts color", Order =83)]
        public Color MaxPutsLineColor
        {
            get => _maxPutsLineColor;
            set { _maxPutsLineColor = value; RedrawChart(); }
        }

        private int _maxLinesThickness =2;
        [Display(GroupName = "7. Max lines", Name = "Max lines thickness", Order =84)]
        [Range(1,20)]
        public int MaxLinesThickness
        {
            get => _maxLinesThickness;
            set { _maxLinesThickness = Math.Clamp(value,1,20); RedrawChart(); }
        }

        private DashStyle _maxLinesDash = DashStyle.Solid;
        [Display(GroupName = "7. Max lines", Name = "Max lines dash style", Order =85)]
        public DashStyle MaxLinesDash
        {
            get => _maxLinesDash;
            set { _maxLinesDash = value; RedrawChart(); }
        }

        // Net levels (Calls - Puts) por strike
        private bool _showNetLevels = true;
        [Display(GroupName = "8. Net levels", Name = "Show net levels (Calls-Puts)", Order =90)]
        public bool ShowNetLevels
        {
            get => _showNetLevels;
            set { _showNetLevels = value; RedrawChart(); }
        }

        private int _netThicknessPx =9;
        [Display(GroupName = "8. Net levels", Name = "Net level thickness (px)", Order =91)]
        [Range(2,60)]
        public int NetThicknessPx
        {
            get => _netThicknessPx;
            set { _netThicknessPx = Math.Clamp(value,2,60); RedrawChart(); }
        }

        private int _netOpacity =200;
        [Display(GroupName = "8. Net levels", Name = "Net level opacity", Order =92)]
        [Range(0,255)]
        public int NetOpacity
        {
            get => _netOpacity;
            set { _netOpacity = Math.Clamp(value,0,255); RedrawChart(); }
        }

        private Color _netCallsColor = Color.DodgerBlue;
        [Display(GroupName = "8. Net levels", Name = "Net Calls color", Order =93)]
        public Color NetCallsColor
        {
            get => _netCallsColor;
            set { _netCallsColor = value; RedrawChart(); }
        }

        private Color _netPutsColor = Color.IndianRed;
        [Display(GroupName = "8. Net levels", Name = "Net Puts color", Order =94)]
        public Color NetPutsColor
        {
            get => _netPutsColor;
            set { _netPutsColor = value; RedrawChart(); }
        }

        // Previous value markers (for last snapshot)
        public enum PrevMarkerMode { Calls, Puts, Both }
        private bool _showPrevMarkers = false;
        private PrevMarkerMode _prevMarkerMode = PrevMarkerMode.Calls;
        private int _prevMarkerSize =6;
        private Color _prevMarkerCallsColor = Color.LightSkyBlue;
        private Color _prevMarkerPutsColor = Color.Salmon;

        [Display(GroupName = "9. Previous markers", Name = "Show previous markers", Order =100)]
        public bool ShowPreviousMarkers
        {
            get => _showPrevMarkers;
            set { _showPrevMarkers = value; RedrawChart(); }
        }

        [Display(GroupName = "9. Previous markers", Name = "Marker mode", Order =101)]
        public PrevMarkerMode PreviousMarkerMode
        {
            get => _prevMarkerMode;
            set { _prevMarkerMode = value; RedrawChart(); }
        }

        [Display(GroupName = "9. Previous markers", Name = "Marker size (px)", Order =102)]
        [Range(2,20)]
        public int PreviousMarkerSize
        {
            get => _prevMarkerSize;
            set { _prevMarkerSize = Math.Clamp(value,2,20); RedrawChart(); }
        }

        [Display(GroupName = "9. Previous markers", Name = "Calls marker color", Order =103)]
        public Color PreviousMarkerCallsColor
        {
            get => _prevMarkerCallsColor;
            set { _prevMarkerCallsColor = value; RedrawChart(); }
        }

        [Display(GroupName = "9. Previous markers", Name = "Puts marker color", Order =104)]
        public Color PreviousMarkerPutsColor
        {
            get => _prevMarkerPutsColor;
            set { _prevMarkerPutsColor = value; RedrawChart(); }
        }

        // Previous NET markers (Calls - Puts)
        private bool _showPrevNetMarkers = false;
        private int _prevNetMarkerSize =6;
        private Color _prevNetPositiveColor = Color.LightGreen;
        private Color _prevNetNegativeColor = Color.LightCoral;

        [Display(GroupName = "9. Previous markers", Name = "Show previous NET markers", Order =105)]
        public bool ShowPreviousNetMarkers
        {
            get => _showPrevNetMarkers;
            set { _showPrevNetMarkers = value; RedrawChart(); }
        }

        [Display(GroupName = "9. Previous markers", Name = "NET marker size (px)", Order =106)]
        [Range(2,20)]
        public int PreviousNetMarkerSize
        {
            get => _prevNetMarkerSize;
            set { _prevNetMarkerSize = Math.Clamp(value,2,20); RedrawChart(); }
        }

        [Display(GroupName = "9. Previous markers", Name = "NET positive color", Order =107)]
        public Color PreviousNetPositiveColor
        {
            get => _prevNetPositiveColor;
            set { _prevNetPositiveColor = value; RedrawChart(); }
        }

        [Display(GroupName = "9. Previous markers", Name = "NET negative color", Order =108)]
        public Color PreviousNetNegativeColor
        {
            get => _prevNetNegativeColor;
            set { _prevNetNegativeColor = value; RedrawChart(); }
        }

        public CashProfile()
        {
            EnableCustomDrawing = true;
            DenyToChangePanel = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);

            _timer.AutoReset = true;
            _timer.Elapsed += OnTimer;
            _timer.Start();
        }

        protected override void OnInitialize()
        {
            ResetTimer();
            LoadCsvSafe();
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            _lastEsPrice = value; // usar precio del gráfico como ES en tiempo real
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo?.PriceChartContainer == null)
            {
                context.DrawString("Chart not ready", new RenderFont("Arial",10), Color.Red,10,10);
                return;
            }

            var xBase = ChartInfo.PriceChartContainer.GetXByBar(CurrentBar, false);
            var xCenter = xBase + _centerOffsetPx;
            var fullWidth = ChartInfo.Region.Width;

            List<StrikeRow> snapshot;
            Dictionary<decimal, (decimal calls, decimal puts)> prevBySpy;
            Dictionary<decimal, (decimal calls, decimal puts)> prevByStrike;
            lock (_sync)
            {
                snapshot = _rows.ToList();
                prevBySpy = new Dictionary<decimal, (decimal calls, decimal puts)>(_prevBySpy);
                prevByStrike = new Dictionary<decimal, (decimal calls, decimal puts)>(_prevByStrike);
            }

            if (!string.IsNullOrEmpty(_error))
            {
                context.DrawString(_error, new RenderFont("Arial",10), Color.Red,10,10);
            }

            if (snapshot.Count ==0)
            {
                if (string.IsNullOrEmpty(_error))
                    context.DrawString("No data", new RenderFont("Arial",10), Color.Gray,10,10);
                return;
            }

            // Scale per side
            var maxCalls = snapshot.Max(r => (double)r.Calls);
            var maxPuts = snapshot.Max(r => (double)r.Puts);
            var maxSide = Math.Max(maxCalls, maxPuts);
            if (maxSide <=0)
                return;

            var scale = MaxBarWidthPx / maxSide; // px per unit for each side

            // Sort by strike to draw consistently
            snapshot.Sort((a, b) => a.Strike.CompareTo(b.Strike));

            if (_showCenterLine)
            {
                var pen = new RenderPen(_centerLineColor, _centerLineThickness);
                context.DrawLine(pen, xCenter,0, xCenter, ChartInfo.Region.Height);
            }

            var sideFont = new RenderFont("Arial",8);
            var strikeFont = new RenderFont("Arial", _strikeFontSize);

            foreach (var row in snapshot)
            {
                var y = ChartInfo.PriceChartContainer.GetYByPrice(row.Strike, false);
                var top = y - _barThicknessPx /2;

                var callsW = (int)Math.Round((double)row.Calls * scale);
                var putsW = (int)Math.Round((double)row.Puts * scale);

                // Decide which side gets which
                int rightW = _callsOnRight ? callsW : putsW;
                int leftW = _callsOnRight ? putsW : callsW;
                Color rightColor = _callsOnRight ? _callsColor : _putsColor;
                Color leftColor = _callsOnRight ? _putsColor : _callsColor;

                // Optional strike horizontal line
                if (ShowStrikeLines)
                {
                    var spen = new RenderPen(_strikeLineColor, _strikeLineThickness) { DashStyle = _strikeLineDash };
                    context.DrawLine(spen,0, y, fullWidth, y);
                }

                // Left segment
                if (leftW >0)
                {
                    var rectL = new Rectangle(xCenter - leftW, top, leftW, _barThicknessPx);
                    context.FillRectangle(Color.FromArgb(_fillOpacity, leftColor), rectL);
                }
                // Right segment
                if (rightW >0)
                {
                    var rectR = new Rectangle(xCenter, top, rightW, _barThicknessPx);
                    context.FillRectangle(Color.FromArgb(_fillOpacity, rightColor), rectR);
                }

                // Previous markers (tip position of previous snapshot for Calls/Puts)
                if (_showPrevMarkers)
                {
                    // Try match by SPY strike first (stable), then by positioned strike
                    (decimal prevCalls, decimal prevPuts) prev;
                    if (!prevBySpy.TryGetValue(row.StrikeSpy, out prev))
                        prevByStrike.TryGetValue(row.Strike, out prev);

                    // Calls marker (on the calls side)
                    if (_prevMarkerMode == PrevMarkerMode.Calls || _prevMarkerMode == PrevMarkerMode.Both)
                    {
                        var prevCallsW = (int)Math.Round((double)prev.prevCalls * scale);
                        if (prev.prevCalls >0 && prevCallsW >=0)
                        {
                            int size = _prevMarkerSize;
                            int mx = _callsOnRight ? (xCenter + prevCallsW) : (xCenter - prevCallsW);
                            int my = y;
                            var mRect = new Rectangle(mx - size /2, my - size /2, size, size);
                            context.FillRectangle(_prevMarkerCallsColor, mRect);
                        }
                    }
                    // Puts marker (on the puts side)
                    if (_prevMarkerMode == PrevMarkerMode.Puts || _prevMarkerMode == PrevMarkerMode.Both)
                    {
                        var prevPutsW = (int)Math.Round((double)prev.prevPuts * scale);
                        if (prev.prevPuts >0 && prevPutsW >=0)
                        {
                            int size = _prevMarkerSize;
                            int mx = _callsOnRight ? (xCenter - prevPutsW) : (xCenter + prevPutsW);
                            int my = y;
                            var mRect = new Rectangle(mx - size /2, my - size /2, size, size);
                            context.FillRectangle(_prevMarkerPutsColor, mRect);
                        }
                    }
                }

                // Net level (Calls - Puts)
                if (_showNetLevels)
                {
                    var diff = row.Calls - row.Puts;
                    if (diff !=0)
                    {
                        int w = (int)Math.Round((double)Math.Abs(diff) * scale);
                        if (w >0)
                        {
                            int t = _netThicknessPx;
                            int topNet = y - t /2;
                            if (diff >0)
                            {
                                // Calls dominan -> dibujar hacia el lado Calls
                                if (_callsOnRight)
                                {
                                    var r = new Rectangle(xCenter, topNet, w, t);
                                    context.FillRectangle(Color.FromArgb(_netOpacity, _netCallsColor), r);
                                }
                                else
                                {
                                    var r = new Rectangle(xCenter - w, topNet, w, t);
                                    context.FillRectangle(Color.FromArgb(_netOpacity, _netCallsColor), r);
                                }
                            }
                            else // diff <0 -> Puts dominan
                            {
                                if (_callsOnRight)
                                {
                                    var r = new Rectangle(xCenter - w, topNet, w, t);
                                    context.FillRectangle(Color.FromArgb(_netOpacity, _netPutsColor), r);
                                }
                                else
                                {
                                    var r = new Rectangle(xCenter, topNet, w, t);
                                    context.FillRectangle(Color.FromArgb(_netOpacity, _netPutsColor), r);
                                }
                            }
                        }
                    }
                }

                // Previous NET marker (tip position of previous net bar)
                if (_showPrevNetMarkers)
                {
                    (decimal prevCalls, decimal prevPuts) prev;
                    if (!prevBySpy.TryGetValue(row.StrikeSpy, out prev))
                        prevByStrike.TryGetValue(row.Strike, out prev);

                    var prevDiff = prev.prevCalls - prev.prevPuts;
                    if (prevDiff !=0)
                    {
                        int prevW = (int)Math.Round((double)Math.Abs(prevDiff) * scale);
                        if (prevW >=0)
                        {
                            int size = _prevNetMarkerSize;
                            int sgn =0;
                            if (prevDiff >0)
                                sgn = _callsOnRight ? +1 : -1;
                            else if (prevDiff <0)
                                sgn = _callsOnRight ? -1 : +1;

                            int mx = xCenter + sgn * prevW;
                            int my = y;
                            var color = prevDiff >0 ? _prevNetPositiveColor : _prevNetNegativeColor;
                            var mRect = new Rectangle(mx - size /2, my - size /2, size, size);
                            context.FillRectangle(color, mRect);
                        }
                    }
                }

                // Values and strikes near bars
                if (_showValues)
                {
                    // left value
                    if (leftW >0)
                    {
                        var valTxt = (_callsOnRight ? row.Puts : row.Calls).ToString(_valueFormat, CultureInfo.InvariantCulture);
                        int valW = EstimateTextWidth(valTxt, sideFont);
                        int valX = xCenter - leftW - valW -6;
                        int valY = top -2;
                        context.DrawString(valTxt, sideFont, leftColor, valX, valY);

                        if (_showCenterStrikes)
                        {
                            var sTxt = $"({row.StrikeSpy.ToString(_strikeFormat, CultureInfo.InvariantCulture)})";
                            int sW = EstimateTextWidth(sTxt, strikeFont);
                            int sx = valX - sW - _strikeLeftMarginPx; // a la izquierda del valor con margen configurable
                            int sy = y - (_strikeFontSize /2) -1;
                            context.DrawString(sTxt, strikeFont, _strikeColor, sx, sy);
                        }
                    }

                    // right value
                    if (rightW >0)
                    {
                        var valTxt = (_callsOnRight ? row.Calls : row.Puts).ToString(_valueFormat, CultureInfo.InvariantCulture);
                        int valX = xCenter + rightW +6;
                        int valY = top -2;
                        context.DrawString(valTxt, sideFont, rightColor, valX, valY);

                        if (_showCenterStrikes)
                        {
                            int valW = EstimateTextWidth(valTxt, sideFont);
                            var sTxt = $"({row.StrikeSpy.ToString(_strikeFormat, CultureInfo.InvariantCulture)})";
                            int sx = valX + valW + _strikeRightMarginPx; // a la derecha del valor con margen configurable
                            int sy = y - (_strikeFontSize /2) -1;
                            context.DrawString(sTxt, strikeFont, _strikeColor, sx, sy);
                        }
                    }
                }
            }

            if (_showTopSummary)
                DrawTopSummary(context, xCenter, snapshot);

            // Max lines
            if (snapshot.Count >0)
            {
                var maxCallsRow = snapshot.OrderByDescending(r => r.Calls).FirstOrDefault();
                var maxPutsRow = snapshot.OrderByDescending(r => r.Puts).FirstOrDefault();
                if (_showMaxCallsLine && maxCallsRow != null && maxCallsRow.Calls >0)
                {
                    var yC = ChartInfo.PriceChartContainer.GetYByPrice(maxCallsRow.Strike, false);
                    var penC = new RenderPen(_maxCallsLineColor, _maxLinesThickness) { DashStyle = _maxLinesDash };
                    context.DrawLine(penC,0, yC, fullWidth, yC);
                }
                if (_showMaxPutsLine && maxPutsRow != null && maxPutsRow.Puts >0)
                {
                    var yP = ChartInfo.PriceChartContainer.GetYByPrice(maxPutsRow.Strike, false);
                    var penP = new RenderPen(_maxPutsLineColor, _maxLinesThickness) { DashStyle = _maxLinesDash };
                    context.DrawLine(penP,0, yP, fullWidth, yP);
                }
            }

            if (_lastLoad.HasValue)
            {
                context.DrawString($"Last load: {_lastLoad.Value:HH:mm:ss}", new RenderFont("Arial",8), Color.Gray,10,26);
            }
        }

        private void DrawTopSummary(RenderContext context, int xCenter, List<StrikeRow> snapshot)
        {
            decimal sumCalls = snapshot.Sum(r => r.Calls);
            decimal sumPuts = snapshot.Sum(r => r.Puts);
            decimal sumTotal = sumCalls + sumPuts;

            var font = new RenderFont("Arial", _topFontSize);
            int rowH = _summaryRowHeightPx;
            int gap = _summaryRowSpacingPx;
            int barW = _summaryBarWidthPx;
            int labelW = _summaryLabelWidthPx;

            // Origen X: bloque centrado en la línea
            int xBar = xCenter - barW /2;
            int xLabel = xBar - labelW -8;
            int xRightText = xBar + barW +8;

            int y = _topMarginPx;

            // helper local
            void DrawRow(string label, decimal value, Color color, decimal total)
            {
                // etiqueta izquierda
                context.DrawString(label, font, SummaryTextColor, xLabel, y + (rowH - _topFontSize) /2);

                // barra de fondo
                var back = _summaryBackBar;
                context.FillRectangle(back, new Rectangle(xBar, y, barW, rowH));

                // barra de valor proporcional
                double ratio = (total >0 ? (double)(value / total) :0.0);
                ratio = Math.Clamp(ratio,0.0,1.0);
                int valW = (int)Math.Round(barW * ratio);
                if (valW >0)
                    context.FillRectangle(color, new Rectangle(xBar, y, valW, rowH));

                // texto derecha: valor compacto y %
                var valTxt = $"{FormatCompact(value)} ({Math.Round(ratio *100)}%)";
                context.DrawString(valTxt, font, SummaryTextColor, xRightText, y + (rowH - _topFontSize) /2);

                y += rowH + gap;
            }

            DrawRow("Calls $$$", sumCalls, _summaryCallsColor, sumTotal);
            DrawRow("Puts $$$", sumPuts, _summaryPutsColor, sumTotal);

            // TOTAL: color del dominante (Calls o Puts). Si iguales, usar color fallback.
            Color totalColor = sumCalls > sumPuts ? _summaryCallsColor : (sumPuts > sumCalls ? _summaryPutsColor : _summaryTotalColor);
            DrawRow("TOTAL $$$", sumTotal, totalColor, sumTotal <=0 ?1 : sumTotal);

            // SPY implicado centrado debajo del panel superior (ratio = ManualES / ManualSPY)
            if (_showSpyCurrent)
            {
                // Precio ES en tiempo real (gráfico)
                decimal esNow = _useChartEs && _lastEsPrice >0 ? _lastEsPrice :0m;
                if (esNow <=0 && !string.IsNullOrWhiteSpace(_quotesCsvPath) && File.Exists(_quotesCsvPath))
                {
                    // fallback a último ES del CSV de quotes
                    if (TryGetLatestSpyEsFromQuotes(_quotesCsvPath, out var spyQ, out var esQ) && esQ >0)
                        esNow = esQ;
                }

                if (esNow >0)
                {
                    decimal ratio =0m;
                    if (_manualEsPrice >0 && _manualSpyPrice >0)
                        ratio = SafeDiv(_manualEsPrice, _manualSpyPrice);
                    else if (TryGetLatestSpyEsFromQuotes(_quotesCsvPath, out var spyL, out var esL) && spyL >0)
                        ratio = SafeDiv(esL, spyL);
                    if (ratio <=0) ratio =1m;

                    var spyNow = SafeDiv(esNow, ratio);
                    var text = $"SPY: {spyNow.ToString(_spyValueFormat, CultureInfo.InvariantCulture)}";
                    int textW = EstimateTextWidth(text, font);
                    int cx = xCenter - textW /2;
                    int spyY = y;
                    context.DrawString(text, font, SummaryTextColor, cx, spyY + (rowH - _topFontSize) /2);
                    y += rowH + gap;
                }
            }
        }

        private bool TryGetLatestSpyEsFromQuotes(string path, out decimal spy, out decimal es)
        {
            spy =0m; es =0m;
            try
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length <=1) return false;
                var headers = SplitCsvLine(lines[0]);
                int idxSpy = FindIndex(headers, "spy");
                int idxEs = FindIndex(headers, "es");
                if (idxSpy <0 || idxEs <0) return false;
                for (int i = lines.Length -1; i >=1; i--)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var cols = SplitCsvLine(line);
                    if (cols.Length <= Math.Max(idxSpy, idxEs)) continue;
                    if (TryParseDecimal(cols[idxSpy], out var s) && TryParseDecimal(cols[idxEs], out var e))
                    { spy = s; es = e; return true; }
                }
                return false;
            }
            catch { return false; }
        }

        private static string FormatCompact(decimal value)
        {
            var abs = Math.Abs(value);
            string suffix;
            decimal num;
            if (abs >=1_000_000_000m)
            {
                suffix = "B";
                num = value /1_000_000_000m;
            }
            else if (abs >=1_000_000m)
            {
                suffix = "M";
                num = value /1_000_000m;
            }
            else if (abs >=1_000m)
            {
                suffix = "K";
                num = value /1_000m;
            }
            else
            {
                return value.ToString("0,0", CultureInfo.CurrentCulture);
            }

            return num.ToString("0.##", CultureInfo.CurrentCulture) + suffix;
        }

        private static int EstimateTextWidth(string text, RenderFont font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            // Heurística aproximada
            double factor =0.58;
            return (int)Math.Ceiling(text.Length * (font.Size * factor));
        }

        private void ResetTimer()
        {
            _timer.Stop();
            _timer.Interval = Math.Max(5, _refreshSeconds) *1000;
            _timer.Start();
        }

        private void OnTimer(object? sender, System.Timers.ElapsedEventArgs e)
        {
            LoadCsvSafe();
        }

        private void ForceReload()
        {
            LoadCsvSafe();
            RedrawChart();
        }

        private void LoadCsvSafe()
        {
            try
            {
                // Capturar snapshot previo para marcadores
                List<StrikeRow> prevSnapshot;
                lock (_sync)
                {
                    prevSnapshot = _rows.ToList();
                }
                var prevBySpyLocal = prevSnapshot.ToDictionary(r => r.StrikeSpy, r => (r.Calls, r.Puts));
                var prevByStrikeLocal = prevSnapshot.ToDictionary(r => r.Strike, r => (r.Calls, r.Puts));

                var data = LoadCsv(FilePath, UseLatestTimestamp, out var err,
                    EnableConversion, QuotesCsvPath, ManualSpyPrice, ManualEsPrice, PriceStep);
                lock (_sync)
                {
                    _prevBySpy = prevBySpyLocal;
                    _prevByStrike = prevByStrikeLocal;
                    _rows.Clear();
                    _rows.AddRange(data);
                }
                _error = err ?? string.Empty;
                _lastLoad = DateTime.Now;
                RedrawChart();
            }
            catch (Exception ex)
            {
                _error = $"Load error: {ex.Message}";
            }
        }

        private static List<StrikeRow> LoadCsv(
            string path,
            bool useLatestTimestamp,
            out string? error,
            bool enableConversion,
            string? quotesCsvPath,
            decimal manualSpy,
            decimal manualEs,
            decimal priceStep)
        {
            error = null;
            var result = new List<StrikeRow>();

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = $"CSV not found: {path}";
                return result;
            }

            try
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length == 0)
                {
                    error = "CSV empty";
                    return result;
                }

                // Parse header
                var headers = SplitCsvLine(lines[0]);
                int idxStrike = FindIndex(headers, "strike");
                int idxCalls = FindIndex(headers, "call"); // matches CALL*
                int idxPuts = FindIndex(headers, "put"); // matches PUT*
                int idxTs = FindIndex(headers, "time"); // matches Timestamp
                int idxSpy = FindIndex(headers, "spy");
                int idxEs = FindIndex(headers, "es");

                if (idxStrike <0 || idxCalls <0 || idxPuts <0)
                {
                    error = "CSV headers not recognized. Expect Strike, CALL*, PUT*, Timestamp";
                    return result;
                }

                // If only latest timestamp requested, find max
                DateTime? maxTs = null;
                string? maxTsRaw = null;
                if (useLatestTimestamp && idxTs >=0)
                {
                    for (int i =1; i < lines.Length; i++)
                    {
                        if (string.IsNullOrWhiteSpace(lines[i])) continue;
                        var cols = SplitCsvLine(lines[i]);
                        if (cols.Length <= idxTs) continue;
                        var tsRaw = cols[idxTs]?.Trim('"', ' ');
                        if (string.IsNullOrEmpty(tsRaw)) continue;

                        if (DateTime.TryParse(tsRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var ts))
                        {
                            if (maxTs == null || ts > maxTs)
                            {
                                maxTs = ts;
                                maxTsRaw = tsRaw;
                            }
                        }
                        else
                        {
                            // fallback lexicographic
                            if (maxTsRaw == null || string.CompareOrdinal(tsRaw, maxTsRaw) >0)
                                maxTsRaw = tsRaw;
                        }
                    }
                }

                // Load external quotes if needed
                Dictionary<string, decimal>? factorByTs = null;
                string? quotesErr = null;
                if (enableConversion && (idxSpy <0 || idxEs <0) && !string.IsNullOrWhiteSpace(quotesCsvPath) && File.Exists(quotesCsvPath))
                {
                    factorByTs = LoadQuotesFactors(quotesCsvPath!, out quotesErr);
                    if (quotesErr != null && error == null) error = $"Quotes CSV: {quotesErr}";
                }

                decimal manualFactor = (enableConversion && manualSpy >0 && manualEs >0) ? SafeDiv(manualEs, manualSpy) :1m;

                var agg = new Dictionary<decimal, (decimal calls, decimal puts, decimal spyStrike)>();
                for (int i =1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var cols = SplitCsvLine(line);
                    if (cols.Length <= Math.Max(idxStrike, Math.Max(idxCalls, Math.Max(idxPuts, idxTs))))
                        continue;

                    string? tsRaw = idxTs >=0 && idxTs < cols.Length ? cols[idxTs]?.Trim('"', ' ') : null;

                    if (useLatestTimestamp && idxTs >=0)
                    {
                        if (!string.Equals(tsRaw, maxTsRaw, StringComparison.Ordinal))
                            continue;
                    }

                    if (!TryParseDecimal(cols[idxStrike], out var strikeSpy)) continue; // original SPY
                    if (!TryParseDecimal(cols[idxCalls], out var calls)) calls =0;
                    if (!TryParseDecimal(cols[idxPuts], out var puts)) puts =0;

                    // determine conversion factor
                    decimal factor =1m;
                    if (enableConversion)
                    {
                        if (idxSpy >=0 && idxEs >=0 && idxSpy < cols.Length && idxEs < cols.Length &&
                            TryParseDecimal(cols[idxSpy], out var spyVal) && TryParseDecimal(cols[idxEs], out var esVal) && spyVal >0)
                        {
                            factor = SafeDiv(esVal, spyVal);
                        }
                        else if (factorByTs != null && tsRaw != null && factorByTs.TryGetValue(tsRaw, out var fByTs))
                        {
                            factor = fByTs;
                        }
                        else if (manualFactor >0)
                        {
                            factor = manualFactor;
                        }
                    }

                    var outStrike = enableConversion ? strikeSpy * factor : strikeSpy;
                    if (priceStep >0)
                        outStrike = RoundToStep(outStrike, priceStep);

                    if (!agg.TryGetValue(outStrike, out var tuple))
                        agg[outStrike] = (calls, puts, strikeSpy);
                    else
                        agg[outStrike] = (tuple.calls + calls, tuple.puts + puts, tuple.spyStrike); // conservar SPY original
                }

                foreach (var kv in agg)
                {
                    result.Add(new StrikeRow
                    {
                        Strike = kv.Key,
                        StrikeSpy = kv.Value.spyStrike,
                        Calls = kv.Value.calls,
                        Puts = kv.Value.puts
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return result;
            }
        }

        private static Dictionary<string, decimal> LoadQuotesFactors(string path, out string? error)
        {
            error = null;
            var dict = new Dictionary<string, decimal>(StringComparer.Ordinal);
            try
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length ==0)
                {
                    error = "Quotes CSV empty";
                    return dict;
                }

                var headers = SplitCsvLine(lines[0]);
                int idxTs = FindIndex(headers, "time");
                int idxSpy = FindIndex(headers, "spy");
                int idxEs = FindIndex(headers, "es");
                if (idxTs <0 || idxSpy <0 || idxEs <0)
                {
                    error = "Quotes CSV headers not recognized. Expect Timestamp, SPY, ES";
                    return dict;
                }

                for (int i =1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var cols = SplitCsvLine(line);
                    if (cols.Length <= Math.Max(idxTs, Math.Max(idxSpy, idxEs))) continue;
                    var tsRaw = cols[idxTs]?.Trim('"', ' ');
                    if (!TryParseDecimal(cols[idxSpy], out var spy) || !TryParseDecimal(cols[idxEs], out var es) || spy <=0)
                        continue;
                    dict[tsRaw ?? string.Empty] = SafeDiv(es, spy);
                }

                return dict;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return dict;
            }
        }

        private static decimal SafeDiv(decimal a, decimal b)
        {
            if (b ==0) return 0;
            return a / b;
        }

        private static decimal RoundToStep(decimal price, decimal step)
        {
            if (step <=0) return price;
            var q = price / step;
            var rounded = Math.Round(q,0, MidpointRounding.AwayFromZero);
            return rounded * step;
        }

        private static int FindIndex(string[] headers, string key)
        {
            key = key.ToLowerInvariant();
            for (int i =0; i < headers.Length; i++)
            {
                var h = headers[i] ?? string.Empty;
                var norm = new string(h.Where(ch => char.IsLetterOrDigit(ch)).ToArray()).ToLowerInvariant();
                if (norm.Contains(key))
                    return i;
            }
            return -1;
        }

        private static string[] SplitCsvLine(string line)
        {
            // Simple CSV splitter supporting quoted fields
            var list = new List<string>();
            bool inQuotes = false;
            var cur = new System.Text.StringBuilder();
            for (int i =0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i +1 < line.Length && line[i +1] == '"')
                    {
                        cur.Append('"');
                        i++; // skip escaped quote
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
            value =0m;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().Trim('"').Replace("$", string.Empty).Replace(" ", string.Empty);
            // Remove thousand separators if present
            if (s.Contains(',') && s.Contains('.'))
            {
                // Try to guess culture: assume comma as thousand sep and dot as decimal
                s = s.Replace(",", string.Empty);
            }
            else if (s.Count(ch => ch == ',') ==1 && !s.Contains('.'))
            {
                // maybe decimal comma
                s = s.Replace(',', '.');
            }

            return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        protected override void OnDispose()
        {
            try
            {
                _timer.Stop();
                _timer.Dispose();
            }
            catch { }
            base.OnDispose();
        }
    }
}
