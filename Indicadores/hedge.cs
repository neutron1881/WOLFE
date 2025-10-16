using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
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
	[DisplayName("NQ OI / TT Levels (Hedge)")]
	[Category(IndicatorCategories.VolumeOrderFlow)]
	public class NQ_OI_TT_Indicator : Indicator
	{
		#region Data model

		private sealed class NqRecord
		{
			public DateTime Timestamp { get; set; }

			// OI levels
			public decimal? PG_OI { get; set; }
			public decimal? FG_OI { get; set; }
			public decimal? ZG_OI { get; set; }
			public decimal? FR_OI { get; set; }
			public decimal? NG_OI { get; set; }

			// TT levels
			public decimal? PG_TT { get; set; }
			public decimal? FG_TT { get; set; }
			public decimal? ZG_TT { get; set; }
			public decimal? FR_TT { get; set; }
			public decimal? NG_TT { get; set; }

			// Extras
			public decimal? EM_HI { get; set; }
			public decimal? EM_LO { get; set; }
			public decimal? TotGEXOI { get; set; }
			public decimal? TotalDelta_OI { get; set; }
			public decimal? AbsGEX_OI { get; set; }
			public decimal? AbsGEX_TT { get; set; }
			public decimal? Skew { get; set; }
			public decimal? PCR { get; set; }

			public string RawBlock { get; set; } = string.Empty;
		}

		#endregion

		#region Fields

		private readonly ValueDataSeries _noop = new("RenderPoke", "RenderPoke") { IsHidden = true };
		private readonly List<NqRecord> _records = new();
		private readonly object _sync = new();

		private FileSystemWatcher? _watcher;
		private Timer? _reloadDebounce;
		private volatile bool _forceReload;
		private volatile bool _loadedOnce;

		// Cache de fuentes
		private readonly Dictionary<int, RenderFont> _fontCache = new();

		#endregion

		#region Config: archivo y filtro

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
				RedrawChart();
			}
		}

		private string _symbolFilter = "NQ";
		[Display(GroupName = "1. Settings", Name = "Symbol Filter", Order = 1)]
		public string SymbolFilter
		{
			get => _symbolFilter;
			set
			{
				_symbolFilter = value ?? string.Empty;
				_forceReload = true;
				RedrawChart();
			}
		}

		private bool _autoReload = true;
		[Display(GroupName = "1. Settings", Name = "Auto Reload (file changes)", Order = 2)]
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
		[Display(GroupName = "3. Date Range", Name = "From (optional)", Order = 3)]
		public DateTime? FromDate
		{
			get => _fromDate;
			set { _fromDate = value; RedrawChart(); }
		}

		private DateTime? _toDate;
		[Display(GroupName = "3. Date Range", Name = "To (optional)", Order = 4)]
		public DateTime? ToDate
		{
			get => _toDate;
			set { _toDate = value; RedrawChart(); }
		}

		#endregion

		#region Config: visualización

		private bool _showOI = true;
		[Display(GroupName = "2. Appearance", Name = "Show OI Levels", Order = 10)]
		public bool ShowOI { get => _showOI; set { _showOI = value; RedrawChart(); } }

		private bool _showTT = true;
		[Display(GroupName = "2. Appearance", Name = "Show TT Levels", Order = 11)]
		public bool ShowTT { get => _showTT; set { _showTT = value; RedrawChart(); } }

		private bool _showEMRange = true;
		[Display(GroupName = "2. Appearance", Name = "Show EM Range", Order = 12)]
		public bool ShowEMRange { get => _showEMRange; set { _showEMRange = value; RedrawChart(); } }

		private bool _levelsFullWidth = false;
		[Display(GroupName = "2. Appearance", Name = "Full-width lines", Order = 13)]
		public bool LevelsFullWidth { get => _levelsFullWidth; set { _levelsFullWidth = value; RedrawChart(); } }

		private int _lineThickness = 2;
		[Range(1, 8)]
		[Display(GroupName = "2. Appearance", Name = "Line thickness", Order = 14)]
		public int LineThickness { get => _lineThickness; set { _lineThickness = Math.Clamp(value, 1, 8); RedrawChart(); } }

		private int _labelFontSize = 10;
		[Range(6, 32)]
		[Display(GroupName = "2. Appearance", Name = "Label font size", Order = 15)]
		public int LabelFontSize { get => _labelFontSize; set { _labelFontSize = Math.Clamp(value, 6, 32); RedrawChart(); } }

		// Colores OI
		private CrossColor _colorPG_OI = CrossColor.FromArgb(255, 0, 200, 0);
		private CrossColor _colorFG_OI = CrossColor.FromArgb(255, 60, 220, 60);
		private CrossColor _colorZG_OI = CrossColor.FromArgb(255, 160, 160, 160);
		private CrossColor _colorFR_OI = CrossColor.FromArgb(255, 220, 40, 40);
		private CrossColor _colorNG_OI = CrossColor.FromArgb(255, 160, 0, 0);

		[Display(GroupName = "4. Colors (OI)", Name = "PG", Order = 30)]
		public CrossColor ColorPG_OI { get => _colorPG_OI; set { _colorPG_OI = value; RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "FG", Order = 31)]
		public CrossColor ColorFG_OI { get => _colorFG_OI; set { _colorFG_OI = value; RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "ZG", Order = 32)]
		public CrossColor ColorZG_OI { get => _colorZG_OI; set { _colorZG_OI = value; RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "FR", Order = 33)]
		public CrossColor ColorFR_OI { get => _colorFR_OI; set { _colorFR_OI = value; RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "NG", Order = 34)]
		public CrossColor ColorNG_OI { get => _colorNG_OI; set { _colorNG_OI = value; RedrawChart(); } }

		// Estilos avanzados por nivel OI (como en Skew)
		// PG
		private bool _showPG_OI = true;
		private VisualMode _visualPG_OI = VisualMode.Line;
		private int _widthPG_OI = 0; // 0 = usa LineThickness
		private string _labelPG_OI = "PG-OI";
		private CrossColor _labelColorPG_OI = CrossColor.FromArgb(255, 0, 200, 0);

		[Display(GroupName = "4. Colors (OI)", Name = "PG Visible", Order = 130)]
		public bool ShowPG_OI { get => _showPG_OI; set { _showPG_OI = value; RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "PG VisualType", Order = 131)]
		public VisualMode VisualPG_OI { get => _visualPG_OI; set { _visualPG_OI = value; RedrawChart(); } }

		[Range(0, 8)]
		[Display(GroupName = "4. Colors (OI)", Name = "PG Width (0=global)", Order = 132)]
		public int WidthPG_OI { get => _widthPG_OI; set { _widthPG_OI = Math.Clamp(value, 0, 8); RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "PG Label", Order = 133)]
		public string LabelPG_OI { get => _labelPG_OI; set { _labelPG_OI = value ?? "PG-OI"; RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "PG Label Color", Order = 134)]
		public CrossColor LabelColorPG_OI { get => _labelColorPG_OI; set { _labelColorPG_OI = value; RedrawChart(); } }

		// FG
		private bool _showFG_OI = true;
		private VisualMode _visualFG_OI = VisualMode.Line;
		private int _widthFG_OI = 0;
		private string _labelFG_OI = "FG-OI";
		private CrossColor _labelColorFG_OI = CrossColor.FromArgb(255, 60, 220, 60);

		[Display(GroupName = "4. Colors (OI)", Name = "FG Visible", Order = 135)]
		public bool ShowFG_OI { get => _showFG_OI; set { _showFG_OI = value; RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "FG VisualType", Order = 136)]
		public VisualMode VisualFG_OI { get => _visualFG_OI; set { _visualFG_OI = value; RedrawChart(); } }

		[Range(0, 8)]
		[Display(GroupName = "4. Colors (OI)", Name = "FG Width (0=global)", Order = 137)]
		public int WidthFG_OI { get => _widthFG_OI; set { _widthFG_OI = Math.Clamp(value, 0, 8); RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "FG Label", Order = 138)]
		public string LabelFG_OI { get => _labelFG_OI; set { _labelFG_OI = value ?? "FG-OI"; RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "FG Label Color", Order = 139)]
		public CrossColor LabelColorFG_OI { get => _labelColorFG_OI; set { _labelColorFG_OI = value; RedrawChart(); } }

		// ZG
		private bool _showZG_OI = true;
		private VisualMode _visualZG_OI = VisualMode.Line;
		private int _widthZG_OI = 0;
		private string _labelZG_OI = "ZG-OI";
		private CrossColor _labelColorZG_OI = CrossColor.FromArgb(255, 160, 160, 160);

		[Display(GroupName = "4. Colors (OI)", Name = "ZG Visible", Order = 140)]
		public bool ShowZG_OI { get => _showZG_OI; set { _showZG_OI = value; RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "ZG VisualType", Order = 141)]
		public VisualMode VisualZG_OI { get => _visualZG_OI; set { _visualZG_OI = value; RedrawChart(); } }

		[Range(0, 8)]
		[Display(GroupName = "4. Colors (OI)", Name = "ZG Width (0=global)", Order = 142)]
		public int WidthZG_OI { get => _widthZG_OI; set { _widthZG_OI = Math.Clamp(value, 0, 8); RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "ZG Label", Order = 143)]
		public string LabelZG_OI { get => _labelZG_OI; set { _labelZG_OI = value ?? "ZG-OI"; RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "ZG Label Color", Order = 144)]
		public CrossColor LabelColorZG_OI { get => _labelColorZG_OI; set { _labelColorZG_OI = value; RedrawChart(); } }

		// FR
		private bool _showFR_OI = true;
		private VisualMode _visualFR_OI = VisualMode.Line;
		private int _widthFR_OI = 0;
		private string _labelFR_OI = "FR-OI";
		private CrossColor _labelColorFR_OI = CrossColor.FromArgb(255, 220, 40, 40);

		[Display(GroupName = "4. Colors (OI)", Name = "FR Visible", Order = 145)]
		public bool ShowFR_OI { get => _showFR_OI; set { _showFR_OI = value; RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "FR VisualType", Order = 146)]
		public VisualMode VisualFR_OI { get => _visualFR_OI; set { _visualFR_OI = value; RedrawChart(); } }

		[Range(0, 8)]
		[Display(GroupName = "4. Colors (OI)", Name = "FR Width (0=global)", Order = 147)]
		public int WidthFR_OI { get => _widthFR_OI; set { _widthFR_OI = Math.Clamp(value, 0, 8); RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "FR Label", Order = 148)]
		public string LabelFR_OI { get => _labelFR_OI; set { _labelFR_OI = value ?? "FR-OI"; RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "FR Label Color", Order = 149)]
		public CrossColor LabelColorFR_OI { get => _labelColorFR_OI; set { _labelColorFR_OI = value; RedrawChart(); } }

		// NG
		private bool _showNG_OI = true;
		private VisualMode _visualNG_OI = VisualMode.Line;
		private int _widthNG_OI = 0;
		private string _labelNG_OI = "NG-OI";
		private CrossColor _labelColorNG_OI = CrossColor.FromArgb(255, 160, 0, 0);

		[Display(GroupName = "4. Colors (OI)", Name = "NG Visible", Order = 150)]
		public bool ShowNG_OI { get => _showNG_OI; set { _showNG_OI = value; RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "NG VisualType", Order = 151)]
		public VisualMode VisualNG_OI { get => _visualNG_OI; set { _visualNG_OI = value; RedrawChart(); } }

		[Range(0, 8)]
		[Display(GroupName = "4. Colors (OI)", Name = "NG Width (0=global)", Order = 152)]
		public int WidthNG_OI { get => _widthNG_OI; set { _widthNG_OI = Math.Clamp(value, 0, 8); RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "NG Label", Order = 153)]
		public string LabelNG_OI { get => _labelNG_OI; set { _labelNG_OI = value ?? "NG-OI"; RedrawChart(); } }

		[Display(GroupName = "4. Colors (OI)", Name = "NG Label Color", Order = 154)]
		public CrossColor LabelColorNG_OI { get => _labelColorNG_OI; set { _labelColorNG_OI = value; RedrawChart(); } }

		// Colores TT
		private CrossColor _colorPG_TT = CrossColor.FromArgb(255, 40, 140, 255);
		private CrossColor _colorFG_TT = CrossColor.FromArgb(255, 80, 170, 255);
		private CrossColor _colorZG_TT = CrossColor.FromArgb(255, 120, 190, 255);
		private CrossColor _colorFR_TT = CrossColor.FromArgb(255, 255, 160, 40);
		private CrossColor _colorNG_TT = CrossColor.FromArgb(255, 255, 120, 0);

		[Display(GroupName = "5. Colors (TT)", Name = "PG", Order = 40)]
		public CrossColor ColorPG_TT { get => _colorPG_TT; set { _colorPG_TT = value; RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "FG", Order = 41)]
		public CrossColor ColorFG_TT { get => _colorFG_TT; set { _colorFG_TT = value; RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "ZG", Order = 42)]
		public CrossColor ColorZG_TT { get => _colorZG_TT; set { _colorZG_TT = value; RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "FR", Order = 43)]
		public CrossColor ColorFR_TT { get => _colorFR_TT; set { _colorFR_TT = value; RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "NG", Order = 44)]
		public CrossColor ColorNG_TT { get => _colorNG_TT; set { _colorNG_TT = value; RedrawChart(); } }

		private bool _ttDashed = true;
		[Display(GroupName = "5. Colors (TT)", Name = "Dashed TT", Order = 45)]
		public bool DashedTT { get => _ttDashed; set { _ttDashed = value; RedrawChart(); } }

		// Estilos avanzados por nivel TT
		// PG-TT
		private bool _showPG_TT = true;
		private VisualMode _visualPG_TT = VisualMode.Line;
		private int _widthPG_TT = 0; // 0 = usa LineThickness
		private string _labelPG_TT = "PG-TT";
		private CrossColor _labelColorPG_TT = CrossColor.FromArgb(255, 40, 140, 255);

		[Display(GroupName = "5. Colors (TT)", Name = "PG Visible", Order = 240)]
		public bool ShowPG_TT { get => _showPG_TT; set { _showPG_TT = value; RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "PG VisualType", Order = 241)]
		public VisualMode VisualPG_TT { get => _visualPG_TT; set { _visualPG_TT = value; RedrawChart(); } }

		[Range(0, 8)]
		[Display(GroupName = "5. Colors (TT)", Name = "PG Width (0=global)", Order = 242)]
		public int WidthPG_TT { get => _widthPG_TT; set { _widthPG_TT = Math.Clamp(value, 0, 8); RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "PG Label", Order = 243)]
		public string LabelPG_TT { get => _labelPG_TT; set { _labelPG_TT = value ?? "PG-TT"; RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "PG Label Color", Order = 244)]
		public CrossColor LabelColorPG_TT { get => _labelColorPG_TT; set { _labelColorPG_TT = value; RedrawChart(); } }

		// FG-TT
		private bool _showFG_TT = true;
		private VisualMode _visualFG_TT = VisualMode.Line;
		private int _widthFG_TT = 0;
		private string _labelFG_TT = "FG-TT";
		private CrossColor _labelColorFG_TT = CrossColor.FromArgb(255, 80, 170, 255);

		[Display(GroupName = "5. Colors (TT)", Name = "FG Visible", Order = 245)]
		public bool ShowFG_TT { get => _showFG_TT; set { _showFG_TT = value; RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "FG VisualType", Order = 246)]
		public VisualMode VisualFG_TT { get => _visualFG_TT; set { _visualFG_TT = value; RedrawChart(); } }

		[Range(0, 8)]
		[Display(GroupName = "5. Colors (TT)", Name = "FG Width (0=global)", Order = 247)]
		public int WidthFG_TT { get => _widthFG_TT; set { _widthFG_TT = Math.Clamp(value, 0, 8); RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "FG Label", Order = 248)]
		public string LabelFG_TT { get => _labelFG_TT; set { _labelFG_TT = value ?? "FG-TT"; RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "FG Label Color", Order = 249)]
		public CrossColor LabelColorFG_TT { get => _labelColorFG_TT; set { _labelColorFG_TT = value; RedrawChart(); } }

		// ZG-TT
		private bool _showZG_TT = true;
		private VisualMode _visualZG_TT = VisualMode.Line;
		private int _widthZG_TT = 0;
		private string _labelZG_TT = "ZG-TT";
		private CrossColor _labelColorZG_TT = CrossColor.FromArgb(255, 120, 190, 255);

		[Display(GroupName = "5. Colors (TT)", Name = "ZG Visible", Order = 250)]
		public bool ShowZG_TT { get => _showZG_TT; set { _showZG_TT = value; RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "ZG VisualType", Order = 251)]
		public VisualMode VisualZG_TT { get => _visualZG_TT; set { _visualZG_TT = value; RedrawChart(); } }

		[Range(0, 8)]
		[Display(GroupName = "5. Colors (TT)", Name = "ZG Width (0=global)", Order = 252)]
		public int WidthZG_TT { get => _widthZG_TT; set { _widthZG_TT = Math.Clamp(value, 0, 8); RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "ZG Label", Order = 253)]
		public string LabelZG_TT { get => _labelZG_TT; set { _labelZG_TT = value ?? "ZG-TT"; RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "ZG Label Color", Order = 254)]
		public CrossColor LabelColorZG_TT { get => _labelColorZG_TT; set { _labelColorZG_TT = value; RedrawChart(); } }

		// FR-TT
		private bool _showFR_TT = true;
		private VisualMode _visualFR_TT = VisualMode.Line;
		private int _widthFR_TT = 0;
		private string _labelFR_TT = "FR-TT";
		private CrossColor _labelColorFR_TT = CrossColor.FromArgb(255, 255, 160, 40);

		[Display(GroupName = "5. Colors (TT)", Name = "FR Visible", Order = 255)]
		public bool ShowFR_TT { get => _showFR_TT; set { _showFR_TT = value; RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "FR VisualType", Order = 256)]
		public VisualMode VisualFR_TT { get => _visualFR_TT; set { _visualFR_TT = value; RedrawChart(); } }

		[Range(0, 8)]
		[Display(GroupName = "5. Colors (TT)", Name = "FR Width (0=global)", Order = 257)]
		public int WidthFR_TT { get => _widthFR_TT; set { _widthFR_TT = Math.Clamp(value, 0, 8); RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "FR Label", Order = 258)]
		public string LabelFR_TT { get => _labelFR_TT; set { _labelFR_TT = value ?? "FR-TT"; RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "FR Label Color", Order = 259)]
		public CrossColor LabelColorFR_TT { get => _labelColorFR_TT; set { _labelColorFR_TT = value; RedrawChart(); } }

		// NG-TT
		private bool _showNG_TT = true;
		private VisualMode _visualNG_TT = VisualMode.Line;
		private int _widthNG_TT = 0;
		private string _labelNG_TT = "NG-TT";
		private CrossColor _labelColorNG_TT = CrossColor.FromArgb(255, 255, 120, 0);

		[Display(GroupName = "5. Colors (TT)", Name = "NG Visible", Order = 260)]
		public bool ShowNG_TT { get => _showNG_TT; set { _showNG_TT = value; RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "NG VisualType", Order = 261)]
		public VisualMode VisualNG_TT { get => _visualNG_TT; set { _visualNG_TT = value; RedrawChart(); } }

		[Range(0, 8)]
		[Display(GroupName = "5. Colors (TT)", Name = "NG Width (0=global)", Order = 262)]
		public int WidthNG_TT { get => _widthNG_TT; set { _widthNG_TT = Math.Clamp(value, 0, 8); RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "NG Label", Order = 263)]
		public string LabelNG_TT { get => _labelNG_TT; set { _labelNG_TT = value ?? "NG-TT"; RedrawChart(); } }

		[Display(GroupName = "5. Colors (TT)", Name = "NG Label Color", Order = 264)]
		public CrossColor LabelColorNG_TT { get => _labelColorNG_TT; set { _labelColorNG_TT = value; RedrawChart(); } }

		// EM range color (relleno)
		private CrossColor _emRangeFill = CrossColor.FromArgb(60, 0, 180, 255);
		[Display(GroupName = "6. EM Range", Name = "Fill", Order = 50)]
		public CrossColor EMRangeFill { get => _emRangeFill; set { _emRangeFill = value; RedrawChart(); } }

		// Panel métricas
		private bool _showMetrics = true;
		[Display(GroupName = "7. Metrics Panel", Name = "Show panel", Order = 60)]
		public bool ShowMetricsPanel { get => _showMetrics; set { _showMetrics = value; RedrawChart(); } }

		// NUEVO: alineación común panel + extras
		public enum HAlign { Left, Center, Right }

		private HAlign _overlayAlign = HAlign.Right;
		[Display(GroupName = "7. Metrics Panel", Name = "Alignment", Order = 61)]
		public HAlign OverlayAlignment
		{
			get => _overlayAlign;
			set { _overlayAlign = value; RedrawChart(); }
		}

		// Extras: barras de magnitud y badges
		private bool _showBars = true;
		[Display(GroupName = "8. Extras", Name = "Show magnitude bars", Order = 70)]
		public bool ShowMagnitudeBars { get => _showBars; set { _showBars = value; RedrawChart(); } }

		private int _barWidthPx = 160;
		[Range(60, 400)]
		[Display(GroupName = "8. Extras", Name = "Bar width (px)", Order = 71)]
		public int BarWidthPx { get => _barWidthPx; set { _barWidthPx = Math.Clamp(value, 60, 400); RedrawChart(); } }

		private int _barHeightPx = 10;
		[Range(6, 24)]
		[Display(GroupName = "8. Extras", Name = "Bar height (px)", Order = 72)]
		public int BarHeightPx { get => _barHeightPx; set { _barHeightPx = Math.Clamp(value, 6, 24); RedrawChart(); } }

		private int _barSpacingPx = 6;
		[Range(2, 20)]
		[Display(GroupName = "8. Extras", Name = "Bar spacing (px)", Order = 73)]
		public int BarSpacingPx { get => _barSpacingPx; set { _barSpacingPx = Math.Clamp(value, 2, 20); RedrawChart(); } }

		private bool _showBadges = true;
		[Display(GroupName = "8. Extras", Name = "Show PCR/Skew badges", Order = 74)]
		public bool ShowBadges { get => _showBadges; set { _showBadges = value; RedrawChart(); } }

		// NUEVO: margen/offset horizontal para las barras de magnitud
		private int _barsOffsetPx = 16;

		[Range(-400, 400)]
		[Display(GroupName = "8. Extras", Name = "Magnitude bars offset (px)", Order = 75)]
		public int MagnitudeBarsOffsetPx
		{
			get => _barsOffsetPx;
			set { _barsOffsetPx = Math.Clamp(value, -400, 400); RedrawChart(); }
		}

		private int _levelLabelRightMarginPx = 180;

		[Range(60, 400)]
		[Display(GroupName = "2. Appearance", Name = "Level label right margin (px)", Order = 16)]
		public int LevelLabelRightMarginPx
		{
			get => _levelLabelRightMarginPx;
			set { _levelLabelRightMarginPx = Math.Clamp(value, 60, 400); RedrawChart(); }
		}

		// NUEVO: opciones de visualización de rango diario para barras
		private bool _showDayRangeOverlay = true;
		[Display(GroupName = "8. Extras", Name = "Show day range overlay", Order = 76)]
		public bool ShowDayRangeOverlay
		{
			get => _showDayRangeOverlay;
			set { _showDayRangeOverlay = value; RedrawChart(); }
		}

		private bool _zeroCenterSignedBars = true;
		[Display(GroupName = "8. Extras", Name = "Zero-centered signed bars", Order = 77)]
		public bool ZeroCenteredSignedBars
		{
			get => _zeroCenterSignedBars;
			set { _zeroCenterSignedBars = value; RedrawChart(); }
		}

		private bool _showPercentOfDay = true;
		[Display(GroupName = "8. Extras", Name = "Show % of day range", Order = 78)]
		public bool ShowPercentOfDay
		{
			get => _showPercentOfDay;
			set { _showPercentOfDay = value; RedrawChart(); }
		}

		#endregion

		#region Ctor / Initialize / Dispose

		public NQ_OI_TT_Indicator()
			: base(true)
		{
			DenyToChangePanel = true;
			DrawAbovePrice = true;

			DataSeries[0] = _noop;

			EnableCustomDrawing = true;
			SubscribeToDrawingEvents(DrawingLayouts.Historical);
			SubscribeToDrawingEvents(DrawingLayouts.Final);
		}

		protected override void OnInitialize()
		{
			base.OnInitialize();
			SetupWatcher();
			_forceReload = true;
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

		#region Calculate

		protected override void OnCalculate(int bar, decimal value)
		{
			if (bar < CurrentBar - 1)
				return;

			if (_forceReload || (!_loadedOnce && File.Exists(_filePath)))
			{
				_forceReload = false;
				TryLoadFile();
				_loadedOnce = true;
			}
		}

		#endregion

		#region Rendering

		protected override void OnRender(RenderContext context, DrawingLayouts layout)
		{
			if (ChartInfo?.PriceChartContainer == null || CurrentBar <= 0)
				return;

			List<NqRecord> data;
			lock (_sync)
			{
				data = FilterByDateUnsafe(_records, _fromDate, _toDate);
				if (data.Count == 0 && _records.Count > 0)
					data = new List<NqRecord> { _records[^1] };
			}
			if (data.Count == 0)
				return;

			var cont = ChartInfo.PriceChartContainer;
			int first = FirstVisibleBarNumber;
			int last = LastVisibleBarNumber;
			if (first < 0 || last < 0 || last <= first)
				return;

			var tFirst = SafeBarTime(first);
			var tLast = SafeBarTime(last);

			var font = GetFont(_labelFontSize);

			// Segmentos
			var segments = BuildSegments(data, tFirst, tLast);

			// EM
			if (_showEMRange)
			{
				var fill = ToColor(_emRangeFill);
				foreach (var s in segments)
				{
					if (s.EM_HI.HasValue && s.EM_LO.HasValue && s.X1 < s.X2)
					{
						var yHi = cont.GetYByPrice(s.EM_HI.Value, false);
						var yLo = cont.GetYByPrice(s.EM_LO.Value, false);
						context.FillRectangle(fill, RectByCorners(s.X1, yHi, s.X2, yLo));
					}
				}
			}

			// Líneas OI (con estilo por nivel)
			if (_showOI)
			{
				int Wd(int perLevel) => perLevel > 0 ? perLevel : _lineThickness;

				if (_showPG_OI && _visualPG_OI != VisualMode.Hide)
				{
					var pen = NewPen(_colorPG_OI, Wd(_widthPG_OI), dashed: false);
					DrawLevelSegments(context, cont, segments, r => r.PG_OI, pen, _labelPG_OI, font, ToColor(_labelColorPG_OI));
				}
				if (_showFG_OI && _visualFG_OI != VisualMode.Hide)
				{
					var pen = NewPen(_colorFG_OI, Wd(_widthFG_OI), dashed: false);
					DrawLevelSegments(context, cont, segments, r => r.FG_OI, pen, _labelFG_OI, font, ToColor(_labelColorFG_OI));
				}
				if (_showZG_OI && _visualZG_OI != VisualMode.Hide)
				{
					var pen = NewPen(_colorZG_OI, Wd(_widthZG_OI), dashed: false);
					DrawLevelSegments(context, cont, segments, r => r.ZG_OI, pen, _labelZG_OI, font, ToColor(_labelColorZG_OI));
				}
				if (_showFR_OI && _visualFR_OI != VisualMode.Hide)
				{
					var pen = NewPen(_colorFR_OI, Wd(_widthFR_OI), dashed: false);
					DrawLevelSegments(context, cont, segments, r => r.FR_OI, pen, _labelFR_OI, font, ToColor(_labelColorFR_OI));
				}
				if (_showNG_OI && _visualNG_OI != VisualMode.Hide)
				{
					var pen = NewPen(_colorNG_OI, Wd(_widthNG_OI), dashed: false);
					DrawLevelSegments(context, cont, segments, r => r.NG_OI, pen, _labelNG_OI, font, ToColor(_labelColorNG_OI));
				}
			}

			// Líneas TT (con estilo por nivel)
			if (_showTT)
			{
				int WdTT(int perLevel) => perLevel > 0 ? perLevel : _lineThickness;

				if (_showPG_TT && _visualPG_TT != VisualMode.Hide)
				{
					var pen = NewPen(_colorPG_TT, WdTT(_widthPG_TT), dashed: _ttDashed);
					DrawLevelSegments(context, cont, segments, r => r.PG_TT, pen, _labelPG_TT, font, ToColor(_labelColorPG_TT));
				}
				if (_showFG_TT && _visualFG_TT != VisualMode.Hide)
				{
					var pen = NewPen(_colorFG_TT, WdTT(_widthFG_TT), dashed: _ttDashed);
					DrawLevelSegments(context, cont, segments, r => r.FG_TT, pen, _labelFG_TT, font, ToColor(_labelColorFG_TT));
				}
				if (_showZG_TT && _visualZG_TT != VisualMode.Hide)
				{
					var pen = NewPen(_colorZG_TT, WdTT(_widthZG_TT), dashed: _ttDashed);
					DrawLevelSegments(context, cont, segments, r => r.ZG_TT, pen, _labelZG_TT, font, ToColor(_labelColorZG_TT));
				}
				if (_showFR_TT && _visualFR_TT != VisualMode.Hide)
				{
					var pen = NewPen(_colorFR_TT, WdTT(_widthFR_TT), dashed: _ttDashed);
					DrawLevelSegments(context, cont, segments, r => r.FR_TT, pen, _labelFR_TT, font, ToColor(_labelColorFR_TT));
				}
				if (_showNG_TT && _visualNG_TT != VisualMode.Hide)
				{
					var pen = NewPen(_colorNG_TT, WdTT(_widthNG_TT), dashed: _ttDashed);
					DrawLevelSegments(context, cont, segments, r => r.NG_TT, pen, _labelNG_TT, font, ToColor(_labelColorNG_TT));
				}
			}

			// Panel métricas
			Rectangle metricsRect = Rectangle.Empty;
			if (_showMetrics && segments.Count > 0)
			{
				metricsRect = DrawMetricsPanel(context, segments[^1].Record, font);
			}

			// Extras (bajo el panel)
			if (segments.Count > 0)
			{
				var lastRec = segments[^1].Record;

				decimal maxMag = 1m;
				foreach (var r in data)
				{
					ConsiderAbs(ref maxMag, r.TotGEXOI);
					ConsiderAbs(ref maxMag, r.TotalDelta_OI);
					ConsiderAbs(ref maxMag, r.AbsGEX_OI);
					ConsiderAbs(ref maxMag, r.AbsGEX_TT);
				}

				int nextY = (metricsRect == Rectangle.Empty ? 10 : metricsRect.Bottom + 8);
				int desiredW = _barWidthPx + 40;
				int panelX = (metricsRect == Rectangle.Empty ? AlignX(desiredW, 10) : metricsRect.X);
				int panelW = (metricsRect == Rectangle.Empty ? desiredW : metricsRect.Width);

				if (_showBars)
				{
					var dayList = data.Where(d => d.Timestamp.Date == lastRec.Timestamp.Date).ToList();
					var dayStats = ComputeDayStats(dayList);

					DrawMagnitudeBars(context, lastRec, font, panelX, ref nextY, panelW, maxMag, dayStats);
				}

				if (_showBadges)
					DrawBadges(context, lastRec, font, panelX, ref nextY, panelW);
			}
		}

		private int AlignX(int contentWidth, int margin = 10)
		{
			int W = ChartInfo?.Region.Width ?? 0;
			return _overlayAlign switch
			{
				HAlign.Left => margin,
				HAlign.Center => Math.Max(0, (W - contentWidth) / 2),
				_ => Math.Max(0, W - contentWidth - margin)
			};
		}

		private static void ConsiderAbs(ref decimal max, decimal? v)
		{
			if (v.HasValue)
			{
				var a = Math.Abs(v.Value);
				if (a > max) max = a;
			}
		}

		private sealed class Segment
		{
			public NqRecord Record = default!;
			public int X1;
			public int X2;
			public decimal? EM_HI;
			public decimal? EM_LO;
		}

		private List<Segment> BuildSegments(List<NqRecord> data, DateTime tFirst, DateTime tLast)
		{
			var segments = new List<Segment>();
			if (data.Count == 0)
				return segments;

			int xLeft = LevelsFullWidth ? 0 : ChartInfo.PriceChartContainer.GetXByBar(FirstVisibleBarNumber, false);
			int xRight = ChartInfo.Region.Width;

			for (int i = 0; i < data.Count; i++)
			{
				var r0 = data[i];
				var t0 = r0.Timestamp;

				DateTime t1 = (i + 1 < data.Count) ? data[i + 1].Timestamp : tLast.AddMinutes(1);

				if (!_levelsFullWidth)
				{
					if (t1 <= tFirst) continue;
					if (t0 >= tLast) break;
				}

				int x1 = _levelsFullWidth ? xLeft : GetXByTimeClamped(t0);
				int x2 = _levelsFullWidth ? xRight : GetXByTimeClamped(t1);
				if (x2 < x1) (x1, x2) = (x2, x1);

				segments.Add(new Segment
				{
					Record = r0,
					X1 = x1,
					X2 = x2,
					EM_HI = r0.EM_HI,
					EM_LO = r0.EM_LO
				});
			}

			if (segments.Count == 0 && data.Count > 0)
			{
				var best = data.LastOrDefault(r => r.Timestamp <= tLast) ?? data.Last();

				segments.Add(new Segment
				{
					Record = best,
					X1 = xLeft,
					X2 = xRight,
					EM_HI = best.EM_HI,
					EM_LO = best.EM_LO
				});
			}

			return segments;
		}

		private void DrawLevelSegments(
			RenderContext ctx,
			ATAS.Indicators.IChartContainer cont,
			List<Segment> segments,
			Func<NqRecord, decimal?> selector,
			RenderPen pen,
			string labelTag,
			RenderFont font,
			Color labelColor)
		{
			if (segments.Count == 0)
				return;

			foreach (var s in segments)
			{
				var v = selector(s.Record);
				if (!v.HasValue)
					continue;

				var y = cont.GetYByPrice(v.Value, false);
				ctx.DrawLine(pen, s.X1, y, s.X2, y);
			}

			var last = segments[^1].Record;
			var lv = selector(last);
			if (lv.HasValue)
			{
				int xRight = ChartInfo.Region.Width;
				int xLabel = Math.Max(0, xRight - _levelLabelRightMarginPx);

				int y = cont.GetYByPrice(lv.Value, false) - (int)Math.Round(font.Size / 2f) - 1;

				var text = $"{labelTag}: {lv.Value:0.####}  @ {last.Timestamp:MM/dd/yyyy HH:mm:ss}";
				ctx.DrawString(text, font, labelColor, xLabel, y);
			}
		}

		private Rectangle DrawMetricsPanel(RenderContext ctx, NqRecord r, RenderFont font)
		{
			var panelBack = Color.FromArgb(170, 30, 30, 30);
			var fore = Color.FromArgb(245, 245, 245);

			string L(string name, decimal? v, string fmt = "0.####") => v.HasValue ? $"{name}: {v.Value.ToString(fmt, CultureInfo.InvariantCulture)}" : $"{name}: n/a";

			var lines = new List<string>
			{
				$"TS: {r.Timestamp:MM/dd/yyyy HH:mm:ss}",
				L("TotGEXOI", r.TotGEXOI, "0"),
				L("TotalDelta-OI", r.TotalDelta_OI, "0"),
				L("Skew", r.Skew),
				L("PCR", r.PCR)
			};

			var maxChars = lines.Max(s => s.Length);
			int fontH = (int)Math.Round(font.Size + 2);
			int h = fontH * lines.Count + 8;
			int w = (int)Math.Ceiling(maxChars * font.Size * 0.56) + 14;

			int x = AlignX(w, 10);
			int y = 10;

			ctx.FillRectangle(panelBack, new Rectangle(x, y, w, h));

			int dy = y + 6;
			foreach (var s in lines)
			{
				ctx.DrawString(s, font, fore, x + 8, dy);
				dy += fontH;
			}

			return new Rectangle(x, y, w, h);
		}

		private void DrawMagnitudeBars(RenderContext ctx, NqRecord r, RenderFont font, int panelX, ref int nextY, int panelW, decimal maxMag, DayStats day)
		{
			int xText = panelX;
			int w = panelW;
			int innerPad = 8;

			int barW = Math.Min(_barWidthPx, Math.Max(60, w - 16));
			int labelW = Math.Max(60, w - barW - innerPad * 2);

			int y = nextY;

			void DrawBar(string label, decimal? v, Color baseFill, bool signed, decimal dayMin, decimal dayMax)
			{
				if (!v.HasValue)
					return;

				var val = v.Value;
				int bx = xText + innerPad + labelW + _barsOffsetPx;
				int by = y;
				int bw = barW;
				int bh = _barHeightPx;

				// Etiqueta
				ctx.DrawString(label, font, Color.White, xText + innerPad, y);

				// Fondo de barra
				ctx.FillRectangle(Color.FromArgb(60, 255, 255, 255), new Rectangle(bx, by, bw, bh));

				// Overlay de rango diario [min..max]
				if (_showDayRangeOverlay)
				{
					var range = dayMax - dayMin;
					if (range > 0m)
					{
						double fracMin = (double)((dayMin - dayMin) / range); // 0
						double fracMax = (double)((dayMax - dayMin) / range); // 1
						int rx = bx + (int)Math.Round(bw * fracMin);
						int rw = Math.Max(1, (int)Math.Round(bw * (fracMax - fracMin)));
						ctx.FillRectangle(Color.FromArgb(35, 200, 200, 200), new Rectangle(rx, by, rw, bh));

						// Marcas mín/máx
						var tickPen = new RenderPen(Color.FromArgb(120, 220, 220, 220), 1);
						ctx.DrawLine(tickPen, bx, by, bx, by + bh);               // min (izquierda del rango)
						ctx.DrawLine(tickPen, bx + bw - 1, by, bx + bw - 1, by + bh); // max (derecha del rango)
					}
				}

				// Relleno actual
				var rangeDay = dayMax - dayMin;
				int fillW = 0;
				int fillX = bx;

				Color barColor = baseFill;
				if (signed)
				{
					if (val < 0) barColor = Color.FromArgb(230, 220, 70, 70);
					if (val > 0) barColor = Color.FromArgb(230, 70, 180, 90);

					if (_zeroCenterSignedBars && dayMin < 0m && dayMax > 0m && rangeDay > 0m)
					{
						// Posición de cero y del valor actual dentro del rango diario
						double fracZero = (double)((0m - dayMin) / rangeDay);
						double fracVal  = (double)((val - dayMin) / rangeDay);

						int xZero = bx + (int)Math.Round(bw * fracZero);
						int xVal  = bx + (int)Math.Round(bw * fracVal);

						fillX = Math.Min(xZero, xVal);
						fillW = Math.Max(1, Math.Abs(xVal - xZero));

						// Marca de cero
						var zeroPen = new RenderPen(Color.FromArgb(160, 180, 180, 180), 1);
						ctx.DrawLine(zeroPen, xZero, by, xZero, by + bh);
					}
					else
					{
						// Escalado de izquierda a derecha
						if (rangeDay <= 0m)
						{
							fillW = Math.Max(1, (int)Math.Round(bw * 0.02));
						}
						else
						{
							double frac = (double)((val - dayMin) / rangeDay);
							frac = Math.Max(0.0, Math.Min(1.0, frac));
							fillW = Math.Max(1, (int)Math.Round(bw * frac));
						}
						fillX = bx;
					}
				}
				else
				{
					// Sin signo: [dayMin..dayMax] normalmente [0..max]
					if (rangeDay <= 0m)
					{
						fillW = Math.Max(1, (int)Math.Round(bw * 0.02));
					}
					else
					{
						double frac = (double)((val - dayMin) / rangeDay);
						frac = Math.Max(0.0, Math.Min(1.0, frac));
						fillW = Math.Max(1, (int)Math.Round(bw * frac));
					}
					fillX = bx;
				}

				ctx.FillRectangle(barColor, new Rectangle(fillX, by, fillW, bh));

				// Valor + porcentaje del rango diario
				var valueText = ShowPercentOfDay && rangeDay > 0m
					? $"{ShortNumber(val)} ({(val - dayMin) / (rangeDay == 0m ? 1m : rangeDay):P0})"
					: ShortNumber(val);

				int valX = bx + bw + 6;
				ctx.DrawString(valueText, font, Color.Gainsboro, valX, y);

				y += bh + _barSpacingPx;
			}

			DrawBar("TotGEXOI",      r.TotGEXOI,      Color.FromArgb(230, 200, 60, 60), signed: true,  day.MinTotGEXOI,     day.MaxTotGEXOI);
			DrawBar("TotalDelta-OI", r.TotalDelta_OI, Color.FromArgb(230, 60, 160, 220), signed: true,  day.MinTotalDeltaOI, day.MaxTotalDeltaOI);
			DrawBar("AbsGEX-OI",     r.AbsGEX_OI,     Color.FromArgb(230, 120, 180, 255), signed: false, day.MinAbsGEX_OI,    day.MaxAbsGEX_OI);
			DrawBar("AbsGEX-TT",     r.AbsGEX_TT,     Color.FromArgb(230, 255, 200, 80),  signed: false, day.MinAbsGEX_TT,    day.MaxAbsGEX_TT);

			nextY = y;
		}

		private void DrawBadges(RenderContext ctx, NqRecord r, RenderFont font, int panelX, ref int nextY, int panelW)
		{
			int x = panelX;
			int y = nextY;
			int pad = 6;
			int sp = 8;

			if (r.PCR.HasValue)
			{
				var pcr = r.PCR.Value;
				var pcrText = $"PCR {pcr:0.###}";
				var (pcrBack, pcrFore) = PcrColors(pcr);
				var (w, h) = Measure(font, pcrText);
				var rect = new Rectangle(x, y, w + pad * 2, h + pad * 2);
				ctx.FillRectangle(pcrBack, rect);
				ctx.DrawString(pcrText, font, pcrFore, rect.X + pad, rect.Y + pad);
				x += rect.Width + sp;
			}

			if (r.Skew.HasValue)
			{
				var sk = r.Skew.Value;
				var skewText = $"Skew {sk:0.###}";
				var (skBack, skFore) = SkewColors(sk);
				var (w, h) = Measure(font, skewText);
				var rect = new Rectangle(x, y, w + pad * 2, h + pad * 2);
				ctx.FillRectangle(skBack, rect);
				ctx.DrawString(skewText, font, skFore, rect.X + pad, rect.Y + pad);
				x += rect.Width + sp;
			}

			nextY = y + (int)Math.Round(font.Size + 2) + pad * 2 + 2;
		}

		private static (int w, int h) Measure(RenderFont font, string text)
		{
			double factor = font.Style.HasFlag(FontStyle.Bold) ? 0.62 : 0.58;
			int w = (int)Math.Ceiling(text.Length * (font.Size * factor));
			int h = (int)Math.Round(font.Size + 2);
			return (w, h);
		}

		private static (Color back, Color fore) PcrColors(decimal pcr)
		{
			if (pcr >= 1.3m) return (Color.FromArgb(180, 180, 60, 60), Color.White);
			if (pcr >= 1.0m) return (Color.FromArgb(160, 220, 140, 60), Color.White);
			if (pcr >= 0.7m) return (Color.FromArgb(140, 90, 140, 200), Color.White);
			return (Color.FromArgb(180, 60, 160, 80), Color.White);
		}

		private static (Color back, Color fore) SkewColors(decimal skew)
		{
			if (skew <= -0.35m) return (Color.FromArgb(180, 180, 60, 60), Color.White);
			if (skew <= -0.15m) return (Color.FromArgb(160, 220, 140, 60), Color.White);
			if (skew <= 0.05m) return (Color.FromArgb(140, 120, 120, 120), Color.White);
			return (Color.FromArgb(180, 60, 160, 80), Color.White);
		}

		private static string ShortNumber(decimal v)
		{
			var av = Math.Abs(v);
			if (av >= 1_000_000_000m) return $"{v / 1_000_000_000m:0.##}B";
			if (av >= 1_000_000m) return $"{v / 1_000_000m:0.##}M";
			if (av >= 1_000m) return $"{v / 1_000m:0.##}K";
			return v.ToString("0.##", CultureInfo.InvariantCulture);
		}

		private static Rectangle RectByCorners(int x1, int y1, int x2, int y2)
		{
			int left = Math.Min(x1, x2);
			int top = Math.Min(y1, y2);
			int width = Math.Abs(x2 - x1);
			int height = Math.Abs(y2 - y1);
			return new Rectangle(left, top, width, height);
		}

		private static Color ToColor(CrossColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

		private RenderPen NewPen(CrossColor color, int thickness, bool dashed)
		{
			var pen = new RenderPen(ToColor(color), thickness);
			pen.DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid;
			return pen;
		}

		private RenderFont GetFont(int size)
		{
			if (!_fontCache.TryGetValue(size, out var f))
			{
				f = new RenderFont("Segoe UI", size);
				_fontCache[size] = f;
			}
			return f;
		}

		private DateTime SafeBarTime(int bar)
		{
			try { return GetCandle(bar).Time; }
			catch { return DateTime.MinValue; }
		}

		private int GetXByTimeClamped(DateTime ts)
		{
			var cont = ChartInfo.PriceChartContainer;
			int first = FirstVisibleBarNumber;
			int last = LastVisibleBarNumber;
			if (first < 0 || last < 0 || last <= first)
				return 0;

			var tFirst = SafeBarTime(first);
			var tLast = SafeBarTime(last);
			if (ts <= tFirst) return cont.GetXByBar(first, false);
			if (ts >= tLast) return cont.GetXByBar(last, false);

			int lo = first, hi = last;
			while (lo + 1 < hi)
			{
				int mid = (lo + hi) / 2;
				var tm = SafeBarTime(mid);
				if (tm == DateTime.MinValue) break;

				if (tm == ts) { lo = hi = mid; break; }
				if (tm < ts) lo = mid;
				else hi = mid;
			}
			return cont.GetXByBar(lo, false);
		}

		#endregion

		#region File loading / parsing

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
			try
			{
				_reloadDebounce?.Change(300, Timeout.Infinite);
			}
			catch { }
		}

		private void TryLoadFile()
		{
			if (string.IsNullOrWhiteSpace(_filePath) || !File.Exists(_filePath))
				return;

			List<NqRecord> parsed;
			try
			{
				var text = File.ReadAllText(_filePath);
				parsed = ParseFile(text);
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

		private List<NqRecord> ParseFile(string content)
		{
			var lines = content.Replace("\r", "").Split('\n');
			var recs = new List<NqRecord>();

			NqRecord? cur = null;
			var block = new List<string>();

			void Flush()
			{
				if (cur != null)
				{
					cur.RawBlock = string.Join("\n", block);
					recs.Add(cur);
				}
				cur = null;
				block.Clear();
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
					var tsStr = m.Groups["ts"].Value;
					var ts = ParseTimestamp(tsStr);
					cur = new NqRecord { Timestamp = ts };
					block.Add(line);
					ExtractPairs(cur, line);
				}
				else
				{
					if (cur == null)
						continue;

					block.Add(line);
					ExtractPairs(cur, line);
				}
			}
			Flush();

			return recs;
		}

		private static DateTime ParseTimestamp(string ts)
		{
			string[] fmts =
			{
				"MM/dd/yyyy HH:mm:ss",
				"M/d/yyyy HH:mm:ss",
				"dd/MM/yyyy HH:mm:ss",
				"d/M/yyyy HH:mm:ss",
				"MM-dd-yyyy HH:mm:ss",
				"dd-MM-yyyy HH:mm:ss"
			};
			foreach (var f in fmts)
			{
				if (DateTime.TryParseExact(ts, f, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
					return dt;
			}
			if (DateTime.TryParse(ts, out var any))
				return any;
			return DateTime.MinValue;
		}

		private static void ExtractPairs(NqRecord rec, string line)
		{
			foreach (Match pm in RxPair.Matches(line))
			{
				var key = pm.Groups["key"].Value.Trim();
				var valStr = pm.Groups["val"].Value.Trim();
				if (!decimal.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
					continue;

				switch (key)
				{
					case "PG-OI":
					case "PG-OI-": rec.PG_OI = val; break;
					case "FG-OI":
					case "FG-OI-": rec.FG_OI = val; break;
					case "ZG-OI":
					case "ZG-OI-": rec.ZG_OI = val; break;
					case "FR-OI":
					case "FR-OI-": rec.FR_OI = val; break;
					case "NG-OI":
					case "NG-OI-": rec.NG_OI = val; break;

					case "PG-TT":
					case "PG-TT-": rec.PG_TT = val; break;
					case "FG-TT":
					case "FG-TT-": rec.FG_TT = val; break;
					case "ZG-TT":
					case "ZG-TT-": rec.ZG_TT = val; break;
					case "FR-TT":
					case "FR-TT-": rec.FR_TT = val; break;
					case "NG-TT":
					case "NG-TT-": rec.NG_TT = val; break;

					case "EM-HI":
					case "EM-HI-": rec.EM_HI = val; break;
					case "EM-LO":
					case "EM-LO-": rec.EM_LO = val; break;
					case "TotGEXOI":
					case "TotGEXOI-": rec.TotGEXOI = val; break;
					case "TotalDelta-OI":
					case "TotalDelta-OI-": rec.TotalDelta_OI = val; break;
					case "AbsGEX-OI":
					case "AbsGEX-OI-": rec.AbsGEX_OI = val; break;
					case "AbsGEX-TT":
					case "AbsGEX-TT-": rec.AbsGEX_TT = val; break;
					case "Skew":
					case "Skew-": rec.Skew = val; break;
					case "PCR":
					case "PCR-": rec.PCR = val; break;
				}
			}
		}

		private static List<NqRecord> FilterByDateUnsafe(List<NqRecord> input, DateTime? from, DateTime? to)
		{
			IEnumerable<NqRecord> q = input;
			if (from.HasValue)
				q = q.Where(r => r.Timestamp >= from.Value);
			if (to.HasValue)
				q = q.Where(r => r.Timestamp <= to.Value);
			return q.OrderBy(r => r.Timestamp).ToList();
		}

		#endregion

		#region Estadísticas diarias

		private readonly record struct DayStats(
			decimal MinTotGEXOI, decimal MaxTotGEXOI,
			decimal MinTotalDeltaOI, decimal MaxTotalDeltaOI,
			decimal MinAbsGEX_OI, decimal MaxAbsGEX_OI,
			decimal MinAbsGEX_TT, decimal MaxAbsGEX_TT);

		private static DayStats ComputeDayStats(List<NqRecord> day)
		{
			static (decimal min, decimal max) MinMax(IEnumerable<decimal> seq)
			{
				var list = seq.ToList();
				if (list.Count == 0) return (0m, 0m);
				return (list.Min(), list.Max());
			}

			var gexoi = day.Where(r => r.TotGEXOI.HasValue).Select(r => r.TotGEXOI!.Value);
			var tdoi  = day.Where(r => r.TotalDelta_OI.HasValue).Select(r => r.TotalDelta_OI!.Value);
			var agoi  = day.Where(r => r.AbsGEX_OI.HasValue).Select(r => r.AbsGEX_OI!.Value);
			var agtt  = day.Where(r => r.AbsGEX_TT.HasValue).Select(r => r.AbsGEX_TT!.Value);

			// Abs* normalmente >= 0; si día vacío, rango 0-0
			var (minGexoi, maxGexoi) = MinMax(gexoi);
			var (minTdoi,  maxTdoi)  = MinMax(tdoi);
			var (minAgoi,  maxAgoi)  = MinMax(agoi);
			var (minAgtt,  maxAgtt)  = MinMax(agtt);

			// Asegurar mínimos no negativos en métricas absolutas
			if (minAgoi < 0) minAgoi = 0;
			if (minAgtt < 0) minAgtt = 0;

			return new DayStats(minGexoi, maxGexoi, minTdoi, maxTdoi, minAgoi, maxAgoi, minAgtt, maxAgtt);
		}

		#endregion
	}
}