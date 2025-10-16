namespace ATAS.Indicators.Technical
{
	using System;
	using System.Collections.Generic;
	using System.ComponentModel;
	using System.ComponentModel.DataAnnotations;
	using System.Drawing;
	using System.Linq;
	using System.Drawing.Drawing2D;
	using ATAS.Indicators.Drawing;
	using OFT.Attributes;
	using OFT.Localization;
	using OFT.Rendering.Context;
	using OFT.Rendering.Tools;

	[DisplayName("Master Candle Range")]
	[Display(ResourceType = typeof(Strings), Description = "Identifica la vela de mayor rango en una ventana (prev/next) y proyecta extensiones")]
	public class MasterCandleRange : Indicator
	{
		#region Nested types

		public enum DisplayMode
		{
			[Display(Name = "Lines")] Lines = 0,
			[Display(Name = "Zones")] Zones = 1
		}

		public enum StatsAlignmentMode
		{
			[Display(Name = "Left")] Left = 0,
			[Display(Name = "Center")] Center = 1,
			[Display(Name = "Right")] Right = 2
		}

		private struct MasterCandle
		{
			public int Bar;
			public decimal High;
			public decimal Low;
			public decimal Range;
			public decimal ExtensionUpper;
			public decimal ExtensionLower;
			public bool Breached;
			public bool BreachedUp;
			public bool BreachedDown;
		}

		private struct ExtensionLine
		{
			public int StartBar;
			public decimal Price;
			public bool IsUpper;
			public Color Color;
		}

		private struct ExtensionZone
		{
			public int StartBar;
			public decimal HighPrice;
			public decimal LowPrice;
			public bool IsUpper;
			public Color Color;
		}

		#endregion

		#region Fields (config)

		private int _nPrevCandles = 10;
		private int _nNextCandles = 10;
		private int _minRangeTicks = 10;
		private int _extensionPercentage = 200;
		private int _zoneWidthTicks = 5;
		private DisplayMode _displayMode = DisplayMode.Lines;
		private Color _lineColor = Color.Blue;
		private Color _zoneColor = Color.LightBlue;
		private Color _masterCandleColor = Color.Yellow;
		private int _showLastN = 0;
		private bool _hideBreached;

		// Stats HUD
		private bool _showStats = true;
		private StatsAlignmentMode _statsAlignment = StatsAlignmentMode.Left;
		private float _statsFontSize = 11f;
		private Color _statsTextColor = Color.White;

		#endregion

		#region State

		private readonly List<MasterCandle> _masterCandles = new();
		private readonly List<ExtensionLine> _extensionLines = new();
		private readonly List<ExtensionZone> _extensionZones = new();

		private int _lastProcessedCenter = -1;
		private int _lastMasterBar = -1;

		#endregion

		#region Propiedades

		[Display(Name = "Previous Candles", GroupName = "Period Settings", Order = 100)]
		[Range(1, 100)]
		public int NPrevCandles
		{
			get => _nPrevCandles;
			set { _nPrevCandles = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Next Candles", GroupName = "Period Settings", Order = 110)]
		[Range(1, 100)]
		public int NNextCandles
		{
			get => _nNextCandles;
			set { _nNextCandles = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Min Range (Ticks)", GroupName = "Filter Settings", Order = 200)]
		[Range(1, 1000)]
		public int MinRangeTicks
		{
			get => _minRangeTicks;
			set { _minRangeTicks = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Extension %", GroupName = "Extension Settings", Order = 300)]
		[Range(50, 1000)]
		public int ExtensionPercentage
		{
			get => _extensionPercentage;
			set { _extensionPercentage = Math.Max(50, value); RecalculateValues(); }
		}

		[Display(Name = "Zone Width (Ticks)", GroupName = "Extension Settings", Order = 310)]
		[Range(1, 50)]
		public int ZoneWidthTicks
		{
			get => _zoneWidthTicks;
			set { _zoneWidthTicks = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Display Mode", GroupName = "Display Settings", Order = 400)]
		public DisplayMode DisplayModeValue
		{
			get => _displayMode;
			set { _displayMode = value; RecalculateValues(); }
		}

		[Display(Name = "Line Color", GroupName = "Display Settings", Order = 410)]
		public Color LineColor
		{
			get => _lineColor;
			set { _lineColor = value; RedrawChart(); }
		}

		[Display(Name = "Zone Color", GroupName = "Display Settings", Order = 420)]
		public Color ZoneColor
		{
			get => _zoneColor;
			set { _zoneColor = value; RedrawChart(); }
		}

		[Display(Name = "Master Candle Color", GroupName = "Display Settings", Order = 430)]
		public Color MasterCandleColor
		{
			get => _masterCandleColor;
			set { _masterCandleColor = value; RedrawChart(); }
		}

		[Display(Name = "Show Only Last N (0 = all)", GroupName = "Display Settings", Order = 440)]
		[Range(0, 500)]
		public int ShowLastN
		{
			get => _showLastN;
			set { _showLastN = Math.Max(0, value); RedrawChart(); }
		}

		[Display(Name = "Hide Breached (±100%)", GroupName = "Display Settings", Order = 450)]
		public bool HideBreached
		{
			get => _hideBreached;
			set { _hideBreached = value; RedrawChart(); }
		}

		[Display(Name = "Show Stats HUD", GroupName = "Stats", Order = 500)]
		public bool ShowStats
		{
			get => _showStats;
			set { _showStats = value; RedrawChart(); }
		}

		[Display(Name = "Stats Alignment", GroupName = "Stats", Order = 510)]
		public StatsAlignmentMode StatsAlignment
		{
			get => _statsAlignment;
			set { _statsAlignment = value; RedrawChart(); }
		}

		[Display(Name = "Stats Font Size", GroupName = "Stats", Order = 520)]
		[Range(6, 48)]
		public float StatsFontSize
		{
			get => _statsFontSize;
			set { _statsFontSize = Math.Clamp(value, 6f, 48f); RedrawChart(); }
		}

		[Display(Name = "Stats Text Color", GroupName = "Stats", Order = 530)]
		public Color StatsTextColor
		{
			get => _statsTextColor;
			set { _statsTextColor = value; RedrawChart(); }
		}

		#endregion

		#region ctor

		public MasterCandleRange()
			: base(true)
		{
			DataSeries[0].IsHidden = true;
			DenyToChangePanel = true;
			EnableCustomDrawing = true;
		}

		#endregion

		#region Cálculo

		protected override void OnInitialize() => ResetState();

		protected override void OnCalculate(int bar, decimal value)
		{
			if (bar >= CurrentBar)
				return;

			UpdateBreaches(bar);

			if (bar < _nPrevCandles + _nNextCandles)
				return;

			var candidateCenter = bar - _nNextCandles;
			if (candidateCenter <= _lastProcessedCenter)
				return;

			_lastProcessedCenter = candidateCenter;

			if (candidateCenter < _nPrevCandles)
				return;

			if (_lastMasterBar != -1 && candidateCenter < _lastMasterBar + _nNextCandles)
				return;

			int windowStart = candidateCenter - _nPrevCandles;
			int windowEnd = candidateCenter + _nNextCandles;

			if (windowEnd >= CurrentBar)
				return;

			decimal maxRange = 0;
			int maxRangeBar = -1;
			decimal maxHigh = 0;
			decimal maxLow = 0;

			for (int i = windowStart; i <= windowEnd; i++)
			{
				var c = GetCandle(i);
				if (c == null)
					continue;

				var range = c.High - c.Low;

				if (range > maxRange || (range == maxRange && i > maxRangeBar))
				{
					maxRange = range;
					maxRangeBar = i;
					maxHigh = c.High;
					maxLow = c.Low;
				}
			}

			if (maxRangeBar != candidateCenter)
				return;

			var rangeTicks = maxRange / InstrumentInfo.TickSize;
			if (rangeTicks < _minRangeTicks)
				return;

			var extensionDistance = maxRange * _extensionPercentage / 100m;

			var master = new MasterCandle
			{
				Bar = candidateCenter,
				High = maxHigh,
				Low = maxLow,
				Range = maxRange,
				ExtensionUpper = maxHigh + extensionDistance,
				ExtensionLower = maxLow - extensionDistance,
				Breached = false,
				BreachedUp = false,
				BreachedDown = false
			};

			_masterCandles.Add(master);
			_lastMasterBar = candidateCenter;

			if (_displayMode == DisplayMode.Lines)
				CreateExtensionLines(master);
			else
				CreateExtensionZones(master);
		}

		private void UpdateBreaches(int bar)
		{
			if (_masterCandles.Count == 0)
				return;

			var c = GetCandle(bar);
			if (c == null)
				return;

			for (int i = 0; i < _masterCandles.Count; i++)
			{
				var mc = _masterCandles[i];
				bool changed = false;

				if (!mc.BreachedUp && c.High >= mc.ExtensionUpper)
				{
					mc.BreachedUp = true;
					changed = true;
				}

				if (!mc.BreachedDown && c.Low <= mc.ExtensionLower)
				{
					mc.BreachedDown = true;
					changed = true;
				}

				if (changed)
				{
					mc.Breached = mc.BreachedUp || mc.BreachedDown;
					_masterCandles[i] = mc;
				}
			}
		}

		#endregion

		#region Render

		protected override void OnRender(RenderContext context, DrawingLayouts layout)
		{
			if (ChartInfo?.PriceChartContainer == null)
				return;

			var subset = GetCurrentSubset();

			RenderMasterCandles(context, subset);

			if (_displayMode == DisplayMode.Lines)
				RenderExtensionLines(context, subset);
			else
				RenderExtensionZones(context, subset);

			if (ShowStats)
				RenderStatsHud(context, subset.Count);
		}

		private List<MasterCandle> GetCurrentSubset()
		{
			IEnumerable<MasterCandle> source = _masterCandles;

			if (HideBreached)
				source = source.Where(m => !m.Breached);

			if (_showLastN > 0)
				source = source
					.OrderByDescending(m => m.Bar)
					.Take(_showLastN)
					.OrderBy(m => m.Bar);

			return source.ToList();
		}

		private void RenderMasterCandles(RenderContext context, List<MasterCandle> subset)
		{
			foreach (var master in subset)
			{
				int x = ChartInfo.PriceChartContainer.GetXByBar(master.Bar, false);
				int yHigh = ChartInfo.PriceChartContainer.GetYByPrice(master.High, false);
				int yLow = ChartInfo.PriceChartContainer.GetYByPrice(master.Low, false);
				int candleWidth = Math.Max(2, (int)ChartInfo.PriceChartContainer.BarsWidth - 2);

				var rect = new Rectangle(
					x - candleWidth / 2,
					Math.Min(yHigh, yLow),
					candleWidth,
					Math.Max(1, Math.Abs(yHigh - yLow))
				);

				if (master.Breached && !HideBreached)
				{
					context.FillRectangle(Color.FromArgb(30, _masterCandleColor), rect);
					context.DrawRectangle(new RenderPen(Color.FromArgb(120, _masterCandleColor), 1), rect);
				}
				else
				{
					context.FillRectangle(Color.FromArgb(80, _masterCandleColor), rect);
					context.DrawRectangle(new RenderPen(_masterCandleColor, 2), rect);
				}
			}
		}

		private void RenderExtensionLines(RenderContext context, List<MasterCandle> subset)
		{
			if (_extensionLines.Count == 0 || subset.Count == 0)
				return;

			var allowed = new HashSet<int>(subset.ConvertAll(m => m.Bar));

			foreach (var line in _extensionLines)
			{
				if (!allowed.Contains(line.StartBar))
					continue;

				int startX = ChartInfo.PriceChartContainer.GetXByBar(line.StartBar, false);
				int endX = ChartInfo.PriceChartContainer.GetXByBar(CurrentBar - 1, false);
				int y = ChartInfo.PriceChartContainer.GetYByPrice(line.Price, false);

				var pen = new RenderPen(line.Color, 2)
				{
					DashStyle = line.IsUpper ? DashStyle.Solid : DashStyle.Dash
				};
				context.DrawLine(pen, startX, y, endX, y);
			}
		}

		private void RenderExtensionZones(RenderContext context, List<MasterCandle> subset)
		{
			if (_extensionZones.Count == 0 || subset.Count == 0)
				return;

			var allowed = new HashSet<int>(subset.ConvertAll(m => m.Bar));

			foreach (var zone in _extensionZones)
			{
				if (!allowed.Contains(zone.StartBar))
					continue;

				int startX = ChartInfo.PriceChartContainer.GetXByBar(zone.StartBar, false);
				int endX = ChartInfo.PriceChartContainer.GetXByBar(CurrentBar - 1, false);
				int yHigh = ChartInfo.PriceChartContainer.GetYByPrice(zone.HighPrice, false);
				int yLow = ChartInfo.PriceChartContainer.GetYByPrice(zone.LowPrice, false);

				var rect = new Rectangle(
					startX,
					Math.Min(yHigh, yLow),
					Math.Max(1, endX - startX),
					Math.Max(1, Math.Abs(yHigh - yLow))
				);

				context.FillRectangle(Color.FromArgb(50, zone.Color), rect);
				context.DrawRectangle(new RenderPen(zone.Color, 1)
				{
					DashStyle = zone.IsUpper ? DashStyle.Solid : DashStyle.Dash
				}, rect);
			}
		}

		private void RenderStatsHud(RenderContext context, int shownCount)
		{
			var font = new RenderFont("Arial", StatsFontSize);
			int breachedUp = _masterCandles.Count(mc => mc.BreachedUp);
			int breachedDown = _masterCandles.Count(mc => mc.BreachedDown);
			int breachedTotal = _masterCandles.Count(mc => mc.Breached);

			var lines = new List<string>
			{
				$"Total: {_masterCandles.Count} (Shown: {shownCount})",
				$"+100%: {breachedUp}   -100%: {breachedDown}   Hit: {breachedTotal}",
				$"Prev={_nPrevCandles} Next={_nNextCandles} Ext%={_extensionPercentage}"
			};

			// Posición base en X según alineación, usando barras como referencia.
			int anchorBar = _statsAlignment switch
			{
				StatsAlignmentMode.Left => Math.Max(0, CurrentBar - 1 -  (CurrentBar - 1)),    // 0
				StatsAlignmentMode.Center => (CurrentBar - 1) / 2,
				StatsAlignmentMode.Right => CurrentBar - 1,
				_ => 0
			};

			int baseX = ChartInfo.PriceChartContainer.GetXByBar(Math.Max(anchorBar, 0), false);
			// Ajuste fino horizontal
			if (_statsAlignment == StatsAlignmentMode.Left)
				baseX = baseX + 10;
			else if (_statsAlignment == StatsAlignmentMode.Center)
				baseX = baseX - 60; // aproximación centrada
			else
				baseX = baseX - 140; // aproximación para derecha

			int y = 8;
			foreach (var line in lines)
			{
				context.DrawString(line, font, StatsTextColor, baseX, y);
				y += (int)(StatsFontSize + 4);
			}
		}

		#endregion

		#region Helpers

		private void CreateExtensionLines(MasterCandle master)
		{
			_extensionLines.Add(new ExtensionLine
			{
				StartBar = master.Bar,
				Price = master.ExtensionUpper,
				IsUpper = true,
				Color = _lineColor
			});
			_extensionLines.Add(new ExtensionLine
			{
				StartBar = master.Bar,
				Price = master.ExtensionLower,
				IsUpper = false,
				Color = _lineColor
			});
		}

		private void CreateExtensionZones(MasterCandle master)
		{
			_extensionZones.Add(new ExtensionZone
			{
				StartBar = master.Bar,
				HighPrice = master.ExtensionUpper,
				LowPrice = master.High,
				IsUpper = true,
				Color = _zoneColor
			});
			_extensionZones.Add(new ExtensionZone
			{
				StartBar = master.Bar,
				HighPrice = master.Low,
				LowPrice = master.ExtensionLower,
				IsUpper = false,
				Color = _zoneColor
			});
		}

		private void ResetState()
		{
			_masterCandles.Clear();
			_extensionLines.Clear();
			_extensionZones.Clear();
			_lastProcessedCenter = -1;
			_lastMasterBar = -1;
		}

		private void RecalculateValues()
		{
			ResetState();
			for (int bar = 0; bar < CurrentBar; bar++)
				OnCalculate(bar, GetCandle(bar)?.Close ?? 0);
			RedrawChart();
		}

		#endregion
	}
}