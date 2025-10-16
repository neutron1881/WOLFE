using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using ATAS.Indicators;
using ATAS.Indicators.Drawing;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using CrossColor = System.Windows.Media.Color;

namespace ATAS.Indicators.Technical
{
	[DisplayName("NQ Skew (Aux)")]
	[Category(IndicatorCategories.VolumeOrderFlow)]
	public class NQ_Skew_Indicator : Indicator
	{
		#region Data structures

		private sealed class SkewRecord
		{
			public DateTime Timestamp { get; set; }
			public decimal? Skew { get; set; }
			public decimal? TotalDeltaTT { get; set; }
			public decimal? TotGEXOI { get; set; }
		}

		#endregion

		#region Series

		private readonly ValueDataSeries _skewSeries = new("Skew", "Skew")
		{
			VisualType = VisualMode.Line,
			Color = CrossColor.FromArgb(255, 100, 200, 255),
			ShowCurrentValue = false,
			ShowZeroValue = false
		};

		private readonly ValueDataSeries _zeroSeries = new("Zero", "Zero")
		{
			VisualType = VisualMode.Line,
			Color = CrossColor.FromArgb(200, 160, 160, 160),
			ShowCurrentValue = false,
			ShowZeroValue = false
		};

		private readonly ValueDataSeries _deltaSeries = new("Delta", "Delta")
		{
			VisualType = VisualMode.Line,
			Color = CrossColor.FromArgb(255, 255, 180, 60),
			ShowCurrentValue = false,
			ShowZeroValue = false
		};

		private readonly ValueDataSeries _gexSeries = new("Gex", "Gex")
		{
			VisualType = VisualMode.Line,
			Color = CrossColor.FromArgb(255, 255, 255, 255),
			ShowCurrentValue = false,
			ShowZeroValue = false
		};

		#endregion

		#region Fields

		private readonly object _sync = new();
		private readonly List<SkewRecord> _records = new();

		private FileSystemWatcher? _watcher;
		private Timer? _reloadDebounce;
		private volatile bool _forceReload;
		private volatile bool _loadedOnce;

		// Fuente para etiquetas derechas
		private RenderFont _labelFont = new("Segoe UI", 10);

		// Guarda el último VisualType no oculto para restaurar al re-mostrar
		private VisualMode _skewPrevVisual = VisualMode.Line;
		private VisualMode _zeroPrevVisual = VisualMode.Line;
		private VisualMode _deltaPrevVisual = VisualMode.Line;
		private VisualMode _gexPrevVisual = VisualMode.Line;

		#endregion

		#region Settings

		private string _filePath = @"C:\Users\jsest\Documents\csv\NQ.txt";
		[Display(GroupName = "1. Settings", Name = "File Path", Order = 0)]
		public string FilePath
		{
			get => _filePath;
			set
			{
				_filePath = value ?? string.Empty;
				SetupWatcher();
				_forceReload = true;
				RecalculateValues();
			}
		}

		private bool _autoReload = true;
		[Display(GroupName = "1. Settings", Name = "Auto Reload (file changes)", Order = 1)]
		public bool AutoReload
		{
			get => _autoReload;
			set
			{
				_autoReload = value;
				SetupWatcher();
			}
		}

		private DateTime? _fromDate;
		[Display(GroupName = "2. Date Range", Name = "From (optional)", Order = 10)]
		public DateTime? FromDate
		{
			get => _fromDate;
			set { _fromDate = value; RecalculateValues(); }
		}

		private DateTime? _toDate;
		[Display(GroupName = "2. Date Range", Name = "To (optional)", Order = 11)]
		public DateTime? ToDate
		{
			get => _toDate;
			set { _toDate = value; RecalculateValues(); }
		}

		private bool _restrictToChartDay = true;
		[Display(GroupName = "3. Behavior", Name = "Restrict to bar day", Order = 19)]
		public bool RestrictToChartDay
		{
			get => _restrictToChartDay;
			set { _restrictToChartDay = value; RecalculateValues(); }
		}

		private bool _holdPrevious = true;
		[Display(GroupName = "3. Behavior", Name = "Hold previous value", Order = 20)]
		public bool HoldPrevious
		{
			get => _holdPrevious;
			set { _holdPrevious = value; RecalculateValues(); }
		}

		private bool _showZero = true;
		[Display(GroupName = "3. Behavior", Name = "Show zero line", Order = 21)]
		public bool ShowZeroLine
		{
			get => _showZero;
			set
			{
				_showZero = value;
				ApplyShowAsHide();
			}
		}

		private bool _showSkew = true;
		[Display(GroupName = "3. Behavior", Name = "Show Skew", Order = 22)]
		public bool ShowSkew
		{
			get => _showSkew;
			set
			{
				_showSkew = value;
				ApplyShowAsHide();
			}
		}

		private bool _showDelta = true;
		[Display(GroupName = "3. Behavior", Name = "Show Delta", Order = 23)]
		public bool ShowDelta
		{
			get => _showDelta;
			set
			{
				_showDelta = value;
				ApplyShowAsHide();
			}
		}

		private bool _showGex = true;
		[Display(GroupName = "3. Behavior", Name = "Show Gex", Order = 24)]
		public bool ShowGex
		{
			get => _showGex;
			set
			{
				_showGex = value;
				ApplyShowAsHide();
			}
		}

		// Escalado (compartido por Delta y Gex) respecto al rango de Skew
		public enum TdttScaleMode { Raw, ScaleToSkewRange }
		private TdttScaleMode _tdttScale = TdttScaleMode.ScaleToSkewRange;

		[Display(GroupName = "4. Visual", Name = "Extras scale mode", Order = 30)]
		public TdttScaleMode TotalDeltaTTScale
		{
			get => _tdttScale;
			set { _tdttScale = value; RecalculateValues(); }
		}

		private decimal _tdttOccupancy = 0.8m;
		[Range(typeof(decimal), "0.10", "2.00")]
		[Display(GroupName = "4. Visual", Name = "Extras occupancy (0.1-2.0)", Order = 31)]
		public decimal TotalDeltaTTOccupancy
		{
			get => _tdttOccupancy;
			set
			{
				var clamped = Math.Clamp(value, 0.10m, 2.00m);
				if (_tdttOccupancy == clamped)
					return;

				_tdttOccupancy = clamped;
				RecalculateValues();
			}
		}

		// Etiquetas derechas
		private bool _showRightLabels = true;
		[Display(GroupName = "4. Visual", Name = "Show right labels", Order = 47)]
		public bool ShowRightLabels
		{
			get => _showRightLabels;
			set { _showRightLabels = value; RedrawChart(); }
		}

		private int _labelFontSize = 10;
		[Range(6, 32)]
		[Display(GroupName = "4. Visual", Name = "Label font size", Order = 48)]
		public int LabelFontSize
		{
			get => _labelFontSize;
			set
			{
				var v = Math.Clamp(value, 6, 32);
				if (_labelFontSize == v) return;
				_labelFontSize = v;
				_labelFont = new RenderFont("Segoe UI", _labelFontSize);
				RedrawChart();
			}
		}

		// Posición de etiquetas
		[Display(GroupName = "4. Visual", Name = "Labels X (px from left)", Order = 49)]
		public int LabelsX { get; set; } = 8;

		[Display(GroupName = "4. Visual", Name = "Labels Y (px from top)", Order = 50)]
		public int LabelsY { get; set; } = 6;

		[Display(GroupName = "4. Visual", Name = "Labels Gap (px)", Order = 51)]
		public int LabelsGap { get; set; } = 2;

		// 3) Behavior: desfase temporal (minutos)
		private int _timeOffsetMinutes = 0;

		[Range(-1440, 1440)]
		[Display(GroupName = "3. Behavior", Name = "Time offset (minutes)", Order = 25)]
		public int TimeOffsetMinutes
		{
			get => _timeOffsetMinutes;
			set
			{
				var v = Math.Clamp(value, -1440, 1440);
				if (_timeOffsetMinutes == v)
					return;
				_timeOffsetMinutes = v;
				RecalculateValues();
			}
		}

		#endregion

		#region Ctor / Initialize / Dispose

		public NQ_Skew_Indicator()
			: base(true)
		{
			DenyToChangePanel = false;
			DrawAbovePrice = false;

			// Habilitar dibujo personalizado y asegurarnos de pintar en histórico y pase final
			EnableCustomDrawing = true;
			SubscribeToDrawingEvents(DrawingLayouts.Historical | DrawingLayouts.Final);

			DataSeries[0] = _skewSeries;
			DataSeries.Add(_zeroSeries);
			DataSeries.Add(_deltaSeries);
			DataSeries.Add(_gexSeries);
		}

		protected override void OnInitialize()
		{
			base.OnInitialize();
			// Inicializa "prevVisual" con el tipo actual de cada serie si no está oculto.
			CaptureCurrentVisuals();
			ApplyShowAsHide();
			SetupWatcher();
			TryLoadFile();
			_loadedOnce = _records.Count > 0;
		}

		protected override void OnDispose()
		{
			try
			{
				_watcher?.Dispose();
				_reloadDebounce?.Dispose();
			}
			catch { }
			base.OnDispose();
		}

		#endregion

		#region Show/Hide via VisualType

		private void CaptureCurrentVisuals()
		{
			if (_skewSeries.VisualType != VisualMode.Hide) _skewPrevVisual = _skewSeries.VisualType;
			if (_zeroSeries.VisualType != VisualMode.Hide) _zeroPrevVisual = _zeroSeries.VisualType;
			if (_deltaSeries.VisualType != VisualMode.Hide) _deltaPrevVisual = _deltaSeries.VisualType;
			if (_gexSeries.VisualType != VisualMode.Hide) _gexPrevVisual = _gexSeries.VisualType;
		}

		private void ApplyShowAsHide()
		{
			// Guarda el último estilo visible antes de ocultar
			CaptureCurrentVisuals();

			_skewSeries.VisualType  = _showSkew  ? (_skewPrevVisual  == VisualMode.Hide ? VisualMode.Line : _skewPrevVisual)   : VisualMode.Hide;
			_zeroSeries.VisualType  = _showZero  ? (_zeroPrevVisual  == VisualMode.Hide ? VisualMode.Line : _zeroPrevVisual)   : VisualMode.Hide;
			_deltaSeries.VisualType = _showDelta ? (_deltaPrevVisual == VisualMode.Hide ? VisualMode.Line : _deltaPrevVisual)  : VisualMode.Hide;
			_gexSeries.VisualType   = _showGex   ? (_gexPrevVisual   == VisualMode.Hide ? VisualMode.Line : _gexPrevVisual)    : VisualMode.Hide;

			RedrawChart();
		}

		#endregion

		#region Calculate

		protected override void OnCalculate(int bar, decimal value)
		{
			if (_forceReload || !_loadedOnce)
			{
				TryLoadFile();
				_loadedOnce = _records.Count > 0;
				_forceReload = false;
			}

			_zeroSeries[bar] = 0m;

			var tOpen = GetCandle(bar).Time;
			DateTime? tNextOpen = bar + 1 < CurrentBar ? GetCandle(bar + 1).Time : (DateTime?)null;

			var offset = TimeSpan.FromMinutes(_timeOffsetMinutes);
			var qOpen = tOpen - offset;
			DateTime? qNext = tNextOpen.HasValue ? tNextOpen.Value - offset : (DateTime?)null;

			var skewRaw = GetValueByIntervalOrPrev(qOpen, qNext, r => r.Skew);
			_skewSeries[bar] = skewRaw ?? ((_holdPrevious && bar > 0) ? _skewSeries[bar - 1] : 0m);

			var tdttRaw = GetValueByIntervalOrPrev(qOpen, qNext, r => r.TotalDeltaTT);
			var gexRaw  = GetValueByIntervalOrPrev(qOpen, qNext, r => r.TotGEXOI);

			if (_tdttScale == TdttScaleMode.Raw)
			{
				_deltaSeries[bar] = tdttRaw.HasValue ? tdttRaw.Value : (bar > 0 ? _deltaSeries[bar - 1] : 0m);
				_gexSeries[bar]   = gexRaw.HasValue  ? gexRaw.Value  : (bar > 0 ? _gexSeries[bar - 1]  : 0m);
			}
			else
			{
				var dayList = GetFilteredListForTime(qOpen);
				if (dayList.Count == 0)
				{
					_deltaSeries[bar] = tdttRaw.HasValue ? tdttRaw.Value : (bar > 0 ? _deltaSeries[bar - 1] : 0m);
					_gexSeries[bar]   = gexRaw.HasValue  ? gexRaw.Value  : (bar > 0 ? _gexSeries[bar - 1]  : 0m);
				}
				else
				{
					var (skewRange, maxAbsTdtt, maxAbsGex) = ComputeDayScaling(dayList);
					if (skewRange <= 0m)
					{
						_deltaSeries[bar] = tdttRaw.HasValue ? tdttRaw.Value : (bar > 0 ? _deltaSeries[bar - 1] : 0m);
						_gexSeries[bar]   = gexRaw.HasValue  ? gexRaw.Value  : (bar > 0 ? _gexSeries[bar - 1]  : 0m);
					}
					else
					{
						var targetHalfRange = (skewRange * _tdttOccupancy) / 2m;

						if (tdttRaw.HasValue && maxAbsTdtt > 0m)
							_deltaSeries[bar] = tdttRaw.Value * (targetHalfRange / maxAbsTdtt);
						else
							_deltaSeries[bar] = bar > 0 ? _deltaSeries[bar - 1] : 0m;

						if (gexRaw.HasValue && maxAbsGex > 0m)
							_gexSeries[bar] = gexRaw.Value * (targetHalfRange / maxAbsGex);
						else
							_gexSeries[bar] = bar > 0 ? _gexSeries[bar - 1] : 0m;
					}
				}
			}
		}

		#endregion

		#region Render

		protected override void OnRender(RenderContext context, DrawingLayouts layout)
		{
			// Las series se dibujan solas; aquí añadimos etiquetas del panel del indicador.
			if (!_showRightLabels || _labelFont == null || ChartInfo == null)
				return;

			// 1) Detectar si este render pertenece al panel de precio. Si es así, no dibujar etiquetas.
			var myRegion = ChartInfo.Region; // región del panel actual (este indicador)
			var priceRegion = ChartInfo.PriceChartContainer?.Region ?? System.Drawing.Rectangle.Empty;

			bool isPricePanel =
				priceRegion != System.Drawing.Rectangle.Empty &&
				myRegion.X == priceRegion.X &&
				myRegion.Y == priceRegion.Y &&
				myRegion.Width == priceRegion.Width &&
				myRegion.Height == priceRegion.Height;

			if (isPricePanel)
				return;

			// 2) Dibujar con coordenadas relativas al panel del indicador (no a la gráfica de precios).
			int x = myRegion.X + Math.Max(0, LabelsX);
			int y = myRegion.Y + Math.Max(0, LabelsY);
			int gap = Math.Max(0, LabelsGap);

			System.Drawing.Color ToDrawingColor(CrossColor c)
				=> System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);

			void DrawLabel(string text, CrossColor color)
			{
				var size = context.MeasureString(text, _labelFont);
				context.DrawString(text, _labelFont, ToDrawingColor(color), x, y);
				y += size.Height + gap;
			}

			if (_skewSeries.VisualType != VisualMode.Hide)
				DrawLabel("Skew", _skewSeries.Color);

			if (_deltaSeries.VisualType != VisualMode.Hide)
				DrawLabel("Delta", _deltaSeries.Color);

			if (_gexSeries.VisualType != VisualMode.Hide)
				DrawLabel("Gex", _gexSeries.Color);
		}

		#endregion

		#region Helpers: scaling

		private static (decimal skewRange, decimal maxAbsTdtt, decimal maxAbsGex) ComputeDayScaling(List<SkewRecord> dayList)
		{
			var skewVals = dayList.Where(r => r.Skew.HasValue).Select(r => r.Skew!.Value).ToList();
			decimal skewRange = 0m;
			if (skewVals.Count > 0)
			{
				var min = skewVals.Min();
				var max = skewVals.Max();
				skewRange = max - min;
			}

			var maxAbsTdtt = dayList.Where(r => r.TotalDeltaTT.HasValue).Select(r => Math.Abs(r.TotalDeltaTT!.Value)).DefaultIfEmpty(0m).Max();
			var maxAbsGex  = dayList.Where(r => r.TotGEXOI.HasValue).Select(r => Math.Abs(r.TotGEXOI!.Value)).DefaultIfEmpty(0m).Max();

			return (skewRange, maxAbsTdtt, maxAbsGex);
		}

		#endregion

		#region Data selection

		private decimal? GetValueByIntervalOrPrev(DateTime barOpen, DateTime? nextOpen, Func<SkewRecord, decimal?> selector)
		{
			var list = GetFilteredListForTime(barOpen);
			if (list.Count == 0)
				return null;

			if (nextOpen.HasValue)
			{
				int lb = LowerBound(list, barOpen);
				int ub = LowerBound(list, nextOpen.Value);
				int idxIn = ub - 1;
				if (idxIn >= lb)
					return selector(list[idxIn]);

				int idxPrev = lb - 1;
				return idxPrev >= 0 ? selector(list[idxPrev]) : (decimal?)null;
			}

			int idx = FindLastIndexByTime(list, barOpen);
			return idx >= 0 ? selector(list[idx]) : (decimal?)null;
		}

		private List<SkewRecord> GetFilteredListForTime(DateTime time)
		{
			List<SkewRecord> list;
			lock (_sync)
			{
				list = ApplyDateFilterUnsafe(_records, _fromDate, _toDate);
			}
			if (_restrictToChartDay)
				list = list.Where(r => r.Timestamp.Date == time.Date).ToList();
			return list;
		}

		private static int FindLastIndexByTime(List<SkewRecord> list, DateTime time)
		{
			int lo = 0, hi = list.Count - 1, ans = -1;
			while (lo <= hi)
			{
				int mid = (lo + hi) / 2;
				var ts = list[mid].Timestamp;
				if (ts <= time) { ans = mid; lo = mid + 1; }
				else hi = mid - 1;
			}
			return ans;
		}

		private static int FindIndexByInterval(List<SkewRecord> list, DateTime barOpen, DateTime? nextOpen)
		{
			if (nextOpen.HasValue)
			{
				int lb = LowerBound(list, barOpen);
				int ub = LowerBound(list, nextOpen.Value);
				int idx = ub - 1;
				return idx >= lb ? idx : -1;
			}
			return FindLastIndexByTime(list, barOpen);
		}

		private static int LowerBound(List<SkewRecord> list, DateTime time)
		{
			int lo = 0, hi = list.Count;
			while (lo < hi)
			{
				int mid = (lo + hi) / 2;
				if (list[mid].Timestamp >= time) hi = mid;
				else lo = mid + 1;
			}
			return lo;
		}

		private static List<SkewRecord> ApplyDateFilterUnsafe(List<SkewRecord> input, DateTime? from, DateTime? to)
		{
			IEnumerable<SkewRecord> q = input;
			if (from.HasValue)
				q = q.Where(r => r.Timestamp >= from.Value);
			if (to.HasValue)
				q = q.Where(r => r.Timestamp <= to.Value);
			return q.OrderBy(r => r.Timestamp).ToList();
		}

		#endregion

		#region File load / parse

		private void SetupWatcher()
		{
			try
			{
				_watcher?.Dispose();
				_watcher = null;

				_reloadDebounce?.Dispose();
				_reloadDebounce = null;

				if (!_autoReload || string.IsNullOrWhiteSpace(_filePath))
					return;

				var dir = Path.GetDirectoryName(_filePath);
				var file = Path.GetFileName(_filePath);
				if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(file) || !Directory.Exists(dir))
					return;

				_watcher = new FileSystemWatcher(dir, file)
				{
					NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
				};
				_watcher.Changed += (_, __) => DebounceReload();
				_watcher.Created += (_, __) => DebounceReload();
				_watcher.Renamed += (_, __) => DebounceReload();
				_watcher.EnableRaisingEvents = true;

				_reloadDebounce = new Timer(_ =>
				{
					_forceReload = true;
					RedrawChart();
				}, null, Timeout.Infinite, Timeout.Infinite);
			}
			catch { }
		}

		private void DebounceReload()
		{
			try { _reloadDebounce?.Change(300, Timeout.Infinite); }
			catch { }
		}

		private void TryLoadFile()
		{
			if (string.IsNullOrWhiteSpace(_filePath) || !File.Exists(_filePath))
				return;

			List<SkewRecord> parsed;
			try
			{
				var text = File.ReadAllText(_filePath);
				parsed = ParseSkew(text);
			}
			catch
			{
				return;
			}

			lock (_sync)
			{
				_records.Clear();
				_records.AddRange(parsed.OrderBy(r => r.Timestamp));
			}
			RedrawChart();
		}

		private static readonly Regex RxTimestamp =
			new(@"(?<ts>\d{1,2}[/\-]\d{1,2}[/\-]\d{2,4}\s+\d{1,2}:\d{2}:\d{2})", RegexOptions.Compiled);

		private static readonly Regex RxPair =
			new(@"(?<key>[A-Za-z]+(?:[-/][A-Za-z]+)*)\s*[:\-]?\s*(?<val>[-+]?\d+(?:\.\d+)?)", RegexOptions.Compiled);

		private List<SkewRecord> ParseSkew(string content)
		{
			var lines = content.Replace("\r", "").Split('\n');
			var recs = new List<SkewRecord>();

			DateTime? currentTs = null;
			decimal? currentSkew = null;
			decimal? currentTdtt = null;
			decimal? currentGex = null;

			void Flush()
			{
				if (currentTs.HasValue)
					recs.Add(new SkewRecord { Timestamp = currentTs.Value, Skew = currentSkew, TotalDeltaTT = currentTdtt, TotGEXOI = currentGex });
				currentTs = null;
				currentSkew = null;
				currentTdtt = null;
				currentGex = null;
			}

			foreach (var raw in lines)
			{
				var line = raw?.Trim();
				if (string.IsNullOrEmpty(line))
					continue;

				var m = RxTimestamp.Match(line);
				if (m.Success)
				{
					Flush();
					currentTs = ParseTimestamp(m.Groups["ts"].Value);

					foreach (Match pm in RxPair.Matches(line))
					{
						var key = pm.Groups["key"].Value.Trim();
						var vs = pm.Groups["val"].Value;
						var norm = NormalizeKey(key);
						if (norm == "SKEW")
						{
							if (decimal.TryParse(vs, NumberStyles.Any, CultureInfo.InvariantCulture, out var sv))
								currentSkew = sv;
						}
						else if (norm == "TOTALDELTATT")
						{
							if (decimal.TryParse(vs, NumberStyles.Any, CultureInfo.InvariantCulture, out var tv))
								currentTdtt = tv;
						}
						else if (norm == "TOTGEXOI")
						{
							if (decimal.TryParse(vs, NumberStyles.Any, CultureInfo.InvariantCulture, out var gv))
								currentGex = gv;
						}
					}
				}
				else if (currentTs.HasValue)
				{
					foreach (Match pm in RxPair.Matches(line))
					{
						var key = pm.Groups["key"].Value.Trim();
						var vs = pm.Groups["val"].Value;
						var norm = NormalizeKey(key);
						if (norm == "SKEW")
						{
							if (decimal.TryParse(vs, NumberStyles.Any, CultureInfo.InvariantCulture, out var sv))
								currentSkew = sv;
						}
						else if (norm == "TOTALDELTATT")
						{
							if (decimal.TryParse(vs, NumberStyles.Any, CultureInfo.InvariantCulture, out var tv))
								currentTdtt = tv;
						}
						else if (norm == "TOTGEXOI")
						{
							if (decimal.TryParse(vs, NumberStyles.Any, CultureInfo.InvariantCulture, out var gv))
								currentGex = gv;
						}
					}
				}
			}
			Flush();

			return recs;
		}

		private static string NormalizeKey(string key)
		{
			var sb = new System.Text.StringBuilder(key.Length);
			foreach (var ch in key)
			{
				if (char.IsLetterOrDigit(ch))
					sb.Append(char.ToUpperInvariant(ch));
			}
			return sb.ToString();
		}

		private static DateTime ParseTimestamp(string ts)
		{
			string[] fmts =
			{
				"MM'/'dd'/'yyyy'\t'HH':'mm':'ss",
				"M'/'d'/'yyyy'\t'HH':'mm':'ss",
				"MM'/'dd'/'yyyy' 'HH':'mm':'ss",
				"M'/'d'/'yyyy' 'HH':'mm':'ss"
			};

			if (DateTime.TryParseExact(
					ts,
					fmts,
					CultureInfo.InvariantCulture,
					DateTimeStyles.AssumeLocal,
					out DateTime dt))
				return dt;

			var normalized = ts?.Replace('\t', ' ')?.Trim();
			if (!string.IsNullOrEmpty(normalized) &&
				DateTime.TryParseExact(
					normalized,
					"MM'/'dd'/'yyyy' 'HH':'mm':'ss",
					CultureInfo.InvariantCulture,
					DateTimeStyles.AssumeLocal,
					out DateTime dt2))
				return dt2;

			if (!string.IsNullOrEmpty(ts) &&
				DateTime.TryParse(
					ts,
					CultureInfo.CurrentCulture,
					DateTimeStyles.AssumeLocal,
					out DateTime any))
				return any;

			return DateTime.MinValue;
		}

		#endregion
	}
}