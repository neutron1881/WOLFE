using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace ATAS.Indicators.Technical
{
    [DisplayName("SpotGamma Panel CSV")]
    public class SpotGammaPanelCsv : Indicator
    {
        private sealed class Metric
        {
            public string Title { get; init; }
            public string[] Keys { get; init; } = Array.Empty<string>();
            public string Format { get; init; } // e.g. "#,0", "+0.##%;-0.##%"
            public string Unit { get; init; } // e.g. "K", "%"
            public bool StickLastPositive { get; init; } // si true, usa último valor > 0 cuando esté vacío o <= 0
        }

        private sealed class Section
        {
            public string Title { get; init; }
            public Color HeaderColor { get; init; }
            public Metric[] Items { get; init; } = Array.Empty<Metric>();
        }

        public enum PanelAlign { Left, Center, Right }

        private readonly List<Section> _sections;

        // Config
        private string _csvPath = @"C:\\Data\\SPY.csv";
        private int _refreshSeconds = 60;
        private int _baseFontSize = 12;
        private PanelAlign _panelAlign = PanelAlign.Right;
        private int _panelOffsetX = 0;
        private int _panelOffsetY = 0;
        private int _panelRightPadding = 10;
        private bool _alternateRows = true;
        private bool _showHeaderStatus = false; // NUEVO
        private Color _rowAlt1 = Color.FromArgb(16, 16, 28);
        private Color _rowAlt2 = Color.FromArgb(30, 30, 48);
        private Color _gridColor = Color.FromArgb(60, 90, 110);
        private Color _titleColor = Color.FromArgb(195, 215, 230);
        private Color _valueColor = Color.White;
        private bool _showBorder = true;
        private Color _borderColor = Color.FromArgb(120, 120, 140);
        private int _columnGap = 8;
        private int _rowPaddingX = 6;
        private int _rowPaddingY = 3;
        private int _headerHeightExtra = 4;

        // Runtime
        private RenderFont _fontNorm;
        private RenderFont _fontBold;
        private RenderFont _fontHeader;

        private volatile bool _readInProgress;
        private DateTime _nextReadUtc = DateTime.MinValue;
        private string _status = "Esperando...";
        private DateTime _lastUpdateLocal = DateTime.MinValue;

        private readonly object _sync = new();
        private Dictionary<string, string> _data = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _stickyPositive = new(StringComparer.OrdinalIgnoreCase);

        public SpotGammaPanelCsv()
        {
            EnableCustomDrawing = true;
            DenyToChangePanel = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);
            SubscribeToDrawingEvents(DrawingLayouts.Final);

            // Secciones solicitadas (incluye UPDATE primero)
            _sections = new()
            {
                // UPDATE
                new Section
                {
                    Title = "UPDATE",
                    HeaderColor = Color.FromArgb(100, 100, 130),
                    Items = new []
                    {
                        // Usa Timestamp del CSV; si no existe, cae a __LastUpdate__ (hora interna de lectura)
                        new Metric{ Title = "Last Update", Keys = new[]{"Timestamp","TimeStamp","LastUpdate","Last Update","__LastUpdate__"}}
                    }
                },
                // VOLUME
                new Section
                {
                    Title = "VOLUME",
                    HeaderColor = Color.FromArgb(60, 110, 220),
                    Items = new []
                    {
                        new Metric{ Title = "Call Vol", Keys = new[]{"CallVol","Call Volume","Call Vol","CallVolK","CallVolume"}},
                        new Metric{ Title = "Put Vol",  Keys = new[]{"PutVol","Put Volume","Put Vol","PutVolK","PutVolume"}},
                        new Metric{ Title = "Net Vol",  Keys = new[]{"NetVol","Net Volume","Net Vol","NetVolume"}}
                    }
                },
                // NET
                new Section
                {
                    Title = "NET",
                    HeaderColor = Color.FromArgb(210, 170, 60),
                    Items = new []
                    {
                        new Metric{ Title = "Net Delta", Keys = new[]{"NetDelta","Net Delta","DeltaNet"}},
                        new Metric{ Title = "Net Gex",   Keys = new[]{"NetGEX","Net Gex","NetGex","GEXNet"}}
                    }
                },
                // OPEN INTEREST
                new Section
                {
                    Title = "OPEN INTEREST",
                    HeaderColor = Color.FromArgb(60, 120, 200),
                    Items = new []
                    {
                        // Añade variantes IO (en CSV vienen como Total IO / Net IO)
                        new Metric{ Title = "Total OI", Keys = new[]{"TotalOI","Total OI","OITotal","TotalIO","Total IO","IOTotal"}},
                        new Metric{ Title = "Net OI",   Keys = new[]{"NetOI","Net OI","NetIO","Net IO"}},
                        new Metric{ Title = "Call OI",  Keys = new[]{"CallOI","Call OI"}},
                        new Metric{ Title = "Put OI",   Keys = new[]{"PutOI","Put OI"}}
                    }
                },
                // FRESH CASH
                new Section
                {
                    Title = "FRESH CASH",
                    HeaderColor = Color.FromArgb(40, 160, 105),
                    Items = new []
                    {
                        new Metric{ Title = "Cash Call", Keys = new[]{"CashCall","Cash Call"}},
                        new Metric{ Title = "Cash Put",  Keys = new[]{"CashPut","Cash Put"}}
                    }
                },
                // IMP VOL
                new Section
                {
                    Title = "IMP VOL",
                    HeaderColor = Color.FromArgb(55, 140, 200),
                    Items = new []
                    {
                        new Metric{ Title = "IV Call", Keys = new[]{"IVCall","IV Call","Call IV"}, StickLastPositive = true},
                        new Metric{ Title = "IV Put",  Keys = new[]{"IVPut","IV Put","Put IV"}, StickLastPositive = true}
                    }
                },
                // DELTA (nueva sección)
                new Section
                {
                    Title = "DELTA",
                    HeaderColor = Color.FromArgb(190, 90, 90),
                    Items = new []
                    {
                        new Metric{ Title = "Call Delta", Keys = new[]{"CallDelta","Call Delta"}},
                        new Metric{ Title = "Put Delta",  Keys = new[]{"PutDelta","Delta Put","Put Delta"}}
                    }
                }
            };
        }

        #region Properties
        [Category("Datos"), Display(Name = "Ruta CSV", Order = 0)]
        public string CsvPath { get => _csvPath; set { if (string.Equals(_csvPath, value, StringComparison.OrdinalIgnoreCase)) return; _csvPath = value ?? string.Empty; ForceReload(); } }

        [Category("Datos"), Display(Name = "Actualizar cada (seg)", Order = 1)]
        [Range(5, 3600)]
        public int RefreshSeconds { get => _refreshSeconds; set { _refreshSeconds = Math.Max(5, value); } }

        [Category("Visual"), Display(Name = "Tamaño fuente base", Order = 0)]
        [Range(8, 28)]
        public int BaseFontSize { get => _baseFontSize; set { var v = Math.Max(8, Math.Min(28, value)); if (v == _baseFontSize) return; _baseFontSize = v; RecreateFonts(); RedrawChart(); } }

        [Category("Panel"), Display(Name = "Posición", Order = 0)]
        public PanelAlign PanelPosition { get => _panelAlign; set { if (value == _panelAlign) return; _panelAlign = value; RedrawChart(); } }

        [Category("Panel"), Display(Name = "Offset X", Order = 1)]
        [Range(-2000, 2000)]
        public int PanelOffsetX { get => _panelOffsetX; set { if (value == _panelOffsetX) return; _panelOffsetX = value; RedrawChart(); } }

        [Category("Panel"), Display(Name = "Offset Y", Order = 2)]
        [Range(-2000, 2000)]
        public int PanelOffsetY { get => _panelOffsetY; set { if (value == _panelOffsetY) return; _panelOffsetY = value; RedrawChart(); } }

        [Category("Panel"), Display(Name = "Padding derecho", Order = 3)]
        [Range(0, 400)]
        public int PanelRightPadding { get => _panelRightPadding; set { if (value == _panelRightPadding) return; _panelRightPadding = Math.Max(0, value); RedrawChart(); } }

        [Category("Visual"), Display(Name = "Filas alternas", Order = 1)]
        public bool AlternateRows { get => _alternateRows; set { if (value == _alternateRows) return; _alternateRows = value; RedrawChart(); } }

        [Category("Visual"), Display(Name = "Mostrar estado en header", Order = 2)]
        public bool ShowHeaderStatus { get => _showHeaderStatus; set { if (value == _showHeaderStatus) return; _showHeaderStatus = value; RedrawChart(); } }

        [Category("Borde"), Display(Name = "Mostrar borde", Order = 0)]
        public bool ShowBorder { get => _showBorder; set { if (value == _showBorder) return; _showBorder = value; RedrawChart(); } }
        #endregion

        protected override void OnInitialize()
        {
            RecreateFonts();
            _nextReadUtc = DateTime.MinValue;
            _ = TryScheduleRead();
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar < CurrentBar - 1) return;
            _ = TryScheduleRead();
        }

        private void RecreateFonts()
        {
            _fontNorm = new RenderFont("Segoe UI", _baseFontSize);
            _fontBold = new RenderFont("Segoe UI", _baseFontSize, FontStyle.Bold);
            _fontHeader = new RenderFont("Segoe UI Semibold", _baseFontSize + 1, FontStyle.Bold);
        }

        private void ForceReload()
        {
            _nextReadUtc = DateTime.MinValue;
            _ = TryScheduleRead();
        }

        private async Task TryScheduleRead()
        {
            var now = DateTime.UtcNow;
            if (_readInProgress || now < _nextReadUtc) return;

            _readInProgress = true;
            _status = "Leyendo...";
            RedrawChart();
            try
            {
                await Task.Run(ReadCsv);
                _status = $"Actualizado {DateTime.Now:HH:mm:ss}";
                _lastUpdateLocal = DateTime.Now;
            }
            catch (Exception ex)
            {
                _status = $"Error: {ex.Message}";
            }
            finally
            {
                _nextReadUtc = DateTime.UtcNow.AddSeconds(_refreshSeconds);
                _readInProgress = false;
                RedrawChart();
            }
        }

        private static string[] SplitCsvLine(string line)
        {
            // Pequeño parser CSV que respeta comas entre comillas
            if (string.IsNullOrEmpty(line)) return Array.Empty<string>();
            var result = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    { sb.Append('"'); i++; }
                    else inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            result.Add(sb.ToString());
            return result.Select(s => s.Trim()).ToArray();
        }

        private void ReadCsv()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(_csvPath))
            {
                lock (_sync) _data = map; // vacío
                throw new FileNotFoundException("CSV no encontrado", _csvPath);
            }

            var lines = File.ReadAllLines(_csvPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();
            if (lines.Length == 0)
            {
                lock (_sync) _data = map;
                return;
            }

            // Dos formatos soportados:
            // 1) Encabezado + filas: header: A,B,C... y usamos la última fila
            // 2) Clave,Valor por fila (2 columnas)
            var first = SplitCsvLine(lines[0]);
            if (first.Length >= 3 || (first.Length >= 2 && lines.Length > 1))
            {
                // Intentar formato 1: header + última fila
                var header = first;
                var lastRow = SplitCsvLine(lines[^1]);
                if (lastRow.Length == header.Length)
                {
                    for (int i = 0; i < header.Length; i++)
                    {
                        var key = header[i];
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        var value = i < lastRow.Length ? lastRow[i] : string.Empty;
                        map[key] = value;
                    }
                }
                else if (first.Length == 2 && lines.All(l => SplitCsvLine(l).Length == 2))
                {
                    // Degrada a formato 2
                    foreach (var l in lines)
                    {
                        var parts = SplitCsvLine(l);
                        if (parts.Length < 2) continue;
                        var key = parts[0];
                        var value = parts[1];
                        if (!string.IsNullOrWhiteSpace(key)) map[key] = value;
                    }
                }
                else
                {
                    // Intentar mapear por el mayor número de pares posibles
                    var headerGuess = first;
                    var dataGuess = SplitCsvLine(lines[Math.Min(1, lines.Length - 1)]);
                    var n = Math.Min(headerGuess.Length, dataGuess.Length);
                    for (int i = 0; i < n; i++)
                    {
                        var key = headerGuess[i];
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        map[key] = dataGuess[i];
                    }
                }
            }
            else if (first.Length == 2)
            {
                // Formato 2: clave,valor por línea
                foreach (var l in lines)
                {
                    var parts = SplitCsvLine(l);
                    if (parts.Length < 2) continue;
                    var key = parts[0];
                    var value = parts[1];
                    if (!string.IsNullOrWhiteSpace(key)) map[key] = value;
                }
            }
            else
            {
                // No reconocible, dejar vacío
            }

            lock (_sync) _data = map;
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo?.PriceChartContainer == null) return;

            Dictionary<string, string> snapshot;
            string status;
            lock (_sync)
            {
                snapshot = new Dictionary<string, string>(_data, StringComparer.OrdinalIgnoreCase);
                status = _status;
            }

            // Alturas basadas en medida real del texto (mejor ajuste con fuentes grandes)
            int normH = MeasureSize(context, "AgjpQqy", _fontNorm).Height;
            int boldH = MeasureSize(context, "AgjpQqy", _fontBold).Height;
            int headerTextH = MeasureSize(context, "HEADER", _fontHeader).Height;
            int textH = Math.Max(normH, boldH);

            // Geometría
            int rowH = Math.Max(_baseFontSize + _rowPaddingY * 2, textH + _rowPaddingY * 2 + 2);
            int headerH = Math.Max(_baseFontSize + _headerHeightExtra + 4, headerTextH + _headerHeightExtra + 4);

            // Calcular ancho de columnas basado en contenido aproximado
            int[] colWidths = new int[_sections.Count];
            int totalWidth = 0;
            for (int c = 0; c < _sections.Count; c++)
            {
                int w = MeasureText(context, _sections[c].Title, _fontHeader) + 20; // header base
                foreach (var m in _sections[c].Items)
                {
                    int wTitle = MeasureText(context, m.Title + "  ", _fontNorm);
                    string tmpVal = GetValue(snapshot, m);
                    int wValue = MeasureText(context, tmpVal, _fontBold);
                    w = Math.Max(w, wTitle + wValue + 22);
                }
                colWidths[c] = Math.Max(160, Math.Min(420, w));
                totalWidth += colWidths[c];
            }
            totalWidth += _columnGap * (_sections.Count - 1);

            int lastBar = CurrentBar - 1;
            int xLast = 800;
            if (lastBar >= 0)
            {
                var tmp = ChartInfo.PriceChartContainer.GetXByBar(lastBar, false);
                if (tmp > 0) xLast = tmp;
            }

            int baseX;
            switch (_panelAlign)
            {
                case PanelAlign.Left:
                    baseX = 0;
                    break;
                case PanelAlign.Center:
                    baseX = Math.Max(0, (xLast - totalWidth) / 2);
                    break;
                case PanelAlign.Right:
                default:
                    baseX = Math.Max(0, xLast - totalWidth - _panelRightPadding);
                    break;
            }
            int panelLeft = Math.Max(0, baseX + _panelOffsetX);
            int panelTop = Math.Max(0, _panelOffsetY);

            // Dibujar columnas
            int x = panelLeft;
            for (int c = 0; c < _sections.Count; c++)
            {
                var sec = _sections[c];
                int w = colWidths[c];

                // Header
                var headerRect = new Rectangle(x, panelTop, w, headerH);
                context.FillRectangle(sec.HeaderColor, headerRect);
                int headerY = panelTop + (headerH - headerTextH) / 2;
                int headerTextW = MeasureText(context, sec.Title, _fontHeader);
                int headerX = x + (w - headerTextW) / 2; // centrado horizontal
                context.DrawString($"{sec.Title}", _fontHeader, Color.White, headerX, headerY);

                // Estado pequeño opcional (no se muestra en UPDATE)
                if (_showHeaderStatus && c == 0 && !string.IsNullOrWhiteSpace(status) && !sec.Title.Equals("UPDATE", StringComparison.OrdinalIgnoreCase))
                {
                    var st = status;
                    int stW = MeasureText(context, st, _fontNorm);
                    if (stW < w - 10)
                        context.DrawString(st, _fontNorm, Color.FromArgb(230, 240, 240, 240), x + w - stW - 6, headerY);
                }

                // Rows
                int y = panelTop + headerH + 2;
                for (int i = 0; i < sec.Items.Length; i++)
                {
                    var m = sec.Items[i];
                    var rect = new Rectangle(x, y, w, rowH);
                    if (_alternateRows)
                        context.FillRectangle((i % 2 == 0) ? _rowAlt1 : _rowAlt2, rect);

                    // grid bottom line (ligeramente por encima del borde para evitar superposición)
                    context.DrawLine(new RenderPen(_gridColor, 1), x, y + rowH - 1, x + w, y + rowH - 1);

                    // title (centrado vertical)
                    int titleH = normH;
                    int titleY = y + (rowH - titleH) / 2;
                    context.DrawString(m.Title, _fontNorm, _titleColor, x + _rowPaddingX, titleY);

                    // value (centrado vertical)
                    var value = GetValue(snapshot, m);
                    var (valText, valColor) = FormatValue(value, m);
                    int valWidth = MeasureText(context, valText, _fontBold);
                    int valH = boldH;
                    int valY = y + (rowH - valH) / 2;
                    context.DrawString(valText, _fontBold, valColor, x + w - valWidth - _rowPaddingX, valY);

                    y += rowH;
                }

                // Col vertical
                if (_showBorder)
                    context.DrawRectangle(new RenderPen(_borderColor, 1), new Rectangle(x, panelTop, w, y - panelTop));

                x += w + _columnGap;
            }
        }

        private string GetLastUpdateString() =>
            _lastUpdateLocal == DateTime.MinValue ? "---" : _lastUpdateLocal.ToString("HH:mm:ss");

        private string GetValue(Dictionary<string, string> map, Metric metric)
        {
            var raw = SafeGet(map, metric.Keys);
            if (!metric.StickLastPositive)
                return raw;

            // Si es sticky, y raw no es > 0, retornamos el último valor positivo guardado
            if (TryParseNumber(raw, out var num, out var hadPercent, out var hadK, out var hadM))
            {
                if (num > 0m)
                {
                    lock (_sync) _stickyPositive[metric.Title] = raw;
                    return raw;
                }
            }
            if (string.IsNullOrWhiteSpace(raw) || raw == "---" || !TryParseNumber(raw, out num, out _, out _, out _) || num <= 0m)
            {
                lock (_sync)
                {
                    if (_stickyPositive.TryGetValue(metric.Title, out var prevCached) && !string.IsNullOrWhiteSpace(prevCached))
                        return prevCached;
                }

                // Intento de backfill desde el CSV (leer hacia atrás hasta encontrar > 0)
                var backfill = TryBackfillStickyFromCsv(metric);
                if (!string.IsNullOrWhiteSpace(backfill))
                {
                    lock (_sync) _stickyPositive[metric.Title] = backfill;
                    return backfill;
                }
            }
            return raw;
        }

        private string TryBackfillStickyFromCsv(Metric metric)
        {
            try
            {
                if (!File.Exists(_csvPath)) return null;
                var lines = File.ReadAllLines(_csvPath)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToArray();
                if (lines.Length == 0) return null;

                var first = SplitCsvLine(lines[0]);
                // Caso 1: header + filas (busca columna y recorre desde el final)
                if (first.Length >= 3 || (first.Length >= 2 && lines.Length > 1))
                {
                    var header = first;
                    // Encuentra índice de cualquiera de las claves
                    int idx = -1;
                    for (int i = 0; i < header.Length; i++)
                    {
                        foreach (var k in metric.Keys)
                        {
                            if (string.Equals(header[i], k, StringComparison.OrdinalIgnoreCase))
                            { idx = i; break; }
                        }
                        if (idx >= 0) break;
                    }
                    if (idx >= 0)
                    {
                        for (int r = lines.Length - 1; r >= 1; r--)
                        {
                            var row = SplitCsvLine(lines[r]);
                            if (idx < row.Length)
                            {
                                var candidate = row[idx];
                                if (TryParseNumber(candidate, out var n, out _, out _, out _) && n > 0m)
                                    return candidate?.Trim();
                            }
                        }
                    }
                }

                // Caso 2: pares clave-valor por línea
                if (first.Length == 2)
                {
                    for (int r = lines.Length - 1; r >= 0; r--)
                    {
                        var parts = SplitCsvLine(lines[r]);
                        if (parts.Length < 2) continue;
                        var key = parts[0];
                        var val = parts[1];
                        foreach (var k in metric.Keys)
                        {
                            if (string.Equals(key, k, StringComparison.OrdinalIgnoreCase))
                            {
                                if (TryParseNumber(val, out var n, out _, out _, out _) && n > 0m)
                                    return val?.Trim();
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private string SafeGet(Dictionary<string, string> map, string[] keys)
        {
            // claves especiales calculadas
            if (keys != null)
            {
                foreach (var k in keys)
                {
                    if (string.Equals(k, "__LastUpdate__", StringComparison.Ordinal))
                        return GetLastUpdateString();
                }
            }

            foreach (var k in keys)
            {
                if (map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            }
            // fallback: intenta por nombre simple sin espacios
            foreach (var k in keys)
            {
                var alt = map.FirstOrDefault(p => string.Equals(Normalize(p.Key), Normalize(k), StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(alt.Key)) return alt.Value?.Trim() ?? string.Empty;
            }
            return "---";
        }

        private static string Normalize(string s) => new string((s ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .ToArray());

        private (string text, Color color) FormatValue(string raw, Metric metric)
        {
            if (string.IsNullOrWhiteSpace(raw)) return ("---", Color.LightGray);
            raw = raw.Trim();

            // Intenta número
            if (TryParseNumber(raw, out var num, out var hadPercent, out var hadK, out var hadM))
            {
                string txt;
                if (!string.IsNullOrWhiteSpace(metric.Unit))
                {
                    txt = FormatNumber(num, metric.Format, metric.Unit);
                }
                else
                {
                    var unit = hadM ? "M" : hadK ? "K" : hadPercent ? "%" : string.Empty;
                    txt = FormatNumber(num, metric.Format, unit);
                }

                var col = num switch
                {
                    > 0m => Color.FromArgb(60, 220, 120),
                    < 0m => Color.FromArgb(240, 100, 100),
                    _ => _valueColor
                };
                return (txt, col);
            }

            // Categórico
            var up = raw.ToUpperInvariant();
            if (up.Contains("HIGH") || up.Contains("STRONG") || up.Contains("ALIGNED") || up.Contains("STABLE") || up.Contains("BULL"))
                return (raw, Color.FromArgb(60, 220, 120));
            if (up.Contains("LOW") || up.Contains("WEAK") || up.Contains("WARNING") || up.Contains("DIVERG") || up.Contains("BEAR"))
                return (raw, Color.FromArgb(240, 120, 120));
            if (up.Contains("MONITOR") || up.Contains("QUIET") || up.Contains("NEUTRAL"))
                return (raw, Color.Khaki);

            return (raw, _valueColor);
        }

        private static bool TryParseNumber(string input, out decimal value, out bool hadPercent, out bool hadK, out bool hadM)
        {
            hadPercent = input.Contains('%');
            hadK = input.Contains('K') || input.Contains('k');
            hadM = input.Contains('M') || input.Contains('m');

            var cleaned = input.Replace("%", string.Empty).Replace(" ", string.Empty)
                .Replace("K", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("M", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);

            // Intento invariante
            if (!decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            {
                decimal tmp;
                if (!decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.CurrentCulture, out tmp))
                    return false;
                value = tmp;
            }

            if (hadK) value *= 1_000m;
            if (hadM) value *= 1_000_000m;
            if (hadPercent) value /= 100m;
            return true;
        }

        private static string FormatNumber(decimal num, string fmt, string unit)
        {
            string text;
            if (!string.IsNullOrWhiteSpace(fmt))
                text = num.ToString(fmt, CultureInfo.InvariantCulture);
            else
            {
                // Formato corto
                var abs = Math.Abs(num);
                if (abs >= 1_000_000m) text = (num / 1_000_000m).ToString("0.#", CultureInfo.InvariantCulture) + "M";
                else if (abs >= 1_000m) text = (num / 1_000m).ToString("0.#", CultureInfo.InvariantCulture) + "K";
                else if (abs < 1 && abs > 0) text = num.ToString("0.###", CultureInfo.InvariantCulture);
                else text = num.ToString("0.##", CultureInfo.InvariantCulture);

                // Si unidad explícita forzada, reemplaza el auto-sufijo
                if (!string.IsNullOrEmpty(unit))
                {
                    if (text.EndsWith("M") || text.EndsWith("K"))
                        text = num.ToString("0.##", CultureInfo.InvariantCulture);
                    text += unit;
                }
            }
            return text;
        }

        private static int MeasureText(RenderContext ctx, string text, RenderFont font)
        {
            try
            {
                var mi = typeof(RenderContext).GetMethod("MeasureString", new[] { typeof(string), typeof(RenderFont) });
                if (mi != null)
                {
                    var size = (Size)mi.Invoke(null, new object[] { text ?? string.Empty, font });
                    return size.Width;
                }
            }
            catch { }
            // Fallback aproximado
            var factor = font.Style.HasFlag(FontStyle.Bold) ? 0.66 : 0.58;
            return (int)Math.Round(((text?.Length ?? 0) + 1) * font.Size * factor);
        }

        private static Size MeasureSize(RenderContext ctx, string text, RenderFont font)
        {
            try
            {
                var mi = typeof(RenderContext).GetMethod("MeasureString", new[] { typeof(string), typeof(RenderFont) });
                if (mi != null)
                {
                    return (Size)mi.Invoke(null, new object[] { text ?? string.Empty, font });
                }
            }
            catch { }
            // Fallback aproximado de alto/alto
            var width = (int)Math.Round(((text?.Length ?? 0) + 1) * font.Size * (font.Style.HasFlag(FontStyle.Bold) ? 0.66 : 0.58));
            var height = (int)Math.Round(font.Size * 1.6f); // aprox con asc/desc
            return new Size(width, height);
        }
    }
}
