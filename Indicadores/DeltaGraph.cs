using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using ATAS.Indicators;
using ATAS.Indicators.Drawing;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace ATAS.Indicators.Technical
{
    [DisplayName("Delta Right Bar")]
    public class DeltaRightBar : Indicator
    {
        // Parámetros configurables
        private Color _colorPositivo = Color.LimeGreen;
        private Color _colorNegativo = Color.OrangeRed;
        private Color _colorTexto = Color.White;

        private bool _autoEscala = true;
        private int _lookback = 50;
        private decimal _escalaFijaAbsDelta = 5000m;

        private int _maxAltoBarraPx = 60;
        private int _anchoBarraPx = 12;
        private int _margenSuperiorPx = 10;
        private int _tamanoFuente = 12;
        private bool _textoColorSegunDelta = true;

        private bool _anclarDerecha = true;
        private int _offsetDerechaPx = 8;

        [Category("Colores")]
        [Display(Name = "Color Delta +", GroupName = "Colores", Order = 10)]
        public Color ColorPositivo { get => _colorPositivo; set { _colorPositivo = value; RedrawChart(); } }

        [Category("Colores")]
        [Display(Name = "Color Delta -", GroupName = "Colores", Order = 11)]
        public Color ColorNegativo { get => _colorNegativo; set { _colorNegativo = value; RedrawChart(); } }

        [Category("Colores")]
        [Display(Name = "Color texto", GroupName = "Colores", Order = 12)]
        public Color ColorTexto { get => _colorTexto; set { _colorTexto = value; RedrawChart(); } }

        [Category("Escala")]
        [Display(Name = "Autoescala", GroupName = "Escala", Order = 20)]
        public bool AutoEscala { get => _autoEscala; set { _autoEscala = value; RedrawChart(); } }

        [Category("Escala")]
        [Display(Name = "Lookback (barras)", GroupName = "Escala", Order = 21)]
        [Range(5, 5000)]
        public int Lookback { get => _lookback; set { _lookback = Math.Max(5, value); RedrawChart(); } }

        [Category("Escala")]
        [Display(Name = "Escala fija |Abs(Delta)|", GroupName = "Escala", Order = 22)]
        [Range(1, 1_000_000)]
        public decimal EscalaFijaAbsDelta { get => _escalaFijaAbsDelta; set { _escalaFijaAbsDelta = Math.Max(1, value); RedrawChart(); } }

        [Category("Diseño")]
        [Display(Name = "Alto máx. barra (px)", GroupName = "Diseño", Order = 30)]
        [Range(10, 400)]
        public int MaxAltoBarraPx { get => _maxAltoBarraPx; set { _maxAltoBarraPx = Math.Max(10, value); RedrawChart(); } }

        [Category("Diseño")]
        [Display(Name = "Ancho barra (px)", GroupName = "Diseño", Order = 31)]
        [Range(4, 60)]
        public int AnchoBarraPx { get => _anchoBarraPx; set { _anchoBarraPx = Math.Max(4, value); RedrawChart(); } }

        [Category("Diseño")]
        [Display(Name = "Margen superior (px)", GroupName = "Diseño", Order = 33)]
        [Range(-5000, 5000)]
        public int MargenSuperiorPx
        {
            get => _margenSuperiorPx;
            set { _margenSuperiorPx = value; RedrawChart(); }
        }

        [Category("Texto")]
        [Display(Name = "Tamaño fuente", GroupName = "Texto", Order = 40)]
        [Range(6, 48)]
        public int TamanoFuente { get => _tamanoFuente; set { _tamanoFuente = Math.Max(6, value); RedrawChart(); } }

        [Category("Texto")]
        [Display(Name = "Texto usa color de la barra", GroupName = "Texto", Order = 41)]
        public bool TextoColorSegunDelta { get => _textoColorSegunDelta; set { _textoColorSegunDelta = value; RedrawChart(); } }

        [Category("Posición")]
        [Display(Name = "Anclar a la derecha", GroupName = "Posición", Order = 50)]
        public bool AnclarDerecha { get => _anclarDerecha; set { _anclarDerecha = value; RedrawChart(); } }

        [Category("Posición")]
        [Display(Name = "Offset derecha (px)", GroupName = "Posición", Order = 51)]
        [Range(-5000, 5000)]
        public int OffsetDerechaPx
        {
            get => _offsetDerechaPx;
            set { _offsetDerechaPx = value; RedrawChart(); }
        }

        public DeltaRightBar()
        {
            EnableCustomDrawing = true;
            DenyToChangePanel = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);
            SubscribeToDrawingEvents(DrawingLayouts.Final);
        }

        protected override void OnInitialize()
        {
            RedrawChart();
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar >= CurrentBar - 1)
                RedrawChart();
        }   

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            int lastIndex = CurrentBar - 1;
            if (lastIndex < 0)
                return;

            var candle = GetCandle(lastIndex);
            if (candle == null)
                return;

            decimal delta = 0m;
            try { delta = (decimal)candle.Delta; } catch { delta = 0m; }

            // X: anclada al último bar visible; permite offsets negativos/positivos
            int x = 20;
            try
            {
                if (AnclarDerecha && ChartInfo?.PriceChartContainer != null)
                {
                    int xRight = ChartInfo.PriceChartContainer.GetXByBar(lastIndex, false);
                    x = xRight - OffsetDerechaPx - AnchoBarraPx;
                }
            }
            catch { }

            // Y: margen superior libre (puede ser negativo o grande)
            int centerY = MargenSuperiorPx + MaxAltoBarraPx / 2;

            decimal maxRef = AutoEscala ? MaxAbsDeltaLookback(lastIndex) : Math.Abs(EscalaFijaAbsDelta);
            if (maxRef <= 0) maxRef = 1;

            int altoPx = (int)Math.Round((double)(Math.Min(1m, Math.Abs(delta) / maxRef) * MaxAltoBarraPx));
            if (altoPx < 1 && Math.Abs(delta) > 0) altoPx = 1;

            bool positivo = delta >= 0;
            var colorBarra = positivo ? ColorPositivo : ColorNegativo;
            var colorBorde = Darken(colorBarra, 0.35f);

            var rect = positivo
                ? new Rectangle(x, centerY - altoPx, AnchoBarraPx, altoPx)
                : new Rectangle(x, centerY, AnchoBarraPx, altoPx);

            context.DrawLine(new RenderPen(Color.FromArgb(120, Color.Gray), 1), x - 6, centerY, x + AnchoBarraPx + 80, centerY);
            context.FillRectangle(Color.FromArgb(180, colorBarra), rect);
            context.DrawRectangle(new RenderPen(colorBorde, 1), rect);

            var font = new RenderFont("Arial", TamanoFuente);
            var colorTexto = TextoColorSegunDelta ? colorBarra : ColorTexto;
            int txtX = x + AnchoBarraPx + 8;
            int txtY = centerY - TamanoFuente / 2 - 1;
            context.DrawString(delta.ToString("N0", CultureInfo.InvariantCulture), font, colorTexto, txtX, txtY);
        }

        private decimal MaxAbsDeltaLookback(int lastIndex)
        {
            if (lastIndex < 0)
                return 1;

            int start = Math.Max(0, lastIndex - Lookback + 1);
            decimal maxAbs = 0m;

            for (int i = start; i <= lastIndex; i++)
            {
                var c = GetCandle(i);
                if (c == null) continue;

                decimal d = 0m;
                try { d = (decimal)c.Delta; } catch { d = 0m; }

                var abs = Math.Abs(d);
                if (abs > maxAbs)
                    maxAbs = abs;
            }

            return maxAbs;
        }

        private static Color Darken(Color c, float amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            int r = (int)(c.R * (1 - amount));
            int g = (int)(c.G * (1 - amount));
            int b = (int)(c.B * (1 - amount));
            return Color.FromArgb(c.A, r, g, b);
        }
    }
}