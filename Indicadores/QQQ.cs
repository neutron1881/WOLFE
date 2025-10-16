namespace ATAS.Indicators.Technical
{
	using System;
	using System.Collections.Generic;
	using System.ComponentModel; // Category/DisplayName
	using System.ComponentModel.DataAnnotations;

	using ATAS.Indicators.Drawing;

	using OFT.Attributes;
	using OFT.Rendering.Context;
	using OFT.Rendering.Settings;

	using Color = System.Drawing.Color;
	using CrossColor = System.Windows.Media.Color;

	[Category(IndicatorCategories.VolumeOrderFlow)]
	[DisplayName("QQQ→NQ Strikes Mapper")]
	[HelpLink("https://help.atas.net/support/solutions/articles/72000602472")]
	public class QQQtoNQStrikes : Indicator
	{
		#region Fields

		private readonly ValueDataSeries _render = new("RenderDataSeries", "Render") { IsHidden = true };

		// Caché de fuentes (RenderFont no es IDisposable)
		private readonly Dictionary<int, OFT.Rendering.Tools.RenderFont> _fontCache = new();

		// Relación
		private decimal _qqqRefPrice; // precio QQQ de referencia (mismo segundo)
		private decimal _nqRefPrice;  // precio NQ de referencia (mismo segundo)
		private decimal _qqqStrikeStep = 1m; // salto entre strikes de QQQ

		// Config visual
		private PenSettings _levelPen = new() { Color = CrossColor.FromArgb(220, 0, 180, 255) };
		private CrossColor _labelColor = CrossColor.FromArgb(255, 240, 240, 240);
		private int _labelFontSize = 12;
		private int _levelSegmentPx = 120;   // segmento corto a la derecha
		private int _labelOffsetXPx = 6;     // desplazamiento texto dentro del segmento
		private int _labelOffsetYPx = 0;     // desplazamiento vertical etiquetas
		private int _strikesAbove = 10;
		private int _strikesBelow = 10;
		private bool _roundToNQTick = true;
		private bool _levelsFullWidth = false; // NUEVO: líneas a toda pantalla

		// Panel QQQ top-center
		private bool _showQQQPanel = true;
		private CrossColor _qqqPanelBack = CrossColor.FromArgb(160, 30, 30, 30);
		private CrossColor _qqqPanelFore = CrossColor.FromArgb(255, 250, 250, 250);
		private int _qqqPanelFontSize = 14;
		private int _qqqPanelPaddingPx = 6;
		private int _qqqPanelTopMarginPx = 8;

		#endregion

		#region Ctor

		public QQQtoNQStrikes()
			: base(true)
		{
			DenyToChangePanel = true;
			DrawAbovePrice = true;
			DataSeries[0] = _render;

			EnableCustomDrawing = true;
			SubscribeToDrawingEvents(DrawingLayouts.Historical);
			SubscribeToDrawingEvents(DrawingLayouts.Final);
		}

		#endregion

		#region Properties

		[Display(GroupName = "Referencia", Name = "QQQ Precio ref.")]
		public decimal QQQReferencePrice
		{
			get => _qqqRefPrice;
			set { _qqqRefPrice = Math.Max(0m, value); RedrawChart(); }
		}

		[Display(GroupName = "Referencia", Name = "NQ Precio ref.")]
		public decimal NQReferencePrice
		{
			get => _nqRefPrice;
			set { _nqRefPrice = Math.Max(0m, value); RedrawChart(); }
		}

		[Range(0.01, 1000)]
		[Display(GroupName = "Referencia", Name = "Paso strike QQQ")]
		public decimal QQQStrikeStep
		{
			get => _qqqStrikeStep;
			set { _qqqStrikeStep = Math.Max(0.01m, value); RedrawChart(); }
		}

		[Range(0, 100)]
		[Display(GroupName = "Strikes", Name = "Arriba (n)")]
		public int StrikesAbove
		{
			get => _strikesAbove;
			set { _strikesAbove = Math.Max(0, value); RedrawChart(); }
		}

		[Range(0, 100)]
		[Display(GroupName = "Strikes", Name = "Abajo (n)")]
		public int StrikesBelow
		{
			get => _strikesBelow;
			set { _strikesBelow = Math.Max(0, value); RedrawChart(); }
		}

		[Display(GroupName = "Visual", Name = "Pen niveles")]
		public PenSettings LevelPen
		{
			get => _levelPen;
			set { _levelPen = value ?? new PenSettings(); RedrawChart(); }
		}

		[Display(GroupName = "Visual", Name = "Color etiqueta")]
		public CrossColor LabelColor
		{
			get => _labelColor;
			set { _labelColor = value; RedrawChart(); }
		}

		[Range(6, 48)]
		[Display(GroupName = "Visual", Name = "Tamaño fuente")]
		public int LabelFontSize
		{
			get => _labelFontSize;
			set { _labelFontSize = Math.Clamp(value, 6, 48); RedrawChart(); }
		}

		[Range(10, 600)]
		[Display(GroupName = "Visual", Name = "Segmento derecha (px)")]
		public int LevelSegmentPx
		{
			get => _levelSegmentPx;
			set { _levelSegmentPx = Math.Clamp(value, 10, 600); RedrawChart(); }
		}

		[Range(-400, 400)]
		[Display(GroupName = "Visual", Name = "Offset etiqueta X (px)")]
		public int LabelOffsetXPx
		{
			get => _labelOffsetXPx;
			set { _labelOffsetXPx = Math.Clamp(value, -400, 400); RedrawChart(); }
		}

		[Range(-400, 400)]
		[Display(GroupName = "Visual", Name = "Offset etiqueta Y (px)")]
		public int LabelOffsetYPx
		{
			get => _labelOffsetYPx;
			set { _labelOffsetYPx = Math.Clamp(value, -400, 400); RedrawChart(); }
		}

		[Display(GroupName = "Visual", Name = "Redondear a tick NQ")]
		public bool RoundToNQTick
		{
			get => _roundToNQTick;
			set { _roundToNQTick = value; RedrawChart(); }
		}

		[Display(GroupName = "Visual", Name = "Niveles a toda pantalla")]
		public bool LevelsFullWidth
		{
			get => _levelsFullWidth;
			set { _levelsFullWidth = value; RedrawChart(); }
		}

		// Panel QQQ top center
		[Display(GroupName = "Panel QQQ", Name = "Mostrar panel")]
		public bool ShowQQQPanel
		{
			get => _showQQQPanel;
			set { _showQQQPanel = value; RedrawChart(); }
		}

		[Range(6, 64)]
		[Display(GroupName = "Panel QQQ", Name = "Tamaño fuente")]
		public int QQQPanelFontSize
		{
			get => _qqqPanelFontSize;
			set { _qqqPanelFontSize = Math.Clamp(value, 6, 64); RedrawChart(); }
		}

		[Range(0, 40)]
		[Display(GroupName = "Panel QQQ", Name = "Padding (px)")]
		public int QQQPanelPaddingPx
		{
			get => _qqqPanelPaddingPx;
			set { _qqqPanelPaddingPx = Math.Clamp(value, 0, 40); RedrawChart(); }
		}

		[Range(0, 200)]
		[Display(GroupName = "Panel QQQ", Name = "Margen superior (px)")]
		public int QQQPanelTopMarginPx
		{
			get => _qqqPanelTopMarginPx;
			set { _qqqPanelTopMarginPx = Math.Clamp(value, 0, 200); RedrawChart(); }
		}

		[Display(GroupName = "Panel QQQ", Name = "Fondo")]
		public CrossColor QQQPanelBack
		{
			get => _qqqPanelBack;
			set { _qqqPanelBack = value; RedrawChart(); }
		}

		[Display(GroupName = "Panel QQQ", Name = "Texto")]
		public CrossColor QQQPanelFore
		{
			get => _qqqPanelFore;
			set { _qqqPanelFore = value; RedrawChart(); }
		}

		#endregion

		#region Overrides

		protected override void OnCalculate(int bar, decimal value)
		{
			_render[bar] = 0m;
		}

		protected override void OnRender(RenderContext context, DrawingLayouts layout)
		{
			if (ChartInfo is null || ChartInfo.PriceChartContainer is null)
				return;

			if (_qqqRefPrice <= 0m || _nqRefPrice <= 0m || _qqqStrikeStep <= 0m)
				return;

			var cont = ChartInfo.PriceChartContainer;

			// Relación diaria NQ/QQQ
			var ratio = _nqRefPrice / _qqqRefPrice;

			// Generar strikes de QQQ alrededor del precio ref
			var baseStrike = Math.Floor(_qqqRefPrice / _qqqStrikeStep) * _qqqStrikeStep;

			var xRight = ChartInfo.Region.Width;
			var xSegStart = Math.Max(0, xRight - _levelSegmentPx);
			var xLeft = cont.GetXByBar(FirstVisibleBarNumber, false);
			if (xLeft < 0) xLeft = 0;

			// Fuente y colores
			var font = GetFont(_labelFontSize);
			var labelColor = Color.FromArgb(_labelColor.A, _labelColor.R, _labelColor.G, _labelColor.B);

			// Dibujar de abajo hacia arriba para consistencia
			for (int i = -_strikesBelow; i <= _strikesAbove; i++)
			{
				var qqqStrike = baseStrike + i * _qqqStrikeStep;

				// Precio equivalente en NQ
				var nqLevel = qqqStrike * ratio;
				if (_roundToNQTick)
					nqLevel = RoundToTick(nqLevel);

				var y = cont.GetYByPrice(nqLevel, false);

				// Línea (segmento derecho o a toda pantalla)
				var x1 = _levelsFullWidth ? xLeft : xSegStart;
				var x2 = xRight;
				context.DrawLine(_levelPen.RenderObject, x1, y, x2, y);

				// Etiqueta (valor QQQ) anclada al segmento derecho
				var text = qqqStrike.ToString("0.####");
				var tx = xSegStart + _labelOffsetXPx;
				var ty = y - (int)Math.Round(font.Size / 2f) - 1 + _labelOffsetYPx;
				context.DrawString(text, font, labelColor, tx, ty);
			}

			// Panel informativo superior-centro con el QQQ "actual" estimado
			if (_showQQQPanel)
				DrawTopCenterQQQPanel(context, ratio);
		}

		#endregion

		#region Helpers

		private OFT.Rendering.Tools.RenderFont GetFont(int size)
		{
			if (!_fontCache.TryGetValue(size, out var font))
			{
				font = new OFT.Rendering.Tools.RenderFont("Segoe UI", size);
				_fontCache[size] = font;
			}
			return font;
		}

		private decimal RoundToTick(decimal price)
		{
			var ts = InstrumentInfo?.TickSize ?? 0m;
			if (ts <= 0m) return price;

			var ticks = Math.Round(price / ts, MidpointRounding.AwayFromZero);
			return ticks * ts;
		}

		private void DrawTopCenterQQQPanel(RenderContext context, decimal ratio)
		{
			if (ratio <= 0m) return;

			// Tomar precio actual del NQ (barra cerrada más reciente)
			decimal nqPrice = _nqRefPrice;
			var lastBar = CurrentBar - 1;
			if (lastBar >= 0)
			{
				try
				{
					nqPrice = GetCandle(lastBar).Close;
				}
				catch { }
			}

			// QQQ actual estimado a partir del ratio diario
			var qqqNow = nqPrice / ratio;
			var text = $"QQQ: {qqqNow:0.####}";

			var font = GetFont(_qqqPanelFontSize);

			// Medición aproximada
			double factor = font.Style.HasFlag(System.Drawing.FontStyle.Bold) ? 0.62 : 0.58;
			int textW = (int)Math.Ceiling(text.Length * (font.Size * factor));
			int textH = (int)Math.Round(font.Size + 4);

			int pad = _qqqPanelPaddingPx;
			int w = textW + pad * 2;
			int h = textH + pad * 2;

			int x = (ChartInfo.Region.Width - w) / 2;
			int y = _qqqPanelTopMarginPx;

			var back = Color.FromArgb(_qqqPanelBack.A, _qqqPanelBack.R, _qqqPanelBack.G, _qqqPanelBack.B);
			var fore = Color.FromArgb(_qqqPanelFore.A, _qqqPanelFore.R, _qqqPanelFore.G, _qqqPanelFore.B);

			context.FillRectangle(back, new System.Drawing.Rectangle(x, y, w, h));
			context.DrawString(text, font, fore, x + pad, y + pad);
		}

		#endregion
	}
}