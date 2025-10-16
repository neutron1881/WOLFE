namespace ATAS.Indicators.Technical;

using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

using ATAS.Indicators.Drawing; // AddText
using OFT.Attributes;
using OFT.Localization;
using CrossColors = System.Windows.Media.Colors;
using CrossColor = System.Windows.Media.Color;
using Utils.Common.Collections.Synchronized;

[Category(IndicatorCategories.VolumeOrderFlow)]
[DisplayName("Cluster Search MOD")]
[Display(ResourceType = typeof(Strings), Description = nameof(Strings.ClusterSearchDescription))]
public partial class ClusterSearchMod : Indicator
{
	// Modo de cálculo
	public enum CalcMode { Volume, Delta }
	public enum LabelAlign { Left, Center, Right }

	#region Fields

	private readonly PriceSelectionDataSeries _renderDataSeries = new("RenderDataSeries", "Price");

	private int _days = 20;
	private int _targetBar;
	private int _lastActualBar = -1; // detección de cierre

	// Cálculo / comunes
	private CalcMode _type = CalcMode.Delta;
	private bool _usePrevClose;

	// Sesiones: ASIA
	private bool _asiaEnabled = true;
	private decimal _asiaPosMinDelta = 1000m;    // >=
	private decimal _asiaNegMaxDelta = -1000m;   // <=
	private bool _asiaUseTimeFilter = false;
	private TimeSpan _asiaTimeFrom = TimeSpan.Zero;
	private TimeSpan _asiaTimeTo = TimeSpan.Zero;

	// Sesiones: LONDON
	private bool _ldnEnabled = true;
	private decimal _ldnPosMinDelta = 1000m;
	private decimal _ldnNegMaxDelta = -1000m;
	private bool _ldnUseTimeFilter = false;
	private TimeSpan _ldnTimeFrom = TimeSpan.Zero;
	private TimeSpan _ldnTimeTo = TimeSpan.Zero;

	// Sesiones: USA
	private bool _usaEnabled = true;
	private decimal _usaPosMinDelta = 1000m;
	private decimal _usaNegMaxDelta = -1000m;
	private bool _usaUseTimeFilter = false;
	private TimeSpan _usaTimeFrom = TimeSpan.Zero;
	private TimeSpan _usaTimeTo = TimeSpan.Zero;

	// Visual POS (comunes)
	private ObjectType _posVisualType = ObjectType.Rectangle;
	private CrossColor _posObjectColor = CrossColors.Lime;
	private int _posTransparency = 70;
	private bool _posShowPriceSelection = true;
	private CrossColor _posPriceSelColor = CrossColors.Lime;
	private bool _posFixedSizes = false;
	private int _posSize = 10;
	private int _posMinSize = 5;
	private int _posMaxSize = 50;

	// Visual NEG (comunes)
	private ObjectType _negVisualType = ObjectType.Rectangle;
	private CrossColor _negObjectColor = CrossColors.Red;
	private int _negTransparency = 70;
	private bool _negShowPriceSelection = true;
	private CrossColor _negPriceSelColor = CrossColors.Red;
	private bool _negFixedSizes = false;
	private int _negSize = 10;
	private int _negMinSize = 5;
	private int _negMaxSize = 50;

	// Label POS (comunes)
	private bool _posShowLabel = true;
	private bool _posLabelShowPrefix = true;
	private string _posLabelFormat = "N0";
	private CrossColor _posLabelColor = CrossColors.White;
	private float _posLabelFontSize = 12f;
	private LabelAlign _posLabelAlign = LabelAlign.Left;
	private int _posLabelOffsetXPx = 6;

	// Label NEG (comunes)
	private bool _negShowLabel = true;
	private bool _negLabelShowPrefix = true;
	private string _negLabelFormat = "N0";
	private CrossColor _negLabelColor = CrossColors.White;
	private float _negLabelFontSize = 12f;
	private LabelAlign _negLabelAlign = LabelAlign.Left;
	private int _negLabelOffsetXPx = 6;

	#endregion

	#region ctor

	public ClusterSearchMod()
		: base(true)
	{
		DenyToChangePanel = true;
		_renderDataSeries.IsHidden = true;
		DataSeries[0] = _renderDataSeries;
	}

	#endregion

	#region Protected methods

	protected override void OnInitialize()
	{
		// sin filtros Minimum/Maximum
	}

	protected override void OnCalculate(int bar, decimal value)
	{
		// pinta al cierre de vela: cuando entra un nuevo bar real, se procesa el anterior
		if (bar < _targetBar)
			return;

		if (bar == _lastActualBar)
			return;

		_lastActualBar = bar;

		var closedBar = bar - 1;
		if (closedBar < 0)
			return;

		CalculateBar(closedBar);
		RedrawChart();
	}

	protected override void OnRecalculate()
	{
		if (InstrumentInfo is null)
			return;

		_lastActualBar = -1;

		_targetBar = 0;
		if (Days is 0)
			return;

		var days = 0;
		for (var i = CurrentBar - 1; i >= 0; i--)
		{
			_targetBar = i;

			if (!IsNewSession(i))
				continue;

			days++;
			if (days == Days)
				break;
		}

		_renderDataSeries.Clear();
	}

	#endregion

	#region Calculation

	private void CalculateBar(int bar)
	{
		var candle = GetCandle(bar);
		if (candle is null)
			return;

		var barData = new SyncList<PriceSelectionValue>();

		switch (CalcType)
		{
			case CalcMode.Volume:
			{
				var vol = ToDecimalSafe(candle.Volume);

				var centerPrice = (candle.High + candle.Low) / 2m;
				// Denominador fijo para escalar tamaño en volumen (ajusta si lo necesitas)
				const decimal volumeSizeDenom = 1000m;
				AddMarker(barData, bar, centerPrice, vol, true, "VOL", volumeSizeDenom);
				break;
			}

			case CalcMode.Delta:
			default:
			{
				var signedDelta = ToDecimalSafe(candle.Delta);
				var centerPrice = (candle.High + candle.Low) / 2m;

				// ASIA
				if (_asiaEnabled && IsInSession(candle.Time, _asiaUseTimeFilter, _asiaTimeFrom, _asiaTimeTo))
				{
					if (signedDelta >= _asiaPosMinDelta)
						AddMarker(barData, bar, centerPrice, signedDelta, true, "ASIA", Math.Max(1m, _asiaPosMinDelta));

					if (signedDelta <= _asiaNegMaxDelta)
						AddMarker(barData, bar, centerPrice, signedDelta, false, "ASIA", Math.Max(1m, Math.Abs(_asiaNegMaxDelta)));
				}

				// LONDON
				if (_ldnEnabled && IsInSession(candle.Time, _ldnUseTimeFilter, _ldnTimeFrom, _ldnTimeTo))
				{
					if (signedDelta >= _ldnPosMinDelta)
						AddMarker(barData, bar, centerPrice, signedDelta, true, "LDN", Math.Max(1m, _ldnPosMinDelta));

					if (signedDelta <= _ldnNegMaxDelta)
						AddMarker(barData, bar, centerPrice, signedDelta, false, "LDN", Math.Max(1m, Math.Abs(_ldnNegMaxDelta)));
				}

				// USA
				if (_usaEnabled && IsInSession(candle.Time, _usaUseTimeFilter, _usaTimeFrom, _usaTimeTo))
				{
					if (signedDelta >= _usaPosMinDelta)
						AddMarker(barData, bar, centerPrice, signedDelta, true, "USA", Math.Max(1m, _usaPosMinDelta));

					if (signedDelta <= _usaNegMaxDelta)
						AddMarker(barData, bar, centerPrice, signedDelta, false, "USA", Math.Max(1m, Math.Abs(_usaNegMaxDelta)));
				}

				break;
			}
		}

		_renderDataSeries[bar] = barData;
	}

	private bool IsInSession(DateTime candleTime, bool useFilter, TimeSpan from, TimeSpan to)
	{
		if (!useFilter)
			return true;

		// Ajuste por zona horaria de InstrumentInfo si aplica
		var t = candleTime.AddHours(InstrumentInfo.TimeZone);
		var time = t.TimeOfDay;

		if (from < to)
			return time >= from && time <= to;

		// Ventanas overnight: por ejemplo 22:00 - 02:00
		return time >= from || time <= to;
	}

	private void AddMarker(SyncList<PriceSelectionValue> target, int bar, decimal price, decimal valueSigned, bool isPositive, string sessionCode, decimal sizeDenom)
	{
		var absVal = Math.Abs(valueSigned);

		// Selección de configuración por signo (comunes)
		var visualType = isPositive ? _posVisualType : _negVisualType;
		var objColor   = isPositive ? _posObjectColor : _negObjectColor;
		var trans      = isPositive ? _posTransparency : _negTransparency;
		var showSel    = isPositive ? _posShowPriceSelection : _negShowPriceSelection;
		var selColor   = isPositive ? _posPriceSelColor : _negPriceSelColor;

		var fixedSizes = isPositive ? _posFixedSizes : _negFixedSizes;
		var sizeBase   = isPositive ? _posSize : _negSize;
		var minSize    = isPositive ? _posMinSize : _negMinSize;
		var maxSize    = isPositive ? _posMaxSize : _negMaxSize;

		var denom = Math.Max(1m, sizeDenom);
		var size = fixedSizes ? sizeBase : (int)(absVal * sizeBase / denom);
		if (!fixedSizes)
		{
			if (size < minSize) size = minSize;
			if (size > maxSize) size = maxSize;
		}

		// Guardamos el signo en Context
		var v = new PriceSelectionValue(price)
		{
			MinimumPrice = price,
			MaximumPrice = price,
			Context = isPositive ? absVal : -absVal,
			Size = size,
			VisualObject = visualType,
			ObjectColor = objColor,
			ObjectsTransparency = trans,
			PriceSelectionColor = showSel ? selColor : CrossColors.Transparent
		};

		target.Add(v);

		// Label (comunes)
		if (isPositive ? _posShowLabel : _negShowLabel)
		{
			var isDeltaMode = _type == CalcMode.Delta;

			var prefixOn = isPositive ? _posLabelShowPrefix : _negLabelShowPrefix;
			var fmt = isPositive ? _posLabelFormat : _negLabelFormat;
			var col = isPositive ? _posLabelColor : _negLabelColor;
			var fs = isPositive ? _posLabelFontSize : _negLabelFontSize;
			var align = (isPositive ? _posLabelAlign : _negLabelAlign) switch
			{
				LabelAlign.Left => DrawingText.TextAlign.Left,
				LabelAlign.Center => DrawingText.TextAlign.Center,
				LabelAlign.Right => DrawingText.TextAlign.Right,
				_ => DrawingText.TextAlign.Left
			};
			var offx = isPositive ? _posLabelOffsetXPx : _negLabelOffsetXPx;

			// En Volume no mostrar “Δ ”; opcionalmente mostramos “V ” si está activo el prefijo
			var prefix = prefixOn
				? (isDeltaMode ? "Δ " : "V ")
				: string.Empty;

			var text = prefix + valueSigned.ToString(fmt);
			var id = $"CS_VAL_{sessionCode}_{(isPositive ? "POS" : "NEG")}_{bar}";

			AddText(
				id,
				text,
				true,
				bar,
				price,
				offx,
				0,
				ConvertColor(col),
				System.Drawing.Color.Transparent,
				System.Drawing.Color.Transparent,
				fs,
				align
			);
		}
	}

	#endregion

	#region Helpers

	private static decimal ToDecimalSafe(object value)
	{
		if (value is null) return 0m;
		if (value is decimal dm) return dm;
		if (value is double d) return (decimal)d;
		if (value is float f) return (decimal)f;
		if (value is long l) return l;
		if (value is int i) return i;
		if (value is short s) return s;
		if (value is byte b) return b;
		if (value is sbyte sb) return sb;
		if (value is uint ui) return ui;
		if (value is ulong ul) return (decimal)ul;
		if (value is ushort us) return us;
		if (value is string str && decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
		try { return Convert.ToDecimal(value, CultureInfo.InvariantCulture); } catch { return 0m; }
	}

	private static System.Drawing.Color ConvertColor(CrossColor color) =>
		System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);

	#endregion

	#region Calculation (comunes)

	[Range(0, int.MaxValue)]
	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Calculation), Name = nameof(Strings.DaysLookBack), Order = 100,
		Description = nameof(Strings.DaysLookBackDescription))]
	public int Days
	{
		get => _days;
		set { _days = value; RecalculateValues(); }
	}

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Calculation), Name = nameof(Strings.UsePreviousClose), Order = 110,
		Description = nameof(Strings.CalculateOnBarCloseDescription))]
	public bool UsePrevClose
	{
		get => _usePrevClose;
		set { _usePrevClose = value; RecalculateValues(); }
	}

	[Display(ResourceType = typeof(Strings), GroupName = nameof(Strings.Filters), Name = nameof(Strings.CalculationMode),
		Description = nameof(Strings.CalculationModeDescription), Order = 120)]
	public CalcMode CalcType
	{
		get => _type;
		set { _type = value; RecalculateValues(); }
	}

	#endregion

	#region Delta Filters ASIA

	[Display(GroupName = "Delta Filters ASIA", Name = "Enabled", Order = 200)]
	public bool AsiaEnabled
	{
		get => _asiaEnabled;
		set { _asiaEnabled = value; RecalculateValues(); }
	}

	[Display(GroupName = "Delta Filters ASIA", Name = "Δ+ mínimo (>=)", Order = 210)]
	public decimal AsiaPositiveMinDelta
	{
		get => _asiaPosMinDelta;
		set { _asiaPosMinDelta = value; RecalculateValues(); }
	}

	[Display(GroupName = "Delta Filters ASIA", Name = "Δ- máximo (<=)", Order = 220)]
	public decimal AsiaNegativeMaxDelta
	{
		get => _asiaNegMaxDelta;
		set { _asiaNegMaxDelta = value; RecalculateValues(); }
	}

	[Display(GroupName = "Delta Filters ASIA", Name = nameof(Strings.UseTimeFilter), Order = 230)]
	public bool AsiaUseTimeFilter
	{
		get => _asiaUseTimeFilter;
		set { _asiaUseTimeFilter = value; RecalculateValues(); }
	}

	[Display(GroupName = "Delta Filters ASIA", Name = nameof(Strings.TimeFrom), Order = 240)]
	public TimeSpan AsiaTimeFrom
	{
		get => _asiaTimeFrom;
		set { _asiaTimeFrom = value; RecalculateValues(); }
	}

	[Display(GroupName = "Delta Filters ASIA", Name = nameof(Strings.TimeTo), Order = 250)]
	public TimeSpan AsiaTimeTo
	{
		get => _asiaTimeTo;
		set { _asiaTimeTo = value; RecalculateValues(); }
	}

	#endregion

	#region Delta Filters LONDON

	[Display(GroupName = "Delta Filters LONDON", Name = "Enabled", Order = 300)]
	public bool LondonEnabled
	{
		get => _ldnEnabled;
		set { _ldnEnabled = value; RecalculateValues(); }
	}

	[Display(GroupName = "Delta Filters LONDON", Name = "Δ+ mínimo (>=)", Order = 310)]
	public decimal LondonPositiveMinDelta
	{
		get => _ldnPosMinDelta;
		set { _ldnPosMinDelta = value; RecalculateValues(); }
	}

	[Display(GroupName = "Delta Filters LONDON", Name = "Δ- máximo (<=)", Order = 320)]
	public decimal LondonNegativeMaxDelta
	{
		get => _ldnNegMaxDelta;
		set { _ldnNegMaxDelta = value; RecalculateValues(); }
	}

	[Display(GroupName = "Delta Filters LONDON", Name = nameof(Strings.UseTimeFilter), Order = 330)]
	public bool LondonUseTimeFilter
	{
		get => _ldnUseTimeFilter;
		set { _ldnUseTimeFilter = value; RecalculateValues(); }
	}

	[Display(GroupName = "Delta Filters LONDON", Name = nameof(Strings.TimeFrom), Order = 340)]
	public TimeSpan LondonTimeFrom
	{
		get => _ldnTimeFrom;
		set { _ldnTimeFrom = value; RecalculateValues(); }
	}

	[Display(GroupName = "Delta Filters LONDON", Name = nameof(Strings.TimeTo), Order = 350)]
	public TimeSpan LondonTimeTo
	{
		get => _ldnTimeTo;
		set { _ldnTimeTo = value; RecalculateValues(); }
	}

	#endregion

	#region Delta Filters USA

	[Display(GroupName = "Delta Filters USA", Name = "Enabled", Order = 400)]
	public bool UsaEnabled
	{
		get => _usaEnabled;
		set { _usaEnabled = value; RecalculateValues(); }
	}

	[Display(GroupName = "Delta Filters USA", Name = "Δ+ mínimo (>=)", Order = 410)]
	public decimal UsaPositiveMinDelta
	{
		get => _usaPosMinDelta;
		set { _usaPosMinDelta = value; RecalculateValues(); }
	}

	[Display(GroupName = "Delta Filters USA", Name = "Δ- máximo (<=)", Order = 420)]
	public decimal UsaNegativeMaxDelta
	{
		get => _usaNegMaxDelta;
		set { _usaNegMaxDelta = value; RecalculateValues(); }
	}

	[Display(GroupName = "Delta Filters USA", Name = nameof(Strings.UseTimeFilter), Order = 430)]
	public bool UsaUseTimeFilter
	{
		get => _usaUseTimeFilter;
		set { _usaUseTimeFilter = value; RecalculateValues(); }
	}

	[Display(GroupName = "Delta Filters USA", Name = nameof(Strings.TimeFrom), Order = 440)]
	public TimeSpan UsaTimeFrom
	{
		get => _usaTimeFrom;
		set { _usaTimeFrom = value; RecalculateValues(); }
	}

	[Display(GroupName = "Delta Filters USA", Name = nameof(Strings.TimeTo), Order = 450)]
	public TimeSpan UsaTimeTo
	{
		get => _usaTimeTo;
		set { _usaTimeTo = value; RecalculateValues(); }
	}

	#endregion

	#region Visualization (+Δ) comunes

	[Display(GroupName = "Visualization (+Δ)", Name = "VisualMode", Order = 600)]
	public ObjectType PosVisualType
	{
		get => _posVisualType;
		set { _posVisualType = value; ApplyVisualToExisting(true, (ref PriceSelectionValue x) => x.VisualObject = value); }
	}

	[Display(GroupName = "Visualization (+Δ)", Name = "ObjectsColor", Order = 605)]
	public CrossColor PosObjectsColor
	{
		get => _posObjectColor;
		set { _posObjectColor = value; ApplyVisualToExisting(true, (ref PriceSelectionValue x) => x.ObjectColor = value); }
	}

	[Display(GroupName = "Visualization (+Δ)", Name = "Transparency", Order = 610)]
	[Range(0, 100)]
	public int PosObjectsTransparency
	{
		get => _posTransparency;
		set { _posTransparency = value; ApplyVisualToExisting(true, (ref PriceSelectionValue x) => x.ObjectsTransparency = value); }
	}

	[Display(GroupName = "Visualization (+Δ)", Name = "ShowPriceSelection", Order = 615)]
	public bool PosShowPriceSelection
	{
		get => _posShowPriceSelection;
		set { _posShowPriceSelection = value; ApplyVisualToExisting(true, (ref PriceSelectionValue x) => x.PriceSelectionColor = value ? _posPriceSelColor : CrossColors.Transparent); }
	}

	[Display(GroupName = "Visualization (+Δ)", Name = "PriceSelectionColor", Order = 620)]
	public CrossColor PosPriceSelectionColor
	{
		get => _posPriceSelColor;
		set { _posPriceSelColor = value; ApplyVisualToExisting(true, (ref PriceSelectionValue x) => x.PriceSelectionColor = _posShowPriceSelection ? value : CrossColors.Transparent); }
	}

	[Display(GroupName = "Visualization (+Δ)", Name = "FixedSizes", Order = 640)]
	public bool PosFixedSizes
	{
		get => _posFixedSizes;
		set { _posFixedSizes = value; RecalcSizesExisting(); }
	}

	[Range(1, int.MaxValue)]
	[Display(GroupName = "Visualization (+Δ)", Name = "Size", Order = 650)]
	public int PosSize
	{
		get => _posSize;
		set { _posSize = value; RecalcSizesExisting(); }
	}

	[Range(1, int.MaxValue)]
	[Display(GroupName = "Visualization (+Δ)", Name = "MinSize", Order = 660)]
	public int PosMinSize
	{
		get => _posMinSize;
		set { _posMinSize = value; RecalcSizesExisting(); }
	}

	[Range(1, int.MaxValue)]
	[Display(GroupName = "Visualization (+Δ)", Name = "MaxSize", Order = 670)]
	public int PosMaxSize
	{
		get => _posMaxSize;
		set { _posMaxSize = value; RecalcSizesExisting(); }
	}

	#endregion

	#region Visualization (-Δ) comunes

	[Display(GroupName = "Visualization (-Δ)", Name = "VisualMode", Order = 700)]
	public ObjectType NegVisualType
	{
		get => _negVisualType;
		set { _negVisualType = value; ApplyVisualToExisting(false, (ref PriceSelectionValue x) => x.VisualObject = value); }
	}

	[Display(GroupName = "Visualization (-Δ)", Name = "ObjectsColor", Order = 705)]
	public CrossColor NegObjectsColor
	{
		get => _negObjectColor;
		set { _negObjectColor = value; ApplyVisualToExisting(false, (ref PriceSelectionValue x) => x.ObjectColor = value); }
	}

	[Display(GroupName = "Visualization (-Δ)", Name = "Transparency", Order = 710)]
	[Range(0, 100)]
	public int NegObjectsTransparency
	{
		get => _negTransparency;
		set { _negTransparency = value; ApplyVisualToExisting(false, (ref PriceSelectionValue x) => x.ObjectsTransparency = value); }
	}

	[Display(GroupName = "Visualization (-Δ)", Name = "ShowPriceSelection", Order = 715)]
	public bool NegShowPriceSelection
	{
		get => _negShowPriceSelection;
		set { _negShowPriceSelection = value; ApplyVisualToExisting(false, (ref PriceSelectionValue x) => x.PriceSelectionColor = value ? _negPriceSelColor : CrossColors.Transparent); }
	}

	[Display(GroupName = "Visualization (-Δ)", Name = "PriceSelectionColor", Order = 720)]
	public CrossColor NegPriceSelectionColor
	{
		get => _negPriceSelColor;
		set { _negPriceSelColor = value; ApplyVisualToExisting(false, (ref PriceSelectionValue x) => x.PriceSelectionColor = _negShowPriceSelection ? value : CrossColors.Transparent); }
	}

	[Display(GroupName = "Visualization (-Δ)", Name = "FixedSizes", Order = 740)]
	public bool NegFixedSizes
	{
		get => _negFixedSizes;
		set { _negFixedSizes = value; RecalcSizesExisting(); }
	}

	[Range(1, int.MaxValue)]
	[Display(GroupName = "Visualization (-Δ)", Name = "Size", Order = 750)]
	public int NegSize
	{
		get => _negSize;
		set { _negSize = value; RecalcSizesExisting(); }
	}

	[Range(1, int.MaxValue)]
	[Display(GroupName = "Visualization (-Δ)", Name = "MinSize", Order = 760)]
	public int NegMinSize
	{
		get => _negMinSize;
		set { _negMinSize = value; RecalcSizesExisting(); }
	}

	[Range(1, int.MaxValue)]
	[Display(GroupName = "Visualization (-Δ)", Name = "MaxSize", Order = 770)]
	public int NegMaxSize
	{
		get => _negMaxSize;
		set { _negMaxSize = value; RecalcSizesExisting(); }
	}

	#endregion

	#region Value Label (+Δ) comunes

	[Display(GroupName = "Value Label (+Δ)", Name = "Show", Order = 800)]
	public bool PosShowValueLabel
	{
		get => _posShowLabel;
		set { _posShowLabel = value; RedrawChart(); }
	}

	[Display(GroupName = "Value Label (+Δ)", Name = "ShowPrefix", Order = 810)]
	public bool PosLabelShowPrefix
	{
		get => _posLabelShowPrefix;
		set { _posLabelShowPrefix = value; RedrawChart(); }
	}

	[Display(GroupName = "Value Label (+Δ)", Name = "Format", Order = 820)]
	public string PosLabelFormat
	{
		get => _posLabelFormat;
		set { _posLabelFormat = string.IsNullOrWhiteSpace(value) ? "N0" : value; RedrawChart(); }
	}

	[Display(GroupName = "Value Label (+Δ)", Name = "Color", Order = 830)]
	public CrossColor PosLabelColor
	{
		get => _posLabelColor;
		set { _posLabelColor = value; RedrawChart(); }
	}

	[Display(GroupName = "Value Label (+Δ)", Name = "FontSize", Order = 840)]
	[Range(6, 96)]
	public float PosLabelFontSize
	{
		get => _posLabelFontSize;
		set { _posLabelFontSize = Math.Max(6, Math.Min(96, value)); RedrawChart(); }
	}

	[Display(GroupName = "Value Label (+Δ)", Name = "Align", Order = 850)]
	public LabelAlign PosLabelTextAlign
	{
		get => _posLabelAlign;
		set { _posLabelAlign = value; RedrawChart(); }
	}

	[Display(GroupName = "Value Label (+Δ)", Name = "OffsetX", Order = 860)]
	[Range(-500, 500)]
	public int PosLabelOffsetXPx
	{
		get => _posLabelOffsetXPx;
		set { _posLabelOffsetXPx = Math.Max(-500, Math.Min(500, value)); RedrawChart(); }
	}

	#endregion

	#region Value Label (-Δ) comunes

	[Display(GroupName = "Value Label (-Δ)", Name = "Show", Order = 900)]
	public bool NegShowValueLabel
	{
		get => _negShowLabel;
		set { _negShowLabel = value; RedrawChart(); }
	}

	[Display(GroupName = "Value Label (-Δ)", Name = "ShowPrefix", Order = 910)]
	public bool NegLabelShowPrefix
	{
		get => _negLabelShowPrefix;
		set { _negLabelShowPrefix = value; RedrawChart(); }
	}

	[Display(GroupName = "Value Label (-Δ)", Name = "Format", Order = 920)]
	public string NegLabelFormat
	{
		get => _negLabelFormat;
		set { _negLabelFormat = string.IsNullOrWhiteSpace(value) ? "N0" : value; RedrawChart(); }
	}

	[Display(GroupName = "Value Label (-Δ)", Name = "Color", Order = 930)]
	public CrossColor NegLabelColor
	{
		get => _negLabelColor;
		set { _negLabelColor = value; RedrawChart(); }
	}

	[Display(GroupName = "Value Label (-Δ)", Name = "FontSize", Order = 940)]
	[Range(6, 96)]
	public float NegLabelFontSize
	{
		get => _negLabelFontSize;
		set { _negLabelFontSize = Math.Max(6, Math.Min(96, value)); RedrawChart(); }
	}

	[Display(GroupName = "Value Label (-Δ)", Name = "Align", Order = 950)]
	public LabelAlign NegLabelTextAlign
	{
		get => _negLabelAlign;
		set { _negLabelAlign = value; RedrawChart(); }
	}

	[Display(GroupName = "Value Label (-Δ)", Name = "OffsetX", Order = 960)]
	[Range(-500, 500)]
	public int NegLabelOffsetXPx
	{
		get => _negLabelOffsetXPx;
		set { _negLabelOffsetXPx = Math.Max(-500, Math.Min(500, value)); RedrawChart(); }
	}

	#endregion

	#region Apply/resize helpers

	private delegate void Mut(ref PriceSelectionValue x);

	private void ApplyVisualToExisting(bool positive, Mut mut)
	{
		for (var i = 0; i < _renderDataSeries.Count; i++)
		{
			var list = _renderDataSeries[i];
			for (var j = 0; j < list.Count; j++)
			{
				var x = list[j];
				var ctx = ToDecimalSafe(x.Context); // object -> decimal

				// Context > 0 => POS; < 0 => NEG
				if ((ctx >= 0m) == positive)
				{
					mut(ref x);
					list[j] = x;
				}
			}
		}
		RedrawChart();
	}

	private void RecalcSizesExisting()
	{
		// Denominadores por lado basados en los umbrales mínimos entre sesiones (escala estable)
		var posDenom = Math.Max(1m, Math.Min(_asiaPosMinDelta, Math.Min(_ldnPosMinDelta, _usaPosMinDelta)));
		var negDenom = Math.Max(1m, Math.Min(Math.Abs(_asiaNegMaxDelta), Math.Min(Math.Abs(_ldnNegMaxDelta), Math.Abs(_usaNegMaxDelta))));

		for (var i = 0; i < _renderDataSeries.Count; i++)
		{
			var list = _renderDataSeries[i];
			for (var j = 0; j < list.Count; j++)
			{
				var x = list[j];
				var ctx = ToDecimalSafe(x.Context); // object -> decimal

				var isPositive = ctx >= 0m;
				var abs = Math.Abs(ctx);

				var fixedSizes = isPositive ? _posFixedSizes : _negFixedSizes;
				var sizeBase = isPositive ? _posSize : _negSize;
				var minSize = isPositive ? _posMinSize : _negMinSize;
				var maxSize = isPositive ? _posMaxSize : _negMaxSize;

				var denom = isPositive ? posDenom : negDenom;

				var size = fixedSizes ? sizeBase : (int)(abs * sizeBase / denom);
				if (!fixedSizes)
				{
					if (size < minSize) size = minSize;
					if (size > maxSize) size = maxSize;
				}

				x.Size = size;
				list[j] = x;
			}
		}
		RedrawChart();
	}

	#endregion
}