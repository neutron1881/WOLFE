using ATAS.Indicators;
using ATAS.Indicators.Drawing;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace ATAS.Indicators
{
    [DisplayName("VelaMasterZone")]
    public class VelaMasterZone : Indicator
    {
        #region Configurable Parameters
        // 1. Configuración Vela Master
        [DisplayName("Volumen mínimo")]
        [Category("1. Configuración Vela Master")]
        [Description("Volumen mínimo que debe tener la vela para ser considerada master")]
        public decimal VolumenMinimo
        {
            get => _volumenMinimo;
            set { _volumenMinimo = value; RecalculateValues(); }
        }

        [DisplayName("Rango mínimo")]
        [Category("1. Configuración Vela Master")]
        [Description("Rango mínimo que debe tener una vela para ser considerada master")]
        public decimal MinRangoMaster
        {
            get => _minRangoMaster;
            set { _minRangoMaster = value; RecalculateValues(); }
        }

        [DisplayName("Rango máximo")]
        [Category("1. Configuración Vela Master")]
        [Description("Rango máximo que puede tener una vela para ser considerada master")]
        public decimal MaxRangoMaster
        {
            get => _maxRangoMaster;
            set { _maxRangoMaster = value; RecalculateValues(); }
        }

        [DisplayName("Velas consecutivas mínimas")]
        [Category("1. Configuración Vela Master")]
        [Description("Cantidad mínima de velas que deben estar dentro del rango de la vela master")]
        public int NumVelas
        {
            get => _numVelas;
            set { _numVelas = value; RecalculateValues(); }
        }

        // 2. Configuración Vela Master Reversal
        [DisplayName("Activar Vela Master Reversal")]
        [Category("2. Configuración Vela Master Reversal")]
        [Description("Detecta zonas donde las velas previas no superan el rango de la vela maestra")]
        public bool MostrarVelaMaestraReversal { get; set; }

        [DisplayName("Rango mínimo")]
        [Category("2. Configuración Vela Master Reversal")]
        public decimal MinRangoMasterReversal
        {
            get => _minRangoMasterReversal;
            set { _minRangoMasterReversal = value; RecalculateValues(); }
        }

        [DisplayName("Rango máximo")]
        [Category("2. Configuración Vela Master Reversal")]
        public decimal MaxRangoMasterReversal
        {
            get => _maxRangoMasterReversal;
            set { _maxRangoMasterReversal = value; RecalculateValues(); }
        }

        [DisplayName("Volumen mínimo")]
        [Category("2. Configuración Vela Master Reversal")]
        public decimal VolumenMinimoReversal
        {
            get => _volumenMinimoReversal;
            set { _volumenMinimoReversal = value; RecalculateValues(); }
        }

        [DisplayName("Velas previas")]
        [Category("2. Configuración Vela Master Reversal")]
        [Range(1, 50)]
        public int NumVelasPrevias { get; set; }

        // 3. Configuración Breakout
        [DisplayName("Puntos mínimos para breakout")]
        [Category("3. Configuración Breakout")]
        [Range(1, 50)]
        public decimal PuntosBreakout
        {
            get => _puntosBreakout;
            set { _puntosBreakout = value; RecalculateValues(); }
        }

        // 4. Apariencia Zona Normal
        [DisplayName("Color zona")]
        [Category("4. Apariencia Zona Normal")]
        public Color ColorZona
        {
            get => _colorZona;
            set { _colorZona = value; RedrawValues(); }
        }

        [DisplayName("Transparencia zona")]
        [Category("4. Apariencia Zona Normal")]
        [Range(0, 255)]
        public int TransparenciaZona
        {
            get => _transparenciaZona;
            set { _transparenciaZona = value; RedrawValues(); }
        }

        [DisplayName("Estilo línea zona")]
        [Category("4. Apariencia Zona Normal")]
        public DashStyle EstiloZona
        {
            get => _estiloZona;
            set { _estiloZona = value; RedrawValues(); }
        }

        // 5. Apariencia Zona Reversal
        [DisplayName("Color zona reversal")]
        [Category("5. Apariencia Zona Reversal")]
        public Color ColorZonaReversal
        {
            get => _colorZonaReversal;
            set { _colorZonaReversal = value; RedrawValues(); }
        }

        [DisplayName("Transparencia zona reversal")]
        [Category("5. Apariencia Zona Reversal")]
        [Range(0, 255)]
        public int TransparenciaZonaReversal
        {
            get => _transparenciaZonaReversal;
            set { _transparenciaZonaReversal = value; RedrawValues(); }
        }

        [DisplayName("Estilo línea zona reversal")]
        [Category("5. Apariencia Zona Reversal")]
        public DashStyle EstiloZonaReversal
        {
            get => _estiloZonaReversal;
            set { _estiloZonaReversal = value; RedrawValues(); }
        }

        // 6. Apariencia Marcas
        [DisplayName("Color marca breakout alcista")]
        [Category("6. Apariencia Marcas")]
        public Color ColorMarcaAlcista
        {
            get => _colorMarcaAlcista;
            set { _colorMarcaAlcista = value; RedrawValues(); }
        }

        [DisplayName("Color marca breakout bajista")]
        [Category("6. Apariencia Marcas")]
        public Color ColorMarcaBajista
        {
            get => _colorMarcaBajista;
            set { _colorMarcaBajista = value; RedrawValues(); }
        }

        [DisplayName("Tamaño de las marcas")]
        [Category("6. Apariencia Marcas")]
        [Range(4, 32)]
        public int TamanoMarcaRuptura
        {
            get => _tamanoMarcaRuptura;
            set { _tamanoMarcaRuptura = value; RedrawValues(); }
        }

        [DisplayName("Distancia de las marcas")]
        [Category("6. Apariencia Marcas")]
        [Range(1, 20)]
        public decimal OffsetMarcaPuntos
        {
            get => _offsetMarcaPuntos;
            set { _offsetMarcaPuntos = value; RecalculateValues(); }
        }

        // 7. Proyecciones
        [DisplayName("Activar proyecciones")]
        [Category("7. Proyecciones")]
        public bool ActivarProyecciones
        {
            get => _activarProyecciones;
            set { _activarProyecciones = value; RedrawValues(); }
        }

        [DisplayName("Porcentajes (coma)")]
        [Category("7. Proyecciones")]
        [Description("Lista de porcentajes extra (ej: 150,200,300) sobre el rango (High-Low) de la vela master")]
        public string PorcentajesProyeccion
        {
            get => _porcentajesProyeccion;
            set
            {
                _porcentajesProyeccion = value;
                ParsePorcentajes();
                RedrawValues();
            }
        }

        [DisplayName("Color proyección superior")]
        [Category("7. Proyecciones")]
        public Color ColorProyeccionSuperior
        {
            get => _colorProySup;
            set { _colorProySup = value; RedrawValues(); }
        }

        [DisplayName("Color proyección inferior")]
        [Category("7. Proyecciones")]
        public Color ColorProyeccionInferior
        {
            get => _colorProyInf;
            set { _colorProyInf = value; RedrawValues(); }
        }

        [DisplayName("Grosor líneas proyección")]
        [Category("7. Proyecciones")]
        [Range(1, 8)]
        public int GrosorProyeccion
        {
            get => _grosorProy;
            set { _grosorProy = Math.Clamp(value, 1, 8); RedrawValues(); }
        }

        [DisplayName("Estilo líneas proyección")]
        [Category("7. Proyecciones")]
        public DashStyle EstiloProyeccion
        {
            get => _estiloProy;
            set { _estiloProy = value; RedrawValues(); }
        }

        [DisplayName("Ocultar proyecciones tocadas")]
        [Category("7. Proyecciones")]
        public bool OcultarProyeccionesTocadas
        {
            get => _ocultarProyTocadas;
            set { _ocultarProyTocadas = value; RedrawValues(); }
        }

        [DisplayName("Mostrar etiqueta porcentaje")]
        [Category("7. Proyecciones")]
        public bool MostrarEtiquetaProyeccion
        {
            get => _mostrarEtiquetaProy;
            set { _mostrarEtiquetaProy = value; RedrawValues(); }
        }
        #endregion

        #region Private Fields
        private Color _colorZonaReversal = Color.Purple;
        private int _transparenciaZonaReversal = 40;
        private DashStyle _estiloZonaReversal = DashStyle.Dot;

        private int _numVelas = 5;
        private decimal _minRangoMaster = 5;
        private decimal _maxRangoMaster = 50;
        private Color _colorZona = Color.Blue;
        private int _transparenciaZona = 50;
        private DashStyle _estiloZona = DashStyle.Solid;
        private Color _colorMarcaAlcista = Color.LimeGreen;
        private Color _colorMarcaBajista = Color.OrangeRed;
        private int _tamanoMarcaRuptura = 8;
        private decimal _offsetMarcaPuntos = 5;
        private decimal _puntosBreakout = 3;
        private int _numVelasPrevias = 3;
        private decimal _volumenMinimo = 1000;
        private decimal _volumenMinimoReversal = 1000;
        private decimal _minRangoMasterReversal = 5;
        private decimal _maxRangoMasterReversal = 50;

        // Proyecciones
        private bool _activarProyecciones = true;
        private string _porcentajesProyeccion = "150,200";
        private readonly List<int> _listPorcentajes = new();
        private Color _colorProySup = Color.Gold;
        private Color _colorProyInf = Color.DeepSkyBlue;
        private int _grosorProy = 2;
        private DashStyle _estiloProy = DashStyle.Dash;
        private bool _ocultarProyTocadas = true;
        private bool _mostrarEtiquetaProy = true;
        #endregion

        #region Internal Structures
        private struct ZonaMaster
        {
            public int BarStart;
            public int BarEnd;
            public decimal High;
            public decimal Low;
            public bool EsReversal;
            public int BarStartPrevio;
            public decimal HighPrevio;
            public decimal LowPrevio;
        }

        private struct MarcaRuptura
        {
            public int Bar;
            public bool EsRupturaArriba;
            public decimal Precio;
        }

        private struct Proyeccion
        {
            public int BarStart;
            public decimal Precio;
            public bool EsSuperior;
            public int Porcentaje;
            public bool Tocado;
        }
        #endregion

        #region Internal Variables
        private List<ZonaMaster> zonas = new();
        private List<MarcaRuptura> rupturas = new();
        private List<Proyeccion> proyecciones = new();

        private int? masterStartBar = null;
        private decimal masterHigh = 0;
        private decimal masterLow = 0;
        private int consecutivas = 0;
        private bool zonaActiva = false;
        private bool zonaEsReversal = false;

        private int masterBarStartPrevio;
        private decimal masterHighPrevio;
        private decimal masterLowPrevio;
        #endregion

        #region Lifecycle
        protected override void OnInitialize()
        {
            EnableCustomDrawing = true;
            zonas.Clear();
            rupturas.Clear();
            proyecciones.Clear();
            masterStartBar = null;
            zonaActiva = false;
            consecutivas = 0;
            ParsePorcentajes();
        }
        #endregion

        #region Calculation
        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar >= CurrentBar)
                return;

            ActualizarProyeccionesTocadas(bar);

            if (bar < Math.Max(NumVelas, NumVelasPrevias))
                return;

            var candle = GetCandle(bar);
            if (candle == null)
                return;

            decimal high = candle.High;
            decimal low = candle.Low;
            decimal close = candle.Close;
            decimal rango = high - low;

            // Inicio de Vela Master
            if (!zonaActiva && rango >= MinRangoMaster && rango <= MaxRangoMaster)
            {
                var volumen = candle.Volume;
                if (volumen < VolumenMinimo)
                    return;

                bool esReversal = false;
                decimal highPrevio = high;
                decimal lowPrevio = low;
                int barStartPrevio = bar;

                if (MostrarVelaMaestraReversal)
                {
                    if (volumen >= VolumenMinimoReversal &&
                        rango >= MinRangoMasterReversal &&
                        rango <= MaxRangoMasterReversal)
                    {
                        esReversal = true;
                        highPrevio = decimal.MinValue;
                        lowPrevio = decimal.MaxValue;

                        for (int i = 1; i <= NumVelasPrevias; i++)
                        {
                            var previa = GetCandle(bar - i);
                            if (previa == null || previa.High > high || previa.Low < low)
                            {
                                esReversal = false;
                                break;
                            }

                            if (esReversal)
                            {
                                highPrevio = Math.Max(highPrevio, previa.High);
                                lowPrevio = Math.Min(lowPrevio, previa.Low);
                                barStartPrevio = bar - i;
                            }
                        }
                    }
                }

                if (!esReversal || MostrarVelaMaestraReversal)
                {
                    masterStartBar = bar;
                    masterHigh = high;
                    masterLow = low;
                    consecutivas = 0;
                    zonaActiva = true;
                    zonaEsReversal = esReversal;

                    if (esReversal)
                    {
                        masterBarStartPrevio = barStartPrevio;
                        masterHighPrevio = highPrevio;
                        masterLowPrevio = lowPrevio;
                    }
                }
            }

            // Gestión zona activa
            if (zonaActiva && masterStartBar.HasValue)
            {
                bool breakoutArriba = close > (masterHigh + (zonaEsReversal ? 0 : PuntosBreakout));
                bool breakoutAbajo = close < (masterLow - (zonaEsReversal ? 0 : PuntosBreakout));
                bool hayBreakout = breakoutArriba || breakoutAbajo;

                if (!hayBreakout)
                {
                    if (high <= masterHigh && low >= masterLow)
                        consecutivas++;
                }
                else
                {
                    if (consecutivas >= NumVelas || zonaEsReversal)
                    {
                        // Registrar zona
                        zonas.Add(new ZonaMaster
                        {
                            BarStart = masterStartBar.Value,
                            BarEnd = bar,
                            High = masterHigh,
                            Low = masterLow,
                            EsReversal = zonaEsReversal,
                            BarStartPrevio = zonaEsReversal ? masterBarStartPrevio : masterStartBar.Value,
                            HighPrevio = zonaEsReversal ? masterHighPrevio : masterHigh,
                            LowPrevio = zonaEsReversal ? masterLowPrevio : masterLow
                        });

                        // Marca breakout
                        if (breakoutArriba)
                        {
                            rupturas.Add(new MarcaRuptura
                            {
                                Bar = bar,
                                EsRupturaArriba = true,
                                Precio = high + OffsetMarcaPuntos
                            });
                        }
                        else
                        {
                            rupturas.Add(new MarcaRuptura
                            {
                                Bar = bar,
                                EsRupturaArriba = false,
                                Precio = low - OffsetMarcaPuntos
                            });
                        }

                        // Proyecciones
                        if (ActivarProyecciones && _listPorcentajes.Count > 0)
                            CrearProyecciones(masterStartBar.Value, masterHigh, masterLow);
                    }

                    zonaActiva = false;
                    masterStartBar = null;
                    consecutivas = 0;
                }
            }
        }
        #endregion

        #region Rendering
        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            // Zonas
            foreach (var zona in zonas)
            {
                int xStart = ChartInfo.PriceChartContainer.GetXByBar(zona.BarStart, false);
                int xEnd = ChartInfo.PriceChartContainer.GetXByBar(zona.BarEnd, false);
                int yHigh = ChartInfo.PriceChartContainer.GetYByPrice(zona.High, false);
                int yLow = ChartInfo.PriceChartContainer.GetYByPrice(zona.Low, false);

                var rect = new Rectangle(
                    Math.Min(xStart, xEnd),
                    Math.Min(yHigh, yLow),
                    Math.Max(1, Math.Abs(xEnd - xStart)),
                    Math.Max(1, Math.Abs(yHigh - yLow))
                );

                var colorZona = zona.EsReversal
                    ? Color.FromArgb(TransparenciaZonaReversal, ColorZonaReversal)
                    : Color.FromArgb(TransparenciaZona, ColorZona);
                var estiloZona = zona.EsReversal ? EstiloZonaReversal : EstiloZona;
                var grosorLinea = zona.EsReversal ? 1 : 2;

                context.FillRectangle(colorZona, rect);
                context.DrawRectangle(new RenderPen(zona.EsReversal ? ColorZonaReversal : ColorZona, grosorLinea)
                {
                    DashStyle = estiloZona
                }, rect);

                if (zona.EsReversal)
                {
                    int xStartPrevio = ChartInfo.PriceChartContainer.GetXByBar(zona.BarStartPrevio, false);
                    int yHighPrevio = ChartInfo.PriceChartContainer.GetYByPrice(zona.High, false);
                    int yLowPrevio = ChartInfo.PriceChartContainer.GetYByPrice(zona.Low, false);

                    var rectPrevio = new Rectangle(
                        Math.Min(xStartPrevio, xStart),
                        Math.Min(yHighPrevio, yLowPrevio),
                        Math.Max(1, Math.Abs(xStart - xStartPrevio)),
                        Math.Max(1, Math.Abs(yHighPrevio - yLowPrevio))
                    );

                    var colorPrevio = Color.FromArgb(TransparenciaZonaReversal / 2, ColorZonaReversal);
                    context.FillRectangle(colorPrevio, rectPrevio);
                    context.DrawRectangle(new RenderPen(ColorZonaReversal, 1) { DashStyle = DashStyle.Dash }, rectPrevio);
                }
            }

            // Breakouts
            foreach (var marca in rupturas)
            {
                int x = ChartInfo.PriceChartContainer.GetXByBar(marca.Bar, false);
                int y = ChartInfo.PriceChartContainer.GetYByPrice(marca.Precio, false);

                var r = new Rectangle(x - TamanoMarcaRuptura / 2, y - TamanoMarcaRuptura / 2, TamanoMarcaRuptura, TamanoMarcaRuptura);
                var color = marca.EsRupturaArriba ? ColorMarcaAlcista : ColorMarcaBajista;
                context.FillEllipse(color, r);
                context.DrawEllipse(new RenderPen(Color.Black, 1), r);
            }

            // Proyecciones
            if (ActivarProyecciones)
                RenderProyecciones(context);
        }
        #endregion

        #region Proyecciones
        private void ParsePorcentajes()
        {
            _listPorcentajes.Clear();

            if (string.IsNullOrWhiteSpace(_porcentajesProyeccion))
                return;

            var parts = _porcentajesProyeccion.Split(',', ';', ' ', '|', '\t');
            foreach (var p in parts)
            {
                if (int.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var val) && val > 0)
                    _listPorcentajes.Add(val);
            }

            if (_listPorcentajes.Count == 0)
                return;

            // Normalizar: ordenar y eliminar duplicados sin reasignar la lista readonly
            _listPorcentajes.Sort();
            int last = -1;
            var tmp = new List<int>(_listPorcentajes.Count);
            foreach (var v in _listPorcentajes)
            {
                if (v != last)
                {
                    tmp.Add(v);
                    last = v;
                }
            }

            _listPorcentajes.Clear();
            _listPorcentajes.AddRange(tmp);
        }

        private void CrearProyecciones(int barStart, decimal high, decimal low)
        {
            var rango = high - low;
            if (rango <= 0)
                return;

            foreach (var pct in _listPorcentajes)
            {
                var factor = pct / 100m;
                var precioSup = high + rango * factor;
                var precioInf = low - rango * factor;

                proyecciones.Add(new Proyeccion
                {
                    BarStart = barStart,
                    Precio = precioSup,
                    EsSuperior = true,
                    Porcentaje = pct,
                    Tocado = false
                });

                proyecciones.Add(new Proyeccion 
                {
                    BarStart = barStart,
                    Precio = precioInf,
                    EsSuperior = false,
                    Porcentaje = pct,
                    Tocado = false
                });
            }
        }

        private void ActualizarProyeccionesTocadas(int bar)
        {
            if (!ActivarProyecciones || proyecciones.Count == 0)
                return;

            var c = GetCandle(bar);
            if (c == null)
                return;

            for (int i = 0; i < proyecciones.Count; i++)
            {
                if (proyecciones[i].Tocado)
                    continue;

                var pr = proyecciones[i];
                if ((pr.EsSuperior && c.High >= pr.Precio) ||
                    (!pr.EsSuperior && c.Low <= pr.Precio))
                {
                    pr.Tocado = true;
                    proyecciones[i] = pr;
                }
            }
        }

        private void RenderProyecciones(RenderContext context)
        {
            if (proyecciones.Count == 0)
                return;

            int xEnd = ChartInfo.PriceChartContainer.GetXByBar(CurrentBar - 1, false);

            foreach (var pr in proyecciones)
            {
                if (OcultarProyeccionesTocadas && pr.Tocado)
                    continue;

                int xStart = ChartInfo.PriceChartContainer.GetXByBar(pr.BarStart, false);
                int y = ChartInfo.PriceChartContainer.GetYByPrice(pr.Precio, false);

                var color = pr.EsSuperior ? _colorProySup : _colorProyInf;
                if (pr.Tocado)
                    color = Color.FromArgb(90, color);

                context.DrawLine(new RenderPen(color, pr.Tocado ? Math.Max(1, _grosorProy - 1) : _grosorProy)
                {
                    DashStyle = _estiloProy
                }, xStart, y, xEnd, y);

                if (MostrarEtiquetaProyeccion)
                {
                    var font = new RenderFont("Arial", 9);
                    string txt = (pr.EsSuperior ? "+" : "-") + pr.Porcentaje + "%";
                    context.DrawString(txt, font, color, xEnd - 30, y - 8);
                }
            }
        }
        #endregion

        #region Helpers / Recalc
        private void RecalculateValues()
        {
            zonas.Clear();
            rupturas.Clear();
            proyecciones.Clear();
            masterStartBar = null;
            zonaActiva = false;
            consecutivas = 0;

            for (int bar = 0; bar < CurrentBar; bar++)
                OnCalculate(bar, GetCandle(bar)?.Close ?? 0);

            RedrawChart();
        }

        private void RedrawValues() => RedrawChart();
        #endregion
    }
}