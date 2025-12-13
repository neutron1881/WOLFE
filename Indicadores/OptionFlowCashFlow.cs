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
    [DisplayName("Option Flow - Cash Flow")]
    [Category("Custom")]
    public class OptionFlowCashFlow : Indicator
    {
        #region Private Fields

        private readonly object _sync = new();
        private readonly System.Timers.Timer _refreshTimer;
        private readonly List<CsvDataRow> _csvData = new();
        private string _error = string.Empty;
        private DateTime? _lastLoad;
        private DateTime? _currentTimestamp;

        // Valores actuales de las 3 columnas
        private decimal _currentCallCashFlow;
        private decimal _currentPutCashFlow;
        private decimal _currentRatioCashFlow;

        #endregion

        #region Data Structure

        private class CsvDataRow
        {
            public long TimestampMs { get; set; }
            public DateTime IsoTime { get; set; }
            public string Underlying { get; set; } = string.Empty;
            public decimal CallMoneyFlow { get; set; }
            public decimal PutMoneyFlow { get; set; }
            public decimal MfRatio { get; set; }
        }

        #endregion

        #region Settings

        private string _filePath = @"C:\Path\To\OptionFlow.csv";
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

        #endregion

        #region Call Cash Flow Appearance

        private bool _showCallCashFlow = true;
        [Display(GroupName = "2. Call Cash Flow", Name = "Show Call Cash Flow", Order = 10)]
        public bool ShowCallCashFlow
        {
            get => _showCallCashFlow;
            set
            {
                _showCallCashFlow = value;
                ((ValueDataSeries)DataSeries[0]).VisualType = value ? (_callShowAsHistogram ? VisualMode.Histogram : VisualMode.Line) : VisualMode.Hide;
                RecalculateValues();
            }
        }

        private Color _callColor = Color.LimeGreen;
        [Display(GroupName = "2. Call Cash Flow", Name = "Color", Order = 20)]
        public Color CallColor
        {
            get => _callColor;
            set
            {
                _callColor = value;
                ((ValueDataSeries)DataSeries[0]).Color = CrossColor.FromRgb(value.R, value.G, value.B);
                RecalculateValues();
            }
        }

        private int _callLineWidth = 2;
        [Display(GroupName = "2. Call Cash Flow", Name = "Line Width", Order = 30)]
        [Range(1, 10)]
        public int CallLineWidth
        {
            get => _callLineWidth;
            set
            {
                _callLineWidth = Math.Clamp(value, 1, 10);
                ((ValueDataSeries)DataSeries[0]).Width = _callLineWidth;
                RecalculateValues();
            }
        }

        private bool _callShowAsHistogram = true;
        [Display(GroupName = "2. Call Cash Flow", Name = "Show as Histogram", Order = 40)]
        public bool CallShowAsHistogram
        {
            get => _callShowAsHistogram;
            set
            {
                _callShowAsHistogram = value;
                if (_showCallCashFlow)
                    ((ValueDataSeries)DataSeries[0]).VisualType = value ? VisualMode.Histogram : VisualMode.Line;
                RecalculateValues();
            }
        }

        #endregion

        #region Put Cash Flow Appearance

        private bool _showPutCashFlow = true;
        [Display(GroupName = "3. Put Cash Flow", Name = "Show Put Cash Flow", Order = 10)]
        public bool ShowPutCashFlow
        {
            get => _showPutCashFlow;
            set
            {
                _showPutCashFlow = value;
                ((ValueDataSeries)DataSeries[1]).VisualType = value ? (_putShowAsHistogram ? VisualMode.Histogram : VisualMode.Line) : VisualMode.Hide;
                RecalculateValues();
            }
        }

        private Color _putColor = Color.Red;
        [Display(GroupName = "3. Put Cash Flow", Name = "Color", Order = 20)]
        public Color PutColor
        {
            get => _putColor;
            set
            {
                _putColor = value;
                ((ValueDataSeries)DataSeries[1]).Color = CrossColor.FromRgb(value.R, value.G, value.B);
                RecalculateValues();
            }
        }

        private int _putLineWidth = 2;
        [Display(GroupName = "3. Put Cash Flow", Name = "Line Width", Order = 30)]
        [Range(1, 10)]
        public int PutLineWidth
        {
            get => _putLineWidth;
            set
            {
                _putLineWidth = Math.Clamp(value, 1, 10);
                ((ValueDataSeries)DataSeries[1]).Width = _putLineWidth;
                RecalculateValues();
            }
        }

        private bool _putShowAsHistogram = true;
        [Display(GroupName = "3. Put Cash Flow", Name = "Show as Histogram", Order = 40)]
        public bool PutShowAsHistogram
        {
            get => _putShowAsHistogram;
            set
            {
                _putShowAsHistogram = value;
                if (_showPutCashFlow)
                    ((ValueDataSeries)DataSeries[1]).VisualType = value ? VisualMode.Histogram : VisualMode.Line;
                RecalculateValues();
            }
        }

        #endregion

        #region Ratio Cash Flow Appearance

        private bool _showRatioCashFlow = true;
        [Display(GroupName = "4. Ratio Cash Flow", Name = "Show Ratio Cash Flow", Order = 10)]
        public bool ShowRatioCashFlow
        {
            get => _showRatioCashFlow;
            set
            {
                _showRatioCashFlow = value;
                ((ValueDataSeries)DataSeries[2]).VisualType = value ? VisualMode.Line : VisualMode.Hide;
                RecalculateValues();
            }
        }

        private Color _ratioColor = Color.Yellow;
        [Display(GroupName = "4. Ratio Cash Flow", Name = "Color", Order = 20)]
        public Color RatioColor
        {
            get => _ratioColor;
            set
            {
                _ratioColor = value;
                ((ValueDataSeries)DataSeries[2]).Color = CrossColor.FromRgb(value.R, value.G, value.B);
                RecalculateValues();
            }
        }

        private int _ratioLineWidth = 2;
        [Display(GroupName = "4. Ratio Cash Flow", Name = "Line Width", Order = 30)]
        [Range(1, 10)]
        public int RatioLineWidth
        {
            get => _ratioLineWidth;
            set
            {
                _ratioLineWidth = Math.Clamp(value, 1, 10);
                ((ValueDataSeries)DataSeries[2]).Width = _ratioLineWidth;
                RecalculateValues();
            }
        }

        #endregion

        #region Zero Line Settings

        private bool _showZeroLine = true;
        [Display(GroupName = "5. Zero Line", Name = "Show Zero Line", Order = 10)]
        public bool ShowZeroLine
        {
            get => _showZeroLine;
            set
            {
                _showZeroLine = value;
                ((ValueDataSeries)DataSeries[3]).VisualType = value ? VisualMode.Line : VisualMode.Hide;
                RecalculateValues();
            }
        }

        private Color _zeroLineColor = Color.Gray;
        [Display(GroupName = "5. Zero Line", Name = "Color", Order = 20)]
        public Color ZeroLineColor
        {
            get => _zeroLineColor;
            set
            {
                _zeroLineColor = value;
                ((ValueDataSeries)DataSeries[3]).Color = CrossColor.FromRgb(value.R, value.G, value.B);
                RecalculateValues();
            }
        }

        #endregion

        #region Info Panel Settings

        private bool _showInfoPanel = true;
        [Display(GroupName = "6. Info Panel", Name = "Show Info Panel", Order = 10)]
        public bool ShowInfoPanel
        {
            get => _showInfoPanel;
            set
            {
                _showInfoPanel = value;
                RecalculateValues();
            }
        }

        private int _infoPanelFontSize = 10;
        [Display(GroupName = "6. Info Panel", Name = "Font Size", Order = 20)]
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
        [Display(GroupName = "6. Info Panel", Name = "Text Color", Order = 30)]
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

        public OptionFlowCashFlow()
        {
            // Crear nuevo panel debajo del gráfico de precios
            Panel = IndicatorDataProvider.NewPanel;

            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);

            // Serie 0: Call Cash Flow
            ((ValueDataSeries)DataSeries[0]).Name = "Call Cash Flow";
            ((ValueDataSeries)DataSeries[0]).Color = CrossColor.FromRgb(50, 205, 50); // LimeGreen
            ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Histogram;
            ((ValueDataSeries)DataSeries[0]).Width = 2;

            // Serie 1: Put Cash Flow
            DataSeries.Add(new ValueDataSeries("PutCashFlow", "Put Cash Flow")
            {
                Color = CrossColor.FromRgb(255, 0, 0), // Red
                VisualType = VisualMode.Histogram,
                Width = 2
            });

            // Serie 2: Ratio Cash Flow
            DataSeries.Add(new ValueDataSeries("RatioCashFlow", "Ratio Cash Flow")
            {
                Color = CrossColor.FromRgb(255, 255, 0), // Yellow
                VisualType = VisualMode.Line,
                Width = 2
            });

            // Serie 3: Línea de cero
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
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            // Línea de cero siempre en 0
            DataSeries[3][bar] = 0m;

            decimal callValue, putValue, ratioValue;

            if (bar == CurrentBar - 1)
            {
                // Asignar los valores actuales a la última barra
                lock (_sync)
                {
                    callValue = _currentCallCashFlow;
                    putValue = _currentPutCashFlow;
                    ratioValue = _currentRatioCashFlow;
                }
            }
            else
            {
                // Para barras históricas, buscar datos por timestamp si están disponibles
                var candle = GetCandle(bar);
                if (candle != null)
                {
                    var candleTime = candle.Time;
                    GetValuesForTime(candleTime, out callValue, out putValue, out ratioValue);
                }
                else
                {
                    callValue = 0m;
                    putValue = 0m;
                    ratioValue = 0m;
                }
            }

            // Asignar valores a las series
            DataSeries[0][bar] = callValue;   // Call Cash Flow
            DataSeries[1][bar] = putValue;    // Put Cash Flow
            DataSeries[2][bar] = ratioValue;  // Ratio Cash Flow
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo?.PriceChartContainer == null)
                return;

            // Mostrar errores si existen
            if (!string.IsNullOrEmpty(_error))
            {
                var errorFont = new RenderFont("Arial", 10);
                context.DrawString(_error, errorFont, Color.Red, 10, 10);
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

                    if (_csvData.Count > 0)
                    {
                        CsvDataRow latestRow;
                        if (_useLatestOnly)
                            latestRow = _csvData.OrderByDescending(r => r.TimestampMs).First();
                        else
                            latestRow = _csvData.Last();

                        _currentCallCashFlow = latestRow.CallMoneyFlow;
                        _currentPutCashFlow = latestRow.PutMoneyFlow;
                        _currentRatioCashFlow = latestRow.MfRatio;
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

        private void GetValuesForTime(DateTime time, out decimal callValue, out decimal putValue, out decimal ratioValue)
        {
            lock (_sync)
            {
                if (_csvData.Count == 0)
                {
                    callValue = 0m;
                    putValue = 0m;
                    ratioValue = 0m;
                    return;
                }

                // Buscar la fila más cercana al tiempo dado
                var closest = _csvData
                    .Where(r => r.IsoTime <= time)
                    .OrderByDescending(r => r.IsoTime)
                    .FirstOrDefault();

                if (closest != null)
                {
                    callValue = closest.CallMoneyFlow;
                    putValue = closest.PutMoneyFlow;
                    ratioValue = closest.MfRatio;
                }
                else
                {
                    // Si no hay datos anteriores, usar el primero disponible
                    var first = _csvData.First();
                    callValue = first.CallMoneyFlow;
                    putValue = first.PutMoneyFlow;
                    ratioValue = first.MfRatio;
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

            row.CallMoneyFlow = GetDecimal(cols, indices, "call_money_flow");
            row.PutMoneyFlow = GetDecimal(cols, indices, "put_money_flow");
            row.MfRatio = GetDecimal(cols, indices, "mf_ratio");

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

        private void DrawInfoPanel(RenderContext context)
        {
            var font = new RenderFont("Arial", _infoPanelFontSize);
            int x = 10;
            int y = 10;
            int lineHeight = _infoPanelFontSize + 4;

            // Valores actuales
            decimal callVal, putVal, ratioVal;
            DateTime? currentTs;
            lock (_sync)
            {
                callVal = _currentCallCashFlow;
                putVal = _currentPutCashFlow;
                ratioVal = _currentRatioCashFlow;
                currentTs = _currentTimestamp;
            }

            // Call Cash Flow
            if (_showCallCashFlow)
            {
                context.DrawString("Call Cash Flow: ", font, _infoPanelTextColor, x, y);
                var labelWidth = EstimateTextWidth("Call Cash Flow: ", font);
                context.DrawString(FormatValue(callVal), font, _callColor, x + labelWidth, y);
                y += lineHeight;
            }

            // Put Cash Flow
            if (_showPutCashFlow)
            {
                context.DrawString("Put Cash Flow: ", font, _infoPanelTextColor, x, y);
                var labelWidth = EstimateTextWidth("Put Cash Flow: ", font);
                context.DrawString(FormatValue(putVal), font, _putColor, x + labelWidth, y);
                y += lineHeight;
            }

            // Ratio Cash Flow
            if (_showRatioCashFlow)
            {
                context.DrawString("Ratio Cash Flow: ", font, _infoPanelTextColor, x, y);
                var labelWidth = EstimateTextWidth("Ratio Cash Flow: ", font);
                context.DrawString(ratioVal.ToString("N4", CultureInfo.InvariantCulture), font, _ratioColor, x + labelWidth, y);
                y += lineHeight;
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

        private static int EstimateTextWidth(string text, RenderFont font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return (int)Math.Ceiling(text.Length * font.Size * 0.6);
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
