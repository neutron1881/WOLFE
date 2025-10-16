namespace ATAS.Indicators.Technical;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Drawing;
using OFT.Attributes;
using OFT.Localization;
using Utils.Common;
using Utils.Common.Collections;
using Utils.Common.Collections.Synchronized;
using static DynamicLevels;
using CrossColors = System.Windows.Media.Colors;
using CrossColor = System.Windows.Media.Color;
using OFT.Rendering.Tools;

[Category(IndicatorCategories.VolumeOrderFlow)]
[DisplayName("Cluster Search First Bar")]
[Display(ResourceType = typeof(Strings), Description = nameof(Strings.ClusterSearchDescription))]
[HelpLink("https://help.atas.net/support/solutions/articles/72000602240")]
public partial class ClusterSearch : Indicator
{
	#region Fields

	private readonly PriceSelectionDataSeries _renderDataSeries = new("RenderDataSeries", "Price");

	// Cálculo base
	private bool _autoFilter;
	private decimal _autoFilterValue;
	private int _days = 20;
	private bool _usePrevClose;
	private CalcMode _type = CalcMode.Volume;

	private int _barsRange = 1;
	private int _priceRange = 1;
	private bool _useTimeFilter;
	private TimeSpan _timeFrom = TimeSpan.Zero;
	private TimeSpan _timeTo = TimeSpan.Zero;
	private CandleDirection _candleDirection = CandleDirection.Any;
	private PriceLocation _priceLocation = PriceLocation.Any;

	private Filter _minFilter = new() { Enabled = true, Value = 1000 };
	private Filter _maxFilter = new() { Enabled = true, Value = 99999 };
	private decimal _minFilterValue;
	private decimal _minPercent;
	private decimal _maxPercent;
	private decimal _minAverageTrade;
	private decimal _maxAverageTrade;
	private Filter _pipsFromHigh = new() { Value = 100000000 };
	private Filter _pipsFromLow = new() { Value = 100000000 };

	public FilterInt MinCandleHeight { get; set; } = new() { Value = 1, Enabled = false };
	public FilterInt MaxCandleHeight { get; set; } = new() { Value = 1, Enabled = false };
	public FilterInt MinCandleBodyHeight { get; set; } = new() { Value = 1, Enabled = false };
	public FilterInt MaxCandleBodyHeight { get; set; } = new() { Value = 1, Enabled = false };

	// Visual clusters
	private bool _fixedSizes;
	private int _size = 10;
	private int _minSize = 5;
	private int _maxSize = 50;
	private bool _showPriceSelection = true;
	private bool _onlyOneSelectionPerBar;
	private int _visualObjectsTransparency;
	private ObjectType _visualType = ObjectType.Rectangle;
	private CrossColor _clusterTransColor;
	private CrossColor _clusterPriceColor;

	// Datos internos
	private readonly HashSet<decimal> _alertPrices = [];
	private readonly SyncList<PriceSelectionValue> _lastSeriesBar = [];
	private Dictionary<(int Bar, decimal Price), PriceVolumeInfo> _clustersCache = new();
	private MergedClusterDictionary _mergedLevels;
	private Dictionary<decimal, CustomVolumeInfo> _validVolumeLevels = new();
	private bool _isFinishRecalculate;
	private int _lastBar = -1;
	private int _targetBar;
	private decimal _lastPrice;

	// Proyección de líneas
	private bool _showClusterLines = true;
	private bool _clusterLineOnlyOnFinal = true;
	private int _clusterLineExtraBars = 400;
	private int _clusterLineThickness = 1;
	private int _clusterLineMergeTicks = 0;
	private Color _clusterLinePositiveColor = Color.FromArgb(150, 0, 200, 0);
	private Color _clusterLineNegativeColor = Color.FromArgb(150, 200, 0, 0);
	private readonly List<ClusterLine> _clusterLines = new();
	private readonly HashSet<(int Bar, decimal Price)> _clusterLinesKeys = new();
	private bool _linesDirty;

	private sealed class ClusterLine
	{
		public int StartBar;
		public decimal Price;
		public bool IsPositive;
	}

	// Dual Delta Filters
	private bool _useDualDeltaFilters;
	private int _deltaPositiveMin;   // delta >= valor
	private int _deltaNegativeMax;   // delta <= valor (negativo)

	// Colores separados
	private bool _useSeparateColors = true;
	private CrossColor _clusterPosColor = CrossColor.FromArgb(120, 0, 200, 0);
	private CrossColor _clusterNegColor = CrossColor.FromArgb(120, 200, 0, 0);
	private CrossColor _priceSelPosColor = CrossColor.FromArgb(180, 0, 200, 0);
	private CrossColor _priceSelNegColor = CrossColor.FromArgb(180, 200, 0, 0);

	// Etiquetas First Bar
	private bool _showFirstBarLabels = true;
	private int _firstBarLabelFontSize = 10;
	private int _firstBarLabelOffsetPx = 6;
	private CrossColor _firstBarLabelPosColor = CrossColors.LawnGreen;
	private CrossColor _firstBarLabelNegColor = CrossColors.OrangeRed;
	private CrossColor _firstBarLabelBackColor = CrossColor.FromArgb(90, 30, 30, 30);

	private readonly Dictionary<int, RenderFont> _labelFontCache = new();

	#endregion

	#region ctor

	public ClusterSearch()
		: base(true)
	{
		DenyToChangePanel = true;
		_renderDataSeries.IsHidden = true;
		DataSeries[0] = _renderDataSeries;

		EnableCustomDrawing = true;
		SubscribeToDrawingEvents(DrawingLayouts.Historical);
		SubscribeToDrawingEvents(DrawingLayouts.Final);
		DrawAbovePrice = true;

		VisualObjectsTransparency = 70;
		PriceSelectionColor = ClusterColor = CrossColor.FromArgb(100, 255, 0, 255);
		VisualType = ObjectType.Rectangle;
	}

	#endregion

	#region Overrides principales

	protected override void OnInitialize()
	{
		_maxFilter.PropertyChanged += MaxMinFilter_PropertyChanged;
		_minFilter.PropertyChanged += MaxMinFilter_PropertyChanged;
		_pipsFromHigh.PropertyChanged += Filter_PropertyChanged;
		_pipsFromLow.PropertyChanged += Filter_PropertyChanged;

		MinCandleHeight.PropertyChanged += Filter_PropertyChanged;
		MaxCandleHeight.PropertyChanged += Filter_PropertyChanged;
		MinCandleBodyHeight.PropertyChanged += Filter_PropertyChanged;
		MaxCandleBodyHeight.PropertyChanged += Filter_PropertyChanged;
	}

	protected override void OnNewTrade(MarketDataArg trade)
	{
		if (!_isFinishRecalculate || UsePrevClose)
			return;

		var curBar = CurrentBar - 1;
		var i = 0;
		while (i < curBar && trade.Time < GetCandle(curBar - i).Time)
			i++;

		var bar = curBar - i;

		if (_lastBar != bar)
		{
			OnNewBar(bar);
			_lastBar = bar;
		}

		CalculateTick(bar, trade);
		_lastPrice = trade.Price;

		if (!_clusterLineOnlyOnFinal && _isFinishRecalculate && RegisterProjectionLines(bar))
			RedrawChart();
	}

	protected override void OnCalculate(int bar, decimal value)
	{
		if (bar is 0 && UsePrevClose)
			return;
		if (UsePrevClose) bar--;

		var newBar = _lastBar != bar;
		_lastBar = bar;

		if (bar < _targetBar || !newBar)
			return;

		if (bar == 0)
			_renderDataSeries[bar] = _lastSeriesBar;
		else
			OnNewBar(bar);

		CalculateBar(bar);

		if (_clusterLineOnlyOnFinal)
		{
			if (_isFinishRecalculate && RegisterProjectionLines(bar))
				_linesDirty = true;
		}
		else if (RegisterProjectionLines(bar))
			_linesDirty = true;

		if (_linesDirty)
		{
			_linesDirty = false;
			RedrawChart();
		}
	}

	protected override void OnRecalculate()
	{
		if (InstrumentInfo is null)
			return;

		_lastBar = -1;
		_isFinishRecalculate = false;
		_mergedLevels = new MergedClusterDictionary(PriceRange, InstrumentInfo.TickSize);
		_autoFilterValue = 0;
		_targetBar = 0;

		if (Days != 0)
		{
			var days = 0;
			for (var i = CurrentBar - 1; i >= 0; i--)
			{
				_targetBar = i;
				if (!IsNewSession(i))
					continue;
				if (++days == Days)
					break;
			}
		}

		_lastSeriesBar.Clear();
		_renderDataSeries.Clear();

		if (!AutoFilter)
			_minFilterValue = MinimalFilter();

		_clusterLines.Clear();
		_clusterLinesKeys.Clear();
	}

	protected override void OnFinishRecalculate()
	{
		if (!AutoFilter)
		{
			_isFinishRecalculate = true;
		}
		else
		{
			var valuesList = new List<PriceSelectionValue>();
			for (var i = 0; i < _renderDataSeries.Count; i++)
				if (_renderDataSeries[i].Count > 0)
					valuesList.AddRange(_renderDataSeries[i]);

			if (valuesList.Count > 0)
			{
				valuesList = CalcType == CalcMode.Delta
					? valuesList.OrderByDescending(x => Math.Abs((decimal)x.Context)).ToList()
					: valuesList.OrderByDescending(x => (decimal)x.Context).ToList();

				_autoFilterValue = valuesList.Count <= 10
					? (decimal)(CalcType == CalcMode.Delta ? Math.Abs((decimal)valuesList.Last().Context) : valuesList.Last().Context)
					: (decimal)(CalcType == CalcMode.Delta ? Math.Abs((decimal)valuesList.Skip(10).First().Context) : valuesList.Skip(10).First().Context);

				MinimumFilter.SetValueSilently(_autoFilterValue);
				_minFilterValue = MinimalFilter();

				for (var i = 0; i < _renderDataSeries.Count; i++)
				{
					if (_renderDataSeries[i].Count == 0) continue;

					_renderDataSeries[i].RemoveAll(x =>
					{
						var raw = (decimal)x.Context;
						var norm = CalcType == CalcMode.Delta ? Math.Abs(raw) : raw;
						return norm < _autoFilterValue;
					});

					_renderDataSeries[i].ForEach(l =>
					{
						var raw = (decimal)l.Context;
						var magnitude = CalcType == CalcMode.Delta ? Math.Abs(raw) : raw;
						var sz = FixedSizes ? _size : (int)(magnitude * _size / _minFilterValue);
						if (!FixedSizes)
						{
							sz = Math.Min(sz, MaxSize);
							sz = Math.Max(sz, MinSize);
						}
						l.Size = sz;
					});
				}
				OnChangeProperty(nameof(MinimumFilter));
			}
			_isFinishRecalculate = true;
		}

		if (_clusterLineOnlyOnFinal)
		{
			_clusterLines.Clear();
			_clusterLinesKeys.Clear();
			for (int b = 0; b < _renderDataSeries.Count; b++)
				RegisterProjectionLines(b);
		}

		ApplyColorsToExisting();
		RedrawChart();
	}

	protected override void OnRender(OFT.Rendering.Context.RenderContext context, DrawingLayouts layout)
	{
		if (!_showClusterLines || _clusterLines.Count == 0 || ChartInfo?.PriceChartContainer == null)
			return;

		var cont = ChartInfo.PriceChartContainer;
		int endBar = CurrentBar - 1 + _clusterLineExtraBars;
		int xEnd = cont.GetXByBar(endBar, false);
		if (xEnd <= 0 && CurrentBar > 0)
			xEnd = cont.GetXByBar(CurrentBar - 1, false);

		var penPos = new OFT.Rendering.Tools.RenderPen(_clusterLinePositiveColor, _clusterLineThickness);
		var penNeg = new OFT.Rendering.Tools.RenderPen(_clusterLineNegativeColor, _clusterLineThickness);

		foreach (var line in _clusterLines)
		{
			int xStart = cont.GetXByBar(line.StartBar, false);
			if (xStart <= 0) continue;
			int y = cont.GetYByPrice(line.Price, false);

			context.DrawLine(line.IsPositive ? penPos : penNeg, xStart, y, xEnd, y);

			if (_showFirstBarLabels &&
			    TryGetClusterDisplayValue(line.StartBar, line.Price, out var valueStr, out var isPositive))
			{
				var fore = isPositive ? _firstBarLabelPosColor : _firstBarLabelNegColor;
				DrawFirstBarLabel(context, xEnd + _firstBarLabelOffsetPx, y, valueStr, fore, _firstBarLabelBackColor);
			}
		}
	}

	#endregion

	#region Núcleo de cálculo

	private void OnNewBar(int bar)
	{
		_mergedLevels.Clear();
		_validVolumeLevels.Clear();

		if (bar > 0 && CheckBarFormation(GetCandle(bar - 1)))
		{
			var lastBar = _lastSeriesBar.Select(p => p.MemberwiseClone()).ToArray();
			_renderDataSeries[bar - 1] = new SyncList<PriceSelectionValue>(lastBar);
		}
		else if (bar > 0)
			_renderDataSeries[bar - 1] = [];

		_lastSeriesBar.Clear();
		_renderDataSeries[bar] = _lastSeriesBar;
		_lastPrice = GetCandle(bar).Close;
		_alertPrices.Clear();
	}

	private void CalculateTick(int bar, MarketDataArg trade)
	{
		var level = _clustersCache.GetOrAdd((bar, trade.Price), () => new CustomVolumeInfo(trade.Price));
		switch (trade.Direction)
		{
			case TradeDirection.Buy: level.Ask += trade.Volume; break;
			case TradeDirection.Sell: level.Bid += trade.Volume; break;
			default: level.Between += trade.Volume; break;
		}
		level.Volume += trade.Volume;
		level.Ticks++;
		UpdateLevelCache(bar, trade);

		var candle = GetCandle(bar);
		var endPrice = Math.Max(candle.Low, candle.High - (PriceRange - 1) * InstrumentInfo.TickSize);

		for (var price = candle.Low; price <= endPrice; price += InstrumentInfo.TickSize)
			if (!CheckCluster(bar, price))
				_validVolumeLevels.Remove(price);

		if (!CheckBarFormation(candle))
		{
			_renderDataSeries[bar] = new SyncList<PriceSelectionValue>();
			return;
		}

		_renderDataSeries[bar] = _lastSeriesBar;

		if (_validVolumeLevels.Count == 0)
		{
			RemoveOldSelection(bar, trade.Price);
			return;
		}

		var ranges = GetPriceRanges(bar, endPrice);

		var changedDirection = _lastPrice != trade.Price &&
			(_lastPrice >= candle.Open && trade.Price < candle.Open ||
			 _lastPrice <= candle.Open && trade.Price > candle.Open ||
			 _lastPrice == candle.Open);

		foreach (var r in ranges)
		{
			if ((trade.Price < r.From || trade.Price > r.To) && !changedDirection)
				continue;
			RemoveOldSelection(bar, trade.Price);
			CheckPriceRange(bar, r.From, r.To);
			break;
		}

		if (_lastPrice == trade.Price)
			return;

		if (PriceLoc != PriceLocation.Any)
			UpdatePriceLocationValues(bar, trade);

		if (PipsFromHigh.Enabled)
		{
			var lowValue = candle.High - InstrumentInfo.TickSize * PipsFromHigh.Value;
			if (lowValue > candle.Low)
			{
				for (int i = _lastSeriesBar.Count - 1; i >= 0; i--)
				{
					if (_lastSeriesBar[i].MinimumPrice >= lowValue) break;
					_lastSeriesBar.RemoveAt(i);
				}
			}
		}

		if (PipsFromLow.Enabled)
		{
			var highValue = candle.Low + PipsFromLow.Value * InstrumentInfo.TickSize;
			if (highValue < candle.High)
			{
				for (int i = _lastSeriesBar.Count - 1; i >= 0; i--)
				{
					if (_lastSeriesBar[i].MinimumPrice <= highValue) break;
					_lastSeriesBar.RemoveAt(i);
				}
			}
		}
	}

	private void CalculateBar(int bar)
	{
		UpdateCumulativeCachePerBar(bar);
		var candle = GetCandle(bar);
		var endPrice = Math.Max(candle.Low, candle.High - (PriceRange - 1) * InstrumentInfo.TickSize);

		for (var price = candle.Low; price <= endPrice; price += InstrumentInfo.TickSize)
			if (!CheckCluster(bar, price))
				_validVolumeLevels.Remove(price);

		if (_validVolumeLevels.Count == 0 || !CheckBarFormation(candle))
			return;

		var ranges = GetPriceRanges(bar, endPrice);
		foreach (var r in ranges)
			CheckPriceRange(bar, r.From, r.To);
	}

	private List<(decimal From, decimal To)> GetPriceRanges(int bar, decimal endPrice)
	{
		var ranges = new List<(decimal From, decimal To)>();
		var candle = GetCandle(bar);

		var maxPrice = PipsFromLow.Enabled
			? candle.Low + PipsFromLow.Value * InstrumentInfo.TickSize
			: candle.High;

		var minPrice = PipsFromHigh.Enabled
			? candle.High - PipsFromHigh.Value * InstrumentInfo.TickSize
			: candle.Low;

		if (minPrice > maxPrice)
			return ranges;

		maxPrice = Math.Min(candle.High, maxPrice);
		minPrice = Math.Max(candle.Low, minPrice);

		switch (PriceLoc)
		{
			case PriceLocation.AtHigh when maxPrice != candle.High:
			case PriceLocation.AtLow when minPrice != candle.Low:
			case PriceLocation.AtHighOrLow when maxPrice != candle.High && minPrice != candle.Low:
				return ranges;
			case PriceLocation.Any:
				return [(minPrice, maxPrice)];
			case PriceLocation.AtHigh:
			case PriceLocation.AtLow:
			case PriceLocation.AtHighOrLow:
				{
					if (PriceLoc is PriceLocation.AtHigh or PriceLocation.AtHighOrLow && maxPrice >= endPrice)
						ranges.Add((endPrice, endPrice));
					if (PriceLoc is PriceLocation.AtLow or PriceLocation.AtHighOrLow && minPrice <= candle.Low)
						ranges.Add((candle.Low, candle.Low));
					return ranges;
				}
			case PriceLocation.Body:
			case PriceLocation.UpperWick:
			case PriceLocation.LowerWick:
			case PriceLocation.AtUpperLowerWick:
				{
					var maxBody = Math.Max(candle.Open, candle.Close);
					var minBody = Math.Min(candle.Open, candle.Close);

					if (PriceLoc == PriceLocation.Body)
					{
						maxBody = Math.Min(maxBody, maxPrice);
						minBody = Math.Max(minBody, minPrice);
						return [(minBody, maxBody)];
					}

					if (PriceLoc is PriceLocation.UpperWick or PriceLocation.AtUpperLowerWick)
						ranges.Add((maxBody + InstrumentInfo.TickSize, maxPrice));
					if (PriceLoc is PriceLocation.LowerWick or PriceLocation.AtUpperLowerWick)
						ranges.Add((minPrice, minBody - InstrumentInfo.TickSize));
					return ranges;
				}
		}
		return ranges;
	}

	private void CheckPriceRange(int bar, decimal from, decimal to)
	{
		for (var p = from; p <= to; p += InstrumentInfo.TickSize)
			CheckPriceRange(bar, p);
	}

	private void CheckPriceRange(int bar, decimal price)
	{
		if (_validVolumeLevels.TryGetValue(price, out var info))
			PlaceToDataSeries(bar, info);
		else
			RemoveOldSelection(bar, price);
	}

	private bool CheckBarFormation(IndicatorCandle candle)
	{
		if ((CandleDir == CandleDirection.Bearish && candle.Close >= candle.Open) ||
		    (CandleDir == CandleDirection.Bullish && candle.Close <= candle.Open) ||
		    (CandleDir == CandleDirection.Neutral && candle.Close != candle.Open))
			return false;

		if (UseTimeFilter)
		{
			var time = candle.Time.AddHours(InstrumentInfo.TimeZone);
			var t = time.TimeOfDay;
			if (TimeFrom < TimeTo)
			{
				if (t < TimeFrom || t > TimeTo) return false;
			}
			else if (t < TimeFrom && t > TimeTo) return false;
		}

		if (MinCandleHeight.Enabled || MaxCandleHeight.Enabled)
		{
			var h = (candle.High - candle.Low) / InstrumentInfo.TickSize + 1;
			if (MinCandleHeight.Enabled && h < MinCandleHeight.Value) return false;
			if (MaxCandleHeight.Enabled && h > MaxCandleHeight.Value) return false;
		}

		if (MinCandleBodyHeight.Enabled || MaxCandleBodyHeight.Enabled)
		{
			var bh = Math.Abs(candle.Close - candle.Open) / InstrumentInfo.TickSize + 1;
			if (MinCandleBodyHeight.Enabled && bh < MinCandleBodyHeight.Value) return false;
			if (MaxCandleBodyHeight.Enabled && bh > MaxCandleBodyHeight.Value) return false;
		}
		return true;
	}

	private bool CheckCluster(int bar, decimal price)
	{
		var fullLevel = new CustomVolumeInfo(price);
		var endPrice = price + (PriceRange - 1) * InstrumentInfo.TickSize;

		for (var p = price; p <= endPrice; p += InstrumentInfo.TickSize)
			if (_mergedLevels.TryGetValue(p, out var lvl))
				fullLevel += lvl;

		if (CalcType == CalcMode.MaxVolume && price != _mergedLevels.PocPrice)
			return false;

		var value = CalcType switch
		{
			CalcMode.Bid => fullLevel.Bid,
			CalcMode.Ask => fullLevel.Ask,
			CalcMode.Delta => fullLevel.Delta,
			CalcMode.Volume or CalcMode.MaxVolume => fullLevel.Volume,
			CalcMode.Tick => fullLevel.Ticks,
			_ => 0
		};

		if (CalcType == CalcMode.Delta && _useDualDeltaFilters)
		{
			var d = fullLevel.Delta;
			if (!((d > 0 && d >= _deltaPositiveMin) || (d < 0 && d <= _deltaNegativeMax)))
				return false;

			_validVolumeLevels[price] = fullLevel;
			return true;
		}

		if (AutoFilter)
		{
			if (_autoFilterValue == 0)
			{
				_validVolumeLevels[price] = fullLevel;
				return true;
			}
			if (value < _autoFilterValue) return false;
		}

		if (MinimumFilter.Enabled && value < MinimumFilter.Value) return false;
		if (MaximumFilter.Enabled && value > MaximumFilter.Value) return false;

		if (MinAverageTrade != 0 && fullLevel.AvgTrade < MinAverageTrade) return false;
		if (MaxAverageTrade != 0 && fullLevel.AvgTrade > MaxAverageTrade) return false;

		if (MinPercent != 0 || MaxPercent != 0)
		{
			var perc = 100 * fullLevel.Volume / Math.Max(_mergedLevels.TotalVolume, 1);
			if (perc < MinPercent || (MaxPercent != 0 && perc > MaxPercent)) return false;
		}

		_validVolumeLevels[price] = fullLevel;
		return true;
	}

	private void UpdateCumulativeCachePerBar(int bar)
	{
		var candle = GetCandle(bar);
		for (var p = candle.Low; p <= candle.High; p += InstrumentInfo.TickSize)
			CreateLevelCache(bar, p);
	}

	private void CreateLevelCache(int bar, decimal price)
	{
		var level = new CustomVolumeInfo(price);
		var endBar = Math.Max(0, bar - (BarsRange - 1));
		for (var i = bar; i >= endBar; i--)
		{
			var ic = GetCandle(i);
			var cluster = _clustersCache.GetOrAdd((i, price), () => ic.GetPriceVolumeInfo(price), true);
			if (cluster is null) continue;
			level.Ask += cluster.Ask;
			level.Between += cluster.Between;
			level.Bid += cluster.Bid;
			level.Ticks += cluster.Ticks;
			level.Volume += cluster.Volume;
		}
		_mergedLevels[price] = level;
	}

	private void UpdateLevelCache(int bar, MarketDataArg trade)
	{
		if (!_mergedLevels.TryGetValue(trade.Price, out var level))
		{
			level = new CustomVolumeInfo(trade.Price);
			var startBar = Math.Max(0, bar - 1);
			var endBar = Math.Max(0, bar - (BarsRange - 1));

			for (var i = startBar; i >= endBar; i--)
			{
				var ic = GetCandle(i);
				var cluster = _clustersCache.GetOrAdd((i, trade.Price), () => ic.GetPriceVolumeInfo(trade.Price), true);
				if (cluster is null) continue;
				level.Ask += cluster.Ask;
				level.Between += cluster.Between;
				level.Bid += cluster.Bid;
				level.Ticks += cluster.Ticks;
				level.Volume += cluster.Volume;
			}
			_mergedLevels[trade.Price] = level;
		}

		_mergedLevels.RemoveVolume(level);

		switch (trade.Direction)
		{
			case TradeDirection.Buy: level.Ask += trade.Volume; break;
			case TradeDirection.Sell: level.Bid += trade.Volume; break;
			default: level.Between += trade.Volume; break;
		}
		level.Volume += trade.Volume;
		level.Ticks++;
		_mergedLevels[trade.Price] = level;
	}

	private decimal MinimalFilter()
	{
		if (AutoFilter)
			return Math.Max(_autoFilterValue, 1);

		var minF = MinimumFilter.Enabled ? MinimumFilter.Value : 0;
		var maxF = MaximumFilter.Enabled ? MaximumFilter.Value : 0;

		if (MinimumFilter.Value >= 0 && MaximumFilter.Value >= 0) return minF;
		if (MinimumFilter.Value < 0 && MaximumFilter.Value >= 0) return Math.Min(Math.Abs(minF), maxF);
		return Math.Abs(maxF);
	}

	private void SetSize()
	{
		if (FixedSizes)
		{
			for (var i = 0; i < _renderDataSeries.Count; i++)
				_renderDataSeries[i].ForEach(x => x.Size = _size);
			return;
		}

		var f = MinimalFilter();
		for (var i = 0; i < _renderDataSeries.Count; i++)
		{
			_renderDataSeries[i].ForEach(x =>
			{
				var ctx = (decimal)x.Context;
				var mag = CalcType == CalcMode.Delta ? Math.Abs(ctx) : ctx;
				x.Size = (int)(mag * _size / Math.Max(f, 1));
				if (x.Size > MaxSize) x.Size = MaxSize;
				if (x.Size < MinSize) x.Size = MinSize;
			});
		}
	}

	private void Filter_PropertyChanged(object sender, PropertyChangedEventArgs e)
	{
		RecalculateValues();
		RedrawChart();
	}

	private void MaxMinFilter_PropertyChanged(object sender, PropertyChangedEventArgs e) =>
		Filter_PropertyChanged(sender, e);

	private void AddClusterAlert(string msg)
	{
		if (!UseAlerts) return;
		AddAlert(AlertFile, InstrumentInfo.Instrument, msg, AlertColor, ClusterColor);
	}

	#endregion

	#region Proyección (helper)

	private bool RegisterProjectionLines(int bar)
	{
		if (!_showClusterLines || bar < 0 || bar >= _renderDataSeries.Count)
			return false;

		var list = _renderDataSeries[bar];
		if (list == null || list.Count == 0)
			return false;

		var tick = InstrumentInfo?.TickSize ?? 0m;
		var added = false;

		foreach (var s in list)
		{
			if (s.Context is null) continue;
			var price = s.MinimumPrice;
			var ctxVal = Convert.ToDecimal(s.Context);
			bool isPos = CalcType == CalcMode.Delta ? ctxVal >= 0 : true;
			var key = (bar, price);
			if (_clusterLinesKeys.Contains(key))
				continue;

			if (_clusterLineMergeTicks > 0 && tick > 0)
			{
				var tol = _clusterLineMergeTicks * tick;
				var existing = _clusterLines.FirstOrDefault(l =>
					l.IsPositive == isPos && Math.Abs(l.Price - price) <= tol);
				if (existing != null)
				{
					if (bar < existing.StartBar)
						existing.StartBar = bar;
					_clusterLinesKeys.Add(key);
					continue;
				}
			}

			_clusterLines.Add(new ClusterLine { StartBar = bar, Price = price, IsPositive = isPos });
			_clusterLinesKeys.Add(key);
			added = true;
		}
		return added;
	}

	private bool TryGetClusterDisplayValue(int bar, decimal price, out string display, out bool isPositive)
	{
		display = "";
		isPositive = true;

		if (bar < 0 || bar >= _renderDataSeries.Count)
			return false;

		var list = _renderDataSeries[bar];
		if (list == null || list.Count == 0)
			return false;

		var item = list.FirstOrDefault(p => p.MinimumPrice == price);
		if (item is null)
			return false;

		if (item.Context is not decimal ctxRaw)
		{
			try { ctxRaw = Convert.ToDecimal(item.Context); }
			catch { return false; }
		}

		decimal valueForShow = ctxRaw;

		string baseStr;
		try
		{
			baseStr = ChartInfo?.TryGetMinimizedVolumeString(valueForShow) ?? valueForShow.ToString("0");
		}
		catch
		{
			baseStr = valueForShow.ToString("0");
		}

		if (CalcType == CalcMode.Delta && valueForShow > 0)
			baseStr = "+" + baseStr;

		display = baseStr;
		isPositive = valueForShow >= 0;
		return true;
	}

	private void DrawFirstBarLabel(OFT.Rendering.Context.RenderContext context, int xLeft, int yCenter, string text,
		CrossColor fore, CrossColor back)
	{
		if (string.IsNullOrEmpty(text))
			return;

		// Obtener / cachear fuente
		if (!_labelFontCache.TryGetValue(_firstBarLabelFontSize, out var font))
		{
			font = new RenderFont("Segoe UI", _firstBarLabelFontSize);
			_labelFontCache[_firstBarLabelFontSize] = font;
		}

		// Aproximar ancho (igual lógica usada en otros indicadores)
		double factor = font.Style.HasFlag(FontStyle.Bold) ? 0.62 : 0.58;
		int textW = (int)Math.Ceiling(text.Length * (font.Size * factor));
		int textH = (int)Math.Round(font.Size + 4); // altura aproximada

		int padX = 4;
		int padY = 2;
		int w = textW + padX * 2;
		int h = textH + padY * 2;
		int yTop = yCenter - h / 2;

		// Convertir CrossColor (WPF) a System.Drawing.Color
		Color backColor = Color.FromArgb(back.A, back.R, back.G, back.B);
		Color foreColor = Color.FromArgb(fore.A, fore.R, fore.G, fore.B);

		var rect = new Rectangle(xLeft, yTop, w, h);
		context.FillRectangle(backColor, rect);
		context.DrawString(text, font, foreColor, xLeft + padX, yTop + padY);
	}

	#endregion

	#region Propiedades públicas

	// Calculation
	[Range(0, int.MaxValue)]
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Calculation), Name = nameof(Strings.DaysLookBack),
		Description = nameof(Strings.DaysLookBackDescription), Order = 9999)]
	public int Days { get => _days; set { _days = value; RecalculateValues(); } }

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Calculation), Name = nameof(Strings.UsePreviousClose),
		Description = nameof(Strings.CalculateOnBarCloseDescription), Order = 100)]
	public bool UsePrevClose { get => _usePrevClose; set { _usePrevClose = value; RecalculateValues(); } }

	// Mode
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Filters), Name = nameof(Strings.CalculationMode),
		Description = nameof(Strings.CalculationModeDescription), Order = 110)]
	public CalcMode CalcType { get => _type; set { _type = value; RecalculateValues(); } }

	// Autofilter
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Filters), Name = nameof(Strings.AutoFilter),
		Description = nameof(Strings.ClusterSearchAutofilterDescription), Order = 120)]
	public bool AutoFilter
	{
		get => _autoFilter;
		set
		{
			_autoFilter = value;
			MinimumFilter.Enabled = MaximumFilter.Enabled = !value;
			RecalculateValues();
		}
	}

	// Value filters
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Filters), Name = nameof(Strings.MinValue), Order = 210)]
	public Filter MinimumFilter { get => _minFilter; set { _minFilter = value; RecalculateValues(); } }

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Filters), Name = nameof(Strings.MaxValue), Order = 220)]
	public Filter MaximumFilter { get => _maxFilter; set { _maxFilter = value; RecalculateValues(); } }

	[Range(0, 100)]
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Filters), Name = nameof(Strings.MinVolumePercent), Order = 250)]
	public decimal MinPercent { get => _minPercent; set { _minPercent = value; RecalculateValues(); } }

	[Range(0, 100)]
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Filters), Name = nameof(Strings.MaxVolumePercent), Order = 260)]
	public decimal MaxPercent { get => _maxPercent; set { _maxPercent = value; RecalculateValues(); } }

	[Range(0, 10000000)]
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Filters), Name = nameof(Strings.MinimumAverageTrade), Order = 230)]
	public decimal MinAverageTrade { get => _minAverageTrade; set { _minAverageTrade = value; RecalculateValues(); } }

	[Range(0, 10000000)]
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Filters), Name = nameof(Strings.MaximumAverageTrade), Order = 240)]
	public decimal MaxAverageTrade { get => _maxAverageTrade; set { _maxAverageTrade = value; RecalculateValues(); } }

	// Delta dual
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.DeltaFilters),
		Name = "Use Dual Delta Filters", Order = 300,
		Description = "Activa el filtrado dual simplificado (>= positivo / <= negativo).")]
	public bool UseDualDeltaFilters
	{
		get => _useDualDeltaFilters;
		set { _useDualDeltaFilters = value; RecalculateValues(); }
	}

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.DeltaFilters),
		Name = "Delta Positive Min", Order = 310,
		Description = "Delta positivo mínimo. Ej: 100 => deltas >= +100.")]
	[Range(0, int.MaxValue)]
	public int DeltaPositiveMin
	{
		get => _deltaPositiveMin;
		set { _deltaPositiveMin = Math.Max(0, value); if (_useDualDeltaFilters) RecalculateValues(); }
	}

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.DeltaFilters),
		Name = "Delta Negative Max", Order = 320,
		Description = "Delta negativo máximo (negativo). Ej: -100 => deltas <= -100.")]
	[Range(int.MinValue, 0)]
	public int DeltaNegativeMax
	{
		get => _deltaNegativeMax;
		set { _deltaNegativeMax = value > 0 ? -value : value; if (_useDualDeltaFilters) RecalculateValues(); }
	}

	// Location filters
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.LocationFilters), Name = nameof(Strings.CandleDirection), Order = 400)]
	public CandleDirection CandleDir { get => _candleDirection; set { _candleDirection = value; RecalculateValues(); } }

	[Range(1, 10000)]
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.LocationFilters), Name = nameof(Strings.BarsRange), Order = 410)]
	public int BarsRange { get => _barsRange; set { _barsRange = value; RecalculateValues(); } }

	[Range(1, 100000)]
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.LocationFilters), Name = nameof(Strings.PriceRange), Order = 420)]
	public int PriceRange { get => _priceRange; set { _priceRange = value; RecalculateValues(); } }

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.LocationFilters), Name = nameof(Strings.PriceLocation), Order = 430)]
	public PriceLocation PriceLoc { get => _priceLocation; set { _priceLocation = value; RecalculateValues(); } }

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.LocationFilters), Name = nameof(Strings.PipsFromHigh), Order = 440)]
	public Filter PipsFromHigh { get => _pipsFromHigh; set { _pipsFromHigh = value; RecalculateValues(); } }

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.LocationFilters), Name = nameof(Strings.PipsFromLow), Order = 450)]
	public Filter PipsFromLow { get => _pipsFromLow; set { _pipsFromLow = value; RecalculateValues(); } }

	// Time
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.TimeFiltration), Name = nameof(Strings.UseTimeFilter), Order = 500)]
	public bool UseTimeFilter { get => _useTimeFilter; set { _useTimeFilter = value; RecalculateValues(); } }

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.TimeFiltration), Name = nameof(Strings.TimeFrom), Order = 510)]
	public TimeSpan TimeFrom { get => _timeFrom; set { _timeFrom = value; RecalculateValues(); } }

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.TimeFiltration), Name = nameof(Strings.TimeTo), Order = 520)]
	public TimeSpan TimeTo { get => _timeTo; set { _timeTo = value; RecalculateValues(); } }

	// Visualization
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Visualization), Name = nameof(Strings.VisualMode), Order = 600)]
	public ObjectType VisualType
	{
		get => _visualType;
		set
		{
			_visualType = value;
			for (int i = 0; i < _renderDataSeries.Count; i++)
				_renderDataSeries[i].ForEach(x => x.VisualObject = value);
		}
	}

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Visualization), Name = nameof(Strings.ObjectsColor), Order = 620)]
	public CrossColor ClusterColor
	{
		get => _clusterTransColor;
		set
		{
			_clusterTransColor = value;
			for (int i = 0; i < _renderDataSeries.Count; i++)
				_renderDataSeries[i].ForEach(x => x.ObjectColor = _clusterTransColor);
		}
	}

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Visualization), Name = nameof(Strings.ShowPriceSelection), Order = 630)]
	public bool ShowPriceSelection
	{
		get => _showPriceSelection;
		set
		{
			_showPriceSelection = value;
			for (int i = 0; i < _renderDataSeries.Count; i++)
				_renderDataSeries[i].ForEach(x => x.PriceSelectionColor = value ? _clusterPriceColor : CrossColors.Transparent);
		}
	}

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Visualization), Name = nameof(Strings.PriceSelectionColor), Order = 631)]
	public CrossColor PriceSelectionColor
	{
		get => _clusterPriceColor;
		set
		{
			_clusterPriceColor = value;
			for (int i = 0; i < _renderDataSeries.Count; i++)
				_renderDataSeries[i].ForEach(x => x.PriceSelectionColor = ShowPriceSelection ? _clusterPriceColor : CrossColors.Transparent);
		}
	}

	[Range(0, 100)]
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Visualization), Name = nameof(Strings.VisualObjectsTransparency), Order = 640)]
	public int VisualObjectsTransparency
	{
		get => _visualObjectsTransparency;
		set
		{
			_visualObjectsTransparency = value;
			for (int i = 0; i < _renderDataSeries.Count; i++)
				_renderDataSeries[i].ForEach(x =>
				{
					x.ObjectColor = _clusterTransColor;
					x.ObjectsTransparency = _visualObjectsTransparency;
				});
		}
	}

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Visualization), Name = nameof(Strings.FixedSizes), Order = 610)]
	public bool FixedSizes { get => _fixedSizes; set { _fixedSizes = value; SetSize(); } }

	[Range(1, int.MaxValue)]
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Visualization), Name = nameof(Strings.Size), Order = 611)]
	public int Size { get => _size; set { _size = value; SetSize(); } }

	[Range(1, int.MaxValue)]
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Visualization), Name = nameof(Strings.MinimumSize), Order = 612)]
	public int MinSize { get => _minSize; set { _minSize = value; SetSize(); } }

	[Range(1, int.MaxValue)]
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Visualization), Name = nameof(Strings.MaximumSize), Order = 613)]
	public int MaxSize { get => _maxSize; set { _maxSize = value; SetSize(); } }

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Visualization), Name = nameof(Strings.OnlyOneSelectionPerBar), Order = 614)]
	public bool OnlyOneSelectionPerBar { get => _onlyOneSelectionPerBar; set { _onlyOneSelectionPerBar = value; RecalculateValues(); } }

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Visualization), Name = "Use Separate Colors", Order = 621)]
	public bool UseSeparateColors { get => _useSeparateColors; set { _useSeparateColors = value; ApplyColorsToExisting(); } }

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Visualization), Name = "Objects Color (+)", Order = 622)]
	public CrossColor PositiveObjectsColor { get => _clusterPosColor; set { _clusterPosColor = value; if (_useSeparateColors) ApplyColorsToExisting(); } }

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Visualization), Name = "Objects Color (-)", Order = 623)]
	public CrossColor NegativeObjectsColor { get => _clusterNegColor; set { _clusterNegColor = value; if (_useSeparateColors) ApplyColorsToExisting(); } }

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Visualization), Name = "Price Sel. Color (+)", Order = 624)]
	public CrossColor PositivePriceSelectionColor { get => _priceSelPosColor; set { _priceSelPosColor = value; if (_useSeparateColors) ApplyColorsToExisting(); } }

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Visualization), Name = "Price Sel. Color (-)", Order = 625)]
	public CrossColor NegativePriceSelectionColor { get => _priceSelNegColor; set { _priceSelNegColor = value; if (_useSeparateColors) ApplyColorsToExisting(); } }

	// Etiquetas First Bar
	[Category("First Bar"), DisplayName("Mostrar etiqueta valor")]
	public bool FirstBar_MostrarEtiqueta { get => _showFirstBarLabels; set { _showFirstBarLabels = value; RedrawChart(); } }

	[Category("First Bar"), DisplayName("Etiqueta font (px)")]
	[Range(6, 28)]
	public int FirstBar_EtiquetaFontSize { get => _firstBarLabelFontSize; set { _firstBarLabelFontSize = Math.Clamp(value, 6, 28); RedrawChart(); } }

	[Category("First Bar"), DisplayName("Etiqueta offset (px)")]
	[Range(0, 100)]
	public int FirstBar_EtiquetaOffset { get => _firstBarLabelOffsetPx; set { _firstBarLabelOffsetPx = Math.Max(0, value); RedrawChart(); } }

	[Category("First Bar"), DisplayName("Etiqueta color (+)")]
	public CrossColor FirstBar_EtiquetaColorPos { get => _firstBarLabelPosColor; set { _firstBarLabelPosColor = value; RedrawChart(); } }

	[Category("First Bar"), DisplayName("Etiqueta color (-)")]
	public CrossColor FirstBar_EtiquetaColorNeg { get => _firstBarLabelNegColor; set { _firstBarLabelNegColor = value; RedrawChart(); } }

	[Category("First Bar"), DisplayName("Etiqueta fondo")]
	public CrossColor FirstBar_EtiquetaFondo { get => _firstBarLabelBackColor; set { _firstBarLabelBackColor = value; RedrawChart(); } }

	// Alerts
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Alerts), Name = nameof(Strings.UseAlerts), Order = 700)]
	public bool UseAlerts { get; set; }

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Alerts), Name = nameof(Strings.AlertFile), Order = 710)]
	public string AlertFile { get; set; } = "alert2";

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Alerts), Name = nameof(Strings.BackGround), Order = 720)]
	public CrossColor AlertColor { get; set; } = CrossColors.Black;

	// First Bar líneas
	[Category("First Bar"), DisplayName("Activar líneas")]
	public bool FirstBar_Enable { get => _showClusterLines; set { _showClusterLines = value; RedrawChart(); } }

	[Category("First Bar"), DisplayName("Sólo al cerrar barra")]
	public bool FirstBar_SoloAlCerrarBarra { get => _clusterLineOnlyOnFinal; set { _clusterLineOnlyOnFinal = value; } }

	[Category("First Bar"), DisplayName("Barras extra derecha")][Range(0, 5000)]
	public int FirstBar_BarrasExtra { get => _clusterLineExtraBars; set { _clusterLineExtraBars = Math.Max(0, value); RedrawChart(); } }

	[Category("First Bar"), DisplayName("Grosor (px)")][Range(1, 8)]
	public int FirstBar_Grosor { get => _clusterLineThickness; set { _clusterLineThickness = Math.Max(1, Math.Min(8, value)); RedrawChart(); } }

	[Category("First Bar"), DisplayName("Color (+)")]
	public Color FirstBar_ColorPositivo { get => _clusterLinePositiveColor; set { _clusterLinePositiveColor = value; RedrawChart(); } }

	[Category("First Bar"), DisplayName("Color (-)")]
	public Color FirstBar_ColorNegativo { get => _clusterLineNegativeColor; set { _clusterLineNegativeColor = value; RedrawChart(); } }

	[Category("First Bar"), DisplayName("Fusionar (ticks)")][Range(0, 500)]
	public int FirstBar_FusionTicks { get => _clusterLineMergeTicks; set { _clusterLineMergeTicks = Math.Max(0, value); RedrawChart(); } }

	#endregion

	#region Coloreado

	private void ApplyColorsToExisting()
	{
		for (var i = 0; i < _renderDataSeries.Count; i++)
		{
			var list = _renderDataSeries[i];
			for (var j = 0; j < list.Count; j++)
			{
				var v = list[j];
				decimal ctx = 0;
				try
				{
					if (v.Context is decimal d) ctx = d;
					else if (v.Context != null) ctx = Convert.ToDecimal(v.Context);
				}
				catch { }

				if (_useSeparateColors && CalcType == CalcMode.Delta)
				{
					var isPos = ctx >= 0;
					v.ObjectColor = isPos ? _clusterPosColor : _clusterNegColor;
					v.PriceSelectionColor = ShowPriceSelection
						? (isPos ? _priceSelPosColor : _priceSelNegColor)
						: CrossColors.Transparent;
				}
				else
				{
					v.ObjectColor = _clusterTransColor;
					v.PriceSelectionColor = ShowPriceSelection ? _clusterPriceColor : CrossColors.Transparent;
				}
				list[j] = v;
			}
		}
		RedrawChart();
	}

	#endregion
}