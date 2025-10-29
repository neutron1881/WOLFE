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
    [DisplayName("SPY CSV Delta Net")]
    public class SpyCsvDeltaNet : Indicator
    {
        private readonly object _sync = new();
        private DateTime _lastLoadUtc = DateTime.MinValue;
        private Dictionary<string, List<(DateTime ts, decimal value)>> _pointsBy = new(StringComparer.OrdinalIgnoreCase);
        private string _status = string.Empty;

        // Definición de series
        private sealed class SeriesDef
        {
            public string Key;      // nombre/clave de la columna a buscar en CSV
            public string Display;  // nombre visible en leyenda
            public Color Color;     // color sugerido
            public ValueDataSeries Series; // serie visual
        }
        private readonly List<SeriesDef> _defs = new();
        private Dictionary<string, SeriesDef> _defByKey = new(StringComparer.OrdinalIgnoreCase);

        // Props de datos
        private string _csvPath = "C\\SPY.csv";
        [Category("Datos"), Display(Name = "Ruta CSV SPY", Order = 0)]
        public string CsvPath { get => _csvPath; set { _csvPath = value ?? string.Empty; ForceReload(); } }

        private int _refreshSeconds = 30;
        [Category("Datos"), Display(Name = "Refresco (seg)", Order = 1)]
        [Range(5, 3600)]
        public int RefreshSeconds { get => _refreshSeconds; set { _refreshSeconds = Math.Max(5, value); } }

        private bool _csvTimeIsLocal = true;
        [Category("Datos"), Display(Name = "Timestamp CSV es local", Order = 2)]
        public bool CsvTimeIsLocal { get => _csvTimeIsLocal; set { _csvTimeIsLocal = value; ForceReload(); } }

        private string _timeColumn = "timestamp";
        [Category("Datos"), Display(Name = "Columna tiempo", Order = 3)]
        public string TimeColumn { get => _timeColumn; set { _timeColumn = value ?? string.Empty; ForceReload(); } }

        private string _deltaColumn = "Net Delta"; // columna principal de la primera serie
        [Category("Datos"), Display(Name = "Columna principal (serie 1)", Order = 4)]
        public string PrimaryColumn
        {
            get => _deltaColumn;
            set
            {
                _deltaColumn = value ?? string.Empty;
                if (_defs.Count > 0)
                    _defs[0].Key = _deltaColumn; // actualizar clave de serie 1
                ForceReload();
            }
        }

        // Ajuste horario (GMT)
        private int _gmtOffsetHours = 0;
        [Category("Datos"), Display(Name = "GMT offset (horas)", Order = 5)]
        [Range(-24, 24)]
        public int GmtOffsetHours
        {
            get => _gmtOffsetHours;
            set { _gmtOffsetHours = Math.Clamp(value, -24, 24); RecalculateValues(); }
        }

        // Suavizado / visual
        private int _emaPeriod = 14;
        [Category("Visual"), Display(Name = "EMA Period", Order = 10)]
        [Range(1, 500)]
        public int EmaPeriod { get => _emaPeriod; set { _emaPeriod = Math.Max(1, value); RecalculateValues(); } }

        private Color _primaryColor = Color.DeepSkyBlue;
        [Category("Visual"), Display(Name = "Color serie 1", Order = 11)]
        public Color PrimaryColor { get => _primaryColor; set { _primaryColor = value; ApplySeriesStyle(); } }

        private int _lineThickness = 2;
        [Category("Visual"), Display(Name = "Grosor líneas", Order = 12)]
        [Range(1, 8)]
        public int LineThickness { get => _lineThickness; set { _lineThickness = Math.Clamp(value, 1, 8); ApplySeriesStyle(); } }

        private bool _showZeroLine = true;
        [Category("Visual"), Display(Name = "Mostrar línea 0", Order = 13)]
        public bool ShowZeroLine { get => _showZeroLine; set { _showZeroLine = value; RedrawChart(); } }

        private Color _zeroLineColor = Color.FromArgb(120, 180, 180, 180);
        [Category("Visual"), Display(Name = "Color línea 0", Order = 14)]
        public Color ZeroLineColor { get => _zeroLineColor; set { _zeroLineColor = value; RedrawChart(); } }

        public SpyCsvDeltaNet() : base(true)
        {
            // Panel separado: no dibujar sobre el precio
            DenyToChangePanel = false;
            DrawAbovePrice = false;

            // Definir todas las series solicitadas (display y clave a buscar en CSV)
            // 0) Serie principal configurable
            _defs.Add(new SeriesDef { Key = _deltaColumn, Display = "Net Delta", Color = _primaryColor });

            // 1.. restantes según lista del usuario
            _defs.AddRange(new[]
            {
                new SeriesDef { Key = "Call Vol",   Display = "Call Vol",   Color = Color.LimeGreen },
                new SeriesDef { Key = "Put Vol",    Display = "Put Vol",    Color = Color.OrangeRed },
                new SeriesDef { Key = "Net Gex",    Display = "Net Gex",    Color = Color.MediumPurple },
                new SeriesDef { Key = "Total IO",   Display = "Total IO",   Color = Color.Goldenrod },
                new SeriesDef { Key = "Net IO",     Display = "Net IO",     Color = Color.DarkKhaki },
                new SeriesDef { Key = "Call OI",    Display = "Call OI",    Color = Color.SeaGreen },
                new SeriesDef { Key = "Put OI",     Display = "Put OI",     Color = Color.IndianRed },
                new SeriesDef { Key = "Net Vol",    Display = "Net Vol",    Color = Color.DeepSkyBlue },
                new SeriesDef { Key = "Cash Call",  Display = "Cash Call",  Color = Color.Teal },
                new SeriesDef { Key = "Cash Put",   Display = "Cash Put",   Color = Color.Maroon },
                new SeriesDef { Key = "IV Call",    Display = "IV Call",    Color = Color.MediumSeaGreen },
                new SeriesDef { Key = "IV Put",     Display = "IV Put",     Color = Color.Salmon },
                new SeriesDef { Key = "Call Delta", Display = "Call Delta", Color = Color.SteelBlue },
                new SeriesDef { Key = "Delta Put",  Display = "Put Delta",  Color = Color.Sienna },
                new SeriesDef { Key = "Cash Neto",  Display = "Cash Neto",  Color = Color.DarkCyan },
                new SeriesDef { Key = "IV Neta",    Display = "IV Neta",    Color = Color.MediumVioletRed },
                new SeriesDef { Key = "Delta Neto", Display = "Delta Neto", Color = Color.RoyalBlue },

                // Nuevas columnas de Size
                new SeriesDef { Key = "CallAsk",    Display = "Call Ask",    Color = Color.ForestGreen },
                new SeriesDef { Key = "CallBid",    Display = "Call Bid",    Color = Color.DarkGreen },
                new SeriesDef { Key = "PutAsk",     Display = "Put Ask",     Color = Color.OrangeRed },
                new SeriesDef { Key = "PutBid",     Display = "Put Bid",     Color = Color.Firebrick },
                new SeriesDef { Key = "TotalCall",  Display = "Total Call",  Color = Color.DarkBlue },
                new SeriesDef { Key = "TotalPut",   Display = "Total Put",   Color = Color.DarkRed },
                new SeriesDef { Key = "SNeto",      Display = "SNeto",      Color = Color.Black },
            });

            // Crear y registrar ValueDataSeries para cada definición
            for (int i = 0; i < _defs.Count; i++)
            {
                var def = _defs[i];
                var color = i == 0 ? _primaryColor : def.Color;
                var ser = new ValueDataSeries(def.Display)
                {
                    VisualType = VisualMode.Line,
                    Color = ToCross(color),
                    Width = _lineThickness
                };
                def.Series = ser;

                if (i == 0)
                    DataSeries[0] = ser; // usar slot 0
                else
                    DataSeries.Add(ser);
            }

            _defByKey = _defs.ToDictionary(d => d.Key, d => d, StringComparer.OrdinalIgnoreCase);
        }

        protected override void OnInitialize()
        {
            TryReloadCsv(force: true);
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            // Cargar/actualizar puntos periódicamente
            if ((DateTime.UtcNow - _lastLoadUtc).TotalSeconds >= _refreshSeconds)
                TryReloadCsv();

            var candle = GetCandle(bar);
            if (candle is null)
            {
                foreach (var d in _defs)
                    d.Series[bar] = 0m;
                return;
            }

            var ts = candle.Time;
            if (!CsvTimeIsLocal)
            {
                try { ts = candle.Time.AddHours(-(InstrumentInfo?.TimeZone ?? 0)); } catch { }
            }
            // Aplica el desplazamiento GMT (positivo: adelanta los datos del CSV respecto al gráfico)
            if (_gmtOffsetHours != 0)
                ts = ts.AddHours(-_gmtOffsetHours);

            // Asignar valores a cada serie (EMA común)
            foreach (var d in _defs)
            {
                var raw = GetValueAt(d.Key, ts);
                var ema = ComputeEma(d.Series, bar, raw);
                d.Series[bar] = ema;
            }
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            base.OnRender(context, layout);
            if (!_showZeroLine || ChartInfo?.PriceChartContainer == null)
                return;

            // Dibujar línea horizontal en 0 en el panel del indicador
            try
            {
                int x1 = 0;
                int x2 = Container.Region.Right;
                int y = ChartInfo.PriceChartContainer.GetYByPrice(0m, true); // true -> escala del panel
                context.DrawLine(new RenderPen(_zeroLineColor, 1), x1, y, x2, y);
            }
            catch { }
        }

        private void ApplySeriesStyle()
        {
            // Actualizar colores/grosor
            for (int i = 0; i < _defs.Count; i++)
            {
                var def = _defs[i];
                if (def.Series == null) continue;
                def.Series.Width = _lineThickness;
                if (i == 0)
                    def.Series.Color = ToCross(_primaryColor);
                else
                    def.Series.Color = ToCross(def.Color);
            }
            RedrawChart();
        }

        private void ForceReload()
        {
            _lastLoadUtc = DateTime.MinValue;
            TryReloadCsv(force: true);
            RecalculateValues();
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
                        _pointsBy = new(StringComparer.OrdinalIgnoreCase);
                        _status = $"CSV no encontrado: {path}";
                    }
                    _lastLoadUtc = DateTime.UtcNow;
                    return;
                }

                var lines = File.ReadAllLines(path);
                if (lines.Length <= 1)
                {
                    lock (_sync)
                    {
                        _pointsBy = new(StringComparer.OrdinalIgnoreCase);
                        _status = "CSV vacío";
                    }
                    _lastLoadUtc = DateTime.UtcNow;
                    return;
                }

                // Parsear cabeceras y localizar columnas
                var header = SplitCsvLine(lines[0]);

                // Construir lista única de claves a buscar (incluye primaria y todas las de _defs)
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                keys.Add(PrimaryColumn);
                foreach (var d in _defs)
                    keys.Add(d.Key);

                var idxByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var k in keys)
                {
                    int idx = FindIndex(header, k);
                    if (idx >= 0)
                        idxByKey[k] = idx;
                }

                if (!idxByKey.ContainsKey(PrimaryColumn) && _defs.Count > 0)
                {
                    // fallback: si no encuentra la primaria, intentar una genérica con "delta"
                    for (int i = 0; i < header.Length; i++)
                    {
                        var norm = new string((header[i] ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                        if (norm.Contains("delta")) { idxByKey[PrimaryColumn] = i; break; }
                    }
                }

                // Preparar contenedores
                var lists = new Dictionary<string, List<(DateTime, decimal)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var k in idxByKey.Keys)
                    lists[k] = new List<(DateTime, decimal)>(Math.Max(1, lines.Length - 1));

                // Recorrer filas
                int idxTime = FindIndex(header, _timeColumn);
                if (idxTime < 0) idxTime = 0; // fallback

                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var cols = SplitCsvLine(line);
                    if (cols.Length <= idxTime) continue;

                    var timeRaw = cols[idxTime]?.Trim('"', ' ');
                    if (string.IsNullOrEmpty(timeRaw)) continue;

                    DateTime t;
                    if (!DateTime.TryParse(timeRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out t))
                    {
                        if (!DateTime.TryParse(timeRaw, out t))
                            continue;
                    }

                    if (!CsvTimeIsLocal)
                    {
                        try { t = t.ToUniversalTime(); t = t.AddHours(InstrumentInfo?.TimeZone ?? 0); } catch { }
                    }

                    foreach (var kv in idxByKey)
                    {
                        var key = kv.Key; var idx = kv.Value;
                        if (cols.Length <= idx) continue;
                        if (!TryParseDecimal(cols[idx], out var v)) continue;
                        lists[key].Add((t, v));
                    }
                }

                // Ordenar y publicar
                foreach (var k in lists.Keys.ToList())
                    lists[k].Sort((a, b) => a.Item1.CompareTo(b.Item1));

                lock (_sync)
                {
                    _pointsBy = lists.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                    _status = $"Cargado {_pointsBy.Count} columnas @ {DateTime.Now:HH:mm:ss}";
                }
                _lastLoadUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _pointsBy = new(StringComparer.OrdinalIgnoreCase);
                    _status = ex.Message;
                }
                _lastLoadUtc = DateTime.UtcNow;
            }
        }

        private decimal GetValueAt(string key, DateTime ts)
        {
            Dictionary<string, List<(DateTime ts, decimal value)>> snapDict;
            lock (_sync) snapDict = _pointsBy;
            if (!snapDict.TryGetValue(key, out var snapshot) || snapshot.Count == 0)
                return 0m;

            // Buscar último <= ts (búsqueda binaria)
            int lo = 0, hi = snapshot.Count - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (snapshot[mid].ts <= ts) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans >= 0 ? snapshot[ans].value : snapshot[0].value;
        }

        private decimal ComputeEma(ValueDataSeries series, int bar, decimal raw)
        {
            if (_emaPeriod <= 1) return raw;
            if (bar == 0) return raw;
            var prev = series[bar - 1];
            var k = 2m / (_emaPeriod + 1m);
            return prev * (1m - k) + raw * k;
        }

        // ==== Visibilidad por grupos (propiedades de menú) ====
        private void SetSeriesVisibility(string key, bool visible)
        {
            if (_defByKey.TryGetValue(key, out var def) && def.Series != null)
            {
                def.Series.VisualType = visible ? VisualMode.Line : VisualMode.Hide;
                RedrawChart();
            }
        }

        // Delta: Delta Neto, Call Delta, Put Delta
        private bool _showDeltaNeto = true, _showCallDelta = true, _showPutDelta = true;
        [Category("Series: Delta"), Display(Name = "Delta Neto", Order = 0)]
        public bool Show_DeltaNeto { get => _showDeltaNeto; set { _showDeltaNeto = value; SetSeriesVisibility("Delta Neto", value); } }
        [Category("Series: Delta"), Display(Name = "Call Delta", Order = 1)]
        public bool Show_CallDelta { get => _showCallDelta; set { _showCallDelta = value; SetSeriesVisibility("Call Delta", value); } }
        [Category("Series: Delta"), Display(Name = "Put Delta", Order = 2)]
        public bool Show_PutDelta { get => _showPutDelta; set { _showPutDelta = value; SetSeriesVisibility("Delta Put", value); } }

        // IV: IV Call, IV Put, IV Neta
        private bool _showIvCall = true, _showIvPut = true, _showIvNeta = true;
        [Category("Series: IV"), Display(Name = "IV Call", Order = 0)]
        public bool Show_IVCall { get => _showIvCall; set { _showIvCall = value; SetSeriesVisibility("IV Call", value); } }
        [Category("Series: IV"), Display(Name = "IV Put", Order = 1)]
        public bool Show_IVPut { get => _showIvPut; set { _showIvPut = value; SetSeriesVisibility("IV Put", value); } }
        [Category("Series: IV"), Display(Name = "IV Neta", Order = 2)]
        public bool Show_IVNeta { get => _showIvNeta; set { _showIvNeta = value; SetSeriesVisibility("IV Neta", value); } }

        // Cash: Cash Call, Cash Put
        private bool _showCashCall = true, _showCashPut = true;
        [Category("Series: Cash"), Display(Name = "Cash Call", Order = 0)]
        public bool Show_CashCall { get => _showCashCall; set { _showCashCall = value; SetSeriesVisibility("Cash Call", value); } }
        [Category("Series: Cash"), Display(Name = "Cash Put", Order = 1)]
        public bool Show_CashPut { get => _showCashPut; set { _showCashPut = value; SetSeriesVisibility("Cash Put", value); } }
        // Cash Neto
        private bool _showCashNeto = true;
        [Category("Series: Cash"), Display(Name = "Cash Neto", Order = 2)]
        public bool Show_CashNeto { get => _showCashNeto; set { _showCashNeto = value; SetSeriesVisibility("Cash Neto", value); } }

        // Volume: Call Vol, Put Vol, Net Vol
        private bool _showCallVol = true, _showPutVol = true, _showNetVol = true;
        [Category("Series: Volume"), Display(Name = "Call Vol", Order = 0)]
        public bool Show_CallVol { get => _showCallVol; set { _showCallVol = value; SetSeriesVisibility("Call Vol", value); } }
        [Category("Series: Volume"), Display(Name = "Put Vol", Order = 1)]
        public bool Show_PutVol { get => _showPutVol; set { _showPutVol = value; SetSeriesVisibility("Put Vol", value); } }
        [Category("Series: Volume"), Display(Name = "Net Vol", Order = 2)]
        public bool Show_NetVol { get => _showNetVol; set { _showNetVol = value; SetSeriesVisibility("Net Vol", value); } }

        // OI/IO: Call OI, Put OI, Net IO (+opcional Total IO)
        private bool _showCallOi = true, _showPutOi = true, _showNetIo = true, _showTotalIo = true;
        [Category("Series: OI / IO"), Display(Name = "Call OI", Order = 0)]
        public bool Show_CallOI { get => _showCallOi; set { _showCallOi = value; SetSeriesVisibility("Call OI", value); } }
        [Category("Series: OI / IO"), Display(Name = "Put OI", Order = 1)]
        public bool Show_PutOI { get => _showPutOi; set { _showPutOi = value; SetSeriesVisibility("Put OI", value); } }
        [Category("Series: OI / IO"), Display(Name = "Net IO", Order = 2)]
        public bool Show_NetIO { get => _showNetIo; set { _showNetIo = value; SetSeriesVisibility("Net IO", value); } }
        [Category("Series: OI / IO"), Display(Name = "Total IO", Order = 3)]
        public bool Show_TotalIO { get => _showTotalIo; set { _showTotalIo = value; SetSeriesVisibility("Total IO", value); } }

        // Net: Net Delta, Net Gex
        private bool _showNetDelta = true, _showNetGex = true;
        [Category("Series: Net"), Display(Name = "Net Delta", Order = 0)]
        public bool Show_NetDelta { get => _showNetDelta; set { _showNetDelta = value; SetSeriesVisibility("Net Delta", value); } }
        [Category("Series: Net"), Display(Name = "Net Gex", Order = 1)]
        public bool Show_NetGex { get => _showNetGex; set { _showNetGex = value; SetSeriesVisibility("Net Gex", value); } }

        // Size: CallAsk, CallBid, PutAsk, PutBid, TotalCall, TotalPut, SNeto
        private bool _showCallAsk = true, _showCallBid = true, _showPutAsk = true, _showPutBid = true, _showTotalCall = true, _showTotalPut = true, _showSNeto = true;
        [Category("Series: Size"), Display(Name = "Call Ask", Order = 0)]
        public bool Show_CallAsk { get => _showCallAsk; set { _showCallAsk = value; SetSeriesVisibility("CallAsk", value); } }
        [Category("Series: Size"), Display(Name = "Call Bid", Order = 1)]
        public bool Show_CallBid { get => _showCallBid; set { _showCallBid = value; SetSeriesVisibility("CallBid", value); } }
        [Category("Series: Size"), Display(Name = "Put Ask", Order = 2)]
        public bool Show_PutAsk { get => _showPutAsk; set { _showPutAsk = value; SetSeriesVisibility("PutAsk", value); } }
        [Category("Series: Size"), Display(Name = "Put Bid", Order = 3)]
        public bool Show_PutBid { get => _showPutBid; set { _showPutBid = value; SetSeriesVisibility("PutBid", value); } }
        [Category("Series: Size"), Display(Name = "Total Call", Order = 4)]
        public bool Show_TotalCall { get => _showTotalCall; set { _showTotalCall = value; SetSeriesVisibility("TotalCall", value); } }
        [Category("Series: Size"), Display(Name = "Total Put", Order = 5)]
        public bool Show_TotalPut { get => _showTotalPut; set { _showTotalPut = value; SetSeriesVisibility("TotalPut", value); } }
        [Category("Series: Size"), Display(Name = "SNeto", Order = 6)]
        public bool Show_SNeto { get => _showSNeto; set { _showSNeto = value; SetSeriesVisibility("SNeto", value); } }

        // Helpers de parseo/encuentro/colores
        private static bool TryParseDecimal(string? s, out decimal value)
        {
            value = 0m;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().Trim('"').Replace("$", string.Empty).Replace(" ", string.Empty);
            if (s.Contains(',') && s.Contains('.')) s = s.Replace(",", string.Empty);
            else if (s.Count(ch => ch == ',') == 1 && !s.Contains('.')) s = s.Replace(',', '.');
            return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        private static int FindIndex(string[] headers, string key)
        {
            key = (key ?? string.Empty).Trim().ToLowerInvariant();
            for (int i = 0; i < headers.Length; i++)
            {
                var h = headers[i] ?? string.Empty;
                var norm = new string(h.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                var knorm = new string(key.Where(char.IsLetterOrDigit).ToArray());
                if (norm.Contains(knorm)) return i;
            }
            // Fallbacks genéricos
            if (key.Contains("time"))
            {
                var aliases = new[] { "time", "timestamp", "date" };
                for (int i = 0; i < headers.Length; i++)
                {
                    var norm = new string((headers[i] ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                    if (aliases.Any(a => norm.Contains(a))) return i;
                }
            }
            if (key.Contains("delta"))
            {
                for (int i = 0; i < headers.Length; i++)
                {
                    var norm = new string((headers[i] ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                    if (norm.Contains("deltanet") || norm.Contains("netdelta") || norm.Contains("delta")) return i;
                }
            }
            return -1;
        }

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
    }
}
