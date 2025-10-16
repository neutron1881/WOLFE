using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace ATAS.Indicators.Technical
{
    [DisplayName("ZonasJC")]
    public class ZonasJC : Indicator
    {
        // Posición del texto
        public enum TextVPos { Arriba, Centro, Abajo }
        public enum TextHPos { Izquierda, Centro, Derecha }

        // Configuración de una zona
        private class ZonaConfig
        {
            public bool Enabled = true;
            public decimal PrecioAlto;
            public decimal PrecioBajo;

            public Color Relleno = Color.Red;
            public int Transparencia = 60; // 0-255
            public Color Borde = Color.Red;
            public int GrosorBorde = 2;

            public bool MostrarTexto = true;
            public string Texto = "";
        }

        // 6 zonas
        private readonly ZonaConfig[] _zonas =
        {
            new ZonaConfig { Relleno = Color.FromArgb(255, 220, 60, 60), Borde = Color.FromArgb(220, 60, 60), Texto = "Zona 1 (Venta)" },
            new ZonaConfig { Relleno = Color.FromArgb(255, 220, 60, 60), Borde = Color.FromArgb(220, 60, 60), Texto = "Zona 2 (Venta)" },
            new ZonaConfig { Relleno = Color.FromArgb(255, 128,128,128), Borde = Color.Gray, Texto = "Zona 3 (Neutra)" },
            new ZonaConfig { Relleno = Color.FromArgb(255, 128,128,128), Borde = Color.Gray, Texto = "Zona 4 (Neutra)" },
            new ZonaConfig { Relleno = Color.FromArgb(255, 60, 180, 75), Borde = Color.FromArgb(60, 160, 70), Texto = "Zona 5 (Compra)" },
            new ZonaConfig { Relleno = Color.FromArgb(255, 60, 180, 75), Borde = Color.FromArgb(60, 160, 70), Texto = "Zona 6 (Compra)" },
        };

        // Opciones globales de texto
        private TextVPos _textoV = TextVPos.Centro;
        private TextHPos _textoH = TextHPos.Centro;
        private Color _colorTexto = Color.White;
        private int _tamanoFuente = 12;
        private int _margenHorizontalPx = 8;

        // Opciones de extensión horizontal
        private bool _soloDesdePrecioDerecha = false;
        private int _barrasExtraDerecha = 50;

        public ZonasJC()
        {
            EnableCustomDrawing = true;
            DenyToChangePanel = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);
        }

        #region Propiedades por zona

        // Zona 1
        [Category("Zona 1")]
        [Display(Name = "Activada", GroupName = "Zona 1", Order = 0)]
        public bool Z1_Enabled { get => _zonas[0].Enabled; set { _zonas[0].Enabled = value; RedrawChart(); } }

        [Category("Zona 1")]
        [Display(Name = "Precio Alto", GroupName = "Zona 1", Order = 1)]
        public decimal Z1_PrecioAlto { get => _zonas[0].PrecioAlto; set { _zonas[0].PrecioAlto = value; RedrawChart(); } }

        [Category("Zona 1")]
        [Display(Name = "Precio Bajo", GroupName = "Zona 1", Order = 2)]
        public decimal Z1_PrecioBajo { get => _zonas[0].PrecioBajo; set { _zonas[0].PrecioBajo = value; RedrawChart(); } }

        [Category("Zona 1")]
        [Display(Name = "Color relleno", GroupName = "Zona 1", Order = 3)]
        public Color Z1_Color { get => _zonas[0].Relleno; set { _zonas[0].Relleno = value; RedrawChart(); } }

        [Category("Zona 1")]
        [Display(Name = "Transparencia", GroupName = "Zona 1", Order = 4)]
        [Range(0,255)]
        public int Z1_Transparencia { get => _zonas[0].Transparencia; set { _zonas[0].Transparencia = Math.Max(0, Math.Min(255, value)); RedrawChart(); } }

        [Category("Zona 1")]
        [Display(Name = "Color borde", GroupName = "Zona 1", Order = 5)]
        public Color Z1_Borde { get => _zonas[0].Borde; set { _zonas[0].Borde = value; RedrawChart(); } }

        [Category("Zona 1")]
        [Display(Name = "Grosor borde", GroupName = "Zona 1", Order = 6)]
        [Range(0, 10)]
        public int Z1_GrosorBorde { get => _zonas[0].GrosorBorde; set { _zonas[0].GrosorBorde = Math.Max(0, value); RedrawChart(); } }

        [Category("Zona 1")]
        [Display(Name = "Mostrar texto", GroupName = "Zona 1", Order = 7)]
        public bool Z1_MostrarTexto { get => _zonas[0].MostrarTexto; set { _zonas[0].MostrarTexto = value; RedrawChart(); } }

        [Category("Zona 1")]
        [Display(Name = "Texto", GroupName = "Zona 1", Order = 8)]
        public string Z1_Texto { get => _zonas[0].Texto; set { _zonas[0].Texto = value; RedrawChart(); } }

        // Zona 2
        [Category("Zona 2")]
        [Display(Name = "Activada", GroupName = "Zona 2", Order = 0)]
        public bool Z2_Enabled { get => _zonas[1].Enabled; set { _zonas[1].Enabled = value; RedrawChart(); } }
        [Category("Zona 2")]
        [Display(Name = "Precio Alto", GroupName = "Zona 2", Order = 1)]
        public decimal Z2_PrecioAlto { get => _zonas[1].PrecioAlto; set { _zonas[1].PrecioAlto = value; RedrawChart(); } }
        [Category("Zona 2")]
        [Display(Name = "Precio Bajo", GroupName = "Zona 2", Order = 2)]
        public decimal Z2_PrecioBajo { get => _zonas[1].PrecioBajo; set { _zonas[1].PrecioBajo = value; RedrawChart(); } }
        [Category("Zona 2")]
        [Display(Name = "Color relleno", GroupName = "Zona 2", Order = 3)]
        public Color Z2_Color { get => _zonas[1].Relleno; set { _zonas[1].Relleno = value; RedrawChart(); } }
        [Category("Zona 2")]
        [Display(Name = "Transparencia", GroupName = "Zona 2", Order = 4)]
        [Range(0,255)]
        public int Z2_Transparencia { get => _zonas[1].Transparencia; set { _zonas[1].Transparencia = Math.Max(0, Math.Min(255, value)); RedrawChart(); } }
        [Category("Zona 2")]
        [Display(Name = "Color borde", GroupName = "Zona 2", Order = 5)]
        public Color Z2_Borde { get => _zonas[1].Borde; set { _zonas[1].Borde = value; RedrawChart(); } }
        [Category("Zona 2")]
        [Display(Name = "Grosor borde", GroupName = "Zona 2", Order = 6)]
        [Range(0,10)]
        public int Z2_GrosorBorde { get => _zonas[1].GrosorBorde; set { _zonas[1].GrosorBorde = Math.Max(0, value); RedrawChart(); } }
        [Category("Zona 2")]
        [Display(Name = "Mostrar texto", GroupName = "Zona 2", Order = 7)]
        public bool Z2_MostrarTexto { get => _zonas[1].MostrarTexto; set { _zonas[1].MostrarTexto = value; RedrawChart(); } }
        [Category("Zona 2")]
        [Display(Name = "Texto", GroupName = "Zona 2", Order = 8)]
        public string Z2_Texto { get => _zonas[1].Texto; set { _zonas[1].Texto = value; RedrawChart(); } }

        // Zona 3
        [Category("Zona 3")]
        [Display(Name = "Activada", GroupName = "Zona 3", Order = 0)]
        public bool Z3_Enabled { get => _zonas[2].Enabled; set { _zonas[2].Enabled = value; RedrawChart(); } }
        [Category("Zona 3")]
        [Display(Name = "Precio Alto", GroupName = "Zona 3", Order = 1)]
        public decimal Z3_PrecioAlto { get => _zonas[2].PrecioAlto; set { _zonas[2].PrecioAlto = value; RedrawChart(); } }
        [Category("Zona 3")]
        [Display(Name = "Precio Bajo", GroupName = "Zona 3", Order = 2)]
        public decimal Z3_PrecioBajo { get => _zonas[2].PrecioBajo; set { _zonas[2].PrecioBajo = value; RedrawChart(); } }
        [Category("Zona 3")]
        [Display(Name = "Color relleno", GroupName = "Zona 3", Order = 3)]
        public Color Z3_Color { get => _zonas[2].Relleno; set { _zonas[2].Relleno = value; RedrawChart(); } }
        [Category("Zona 3")]
        [Display(Name = "Transparencia", GroupName = "Zona 3", Order = 4)]
        [Range(0,255)]
        public int Z3_Transparencia { get => _zonas[2].Transparencia; set { _zonas[2].Transparencia = Math.Max(0, Math.Min(255, value)); RedrawChart(); } }
        [Category("Zona 3")]
        [Display(Name = "Color borde", GroupName = "Zona 3", Order = 5)]
        public Color Z3_Borde { get => _zonas[2].Borde; set { _zonas[2].Borde = value; RedrawChart(); } }
        [Category("Zona 3")]
        [Display(Name = "Grosor borde", GroupName = "Zona 3", Order = 6)]
        [Range(0,10)]
        public int Z3_GrosorBorde { get => _zonas[2].GrosorBorde; set { _zonas[2].GrosorBorde = Math.Max(0, value); RedrawChart(); } }
        [Category("Zona 3")]
        [Display(Name = "Mostrar texto", GroupName = "Zona 3", Order = 7)]
        public bool Z3_MostrarTexto { get => _zonas[2].MostrarTexto; set { _zonas[2].MostrarTexto = value; RedrawChart(); } }
        [Category("Zona 3")]
        [Display(Name = "Texto", GroupName = "Zona 3", Order = 8)]
        public string Z3_Texto { get => _zonas[2].Texto; set { _zonas[2].Texto = value; RedrawChart(); } }

        // Zona 4
        [Category("Zona 4")]
        [Display(Name = "Activada", GroupName = "Zona 4", Order = 0)]
        public bool Z4_Enabled { get => _zonas[3].Enabled; set { _zonas[3].Enabled = value; RedrawChart(); } }
        [Category("Zona 4")]
        [Display(Name = "Precio Alto", GroupName = "Zona 4", Order = 1)]
        public decimal Z4_PrecioAlto { get => _zonas[3].PrecioAlto; set { _zonas[3].PrecioAlto = value; RedrawChart(); } }
        [Category("Zona 4")]
        [Display(Name = "Precio Bajo", GroupName = "Zona 4", Order = 2)]
        public decimal Z4_PrecioBajo { get => _zonas[3].PrecioBajo; set { _zonas[3].PrecioBajo = value; RedrawChart(); } }
        [Category("Zona 4")]
        [Display(Name = "Color relleno", GroupName = "Zona 4", Order = 3)]
        public Color Z4_Color { get => _zonas[3].Relleno; set { _zonas[3].Relleno = value; RedrawChart(); } }
        [Category("Zona 4")]
        [Display(Name = "Transparencia", GroupName = "Zona 4", Order = 4)]
        [Range(0,255)]
        public int Z4_Transparencia { get => _zonas[3].Transparencia; set { _zonas[3].Transparencia = Math.Max(0, Math.Min(255, value)); RedrawChart(); } }
        [Category("Zona 4")]
        [Display(Name = "Color borde", GroupName = "Zona 4", Order = 5)]
        public Color Z4_Borde { get => _zonas[3].Borde; set { _zonas[3].Borde = value; RedrawChart(); } }
        [Category("Zona 4")]
        [Display(Name = "Grosor borde", GroupName = "Zona 4", Order = 6)]
        [Range(0,10)]
        public int Z4_GrosorBorde { get => _zonas[3].GrosorBorde; set { _zonas[3].GrosorBorde = Math.Max(0, value); RedrawChart(); } }
        [Category("Zona 4")]
        [Display(Name = "Mostrar texto", GroupName = "Zona 4", Order = 7)]
        public bool Z4_MostrarTexto { get => _zonas[3].MostrarTexto; set { _zonas[3].MostrarTexto = value; RedrawChart(); } }
        [Category("Zona 4")]
        [Display(Name = "Texto", GroupName = "Zona 4", Order = 8)]
        public string Z4_Texto { get => _zonas[3].Texto; set { _zonas[3].Texto = value; RedrawChart(); } }

        // Zona 5
        [Category("Zona 5")]
        [Display(Name = "Activada", GroupName = "Zona 5", Order = 0)]
        public bool Z5_Enabled { get => _zonas[4].Enabled; set { _zonas[4].Enabled = value; RedrawChart(); } }
        [Category("Zona 5")]
        [Display(Name = "Precio Alto", GroupName = "Zona 5", Order = 1)]
        public decimal Z5_PrecioAlto { get => _zonas[4].PrecioAlto; set { _zonas[4].PrecioAlto = value; RedrawChart(); } }
        [Category("Zona 5")]
        [Display(Name = "Precio Bajo", GroupName = "Zona 5", Order = 2)]
        public decimal Z5_PrecioBajo { get => _zonas[4].PrecioBajo; set { _zonas[4].PrecioBajo = value; RedrawChart(); } }
        [Category("Zona 5")]
        [Display(Name = "Color relleno", GroupName = "Zona 5", Order = 3)]
        public Color Z5_Color { get => _zonas[4].Relleno; set { _zonas[4].Relleno = value; RedrawChart(); } }
        [Category("Zona 5")]
        [Display(Name = "Transparencia", GroupName = "Zona 5", Order = 4)]
        [Range(0,255)]
        public int Z5_Transparencia { get => _zonas[4].Transparencia; set { _zonas[4].Transparencia = Math.Max(0, Math.Min(255, value)); RedrawChart(); } }
        [Category("Zona 5")]
        [Display(Name = "Color borde", GroupName = "Zona 5", Order = 5)]
        public Color Z5_Borde { get => _zonas[4].Borde; set { _zonas[4].Borde = value; RedrawChart(); } }
        [Category("Zona 5")]
        [Display(Name = "Grosor borde", GroupName = "Zona 5", Order = 6)]
        [Range(0,10)]
        public int Z5_GrosorBorde { get => _zonas[4].GrosorBorde; set { _zonas[4].GrosorBorde = Math.Max(0, value); RedrawChart(); } }
        [Category("Zona 5")]
        [Display(Name = "Mostrar texto", GroupName = "Zona 5", Order = 7)]
        public bool Z5_MostrarTexto { get => _zonas[4].MostrarTexto; set { _zonas[4].MostrarTexto = value; RedrawChart(); } }
        [Category("Zona 5")]
        [Display(Name = "Texto", GroupName = "Zona 5", Order = 8)]
        public string Z5_Texto { get => _zonas[4].Texto; set { _zonas[4].Texto = value; RedrawChart(); } }

        // Zona 6
        [Category("Zona 6")]
        [Display(Name = "Activada", GroupName = "Zona 6", Order = 0)]
        public bool Z6_Enabled { get => _zonas[5].Enabled; set { _zonas[5].Enabled = value; RedrawChart(); } }
        [Category("Zona 6")]
        [Display(Name = "Precio Alto", GroupName = "Zona 6", Order = 1)]
        public decimal Z6_PrecioAlto { get => _zonas[5].PrecioAlto; set { _zonas[5].PrecioAlto = value; RedrawChart(); } }
        [Category("Zona 6")]
        [Display(Name = "Precio Bajo", GroupName = "Zona 6", Order = 2)]
        public decimal Z6_PrecioBajo { get => _zonas[5].PrecioBajo; set { _zonas[5].PrecioBajo = value; RedrawChart(); } }
        [Category("Zona 6")]
        [Display(Name = "Color relleno", GroupName = "Zona 6", Order = 3)]
        public Color Z6_Color { get => _zonas[5].Relleno; set { _zonas[5].Relleno = value; RedrawChart(); } }
        [Category("Zona 6")]
        [Display(Name = "Transparencia", GroupName = "Zona 6", Order = 4)]
        [Range(0,255)]
        public int Z6_Transparencia { get => _zonas[5].Transparencia; set { _zonas[5].Transparencia = Math.Max(0, Math.Min(255, value)); RedrawChart(); } }
        [Category("Zona 6")]
        [Display(Name = "Color borde", GroupName = "Zona 6", Order = 5)]
        public Color Z6_Borde { get => _zonas[5].Borde; set { _zonas[5].Borde = value; RedrawChart(); } }
        [Category("Zona 6")]
        [Display(Name = "Grosor borde", GroupName = "Zona 6", Order = 6)]
        [Range(0,10)]
        public int Z6_GrosorBorde { get => _zonas[5].GrosorBorde; set { _zonas[5].GrosorBorde = Math.Max(0, value); RedrawChart(); } }
        [Category("Zona 6")]
        [Display(Name = "Mostrar texto", GroupName = "Zona 6", Order = 7)]
        public bool Z6_MostrarTexto { get => _zonas[5].MostrarTexto; set { _zonas[5].MostrarTexto = value; RedrawChart(); } }
        [Category("Zona 6")]
        [Display(Name = "Texto", GroupName = "Zona 6", Order = 8)]
        public string Z6_Texto { get => _zonas[5].Texto; set { _zonas[5].Texto = value; RedrawChart(); } }

        #endregion

        #region Propiedades globales

        [Category("Texto")]
        [Display(Name = "Color", GroupName = "Texto", Order = 1)]
        public Color ColorTexto
        {
            get => _colorTexto;
            set { _colorTexto = value; RedrawChart(); }
        }

        [Category("Texto")]
        [Display(Name = "Tamaño", GroupName = "Texto", Order = 2)]
        [Range(6, 60)]
        public int TamanoFuente
        {
            get => _tamanoFuente;
            set { _tamanoFuente = Math.Max(6, value); RedrawChart(); }
        }

        [Category("Texto")]
        [Display(Name = "Vertical", GroupName = "Texto", Order = 3)]
        public TextVPos PosicionVertical
        {
            get => _textoV;
            set { _textoV = value; RedrawChart(); }
        }

        [Category("Texto")]
        [Display(Name = "Horizontal", GroupName = "Texto", Order = 4)]
        public TextHPos PosicionHorizontal
        {
            get => _textoH;
            set { _textoH = value; RedrawChart(); }
        }

        [Category("Texto")]
        [Display(Name = "Margen horizontal (px)", GroupName = "Texto", Order = 5)]
        [Range(0, 300)]
        public int MargenHorizontalPx
        {
            get => _margenHorizontalPx;
            set { _margenHorizontalPx = Math.Max(0, value); RedrawChart(); }
        }

        [Category("Opciones")]
        [Display(Name = "Sólo desde precio → derecha", GroupName = "Opciones", Order = 10)]
        public bool SoloDesdePrecioDerecha
        {
            get => _soloDesdePrecioDerecha;
            set { _soloDesdePrecioDerecha = value; RedrawChart(); }
        }

        [Category("Opciones")]
        [Display(Name = "Barras extra a la derecha", GroupName = "Opciones", Order = 11)]
        [Range(0, 1000)]
        public int BarrasExtraDerecha
        {
            get => _barrasExtraDerecha;
            set { _barrasExtraDerecha = Math.Max(0, value); RedrawChart(); }
        }

        #endregion

        protected override void OnCalculate(int bar, decimal value)
        {
            // Zonas puramente gráficas.
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo?.PriceChartContainer == null)
                return;

            // X a la derecha del chart (extiende unas barras "virtuales" para cubrir margen derecho)
            int xRightBars = ChartInfo.PriceChartContainer.GetXByBar(CurrentBar + _barrasExtraDerecha, false);
            int xRightNow = ChartInfo.PriceChartContainer.GetXByBar(Math.Max(0, CurrentBar - 1), false);
            if (xRightBars <= 0 && CurrentBar > 0)
                xRightBars = ChartInfo.PriceChartContainer.GetXByBar(CurrentBar, false);
            if (xRightBars <= xRightNow)
                xRightBars = xRightNow + 100; // fallback visual

            int xStartAll = _soloDesdePrecioDerecha ? Math.Max(0, xRightNow) : 0;
            int xEndAll = Math.Max(xStartAll + 1, xRightBars);

            for (int i = 0; i < _zonas.Length; i++)
            {
                var z = _zonas[i];
                if (!z.Enabled)
                    continue;

                if (z.PrecioAlto == 0m && z.PrecioBajo == 0m)
                    continue;

                decimal high = Math.Max(z.PrecioAlto, z.PrecioBajo);
                decimal low = Math.Min(z.PrecioAlto, z.PrecioBajo);

                int yHigh = ChartInfo.PriceChartContainer.GetYByPrice(high, false);
                int yLow = ChartInfo.PriceChartContainer.GetYByPrice(low, false);

                var rect = new Rectangle(
                    Math.Min(xStartAll, xEndAll),
                    Math.Min(yHigh, yLow),
                    Math.Max(1, Math.Abs(xEndAll - xStartAll)),
                    Math.Max(1, Math.Abs(yHigh - yLow))
                );

                // Relleno y borde
                var fill = Color.FromArgb(Math.Max(0, Math.Min(255, z.Transparencia)), z.Relleno);
                context.FillRectangle(fill, rect);

                if (z.GrosorBorde > 0)
                    context.DrawRectangle(new RenderPen(z.Borde, z.GrosorBorde), rect);

                // Texto
                if (z.MostrarTexto && !string.IsNullOrWhiteSpace(z.Texto))
                {
                    var font = new RenderFont("Arial", TamanoFuente);

                    // X del texto
                    int textX;
                    switch (_textoH)
                    {
                        case TextHPos.Izquierda:
                            textX = rect.Left + MargenHorizontalPx;
                            break;
                        case TextHPos.Derecha:
                            textX = rect.Right - MargenHorizontalPx - 80; // reserva aproximada
                            break;
                        case TextHPos.Centro:
                        default:
                            textX = rect.Left + (rect.Width / 2);
                            break;
                    }

                    // Y del texto
                    int textY;
                    switch (_textoV)
                    {
                        case TextVPos.Arriba:
                            textY = rect.Top - (TamanoFuente + 2);
                            break;
                        case TextVPos.Abajo:
                            textY = rect.Bottom + 2;
                            break;
                        case TextVPos.Centro:
                        default:
                            textY = rect.Top + (rect.Height / 2) - (TamanoFuente / 2);
                            break;
                    }

                    context.DrawString(z.Texto, font, ColorTexto, textX, textY);
                }
            }
        }
    }
}