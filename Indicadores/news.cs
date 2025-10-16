using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace ATAS.Indicators.Technical
{
    [DisplayName("ForexFactory News")]
    public class ForexFactoryNewsIndicator : Indicator
    {
        #region DTO JSON
        private sealed class FfItem
        {
            [JsonPropertyName("title")] public string Title { get; set; }
            [JsonPropertyName("country")] public string Country { get; set; }
            [JsonPropertyName("date")] public DateTime Date { get; set; }
            [JsonPropertyName("impact")] public string Impact { get; set; }
            [JsonPropertyName("forecast")] public string Forecast { get; set; }
            [JsonPropertyName("previous")] public string Previous { get; set; }
        }
        #endregion

        #region Modelo interno
        private sealed class NewsEvent
        {
            public DateTime TimeUtc;
            public DateTime TimeLocal;
            public string Title;
            public string Impact;
            public string Country;
            public string Forecast;
            public string Previous;
            public bool IsToday;
            public bool IsTomorrow;
            public bool IsPast;
            public bool IsCurrent;
            public bool IsSoon;
        }
        #endregion

        #region Campos privados
        private static readonly HttpClient Http = new();
        private const string FfUrl = "https://nfs.faireconomy.media/ff_calendar_thisweek.json";
        private readonly object _sync = new();
        private readonly List<NewsEvent> _allEvents = new();
        private readonly List<NewsEvent> _events = new();

        private bool _fetchInProgress;
        private DateTime _nextFetchUtc = DateTime.MinValue;
        private string _lastStatus = "Esperando carga...";

        private RenderFont _fontNormal;
        private RenderFont _fontBold;
        private RenderFont _fontHeader;

        // Filtros / flags
        private int _fetchIntervalMinutes = 10;
        private bool _showHigh = true;
        private bool _showMedium = true;
        private bool _showLow = true;
        private bool _showHoliday = false;

        private bool _fxUSD = true;
        private bool _fxEUR = true;
        private bool _fxGBP = true;
        private bool _fxJPY = true;
        private bool _fxAUD;
        private bool _fxCAD;
        private bool _fxNZD;
        private bool _fxCHF;
        private bool _fxALL = true;

        private int _maxEvents = 40;
        private bool _showTomorrow = true;
        private int _soonMinutes = 15;
        private int _hidePastMinutes = 60;
        private bool _showForecastPrevious = true;
        private Color _headerColor = Color.FromArgb(30, 30, 60);
        private Color _headerTextColor = Color.LightSkyBlue;
        private int _baseFontSize = 11;
        private int _blockSpacing = 6;
        private bool _alignRight;
        private bool _drawTimeMarkers = true;
        private bool _injectDummyIfEmpty = true;

        // Estética
        private bool _useImpactShapes = true;
        private ImpactShape _impactShapeStyle = ImpactShape.Circle;
        private bool _alternateRows = true;
        private Color _rowAlt1 = Color.FromArgb(12, 12, 20);
        private Color _rowAlt2 = Color.FromArgb(25, 25, 40);
        private bool _showRowSeparator = true;
        private Color _rowSeparatorColor = Color.FromArgb(35, 35, 60);
        private Color _currentBack = Color.FromArgb(60, 255, 255, 255);
        private Color _soonBack = Color.FromArgb(40, 255, 200, 0);
        private Color _timeColor = Color.FromArgb(180, 200, 200, 210);
        private Color _timeSoonColor = Color.FromArgb(255, 255, 200, 120);
        private Color _timeCurrentColor = Color.White;
        private bool _boldCurrent = true;
        private bool _shadowText = true;
        private int _timeShapeGapPx = 12; // separación hora → icono

        // Interlineado y offsets verticales
        private int _rowSpacingPx = 0;          // extra al alto base de cada fila
        private int _rowTextOffsetY = 1;        // desplazamiento vertical adicional del texto (fino)

        // Centrado vertical
        private bool _centerTextVertically = true; // NUEVO

        // Ancho del panel (sombreado)
        private bool _autoShadeWidth = true;    // si true, calcula por tamaño de fuente
        private float _shadeWidthFactor = 28f;  // panel ≈ BaseFontSize * factor
        private int _fixedShadeWidthPx = 420;   // si _autoShadeWidth=false
        private int _minShadeWidthPx = 220;
        private int _maxShadeWidthPx = 560;

        // Posicionamiento horizontal/offset del panel
        private PanelAlign _panelAlign = PanelAlign.Left;
        private int _panelOffsetX = 0;
        private int _panelOffsetY = 0;
        private int _panelRightPadding = 10; // separación a la derecha de la última barra

        // Rango horario, export, contadores y countdown
        private bool _timeRangeEnabled;
        private TimeSpan _timeRangeFrom = new(0, 0, 0);
        private TimeSpan _timeRangeTo = new(23, 59, 59);
        private bool _exportUpcoming;
        private string _exportPath = "ff_upcoming_events.txt";
        private int _exportLookAheadMinutes = 240;
        private bool _showImpactCounters = true;
        private bool _showCountdown = true;
        private Dictionary<string, int> _todayCounters = new();
        private Dictionary<string, int> _tomorrowCounters = new();
        private System.Threading.Timer _countdownTimer;
        private bool _countdownTimerActive;
        private int _countdownRefreshMs = 1000;

        // Cache medidas texto
        private readonly Dictionary<(string,float,bool), int> _textWidthCache = new();
        private DateTime _lastWidthCacheClear = DateTime.UtcNow;

        public enum ImpactShape { Circle, Square, Diamond, Bar }
        public enum PanelAlign { Left, Center, Right }
        #endregion

        #region Propiedades
        [Category("Actualización"), Display(Name = "Intervalo fetch (min)", Order = 0)]
        [Range(1, 240)]
        public int FetchIntervalMinutes { get => _fetchIntervalMinutes; set { if (value == _fetchIntervalMinutes) return; _fetchIntervalMinutes = Math.Max(1, value); } }

        [Category("Actualización"), Display(Name = "Forzar refresco", Order = 1)]
        public bool RefreshNow { get => false; set { if (value) { _nextFetchUtc = DateTime.MinValue; TryScheduleFetch(); } } }

        // Impacto
        [Category("Filtro Impacto"), Display(Name = "High", Order = 0)]
        public bool ShowHigh { get => _showHigh; set { if (value == _showHigh) return; _showHigh = value; Refilter(); } }
        [Category("Filtro Impacto"), Display(Name = "Medium", Order = 1)]
        public bool ShowMedium { get => _showMedium; set { if (value == _showMedium) return; _showMedium = value; Refilter(); } }
        [Category("Filtro Impacto"), Display(Name = "Low", Order = 2)]
        public bool ShowLow { get => _showLow; set { if (value == _showLow) return; _showLow = value; Refilter(); } }
        [Category("Filtro Impacto"), Display(Name = "Holiday / Other", Order = 3)]
        public bool ShowHoliday { get => _showHoliday; set { if (value == _showHoliday) return; _showHoliday = value; Refilter(); } }

        // País
        [Category("Filtro País"), Display(Name = "USD", Order = 0)]
        public bool FxUSD { get => _fxUSD; set { if (value == _fxUSD) return; _fxUSD = value; Refilter(); } }
        [Category("Filtro País"), Display(Name = "EUR", Order = 1)]
        public bool FxEUR { get => _fxEUR; set { if (value == _fxEUR) return; _fxEUR = value; Refilter(); } }
        [Category("Filtro País"), Display(Name = "GBP", Order = 2)]
        public bool FxGBP { get => _fxGBP; set { if (value == _fxGBP) return; _fxGBP = value; Refilter(); } }
        [Category("Filtro País"), Display(Name = "JPY", Order = 3)]
        public bool FxJPY { get => _fxJPY; set { if (value == _fxJPY) return; _fxJPY = value; Refilter(); } }
        [Category("Filtro País"), Display(Name = "AUD", Order = 4)]
        public bool FxAUD { get => _fxAUD; set { if (value == _fxAUD) return; _fxAUD = value; Refilter(); } }
        [Category("Filtro País"), Display(Name = "CAD", Order = 5)]
        public bool FxCAD { get => _fxCAD; set { if (value == _fxCAD) return; _fxCAD = value; Refilter(); } }
        [Category("Filtro País"), Display(Name = "NZD", Order = 6)]
        public bool FxNZD { get => _fxNZD; set { if (value == _fxNZD) return; _fxNZD = value; Refilter(); } }
        [Category("Filtro País"), Display(Name = "CHF", Order = 7)]
        public bool FxCHF { get => _fxCHF; set { if (value == _fxCHF) return; _fxCHF = value; Refilter(); } }
        [Category("Filtro País"), Display(Name = "ALL (Global)", Order = 8)]
        public bool FxALL { get => _fxALL; set { if (value == _fxALL) return; _fxALL = value; Refilter(); } }

        // Visual
        [Category("Visual"), Display(Name = "Máx eventos mostrar", Order = 0)]
        [Range(1, 200)]
        public int MaxEvents { get => _maxEvents; set { var v = Math.Max(1, value); if (v == _maxEvents) return; _maxEvents = v; Refilter(); } }

        [Category("Visual"), Display(Name = "Mostrar de mañana", Order = 1)]
        public bool ShowTomorrow { get => _showTomorrow; set { if (value == _showTomorrow) return; _showTomorrow = value; Refilter(); } }

        [Category("Visual"), Display(Name = "Minutos 'Próximo'", Order = 2)]
        [Range(1, 180)]
        public int SoonMinutes { get => _soonMinutes; set { var v = Math.Max(1, value); if (v == _soonMinutes) return; _soonMinutes = v; RecalcFlags(); } }

        [Category("Visual"), Display(Name = "Ocultar pasados (min)", Order = 3)]
        [Range(0, 240)]
        public int HidePastMinutes { get => _hidePastMinutes; set { var v = Math.Max(0, value); if (v == _hidePastMinutes) return; _hidePastMinutes = v; Refilter(); } }

        [Category("Visual"), Display(Name = "Mostrar Forecast/Previous", Order = 4)]
        public bool ShowForecastPrevious { get => _showForecastPrevious; set { if (value == _showForecastPrevious) return; _showForecastPrevious = value; RedrawChart(); } }

        [Category("Visual"), Display(Name = "Color fondo cabecera", Order = 5)]
        public Color HeaderColor { get => _headerColor; set { if (value == _headerColor) return; _headerColor = value; RedrawChart(); } }

        [Category("Visual"), Display(Name = "Color texto cabecera", Order = 6)]
        public Color HeaderTextColor { get => _headerTextColor; set { if (value == _headerTextColor) return; _headerTextColor = value; RedrawChart(); } }

        [Category("Visual"), Display(Name = "Fuente base (px)", Order = 7)]
        [Range(8, 48)]
        public int BaseFontSize
        {
            get => _baseFontSize;
            set
            {
                var v = Math.Max(8, value);
                if (v == _baseFontSize) return;
                _baseFontSize = v;
                _fontNormal = new RenderFont("Arial", _baseFontSize);
                _fontBold = new RenderFont("Arial", _baseFontSize, FontStyle.Bold);
                _fontHeader = new RenderFont("Arial", _baseFontSize + 1, FontStyle.Bold);
                ClearWidthCache();
                RedrawChart();
            }
        }

        [Category("Visual"), Display(Name = "Separa bloques (px)", Order = 8)]
        [Range(0, 50)]
        public int BlockSpacing { get => _blockSpacing; set { var v = Math.Max(0, value); if (v == _blockSpacing) return; _blockSpacing = v; RedrawChart(); } }

        [Category("Visual"), Display(Name = "Alineación derecha (texto)", Order = 9)]
        public bool AlignRight { get => _alignRight; set { if (value == _alignRight) return; _alignRight = value; RedrawChart(); } }

        [Category("Visual"), Display(Name = "Mostrar líneas tiempo", Order = 10)]
        public bool DrawTimeMarkers { get => _drawTimeMarkers; set { if (value == _drawTimeMarkers) return; _drawTimeMarkers = value; RedrawChart(); } }

        // Avanzado (iconos y panel)
        [Category("Visual Avanzado"), Display(Name = "Formas impacto", Order = 0)]
        public bool UseImpactShapes { get => _useImpactShapes; set { if (value == _useImpactShapes) return; _useImpactShapes = value; RedrawChart(); } }

        [Category("Visual Avanzado"), Display(Name = "Tipo forma", Order = 1)]
        public ImpactShape ImpactShapeStyle { get => _impactShapeStyle; set { if (value == _impactShapeStyle) return; _impactShapeStyle = value; RedrawChart(); } }

        [Category("Panel"), Display(Name = "Posición panel", Order = 0)]
        public PanelAlign PanelPosition { get => _panelAlign; set { if (value == _panelAlign) return; _panelAlign = value; RedrawChart(); } }

        [Category("Panel"), Display(Name = "Offset X (px)", Order = 1)]
        [Range(-2000, 2000)]
        public int PanelOffsetX { get => _panelOffsetX; set { if (value == _panelOffsetX) return; _panelOffsetX = value; RedrawChart(); } }

        [Category("Panel"), Display(Name = "Offset Y (px)", Order = 2)]
        [Range(-2000, 2000)]
        public int PanelOffsetY { get => _panelOffsetY; set { if (value == _panelOffsetY) return; _panelOffsetY = value; RedrawChart(); } }

        [Category("Panel"), Display(Name = "Padding derecho (px)", Order = 3)]
        [Range(0, 200)]
        public int PanelRightPadding { get => _panelRightPadding; set { if (value == _panelRightPadding) return; _panelRightPadding = Math.Max(0, value); RedrawChart(); } }

        [Category("Visual Avanzado"), Display(Name = "Gap hora-forma (px)", Order = 4)]
        [Range(2, 60)]
        public int TimeShapeGapPx { get => _timeShapeGapPx; set { var v = Math.Max(2, value); if (v == _timeShapeGapPx) return; _timeShapeGapPx = v; RedrawChart(); } }

        [Category("Visual Avanzado"), Display(Name = "Interlineado extra (px)", Order = 5)]
        [Range(0, 40)]
        public int RowSpacingPx { get => _rowSpacingPx; set { var v = Math.Max(0, value); if (v == _rowSpacingPx) return; _rowSpacingPx = v; RedrawChart(); } }

        [Category("Visual Avanzado"), Display(Name = "Offset vertical texto (px)", Order = 6)]
        [Range(-10, 20)]
        public int RowTextOffsetY { get => _rowTextOffsetY; set { if (value == _rowTextOffsetY) return; _rowTextOffsetY = value; RedrawChart(); } }

        [Category("Visual Avanzado"), Display(Name = "Centrar texto vertical", Order = 7)]
        public bool CenterTextVertically { get => _centerTextVertically; set { if (value == _centerTextVertically) return; _centerTextVertically = value; RedrawChart(); } }

        [Category("Visual Avanzado"), Display(Name = "Ancho sombreado automático", Order = 8)]
        public bool AutoShadeWidth { get => _autoShadeWidth; set { if (value == _autoShadeWidth) return; _autoShadeWidth = value; RedrawChart(); } }

        [Category("Visual Avanzado"), Display(Name = "Factor ancho (px/pt fuente)", Order = 9)]
        [Range(10, 100)]
        public double ShadeWidthFactor { get => _shadeWidthFactor; set { var v = (float)Math.Max(10, Math.Min(100, value)); if (Math.Abs(v - _shadeWidthFactor) < 0.001f) return; _shadeWidthFactor = v; RedrawChart(); } }

        [Category("Visual Avanzado"), Display(Name = "Ancho sombreado fijo (px)", Order = 10)]
        [Range(120, 1200)]
        public int FixedShadeWidthPx { get => _fixedShadeWidthPx; set { var v = Math.Max(120, value); if (v == _fixedShadeWidthPx) return; _fixedShadeWidthPx = v; RedrawChart(); } }

        [Category("Visual Avanzado"), Display(Name = "Ancho sombreado mín-máx", Order = 11)]
        public string ShadeWidthClamp
        {
            get => $"{_minShadeWidthPx}-{_maxShadeWidthPx}";
            set
            {
                var parts = value.Split('-', '–', '—');
                if (parts.Length == 2 && int.TryParse(parts[0], out var mn) && int.TryParse(parts[1], out var mx) && mn > 0 && mx > mn)
                { _minShadeWidthPx = mn; _maxShadeWidthPx = mx; RedrawChart(); }
            }
        }

        // Alternancia y separadores
        [Category("Visual Avanzado"), Display(Name = "Filas alternas", Order = 12)]
        public bool AlternateRows { get => _alternateRows; set { if (value == _alternateRows) return; _alternateRows = value; RedrawChart(); } }

        [Category("Visual Avanzado"), Display(Name = "Color fila A", Order = 13)]
        public Color RowAlt1 { get => _rowAlt1; set { if (value == _rowAlt1) return; _rowAlt1 = value; RedrawChart(); } }

        [Category("Visual Avanzado"), Display(Name = "Color fila B", Order = 14)]
        public Color RowAlt2 { get => _rowAlt2; set { if (value == _rowAlt2) return; _rowAlt2 = value; RedrawChart(); } }

        [Category("Visual Avanzado"), Display(Name = "Separador filas", Order = 15)]
        public bool ShowRowSeparator { get => _showRowSeparator; set { if (value == _showRowSeparator) return; _showRowSeparator = value; RedrawChart(); } }

        [Category("Visual Avanzado"), Display(Name = "Color separador", Order = 16)]
        public Color RowSeparatorColor { get => _rowSeparatorColor; set { if (value == _rowSeparatorColor) return; _rowSeparatorColor = value; RedrawChart(); } }

        // Resaltado
        [Category("Resaltado"), Display(Name = "Fondo CURRENT", Order = 0)]
        public Color CurrentBack { get => _currentBack; set { if (value == _currentBack) return; _currentBack = value; RedrawChart(); } }
        [Category("Resaltado"), Display(Name = "Fondo SOON", Order = 1)]
        public Color SoonBack { get => _soonBack; set { if (value == _soonBack) return; _soonBack = value; RedrawChart(); } }
        [Category("Resaltado"), Display(Name = "Color hora (normal)", Order = 2)]
        public Color TimeColor { get => _timeColor; set { if (value == _timeColor) return; _timeColor = value; RedrawChart(); } }
        [Category("Resaltado"), Display(Name = "Color hora (soon)", Order = 3)]
        public Color TimeSoonColor { get => _timeSoonColor; set { if (value == _timeSoonColor) return; _timeSoonColor = value; RedrawChart(); } }
        [Category("Resaltado"), Display(Name = "Color hora (current)", Order = 4)]
        public Color TimeCurrentColor { get => _timeCurrentColor; set { if (value == _timeCurrentColor) return; _timeCurrentColor = value; RedrawChart(); } }
        [Category("Resaltado"), Display(Name = "Negrita current/soon", Order = 5)]
        public bool BoldCurrent { get => _boldCurrent; set { if (value == _boldCurrent) return; _boldCurrent = value; RedrawChart(); } }
        [Category("Resaltado"), Display(Name = "Sombra texto", Order = 6)]
        public bool ShadowText { get => _shadowText; set { if (value == _shadowText) return; _shadowText = value; RedrawChart(); } }

        // Rango horario
        [Category("Filtro Horario"), Display(Name = "Filtrar por rango", Order = 0)]
        public bool TimeRangeFilterEnabled { get => _timeRangeEnabled; set { if (value == _timeRangeEnabled) return; _timeRangeEnabled = value; Refilter(); } }

        [Category("Filtro Horario"), Display(Name = "Desde (HH:mm)", Order = 1)]
        public string TimeRangeFrom
        {
            get => _timeRangeFrom.ToString(@"hh\:mm");
            set
            {
                if (TimeSpan.TryParse(value, out var ts))
                { if (ts == _timeRangeFrom) return; _timeRangeFrom = ts; if (_timeRangeEnabled) Refilter(); }
            }
        }

        [Category("Filtro Horario"), Display(Name = "Hasta (HH:mm)", Order = 2)]
        public string TimeRangeTo
        {
            get => _timeRangeTo.ToString(@"hh\:mm");
            set
            {
                if (TimeSpan.TryParse(value, out var ts))
                { if (ts == _timeRangeTo) return; _timeRangeTo = ts; if (_timeRangeEnabled) Refilter(); }
            }
        }

        // Exportación
        [Category("Export"), Display(Name = "Exportar próximos", Order = 0)]
        public bool ExportUpcoming { get => _exportUpcoming; set { if (value == _exportUpcoming) return; _exportUpcoming = value; if (value) ExportUpcomingEvents(); } }

        [Category("Export"), Display(Name = "Ruta archivo", Order = 1)]
        public string ExportPath { get => _exportPath; set { if (string.IsNullOrWhiteSpace(value) || value == _exportPath) return; _exportPath = value; if (_exportUpcoming) ExportUpcomingEvents(); } }

        [Category("Export"), Display(Name = "Ventana minutos", Order = 2)]
        [Range(1, 1440)]
        public int ExportLookAheadMinutes { get => _exportLookAheadMinutes; set { var v = Math.Max(1, value); if (v == _exportLookAheadMinutes) return; _exportLookAheadMinutes = v; if (_exportUpcoming) ExportUpcomingEvents(); } }

        // Resumen
        [Category("Resumen"), Display(Name = "Mostrar contadores impacto", Order = 0)]
        public bool ShowImpactCounters { get => _showImpactCounters; set { if (value == _showImpactCounters) return; _showImpactCounters = value; RedrawChart(); } }

        [Category("Resumen"), Display(Name = "Mostrar cuenta atrás", Order = 1)]
        public bool ShowCountdown
        {
            get => _showCountdown;
            set { if (value == _showCountdown) return; _showCountdown = value; SetupCountdownTimer(); RedrawChart(); }
        }

        [Category("Sincronización"), Display(Name = "Forzar Dummy si vacío", Order = 0)]
        public bool InjectDummyIfEmpty { get => _injectDummyIfEmpty; set { if (value == _injectDummyIfEmpty) return; _injectDummyIfEmpty = value; Refilter(); } }
        #endregion

        #region Constructor
        public ForexFactoryNewsIndicator()
        {
            EnableCustomDrawing = true;
            DenyToChangePanel = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);
            SubscribeToDrawingEvents(DrawingLayouts.Final);
        }
        protected override void OnInitialize()
        {
            _fontNormal = new RenderFont("Arial", _baseFontSize);
            _fontBold = new RenderFont("Arial", _baseFontSize, FontStyle.Bold);
            _fontHeader = new RenderFont("Arial", _baseFontSize + 1, FontStyle.Bold);
            _nextFetchUtc = DateTime.MinValue;
            TryScheduleFetch();
            SetupCountdownTimer();
        }
        protected override void OnDispose()
        {
            _countdownTimer?.Dispose();
            base.OnDispose();
        }
        #endregion

        #region Scheduler
        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar < CurrentBar - 1) return;
            TryScheduleFetch();
        }
        private void TryScheduleFetch()
        {
            var now = DateTime.UtcNow;
            if (_fetchInProgress || now < _nextFetchUtc) return;

            _fetchInProgress = true;
            _lastStatus = "Descargando...";
            RedrawChart();

            _ = Task.Run(async () =>
            {
                try
                {
                    await FetchAsync();
                    _lastStatus = $"Actualizado: {DateTime.Now:HH:mm:ss}";
                }
                catch { _lastStatus = "Error descarga"; }
                finally
                {
                    _nextFetchUtc = DateTime.UtcNow.AddMinutes(_fetchIntervalMinutes);
                    _fetchInProgress = false;
                    RedrawChart();
                }
            });
        }
        #endregion

        #region Fetch
        private async Task FetchAsync()
        {
            await FetchForexFactoryAsync();
            Refilter();
        }
        private async Task FetchForexFactoryAsync()
        {
            var resp = await Http.GetAsync(FfUrl);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync();
            var items = await JsonSerializer.DeserializeAsync<List<FfItem>>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var list = new List<NewsEvent>();
            if (items != null)
            {
                foreach (var f in items)
                {
                    var utc = f.Date.Kind == DateTimeKind.Utc ? f.Date : DateTime.SpecifyKind(f.Date, DateTimeKind.Utc);
                    var local = ConvertToInstrumentTime(utc);
                    list.Add(new NewsEvent
                    {
                        TimeUtc = utc,
                        TimeLocal = local,
                        Title = f.Title,
                        Impact = f.Impact,
                        Country = f.Country,
                        Forecast = f.Forecast,
                        Previous = f.Previous
                    });
                }
            }
            lock (_sync)
            {
                _allEvents.Clear();
                _allEvents.AddRange(list);
            }
        }
        #endregion

        #region Filtrado y Flags
        private void Refilter()
        {
            lock (_sync)
            {
                _events.Clear();
                foreach (var ev in _allEvents)
                {
                    if (!PassImpact(ev.Impact)) continue;
                    if (!PassCountry(ev.Country)) continue;
                    if (_timeRangeEnabled && !PassTimeRange(ev.TimeLocal)) continue;
                    _events.Add(ev);
                }
                _events.Sort((a, b) => a.TimeLocal.CompareTo(b.TimeLocal));
                if (_events.Count > _maxEvents)
                    _events.RemoveRange(0, _events.Count - _maxEvents);

                if (_injectDummyIfEmpty && _events.Count == 0)
                {
                    var nowUtc = DateTime.UtcNow;
                    var local = ConvertToInstrumentTime(nowUtc.AddMinutes(15));
                    _events.Add(new NewsEvent
                    {
                        TimeUtc = nowUtc.AddMinutes(15),
                        TimeLocal = local,
                        Title = "Sin datos (dummy)",
                        Impact = "Medium",
                        Country = "ALL"
                    });
                }
                ApplyFlags_NoLock();
                ComputeImpactCounters_NoLock();
            }
            if (_exportUpcoming) ExportUpcomingEvents();
            RedrawChart();
        }
        private void RecalcFlags()
        {
            lock (_sync)
            {
                ApplyFlags_NoLock();
                ComputeImpactCounters_NoLock();
            }
            RedrawChart();
        }
        private void ApplyFlags_NoLock()
        {
            var nowLocal = ConvertToInstrumentTime(DateTime.UtcNow);
            var today = nowLocal.Date;
            var tomorrow = today.AddDays(1);
            foreach (var e in _events)
            {
                e.IsToday = e.TimeLocal.Date == today;
                e.IsTomorrow = e.TimeLocal.Date == tomorrow;
                e.IsPast = e.TimeLocal < nowLocal.AddMinutes(-_hidePastMinutes);
                e.IsCurrent = Math.Abs((e.TimeLocal - nowLocal).TotalMinutes) < 0.99;
                e.IsSoon = !e.IsPast && !e.IsCurrent &&
                           (e.TimeLocal > nowLocal) &&
                           (e.TimeLocal - nowLocal).TotalMinutes <= _soonMinutes;
            }
            if (!_showTomorrow) _events.RemoveAll(x => x.IsTomorrow);
            if (_hidePastMinutes >= 0) _events.RemoveAll(x => x.IsPast);
        }
        private void ComputeImpactCounters_NoLock()
        {
            _todayCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _tomorrowCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _events)
            {
                var bucket = e.IsTomorrow ? _tomorrowCounters : (e.IsToday ? _todayCounters : null);
                if (bucket == null) continue;
                var key = NormalizeImpact(e.Impact);
                bucket.TryGetValue(key, out var cnt);
                bucket[key] = cnt + 1;
            }
        }
        #endregion

        #region Helpers filtro
        private bool PassImpact(string impact)
        {
            if (string.IsNullOrWhiteSpace(impact)) return true;
            switch (impact.Trim().ToLowerInvariant())
            {
                case "high": return _showHigh;
                case "medium": return _showMedium;
                case "low": return _showLow;
                case "holiday":
                case "non-economic":
                case "none": return _showHoliday;
                default: return true;
            }
        }
        private bool PassCountry(string country)
        {
            if (string.IsNullOrWhiteSpace(country)) return true;
            switch (country.ToUpperInvariant())
            {
                case "USD": return _fxUSD;
                case "EUR": return _fxEUR;
                case "GBP": return _fxGBP;
                case "JPY": return _fxJPY;
                case "AUD": return _fxAUD;
                case "CAD": return _fxCAD;
                case "NZD": return _fxNZD;
                case "CHF": return _fxCHF;
                case "ALL": return _fxALL;
                default: return true;
            }
        }
        private bool PassTimeRange(DateTime localTime)
        {
            var t = localTime.TimeOfDay;
            if (_timeRangeFrom <= _timeRangeTo) return t >= _timeRangeFrom && t <= _timeRangeTo;
            // Rango cruzando medianoche
            return t >= _timeRangeFrom || t <= _timeRangeTo;
        }
        private DateTime ConvertToInstrumentTime(DateTime utc)
        {
            try
            {
                var offset = InstrumentInfo?.TimeZone ?? 0;
                return utc.AddHours(offset);
            }
            catch { return utc.ToLocalTime(); }
        }
        #endregion

        #region Exportación
        private void ExportUpcomingEvents()
        {
            try
            {
                List<NewsEvent> copy;
                DateTime nowLocal = ConvertToInstrumentTime(DateTime.UtcNow);
                lock (_sync) copy = _events.ToList();
                var limit = nowLocal.AddMinutes(_exportLookAheadMinutes);
                var sb = new StringBuilder();
                sb.AppendLine($"Export @ {nowLocal:yyyy-MM-dd HH:mm:ss} (hasta +{_exportLookAheadMinutes}m)");
                foreach (var ev in copy.Where(e => e.TimeLocal >= nowLocal && e.TimeLocal <= limit))
                {
                    var imp = NormalizeImpact(ev.Impact);
                    var cd = _showCountdown && ev.IsSoon ? CountdownString(ev.TimeLocal, nowLocal) : "";
                    sb.AppendLine($"{ev.TimeLocal:HH:mm} | {imp,-6} | {ev.Country} | {ev.Title}{(string.IsNullOrWhiteSpace(cd) ? "" : $" | {cd}")}");
                }
                File.WriteAllText(_exportPath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Ignorar
            }
        }
        #endregion

        #region Countdown timer
        private void SetupCountdownTimer()
        {
            if (!_showCountdown)
            {
                _countdownTimer?.Dispose();
                _countdownTimer = null;
                _countdownTimerActive = false;
                return;
            }
            if (_countdownTimerActive) return;
            _countdownTimer = new System.Threading.Timer(_ =>
            {
                bool anySoon;
                lock (_sync)
                {
                    var nowLocal = ConvertToInstrumentTime(DateTime.UtcNow);
                    anySoon = _events.Any(e => e.IsSoon && e.TimeLocal > nowLocal);
                }
                if (anySoon) RecalcFlags();
            }, null, _countdownRefreshMs, _countdownRefreshMs);
            _countdownTimerActive = true;
        }
        private string CountdownString(DateTime targetLocal, DateTime nowLocal)
        {
            var diff = targetLocal - nowLocal;
            if (diff.TotalSeconds < 0) return "";
            return $"{(int)diff.TotalMinutes:00}:{diff.Seconds:00}";
        }
        #endregion

        #region Render
        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo?.PriceChartContainer == null) return;

            List<NewsEvent> snapshot;
            string status;
            Dictionary<string, int> todayCnt, tomorrowCnt;
            lock (_sync)
            {
                snapshot = _events.ToList();
                status = _lastStatus;
                todayCnt = new Dictionary<string, int>(_todayCounters, StringComparer.OrdinalIgnoreCase);
                tomorrowCnt = new Dictionary<string, int>(_tomorrowCounters, StringComparer.OrdinalIgnoreCase);
            }

            // Columnas y dimensiones
            int timeColWidth = GetTextWidth("22:22", _fontNormal) + 6;
            int shapeSize = Math.Max(6, Math.Min(14, _baseFontSize - 1));
            int shapeColWidth = _useImpactShapes ? (shapeSize + 4 + _timeShapeGapPx) : 0;
            int panelWidth = ComputePanelWidth(timeColWidth, shapeColWidth);
            int panelLeft = ComputePanelLeft(panelWidth);
            int panelTop = Math.Max(0, _panelOffsetY); // <- puede mantener 0 si no quieres que suba

            // Cabecera
            var todaySummary = _showImpactCounters ? FormatCounters(todayCnt) : "";
            int headerH = _baseFontSize + 6;
            int headerTextY = panelTop + CenterOffset(_fontHeader, headerH);
            context.FillRectangle(_headerColor, new Rectangle(panelLeft, panelTop, panelWidth, headerH));
            context.DrawString($"FF News | {status} {todaySummary}", _fontHeader, _headerTextColor, panelLeft + 8, headerTextY);
            if (snapshot.Count == 0) return;

            int y = panelTop + headerH + 4;
            int rowH = _baseFontSize + 4 + _rowSpacingPx;
            bool headerToday = false;
            bool headerTomorrow = false;
            int rowIndex = 0;
            var nowLocalRef = ConvertToInstrumentTime(DateTime.UtcNow);

            foreach (var ev in snapshot)
            {
                if (ev.IsToday && !headerToday)
                {
                    y = DrawSectionHeader(context, "HOY" + (_showImpactCounters ? $" {FormatCounters(todayCnt)}" : ""), y, panelLeft, panelWidth);
                    headerToday = true;
                }
                if (ev.IsTomorrow && !headerTomorrow)
                {
                    y += _blockSpacing;
                    y = DrawSectionHeader(context, "MAÑANA" + (_showImpactCounters ? $" {FormatCounters(tomorrowCnt)}" : ""), y, panelLeft, panelWidth);
                    headerTomorrow = true;
                }

                int rowTop = y;

                // Fondos dentro del panel
                if (_alternateRows)
                    context.FillRectangle((rowIndex % 2 == 0) ? _rowAlt1 : _rowAlt2, new Rectangle(panelLeft, rowTop, panelWidth, rowH));
                if (ev.IsSoon)
                    context.FillRectangle(_soonBack, new Rectangle(panelLeft, rowTop, panelWidth, rowH));
                if (ev.IsCurrent)
                    context.FillRectangle(_currentBack, new Rectangle(panelLeft, rowTop, panelWidth, rowH));

                // Hora + countdown (centrado vertical)
                var timeColor = ev.IsCurrent ? _timeCurrentColor : ev.IsSoon ? _timeSoonColor : _timeColor;
                var timeStr = ev.TimeLocal.ToString("HH:mm");
                if (_showCountdown && ev.IsSoon && ev.TimeLocal > nowLocalRef)
                    timeStr += " " + CountdownString(ev.TimeLocal, nowLocalRef);
                int leftMargin = panelLeft + 8;
                int timeY = ComputeTextY(_fontNormal, rowTop, rowH);
                DrawTextWithShadow(context, timeStr, _fontNormal, timeColor, leftMargin, timeY);

                // Icono
                int shapeCenterY = rowTop + rowH / 2;
                int shapeCenterX = leftMargin + timeColWidth + (_timeShapeGapPx / 2);
                if (_useImpactShapes)
                    DrawImpactShape(context, shapeCenterX, shapeCenterY, ev, shapeSize);

                // Texto
                string impactN = NormalizeImpact(ev.Impact);
                string forecast = "";
                if (_showForecastPrevious && (!string.IsNullOrWhiteSpace(ev.Forecast) || !string.IsNullOrWhiteSpace(ev.Previous)))
                    forecast = $" (F:{ev.Forecast ?? "-"} P:{ev.Previous ?? "-"})";
                string country = string.IsNullOrWhiteSpace(ev.Country) ? "" : $"[{ev.Country.ToUpperInvariant()}] ";
                string text = $"{country}{ev.Title}{forecast}";

                int xText = leftMargin + timeColWidth + shapeColWidth;
                int xRight = panelLeft + panelWidth - 8;
                var font = (_boldCurrent && (ev.IsCurrent || ev.IsSoon)) ? _fontBold : _fontNormal;
                int textY = ComputeTextY(font, rowTop, rowH);
                if (_alignRight)
                {
                    int textWidth = GetTextWidth(text, font);
                    xText = xRight - textWidth;
                }
                var color = ImpactColor(impactN, ev);
                DrawTextWithShadow(context, text, font, color, xText, textY);

                // Separador
                if (_showRowSeparator)
                    context.DrawLine(new RenderPen(_rowSeparatorColor, 1), panelLeft, rowTop + rowH - 1, panelLeft + panelWidth, rowTop + rowH - 1);

                y += rowH;
                rowIndex++;
                if (y > panelTop + 2000) break;
            }
        }

        private int DrawSectionHeader(RenderContext ctx, string title, int y, int panelLeft, int panelWidth)
        {
            int headerH = _baseFontSize + 6;
            int textY = (y) + CenterOffset(_fontBold, headerH);
            ctx.FillRectangle(Color.FromArgb(80, _headerColor), new Rectangle(panelLeft, y, panelWidth, headerH));
            DrawTextWithShadow(ctx, $"--- {title} ---", _fontBold, Color.Orange, panelLeft + 8, textY);
            return y + headerH;
        }

        private void DrawTextWithShadow(RenderContext ctx, string text, RenderFont font, Color color, int x, int y)
        {
            if (_shadowText)
                ctx.DrawString(text, font, Color.FromArgb(120, 0, 0, 0), x + 1, y + 1);
            ctx.DrawString(text, font, color, x, y);
        }

        private void DrawImpactShape(RenderContext ctx, int cx, int cy, NewsEvent ev, int size)
        {
            string impact = NormalizeImpact(ev.Impact);
            var fill = ImpactColor(impact, ev);
            int half = size / 2;
            var rect = new Rectangle(cx - half, cy - half, size, size);

            switch (_impactShapeStyle)
            {
                case ImpactShape.Circle:
                    ctx.FillEllipse(fill, rect);
                    ctx.DrawEllipse(new RenderPen(Darken(fill, 0.4f), 1), rect);
                    break;
                case ImpactShape.Square:
                    ctx.FillRectangle(fill, rect);
                    ctx.DrawRectangle(new RenderPen(Darken(fill, 0.4f), 1), rect);
                    break;
                case ImpactShape.Diamond:
                    var p = new[]
                    {
                        new Point(cx, cy - half),
                        new Point(cx + half, cy),
                        new Point(cx, cy + half),
                        new Point(cx - half, cy)
                    };
                    ctx.FillPolygon(fill, p);
                    ctx.DrawPolygon(new RenderPen(Darken(fill, 0.4f),1), p);
                    break;
                case ImpactShape.Bar:
                    var bar = new Rectangle(cx - (size/4), cy - half, size/2, size);
                    ctx.FillRectangle(fill, bar);
                    break;
            }
        }
        #endregion

        #region Utilidades / Métricas
        private int EstimateWidth()
        {
            int lastBar = CurrentBar - 1;
            if (lastBar < 0) return 600;
            int xLast = ChartInfo.PriceChartContainer.GetXByBar(lastBar, false);
            if (xLast < 220) xLast = 600;
            return xLast + 260;
        }

        private int ComputePanelWidth(int timeColWidth, int shapeColWidth)
        {
            int w = _autoShadeWidth ? (int)(_baseFontSize * _shadeWidthFactor) : _fixedShadeWidthPx;
            w = Math.Max(_minShadeWidthPx, Math.Min(_maxShadeWidthPx, w));

            // No sobrepasar la última vela visible (solo si el panel está a la derecha o centrado superando esa zona)
            int lastBar = CurrentBar - 1;
            if (lastBar >= 0)
            {
                int xLast = ChartInfo.PriceChartContainer.GetXByBar(lastBar, false);
                if (xLast > 0 && _panelAlign != PanelAlign.Left)
                    w = Math.Min(w, Math.Max(120, xLast - _panelRightPadding));
            }

            // Garantizar contenido mínimo
            int minContent = 8 + timeColWidth + shapeColWidth + Math.Max(80, _baseFontSize * 6);
            if (w < minContent) w = minContent;
            return w;
        }

        private int ComputePanelLeft(int panelWidth)
        {
            int lastBar = CurrentBar - 1;
            int xLast = 800; // fallback
            if (lastBar >= 0)
            {
                int tmp = ChartInfo.PriceChartContainer.GetXByBar(lastBar, false);
                if (tmp > 0) xLast = tmp;
            }

            int baseX = 0;
            switch (_panelAlign)
            {
                case PanelAlign.Left:
                    baseX = 0;
                    break;
                case PanelAlign.Center:
                    baseX = Math.Max(0, (xLast - panelWidth) / 2);
                    break;
                case PanelAlign.Right:
                    baseX = Math.Max(0, xLast - panelWidth - _panelRightPadding);
                    break;
            }
            baseX += _panelOffsetX;
            if (baseX < 0) baseX = 0;
            return baseX;
        }

        // Centra verticalmente el texto dentro de una fila (fila: rowTop..rowTop+rowH)
        private int ComputeTextY(RenderFont font, int rowTop, int rowH)
        {
            if (!_centerTextVertically)
                return rowTop + _rowTextOffsetY;

            int offset = (int)Math.Round((rowH - font.Size) / 2f);
            if (offset < 0) offset = 0;
            return rowTop + offset + _rowTextOffsetY;
        }

        // Desplazamiento centrado para cabeceras (contenedor de altura containerH)
        private int CenterOffset(RenderFont font, int containerH)
        {
            if (!_centerTextVertically) return 2;
            int o = (int)Math.Round((containerH - font.Size) / 2f);
            return Math.Max(0, o);
        }

        private int GetTextWidth(string text, RenderFont font)
        {
            var key = (text, font.Size, font.Style.HasFlag(FontStyle.Bold)); // (string,float,bool)

            var now = DateTime.UtcNow;
            if ((now - _lastWidthCacheClear).TotalMinutes > 5)
            {
                _textWidthCache.Clear();
                _lastWidthCacheClear = now;
            }

            if (_textWidthCache.TryGetValue(key, out var w))
                return w;

            try
            {
                var mi = typeof(RenderContext).GetMethod("MeasureString", new[] { typeof(string), typeof(RenderFont) });
                if (mi != null)
                {
                    w = (int)((Size)mi.Invoke(null, new object[] { text, font })).Width;
                }
                else
                {
                    w = (int)(text.Length * (font.Size * (font.Style.HasFlag(FontStyle.Bold) ? 0.68f : 0.6f)));
                }
            }
            catch
            {
                w = (int)(text.Length * (font.Size * (font.Style.HasFlag(FontStyle.Bold) ? 0.68f : 0.6f)));
            }

            _textWidthCache[key] = w;
            return w;
        }

        private void ClearWidthCache() => _textWidthCache.Clear();

        private string NormalizeImpact(string impact)
        {
            if (string.IsNullOrWhiteSpace(impact)) return "";
            impact = impact.Trim().ToLowerInvariant();
            return impact switch
            {
                "high" => "HIGH",
                "medium" => "MEDIUM",
                "low" => "LOW",
                "holiday" or "non-economic" or "none" => "HOLIDAY",
                _ => impact.ToUpperInvariant()
            };
        }

        private Color ImpactColor(string impact, NewsEvent ev)
        {
            var baseColor = impact switch
            {
                "HIGH" => Color.Red,
                "MEDIUM" => Color.FromArgb(255, 140, 0),
                "LOW" => Color.Gold,
                "HOLIDAY" => Color.Silver,
                _ => Color.White
            };
            if (ev.IsCurrent) return Color.FromArgb(255, baseColor);
            if (ev.IsSoon) return Color.FromArgb(220, baseColor);
            return Color.FromArgb(180, baseColor);
        }

        private Color Darken(Color c, float amt)
        {
            amt = Math.Min(1f, Math.Max(0f, amt));
            return Color.FromArgb(c.A,
                (int)(c.R * (1 - amt)),
                (int)(c.G * (1 - amt)),
                (int)(c.B * (1 - amt)));
        }

        private string FormatCounters(Dictionary<string, int> counters)
        {
            if (counters.Count == 0) return "";
            var order = new[] { "HIGH", "MEDIUM", "LOW", "HOLIDAY" };
            var parts = new List<string>();
            foreach (var key in order)
                if (counters.TryGetValue(key, out var v) && v > 0) parts.Add($"{key.Substring(0,1)}:{v}");
            return parts.Count > 0 ? $"[{string.Join(" ", parts)}]" : "";
        }
        #endregion
    }
}