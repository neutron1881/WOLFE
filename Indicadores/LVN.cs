
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.Linq;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace ATAS.Indicators.Technical
{
	[DisplayName("LVN (Single Prints)")]
	public class LVN : Indicator
	{
		private readonly object _sync = new();

		private sealed class Zone
		{
			public decimal PriceLow { get; init; }
			public decimal PriceHigh { get; init; }
			public DateTime DayKey { get; init; }

			public int StartBar { get; set; }
			public int? EndBar { get; set; }
		}

		private bool _hideFilledZones;
		[Display(GroupName = "2. Zones", Name = "Hide filled zones", Order = 15)]
		public bool HideFilledZones
		{
			get => _hideFilledZones;
			set
			{
				_hideFilledZones = value;
				RecalculateValues();
				RedrawChart();
			}
		}

		public enum DaySessionMode { FullDay, Custom }

		private DaySessionMode _sessionMode = DaySessionMode.FullDay;
		[Display(GroupName = "1. Settings", Name = "Session mode", Order = 5)]
		public DaySessionMode SessionMode
		{
			get => _sessionMode;
			set
			{
				_sessionMode = value;
				RecalculateValues();
				RedrawChart();
			}
		}

		private string _sessionStart = "09:30";
		[Display(GroupName = "1. Settings", Name = "Session start (HH:mm)", Order = 6)]
		public string SessionStart
		{
			get => _sessionStart;
			set
			{
				_sessionStart = value ?? string.Empty;
				RecalculateValues();
				RedrawChart();
			}
		}

		private string _sessionEnd = "16:00";
		[Display(GroupName = "1. Settings", Name = "Session end (HH:mm)", Order = 7)]
		public string SessionEnd
		{
			get => _sessionEnd;
			set
			{
				_sessionEnd = value ?? string.Empty;
				RecalculateValues();
				RedrawChart();
			}
		}

		private int _tpoTimeframeMinutes = 30;
		[Display(GroupName = "1. Settings", Name = "TPO timeframe (minutes)", Order = 10)]
		[Range(1, 1440)]
		public int TpoTimeframeMinutes
		{
			get => _tpoTimeframeMinutes;
			set
			{
				_tpoTimeframeMinutes = Math.Clamp(value, 1, 1440);
				RecalculateValues();
				RedrawChart();
			}
		}

		private bool _useChartTimeframe = true;
		[Display(GroupName = "1. Settings", Name = "Use chart timeframe", Order = 20)]
		public bool UseChartTimeframe
		{
			get => _useChartTimeframe;
			set
			{
				_useChartTimeframe = value;
				RecalculateValues();
				RedrawChart();
			}
		}

		private decimal _priceStep = 0m;
		[Display(GroupName = "1. Settings", Name = "Price step (0=TickSize)", Order = 30)]
		[Range(0, 1000000)]
		public decimal PriceStep
		{
			get => _priceStep;
			set
			{
				_priceStep = Math.Max(0m, value);
				RecalculateValues();
				RedrawChart();
			}
		}

		private int _daysToKeep = 5;
		[Display(GroupName = "1. Settings", Name = "Days to keep", Order = 40)]
		[Range(1, 200)]
		public int DaysToKeep
		{
			get => _daysToKeep;
			set
			{
				_daysToKeep = Math.Clamp(value, 1, 200);
				RecalculateValues();
				RedrawChart();
			}
		}

		private bool _extendUntilFilled = true;
		[Display(GroupName = "2. Zones", Name = "Extend until filled", Order = 10)]
		public bool ExtendUntilFilled
		{
			get => _extendUntilFilled;
			set
			{
				_extendUntilFilled = value;
				RecalculateValues();
				RedrawChart();
			}
		}

		private bool _mergeAdjacentLevels = true;
		[Display(GroupName = "2. Zones", Name = "Merge adjacent levels", Order = 20)]
		public bool MergeAdjacentLevels
		{
			get => _mergeAdjacentLevels;
			set
			{
				_mergeAdjacentLevels = value;
				RecalculateValues();
				RedrawChart();
			}
		}

		private Color _zoneColor = Color.FromArgb(80, Color.DeepSkyBlue);
		[Display(GroupName = "2. Zones", Name = "Zone color", Order = 30)]
		public Color ZoneColor
		{
			get => _zoneColor;
			set
			{
				_zoneColor = value;
				RedrawChart();
			}
		}

		private Color _zoneBorderColor = Color.FromArgb(160, Color.DeepSkyBlue);
		[Display(GroupName = "2. Zones", Name = "Border color", Order = 40)]
		public Color ZoneBorderColor
		{
			get => _zoneBorderColor;
			set
			{
				_zoneBorderColor = value;
				RedrawChart();
			}
		}

		private int _zoneBorderThickness = 1;
		[Display(GroupName = "2. Zones", Name = "Border thickness", Order = 50)]
		[Range(0, 10)]
		public int ZoneBorderThickness
		{
			get => _zoneBorderThickness;
			set
			{
				_zoneBorderThickness = Math.Clamp(value, 0, 10);
				RedrawChart();
			}
		}

		private bool _showDebugText;
		[Display(GroupName = "3. Debug", Name = "Show debug text", Order = 10)]
		public bool ShowDebugText
		{
			get => _showDebugText;
			set
			{
				_showDebugText = value;
				RedrawChart();
			}
		}

		private readonly List<Zone> _zones = new();
		private long _lastRebuildKey;

		public LVN()
		{
			EnableCustomDrawing = true;
			DenyToChangePanel = true;
			SubscribeToDrawingEvents(DrawingLayouts.Historical);
		}

		protected override void OnCalculate(int bar, decimal value)
		{
			// no-op
		}

		protected override void OnRender(RenderContext context, DrawingLayouts layout)
		{
			if (ChartInfo?.PriceChartContainer == null)
			{
				context.DrawString("Chart not ready", new RenderFont("Arial", 10), Color.Red, 10, 10);
				return;
			}

			RebuildZonesIfNeeded();

			List<Zone> zonesSnapshot;
			lock (_sync)			
				zonesSnapshot = _zones.ToList();

			if (zonesSnapshot.Count == 0)
			{
				if (_showDebugText)
					context.DrawString("No zones", new RenderFont("Arial", 10), Color.Gray, 10, 10);
				return;
			}

			int first = FirstVisibleBarNumber;
			int last = LastVisibleBarNumber;

			foreach (var z in zonesSnapshot)
			{
				int zStart = z.StartBar;
				int zEnd = z.EndBar ?? last;

				if (zEnd < first || zStart > last)
					continue;

				zStart = Math.Max(zStart, first);
				zEnd = Math.Min(zEnd, last);

				int x1 = ChartInfo.PriceChartContainer.GetXByBar(zStart, false);
				int x2 = ChartInfo.PriceChartContainer.GetXByBar(zEnd, false);
				if (x2 < x1)
				{
					var t = x1;
					x1 = x2;
					x2 = t;
				}

				int yHigh = ChartInfo.PriceChartContainer.GetYByPrice(z.PriceHigh, false);
				int yLow = ChartInfo.PriceChartContainer.GetYByPrice(z.PriceLow, false);
				int top = Math.Min(yHigh, yLow);
				int bottom = Math.Max(yHigh, yLow);

				var rect = new Rectangle(x1, top, Math.Max(1, x2 - x1), Math.Max(1, bottom - top));
				context.FillRectangle(_zoneColor, rect);

				if (_zoneBorderThickness > 0)
				{
					var pen = new RenderPen(_zoneBorderColor, _zoneBorderThickness);
					context.DrawRectangle(pen, rect);
				}
			}

			if (_showDebugText)
			{
				var font = new RenderFont("Arial", 9);
				context.DrawString($"Zones: {zonesSnapshot.Count}", font, Color.White, 10, 10);
				var z0 = zonesSnapshot[^1];
				context.DrawString($"Last: {z0.DayKey:yyyy-MM-dd} [{z0.PriceLow.ToString(CultureInfo.InvariantCulture)}-{z0.PriceHigh.ToString(CultureInfo.InvariantCulture)}]", font, Color.White, 10, 24);
			}
		}

		private void RebuildZonesIfNeeded()
		{
			long key = ((long)CurrentBar << 32)
					   ^ (uint)_tpoTimeframeMinutes
					   ^ (uint)(_useChartTimeframe ? 1 : 0)
					   ^ (uint)(_mergeAdjacentLevels ? 2 : 0)
					   ^ (uint)(_extendUntilFilled ? 4 : 0)
					   ^ (uint)(_hideFilledZones ? 8 : 0)
					   ^ (uint)((int)_sessionMode << 4)
					   ^ (uint)_daysToKeep
					   ^ (long)(_priceStep * 100000m);

			if (key == _lastRebuildKey)
				return;

			_lastRebuildKey = key;
			RebuildZones();
		}

		private void RebuildZones()
		{
			var step = GetEffectivePriceStep();
			if (step <= 0)
				return;

			var zones = new List<Zone>();
			var dayBars = new Dictionary<DateTime, List<int>>();

			for (int bar = 0; bar <= CurrentBar; bar++)
			{
				var dt = GetBarTime(bar);
				if (dt == null)
					continue;
				if (!IsInSession(dt.Value))
					continue;

				var day = dt.Value.Date;
				if (!dayBars.TryGetValue(day, out var list))
				{
					list = new List<int>();
					dayBars[day] = list;
				}
				list.Add(bar);
			}

			var orderedDays = dayBars.Keys.OrderBy(d => d).ToList();
			if (orderedDays.Count > _daysToKeep)
				orderedDays = orderedDays.Skip(orderedDays.Count - _daysToKeep).ToList();

			foreach (var day in orderedDays)
			{
				var bars = dayBars[day];
				if (bars.Count == 0)
					continue;

				var tpoCounts = new Dictionary<decimal, int>();
				var firstSeenBar = new Dictionary<decimal, int>();

				foreach (var tpo in EnumerateTpoRangesForDay(bars))
				{
					var p1 = FloorToStep(tpo.Low, step);
					var p2 = FloorToStep(tpo.High, step);
					if (p2 < p1)
					{
						var t = p1;
						p1 = p2;
						p2 = t;
					}

					int guard = 0;
					for (var p = p1; p <= p2 && guard < 200000; p += step, guard++)
					{
						if (!tpoCounts.TryGetValue(p, out var c))
						{
							tpoCounts[p] = 1;
							firstSeenBar[p] = tpo.StartBar;
						}
						else
						{
							tpoCounts[p] = c + 1;
						}
					}
				}

				var singles = tpoCounts
					.Where(kv => kv.Value == 1)
					.Select(kv => kv.Key)
					.OrderBy(p => p)
					.ToList();

				if (singles.Count == 0)
					continue;

				if (_mergeAdjacentLevels)
				{
					var start = singles[0];
					var last = singles[0];
					int zoneStartBar = firstSeenBar[start];

					for (int i = 1; i < singles.Count; i++)
					{
						var p = singles[i];
						if (p == last + step)
						{
							last = p;
							zoneStartBar = Math.Min(zoneStartBar, firstSeenBar[p]);
							continue;
						}

						zones.Add(BuildZone(day, start, last, step, zoneStartBar));

						start = p;
						last = p;
						zoneStartBar = firstSeenBar[p];
					}

					zones.Add(BuildZone(day, start, last, step, zoneStartBar));
				}
				else
				{
					foreach (var p in singles)
						zones.Add(BuildZone(day, p, p, step, firstSeenBar[p]));
				}
			}

			if (_extendUntilFilled)
			{
				foreach (var z in zones)
				{
					if (z.EndBar.HasValue)
						continue;

					for (int bar = z.StartBar + 1; bar <= CurrentBar; bar++)
					{
						if (!TryGetBarRange(bar, out var low, out var high))
							continue;

						if (FillsCompletely(low, high, z.PriceLow, z.PriceHigh))
						{
							z.EndBar = bar;
							break;
						}
					}

			if (_hideFilledZones)
				zones = zones.Where(z => !z.EndBar.HasValue).ToList();
				}
			}

			lock (_sync)
			{
				_zones.Clear();
				_zones.AddRange(zones);
			}
		}

		private readonly struct TpoRange
		{
			public TpoRange(decimal low, decimal high, int startBar)
			{
				Low = low;
				High = high;
				StartBar = startBar;
			}

			public decimal Low { get; }
			public decimal High { get; }
			public int StartBar { get; }
		}

		private IEnumerable<TpoRange> EnumerateTpoRangesForDay(List<int> dayBars)
		{
			if (dayBars.Count == 0)
				yield break;

			// Fast path: chart bars are the TPOs.
			if (_useChartTimeframe)
			{
				foreach (var bar in dayBars)
				{
					if (!TryGetBarRange(bar, out var low, out var high))
						continue;
					yield return new TpoRange(low, high, bar);
				}
				yield break;
			}

			// Aggregate multiple chart candles into larger TPO buckets of TpoTimeframeMinutes.
			// Bucket key: (dayStart + floor((time - dayStart)/bucket))
			var buckets = new Dictionary<long, (decimal low, decimal high, int startBar)>();
			var mins = Math.Max(1, _tpoTimeframeMinutes);
			var dayStart = GetBarTime(dayBars[0])?.Date;
			if (!dayStart.HasValue)
				yield break;

			foreach (var bar in dayBars)
			{
				var time = GetBarTime(bar);
				if (!time.HasValue)
					continue;
				if (!TryGetBarRange(bar, out var low, out var high))
					continue;

				var deltaMin = (long)Math.Floor((time.Value - dayStart.Value).TotalMinutes);
				if (deltaMin < 0)
					deltaMin = 0;
				var bucket = deltaMin / mins;
				if (!buckets.TryGetValue(bucket, out var agg))
					buckets[bucket] = (low, high, bar);
				else
					buckets[bucket] = (Math.Min(agg.low, low), Math.Max(agg.high, high), Math.Min(agg.startBar, bar));
			}

			foreach (var kv in buckets.OrderBy(k => k.Key))			
				yield return new TpoRange(kv.Value.low, kv.Value.high, kv.Value.startBar);
		}

		private bool IsInSession(DateTime time)
		{
			if (_sessionMode == DaySessionMode.FullDay)
				return true;

			if (!TryParseHm(_sessionStart, out var start))
				start = new TimeSpan(0, 0, 0);
			if (!TryParseHm(_sessionEnd, out var end))
				end = new TimeSpan(23, 59, 59);

			var t = time.TimeOfDay;
			// handle normal and overnight sessions
			if (end >= start)
				return t >= start && t <= end;
			return t >= start || t <= end;
		}

		private static bool TryParseHm(string s, out TimeSpan ts)
		{
			ts = default;
			if (string.IsNullOrWhiteSpace(s))
				return false;
			return TimeSpan.TryParseExact(s.Trim(), "hh\\:mm", CultureInfo.InvariantCulture, out ts)
				|| TimeSpan.TryParseExact(s.Trim(), "h\\:mm", CultureInfo.InvariantCulture, out ts)
				|| TimeSpan.TryParse(s.Trim(), CultureInfo.InvariantCulture, out ts);
		}

		private Zone BuildZone(DateTime day, decimal a, decimal b, decimal step, int startBar)
		{
			var low = Math.Min(a, b);
			var high = Math.Max(a, b) + step;
			return new Zone
			{
				DayKey = day,
				PriceLow = low,
				PriceHigh = high,
				StartBar = startBar,
				EndBar = null
			};
		}

		private decimal GetEffectivePriceStep()
		{
			if (_priceStep > 0)
				return _priceStep;

			try
			{
				var ti = InstrumentInfo;
				if (ti != null && ti.TickSize > 0)
					return ti.TickSize;
			}
			catch
			{
				// ignore
			}

			return 0.25m;
		}

		private DateTime? GetBarTime(int bar)
		{
			try
			{
				var c = GetCandle(bar);
				return c?.Time;
			}
			catch
			{
				return null;
			}
		}

		private bool TryGetBarRange(int bar, out decimal low, out decimal high)
		{
			low = 0m;
			high = 0m;
			try
			{
				var c = GetCandle(bar);
				if (c == null)
					return false;
				low = c.Low;
				high = c.High;
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static bool RangesOverlap(decimal aLow, decimal aHigh, decimal bLow, decimal bHigh)
		{
			var low = Math.Max(Math.Min(aLow, aHigh), Math.Min(bLow, bHigh));
			var high = Math.Min(Math.Max(aLow, aHigh), Math.Max(bLow, bHigh));
			return high >= low;
		}

		private static bool FillsCompletely(decimal barLow, decimal barHigh, decimal zoneLow, decimal zoneHigh)
		{
			if (barLow > barHigh)
			{
				var t = barLow;
				barLow = barHigh;
				barHigh = t;
			}
			if (zoneLow > zoneHigh)
			{
				var t = zoneLow;
				zoneLow = zoneHigh;
				zoneHigh = t;
			}
			return barLow <= zoneLow && barHigh >= zoneHigh;
		}

		private static decimal FloorToStep(decimal price, decimal step)
		{
			if (step <= 0)
				return price;
			var q = Math.Floor(price / step);
			return (decimal)q * step;
		}
	}
}
