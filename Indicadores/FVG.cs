using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using ATAS.Indicators;
using ATAS.Indicators.Drawing;

namespace ATAS.Indicators.Technical
{
    [DisplayName("Patrón FVG 3 Velas")]
    public class PatronFVG : Indicator
    {
        #region Configurables

        // Apariencia
        private Color _colorPatronBajista = Color.Red;
        private Color _colorPatronAlcista = Color.Green;
        private int _transparencia = 120;
        private int _grosorBorde = 2;

        // Filtros de patrón
        private decimal _minRangeVela2 = 0.0m;
        private decimal _maxRangeVela2 = 1000.0m;
        private decimal _minVolumenVela2 = 0.0m;
        private int _maxVelasBetween2y4 = 2;

        // Filtros de delta individuales
        private decimal _minDeltaVela2Bajista = 0.0m;
        private decimal _maxDeltaVela2Alcista = 0.0m;

        [Display(GroupName = "Apariencia", Name = "Color patrón bajista", Order = 10)]
        public Color ColorPatronBajista
        {
            get => _colorPatronBajista;
            set { _colorPatronBajista = value; RecalculateValues(); }
        }

        [Display(GroupName = "Apariencia", Name = "Color patrón alcista", Order = 20)]
        public Color ColorPatronAlcista
        {
            get => _colorPatronAlcista;
            set { _colorPatronAlcista = value; RecalculateValues(); }
        }

        [Display(GroupName = "Apariencia", Name = "Transparencia (0-255)", Order = 30)]
        [Range(0, 255)]
        public int Transparencia
        {
            get => _transparencia;
            set { _transparencia = Math.Max(0, Math.Min(255, value)); RecalculateValues(); }
        }

        [Display(GroupName = "Apariencia", Name = "Grosor borde", Order = 40)]
        [Range(1, 10)]
        public int GrosorBorde
        {
            get => _grosorBorde;
            set { _grosorBorde = Math.Max(1, value); RecalculateValues(); }
        }

        [Display(GroupName = "Filtros de Rango y Volumen", Name = "Rango mínimo vela 2", Order = 50)]
        [Description("Rango mínimo (High-Low) que debe tener la vela 2 para validar el patrón")]
        public decimal MinRangeVela2
        {
            get => _minRangeVela2;
            set { _minRangeVela2 = value; RecalculateValues(); }
        }

        [Display(GroupName = "Filtros de Rango y Volumen", Name = "Rango máximo vela 2", Order = 60)]
        [Description("Rango máximo (High-Low) que puede tener la vela 2 para validar el patrón")]
        public decimal MaxRangeVela2
        {
            get => _maxRangeVela2;
            set { _maxRangeVela2 = value; RecalculateValues(); }
        }

        [Display(GroupName = "Filtros de Rango y Volumen", Name = "Volumen mínimo vela 2", Order = 70)]
        [Description("Volumen mínimo que debe tener la vela 2 para validar el patrón")]
        public decimal MinVolumenVela2
        {
            get => _minVolumenVela2;
            set { _minVolumenVela2 = value; RecalculateValues(); }
        }

        [Display(GroupName = "Filtros de Estructura", Name = "Máx. velas entre 2 y 4", Order = 80)]
        [Description("Cantidad máxima de velas entre la vela 2 y la vela 4 (patrón extendido)")]
        [Range(1, 10)]
        public int MaxVelasBetween2y4
        {
            get => _maxVelasBetween2y4;
            set { _maxVelasBetween2y4 = Math.Max(1, value); RecalculateValues(); }
        }

        [Display(GroupName = "Filtros de Delta", Name = "Delta mínimo vela 2 (bajista)", Order = 90)]
        [Description("Delta mínimo que debe tener la vela 2 para validar el patrón bajista")]
        public decimal MinDeltaVela2Bajista
        {
            get => _minDeltaVela2Bajista;
            set { _minDeltaVela2Bajista = value; RecalculateValues(); }
        }

        [Display(GroupName = "Filtros de Delta", Name = "Delta máximo vela 2 (alcista, negativo)", Order = 100)]
        [Description("Delta máximo (negativo) que debe tener la vela 2 para validar el patrón alcista")]
        public decimal MaxDeltaVela2Alcista
        {
            get => _maxDeltaVela2Alcista;
            set { _maxDeltaVela2Alcista = value; RecalculateValues(); }
        }

        #endregion

        private struct ZonaFVG
        {
            public int BarStart;
            public int BarEnd;
            public decimal High;
            public decimal Low;
            public Color Color;
            public int BorderWidth;
            public int Transparency;
        }

        private readonly List<ZonaFVG> _zonas = new();

        // Cola circular simple para debug (mensajes visibles en gráfico)
        private readonly Queue<string> _debug = new();

        public PatronFVG()
        {
            EnableCustomDrawing = true;
            DenyToChangePanel = true;

            _colorPatronBajista = Color.FromArgb(120, Color.Red);
            _colorPatronAlcista = Color.FromArgb(120, Color.Green);
            _transparencia = 120;
            _grosorBorde = 2;
        }

        protected override void OnInitialize()
        {
            _zonas.Clear();
            _debug.Clear();
        }

        private void EnqueueDebug(string msg)
        {
            if (string.IsNullOrEmpty(msg))
                return;

            _debug.Enqueue(msg);
            while (_debug.Count > 50)
                _debug.Dequeue();
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            // Procesar solo velas cerradas
            if (bar >= CurrentBar)
                return;

            // Necesitamos al menos vela 1 y vela 2
            if (bar < 3)
                return;

            var c1 = GetCandle(bar - 3);
            var c2 = GetCandle(bar - 2);

            if (c1 == null || c2 == null)
                return;

            decimal rangoVela2 = c2.High - c2.Low;
            decimal volumenVela2 = c2.Volume;
            decimal deltaVela2 = c2.Delta; // Usar el delta real

            // Filtro de rango y volumen para la vela 2
            if (rangoVela2 < MinRangeVela2 || rangoVela2 > MaxRangeVela2)
                return;
            if (volumenVela2 < MinVolumenVela2)
                return;

            // --- PATRÓN BAJISTA ---
            if (c2.Close > c2.Open && c2.Close > c1.Close && deltaVela2 >= MinDeltaVela2Bajista)
            {
                for (int i = 1; i <= MaxVelasBetween2y4; i++)
                {
                    int idx3 = bar - 2 + i;
                    int idx4 = idx3 + 1;
                    if (idx4 >= CurrentBar)
                        break;

                    var c3 = GetCandle(idx3);
                    var c4 = GetCandle(idx4);
                    if (c3 == null || c4 == null)
                        continue;

                    if (c3.Low > c1.High && c4.Close < c1.High && c4.Close < c4.Open)
                    {
                        decimal zoneHigh = c3.Low;
                        decimal zoneLow = c1.High;
                        if (zoneHigh > zoneLow)
                        {
                            _zonas.Add(new ZonaFVG
                            {
                                BarStart = bar - 3,
                                BarEnd = idx4,
                                High = zoneHigh,
                                Low = zoneLow,
                                Color = ColorPatronBajista,
                                BorderWidth = GrosorBorde,
                                Transparency = Transparencia
                            });
                            EnqueueDebug($"Bar {bar}: Bajista FVG [{zoneLow} - {zoneHigh}] entre velas 2 y 4 ({i} velas intermedias), Delta2={deltaVela2}");
                        }
                        break;
                    }
                }
            }

            // --- PATRÓN ALCISTA (simétrico) ---
            if (c2.Close < c2.Open && c2.Close < c1.Close && deltaVela2 <= MaxDeltaVela2Alcista)
            {
                for (int i = 1; i <= MaxVelasBetween2y4; i++)
                {
                    int idx3 = bar - 2 + i;
                    int idx4 = idx3 + 1;
                    if (idx4 >= CurrentBar)
                        break;

                    var c3 = GetCandle(idx3);
                    var c4 = GetCandle(idx4);
                    if (c3 == null || c4 == null)
                        continue;

                    if (c3.High < c1.Low && c4.Close > c1.Low && c4.Close > c4.Open)
                    {
                        decimal zoneHigh = c1.Low;
                        decimal zoneLow = c3.High;
                        if (zoneHigh > zoneLow)
                        {
                            _zonas.Add(new ZonaFVG
                            {
                                BarStart = bar - 3,
                                BarEnd = idx4,
                                High = zoneHigh,
                                Low = zoneLow,
                                Color = ColorPatronAlcista,
                                BorderWidth = GrosorBorde,
                                Transparency = Transparencia
                            });
                            EnqueueDebug($"Bar {bar}: Alcista FVG [{zoneLow} - {zoneHigh}] entre velas 2 y 4 ({i} velas intermedias), Delta2={deltaVela2}");
                        }
                        break;
                    }
                }
            }
        }

        private void RecalculateValues()
        {
            _zonas.Clear();
            _debug.Clear();

            for (int b = 0; b < CurrentBar; b++)
            {
                OnCalculate(b, GetCandle(b)?.Close ?? 0);
            }

            RedrawChart();
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo?.PriceChartContainer == null)
                return;

            int px = 8;
            int py = 8;
            var font = new RenderFont("Arial", 10);
            context.DrawString($"Zonas FVG: {_zonas.Count}", font, Color.White, px, py);
            py += 14;

            foreach (var msg in _debug)
            {
                context.DrawString(msg, font, Color.LightYellow, px, py);
                py += 12;
                if (py > 200)
                    break;
            }

            foreach (var z in _zonas)
            {
                int xStart = ChartInfo.PriceChartContainer.GetXByBar(z.BarStart, false);
                int xEnd = ChartInfo.PriceChartContainer.GetXByBar(z.BarEnd, false);
                int yHigh = ChartInfo.PriceChartContainer.GetYByPrice(z.High, false);
                int yLow = ChartInfo.PriceChartContainer.GetYByPrice(z.Low, false);

                var rect = new Rectangle(
                    Math.Min(xStart, xEnd),
                    Math.Min(yHigh, yLow),
                    Math.Max(1, Math.Abs(xEnd - xStart)),
                    Math.Max(1, Math.Abs(yHigh - yLow))
                );

                context.FillRectangle(Color.FromArgb(z.Transparency, z.Color), rect);
                context.DrawRectangle(new RenderPen(z.Color, z.BorderWidth) { DashStyle = DashStyle.Solid }, rect);
            }
        }

        #region Helpers

        private static bool EsVelaAlcista(dynamic c) => c.Close > c.Open;
        private static bool EsVelaBajista(dynamic c) => c.Close < c.Open;

        private bool TieneFVGAlcista(int bar)
        {
            if (bar <= 0)
                return false;

            var vela = GetCandle(bar);
            var previa = GetCandle(bar - 1);
            if (vela == null || previa == null)
                return false;

            return vela.Low > previa.High;
        }

        private bool TieneFVGBajista(int bar)
        {
            if (bar <= 0)
                return false;

            var vela = GetCandle(bar);
            var previa = GetCandle(bar - 1);
            if (vela == null || previa == null)
                return false;

            return vela.High < previa.Low;
        }

        #endregion
    }
}