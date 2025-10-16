using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using ATAS.Indicators.Technical;
using ATAS.Indicators.Drawing;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace ATAS.Indicators.Technical
{
    /// <summary>
    /// Indicador para detectar patrones de Doble Techo (Double Top) y Doble Suelo (Double Bottom)
    /// en gráficos de trading de la plataforma ATAS
    /// </summary>
    [DisplayName("W Pattern Indicator")]
    public class WPatternIndicator : Indicator
    {
        #region Campos Privados
        
        /// <summary>
        /// Lista para almacenar los pivotes detectados
        /// Estructura: (precio, índice de barra, tipo: 1 para máximo, -1 para mínimo)
        /// </summary>
        private List<(decimal price, int barIndex, int type)> _pivots;
        
        /// <summary>
        /// Número máximo de pivotes a mantener en memoria
        /// </summary>
        private const int MaxPivots = 10;

        /// <summary>
        /// Lista para almacenar las señales detectadas
        /// </summary>
        private List<(int barIndex, string text, decimal price, Color color)> _signals;

        /// <summary>
        /// Lista para almacenar las etiquetas de pivotes (A, B, C, D, E)
        /// </summary>
        private List<(int barIndex, string label, decimal price, Color color)> _pivotLabels;

        /// <summary>
        /// Array de letras para marcar los pivotes
        /// </summary>
        private readonly string[] _pivotLetters = { "A", "B", "C", "D", "E" };
        
        #endregion

        #region Parámetros Configurables

        /// <summary>
        /// Número de barras a la izquierda y derecha para identificar un pivote
        /// </summary>
        [DisplayName("Pivot Lookback")]
        [Description("Número de barras a la izquierda y derecha para identificar un pivote")]
        [Category("Configuración")]
        public int PivotLookback { get; set; } = 5;

        /// <summary>
        /// Tolerancia porcentual para considerar dos picos/valles al mismo nivel
        /// </summary>
        [DisplayName("Price Tolerance (%)")]
        [Description("Tolerancia porcentual para considerar dos picos/valles al mismo nivel")]
        [Category("Configuración")]
        public decimal PriceTolerance { get; set; } = 1.0m;

        /// <summary>
        /// Mostrar etiquetas de pivotes (A, B, C, D, E)
        /// </summary>
        [DisplayName("Show Pivot Labels")]
        [Description("Mostrar etiquetas de pivotes con letras A, B, C, D, E")]
        [Category("Visualización")]
        public bool ShowPivotLabels { get; set; } = true;

        /// <summary>
        /// Color para máximos
        /// </summary>
        [DisplayName("High Pivot Color")]
        [Description("Color para etiquetar máximos")]
        [Category("Visualización")]
        public Color HighPivotColor { get; set; } = Color.Orange;

        /// <summary>
        /// Color para mínimos
        /// </summary>
        [DisplayName("Low Pivot Color")]
        [Description("Color para etiquetar mínimos")]
        [Category("Visualización")]
        public Color LowPivotColor { get; set; } = Color.Cyan;

        #endregion

        #region Constructor

        /// <summary>
        /// Constructor del indicador
        /// </summary>
        public WPatternIndicator() : base(true)
        {
            // Habilitar dibujo personalizado
            EnableCustomDrawing = true;
            DenyToChangePanel = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);
            
            // Inicializar las listas
            _pivots = new List<(decimal, int, int)>();
            _signals = new List<(int, string, decimal, Color)>();
            _pivotLabels = new List<(int, string, decimal, Color)>();
        }

        #endregion

        #region Método Principal de Cálculo

        /// <summary>
        /// Método principal que se ejecuta en cada barra para detectar patrones
        /// </summary>
        /// <param name="bar">Índice de la barra actual</param>
        /// <param name="value">Valor de la barra (no utilizado en este indicador)</param>
        protected override void OnCalculate(int bar, decimal value)
        {
            // Verificar que tenemos suficientes barras para calcular pivotes
            if (bar < PivotLookback * 2)
                return;

            // Detectar pivotes en la barra actual
            DetectPivot(bar);

            // Actualizar etiquetas de pivotes
            UpdatePivotLabels();

            // Si tenemos al menos 3 pivotes, buscar patrones
            if (_pivots.Count >= 3)
            {
                AnalyzePatterns(bar);
            }
        }

        #endregion

        #region Métodos de Detección de Pivotes

        /// <summary>
        /// Detecta si la barra actual es un pivote (máximo o mínimo)
        /// </summary>
        /// <param name="bar">Índice de la barra a analizar</param>
        private void DetectPivot(int bar)
        {
            // Solo analizamos barras que no están en el extremo derecho
            if (bar >= CurrentBar - PivotLookback)
                return;

            var currentCandle = GetCandle(bar);
            
            // Verificar si es un pivote de máximo
            if (IsPivotHigh(bar))
            {
                AddOrUpdatePivot(currentCandle.High, bar, 1);
            }
            // Verificar si es un pivote de mínimo
            else if (IsPivotLow(bar))
            {
                AddOrUpdatePivot(currentCandle.Low, bar, -1);
            }
        }

        /// <summary>
        /// Verifica si la barra actual es un pivote de máximo
        /// </summary>
        /// <param name="bar">Índice de la barra</param>
        /// <returns>True si es un pivote de máximo</returns>
        private bool IsPivotHigh(int bar)
        {
            var currentHigh = GetCandle(bar).High;

            // Verificar barras a la izquierda
            for (int i = 1; i <= PivotLookback; i++)
            {
                if (GetCandle(bar - i).High >= currentHigh)
                    return false;
            }

            // Verificar barras a la derecha
            for (int i = 1; i <= PivotLookback; i++)
            {
                if (GetCandle(bar + i).High >= currentHigh)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Verifica si la barra actual es un pivote de mínimo
        /// </summary>
        /// <param name="bar">Índice de la barra</param>
        /// <returns>True si es un pivote de mínimo</returns>
        private bool IsPivotLow(int bar)
        {
            var currentLow = GetCandle(bar).Low;

            // Verificar barras a la izquierda
            for (int i = 1; i <= PivotLookback; i++)
            {
                if (GetCandle(bar - i).Low <= currentLow)
                    return false;
            }

            // Verificar barras a la derecha
            for (int i = 1; i <= PivotLookback; i++)
            {
                if (GetCandle(bar + i).Low <= currentLow)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Añade o actualiza un pivote en la lista, manteniendo la alternancia
        /// </summary>
        /// <param name="price">Precio del pivote</param>
        /// <param name="barIndex">Índice de la barra del pivote</param>
        /// <param name="type">Tipo de pivote (1: máximo, -1: mínimo)</param>
        private void AddOrUpdatePivot(decimal price, int barIndex, int type)
        {
            // Si la lista está vacía, añadir el primer pivote
            if (_pivots.Count == 0)
            {
                _pivots.Add((price, barIndex, type));
                return;
            }

            var lastPivot = _pivots.Last();

            // Si el nuevo pivote es del mismo tipo que el último
            if (lastPivot.type == type)
            {
                // Para máximos, mantener el más alto
                if (type == 1 && price > lastPivot.price)
                {
                    _pivots[_pivots.Count - 1] = (price, barIndex, type);
                }
                // Para mínimos, mantener el más bajo
                else if (type == -1 && price < lastPivot.price)
                {
                    _pivots[_pivots.Count - 1] = (price, barIndex, type);
                }
            }
            else
            {
                // El nuevo pivote es de tipo diferente, añadirlo
                _pivots.Add((price, barIndex, type));

                // Mantener solo los últimos MaxPivots pivotes
                if (_pivots.Count > MaxPivots)
                {
                    _pivots.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// Actualiza las etiquetas de los pivotes con letras A, B, C, D, E
        /// </summary>
        private void UpdatePivotLabels()
        {
            if (!ShowPivotLabels)
                return;

            _pivotLabels.Clear();

            // Tomar los últimos 5 pivotes para etiquetar
            int startIndex = Math.Max(0, _pivots.Count - 5);
            for (int i = startIndex; i < _pivots.Count; i++)
            {
                var pivot = _pivots[i];
                var letterIndex = i - startIndex;
                
                if (letterIndex < _pivotLetters.Length)
                {
                    var label = _pivotLetters[letterIndex];
                    var color = pivot.type == 1 ? HighPivotColor : LowPivotColor;
                    var displayPrice = pivot.type == 1 ? pivot.price * 1.002m : pivot.price * 0.998m;
                    
                    _pivotLabels.Add((pivot.barIndex, label, displayPrice, color));
                }
            }
        }

        #endregion

        #region Métodos de Análisis de Patrones

        /// <summary>
        /// Analiza los pivotes para detectar patrones de Doble Techo y Doble Suelo
        /// </summary>
        /// <param name="currentBar">Barra actual</param>
        private void AnalyzePatterns(int currentBar)
        {
            if (_pivots.Count < 3)
                return;

            // Obtener los últimos tres pivotes
            var p1 = _pivots[_pivots.Count - 1]; // Más reciente
            var p2 = _pivots[_pivots.Count - 2]; // Intermedio
            var p3 = _pivots[_pivots.Count - 3]; // Más antiguo

            // Verificar patrón de Doble Techo (Máximo-Mínimo-Máximo)
            if (p3.type == 1 && p2.type == -1 && p1.type == 1)
            {
                CheckDoubleTop(p1, p2, p3, currentBar);
            }
            // Verificar patrón de Doble Suelo (Mínimo-Máximo-Mínimo)
            else if (p3.type == -1 && p2.type == 1 && p1.type == -1)
            {
                CheckDoubleBottom(p1, p2, p3, currentBar);
            }
        }

        /// <summary>
        /// Verifica y confirma un patrón de Doble Techo
        /// </summary>
        /// <param name="p1">Segundo máximo</param>
        /// <param name="p2">Mínimo intermedio</param>
        /// <param name="p3">Primer máximo</param>
        /// <param name="currentBar">Barra actual</param>
        private void CheckDoubleTop(
            (decimal price, int barIndex, int type) p1,
            (decimal price, int barIndex, int type) p2,
            (decimal price, int barIndex, int type) p3,
            int currentBar)
        {
            // Verificar proximidad de precios entre los dos máximos
            var priceDifference = Math.Abs(p3.price - p1.price);
            var tolerance = p1.price * (PriceTolerance / 100m);

            if (priceDifference <= tolerance)
            {
                // Verificar confirmación: precio actual debe estar por debajo de la línea de cuello
                var necklinePrice = p2.price;
                var currentClose = GetCandle(currentBar).Close;

                if (currentClose < necklinePrice && currentBar > p1.barIndex)
                {
                    // Patrón confirmado - Añadir señal
                    var signalId = $"DT_{p1.barIndex}";
                    if (!_signals.Any(s => s.barIndex == p1.barIndex && s.text == "DT"))
                    {
                        _signals.Add((p1.barIndex, "DT", p1.price * 1.002m, Color.Red));
                    }
                }
            }
        }

        /// <summary>
        /// Verifica y confirma un patrón de Doble Suelo
        /// </summary>
        /// <param name="p1">Segundo mínimo</param>
        /// <param name="p2">Máximo intermedio</param>
        /// <param name="p3">Primer mínimo</param>
        /// <param name="currentBar">Barra actual</param>
        private void CheckDoubleBottom(
            (decimal price, int barIndex, int type) p1,
            (decimal price, int barIndex, int type) p2,
            (decimal price, int barIndex, int type) p3,
            int currentBar)
        {
            // Verificar proximidad de precios entre los dos mínimos
            var priceDifference = Math.Abs(p3.price - p1.price);
            var tolerance = p1.price * (PriceTolerance / 100m);

            if (priceDifference <= tolerance)
            {
                // Verificar confirmación: precio actual debe estar por encima de la línea de cuello
                var necklinePrice = p2.price;
                var currentClose = GetCandle(currentBar).Close;

                if (currentClose > necklinePrice && currentBar > p1.barIndex)
                {
                    // Patrón confirmado - Añadir señal
                    var signalId = $"DB_{p1.barIndex}";
                    if (!_signals.Any(s => s.barIndex == p1.barIndex && s.text == "DB"))
                    {
                        _signals.Add((p1.barIndex, "DB", p1.price * 0.998m, Color.Green));
                    }
                }
            }
        }

        #endregion

        #region Dibujo Personalizado

        /// <summary>
        /// Método para renderizar las señales en el gráfico
        /// </summary>
        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo?.PriceChartContainer == null)
                return;

            var patternFont = new RenderFont("Arial", 10, FontStyle.Bold);
            var pivotFont = new RenderFont("Arial", 12, FontStyle.Bold);

            // Dibujar etiquetas de pivotes (A, B, C, D, E)
            if (ShowPivotLabels)
            {
                foreach (var pivotLabel in _pivotLabels)
                {
                    // Verificar si la barra está visible en el gráfico
                    if (pivotLabel.barIndex >= FirstVisibleBarNumber && pivotLabel.barIndex <= LastVisibleBarNumber)
                    {
                        var xPos = ChartInfo.PriceChartContainer.GetXByBar(pivotLabel.barIndex, false);
                        var yPos = ChartInfo.PriceChartContainer.GetYByPrice(pivotLabel.price, false);

                        // Dibujar el texto de la etiqueta del pivote
                        context.DrawString(pivotLabel.label, pivotFont, pivotLabel.color, xPos - 8, yPos - 20);
                    }
                }
            }

            // Dibujar señales de patrones (DT, DB)
            foreach (var signal in _signals)
            {
                // Verificar si la barra está visible en el gráfico
                if (signal.barIndex >= FirstVisibleBarNumber && signal.barIndex <= LastVisibleBarNumber)
                {
                    var xPos = ChartInfo.PriceChartContainer.GetXByBar(signal.barIndex, false);
                    var yPos = ChartInfo.PriceChartContainer.GetYByPrice(signal.price, false);

                    // Dibujar el texto de la señal del patrón
                    context.DrawString(signal.text, patternFont, signal.color, xPos - 10, yPos - 15);
                }
            }
        }

        #endregion
    }
}   