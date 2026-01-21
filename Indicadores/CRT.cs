namespace ATAS.Indicators.Technical;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;

using ATAS.Indicators.Drawing;

using OFT.Attributes;
using OFT.Localization;
using OFT.Rendering;
using OFT.Rendering.Context;

using CrossColor = System.Windows.Media.Color;
using CrossColors = System.Windows.Media.Colors;

[DisplayName("Institutional CRT Engine")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class CRT : Indicator
{
	public enum HtfRange
	{
		[Display(Name = "4 Hours")]
		H4,

		[Display(Name = "Daily")]
		Daily
	}

	private sealed class HtfBar
	{
		public int StartBar { get; init; }
		public int EndBar { get; set; }
		public DateTime SessionTimeNy { get; init; }
		public decimal High { get; set; }
		public decimal Low { get; set; }
		public bool IsFinalized { get; set; }
		public decimal Eq => (High + Low) / 2m;
	}

	private sealed class Kalman1D
	{
		private bool _hasState;
		private double _x;
		private double _p;

		public double ProcessNoiseQ { get; set; } = 1e-5;
		public double MeasurementNoiseR { get; set; } = 1e-3;
		public double Value => _x;

		public void Reset()
		{
			_hasState = false;
			_x = 0;
			_p = 1;
		}

		public double Update(double measurement)
		{
			if (!_hasState)
			{
				_hasState = true;
				_x = measurement;
				_p = 1;
				return _x;
			}

			_p += ProcessNoiseQ;

			var k = _p / (_p + MeasurementNoiseR);
			_x = _x + k * (measurement - _x);
			_p = (1 - k) * _p;
			return _x;
		}
	}

	private readonly List<HtfBar> _htfBars = new();
	private HtfBar? _currentHtf;
	private HtfBar? _prevHtf;

	private readonly ValueDataSeries _crtHigh = new("CrtHigh", "CRT High")
	{
		Color = DefaultColors.Blue.Convert(),
		VisualType = VisualMode.Square,
		Width = 1
	};

	private readonly ValueDataSeries _crtLow = new("CrtLow", "CRT Low")
	{
		Color = DefaultColors.Red.Convert(),
		VisualType = VisualMode.Square,
		Width = 1
	};

	private readonly ValueDataSeries _crtEq = new("CrtEq", "CRT EQ")
	{
		Color = DefaultColors.Green.Convert(),
		VisualType = VisualMode.Square,
		Width = 1
	};

	private readonly ValueDataSeries _buy = new("CrtBull", "CRT Bull")
	{
		Color = DefaultColors.Lime.Convert(),
		VisualType = VisualMode.UpArrow,
		Width = 2
	};

	private readonly ValueDataSeries _sell = new("CrtBear", "CRT Bear")
	{
		Color = DefaultColors.Red.Convert(),
		VisualType = VisualMode.DownArrow,
		Width = 2
	};

	private readonly Kalman1D _kalman = new();
	private double _prevKalman;

	private TimeZoneInfo? _nyTz;

	[Display(Name = "HTF Range", GroupName = "CRT", Order = 10)]
	public HtfRange Range { get; set; } = HtfRange.H4;

	[Display(Name = "Kalman Q (process noise)", GroupName = "Quant", Order = 20)]
	[Range(1e-10, 1.0)]
	public double KalmanQ
	{
		get => _kalman.ProcessNoiseQ;
		set => _kalman.ProcessNoiseQ = Math.Max(1e-10, value);
	}

	[Display(Name = "Kalman R (measurement noise)", GroupName = "Quant", Order = 21)]
	[Range(1e-10, 10.0)]
	public double KalmanR
	{
		get => _kalman.MeasurementNoiseR;
		set => _kalman.MeasurementNoiseR = Math.Max(1e-10, value);
	}

	[Display(Name = "Min slope abs", GroupName = "Quant", Order = 22)]
	public double MinSlopeAbs { get; set; } = 0;

	[Display(Name = "Enable London Open (02:00-05:00 NY)", GroupName = "Kill Zones (NY)", Order = 30)]
	public bool EnableLondonOpen { get; set; } = true;

	[Display(Name = "Enable NY AM (08:30-11:00 NY)", GroupName = "Kill Zones (NY)", Order = 31)]
	public bool EnableNyAm { get; set; } = true;

	[Display(Name = "Enable NY PM (13:30-16:00 NY)", GroupName = "Kill Zones (NY)", Order = 32)]
	public bool EnableNyPm { get; set; } = true;

	[Display(Name = "Draw Kill Zones background", GroupName = "Kill Zones (NY)", Order = 33)]
	public bool DrawKillZones { get; set; } = true;

	[Display(Name = "Draw zones above price (foreground)", GroupName = "Kill Zones (NY)", Order = 34)]
	public bool DrawZonesForeground { get; set; } = false;

	[Display(Name = "Zone opacity (0-1)", GroupName = "Kill Zones (NY)", Order = 35)]
	[Range(0.0, 1.0)]
	public double ZoneOpacity { get; set; } = 0.12;

	[Display(Name = "London color", GroupName = "Kill Zones (NY)", Order = 36)]
	public Color LondonZoneColor { get; set; } = Color.FromArgb(40, 80, 170);

	[Display(Name = "NY AM color", GroupName = "Kill Zones (NY)", Order = 37)]
	public Color NyAmZoneColor { get; set; } = Color.FromArgb(30, 140, 60);

	[Display(Name = "NY PM color", GroupName = "Kill Zones (NY)", Order = 38)]
	public Color NyPmZoneColor { get; set; } = Color.FromArgb(170, 100, 30);

	[Display(Name = "Enable Alerts", GroupName = "Alerts", Order = 40)]
	public bool EnableAlerts { get; set; } = true;

	[Display(Name = "Sound file", GroupName = "Alerts", Order = 41)]
	public string SoundFile { get; set; } = string.Empty;

	public CRT()
	{
		DataSeries[0] = _crtHigh;
		DataSeries.Add(_crtLow);
		DataSeries.Add(_crtEq);
		DataSeries.Add(_buy);
		DataSeries.Add(_sell);

		_buy.ShowZeroValue = false;
		_sell.ShowZeroValue = false;

		EnableCustomDrawing = true;
		SubscribeToDrawingEvents(DrawingLayouts.Final);
	}

	protected override void OnRecalculate()
	{
		_htfBars.Clear();
		_currentHtf = null;
		_prevHtf = null;
		_kalman.Reset();
		_prevKalman = 0;

		_nyTz = ResolveNewYorkTimeZone();

		// Ensure drawing order reflects current setting.
		SubscribeToDrawingEvents(DrawZonesForeground ? DrawingLayouts.Final : DrawingLayouts.Historical);
	}

	protected override void OnCalculate(int bar, decimal value)
	{
		if (bar < 0)
			return;

		var candle = GetCandle(bar);
		if (candle == null)
			return;

		_nyTz ??= ResolveNewYorkTimeZone();

		UpdateHtfAggregation(bar, candle);
		UpdateCrtSeries(bar);
		UpdateSignals(bar, candle);
	}

	private void UpdateHtfAggregation(int bar, dynamic candle)
	{
		// CRT: Aggregación HTF desde LTF en horario NY.
		var timeNy = ToNewYorkTime((DateTime)candle.Time);
		var bucketStartNy = GetHtfBucketStartNy(timeNy);

		if (_currentHtf == null)
		{
			_currentHtf = new HtfBar
			{
				StartBar = bar,
				EndBar = bar,
				SessionTimeNy = bucketStartNy,
				High = (decimal)candle.High,
				Low = (decimal)candle.Low,
				IsFinalized = false
			};
			_htfBars.Add(_currentHtf);
			return;
		}

		if (bucketStartNy != _currentHtf.SessionTimeNy)
		{
			_currentHtf.IsFinalized = true;
			_prevHtf = _currentHtf;

			_currentHtf = new HtfBar
			{
				StartBar = bar,
				EndBar = bar,
				SessionTimeNy = bucketStartNy,
				High = (decimal)candle.High,
				Low = (decimal)candle.Low,
				IsFinalized = false
			};
			_htfBars.Add(_currentHtf);
			return;
		}

		_currentHtf.EndBar = bar;
		_currentHtf.High = Math.Max(_currentHtf.High, (decimal)candle.High);
		_currentHtf.Low = Math.Min(_currentHtf.Low, (decimal)candle.Low);
	}

	private void UpdateCrtSeries(int bar)
	{
		if (_currentHtf == null)
			return;

		_crtHigh[bar] = _currentHtf.High;
		_crtLow[bar] = _currentHtf.Low;
		_crtEq[bar] = _currentHtf.Eq;
	}

	private void UpdateSignals(int bar, dynamic candle)
	{
		_buy[bar] = 0;
		_sell[bar] = 0;

		if (_prevHtf == null)
			return;

		var timeNy = ToNewYorkTime((DateTime)candle.Time);
		if (!IsInEnabledKillZone(timeNy))
			return;

		var close = (double)(decimal)candle.Close;
		var k = _kalman.Update(close);
		var slope = k - _prevKalman;
		_prevKalman = k;

		if (Math.Abs(slope) < MinSlopeAbs)
			return;

		var high = (decimal)candle.High;
		var low = (decimal)candle.Low;
		var c = (decimal)candle.Close;

		var sweptHigh = high > _prevHtf.High;
		var sweptLow = low < _prevHtf.Low;

		// Turtle Soup: barrido + cierre dentro del rango HTF previo.
		var bearish = sweptHigh && c < _prevHtf.High;
		var bullish = sweptLow && c > _prevHtf.Low;

		if (bearish && slope < 0)
		{
			_sell[bar] = high;
			RaiseSignal(bar, isBuy: false);
		}
		else if (bullish && slope > 0)
		{
			_buy[bar] = low;
			RaiseSignal(bar, isBuy: true);
		}
	}

	private void RaiseSignal(int bar, bool isBuy)
	{
		if (!EnableAlerts)
			return;

		if (bar != CurrentBar)
			return;

		try
		{
			var msg = isBuy
				? "Institutional CRT Engine: Bullish Turtle Soup"
				: "Institutional CRT Engine: Bearish Turtle Soup";

			AddAlert(msg, msg);

			// Nota: en esta base de código la API expuesta puede no incluir `PlaySound`.
			// La alerta emergente sigue funcionando a través de `AddAlert`.
		}
		catch
		{
		}
	}

	protected override void OnRender(RenderContext context, DrawingLayouts layout)
	{
		base.OnRender(context, layout);

		if (DrawZonesForeground && layout != DrawingLayouts.Final)
			return;

		if (!DrawZonesForeground && layout == DrawingLayouts.Final)
			return;

		if (!DrawKillZones || _nyTz == null)
			return;

		if (ChartInfo == null)
			return;

		if (Container == null)
			return;

		try
		{
			var first = FirstVisibleBarNumber;
			var last = LastVisibleBarNumber;
			if (first < 0 || last < 0 || last < first)
				return;

			var top = Container.Region.Top;
			var height = Container.Region.Height;

			int? segStart = null;
			CrossColor segColor = CrossColors.Transparent;

			for (var bar = first; bar <= last; bar++)
			{
				var candle = GetCandle(bar);
				if (candle == null)
					continue;

				var tNy = ToNewYorkTime(candle.Time);
				var zone = GetKillZone(tNy);
				var inZone = zone != KillZone.None && IsZoneEnabled(zone);

				if (inZone && segStart == null)
				{
					segStart = bar;
					segColor = GetZoneColor(zone);
				}
				else if (!inZone && segStart != null)
				{
					DrawZoneSegment(context, segStart.Value, bar - 1, top, height, segColor);
					segStart = null;
				}
			}

			if (segStart != null)
				DrawZoneSegment(context, segStart.Value, last, top, height, segColor);
		}
		catch
		{
		}
	}

	private void DrawZoneSegment(RenderContext context, int fromBar, int toBar, int top, int height, CrossColor color)
	{
		if (ChartInfo == null)
			return;

		var x1 = (int)(decimal)ChartInfo.GetXByBar(fromBar);
		var x2 = (int)(decimal)ChartInfo.GetXByBar(toBar) + ChartInfo.PriceChartContainer.BarsWidth;
		var rect = new Rectangle(x1, top, Math.Max(1, (int)(x2 - x1)), height);
		var brushColor = ApplyOpacity(color.Convert(), ZoneOpacity);
		context.FillRectangle(brushColor, rect);
	}

	private enum KillZone
	{
		None,
		London,
		NyAm,
		NyPm
	}

	private bool IsInEnabledKillZone(DateTime timeNy)
	{
		var zone = GetKillZone(timeNy);
		return zone != KillZone.None && IsZoneEnabled(zone);
	}

	private bool IsZoneEnabled(KillZone zone)
	{
		return zone switch
		{
			KillZone.London => EnableLondonOpen,
			KillZone.NyAm => EnableNyAm,
			KillZone.NyPm => EnableNyPm,
			_ => false
		};
	}

	private KillZone GetKillZone(DateTime timeNy)
	{
		var t = timeNy.TimeOfDay;

		if (t >= new TimeSpan(2, 0, 0) && t <= new TimeSpan(5, 0, 0))
			return KillZone.London;

		if (t >= new TimeSpan(8, 30, 0) && t <= new TimeSpan(11, 0, 0))
			return KillZone.NyAm;

		if (t >= new TimeSpan(13, 30, 0) && t <= new TimeSpan(16, 0, 0))
			return KillZone.NyPm;

		return KillZone.None;
	}

	private CrossColor GetZoneColor(KillZone zone)
	{
		return zone switch
		{
			KillZone.London => ToMediaColor(LondonZoneColor),
			KillZone.NyAm => ToMediaColor(NyAmZoneColor),
			KillZone.NyPm => ToMediaColor(NyPmZoneColor),
			_ => CrossColors.Transparent
		};
	}

	private static CrossColor ToMediaColor(Color c)
	{
		return CrossColor.FromArgb(255, c.R, c.G, c.B);
	}

	private static Color ApplyOpacity(Color baseColor, double opacity01)
	{
		var o = Math.Clamp(opacity01, 0.0, 1.0);
		var a = (int)Math.Round(255 * o);
		return Color.FromArgb(a, baseColor.R, baseColor.G, baseColor.B);
	}

	private DateTime GetHtfBucketStartNy(DateTime timeNy)
	{
		if (Range == HtfRange.Daily)
			return timeNy.Date;

		var hour = (timeNy.Hour / 4) * 4;
		return new DateTime(timeNy.Year, timeNy.Month, timeNy.Day, hour, 0, 0, timeNy.Kind);
	}

	private DateTime ToNewYorkTime(DateTime time)
	{
		if (_nyTz == null)
			return time;

		var src = time.Kind == DateTimeKind.Utc
			? time
			: DateTime.SpecifyKind(time, DateTimeKind.Local);

		return TimeZoneInfo.ConvertTime(src, _nyTz);
	}

	private static TimeZoneInfo? ResolveNewYorkTimeZone()
	{
		try
		{
			return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
		}
		catch
		{
			try
			{
				return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
			}
			catch
			{
				return null;
			}
		}
	}
}
