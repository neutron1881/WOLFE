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
using CrossColor = System.Windows.Media.Color;

namespace ATAS.Indicators.Technical
{
    [DisplayName("ATM Pairs Panel")] // Panel inferior tipo PCN
    public class ATM : Indicator
    {
        private readonly object _sync = new();
        private DateTime _lastLoadUtc = DateTime.MinValue;
        private string _status = string.Empty;

        // Snapshot de filas por strike (último timestamp)
        private sealed class StrikeRow
        {
            public decimal Strike;
            public decimal CallCash;
            public decimal PutCash;
            public decimal IvCalls;
            public decimal IvPuts;
            public decimal DCall;
            public decimal DPuts;
            public decimal CallGex;
            public decimal PutGex;
            public decimal Bull;
            public decimal Bear;
        }
        private readonly List<StrikeRow> _rows = new();
        private Dictionary<decimal, StrikeRow> _rowsByStrike = new();
        private decimal _lastCsvPrice; // Price del CSV para cálculo ATM

        // Histórico por strike
        private class StrikeHist
        {
            public DateTime Ts;
            public decimal CallCash;
            public decimal PutCash;
            public decimal IvCalls;
            public decimal IvPuts;
            public decimal DCall;
            public decimal DPuts;
            public decimal CallGex;
            public decimal PutGex;
            public decimal Bull;
            public decimal Bear;
        }
        private readonly Dictionary<decimal, List<StrikeHist>> _histByStrike = new();
        private readonly List<(DateTime Ts, decimal Price)> _priceHist = new();
        private bool _loadFullHistory = true;
        [Category("Datos"), Display(Name = "Cargar histórico completo", Order = 6)]
        public bool LoadFullHistory { get => _loadFullHistory; set { _loadFullHistory = value; ForceReload(); } }

        private readonly string[] _timeFormats = new[]
        {
            "MM/dd/yyyy HH:mm:ss",
            "MM/dd/yyyy H:mm:ss",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd H:mm:ss",
            "MM/dd/yyyy",
            "yyyy-MM-dd"
        };

        // Series definition
        private sealed class SeriesDef
        {
            public string Key;
            public string Display;
            public Color Color;
            public ValueDataSeries Series;
            public Func<StrikeRow, decimal> Selector; // para obtener valor de la fila
        }
        private readonly List<SeriesDef> _defs = new();
        private Dictionary<string, SeriesDef> _defByKey = new(StringComparer.OrdinalIgnoreCase);

        // Settings
        private string _csvPath = @"C:\\Path\\To\\ATM.csv";
        [Category("Datos"), Display(Name = "Ruta CSV", Order = 0)]
        public string CsvPath { get => _csvPath; set { _csvPath = value ?? string.Empty; ForceReload(); } }

        private string _timeColumn = "Timestamp";
        [Category("Datos"), Display(Name = "Columna tiempo", Order = 1)]
        public string TimeColumn { get => _timeColumn; set { _timeColumn = value ?? string.Empty; ForceReload(); } }

        private bool _csvTimeIsLocal = true;
        [Category("Datos"), Display(Name = "Timestamp es local", Order = 2)]
        public bool CsvTimeIsLocal { get => _csvTimeIsLocal; set { _csvTimeIsLocal = value; ForceReload(); } }

        private int _refreshSeconds = 30;
        [Category("Datos"), Display(Name = "Refresco (seg)", Order = 3)]
        [Range(5, 3600)]
        public int RefreshSeconds { get => _refreshSeconds; set { _refreshSeconds = Math.Clamp(value, 5, 3600); RecalculateValues(); RedrawChart(); } }

        private int _gmtOffsetHours = 0;
        [Category("Datos"), Display(Name = "GMT offset horas", Order = 4)]
        [Range(-24, 24)]
        public int GmtOffsetHours { get => _gmtOffsetHours; set { _gmtOffsetHours = Math.Clamp(value, -24, 24); RecalculateValues(); RedrawChart(); } }

        // Usar Price del CSV para strikes ATM
        private bool _useCsvPriceForAtm = true;
        [Category("Datos"), Display(Name = "Usar Price CSV para ATM", Order = 5)]
        public bool UseCsvPriceForATM { get => _useCsvPriceForAtm; set { _useCsvPriceForAtm = value; RecalculateValues(); RedrawChart(); } }

        // Visual
        private int _lineThickness = 2;
        [Category("Visual"), Display(Name = "Grosor líneas", Order = 0)]
        [Range(1, 8)]
        public int LineThickness { get => _lineThickness; set { _lineThickness = Math.Clamp(value, 1, 8); ApplySeriesStyle(); } }

        private bool _showZeroLine = true;
        [Category("Visual"), Display(Name = "Mostrar línea 0", Order = 1)]
        public bool ShowZeroLine { get => _showZeroLine; set { _showZeroLine = value; ApplyConstLinesStyle(); RecalculateValues(); RedrawChart(); } }

        private Color _zeroLineColor = Color.FromArgb(120, 180, 180, 180);
        [Category("Visual"), Display(Name = "Color línea 0", Order = 2)]
        public Color ZeroLineColor { get => _zeroLineColor; set { _zeroLineColor = value; ApplyConstLinesStyle(); RedrawChart(); } }

        private int _emaPeriod = 1;
        [Category("Visual"), Display(Name = "EMA Period", Order = 3)]
        [Range(1, 500)]
        public int EmaPeriod { get => _emaPeriod; set { _emaPeriod = Math.Max(1, value); RecalculateValues(); RedrawChart(); } }

        // High/Low custom lines
        private bool _showHighLine = false;
        private decimal _highLevel = 0m;
        private Color _highLineColor = Color.FromArgb(180, 220, 80, 80);
        private int _highLineThickness = 1;

        private bool _showLowLine = false;
        private decimal _lowLevel = 0m;
        private Color _lowLineColor = Color.FromArgb(180, 80, 160, 220);
        private int _lowLineThickness = 1;

        // Extra constant lines
        private bool _showExtra1Line = false;
        private decimal _extra1Level = 0m;
        private Color _extra1Color = Color.FromArgb(180, 200, 200, 80);
        private int _extra1Thickness = 1;

        private bool _showExtra2Line = false;
        private decimal _extra2Level = 0m;
        private Color _extra2Color = Color.FromArgb(180, 200, 80, 200);
        private int _extra2Thickness = 1;

        [Category("Visual: High/Low"), Display(Name = "Mostrar línea High", Order = 0)]
        public bool ShowHighLine { get => _showHighLine; set { _showHighLine = value; ApplyConstLinesStyle(); RecalculateValues(); RedrawChart(); } }
        [Category("Visual: High/Low"), Display(Name = "Nivel High", Order = 1)]
        public decimal High { get => _highLevel; set { _highLevel = value; ApplyConstLinesStyle(); RecalculateValues(); RedrawChart(); } }
        [Category("Visual: High/Low"), Display(Name = "Color High", Order = 2)]
        public Color HighLineColor { get => _highLineColor; set { _highLineColor = value; ApplyConstLinesStyle(); RedrawChart(); } }
        [Category("Visual: High/Low"), Display(Name = "Grosor High", Order = 3)]
        [Range(1, 8)]
        public int HighLineThickness { get => _highLineThickness; set { _highLineThickness = Math.Clamp(value, 1, 8); ApplyConstLinesStyle(); RedrawChart(); } }

        [Category("Visual: High/Low"), Display(Name = "Mostrar línea Low", Order = 4)]
        public bool ShowLowLine { get => _showLowLine; set { _showLowLine = value; ApplyConstLinesStyle(); RecalculateValues(); RedrawChart(); } }
        [Category("Visual: High/Low"), Display(Name = "Nivel Low", Order = 5)]
        public decimal Low { get => _lowLevel; set { _lowLevel = value; ApplyConstLinesStyle(); RecalculateValues(); RedrawChart(); } }
        [Category("Visual: High/Low"), Display(Name = "Color Low", Order = 6)]
        public Color LowLineColor { get => _lowLineColor; set { _lowLineColor = value; ApplyConstLinesStyle(); RedrawChart(); } }
        [Category("Visual: High/Low"), Display(Name = "Grosor Low", Order = 7)]
        [Range(1, 8)]
        public int LowLineThickness { get => _lowLineThickness; set { _lowLineThickness = Math.Clamp(value, 1, 8); ApplyConstLinesStyle(); RedrawChart(); } }

        [Category("Visual: Extra"), Display(Name = "Mostrar línea Extra 1", Order = 0)]
        public bool ShowExtra1Line { get => _showExtra1Line; set { _showExtra1Line = value; ApplyConstLinesStyle(); RecalculateValues(); RedrawChart(); } }
        [Category("Visual: Extra"), Display(Name = "Nivel Extra 1", Order = 1)]
        public decimal Extra1 { get => _extra1Level; set { _extra1Level = value; ApplyConstLinesStyle(); RecalculateValues(); RedrawChart(); } }
        [Category("Visual: Extra"), Display(Name = "Color Extra 1", Order = 2)]
        public Color Extra1LineColor { get => _extra1Color; set { _extra1Color = value; ApplyConstLinesStyle(); RedrawChart(); } }
        [Category("Visual: Extra"), Display(Name = "Grosor Extra 1", Order = 3)]
        [Range(1, 8)]
        public int Extra1LineThickness { get => _extra1Thickness; set { _extra1Thickness = Math.Clamp(value, 1, 8); ApplyConstLinesStyle(); RedrawChart(); } }

        [Category("Visual: Extra"), Display(Name = "Mostrar línea Extra 2", Order = 4)]
        public bool ShowExtra2Line { get => _showExtra2Line; set { _showExtra2Line = value; ApplyConstLinesStyle(); RecalculateValues(); RedrawChart(); } }
        [Category("Visual: Extra"), Display(Name = "Nivel Extra 2", Order = 5)]
        public decimal Extra2 { get => _extra2Level; set { _extra2Level = value; ApplyConstLinesStyle(); RecalculateValues(); RedrawChart(); } }
        [Category("Visual: Extra"), Display(Name = "Color Extra 2", Order = 6)]
        public Color Extra2LineColor { get => _extra2Color; set { _extra2Color = value; ApplyConstLinesStyle(); RedrawChart(); } }
        [Category("Visual: Extra"), Display(Name = "Grosor Extra 2", Order = 7)]
        [Range(1, 8)]
        public int Extra2LineThickness { get => _extra2Thickness; set { _extra2Thickness = Math.Clamp(value, 1, 8); ApplyConstLinesStyle(); RedrawChart(); } }

        // Column keys
        private const string KEY_CALL_CASH = "CALL $$$";
        private const string KEY_PUT_CASH = "PUTS $$$";
        private const string KEY_IV_CALLS = "IVCALLS";
        private const string KEY_IV_PUTS = "IVPUTS";
        private const string KEY_DCALL = "DCALL";
        private const string KEY_DPUTS = "DPUTS";
        private const string KEY_CALL_GEX = "Call Gex";
        private const string KEY_PUT_GEX = "Put Gex";
        private const string KEY_BULL = "BULL";
        private const string KEY_BEAR = "BEAR";

        // Toggles (parejas) y colores
        private bool _showCashPair = true;
        private Color _callCashColor = Color.DodgerBlue;
        private Color _putCashColor = Color.IndianRed;
        [Category("Series: Cash"), Display(Name = "Mostrar CALL $$$ / PUTS $$$", Order = 0)]
        public bool Show_CashPair { get => _showCashPair; set { _showCashPair = value; SetPairVisibility(KEY_CALL_CASH, KEY_PUT_CASH, value); } }
        [Category("Series: Cash"), Display(Name = "CALL $$$ color", Order = 1)]
        public Color CallCashColor { get => _callCashColor; set { _callCashColor = value; ApplySeriesStyle(); } }
        [Category("Series: Cash"), Display(Name = "PUTS $$$ color", Order = 2)]
        public Color PutCashColor { get => _putCashColor; set { _putCashColor = value; ApplySeriesStyle(); } }

        private bool _showIvPair = true;
        private Color _ivCallColor = Color.MediumSeaGreen;
        private Color _ivPutColor = Color.Salmon;
        private int _ivStrikesRange = 1; // N arriba y N abajo
        [Category("Series: IV"), Display(Name = "Mostrar IVCALLS / IVPUTS", Order = 0)]
        public bool Show_IVPair { get => _showIvPair; set { _showIvPair = value; SetPairVisibility(KEY_IV_CALLS, KEY_IV_PUTS, value); } }
        [Category("Series: IV"), Display(Name = "IVCALLS color", Order = 1)]
        public Color IvCallsColor { get => _ivCallColor; set { _ivCallColor = value; ApplySeriesStyle(); } }
        [Category("Series: IV"), Display(Name = "IVPUTS color", Order = 2)]
        public Color IvPutsColor { get => _ivPutColor; set { _ivPutColor = value; ApplySeriesStyle(); } }
        [Category("Series: IV"), Display(Name = "Rango strikes ±", Order = 3)]
        [Range(1, 50)]
        public int IV_StrikesRange { get => _ivStrikesRange; set { _ivStrikesRange = Math.Clamp(value, 1, 50); RecalculateValues(); RedrawChart(); } }

        private bool _showDeltaPair = true;
        private Color _dCallColor = Color.SteelBlue;
        private Color _dPutsColor = Color.Sienna;
        [Category("Series: Delta"), Display(Name = "Mostrar DCALL / DPUTS", Order = 0)]
        public bool Show_DeltaPair { get => _showDeltaPair; set { _showDeltaPair = value; SetPairVisibility(KEY_DCALL, KEY_DPUTS, value); } }
        [Category("Series: Delta"), Display(Name = "DCALL color", Order = 1)]
        public Color DCallColor { get => _dCallColor; set { _dCallColor = value; ApplySeriesStyle(); } }
        [Category("Series: Delta"), Display(Name = "DPUTS color", Order = 2)]
        public Color DPutsColor { get => _dPutsColor; set { _dPutsColor = value; ApplySeriesStyle(); } }

        private bool _showGexPair = true;
        private Color _callGexColor = Color.MediumOrchid;
        private Color _putGexColor = Color.Orchid;
        [Category("Series: GEX"), Display(Name = "Mostrar Call Gex / Put Gex", Order = 0)]
        public bool Show_GexPair { get => _showGexPair; set { _showGexPair = value; SetPairVisibility(KEY_CALL_GEX, KEY_PUT_GEX, value); } }
        [Category("Series: GEX"), Display(Name = "Call Gex color", Order = 1)]
        public Color CallGexColor { get => _callGexColor; set { _callGexColor = value; ApplySeriesStyle(); } }
        [Category("Series: GEX"), Display(Name = "Put Gex color", Order = 2)]
        public Color PutGexColor { get => _putGexColor; set { _putGexColor = value; ApplySeriesStyle(); } }

        private bool _showBullBearPair = true;
        private Color _bullColor = Color.LimeGreen;
        private Color _bearColor = Color.OrangeRed;
        [Category("Series: Bull/Bear"), Display(Name = "Mostrar BULL / BEAR", Order = 0)]
        public bool Show_BullBearPair { get => _showBullBearPair; set { _showBullBearPair = value; SetPairVisibility(KEY_BULL, KEY_BEAR, value); } }
        [Category("Series: Bull/Bear"), Display(Name = "BULL color", Order = 1)]
        public Color BullColor { get => _bullColor; set { _bullColor = value; ApplySeriesStyle(); } }
        [Category("Series: Bull/Bear"), Display(Name = "BEAR color", Order = 2)]
        public Color BearColor { get => _bearColor; set { _bearColor = value; ApplySeriesStyle(); } }

        private ValueDataSeries _highSeries;
        private ValueDataSeries _lowSeries;
        private ValueDataSeries _zeroSeries;
        private ValueDataSeries _extra1Series;
        private ValueDataSeries _extra2Series;

        public ATM() : base(true)
        {
            // Panel separado
            DenyToChangePanel = false;
            DrawAbovePrice = false;

            // Habilitar dibujo custom como en indicadores oficiales
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);
            SubscribeToDrawingEvents(DrawingLayouts.Final);

            // Definiciones de series con selectores
            _defs.AddRange(new[]
            {
                new SeriesDef{ Key = KEY_CALL_CASH, Display = KEY_CALL_CASH, Color = _callCashColor, Selector = r => r.CallCash },
                new SeriesDef{ Key = KEY_PUT_CASH, Display = KEY_PUT_CASH, Color = _putCashColor, Selector = r => r.PutCash },
                new SeriesDef{ Key = KEY_IV_CALLS, Display = KEY_IV_CALLS, Color = _ivCallColor, Selector = r => r.IvCalls },
                new SeriesDef{ Key = KEY_IV_PUTS, Display = KEY_IV_PUTS, Color = _ivPutColor, Selector = r => r.IvPuts },
                new SeriesDef{ Key = KEY_DCALL, Display = KEY_DCALL, Color = _dCallColor, Selector = r => r.DCall },
                new SeriesDef{ Key = KEY_DPUTS, Display = KEY_DPUTS, Color = _dPutsColor, Selector = r => r.DPuts },
                new SeriesDef{ Key = KEY_CALL_GEX, Display = KEY_CALL_GEX, Color = _callGexColor, Selector = r => r.CallGex },
                new SeriesDef{ Key = KEY_PUT_GEX, Display = KEY_PUT_GEX, Color = _putGexColor, Selector = r => r.PutGex },
                new SeriesDef{ Key = KEY_BULL, Display = KEY_BULL, Color = _bullColor, Selector = r => r.Bull },
                new SeriesDef{ Key = KEY_BEAR, Display = KEY_BEAR, Color = _bearColor, Selector = r => r.Bear },
            });

            for (int i = 0; i < _defs.Count; i++)
            {
                var d = _defs[i];
                var ser = new ValueDataSeries(d.Display)
                {
                    VisualType = VisualMode.Line,
                    Width = _lineThickness,
                    Color = ToCross(d.Color)
                };
                d.Series = ser;
                if (i == 0)
                    DataSeries[0] = ser;
                else
                    DataSeries.Add(ser);
            }

            // Series constantes de líneas: Zero / High / Low (como en KAUFMAN)
            _zeroSeries = new ValueDataSeries("ZeroLine")
            {
                VisualType = _showZeroLine ? VisualMode.Line : VisualMode.Hide,
                Width = 1,
                Color = ToCross(_zeroLineColor),
                ShowCurrentValue = false,
                ShowZeroValue = false
            };
            _highSeries = new ValueDataSeries("HighLine")
            {
                VisualType = _showHighLine ? VisualMode.Line : VisualMode.Hide,
                Width = _highLineThickness,
                Color = ToCross(_highLineColor),
                ShowCurrentValue = false,
                ShowZeroValue = false
            };
            _lowSeries = new ValueDataSeries("LowLine")
            {
                VisualType = _showLowLine ? VisualMode.Line : VisualMode.Hide,
                Width = _lowLineThickness,
                Color = ToCross(_lowLineColor),
                ShowCurrentValue = false,
                ShowZeroValue = false
            };
            _extra1Series = new ValueDataSeries("ExtraLine1")
            {
                VisualType = _showExtra1Line ? VisualMode.Line : VisualMode.Hide,
                Width = _extra1Thickness,
                Color = ToCross(_extra1Color),
                ShowCurrentValue = false,
                ShowZeroValue = false
            };
            _extra2Series = new ValueDataSeries("ExtraLine2")
            {
                VisualType = _showExtra2Line ? VisualMode.Line : VisualMode.Hide,
                Width = _extra2Thickness,
                Color = ToCross(_extra2Color),
                ShowCurrentValue = false,
                ShowZeroValue = false
            };
            DataSeries.Add(_zeroSeries);
            DataSeries.Add(_highSeries);
            DataSeries.Add(_lowSeries);
            DataSeries.Add(_extra1Series);
            DataSeries.Add(_extra2Series);

            _defByKey = _defs.ToDictionary(x => x.Key, x => x, StringComparer.OrdinalIgnoreCase);
        }

        protected override void OnInitialize()
        {
            ApplyConstLinesStyle();
            TryReloadCsv(true);
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            if ((DateTime.UtcNow - _lastLoadUtc).TotalSeconds >= _refreshSeconds)
                TryReloadCsv();

            var candle = GetCandle(bar);
            if (candle == null)
            {
                foreach (var d in _defs) d.Series[bar] = 0m;
                if (_zeroSeries != null) _zeroSeries[bar] = 0m;
                if (_highSeries != null) _highSeries[bar] = _highLevel;
                if (_lowSeries != null) _lowSeries[bar] = _lowLevel;
                return;
            }
            var tsBar = candle.Time;
            if (_gmtOffsetHours != 0) tsBar = tsBar.AddHours(-_gmtOffsetHours);
            if (!CsvTimeIsLocal)
            {
                try { tsBar = tsBar.AddHours(-(InstrumentInfo?.TimeZone ?? 0)); } catch { }
            }

            // Precio ATM: si está habilitado, tomar del histórico de Price en el timestamp de la vela
            decimal atmPrice = value;
            if (_useCsvPriceForAtm)
            {
                var p = FindPriceAt(tsBar);
                if (p > 0) atmPrice = p;
                else if (value > 0) atmPrice = value;
                else if (_lastCsvPrice > 0) atmPrice = _lastCsvPrice;
            }

            if (atmPrice <= 0)
            {
                foreach (var d in _defs) d.Series[bar] = 0m;
                if (_zeroSeries != null) _zeroSeries[bar] = 0m;
                if (_highSeries != null) _highSeries[bar] = _highLevel;
                if (_lowSeries != null) _lowSeries[bar] = _lowLevel;
                return;
            }

            var floorStrike = Math.Floor(atmPrice);
            var ceilStrike = floorStrike + 1m;

            // Calcular valores por serie
            foreach (var d in _defs)
            {
                decimal val = 0m;

                if (d.Key.Equals(KEY_IV_CALLS, StringComparison.OrdinalIgnoreCase) ||
                    d.Key.Equals(KEY_IV_PUTS, StringComparison.OrdinalIgnoreCase))
                {
                    // Sumar N strikes abajo y N arriba alrededor del ATM (floor/ceil base)
                    int range = Math.Max(1, _ivStrikesRange);
                    lock (_sync)
                    {
                        for (int off = 0; off < range; off++)
                        {
                            var sDown = floorStrike - off;
                            var sUp = ceilStrike + off;
                            if (_histByStrike.TryGetValue(sDown, out var listD))
                            {
                                var h = FindHist(listD, tsBar);
                                if (h != null) val += SelectHist(d.Key, h);
                            }
                            if (_histByStrike.TryGetValue(sUp, out var listU))
                            {
                                var h = FindHist(listU, tsBar);
                                if (h != null) val += SelectHist(d.Key, h);
                            }
                        }
                    }
                }
                else
                {
                    StrikeHist h1 = null, h2 = null;
                    lock (_sync)
                    {
                        if (_histByStrike.TryGetValue(floorStrike, out var list1)) h1 = FindHist(list1, tsBar);
                        if (_histByStrike.TryGetValue(ceilStrike, out var list2)) h2 = FindHist(list2, tsBar);
                    }
                    if (h1 != null) val += SelectHist(d.Key, h1);
                    if (h2 != null) val += SelectHist(d.Key, h2);
                }

                d.Series[bar] = _emaPeriod > 1 ? ComputeEma(d.Series, bar, val) : val;
            }

            // Actualizar líneas constantes por barra
            if (_zeroSeries != null) _zeroSeries[bar] = 0m;
            if (_highSeries != null) _highSeries[bar] = _highLevel;
            if (_lowSeries != null) _lowSeries[bar] = _lowLevel;
            if (_extra1Series != null) _extra1Series[bar] = _extra1Level;
            if (_extra2Series != null) _extra2Series[bar] = _extra2Level;
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            base.OnRender(context, layout);
            if (ChartInfo == null)
                return;

            // Sólo status e info; las líneas se dibujan como series para respetar la escala del panel
            if (!string.IsNullOrEmpty(_status))
                context.DrawString(_status, new RenderFont("Arial", 9), Color.Gray, 6, 6);

            var info = $"ATM Price={_lastCsvPrice:0.##} Floor={Math.Floor(_lastCsvPrice)} Ceil={Math.Floor(_lastCsvPrice)+1}";
            context.DrawString(info, new RenderFont("Arial", 9), Color.LightGray, 6, 18);
        }

        private void ForceReload()
        {
            _lastLoadUtc = DateTime.MinValue;
            TryReloadCsv(true);
            RecalculateValues();
            RedrawChart();
        }

        private void TryReloadCsv(bool force = false)
        {
            if (!force && (DateTime.UtcNow - _lastLoadUtc).TotalSeconds < 1)
                return;
            try
            {
                var path = _csvPath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    lock (_sync)
                    {
                        _rows.Clear();
                        _rowsByStrike = new();
                        _histByStrike.Clear();
                        _priceHist.Clear();
                        _lastCsvPrice = 0m;
                        _status = $"CSV no encontrado: {path}";
                    }
                    _lastLoadUtc = DateTime.UtcNow; return;
                }

                var lines = File.ReadAllLines(path);
                if (lines.Length <= 1)
                {
                    lock (_sync)
                    {
                        _rows.Clear();
                        _rowsByStrike = new();
                        _histByStrike.Clear();
                        _priceHist.Clear();
                        _lastCsvPrice = 0m;
                        _status = "CSV vacío";
                    }
                    _lastLoadUtc = DateTime.UtcNow; return;
                }

                string[] SplitFlex(string line)
                {
                    if (string.IsNullOrEmpty(line)) return Array.Empty<string>();
                    if (line.Contains('\t')) return line.Split('\t');
                    return SplitCsvLine(line);
                }

                var header = SplitFlex(lines[0]);
                if (header.Length == 1 && lines[0].Contains('\t')) header = lines[0].Split('\t');

                int idxStrike = FindIndex(header, "strike");
                int idxTs = FindIndex(header, "timestamp");
                int idxPrice = FindIndex(header, "price");
                int idxCallCash = FindIndex(header, KEY_CALL_CASH);
                int idxPutCash = FindIndex(header, KEY_PUT_CASH);
                int idxIvCalls = FindIndex(header, KEY_IV_CALLS);
                int idxIvPuts = FindIndex(header, KEY_IV_PUTS);
                int idxDCall = FindIndex(header, KEY_DCALL);
                int idxDPuts = FindIndex(header, KEY_DPUTS);
                int idxCallGex = FindIndex(header, KEY_CALL_GEX);
                int idxPutGex = FindIndex(header, KEY_PUT_GEX);
                int idxBull = FindIndex(header, KEY_BULL);
                int idxBear = FindIndex(header, KEY_BEAR);

                var tempHist = new Dictionary<decimal, List<StrikeHist>>();
                var priceTemp = new List<(DateTime, decimal)>();
                decimal lastPriceLocal = 0m;
                int errorRows = 0;

                for (int i = 1; i < lines.Length; i++)
                {
                    var cols = SplitFlex(lines[i]);
                    if (cols.Length == 0) continue;

                    // Determinar timestamp de la fila
                    DateTime tsRow;
                    if (TryParseDate(cols[0], out tsRow))
                    {
                        // ok
                    }
                    else if (idxTs >= 0 && idxTs < cols.Length && TryParseDate(cols[idxTs], out tsRow))
                    {
                        // ok
                    }
                    else
                    {
                        // escanear cualquier columna que sea fecha
                        tsRow = DateTime.MinValue;
                        for (int c = 0; c < cols.Length; c++)
                        {
                            if (TryParseDate(cols[c], out tsRow)) break;
                        }
                        if (tsRow == DateTime.MinValue) { errorRows++; continue; }
                    }

                    // Determinar strike de la fila
                    decimal strikeVal = 0m;
                    if (TryParseDate(cols[0], out _))
                    {
                        if (cols.Length > 1) TryParseDecimal(cols[1], out strikeVal);
                    }
                    if (strikeVal == 0m && idxStrike >= 0 && idxStrike < cols.Length)
                        TryParseDecimal(cols[idxStrike], out strikeVal);
                    if (strikeVal == 0m)
                    {
                        // fallback: buscar primera celda numérica razonable
                        for (int c = 0; c < cols.Length; c++)
                        {
                            if (TryParseDecimal(cols[c], out var cand) && cand > 0 && cand < 1000000)
                            {
                                strikeVal = cand; break;
                            }
                        }
                    }
                    if (strikeVal <= 0) { errorRows++; continue; }

                    if (!CsvTimeIsLocal)
                    {
                        try { tsRow = tsRow.ToUniversalTime(); tsRow = tsRow.AddHours(InstrumentInfo?.TimeZone ?? 0); } catch { }
                    }

                    // Price histórico por timestamp
                    if (idxPrice >= 0 && idxPrice < cols.Length && TryParseDecimal(cols[idxPrice], out var p) && p > 0)
                    {
                        priceTemp.Add((tsRow, p));
                        lastPriceLocal = p;
                    }

                    var h = new StrikeHist { Ts = tsRow };
                    if (idxCallCash >= 0 && idxCallCash < cols.Length && TryParseDecimal(cols[idxCallCash], out var tmp)) h.CallCash = tmp;
                    if (idxPutCash >= 0 && idxPutCash < cols.Length && TryParseDecimal(cols[idxPutCash], out tmp)) h.PutCash = tmp;
                    if (idxIvCalls >= 0 && idxIvCalls < cols.Length && TryParseDecimal(cols[idxIvCalls], out tmp)) h.IvCalls = tmp;
                    if (idxIvPuts >= 0 && idxIvPuts < cols.Length && TryParseDecimal(cols[idxIvPuts], out tmp)) h.IvPuts = tmp;
                    if (idxDCall >= 0 && idxDCall < cols.Length && TryParseDecimal(cols[idxDCall], out tmp)) h.DCall = tmp;
                    if (idxDPuts >= 0 && idxDPuts < cols.Length && TryParseDecimal(cols[idxDPuts], out tmp)) h.DPuts = tmp;
                    if (idxCallGex >= 0 && idxCallGex < cols.Length && TryParseDecimal(cols[idxCallGex], out tmp)) h.CallGex = tmp;
                    if (idxPutGex >= 0 && idxPutGex < cols.Length && TryParseDecimal(cols[idxPutGex], out tmp)) h.PutGex = tmp;
                    if (idxBull >= 0 && idxBull < cols.Length && TryParseDecimal(cols[idxBull], out tmp)) h.Bull = tmp;
                    if (idxBear >= 0 && idxBear < cols.Length && TryParseDecimal(cols[idxBear], out tmp)) h.Bear = tmp;

                    if (!tempHist.TryGetValue(strikeVal, out var list))
                    {
                        list = new List<StrikeHist>();
                        tempHist[strikeVal] = list;
                    }
                    list.Add(h);
                }

                // Ordenar
                foreach (var kv in tempHist)
                    kv.Value.Sort((a, b) => a.Ts.CompareTo(b.Ts));
                priceTemp.Sort((a, b) => a.Item1.CompareTo(b.Item1));

                lock (_sync)
                {
                    _histByStrike.Clear();
                    foreach (var kv in tempHist) _histByStrike[kv.Key] = kv.Value;
                    _priceHist.Clear();
                    _priceHist.AddRange(priceTemp);
                    if (lastPriceLocal > 0) _lastCsvPrice = lastPriceLocal;
                    _status = $"Strikes hist: {_histByStrike.Count} Prices: {_priceHist.Count} err={errorRows} @{DateTime.Now:HH:mm:ss}";
                }
                _lastLoadUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _histByStrike.Clear();
                    _priceHist.Clear();
                    _status = ex.Message;
                }
                _lastLoadUtc = DateTime.UtcNow;
            }
        }

        private StrikeHist FindHist(List<StrikeHist> list, DateTime ts)
        {
            if (list.Count == 0) return null;
            int lo = 0, hi = list.Count - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (list[mid].Ts <= ts) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans >= 0 ? list[ans] : list[0];
        }

        private decimal SelectHist(string key, StrikeHist h)
        {
            if (h == null) return 0m;
            if (key.Equals(KEY_CALL_CASH, StringComparison.OrdinalIgnoreCase)) return h.CallCash;
            if (key.Equals(KEY_PUT_CASH, StringComparison.OrdinalIgnoreCase)) return h.PutCash;
            if (key.Equals(KEY_IV_CALLS, StringComparison.OrdinalIgnoreCase)) return h.IvCalls;
            if (key.Equals(KEY_IV_PUTS, StringComparison.OrdinalIgnoreCase)) return h.IvPuts;
            if (key.Equals(KEY_DCALL, StringComparison.OrdinalIgnoreCase)) return h.DCall;
            if (key.Equals(KEY_DPUTS, StringComparison.OrdinalIgnoreCase)) return h.DPuts;
            if (key.Equals(KEY_CALL_GEX, StringComparison.OrdinalIgnoreCase)) return h.CallGex;
            if (key.Equals(KEY_PUT_GEX, StringComparison.OrdinalIgnoreCase)) return h.PutGex;
            if (key.Equals(KEY_BULL, StringComparison.OrdinalIgnoreCase)) return h.Bull;
            if (key.Equals(KEY_BEAR, StringComparison.OrdinalIgnoreCase)) return h.Bear;
            return 0m;
        }

        private decimal ComputeEma(ValueDataSeries s, int bar, decimal raw)
        {
            if (_emaPeriod <= 1) return raw;
            if (bar == 0) return raw;
            var prev = s[bar - 1];
            var k = 2m / (_emaPeriod + 1m);
            return prev * (1m - k) + raw * k;
        }

        private void ApplySeriesStyle()
        {
            foreach (var d in _defs)
            {
                if (d.Series == null) continue;
                d.Series.Width = _lineThickness;
                if (d.Key.Equals(KEY_CALL_CASH, StringComparison.OrdinalIgnoreCase)) d.Series.Color = ToCross(_callCashColor);
                else if (d.Key.Equals(KEY_PUT_CASH, StringComparison.OrdinalIgnoreCase)) d.Series.Color = ToCross(_putCashColor);
                else if (d.Key.Equals(KEY_IV_CALLS, StringComparison.OrdinalIgnoreCase)) d.Series.Color = ToCross(_ivCallColor);
                else if (d.Key.Equals(KEY_IV_PUTS, StringComparison.OrdinalIgnoreCase)) d.Series.Color = ToCross(_ivPutColor);
                else if (d.Key.Equals(KEY_DCALL, StringComparison.OrdinalIgnoreCase)) d.Series.Color = ToCross(_dCallColor);
                else if (d.Key.Equals(KEY_DPUTS, StringComparison.OrdinalIgnoreCase)) d.Series.Color = ToCross(_dPutsColor);
                else if (d.Key.Equals(KEY_CALL_GEX, StringComparison.OrdinalIgnoreCase)) d.Series.Color = ToCross(_callGexColor);
                else if (d.Key.Equals(KEY_PUT_GEX, StringComparison.OrdinalIgnoreCase)) d.Series.Color = ToCross(_putGexColor);
                else if (d.Key.Equals(KEY_BULL, StringComparison.OrdinalIgnoreCase)) d.Series.Color = ToCross(_bullColor);
                else if (d.Key.Equals(KEY_BEAR, StringComparison.OrdinalIgnoreCase)) d.Series.Color = ToCross(_bearColor);
            }
            ApplyConstLinesStyle();
            RedrawChart();
        }

        private void ApplyConstLinesStyle()
        {
            if (_zeroSeries != null)
            {
                _zeroSeries.VisualType = _showZeroLine ? VisualMode.Line : VisualMode.Hide;
                _zeroSeries.Width = 1;
                _zeroSeries.Color = ToCross(_zeroLineColor);
            }
            if (_highSeries != null)
            {
                _highSeries.VisualType = _showHighLine ? VisualMode.Line : VisualMode.Hide;
                _highSeries.Width = _highLineThickness;
                _highSeries.Color = ToCross(_highLineColor);
            }
            if (_lowSeries != null)
            {
                _lowSeries.VisualType = _showLowLine ? VisualMode.Line : VisualMode.Hide;
                _lowSeries.Width = _lowLineThickness;
                _lowSeries.Color = ToCross(_lowLineColor);
            }
            if (_extra1Series != null)
            {
                _extra1Series.VisualType = _showExtra1Line ? VisualMode.Line : VisualMode.Hide;
                _extra1Series.Width = _extra1Thickness;
                _extra1Series.Color = ToCross(_extra1Color);
            }
            if (_extra2Series != null)
            {
                _extra2Series.VisualType = _showExtra2Line ? VisualMode.Line : VisualMode.Hide;
                _extra2Series.Width = _extra2Thickness;
                _extra2Series.Color = ToCross(_extra2Color);
            }
        }

        private void SetPairVisibility(string k1, string k2, bool vis)
        {
            SetSeriesVisibility(k1, vis);
            SetSeriesVisibility(k2, vis);
        }

        private void SetSeriesVisibility(string key, bool vis)
        {
            if (_defByKey.TryGetValue(key, out var def) && def.Series != null)
            {
                def.Series.VisualType = vis ? VisualMode.Line : VisualMode.Hide;
                RedrawChart();
            }
        }

        private bool TryParseDate(string? s, out DateTime dt)
        {
            dt = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().Trim('"');
            // Reemplazar posible 'T'
            s = s.Replace('T', ' ');
            // Intentos directos comunes
            if (DateTime.TryParseExact(s, _timeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return true;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt)) return true;
            return false;
        }

        private static bool TryParseDecimal(string? s, out decimal value)
        {
            value = 0m;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().Trim('"');
            if (s.StartsWith("Error", StringComparison.OrdinalIgnoreCase)) return false;
            s = s.Replace("$", string.Empty).Replace(" ", string.Empty);
            if (s.Contains(',') && s.Contains('.')) s = s.Replace(",", string.Empty);
            else if (s.Count(ch => ch == ',') == 1 && !s.Contains('.')) s = s.Replace(',', '.');
            return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        private static int FindIndex(string[] headers, string key)
        {
            key = (key ?? string.Empty).Trim().ToLowerInvariant();
            string Normalize(string s) => new string((s ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            var normKey = Normalize(key);
            for (int i = 0; i < headers.Length; i++)
            {
                var hNorm = Normalize(headers[i]);
                if (hNorm.Contains(normKey)) return i;
            }
            return -1;
        }

        private static string Get(string[] cols, int idx) => idx >= 0 && idx < cols.Length ? cols[idx] : string.Empty;

        private static string[] SplitCsvLine(string line)
        {
            var list = new List<string>();
            bool inQuotes = false; var cur = new System.Text.StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes) { list.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            list.Add(cur.ToString());
            return list.ToArray();
        }

        private static CrossColor ToCross(Color c) => CrossColor.FromArgb(c.A, c.R, c.G, c.B);

        private decimal FindPriceAt(DateTime ts)
        {
            if (_priceHist == null || _priceHist.Count == 0) return 0m;
            if (ts < _priceHist[0].Ts) return 0m;
            int lo = 0, hi = _priceHist.Count - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_priceHist[mid].Ts <= ts) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans >= 0 ? _priceHist[ans].Price : 0m;
        }

        private int SafeGetY(decimal v)
        {
            try
            {
                // Intento usar mapeo de panel (no precio). Si falla, fallback 0.
                return ChartInfo.GetYByPrice(v, false);
            }
            catch { return 0; }
        }
    }
}
