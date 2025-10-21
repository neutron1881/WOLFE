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
using System.Text.RegularExpressions;
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

        // NUEVO: disposición vertical de velocímetros (apila en una sola columna)
        private bool _speedGaugesVertical = false;

        // Velocidad config
        private decimal _dexAbsMin = -50m;
        private decimal _dexAbsMax = 50m;
        private decimal _dexPctMin = -100m;
        private decimal _dexPctMax = 100m;
        // Rango por panel de velocidad (configurable)
        private decimal _gexPctMin = -100m;
        private decimal _gexPctMax = 100m;
        private decimal _vannaPctMin = -100m;
        private decimal _vannaPctMax = 100m;
        private decimal _skewPctMin = -1m;
        private decimal _skewPctMax = 1m;
        // Altura mínima de fila para paneles de velocidad
        private int _speedMinRowHeight = 130;

        // Gauge styling
        private readonly Color _gaugeBaseColor = Color.FromArgb(55, 55, 70);
        private readonly Color _gaugeInnerBaseColor = Color.FromArgb(35, 35, 50);
        private readonly Color _gaugeProgressColor = Color.FromArgb(40, 110, 230); // azul como en la imagen
        private readonly Color _gaugeZeroTickColor = Color.FromArgb(30, 200, 90); // verde

        // Gauge options v2
        private bool _useFileWatcher = true;
        private int _fileWatcherDebounceMs = 300;
        private FileSystemWatcher _fsw;
        private DateTime _fswLastEventUtc = DateTime.MinValue;
        private readonly object _fswSync = new();

        private decimal _gaugeSmoothing = 0.2m; // 0=sin suavizado, 1=sin movimiento (no tiene sentido); típico 0.15-0.3
        private bool _gaugeShowArrow = true;
        private Color _gaugeProgressPositive = Color.FromArgb(60, 220, 120);
        private Color _gaugeProgressNegative = Color.FromArgb(240, 110, 110);
        private bool _gaugeShowBands = true;
        private decimal _gaugeBandLow = 0.33m;   // fracción del rango total
        private decimal _gaugeBandHigh = 0.66m;  // fracción del rango total
        private Color _gaugeBandLowColor = Color.FromArgb(110, 45, 45);
        private Color _gaugeBandMidColor = Color.FromArgb(150, 135, 60);
        private Color _gaugeBandHighColor = Color.FromArgb(45, 110, 60);
        private bool _gaugeShowDetails = true;   // muestra línea pequeña con abs y %

        private readonly Dictionary<string, decimal> _smoothValues = new(StringComparer.OrdinalIgnoreCase);

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

        // Estado previo para GEX (para calcular velocidad respecto a la lectura anterior del CSV)
        private decimal? _gexPrev;
        private decimal? _gexCurr;
        private DateTime? _lastCsvTimestamp;
        // Timestamps previos/actuales para calcular velocidad por minuto
        private DateTime? _gexPrevTs;
        private DateTime? _gexCurrTs;

        // Seguimiento DEX combinado (Call/Put) para velocidad por minuto
        private decimal? _dexPrevCombined;
        private decimal? _dexCurrCombined;
        private DateTime? _dexPrevTs;
        private DateTime? _dexCurrTs;

        // Seguimiento individual de Call/Put Delta para Velocidad DEX (promedio cambios relativos)
        private decimal? _dexCallPrev;
        private decimal? _dexCallCurr;
        private decimal? _dexPutPrev;
        private decimal? _dexPutCurr;

        // Seguimiento de Cash Call/Put para Velocidad Vanna (promedio cambios relativos)
        private decimal? _cashCallPrev;
        private decimal? _cashCallCurr;
        private decimal? _cashPutPrev;
        private decimal? _cashPutCurr;
        
        // Seguimiento de IV Call/Put para Velocidad Skew
        private decimal? _ivCallPrev;
        private decimal? _ivCallCurr;
        private decimal? _ivPutPrev;
        private decimal? _ivPutCurr;

        public SpotGammaPanelCsv()
        {
            EnableCustomDrawing = true;
            DenyToChangePanel = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);
            SubscribeToDrawingEvents(DrawingLayouts.Final);

            // Secciones solicitadas (paneles de velocidad individuales a la izquierda de UPDATE)
            _sections = new()
            {
                // VELOCIDAD DEX
                new Section
                {
                    Title = "Velocidad DEX",
                    HeaderColor = Color.FromArgb(200, 80, 80),
                    Items = new []
                    {
                        new Metric{ Title = "Velocidad DEX (%/min)", Keys = new[]{"__DexPct__"}, Format = "+0.##;-0.##", Unit = "%" }
                    }
                },
                // VELOCIDAD GEX
                new Section
                {
                    Title = "Velocidad GEX",
                    HeaderColor = Color.FromArgb(220, 140, 60),
                    Items = new []
                    {
                        new Metric{ Title = "Velocidad GEX (%/min)", Keys = new[]{"__GexPct__"}, Format = "+0.##;-0.##", Unit = "%" }
                    }
                },
                // VELOCIDAD VANNA
                new Section
                {
                    Title = "Velocidad Cash",
                    HeaderColor = Color.FromArgb(130, 90, 180),
                    Items = new []
                    {
                        new Metric{ Title = "Velocidad Cash (%/min)", Keys = new[]{"__VannaPct__"}, Format = "+0;-0;0", Unit = "%" }
                    }
                },
                // VELOCIDAD SKEW
                new Section
                {
                    Title = "Velocidad Skew",
                    HeaderColor = Color.FromArgb(70, 150, 160),
                    Items = new []
                    {
                        new Metric{ Title = "Velocidad IV Skew (%/min)", Keys = new[]{"__IvSkewPct__"}, Format = "+0.###;-0.###", Unit = "%" }
                    }
                },
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
                        new Metric{ Title = "Call Delta", Keys = new[]{"CallDelta","Call Delta","net_call_dex","Net Call Dex"}},
                        new Metric{ Title = "Put Delta",  Keys = new[]{"PutDelta","Delta Put","Put Delta","net_put_dex","Net Put Dex"}}
                    }
                }
            };
        }

        #region Properties
        [Category("Datos"), Display(Name = "Ruta CSV", Order = 0)]
        public string CsvPath { get => _csvPath; set { if (string.Equals(_csvPath, value, StringComparison.OrdinalIgnoreCase)) return; _csvPath = value ?? string.Empty; InitFileWatcher(); ForceReload(); } }

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

        [Category("Velocidad"), Display(Name = "Velocímetros en columna", Order = 0)]
        public bool SpeedGaugesVertical { get => _speedGaugesVertical; set { if (_speedGaugesVertical == value) return; _speedGaugesVertical = value; RedrawChart(); } }

        [Category("Velocidad"), Display(Name = "DEX abs/min - Min", Order = 1)]
        public decimal DexAbsMin { get => _dexAbsMin; set { _dexAbsMin = value; RedrawChart(); } }

        [Category("Velocidad"), Display(Name = "DEX abs/min - Max", Order = 2)]
        public decimal DexAbsMax { get => _dexAbsMax; set { _dexAbsMax = value; RedrawChart(); } }

        [Category("Velocidad"), Display(Name = "DEX %/min - Min", Order = 3)]
        public decimal DexPctMin { get => _dexPctMin; set { _dexPctMin = value; RedrawChart(); } }

        [Category("Velocidad"), Display(Name = "DEX %/min - Max", Order = 4)]
        public decimal DexPctMax { get => _dexPctMax; set { _dexPctMax = value; RedrawChart(); } }

        [Category("Velocidad"), Display(Name = "GEX %/min - Min", Order = 5)]
        public decimal GexPctMin { get => _gexPctMin; set { _gexPctMin = value; RedrawChart(); } }
        [Category("Velocidad"), Display(Name = "GEX %/min - Max", Order = 6)]
        public decimal GexPctMax { get => _gexPctMax; set { _gexPctMax = value; RedrawChart(); } }

        [Category("Velocidad"), Display(Name = "Vanna %/min - Min", Order = 7)]
        public decimal VannaPctMin { get => _vannaPctMin; set { _vannaPctMin = value; RedrawChart(); } }
        [Category("Velocidad"), Display(Name = "Vanna %/min - Max", Order = 8)]
        public decimal VannaPctMax { get => _vannaPctMax; set { _vannaPctMax = value; RedrawChart(); } }

        [Category("Velocidad"), Display(Name = "Skew %/min - Min", Order = 9)]
        public decimal SkewPctMin { get => _skewPctMin; set { _skewPctMin = value; RedrawChart(); } }
        [Category("Velocidad"), Display(Name = "Skew %/min - Max", Order = 10)]
        public decimal SkewPctMax { get => _skewPctMax; set { _skewPctMax = value; RedrawChart(); } }

        [Category("Velocidad"), Display(Name = "Altura mínima panel (px)", Order = 11)]
        [Range(80, 300)]
        public int SpeedMinRowHeight { get => _speedMinRowHeight; set { _speedMinRowHeight = Math.Max(80, Math.Min(300, value)); RedrawChart(); } }

        [Category("Datos"), Display(Name = "Usar File Watcher", Order = 2)]
        public bool UseFileWatcher { get => _useFileWatcher; set { if (_useFileWatcher == value) return; _useFileWatcher = value; InitFileWatcher(); } }

        [Category("Datos"), Display(Name = "Debounce Watcher (ms)", Order = 3)]
        [Range(50, 5000)]
        public int FileWatcherDebounceMs { get => _fileWatcherDebounceMs; set { _fileWatcherDebounceMs = Math.Max(50, Math.Min(5000, value)); } }

        [Category("Velocidad"), Display(Name = "Suavizado gauge (0-1)", Order = 12)]
        [Range(0.0, 0.9)]
        public decimal GaugeSmoothing { get => _gaugeSmoothing; set { _gaugeSmoothing = Math.Max(0m, Math.Min(0.9m, value)); } }

        [Category("Velocidad"), Display(Name = "Mostrar flecha dirección", Order = 13)]
        public bool GaugeShowArrow { get => _gaugeShowArrow; set { _gaugeShowArrow = value; RedrawChart(); } }
        [Category("Velocidad"), Display(Name = "Mostrar bandas", Order = 14)]
        public bool GaugeShowBands { get => _gaugeShowBands; set { _gaugeShowBands = value; RedrawChart(); } }
        [Category("Velocidad"), Display(Name = "Banda baja (% rango)", Order = 15)]
        public decimal GaugeBandLow { get => _gaugeBandLow; set { _gaugeBandLow = Math.Max(0m, Math.Min(1m, value)); RedrawChart(); } }
        [Category("Velocidad"), Display(Name = "Banda alta (% rango)", Order = 16)]
        public decimal GaugeBandHigh { get => _gaugeBandHigh; set { _gaugeBandHigh = Math.Max(0m, Math.Min(1m, value)); RedrawChart(); } }
        [Category("Velocidad"), Display(Name = "Color banda baja", Order = 17)]
        public Color GaugeBandLowColor { get => _gaugeBandLowColor; set { _gaugeBandLowColor = value; RedrawChart(); } }
        [Category("Velocidad"), Display(Name = "Color banda media", Order = 18)]
        public Color GaugeBandMidColor { get => _gaugeBandMidColor; set { _gaugeBandMidColor = value; RedrawChart(); } }
        [Category("Velocidad"), Display(Name = "Color banda alta", Order = 19)]
        public Color GaugeBandHighColor { get => _gaugeBandHighColor; set { _gaugeBandHighColor = value; RedrawChart(); } }
        [Category("Velocidad"), Display(Name = "Mostrar detalles (abs/%)", Order = 20)]
        public bool GaugeShowDetails { get => _gaugeShowDetails; set { _gaugeShowDetails = value; RedrawChart(); } }
        #endregion

        protected override void OnInitialize()
        {
            RecreateFonts();
            _nextReadUtc = DateTime.MinValue;
            InitFileWatcher();
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
                // No recognible, dejar vacío
            }

            // Actualiza snapshot y extrae NetGEX y Timestamp para controlar previo/actual
            DateTime? ts = null;
            if (map.TryGetValue("Timestamp", out var tsStr) || map.TryGetValue("TimeStamp", out tsStr))
            {
                if (DateTime.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var dtInv) ||
                    DateTime.TryParse(tsStr, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out dtInv))
                {
                    ts = dtInv;
                }
            }

            string gexRaw = null;
            foreach (var k in new[] { "NetGEX", "Net Gex", "NetGex", "GEXNet", "GEX" })
            {
                if (map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) { gexRaw = v; break; }
            }

            // DEX actual (Call/Put) para combinado
            string dexCallRaw = null;
            foreach (var k in new[] { "net_call_dex", "call_dex", "Call Delta", "CallDelta" })
            {
                if (map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) { dexCallRaw = v; break; }
            }
            string dexPutRaw = null;
            foreach (var k in new[] { "net_put_dex", "put_dex", "Put Delta", "PutDelta" })
            {
                if (map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) { dexPutRaw = v; break; }
            }

            // Cash Call/Put (actuales)
            string cashCallRaw = null;
            foreach (var k in new[] { "CashCall", "Cash Call" })
            {
                if (map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) { cashCallRaw = v; break; }
            }
            string cashPutRaw = null;
            foreach (var k in new[] { "CashPut", "Cash Put" })
            {
                if (map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) { cashPutRaw = v; break; }
            }

            // IV Call/Put (actuales)
            string ivCallRaw = null;
            foreach (var k in new[] { "IVCall", "IV Call", "Call IV" })
            {
                if (map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) { ivCallRaw = v; break; }
            }
            string ivPutRaw = null;
            foreach (var k in new[] { "IVPut", "IV Put", "Put IV" })
            {
                if (map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) { ivPutRaw = v; break; }
            }

            // Intenta obtener Net Gex actual y previo desde la última y penúltima fila del CSV (formato cabecera+filas)
            decimal? gexCurrFromRows = null, gexPrevFromRows = null;
            DateTime? tsCurrRow = null, tsPrevRow = null;
            // También intentaremos obtener Call/Put Delta actual y previo desde filas
            decimal? callCurrFromRows = null, callPrevFromRows = null;
            decimal? putCurrFromRows = null, putPrevFromRows = null;
            // Y Cash Call/Put actual y previo desde filas
            decimal? cashCallCurrFromRows = null, cashCallPrevFromRows = null;
            decimal? cashPutCurrFromRows = null, cashPutPrevFromRows = null;
            // Y IV Call/Put actual y previo desde filas
            decimal? ivCallCurrFromRows = null, ivCallPrevFromRows = null;
            decimal? ivPutCurrFromRows = null, ivPutPrevFromRows = null;
            try
            {
                var header = first;
                var lastRow = SplitCsvLine(lines[^1]);
                if (lastRow.Length == header.Length && lines.Length >= 3)
                {
                    // Busca índices de columnas relevantes
                    int colGex = -1;
                    var gexCols = new[] { "NetGEX", "Net Gex", "NetGex", "GEXNet", "GEX" };
                    for (int i = 0; i < header.Length && colGex < 0; i++)
                    {
                        foreach (var key in gexCols)
                        {
                            if (string.Equals(header[i], key, StringComparison.OrdinalIgnoreCase))
                            { colGex = i; break; }
                        }
                    }
                    int colTs = -1;
                    var tsCols = new[] { "Timestamp", "TimeStamp", "LastUpdate", "Last Update" };
                    for (int i = 0; i < header.Length && colTs < 0; i++)
                    {
                        foreach (var key in tsCols)
                        {
                            if (string.Equals(header[i], key, StringComparison.OrdinalIgnoreCase))
                            { colTs = i; break; }
                        }
                    }

                    // Índices de Call/Put Delta
                    int colCall = -1;
                    var callCols = new[] { "net_call_dex", "call_dex", "Call Delta", "CallDelta" };
                    for (int i = 0; i < header.Length && colCall < 0; i++)
                    {
                        foreach (var key in callCols)
                        {
                            if (string.Equals(header[i], key, StringComparison.OrdinalIgnoreCase))
                            { colCall = i; break; }
                        }
                    }
                    int colPut = -1;
                    var putCols = new[] { "net_put_dex", "put_dex", "Put Delta", "PutDelta" };
                    for (int i = 0; i < header.Length && colPut < 0; i++)
                    {
                        foreach (var key in putCols)
                        {
                            if (string.Equals(header[i], key, StringComparison.OrdinalIgnoreCase))
                            { colPut = i; break; }
                        }
                    }

                    // Índices para Cash Call/Put
                    int colCashCall = -1;
                    foreach (var key in new[] { "CashCall", "Cash Call" })
                    {
                        for (int i = 0; i < header.Length && colCashCall < 0; i++)
                            if (string.Equals(header[i], key, StringComparison.OrdinalIgnoreCase)) { colCashCall = i; break; }
                        if (colCashCall >= 0) break;
                    }
                    int colCashPut = -1;
                    foreach (var key in new[] { "CashPut", "Cash Put" })
                    {
                        for (int i = 0; i < header.Length && colCashPut < 0; i++)
                            if (string.Equals(header[i], key, StringComparison.OrdinalIgnoreCase)) { colCashPut = i; break; }
                        if (colCashPut >= 0) break;
                    }

                    // Índices para IV Call/Put
                    int colIvCall = -1;
                    foreach (var key in new[] { "IVCall", "IV Call", "Call IV" })
                    {
                        for (int i = 0; i < header.Length && colIvCall < 0; i++)
                            if (string.Equals(header[i], key, StringComparison.OrdinalIgnoreCase)) { colIvCall = i; break; }
                        if (colIvCall >= 0) break;
                    }
                    int colIvPut = -1;
                    foreach (var key in new[] { "IVPut", "IV Put", "Put IV" })
                    {
                        for (int i = 0; i < header.Length && colIvPut < 0; i++)
                            if (string.Equals(header[i], key, StringComparison.OrdinalIgnoreCase)) { colIvPut = i; break; }
                        if (colIvPut >= 0) break;
                    }
                    var prevRow = SplitCsvLine(lines[^2]);
                    if (prevRow.Length == header.Length)
                    {
                        // Última fila = actual, Penúltima = previo
                        if (colGex >= 0)
                        {
                            if (colGex < lastRow.Length && TryParseNumber(lastRow[colGex], out var gexNow, out _, out _, out _))
                                gexCurrFromRows = gexNow;
                            if (colGex < prevRow.Length && TryParseNumber(prevRow[colGex], out var gexPrev, out _, out _, out _))
                                gexPrevFromRows = gexPrev;
                        }
                        if (colCall >= 0)
                        {
                            if (colCall < lastRow.Length && TryParseNumber(lastRow[colCall], out var cNow, out _, out _, out _))
                                callCurrFromRows = cNow;
                            if (colCall < prevRow.Length && TryParseNumber(prevRow[colCall], out var cPrev, out _, out _, out _))
                                callPrevFromRows = cPrev;
                        }
                        if (colPut >= 0)
                        {
                            if (colPut < lastRow.Length && TryParseNumber(lastRow[colPut], out var pNow, out _, out _, out _))
                                putCurrFromRows = pNow;
                            if (colPut < prevRow.Length && TryParseNumber(prevRow[colPut], out var pPrev, out _, out _, out _))
                                putPrevFromRows = pPrev;
                        }

                        if (colCashCall >= 0)
                        {
                            if (colCashCall < lastRow.Length && TryParseNumber(lastRow[colCashCall], out var ccNow, out _, out _, out _))
                                cashCallCurrFromRows = ccNow;
                            if (colCashCall < prevRow.Length && TryParseNumber(prevRow[colCashCall], out var ccPrev, out _, out _, out _))
                                cashCallPrevFromRows = ccPrev;
                        }
                        if (colCashPut >= 0)
                        {
                            if (colCashPut < lastRow.Length && TryParseNumber(lastRow[colCashPut], out var cpNow, out _, out _, out _))
                                cashPutCurrFromRows = cpNow;
                            if (colCashPut < prevRow.Length && TryParseNumber(prevRow[colCashPut], out var cpPrev, out _, out _, out _))
                                cashPutPrevFromRows = cpPrev;
                        }

                        if (colIvCall >= 0)
                        {
                            // Escanear desde el final y tomar los dos últimos valores no nulos/ni cero
                            var found = new List<decimal>(2);
                            for (int r = lines.Length - 1; r >= 1 && found.Count < 2; r--)
                            {
                                var row = SplitCsvLine(lines[r]);
                                if (colIvCall < row.Length && TryParseNumber(row[colIvCall], out var val, out _, out _, out _))
                                {
                                    if (val != 0m) found.Add(val);
                                }
                            }
                            if (found.Count > 0) ivCallCurrFromRows = found[0];
                            if (found.Count > 1) ivCallPrevFromRows = found[1];
                        }
                        if (colIvPut >= 0)
                        {
                            // Escanear desde el final y tomar los dos últimos valores no nulos/ni cero
                            var found = new List<decimal>(2);
                            for (int r = lines.Length - 1; r >= 1 && found.Count < 2; r--)
                            {
                                var row = SplitCsvLine(lines[r]);
                                if (colIvPut < row.Length && TryParseNumber(row[colIvPut], out var val, out _, out _, out _))
                                {
                                    if (val != 0m) found.Add(val);
                                }
                            }
                            if (found.Count > 0) ivPutCurrFromRows = found[0];
                            if (found.Count > 1) ivPutPrevFromRows = found[1];
                        }

                        if (colTs >= 0)
                        {
                            if (colTs < lastRow.Length && DateTime.TryParse(lastRow[colTs], CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var dt1))
                                tsCurrRow = dt1;
                            else if (colTs < lastRow.Length && DateTime.TryParse(lastRow[colTs], CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out dt1))
                                tsCurrRow = dt1;

                            if (colTs < prevRow.Length && DateTime.TryParse(prevRow[colTs], CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var dt2))
                                tsPrevRow = dt2;
                            else if (colTs < prevRow.Length && DateTime.TryParse(prevRow[colTs], CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out dt2))
                                tsPrevRow = dt2;
                        }
                    }
                }
            }
            catch { }

            lock (_sync)
            {
                var isNewSample = false;
                if (ts.HasValue)
                {
                    if (!_lastCsvTimestamp.HasValue || _lastCsvTimestamp.Value != ts.Value)
                    {
                        isNewSample = true;
                        _lastCsvTimestamp = ts.Value;
                    }
                }

                if (TryParseNumber(gexRaw ?? string.Empty, out var parsedCurr, out _, out _, out _))
                {
                    if (!isNewSample)
                    {
                        // si no hay timestamp o no cambió, usa cambio de valor como nueva muestra
                        if (!_gexCurr.HasValue || _gexCurr.Value != parsedCurr)
                            isNewSample = true;
                    }

                    if (isNewSample)
                    {
                        // Mueve actuales a previos
                        _gexPrev = _gexCurr;
                        _gexPrevTs = _gexCurrTs;
                        _gexCurr = parsedCurr;
                        _gexCurrTs = ts ?? DateTime.Now;
                    }
                    else
                    {
                        _gexCurr = parsedCurr; // actualizar por si acaso
                        if (ts.HasValue) _gexCurrTs = ts;
                    }
                }

                // Si logramos leer desde filas, forzamos prev/curr de GEX directamente del archivo
                if (gexCurrFromRows.HasValue && gexPrevFromRows.HasValue)
                {
                    _gexCurr = gexCurrFromRows.Value;
                    _gexPrev = gexPrevFromRows.Value;
                    _gexCurrTs = tsCurrRow ?? _gexCurrTs;
                    _gexPrevTs = tsPrevRow ?? _gexPrevTs;
                }

                // Si logramos leer Call/Put desde filas, forzamos prev/curr directamente del archivo
                if (callCurrFromRows.HasValue && callPrevFromRows.HasValue)
                {
                    _dexCallCurr = callCurrFromRows.Value;
                    _dexCallPrev = callPrevFromRows.Value;
                }
                if (putCurrFromRows.HasValue && putPrevFromRows.HasValue)
                {
                    _dexPutCurr = putCurrFromRows.Value;
                    _dexPutPrev = putPrevFromRows.Value;
                }

                // Si logramos leer Cash Call/Put desde filas, forzamos prev/curr directamente del archivo
                if (cashCallCurrFromRows.HasValue && cashCallPrevFromRows.HasValue)
                {
                    _cashCallCurr = cashCallCurrFromRows.Value;
                    _cashCallPrev = cashCallPrevFromRows.Value;
                }
                if (cashPutCurrFromRows.HasValue && cashPutPrevFromRows.HasValue)
                {
                    _cashPutCurr = cashPutCurrFromRows.Value;
                    _cashPutPrev = cashPutPrevFromRows.Value;
                }

                // Si logramos leer IV Call/Put desde filas, forzamos prev/curr directamente del archivo
                if (ivCallCurrFromRows.HasValue && ivCallPrevFromRows.HasValue)
                {
                    _ivCallCurr = ivCallCurrFromRows.Value;
                    _ivCallPrev = ivCallPrevFromRows.Value;
                }
                if (ivPutCurrFromRows.HasValue && ivPutPrevFromRows.HasValue)
                {
                    _ivPutCurr = ivPutCurrFromRows.Value;
                    _ivPutPrev = ivPutPrevFromRows.Value;
                }

                _data = map; // actualizar snapshot visible
            }
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
            int[] sectionWidths = new int[_sections.Count];
            int totalWidth = 0;

            // Detecta qué secciones son de velocímetro
            bool IsSpeedMetric(Metric m) => m.Keys != null && (
                m.Keys.Any(k => string.Equals(k, "__DexPct__", StringComparison.Ordinal)) ||
                m.Keys.Any(k => string.Equals(k, "__GexPct__", StringComparison.Ordinal)) ||
                m.Keys.Any(k => string.Equals(k, "__VannaPct__", StringComparison.Ordinal)) ||
                m.Keys.Any(k => string.Equals(k, "__IvSkewPct__", StringComparison.Ordinal))
            );
            bool IsSpeedSection(Section s) => s.Items != null && s.Items.Any(IsSpeedMetric);

            var speedIndices = new List<int>();

            for (int c = 0; c < _sections.Count; c++)
            {
                var w = MeasureText(context, _sections[c].Title, _fontHeader) + 20; // header base
                foreach (var m in _sections[c].Items)
                {
                    int wTitle = MeasureText(context, m.Title + " ", _fontNorm);
                    string tmpVal = GetValue(snapshot, m);
                    int wValue = MeasureText(context, tmpVal, _fontBold);
                    w = Math.Max(w, wTitle + wValue + 22);
                }
                sectionWidths[c] = Math.Max(160, Math.Min(420, w));
                if (IsSpeedSection(_sections[c]))
                    speedIndices.Add(c);
            }

            int numColumns;
            int speedColumnWidth = 0;
            if (_speedGaugesVertical && speedIndices.Count > 0)
            {
                speedColumnWidth = speedIndices.Select(i => sectionWidths[i]).Max();
                numColumns = 1 + _sections.Select((s, idx) => new { s, idx }).Count(p => !speedIndices.Contains(p.idx));
                totalWidth = speedColumnWidth + _sections.Select((s, idx) => new { s, idx })
                    .Where(p => !speedIndices.Contains(p.idx))
                    .Select(p => sectionWidths[p.idx])
                    .Sum();
            }
            else
            {
                numColumns = _sections.Count;
                totalWidth = sectionWidths.Sum();
            }

            if (numColumns > 0)
                totalWidth += _columnGap * (numColumns - 1);

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

            // Precalcular valores velocidad
            var (dexAbs, dexPct) = CalculateDexRates(snapshot);
            var (gexAbs, gexPct) = CalculateGexRates(snapshot);
            var (vannaAbs, vannaPct) = CalculateVannaRates(snapshot);
            var (ivSkewAbs, ivSkewPct) = CalculateIvSkewRates(snapshot);

            // Dibujo con disposición opcional vertical para velocímetros
            int currentX = panelLeft;
            int speedColumnX = panelLeft;
            int speedColumnY = panelTop;
            bool speedStarted = false;
            int lastSpeedIndex = speedIndices.Count > 0 ? speedIndices[^1] : -1;

            for (int c = 0; c < _sections.Count; c++)
            {
                var sec = _sections[c];
                bool sectionIsSpeed = IsSpeedSection(sec);

                int w;
                int colTop;
                int x;

                if (_speedGaugesVertical && sectionIsSpeed && speedIndices.Count > 0)
                {
                    w = speedColumnWidth;
                    if (!speedStarted)
                    {
                        speedStarted = true;
                        speedColumnX = currentX;
                        speedColumnY = panelTop;
                    }
                    x = speedColumnX;
                    colTop = speedColumnY;
                }
                else
                {
                    w = sectionWidths[c];
                    x = currentX;
                    colTop = panelTop;
                }

                // Header
                var headerRect = new Rectangle(x, colTop, w, headerH);
                context.FillRectangle(sec.HeaderColor, headerRect);
                int headerY = colTop + (headerH - headerTextH) / 2;
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
                int y = colTop + headerH + 2;

                // Layout horizontal especial para VELOCIDAD (no usado actualmente)
                if (sec.Title.Equals("VELOCIDAD", StringComparison.OrdinalIgnoreCase) && sec.Items.Length > 0)
                {
                    int rowHVel = Math.Max(rowH, _speedMinRowHeight);
                    var rectVel = new Rectangle(x, y, w, rowHVel);
                    if (_alternateRows)
                        context.FillRectangle(_rowAlt1, rectVel);

                    int n = sec.Items.Length;
                    int subGap = 6;
                    int subW = Math.Max(80, (w - subGap * (n + 1)) / n);
                    for (int i = 0; i < n; i++)
                    {
                        int left = x + subGap + i * (subW + subGap);
                        var subRect = new Rectangle(left, y, subW, rowHVel);

                        var mi = sec.Items[i];
                        decimal? gaugeVal;
                        decimal minRange;
                        decimal maxRange;

                        // Resolver métrica
                        if (mi.Keys != null && mi.Keys.Any(k => string.Equals(k, "__GexPct__", StringComparison.Ordinal)))
                        {
                            gaugeVal = gexPct;
                            minRange = _dexPctMin; maxRange = _dexPctMax;
                        }
                        else if (mi.Keys != null && mi.Keys.Any(k => string.Equals(k, "__DexPct__", StringComparison.Ordinal)))
                        {
                            gaugeVal = dexPct;
                            minRange = _dexPctMin; maxRange = _dexPctMax;
                        }
                        else if (mi.Keys != null && mi.Keys.Any(k => string.Equals(k, "__VannaPct__", StringComparison.Ordinal)))
                        {
                            // Velocidad Cash: porcentaje (promedio de difs relativas con signo)
                            gaugeVal = vannaPct;
                            minRange = _vannaPctMin; maxRange = _vannaPctMax;
                        }
                        else if (mi.Keys != null && mi.Keys.Any(k => string.Equals(k, "__IvSkewPct__", StringComparison.Ordinal)))
                        {
                            gaugeVal = ivSkewPct;
                            minRange = _skewPctMin; maxRange = _skewPctMax; // usa rango configurable
                        }
                        else
                        {
                            gaugeVal = null; minRange = _dexPctMin; maxRange = _dexPctMax;
                        }

                        DrawSpeedometer(context, subRect, gaugeVal, minRange, maxRange, mi);

                        // separadores opcionales
                        if (_showBorder)
                            context.DrawRectangle(new RenderPen(_borderColor, 1), subRect);
                    }

                    if (_showBorder)
                        context.DrawRectangle(new RenderPen(_borderColor, 1), rectVel);

                    y += rowHVel;
                }
                else
                {
                    for (int i = 0; i < sec.Items.Length; i++)
                    {
                        var m = sec.Items[i];

                        // Paneles individuales de VELOCIDAD con anillos (como el primero)
                        bool isSpeedMetric = m.Keys != null && (
                            m.Keys.Any(k => string.Equals(k, "__DexPct__", StringComparison.Ordinal)) ||
                            m.Keys.Any(k => string.Equals(k, "__GexPct__", StringComparison.Ordinal)) ||
                            m.Keys.Any(k => string.Equals(k, "__VannaPct__", StringComparison.Ordinal)) ||
                            m.Keys.Any(k => string.Equals(k, "__IvSkewPct__", StringComparison.Ordinal))
                        );
                        if (isSpeedMetric)
                        {
                            int rowHVel = Math.Max(rowH, _speedMinRowHeight);
                            var rectVel = new Rectangle(x, y, w, rowHVel);
                            if (_alternateRows)
                                context.FillRectangle((i % 2 == 0) ? _rowAlt1 : _rowAlt2, rectVel);

                            decimal? gaugeVal;
                            decimal minRange;
                            decimal maxRange;
                            var metricForDraw = m; // por defecto usa la métrica original

                            if (m.Keys.Any(k => string.Equals(k, "__DexPct__", StringComparison.Ordinal)))
                            {
                                gaugeVal = dexPct; minRange = _dexPctMin; maxRange = _dexPctMax;
                                metricForDraw = new Metric { Title = m.Title, Keys = m.Keys, Format = "+0;-0;0", Unit = "%" };
                            }
                            else if (m.Keys.Any(k => string.Equals(k, "__GexPct__", StringComparison.Ordinal)))
                            {
                                gaugeVal = gexPct; minRange = _gexPctMin; maxRange = _gexPctMax;
                                metricForDraw = new Metric { Title = m.Title, Keys = m.Keys, Format = "+0;-0;0", Unit = "%" };
                            }
                            else if (m.Keys.Any(k => string.Equals(k, "__VannaPct__", StringComparison.Ordinal)))
                            {
                                // Velocidad Cash (%): usa rango VannaPct
                                gaugeVal = vannaPct; minRange = _vannaPctMin; maxRange = _vannaPctMax;
                                metricForDraw = new Metric { Title = m.Title, Keys = m.Keys, Format = "+0;-0;0", Unit = "%" };
                            }
                            else if (m.Keys.Any(k => string.Equals(k, "__IvSkewPct__", StringComparison.Ordinal)))
                            {
                                gaugeVal = ivSkewPct; minRange = _skewPctMin; maxRange = _skewPctMax;
                                metricForDraw = new Metric { Title = m.Title, Keys = m.Keys, Format = "+0;-0;0", Unit = "%" };
                            }
                            else { gaugeVal = null; minRange = _dexPctMin; maxRange = _dexPctMax; }

                            DrawSpeedometer(context, rectVel, gaugeVal, minRange, maxRange, metricForDraw);

                            if (_showBorder)
                                context.DrawRectangle(new RenderPen(_borderColor, 1), rectVel);

                            y += rowHVel;
                            continue;
                        }

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
                }

                // Col/borde por sección
                if (_showBorder)
                    context.DrawRectangle(new RenderPen(_borderColor, 1), new Rectangle(x, colTop, w, y - colTop));

                // Avance X según modalidad
                if (_speedGaugesVertical && sectionIsSpeed)
                {
                    // apilar en misma columna
                    speedColumnY = y; // siguiente sección de velocidad empieza debajo de la actual
                    if (c == lastSpeedIndex)
                    {
                        // última de las de velocidad: ahora sí avanzamos X
                        currentX += w + _columnGap;
                    }
                }
                else
                {
                    // secciones normales: avanzan X como siempre
                    currentX += w + _columnGap;
                }
            }
        }

        private string GetLastUpdateString() =>
            _lastUpdateLocal == DateTime.MinValue ? "---" : _lastUpdateLocal.ToString("HH:mm:ss");

        private string GetValue(Dictionary<string, string> map, Metric metric)
        {
            // Derivados especiales
            if (metric.Keys != null)
            {
                foreach (var k in metric.Keys)
                {
                    if (string.Equals(k, "__DexAbs__", StringComparison.Ordinal))
                    {
                        var (abs, _) = CalculateDexRates(map);
                        return abs.HasValue ? abs.Value.ToString("0.##", CultureInfo.InvariantCulture) : "---";
                    }
                    if (string.Equals(k, "__DexPct__", StringComparison.Ordinal))
                    {
                        var (_, pct) = CalculateDexRates(map);
                        return pct.HasValue ? pct.Value.ToString("+0.##;-0.##", CultureInfo.InvariantCulture) + "%" : "---";
                    }
                }
                // Si es una clave especial (__...), no intentes map fallback para evitar colisión con Net Gex
                if (metric.Keys.Any(k => k.StartsWith("__", StringComparison.Ordinal)))
                    return "---";
            }

            var raw = SafeGet(map, metric.Keys);
            if (!metric.StickLastPositive)
                return raw;

            // Sticky logic
            if (TryParseNumber(raw, out var num, out _, out _, out _))
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

                var backfill = TryBackfillStickyFromCsv(metric);
                if (!string.IsNullOrWhiteSpace(backfill))
                {
                    lock (_sync) _stickyPositive[metric.Title] = backfill;
                    return backfill;
                }
            }
            return raw;
        }

        private (decimal? abs, decimal? pct) CalculateDexRates(Dictionary<string, string> map)
        {
            // Nueva definición: promedio de cambios relativos de Call y Put respecto a sus valores previos
            decimal? callPrev, callCurr, putPrev, putCurr;
            lock (_sync)
            {
                callPrev = _dexCallPrev; callCurr = _dexCallCurr;
                putPrev = _dexPutPrev; putCurr = _dexPutCurr;
            }

            decimal? callPct = null, putPct = null;
            if (callPrev.HasValue && callPrev.Value != 0m && callCurr.HasValue)
                callPct = ((callCurr.Value - callPrev.Value) / (callPrev.Value)) * 100m;
            if (putPrev.HasValue && putPrev.Value != 0m && putCurr.HasValue)
                putPct = ((putCurr.Value - putPrev.Value) / (putPrev.Value)) * 100m;

            decimal? avgPct = null;
            if (callPct.HasValue && putPct.HasValue)
                avgPct = (callPct.Value + putPct.Value) / 2m;
            else if (callPct.HasValue)
                avgPct = callPct.Value;
            else if (putPct.HasValue)
                avgPct = putPct.Value;

            return (null, avgPct);
        }

        private static (decimal? abs, decimal? pct) CalculateRate(Dictionary<string, string> map, string[] histKeys, string[] currKeys)
        {
            // Obtén serie histórica y actual
            string histRaw = null;
            foreach (var k in histKeys)
            {
                if (map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) { histRaw = v; break; }
            }
            string currRaw = null;
            foreach (var k in currKeys)
            {
                if (map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) { currRaw = v; break; }
            }

            var series = ParseSeries(histRaw);
            if (TryParseNumber(currRaw ?? string.Empty, out var curr, out _, out _, out _))
            {
                if (series.Count == 0 || series[^1] != curr)
                    series.Add(curr);
            }

            if (series.Count < 2)
                return (null, null);

            // diferencias por paso (asumimos minuto entre muestras)
            var diffs = new List<decimal>();
            var pctChanges = new List<decimal>();
            for (int i = 1; i < series.Count; i++)
            {
                var prev = series[i - 1];
                var now = series[i];
                var d = now - prev;
                diffs.Add(Math.Abs(d));
                var denom = Math.Max(Math.Abs(prev), 1e-8m);
                pctChanges.Add((d / denom) * 100m);
            }

            if (diffs.Count == 0) return (null, null);
            var absAvg = diffs.Average();
            var pctAvg = pctChanges.Average();
            // recorta a [-100,100] para pct, como guía visual
            pctAvg = Math.Max(-100m, Math.Min(100m, pctAvg));
            return (absAvg, pctAvg);
        }

        private static List<decimal> ParseSeries(string raw)
        {
            var list = new List<decimal>();
            if (string.IsNullOrWhiteSpace(raw)) return list;
            // Acepta separadores comunes y limpia chars no numéricos salvo - . ,
            // También acepta formato tipo "[1,2,3]" o "1|2|3".
            var tokens = Regex.Split(raw.Trim().Trim('[', ']'), "[;,\n\r\t| ]+");
            foreach (var t in tokens)
            {
                var s = t.Trim();
                if (s.Length == 0) continue;
                // normaliza coma decimal si viniera con coma
                s = s.Replace("%", string.Empty);
                if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ||
                    decimal.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out v))
                {
                    list.Add(v);
                }
            }
            return list;
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

                // Color base por signo
                var col = num switch
                {
                    > 0m => Color.FromArgb(60, 220, 120),
                    < 0m => Color.FromArgb(240, 100, 100),
                    _ => _valueColor
                };

                // Overrides por métrica solicitados
                var title = metric.Title?.Trim();
                if (!string.IsNullOrEmpty(title))
                {
                    if (string.Equals(title, "Put Vol", StringComparison.OrdinalIgnoreCase))
                    {
                        col = Color.FromArgb(240, 100, 100); // siempre rojo
                    }
                    else if (string.Equals(title, "Put OI", StringComparison.OrdinalIgnoreCase))
                    {
                        col = Color.FromArgb(240, 100, 100); // siempre rojo
                    }
                    else if (string.Equals(title, "IV Put", StringComparison.OrdinalIgnoreCase))
                    {
                        col = Color.FromArgb(240, 100, 100); // siempre rojo
                    }
                    // Net Vol / Net Delta / Net Gex: ya usan color por signo (default)
                }
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

        private void DrawSpeedometer(RenderContext ctx, Rectangle rect, decimal? value, decimal min, decimal max, Metric metric)
        {
            // Geometría del gauge (semicírculo superior) con anillos como el primero
            int padding = Math.Max(8, _rowPaddingY + 6);
            int extraMargin = 8;

            int ringThickness = Math.Max(8, rect.Height / 7);
            int innerGap = Math.Max(3, ringThickness / 3);

            int cx = rect.X + rect.Width / 2;
            int cy = rect.Y + rect.Height - padding - extraMargin + 6;

            int maxRadiusX = Math.Max(0, rect.Width / 2 - padding - extraMargin - ringThickness / 2);
            int maxRadiusY = Math.Max(0, cy - (rect.Y + padding + extraMargin) - ringThickness / 2);
            int radius = Math.Max(10, Math.Min(maxRadiusX, maxRadiusY));

            // Anillo externo e interno (profundidad)
            DrawArc(ctx, cx, cy, radius, -180, 0, _gaugeBaseColor, ringThickness, 72);
            DrawArc(ctx, cx, cy, radius - ringThickness - innerGap, -180, 0, _gaugeInnerBaseColor, Math.Max(1, ringThickness - 2), 72);

            // Bandas de zonas (debajo del progreso) para que no tape el indicador
            if (_gaugeShowBands)
            {
                float aStart = -180f;
                float aEnd = 0f;
                float aLowEnd = aStart + (float)_gaugeBandLow * (aEnd - aStart);
                float aHighStart = aStart + (float)_gaugeBandHigh * (aEnd - aStart);
                int bandThickness = Math.Max(2, ringThickness - 6);
                var lowCol = Color.FromArgb(100, _gaugeBandLowColor);
                var midCol = Color.FromArgb(100, _gaugeBandMidColor);
                var highCol = Color.FromArgb(100, _gaugeBandHighColor);

                DrawArc(ctx, cx, cy, radius, aStart, aLowEnd, lowCol, bandThickness, 32);
                DrawArc(ctx, cx, cy, radius, aLowEnd, aHighStart, midCol, bandThickness, 32);
                DrawArc(ctx, cx, cy, radius, aHighStart, aEnd, highCol, bandThickness, 32);
            }

            // Valor a mostrar con suavizado opcional
            decimal val = value ?? 0m;
            if (_gaugeSmoothing > 0m)
            {
                var key = metric.Title ?? "__gauge__";
                if (!_smoothValues.TryGetValue(key, out var last)) last = val;
                var smoothed = last + (val - last) * _gaugeSmoothing;
                _smoothValues[key] = smoothed;
                val = smoothed;
            }

            if (max <= min) max = min + 1;
            var clamped = Math.Max(min, Math.Min(max, val));

            // Progreso solo en el lado correspondiente: verde (derecha, >0), rojo (izquierda, <0)
            float zeroAngle = -90f;
            int segs = 90;
            if (clamped > 0m)
            {
                float tPos = (float)(clamped / Math.Max(1e-8m, (max - 0m))); // 0..1 relativo al lado derecho
                tPos = Math.Max(0f, Math.Min(1f, tPos));
                float endRight = zeroAngle + tPos * 90f; // -90 .. 0
                DrawArc(ctx, cx, cy, radius, zeroAngle, endRight, _gaugeProgressPositive, ringThickness, segs);
            }
            else if (clamped < 0m)
            {
                float tNeg = (float)((0m - clamped) / Math.Max(1e-8m, (0m - min))); // 0..1 relativo al lado izquierdo
                tNeg = Math.Max(0f, Math.Min(1f, tNeg));
                float startLeft = zeroAngle - tNeg * 90f; // -180 .. -90
                DrawArc(ctx, cx, cy, radius, startLeft, zeroAngle, _gaugeProgressNegative, ringThickness, segs);
            }

            // Marca del 0 (arriba)
            DrawArc(ctx, cx, cy, radius, -92, -88, _gaugeZeroTickColor, Math.Max(2, ringThickness - 2), 4);

            // Etiquetas mín/0/máx
            string tMin = ((double)min).ToString("0.#", CultureInfo.InvariantCulture);
            string tZero = "0";
            string tMax = ((double)max).ToString("0.#", CultureInfo.InvariantCulture);
            int offText = ringThickness + 8;
            DrawLabelOnArc(ctx, tMin, _fontNorm, Color.Gainsboro, cx, cy, radius + 2, -180, -offText);
            DrawLabelOnArc(ctx, tZero, _fontNorm, Color.Gainsboro, cx, cy, radius + 2, -90, -offText);
            DrawLabelOnArc(ctx, tMax, _fontNorm, Color.Gainsboro, cx, cy, radius + 2, 0, -offText);

            // Título
            int titleW = MeasureText(ctx, metric.Title, _fontNorm);
            ctx.DrawString(metric.Title, _fontNorm, _titleColor, cx - titleW / 2, rect.Y + _rowPaddingY);

            // Valor grande
            var bigFont = new RenderFont("Segoe UI", Math.Min(_baseFontSize + 6, _baseFontSize * 2), FontStyle.Bold);
            string textVal;
            try
            {
                var fmt = string.IsNullOrWhiteSpace(metric.Format) ? "+0.##;-0.##" : metric.Format;
                textVal = (value ?? 0m).ToString(fmt, CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(metric.Unit) ? string.Empty : metric.Unit);
            }
            catch { textVal = (value ?? 0m).ToString("+0.##;-0.##", CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(metric.Unit) ? string.Empty : metric.Unit); }
            int valW = MeasureText(ctx, textVal, bigFont);
            int valH = MeasureSize(ctx, textVal, bigFont).Height;
            var bigCol = (value ?? 0m) >= 0m ? _gaugeProgressPositive : _gaugeProgressNegative;
            ctx.DrawString(textVal, bigFont, bigCol, cx - valW / 2, cy - valH - ringThickness - 4);

            // Flecha dirección opcional
            if (_gaugeShowArrow)
            {
                string arrow = (val >= 0m) ? "▲" : "▼";
                var arrColor = (val >= 0m) ? _gaugeProgressPositive : _gaugeProgressNegative;
                int arrW = MeasureText(ctx, arrow, _fontNorm);
                ctx.DrawString(arrow, _fontNorm, arrColor, cx - arrW / 2, cy - ringThickness - 4);
            }

            // Detalles opcionales (valor abs y %)
            if (_gaugeShowDetails)
            {
                string details;
                try
                {
                    var pctText = clamped == 0 ? "0%" : ((clamped - 0) / Math.Max(1e-8m, (max - min)) * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";
                    details = pctText;
                }
                catch { details = string.Empty; }
                if (!string.IsNullOrEmpty(details))
                {
                    int dw = MeasureText(ctx, details, _fontNorm);
                    ctx.DrawString(details, _fontNorm, Color.Gainsboro, cx - dw / 2, cy - ringThickness - 4 - (_gaugeShowArrow ? MeasureSize(ctx, "A", _fontNorm).Height + 2 : 0));
                }
            }
        }

        private static void DrawTick(RenderContext ctx, int cx, int cy, int r, float angleDeg, Color color)
        {
            double ang = angleDeg * Math.PI / 180.0;
            int r1 = r - 2;
            int r2 = r - 10;
            int x1 = cx + (int)(Math.Cos(ang) * r1);
            int y1 = cy + (int)(Math.Sin(ang) * r1);
            int x2 = cx + (int)(Math.Cos(ang) * r2);
            int y2 = cy + (int)(Math.Sin(ang) * r2);
            ctx.DrawLine(new RenderPen(color, 1), x1, y1, x2, y2);
        }

        private void DrawLabelOnArc(RenderContext ctx, string text, RenderFont font, Color color, int cx, int cy, int r, float angleDeg, int dy)
        {
            int w = MeasureText(ctx, text, font);
            double ang = angleDeg * Math.PI / 180.0;
            int x = cx + (int)(Math.Cos(ang) * r) - w / 2;
            int y = cy + (int)(Math.Sin(ang) * r) + dy;
            ctx.DrawString(text, font, color, x, y);
        }

        private static void DrawArc(RenderContext ctx, int cx, int cy, int radius, float startDeg, float endDeg, Color color, int thickness, int segments)
        {
            // Normaliza
            if (endDeg < startDeg) { var tmp = startDeg; startDeg = endDeg; endDeg = tmp; }
            double start = startDeg * Math.PI / 180.0;
            double end = endDeg * Math.PI / 180.0;
            double step = Math.Max(0.001, (end - start) / segments);
            int px = cx + (int)(Math.Cos(start) * radius);
            int py = cy + (int)(Math.Sin(start) * radius);
            var pen = new RenderPen(color, thickness);
            for (double a = start + step; a <= end + 1e-6; a += step)
            {
                int x = cx + (int)(Math.Cos(a) * radius);
                int y = cy + (int)(Math.Sin(a) * radius);
                ctx.DrawLine(pen, px, py, x, y);
                px = x; py = y;
            }
        }

        private static Color Lerp(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t)
            );
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
                    // Encuentra índice de cualquiera de los claves
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

        private (decimal? abs, decimal? pct) CalculateGexRates(Dictionary<string, string> map)
        {
            // Usa las dos últimas lecturas persistidas del CSV con timestamps para normalizar a minutos
            decimal? curr;
            decimal? prev;
            DateTime? tsCurr;
            DateTime? tsPrev;
            lock (_sync)
            {
                curr = _gexCurr;
                prev = _gexPrev;
                tsCurr = _gexCurrTs;
                tsPrev = _gexPrevTs;
            }

            if (!curr.HasValue || !prev.HasValue)
                return (null, null);

            var diff = curr.Value - prev.Value;
            // minutos transcurridos entre muestras; si no hay timestamp válido, asumimos 1 min
            double minutes = 1d;
            if (tsCurr.HasValue && tsPrev.HasValue)
            {
                var dt = (tsCurr.Value - tsPrev.Value).TotalMinutes;
                if (dt > 1e-6) minutes = dt;
            }

            var absPerMin = Math.Abs(diff) / (decimal)minutes;
            var denom = Math.Max(Math.Abs(prev.Value), 1e-8m);
            var pctPerMin = ((diff / denom) * 100m) / (decimal)minutes;
            pctPerMin = Math.Max(-100m, Math.Min(100m, pctPerMin));
            return (absPerMin, pctPerMin);
        }

        private (decimal? abs, decimal? pct) CalculateVannaRates(Dictionary<string, string> map)
        {
            // Definición correcta: ((ΔCashCall/prevCashCall) - (ΔCashPut/prevCashPut)) / 2 * 100
            decimal? cashCallPrev, cashCallCurr, cashPutPrev, cashPutCurr;
            lock (_sync)
            {
                cashCallPrev = _cashCallPrev; cashCallCurr = _cashCallCurr;
                cashPutPrev = _cashPutPrev;   cashPutCurr = _cashPutCurr;
            }

            decimal? callPct = null, putPct = null; // en porcentaje ya multiplicado por 100
            if (cashCallPrev.HasValue && cashCallPrev.Value != 0m && cashCallCurr.HasValue)
                callPct = ((cashCallCurr.Value - cashCallPrev.Value) / cashCallPrev.Value) * 100m;
            if (cashPutPrev.HasValue && cashPutPrev.Value != 0m && cashPutCurr.HasValue)
                putPct = ((cashPutCurr.Value - cashPutPrev.Value) / cashPutPrev.Value) * 100m;

            decimal? resultPct = null;
            if (callPct.HasValue && putPct.HasValue)
                resultPct = (callPct.Value - putPct.Value) / 2m;
            else if (callPct.HasValue)
                resultPct = callPct.Value / 2m;       // asumiendo put≈0
            else if (putPct.HasValue)
                resultPct = (-putPct.Value) / 2m;     // asumiendo call≈0

            return (null, resultPct);
        }

        private (decimal? abs, decimal? pct) CalculateIvSkewRates(Dictionary<string, string> map)
        {
            // Definición: Velocidad Skew = ((ΔIVCall/prevIVCall) - (ΔIVPut/prevIVPut)) / 2 * 100
            decimal? ivCallPrev, ivCallCurr, ivPutPrev, ivPutCurr;
            lock (_sync)
            {
                ivCallPrev = _ivCallPrev; ivCallCurr = _ivCallCurr;
                ivPutPrev  = _ivPutPrev;  ivPutCurr  = _ivPutCurr;
            }

            decimal? callPct = null, putPct = null;
            if (ivCallPrev.HasValue && ivCallPrev.Value != 0m && ivCallCurr.HasValue)
                callPct = ((ivCallCurr.Value - ivCallPrev.Value) / ivCallPrev.Value) * 100m;
            if (ivPutPrev.HasValue && ivPutPrev.Value != 0m && ivPutCurr.HasValue)
                putPct = ((ivPutCurr.Value - ivPutPrev.Value) / ivPutPrev.Value) * 100m;

            decimal? resultPct = null;
            if (callPct.HasValue && putPct.HasValue)
                resultPct = (callPct.Value - putPct.Value) / 2m;
            else if (callPct.HasValue)
                resultPct = callPct.Value / 2m;   // asume put≈0
            else if (putPct.HasValue)
                resultPct = (-putPct.Value) / 2m; // asume call≈0

            return (null, resultPct);
        }

        private void InitFileWatcher()
        {
            try
            {
                lock (_fswSync)
                {
                    if (_fsw != null)
                    {
                        _fsw.EnableRaisingEvents = false;
                        _fsw.Changed -= OnCsvChanged;
                        _fsw.Created -= OnCsvChanged;
                        _fsw.Renamed -= OnCsvRenamed;
                        _fsw.Dispose();
                        _fsw = null;
                    }

                    if (!_useFileWatcher || string.IsNullOrWhiteSpace(_csvPath) || !File.Exists(_csvPath))
                        return;

                    var dir = Path.GetDirectoryName(_csvPath);
                    var file = Path.GetFileName(_csvPath);
                    if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(file)) return;

                    _fsw = new FileSystemWatcher(dir, file)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                    };
                    _fsw.Changed += OnCsvChanged;
                    _fsw.Created += OnCsvChanged;
                    _fsw.Renamed += OnCsvRenamed;
                    _fsw.EnableRaisingEvents = _useFileWatcher;
                }
            }
            catch { }
        }

        private void OnCsvChanged(object sender, FileSystemEventArgs e)
        {
            var now = DateTime.UtcNow;
            if ((now - _fswLastEventUtc).TotalMilliseconds < _fileWatcherDebounceMs) return;
            _fswLastEventUtc = now;
            _nextReadUtc = DateTime.MinValue;
            _ = TryScheduleRead();
        }

        private void OnCsvRenamed(object sender, RenamedEventArgs e)
        {
            _csvPath = e.FullPath;
            InitFileWatcher();
            _nextReadUtc = DateTime.MinValue;
            _ = TryScheduleRead();
        }
    }
}
