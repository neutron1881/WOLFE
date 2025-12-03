using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace ATAS.Indicators.Technical
{
    [DisplayName("CashProfile NewFlow")]
    public class CashProfile : Indicator
    {
        private class StrikeRow
        {
            // Strike final usado para posicionar (ES si conversión activada, SPY si no)
            public decimal Strike { get; set; }
            // Strike original del CSV (SPY), para etiquetar al lado de los valores
            public decimal StrikeSpy { get; set; }
            public decimal Calls { get; set; }
            public decimal Puts { get; set; }
        }

        // NUEVO: Estructura para Big Trades persistentes
        private class PersistentBigTrade
        {
            public decimal StrikeSpy { get; set; }    // Strike original SPY
            public decimal Strike { get; set; }        // Strike final (ES o SPY)
            public decimal Value { get; set; }         // Valor del big trade
            public bool IsCall { get; set; }           // true = Call, false = Put
            public DateTime DetectionTime { get; set; } // Momento de detección
            public int Bar { get; set; }               // Barra donde se detectó
        }

        private readonly object _sync = new();
        private readonly List<StrikeRow> _rows = new();
        private readonly System.Timers.Timer _timer = new(60000);
        private System.Timers.Timer? _hotkeyTimer;
        private System.Timers.Timer? _autoConvTimer;
        private string _error = string.Empty;
        private DateTime? _lastLoad;

        // Guardar valores previos por strike para dibujar marcadores
        private Dictionary<decimal, (decimal calls, decimal puts)> _prevBySpy = new();
        private Dictionary<decimal, (decimal calls, decimal puts)> _prevByStrike = new();

        // NUEVO: Big Trades persistentes - lista que se conserva durante la sesión
        private List<PersistentBigTrade> _persistentBigTrades = new();

        // Base rows para auto-conversión (StrikeSpy + valores)
        private readonly List<StrikeRow> _baseRows = new();

        // Último precio del CSV (columna Price) para auto-conversión
        private decimal _lastCsvPrice;
        // Último timestamp usado del CSV (si existe)
        private DateTime? _lastCsvTimestamp;

        // Precio ES en tiempo real (desde el gráfico)
        private decimal _lastEsPrice;

        // Sticky factor para autoconversión por timestamp
        private decimal? _tsStickyFactor;
        private DateTime? _tsStickyAtUtc;

        // Factor activo de conversión (se mantiene fijo hasta la siguiente ventana)
        private decimal? _activeConvFactor;

        // Settings
        private string _filePath = @"C:\\Path\\To\\SPY_Cash.csv";
        [Display(GroupName = "1. Settings", Name = "CSV File Path", Order =10)]
        public string FilePath
        {
            get => _filePath;
            set
            {
                _filePath = value;
                ForceReload();
            }
        }

        private int _refreshSeconds =60;
        [Display(GroupName = "1. Settings", Name = "Refresh (sec)", Order =20)]
        [Range(5,3600)]
        public int RefreshSeconds
        {
            get => _refreshSeconds;
            set
            {
                _refreshSeconds = Math.Max(5, value);
                ResetTimer();
            }
        }

        private bool _useLatestTimestamp = true;
        [Display(GroupName = "1. Settings", Name = "Use latest timestamp only", Order =30)]
        public bool UseLatestTimestamp
        {
            get => _useLatestTimestamp;
            set
            {
                _useLatestTimestamp = value;
                ForceReload();
            }
        }

        // Conversion Settings
        private bool _enableConversion;
        [Display(GroupName = "1. Settings", Name = "Convert strike to ES", Order =70)]
        public bool EnableConversion
        {
            get => _enableConversion;
            set { _enableConversion = value; ForceReload(); }
        }

        // Conversión automática (usa Price del CSV y precio actual del gráfico)
        private bool _enableAutoConversion;
        [Display(GroupName = "1. Settings", Name = "Automatic conversion", Order =75)]
        public bool EnableAutoConversion
        {
            get => _enableAutoConversion;
            set
            {
                _enableAutoConversion = value;
                ResetAutoConvTimer();
                ForceReload();
            }
        }

        private int _autoConversionSeconds = 10;
        [Display(GroupName = "1. Settings", Name = "Auto conversion period (sec)", Order =76)]
        [Range(1,3600)]
        public int AutoConversionSeconds
        {
            get => _autoConversionSeconds;
            set
            {
                _autoConversionSeconds = Math.Clamp(value, 1, 3600);
                ResetAutoConvTimer();
            }
        }

        // Alineación por timestamp para la conversión automática
        private bool _autoConvUseTimestamp;
        [Display(GroupName = "1. Settings", Name = "Auto conversion align by timestamp", Order =77)]
        public bool AutoConversionAlignByTimestamp
        {
            get => _autoConvUseTimestamp;
            set { _autoConvUseTimestamp = value; }
        }

        private int _autoConvToleranceSec = 2;
        [Display(GroupName = "1. Settings", Name = "Timestamp tolerance (sec)", Order =78)]
        [Range(0, 600)]
        public int AutoConversionTimestampToleranceSec
        {
            get => _autoConvToleranceSec;
            set { _autoConvToleranceSec = Math.Clamp(value, 0, 600); }
        }

        // Tipo de perfil (preset de columnas)
        public enum ProfileDataType { Custom, Cash, IV, Delta, Gex }
        private ProfileDataType _profileType = ProfileDataType.Cash;
        [Display(GroupName = "1. Settings", Name = "Profile type", Order =40)]
        public ProfileDataType ProfileType
        {
            get => _profileType;
            set
            {
                _profileType = value;
                ApplyProfilePreset();
            }
        }

        private string _quotesCsvPath = string.Empty;
        [Display(GroupName = "1. Settings", Name = "Quotes CSV (Timestamp, SPY, ES)", Order =80)]
        public string QuotesCsvPath
        {
            get => _quotesCsvPath;
            set { _quotesCsvPath = value ?? string.Empty; ForceReload(); }
        }

        // NUEVO: claves de columnas a usar para values (evita confusiones con múltiples columnas)
        private string _callsColumnKey = "Cash Call";
        [Display(GroupName = "1. Settings", Name = "Calls column key", Order =50)]
        public string CallsColumnKey
        {
            get => _callsColumnKey;
            set { _callsColumnKey = value ?? string.Empty; ForceReload(); }
        }

        private string _putsColumnKey = "Cash Put";
        [Display(GroupName = "1. Settings", Name = "Puts column key", Order =60)]
        public string PutsColumnKey
        {
            get => _putsColumnKey;
            set { _putsColumnKey = value ?? string.Empty; ForceReload(); }
        }

        // Claves por defecto para GEX (se usan vía ProfileType -> ApplyProfilePreset)
        private string _gexCallsColumnKey = "Call Gex";
        private string _gexPutsColumnKey = "Put Gex";

        private decimal _manualSpyPrice;
        [Display(GroupName = "1. Settings", Name = "Manual SPY price", Order =90)]
        public decimal ManualSpyPrice
        {
            get => _manualSpyPrice;
            set { _manualSpyPrice = Math.Max(0, value); ForceReload(); }
        }

        private decimal _manualEsPrice;
        [Display(GroupName = "1. Settings", Name = "Manual ES price", Order =100)]
        public decimal ManualEsPrice
        {
            get => _manualEsPrice;
            set { _manualEsPrice = Math.Max(0, value); ForceReload(); }
        }

        private decimal _priceStep =0.25m;
        [Display(GroupName = "1. Settings", Name = "Price step (rounding)", Order =110)]
        public decimal PriceStep
        {
            get => _priceStep;
            set { _priceStep = value <=0 ?0.25m : value; ForceReload(); }
        }

        // Positioning
        private int _centerOffsetPx =0;
        [Display(GroupName = "2. Position", Name = "Center offset (px)", Order =10)]
        [Range(-5000,5000)]
        public int CenterOffsetPx
        {
            get => _centerOffsetPx;
            set { _centerOffsetPx = Math.Clamp(value, -5000,5000); RedrawChart(); }
        }

        private bool _callsOnRight = true;
        [Display(GroupName = "2. Position", Name = "Calls on right side", Order =20)]
        public bool CallsOnRight
        {
            get => _callsOnRight;
            set { _callsOnRight = value; RedrawChart(); }
        }

        private bool _showCenterLine = true;
        [Display(GroupName = "2. Position", Name = "Show center line", Order =30)]
        public bool ShowCenterLine
        {
            get => _showCenterLine;
            set { _showCenterLine = value; RedrawChart(); }
        }

        private Color _centerLineColor = Color.FromArgb(140, Color.Red);
        [Display(GroupName = "2. Position", Name = "Center line color", Order =40)]
        public Color CenterLineColor
        {
            get => _centerLineColor;
            set { _centerLineColor = value; RedrawChart(); }
        }

        private int _centerLineThickness =1;
        [Display(GroupName = "2. Position", Name = "Center line thickness", Order =50)]
        [Range(1,10)]
        public int CenterLineThickness
        {
            get => _centerLineThickness;
            set { _centerLineThickness = Math.Clamp(value,1,10); RedrawChart(); }
        }

        // Appearance
        private int _maxBarWidthPx =220;
        [Display(GroupName = "3. Appearance", Name = "Max side width (px)", Order =10)]
        [Range(20,1000)]
        public int MaxBarWidthPx
        {
            get => _maxBarWidthPx;
            set { _maxBarWidthPx = Math.Clamp(value,20,1000); RedrawChart(); }
        }

        private int _barThicknessPx =7;
        [Display(GroupName = "3. Appearance", Name = "Bar thickness (px)", Order =20)]
        [Range(2,50)]
        public int BarThicknessPx
        {
            get => _barThicknessPx;
            set { _barThicknessPx = Math.Clamp(value,2,50); RedrawChart(); }
        }

        private Color _callsColor = Color.DodgerBlue;
        [Display(GroupName = "3. Appearance", Name = "Calls color", Order =40)]
        public Color CallsColor
        {
            get => _callsColor;
            set { _callsColor = value; RedrawChart(); }
        }

        private Color _putsColor = Color.IndianRed;
        [Display(GroupName = "3. Appearance", Name = "Puts color", Order =50)]
        public Color PutsColor
        {
            get => _putsColor;
            set { _putsColor = value; RedrawChart(); }
        }

        // Colores por perfil (IV y Delta y GEX)
        private Color _ivCallsColor = Color.MediumSeaGreen;
        [Display(GroupName = "3. Appearance", Name = "IV Calls color", Order =60)]
        public Color IvCallsColor
        {
            get => _ivCallsColor;
            set { _ivCallsColor = value; RedrawChart(); }
        }

        private Color _ivPutsColor = Color.Salmon;
        [Display(GroupName = "3. Appearance", Name = "IV Puts color", Order =70)]
        public Color IvPutsColor
        {
            get => _ivPutsColor;
            set { _ivPutsColor = value; RedrawChart(); }
        }

        private Color _deltaCallsColor = Color.SteelBlue;
        [Display(GroupName = "3. Appearance", Name = "Delta Calls color", Order =80)]
        public Color DeltaCallsColor
        {
            get => _deltaCallsColor;
            set { _deltaCallsColor = value; RedrawChart(); }
        }

        private Color _deltaPutsColor = Color.Sienna;
        [Display(GroupName = "3. Appearance", Name = "Delta Puts color", Order =90)]
        public Color DeltaPutsColor
        {
            get => _deltaPutsColor;
            set { _deltaPutsColor = value; RedrawChart(); }
        }

        private Color _gexCallsColor = Color.MediumOrchid;
        [Display(GroupName = "3. Appearance", Name = "GEX Calls color", Order =95)]
        public Color GexCallsColor
        {
            get => _gexCallsColor;
            set { _gexCallsColor = value; RedrawChart(); }
        }

        private Color _gexPutsColor = Color.Orchid;
        [Display(GroupName = "3. Appearance", Name = "GEX Puts color", Order =96)]
        public Color GexPutsColor
        {
            get => _gexPutsColor;
            set { _gexPutsColor = value; RedrawChart(); }
        }

        private int _fillOpacity =140; //0..255
        [Display(GroupName = "3. Appearance", Name = "Fill opacity", Order =30)]
        [Range(0,255)]
        public int FillOpacity
        {
            get => _fillOpacity;
            set { _fillOpacity = Math.Clamp(value,0,255); RedrawChart(); }
        }

        private bool _showValues;
        [Display(GroupName = "3. Appearance", Name = "Show side values", Order =100)]
        public bool ShowValues
        {
            get => _showValues;
            set { _showValues = value; RedrawChart(); }
        }

        private string _valueFormat = "0,0";
        [Display(GroupName = "3. Appearance", Name = "Side value format", Order =110)]
        public string ValueFormat
        {
            get => _valueFormat;
            set { _valueFormat = value ?? string.Empty; RedrawChart(); }
        }

        // Strike labels next to side values (SPY)
        private bool _showCenterStrikes = true;
        [Display(GroupName = "4. Strike labels", Name = "Show SPY strike next to values", Order =10)]
        public bool ShowCenterStrikes
        {
            get => _showCenterStrikes;
            set { _showCenterStrikes = value; RedrawChart(); }
        }

        private string _strikeFormat = "0"; // entero por defecto para formato (619)
        [Display(GroupName = "4. Strike labels", Name = "SPY strike number format", Order =20)]
        public string StrikeFormat
        {
            get => _strikeFormat;
            set { _strikeFormat = value ?? string.Empty; }
        }

        private int _strikeFontSize =8;
        [Display(GroupName = "4. Strike labels", Name = "Font size", Order =30)]
        [Range(6,40)]
        public int StrikeFontSize
        {
            get => _strikeFontSize;
            set { _strikeFontSize = Math.Clamp(value,6,40); RedrawChart(); }
        }

        private Color _strikeColor = Color.LightSteelBlue;
        [Display(GroupName = "4. Strike labels", Name = "Color", Order =40)]
        public Color StrikeColor
        {
            get => _strikeColor;
            set { _strikeColor = value; RedrawChart(); }
        }

        private int _strikeLeftMarginPx =10;
        [Display(GroupName = "4. Strike labels", Name = "Left strike extra margin (px)", Order =50)]
        [Range(0,300)]
        public int StrikeLeftMarginPx
        {
            get => _strikeLeftMarginPx;
            set { _strikeLeftMarginPx = Math.Clamp(value,0,300); RedrawChart(); }
        }

        private int _strikeRightMarginPx =10;
        [Display(GroupName = "4. Strike labels", Name = "Right strike extra margin (px)", Order =60)]
        [Range(0,300)]
        public int StrikeRightMarginPx
        {
            get => _strikeRightMarginPx;
            set { _strikeRightMarginPx = Math.Clamp(value,0,300); RedrawChart(); }
        }

        // Top summary panel (series style)
        private bool _showTopSummary = true;
        [Display(GroupName = "5. Top summary", Name = "Show top summary", Order =10)]
        public bool ShowTopSummary
        {
            get => _showTopSummary;
            set { _showTopSummary = value; RedrawChart(); }
        }

        private int _topFontSize =11;
        [Display(GroupName = "5. Top summary", Name = "Font size", Order =20)]
        [Range(6,60)]
        public int TopFontSize
        {
            get => _topFontSize;
            set { _topFontSize = Math.Clamp(value,6,60); RedrawChart(); }
        }

        private int _topMarginPx =6;
        [Display(GroupName = "5. Top summary", Name = "Top margin (px)", Order =30)]
        [Range(0,200)]
        public int TopMarginPx
        {
            get => _topMarginPx;
            set { _topMarginPx = Math.Clamp(value,0,200); RedrawChart(); }
        }

        private int _summaryBarWidthPx =240;
        [Display(GroupName = "5. Top summary", Name = "Bar width (px)", Order =40)]
        [Range(60,1000)]
        public int SummaryBarWidthPx
        {
            get => _summaryBarWidthPx;
            set { _summaryBarWidthPx = Math.Clamp(value,60,1000); RedrawChart(); }
        }

        private int _summaryRowHeightPx =12;
        [Display(GroupName = "5. Top summary", Name = "Row height (px)", Order =50)]
        [Range(8,40)]
        public int SummaryRowHeightPx
        {
            get => _summaryRowHeightPx;
            set { _summaryRowHeightPx = Math.Clamp(value,8,40); RedrawChart(); }
        }

        private int _summaryRowSpacingPx =4;
        [Display(GroupName = "5. Top summary", Name = "Row spacing (px)", Order =60)]
        [Range(0,40)]
        public int SummaryRowSpacingPx
        {
            get => _summaryRowSpacingPx;
            set { _summaryRowSpacingPx = Math.Clamp(value,0,40); RedrawChart(); }
        }

        private int _summaryLabelWidthPx =110;
        [Display(GroupName = "5. Top summary", Name = "Label width (px)", Order =70)]
        [Range(50,300)]
        public int SummaryLabelWidthPx
        {
            get => _summaryLabelWidthPx;
            set { _summaryLabelWidthPx = Math.Clamp(value,50,300); RedrawChart(); }
        }

        private Color _summaryBackBar = Color.FromArgb(80,120,120,120);
        [Display(GroupName = "5. Top summary", Name = "Back bar color", Order =80)]
        public Color SummaryBackBar
        {
            get => _summaryBackBar;
            set { _summaryBackBar = value; RedrawChart(); }
        }

        private Color _summaryCallsColor = Color.DodgerBlue;
        [Display(GroupName = "5. Top summary", Name = "Calls bar color", Order =90)]
        public Color SummaryCallsColor
        {
            get => _summaryCallsColor;
            set { _summaryCallsColor = value; RedrawChart(); }
        }

        private Color _summaryPutsColor = Color.IndianRed;
        [Display(GroupName = "5. Top summary", Name = "Puts bar color", Order =100)]
        public Color SummaryPutsColor
        {
            get => _summaryPutsColor;
            set { _summaryPutsColor = value; RedrawChart(); }
        }

        // Colores del panel superior por perfil (IV, Delta y GEX)
        private Color _summaryCallsColorIv = Color.MediumSeaGreen;
        [Display(GroupName = "5. Top summary", Name = "IV Calls bar color", Order =110)]
        public Color SummaryCallsColorIV
        {
            get => _summaryCallsColorIv;
            set { _summaryCallsColorIv = value; RedrawChart(); }
        }

        private Color _summaryPutsColorIv = Color.Salmon;
        [Display(GroupName = "5. Top summary", Name = "IV Puts bar color", Order =120)]
        public Color SummaryPutsColorIV
        {
            get => _summaryPutsColorIv;
            set { _summaryPutsColorIv = value; RedrawChart(); }
        }

        private Color _summaryCallsColorDelta = Color.SteelBlue;
        [Display(GroupName = "5. Top summary", Name = "Delta Calls bar color", Order =130)]
        public Color SummaryCallsColorDelta
        {
            get => _summaryCallsColorDelta;
            set { _summaryCallsColorDelta = value; RedrawChart(); }
        }

        private Color _summaryPutsColorDelta = Color.Sienna;
        [Display(GroupName = "5. Top summary", Name = "Delta Puts bar color", Order =140)]
        public Color SummaryPutsColorDelta
        {
            get => _summaryPutsColorDelta;
            set { _summaryPutsColorDelta = value; RedrawChart(); }
        }

        private Color _summaryCallsColorGex = Color.MediumOrchid;
        [Display(GroupName = "5. Top summary", Name = "GEX Calls bar color", Order =145)]
        public Color SummaryCallsColorGEX
        {
            get => _summaryCallsColorGex;
            set { _summaryCallsColorGex = value; RedrawChart(); }
        }

        private Color _summaryPutsColorGex = Color.Orchid;
        [Display(GroupName = "5. Top summary", Name = "GEX Puts bar color", Order =146)]
        public Color SummaryPutsColorGEX
        {
            get => _summaryPutsColorGex;
            set { _summaryPutsColorGex = value; RedrawChart(); }
        }

        // Mostrar SPY implícado centrado
        private bool _showSpyCurrent = true;
        [Display(GroupName = "5. Top summary", Name = "Show implied SPY below", Order =150)]
        public bool ShowSpyCurrent
        {
            get => _showSpyCurrent;
            set { _showSpyCurrent = value; RedrawChart(); }
        }

        private string _spyValueFormat = "0.00";
        [Display(GroupName = "5. Top summary", Name = "SPY value format", Order =160)]
        public string SpyValueFormat
        {
            get => _spyValueFormat;
            set { _spyValueFormat = value ?? string.Empty; }
        }

        // Color de textos del Top Summary
        private Color _topSummaryTextColor = Color.White;
        [Display(GroupName = "5. Top summary", Name = "Text color", Order =165)]
        public Color TopSummaryTextColor
        {
            get => _topSummaryTextColor;
            set { _topSummaryTextColor = value; RedrawChart(); }
        }

        private bool _useChartEs = true;
        [Display(GroupName = "5. Top summary", Name = "Use chart price as ES", Order =170)]
        public bool UseChartPriceAsES
        {
            get => _useChartEs;
            set { _useChartEs = value; }
        }

        // Strike horizontal lines
        private bool _showStrikeLines = false;
        [Display(GroupName = "6. Strike lines", Name = "Show strike lines", Order =10)]
        public bool ShowStrikeLines
        {
            get => _showStrikeLines;
            set { _showStrikeLines = value; RedrawChart(); }
        }

        private Color _strikeLineColor = Color.FromArgb(60,200,200,200);
        [Display(GroupName = "6. Strike lines", Name = "Line color", Order =20)]
        public Color StrikeLineColor
        {
            get => _strikeLineColor;
            set { _strikeLineColor = value; RedrawChart(); }
        }

        private int _strikeLineThickness =1;
        [Display(GroupName = "6. Strike lines", Name = "Thickness", Order =30)]
        [Range(1,10)]
        public int StrikeLineThickness
        {
            get => _strikeLineThickness;
            set { _strikeLineThickness = Math.Clamp(value,1,10); RedrawChart(); }
        }

        private DashStyle _strikeLineDash = DashStyle.Solid;
        [Display(GroupName = "6. Strike lines", Name = "Dash style", Order =40)]
        public DashStyle StrikeLineDash
        {
            get => _strikeLineDash;
            set { _strikeLineDash = value; RedrawChart(); }
        }

        // Max lines (dominant strikes)
        private bool _showMaxCallsLine = true;
        [Display(GroupName = "7. Max lines", Name = "Show Max Calls line", Order =10)]
        public bool ShowMaxCallsLine
        {
            get => _showMaxCallsLine;
            set { _showMaxCallsLine = value; RedrawChart(); }
        }

        private bool _showMaxPutsLine = true;
        [Display(GroupName = "7. Max lines", Name = "Show Max Puts line", Order =20)]
        public bool ShowMaxPutsLine
        {
            get => _showMaxPutsLine;
            set { _showMaxPutsLine = value; RedrawChart(); }
        }

        private Color _maxCallsLineColor = Color.DodgerBlue;
        [Display(GroupName = "7. Max lines", Name = "Max Calls color", Order =30)]
        public Color MaxCallsLineColor
        {
            get => _maxCallsLineColor;
            set { _maxCallsLineColor = value; RedrawChart(); }
        }

        private Color _maxPutsLineColor = Color.IndianRed;
        [Display(GroupName = "7. Max lines", Name = "Max Puts color", Order =40)]
        public Color MaxPutsLineColor
        {
            get => _maxPutsLineColor;
            set { _maxPutsLineColor = value; RedrawChart(); }
        }

        private int _maxLinesThickness =2;
        [Display(GroupName = "7. Max lines", Name = "Max lines thickness", Order =50)]
        [Range(1,20)]
        public int MaxLinesThickness
        {
            get => _maxLinesThickness;
            set { _maxLinesThickness = Math.Clamp(value,1,20); RedrawChart(); }
        }

        private DashStyle _maxLinesDash = DashStyle.Solid;
        [Display(GroupName = "7. Max lines", Name = "Max lines dash style", Order =60)]
        public DashStyle MaxLinesDash
        {
            get => _maxLinesDash;
            set { _maxLinesDash = value; RedrawChart(); }
        }

        // Net levels (Calls - Puts) por strike
        private bool _showNetLevels = true;
        [Display(GroupName = "8. Net levels", Name = "Show net levels (Calls-Puts)", Order =10)]
        public bool ShowNetLevels
        {
            get => _showNetLevels;
            set { _showNetLevels = value; RedrawChart(); }
        }

        private int _netThicknessPx =9;
        [Display(GroupName = "8. Net levels", Name = "Net level thickness (px)", Order =20)]
        [Range(2,60)]
        public int NetThicknessPx
        {
            get => _netThicknessPx;
            set { _netThicknessPx = Math.Clamp(value,2,60); RedrawChart(); }
        }

        private int _netOpacity =200;
        [Display(GroupName = "8. Net levels", Name = "Net level opacity", Order =30)]
        [Range(0,255)]
        public int NetOpacity
        {
            get => _netOpacity;
            set { _netOpacity = Math.Clamp(value,0,255); RedrawChart(); }
        }

        private Color _netCallsColor = Color.DodgerBlue;
        [Display(GroupName = "8. Net levels", Name = "Net Calls color", Order =40)]
        public Color NetCallsColor
        {
            get => _netCallsColor;
            set { _netCallsColor = value; RedrawChart(); }
        }

        private Color _netPutsColor = Color.IndianRed;
        [Display(GroupName = "8. Net levels", Name = "Net Puts color", Order =50)]
        public Color NetPutsColor
        {
            get => _netPutsColor;
            set { _netPutsColor = value; RedrawChart(); }
        }

        // Nuevos: Colores NET por perfil (IV, Delta, GEX)
        private Color _netCallsColorIv = Color.MediumSeaGreen;
        [Display(GroupName = "8. Net levels", Name = "Net Calls color (IV)", Order =60)]
        public Color NetCallsColorIV
        {
            get => _netCallsColorIv;
            set { _netCallsColorIv = value; RedrawChart(); }
        }

        private Color _netPutsColorIv = Color.Salmon;
        [Display(GroupName = "8. Net levels", Name = "Net Puts color (IV)", Order =70)]
        public Color NetPutsColorIV
        {
            get => _netPutsColorIv;
            set { _netPutsColorIv = value; RedrawChart(); }
        }

        private Color _netCallsColorDelta = Color.SteelBlue;
        [Display(GroupName = "8. Net levels", Name = "Net Calls color (Delta)", Order =80)]
        public Color NetCallsColorDelta
        {
            get => _netCallsColorDelta;
            set { _netCallsColorDelta = value; RedrawChart(); }
        }

        private Color _netPutsColorDelta = Color.Sienna;
        [Display(GroupName = "8. Net levels", Name = "Net Puts color (Delta)", Order =90)]
        public Color NetPutsColorDelta
        {
            get => _netPutsColorDelta;
            set { _netPutsColorDelta = value; RedrawChart(); }
        }

        private Color _netCallsColorGex = Color.MediumOrchid;
        [Display(GroupName = "8. Net levels", Name = "Net Calls color (GEX)", Order =91)]
        public Color NetCallsColorGEX
        {
            get => _netCallsColorGex;
            set { _netCallsColorGex = value; RedrawChart(); }
        }

        private Color _netPutsColorGex = Color.Orchid;
        [Display(GroupName = "8. Net levels", Name = "Net Puts color (GEX)", Order =92)]
        public Color NetPutsColorGEX
        {
            get => _netPutsColorGex;
            set { _netPutsColorGex = value; RedrawChart(); }
        }

        // Previous value markers (for last snapshot)
        public enum PrevMarkerMode { Calls, Puts, Both }
        private bool _showPrevMarkers = false;
        private PrevMarkerMode _prevMarkerMode = PrevMarkerMode.Calls;
        private int _prevMarkerSize =6;
        private Color _prevMarkerCallsColor = Color.LightSkyBlue;
        private Color _prevMarkerPutsColor = Color.Salmon;

        [Display(GroupName = "9. Previous markers", Name = "Show previous markers", Order =10)]
        public bool ShowPreviousMarkers
        {
            get => _showPrevMarkers;
            set { _showPrevMarkers = value; RedrawChart(); }
        }

        [Display(GroupName = "9. Previous markers", Name = "Marker mode", Order =20)]
        public PrevMarkerMode PreviousMarkerMode
        {
            get => _prevMarkerMode;
            set { _prevMarkerMode = value; }
        }

        [Display(GroupName = "9. Previous markers", Name = "Marker size (px)", Order =30)]
        [Range(2,20)]
        public int PreviousMarkerSize
        {
            get => _prevMarkerSize;
            set { _prevMarkerSize = Math.Clamp(value,2,20); RedrawChart(); }
        }

        [Display(GroupName = "9. Previous markers", Name = "Calls marker color", Order =40)]
        public Color PreviousMarkerCallsColor
        {
            get => _prevMarkerCallsColor;
            set { _prevMarkerCallsColor = value; RedrawChart(); }
        }

        [Display(GroupName = "9. Previous markers", Name = "Puts marker color", Order =50)]
        public Color PreviousMarkerPutsColor
        {
            get => _prevMarkerPutsColor;
            set { _prevMarkerPutsColor = value; RedrawChart(); }
        }

        // Previous NET markers (Calls - Puts)
        private bool _showPrevNetMarkers = false;
        private int _prevNetMarkerSize =6;
        private Color _prevNetPositiveColor = Color.LightGreen;
        private Color _prevNetNegativeColor = Color.LightCoral;

        [Display(GroupName = "9. Previous markers", Name = "Show previous NET markers", Order =60)]
        public bool ShowPreviousNetMarkers
        {
            get => _showPrevNetMarkers;
            set { _showPrevNetMarkers = value; RedrawChart(); }
        }

        [Display(GroupName = "9. Previous markers", Name = "NET marker size (px)", Order =70)]
        [Range(2,20)]
        public int PreviousNetMarkerSize
        {
            get => _prevNetMarkerSize;
            set { _prevNetMarkerSize = Math.Clamp(value,2,20); RedrawChart(); }
        }

        [Display(GroupName = "9. Previous markers", Name = "NET positive color", Order =80)]
        public Color PreviousNetPositiveColor
        {
            get => _prevNetPositiveColor;
            set { _prevNetPositiveColor = value; RedrawChart(); }
        }

        [Display(GroupName = "9. Previous markers", Name = "NET negative color", Order =90)]
        public Color PreviousNetNegativeColor
        {
            get => _prevNetNegativeColor;
            set { _prevNetNegativeColor = value; RedrawChart(); }
        }

        // 11. Alerts - NET change panel
        private bool _showNetChangePanel = false;
        private decimal _netChangeThresholdPercent = 5m;
        private int _netChangeMaxItems = 5;
        private int _netChangeFontSize = 10;
        private Color _netChangeTextColor = Color.Khaki;

        [Display(GroupName = "11. Alerts", Name = "Show NET change panel", Order = 10)]
        public bool ShowNetChangePanel
        {
            get => _showNetChangePanel;
            set { _showNetChangePanel = value; RedrawChart(); }
        }

        [Display(GroupName = "11. Alerts", Name = "NET change threshold %", Order = 20)]
        [Range(0, 1000)]
        public decimal NetChangeThresholdPercent
        {
            get => _netChangeThresholdPercent;
            set { _netChangeThresholdPercent = Math.Clamp(value, 0m, 1000m); }
        }

        [Display(GroupName = "11. Alerts", Name = "Max items", Order = 30)]
        [Range(1, 100)]
        public int NetChangeMaxItems
        {
            get => _netChangeMaxItems;
            set { _netChangeMaxItems = Math.Clamp(value, 1, 100); }
        }

        [Display(GroupName = "11. Alerts", Name = "Font size", Order = 40)]
        [Range(6, 40)]
        public int NetChangeFontSize
        {
            get => _netChangeFontSize;
            set { _netChangeFontSize = Math.Clamp(value, 6, 40); }
        }

        [Display(GroupName = "11. Alerts", Name = "Text color", Order = 50)]
        public Color NetChangeTextColor
        {
            get => _netChangeTextColor;
            set { _netChangeTextColor = value; }
        }

        // 12. Big trades (marcadores de incrementos grandes por strike)
        private bool _showBigTradeMarkers = false;
        private decimal _bigTradeThreshold = 1000m; // cambio mínimo para mostrar
        private int _bigTradeBaseRadius = 6; // radio mínimo
        private int _bigTradeMaxRadius = 40; // radio máximo
        private decimal _bigTradeRadiusPerUnit = 0.01m; // incremento de radio por unidad sobre el umbral
        private int _bigTradeOffsetPx = 6; // separación desde el extremo de la barra
        private Color _bigTradeCallsColor = Color.Gold;
        private Color _bigTradePutsColor = Color.OrangeRed;
        private bool _bigTradeShowValue = true;
        private Color _bigTradeTextColor = Color.Black;
        private int _bigTradeFontSize = 8;

        [Display(GroupName = "12. Big trades", Name = "Show big trade markers", Order = 10)]
        public bool ShowBigTradeMarkers
        {
            get => _showBigTradeMarkers;
            set { _showBigTradeMarkers = value; RedrawChart(); }
        }

        [Display(GroupName = "12. Big trades", Name = "Change threshold", Order = 20)]
        [Range(1, 100000000)]
        public decimal BigTradeThreshold
        {
            get => _bigTradeThreshold;
            set { _bigTradeThreshold = Math.Clamp(value, 1m, 100000000m); }
        }

        [Display(GroupName = "12. Big trades", Name = "Base radius (px)", Order = 30)]
        [Range(2, 200)]
        public int BigTradeBaseRadius
        {
            get => _bigTradeBaseRadius;
            set { _bigTradeBaseRadius = Math.Clamp(value, 2, 200); RedrawChart(); }
        }

        [Display(GroupName = "12. Big trades", Name = "Max radius (px)", Order = 40)]
        [Range(2, 400)]
        public int BigTradeMaxRadius
        {
            get => _bigTradeMaxRadius;
            set { _bigTradeMaxRadius = Math.Clamp(value, 2, 400); RedrawChart(); }
        }

        [Display(GroupName = "12. Big trades", Name = "Radius per unit", Order = 50)]
        public decimal BigTradeRadiusPerUnit
        {
            get => _bigTradeRadiusPerUnit;
            set { _bigTradeRadiusPerUnit = value <= 0 ? 0.0001m : value; }
        }

        [Display(GroupName = "12. Big trades", Name = "Offset from bar (px)", Order = 60)]
        [Range(0, 500)]
        public int BigTradeOffsetPx
        {
            get => _bigTradeOffsetPx;
            set { _bigTradeOffsetPx = Math.Clamp(value, 0, 500); RedrawChart(); }
        }

        [Display(GroupName = "12. Big trades", Name = "Calls marker color", Order = 70)]
        public Color BigTradeCallsColor
        {
            get => _bigTradeCallsColor;
            set { _bigTradeCallsColor = value; RedrawChart(); }
        }

        [Display(GroupName = "12. Big trades", Name = "Puts marker color", Order = 80)]
        public Color BigTradePutsColor
        {
            get => _bigTradePutsColor;
            set { _bigTradePutsColor = value; RedrawChart(); }
        }

        [Display(GroupName = "12. Big trades", Name = "Show value inside", Order = 90)]
        public bool BigTradeShowValue
        {
            get => _bigTradeShowValue;
            set { _bigTradeShowValue = value; RedrawChart(); }
        }

        [Display(GroupName = "12. Big trades", Name = "Value text color", Order = 100)]
        public Color BigTradeTextColor
        {
            get => _bigTradeTextColor;
            set { _bigTradeTextColor = value; RedrawChart(); }
        }

        [Display(GroupName = "12. Big trades", Name = "Font size", Order = 110)]
        [Range(6, 60)]
        public int BigTradeFontSize
        {
            get => _bigTradeFontSize;
            set { _bigTradeFontSize = Math.Clamp(value, 6, 60); RedrawChart(); }
        }

        // 13. Persistent Big Trades (nuevas opciones)
        private bool _showPersistentBigTrades = false;
        private int _persistentBigTradeBaseRadius = 8;
        private int _persistentBigTradeMaxRadius = 50;
        private decimal _persistentBigTradeRadiusPerUnit = 0.01m;
        private int _persistentBigTradeOffsetPx = 15;
        private Color _persistentBigTradeCallsColor = Color.LimeGreen;
        private Color _persistentBigTradePutsColor = Color.Salmon;
        private bool _persistentBigTradeShowValue = true;
        private Color _persistentBigTradeTextColor = Color.White;
        private int _persistentBigTradeFontSize = 9;
        private int _persistentBigTradeOpacity = 160;
        private bool _persistentBigTradeShowTime = true;
        private Color _persistentBigTradeTimeColor = Color.LightGray;
        private int _persistentBigTradeTimeFontSize = 7;

        [Display(GroupName = "13. Persistent Big Trades", Name = "Show persistent big trades", Order = 10)]
        public bool ShowPersistentBigTrades
        {
            get => _showPersistentBigTrades;
            set { _showPersistentBigTrades = value; RedrawChart(); }
        }

        [Display(GroupName = "13. Persistent Big Trades", Name = "Base radius (px)", Order = 20)]
        [Range(2, 200)]
        public int PersistentBigTradeBaseRadius
        {
            get => _persistentBigTradeBaseRadius;
            set { _persistentBigTradeBaseRadius = Math.Clamp(value, 2, 200); RedrawChart(); }
        }

        [Display(GroupName = "13. Persistent Big Trades", Name = "Max radius (px)", Order = 30)]
        [Range(2, 400)]
        public int PersistentBigTradeMaxRadius
        {
            get => _persistentBigTradeMaxRadius;
            set { _persistentBigTradeMaxRadius = Math.Clamp(value, 2, 400); RedrawChart(); }
        }

        [Display(GroupName = "13. Persistent Big Trades", Name = "Radius per unit", Order = 40)]
        public decimal PersistentBigTradeRadiusPerUnit
        {
            get => _persistentBigTradeRadiusPerUnit;
            set { _persistentBigTradeRadiusPerUnit = value <= 0 ? 0.0001m : value; }
        }

        [Display(GroupName = "13. Persistent Big Trades", Name = "Offset from bar (px)", Order = 50)]
        [Range(0, 500)]
        public int PersistentBigTradeOffsetPx
        {
            get => _persistentBigTradeOffsetPx;
            set { _persistentBigTradeOffsetPx = Math.Clamp(value, 0, 500); RedrawChart(); }
        }

        [Display(GroupName = "13. Persistent Big Trades", Name = "Calls marker color", Order = 60)]
        public Color PersistentBigTradeCallsColor
        {
            get => _persistentBigTradeCallsColor;
            set { _persistentBigTradeCallsColor = value; RedrawChart(); }
        }

        [Display(GroupName = "13. Persistent Big Trades", Name = "Puts marker color", Order = 70)]
        public Color PersistentBigTradePutsColor
        {
            get => _persistentBigTradePutsColor;
            set { _persistentBigTradePutsColor = value; RedrawChart(); }
        }

        [Display(GroupName = "13. Persistent Big Trades", Name = "Show value inside", Order = 80)]
        public bool PersistentBigTradeShowValue
        {
            get => _persistentBigTradeShowValue;
            set { _persistentBigTradeShowValue = value; RedrawChart(); }
        }

        [Display(GroupName = "13. Persistent Big Trades", Name = "Value text color", Order = 90)]
        public Color PersistentBigTradeTextColor
        {
            get => _persistentBigTradeTextColor;
            set { _persistentBigTradeTextColor = value; RedrawChart(); }
        }

        [Display(GroupName = "13. Persistent Big Trades", Name = "Value font size", Order = 100)]
        [Range(6, 60)]
        public int PersistentBigTradeFontSize
        {
            get => _persistentBigTradeFontSize;
            set { _persistentBigTradeFontSize = Math.Clamp(value, 6, 60); RedrawChart(); }
        }

        [Display(GroupName = "13. Persistent Big Trades", Name = "Circle opacity", Order = 110)]
        [Range(0, 255)]
        public int PersistentBigTradeOpacity
        {
            get => _persistentBigTradeOpacity;
            set { _persistentBigTradeOpacity = Math.Clamp(value, 0, 255); RedrawChart(); }
        }

        [Display(GroupName = "13. Persistent Big Trades", Name = "Show detection time", Order = 120)]
        public bool PersistentBigTradeShowTime
        {
            get => _persistentBigTradeShowTime;
            set { _persistentBigTradeShowTime = value; RedrawChart(); }
        }

        [Display(GroupName = "13. Persistent Big Trades", Name = "Time text color", Order = 130)]
        public Color PersistentBigTradeTimeColor
        {
            get => _persistentBigTradeTimeColor;
            set { _persistentBigTradeTimeColor = value; RedrawChart(); }
        }

        [Display(GroupName = "13. Persistent Big Trades", Name = "Time font size", Order = 140)]
        [Range(6, 20)]
        public int PersistentBigTradeTimeFontSize
        {
            get => _persistentBigTradeTimeFontSize;
            set { _persistentBigTradeTimeFontSize = Math.Clamp(value, 6, 20); RedrawChart(); }
        }

        public CashProfile()
        {
            EnableCustomDrawing = true;
            DenyToChangePanel = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);

            _timer.AutoReset = true;
            _timer.Elapsed += OnTimer;
            _timer.Start();

            // Hotkey polling timer (más fiable que depender del render loop)
            _hotkeyTimer = new System.Timers.Timer(30) { AutoReset = true, Enabled = true };
            _hotkeyTimer.Elapsed += OnHotkeyTimer;
            _hotkeyTimer.Start();

            // Auto conversion timer (desactivado por defecto)
            _autoConvTimer = new System.Timers.Timer(10000) { AutoReset = true, Enabled = false };
            _autoConvTimer.Elapsed += OnAutoConvTimer;
        }

        protected override void OnInitialize()
        {
            ResetTimer();
            LoadCsvSafe();
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            _lastEsPrice = value; // usar precio del gráfico como ES en tiempo real
        }

        // Hotkey handling: cycle profile before drawing
        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            // Nota: el manejo de hotkeys se hace en _hotkeyTimer para no perder pulsaciones
            if (ChartInfo?.PriceChartContainer == null)
            {
                context.DrawString("Chart not ready", new RenderFont("Arial",10), Color.Red,10,10);
                return;
            }

            var xBase = ChartInfo.PriceChartContainer.GetXByBar(CurrentBar, false);
            var xCenter = xBase + _centerOffsetPx;
            var fullWidth = ChartInfo.Region.Width;

            List<StrikeRow> snapshot;
            Dictionary<decimal, (decimal calls, decimal puts)> prevBySpy;
            Dictionary<decimal, (decimal calls, decimal puts)> prevByStrike;
            lock (_sync)
            {
                snapshot = _rows.ToList();
                prevBySpy = new Dictionary<decimal, (decimal calls, decimal puts)>(_prevBySpy);
                prevByStrike = new Dictionary<decimal, (decimal calls, decimal puts)>(_prevByStrike);
            }

            if (!string.IsNullOrEmpty(_error))
            {
                context.DrawString(_error, new RenderFont("Arial",10), Color.Red,10,10);
            }

            if (snapshot.Count ==0)
            {
                if (string.IsNullOrEmpty(_error))
                    context.DrawString("No data", new RenderFont("Arial",10), Color.Gray,10,10);
                return;
            }

            // Scale per side
            double maxCalls;
            double maxPuts;
            if (_profileType == ProfileDataType.Gex)
            {
                maxCalls = snapshot.Max(r => (double)Math.Abs(r.Calls));
                maxPuts = snapshot.Max(r => (double)Math.Abs(r.Puts));
            }
            else
            {
                maxCalls = snapshot.Max(r => (double)r.Calls);
                maxPuts = snapshot.Max(r => (double)r.Puts);
            }
            var maxSide = Math.Max(maxCalls, maxPuts);
            if (maxSide <=0)
                return;

            var scale = MaxBarWidthPx / maxSide; // px per unit for each side

            // Sort by strike to draw consistently
            snapshot.Sort((a, b) => a.Strike.CompareTo(b.Strike));

            if (_showCenterLine)
            {
                var pen = new RenderPen(_centerLineColor, _centerLineThickness);
                context.DrawLine(pen, xCenter,0, xCenter, ChartInfo.Region.Height);
            }

            var sideFont = new RenderFont("Arial",8);
            var strikeFont = new RenderFont("Arial", _strikeFontSize);

            foreach (var row in snapshot)
            {
                var y = ChartInfo.PriceChartContainer.GetYByPrice(row.Strike, false);
                var top = y - _barThicknessPx /2;

                var callsMag = _profileType == ProfileDataType.Gex ? Math.Abs(row.Calls) : row.Calls;
                var putsMag = _profileType == ProfileDataType.Gex ? Math.Abs(row.Puts) : row.Puts;
                var callsW = (int)Math.Round((double)callsMag * scale);
                var putsW = (int)Math.Round((double)putsMag * scale);

                // Selección de color por perfil
                Color callsBase, putsBase;
                switch (_profileType)
                {
                    case ProfileDataType.IV:
                        callsBase = _ivCallsColor; putsBase = _ivPutsColor; break;
                    case ProfileDataType.Delta:
                        callsBase = _deltaCallsColor; putsBase = _deltaPutsColor; break;
                    case ProfileDataType.Gex:
                        callsBase = _gexCallsColor; putsBase = _gexPutsColor; break;
                    case ProfileDataType.Cash:
                    case ProfileDataType.Custom:
                    default:
                        callsBase = _callsColor; putsBase = _putsColor; break;
                }

                // Decide which side gets which
                int rightW = _callsOnRight ? callsW : putsW;
                int leftW = _callsOnRight ? putsW : callsW;
                Color rightColor = _callsOnRight ? callsBase : putsBase;
                Color leftColor = _callsOnRight ? putsBase : callsBase;

                // Optional strike horizontal line
                if (ShowStrikeLines)
                {
                    var spen = new RenderPen(_strikeLineColor, _strikeLineThickness) { DashStyle = _strikeLineDash };
                    context.DrawLine(spen,0, y, fullWidth, y);
                }

                // Left segment
                if (leftW >0)
                {
                    var rectL = new Rectangle(xCenter - leftW, top, leftW, _barThicknessPx);
                    context.FillRectangle(Color.FromArgb(_fillOpacity, leftColor), rectL);
                }
                // Right segment
                if (rightW >0)
                {
                    var rectR = new Rectangle(xCenter, top, rightW, _barThicknessPx);
                    context.FillRectangle(Color.FromArgb(_fillOpacity, rightColor), rectR);
                }

                // Previous markers (tip position of previous snapshot for Calls/Puts)
                if (_showPrevMarkers)
                {
                    // Try match by SPY strike first (stable), then by positioned strike
                    (decimal prevCalls, decimal prevPuts) prev;
                    if (!prevBySpy.TryGetValue(row.StrikeSpy, out prev))
                        prevByStrike.TryGetValue(row.Strike, out prev);

                    // Calls marker (on the calls side)
                    if (_prevMarkerMode == PrevMarkerMode.Calls || _prevMarkerMode == PrevMarkerMode.Both)
                    {
                        var prevCallsMag = _profileType == ProfileDataType.Gex ? Math.Abs(prev.prevCalls) : prev.prevCalls;
                        var prevCallsW = (int)Math.Round((double)prevCallsMag * scale);
                        if (prevCallsMag >0 && prevCallsW >=0)
                        {
                            int size = _prevMarkerSize;
                            int mx = _callsOnRight ? (xCenter + prevCallsW) : (xCenter - prevCallsW);
                            int my = y;
                            var mRect = new Rectangle(mx - size /2, my - size /2, size, size);
                            context.FillRectangle(_prevMarkerCallsColor, mRect);
                        }
                    }
                    // Puts marker (on the puts side)
                    if (_prevMarkerMode == PrevMarkerMode.Puts || _prevMarkerMode == PrevMarkerMode.Both)
                    {
                        var prevPutsMag = _profileType == ProfileDataType.Gex ? Math.Abs(prev.prevPuts) : prev.prevPuts;
                        var prevPutsW = (int)Math.Round((double)prevPutsMag * scale);
                        if (prevPutsMag >0 && prevPutsW >=0)
                        {
                            int size = _prevMarkerSize;
                            int mx = _callsOnRight ? (xCenter - prevPutsW) : (xCenter + prevPutsW);
                            int my = y;
                            var mRect = new Rectangle(mx - size /2, my - size /2, size, size);
                            context.FillRectangle(_prevMarkerPutsColor, mRect);
                        }
                    }
                }

                // Net level (Calls - Puts) o GEX (Calls + Puts si Puts<0)
                if (_showNetLevels)
                {
                    var diff = _profileType == ProfileDataType.Gex
                        ? (row.Calls + row.Puts)
                        : (row.Calls - row.Puts);
                    if (diff !=0)
                    {
                        int w = (int)Math.Round((double)Math.Abs(diff) * scale);
                        if (w >0)
                        {
                            int t = _netThicknessPx;
                            int topNet = y - t /2;

                            // Selección de colores NET por perfil
                            Color netCallsBase, netPutsBase;
                            switch (_profileType)
                            {
                                case ProfileDataType.IV:
                                    netCallsBase = _netCallsColorIv; netPutsBase = _netPutsColorIv; break;
                                case ProfileDataType.Delta:
                                    netCallsBase = _netCallsColorDelta; netPutsBase = _netPutsColorDelta; break;
                                case ProfileDataType.Gex:
                                    netCallsBase = _netCallsColorGex; netPutsBase = _netPutsColorGex; break;
                                case ProfileDataType.Cash:
                                case ProfileDataType.Custom:
                                default:
                                    netCallsBase = _netCallsColor; netPutsBase = _netPutsColor; break;
                            }

                            if (diff >0)
                            {
                                // Dominio Calls -> dibujar hacia Calls
                                if (_callsOnRight)
                                {
                                    var r = new Rectangle(xCenter, topNet, w, t);
                                    context.FillRectangle(Color.FromArgb(_netOpacity, netCallsBase), r);
                                }
                                else
                                {
                                    var r = new Rectangle(xCenter - w, topNet, w, t);
                                    context.FillRectangle(Color.FromArgb(_netOpacity, netCallsBase), r);
                                }
                            }
                            else // diff <0 -> Dominio Puts
                            {
                                if (_callsOnRight)
                                {
                                    var r = new Rectangle(xCenter - w, topNet, w, t);
                                    context.FillRectangle(Color.FromArgb(_netOpacity, netPutsBase), r);
                                }
                                else
                                {
                                    var r = new Rectangle(xCenter, topNet, w, t);
                                    context.FillRectangle(Color.FromArgb(_netOpacity, netPutsBase), r);
                                }
                            }
                        }
                    }
                }

                // Previous NET marker (tip position of previous net bar)
                if (_showPrevNetMarkers)
                {
                    (decimal prevCalls, decimal prevPuts) prev;
                    if (!prevBySpy.TryGetValue(row.StrikeSpy, out prev))
                        prevByStrike.TryGetValue(row.Strike, out prev);

                    var prevDiff = _profileType == ProfileDataType.Gex
                        ? (prev.prevCalls + prev.prevPuts)
                        : (prev.prevCalls - prev.prevPuts);
                    if (prevDiff !=0)
                    {
                        int prevW = (int)Math.Round((double)Math.Abs(prevDiff) * scale);
                        if (prevW >=0)
                        {
                            int size = _prevNetMarkerSize;
                            int sgn =0;
                            if (prevDiff >0)
                                sgn = _callsOnRight ? +1 : -1;
                            else if (prevDiff <0)
                                sgn = _callsOnRight ? -1 : +1;

                            int mx = xCenter + sgn * prevW;
                            int my = y;
                            var color = prevDiff >0 ? _prevNetPositiveColor : _prevNetNegativeColor;
                            var mRect = new Rectangle(mx - size /2, my - size /2, size, size);
                            context.FillRectangle(color, mRect);
                        }
                    }
                }

                // Big trade markers (incrementos grandes respecto al snapshot previo)
                if (_showBigTradeMarkers)
                {
                    (decimal prevCalls, decimal prevPuts) prevVals;
                    if (!prevBySpy.TryGetValue(row.StrikeSpy, out prevVals))
                        prevByStrike.TryGetValue(row.Strike, out prevVals);

                    // Diferencias según perfil (en GEX usamos magnitudes, en otros valores directos)
                    decimal callsDiff = _profileType == ProfileDataType.Gex
                        ? Math.Abs(row.Calls) - Math.Abs(prevVals.prevCalls)
                        : (row.Calls - prevVals.prevCalls);
                    decimal putsDiff = _profileType == ProfileDataType.Gex
                        ? Math.Abs(row.Puts) - Math.Abs(prevVals.prevPuts)
                        : (row.Puts - prevVals.prevPuts);

                    // Dibujar marcador para Calls
                    if (callsDiff >= _bigTradeThreshold && callsDiff > 0)
                    {
                        int radius = _bigTradeBaseRadius + (int)Math.Round((double)((callsDiff - _bigTradeThreshold) * _bigTradeRadiusPerUnit));
                        radius = Math.Clamp(radius, _bigTradeBaseRadius, _bigTradeMaxRadius);
                        int tipX = _callsOnRight ? (xCenter + callsW) : (xCenter - callsW);
                        int cx = _callsOnRight ? (tipX + _bigTradeOffsetPx + radius) : (tipX - _bigTradeOffsetPx - radius);
                        int cy = y;
                        var ellipseRect = new Rectangle(cx - radius, cy - radius, radius * 2, radius * 2);
                        try { context.FillEllipse(Color.FromArgb(180, _bigTradeCallsColor), ellipseRect); } catch { context.FillRectangle(Color.FromArgb(180, _bigTradeCallsColor), ellipseRect); }

                        if (_bigTradeShowValue)
                        {
                            var fontBt = new RenderFont("Arial", _bigTradeFontSize);
                            string txt = FormatCompact(callsDiff);
                            int tw = EstimateTextWidth(txt, fontBt);
                            int tx = cx - tw / 2;
                            int ty = cy - (_bigTradeFontSize / 2);
                            context.DrawString(txt, fontBt, _bigTradeTextColor, tx, ty);
                        }
                    }

                    // Dibujar marcador para Puts
                    if (putsDiff >= _bigTradeThreshold && putsDiff > 0)
                    {
                        int radius = _bigTradeBaseRadius + (int)Math.Round((double)((putsDiff - _bigTradeThreshold) * _bigTradeRadiusPerUnit));
                        radius = Math.Clamp(radius, _bigTradeBaseRadius, _bigTradeMaxRadius);
                        int tipX = _callsOnRight ? (xCenter - putsW) : (xCenter + putsW);
                        int cx = _callsOnRight ? (tipX - _bigTradeOffsetPx - radius) : (tipX + _bigTradeOffsetPx + radius);
                        int cy = y;
                        var ellipseRect = new Rectangle(cx - radius, cy - radius, radius * 2, radius * 2);
                        try { context.FillEllipse(Color.FromArgb(180, _bigTradePutsColor), ellipseRect); } catch { context.FillRectangle(Color.FromArgb(180, _bigTradePutsColor), ellipseRect); }

                        if (_bigTradeShowValue)
                        {
                            var fontBt = new RenderFont("Arial", _bigTradeFontSize);
                            string txt = FormatCompact(putsDiff);
                            int tw = EstimateTextWidth(txt, fontBt);
                            int tx = cx - tw / 2;
                            int ty = cy - (_bigTradeFontSize / 2);
                            context.DrawString(txt, fontBt, _bigTradeTextColor, tx, ty);
                        }
                    }
                }

                // NUEVO: Persistent Big Trades - detectar y almacenar
                if (_showPersistentBigTrades && _showBigTradeMarkers)
                {
                    (decimal prevCalls, decimal prevPuts) prevVals;
                    if (!prevBySpy.TryGetValue(row.StrikeSpy, out prevVals))
                        prevByStrike.TryGetValue(row.Strike, out prevVals);

                    decimal callsDiff = _profileType == ProfileDataType.Gex
                        ? Math.Abs(row.Calls) - Math.Abs(prevVals.prevCalls)
                        : (row.Calls - prevVals.prevCalls);
                    decimal putsDiff = _profileType == ProfileDataType.Gex
                        ? Math.Abs(row.Puts) - Math.Abs(prevVals.prevPuts)
                        : (row.Puts - prevVals.prevPuts);

                    // Detectar Call Big Trade y almacenar
                    if (callsDiff >= _bigTradeThreshold && callsDiff > 0)
                    {
                        lock (_sync)
                        {
                            _persistentBigTrades.Add(new PersistentBigTrade
                            {
                                StrikeSpy = row.StrikeSpy,
                                Strike = row.Strike,
                                Value = callsDiff,
                                IsCall = true,
                                DetectionTime = DateTime.Now,
                                Bar = CurrentBar
                            });
                        }
                    }

                    // Detectar Put Big Trade y almacenar
                    if (putsDiff >= _bigTradeThreshold && putsDiff > 0)
                    {
                        lock (_sync)
                        {
                            _persistentBigTrades.Add(new PersistentBigTrade
                            {
                                StrikeSpy = row.StrikeSpy,
                                Strike = row.Strike,
                                Value = putsDiff,
                                IsCall = false,
                                DetectionTime = DateTime.Now,
                                Bar = CurrentBar
                            });
                        }
                    }
                }

                // Values and strikes near bars
                if (_showValues)
                {
                    // left value
                    if (leftW >0)
                    {
                        var leftVal = _callsOnRight ? putsMag : callsMag;
                        var valTxt = leftVal.ToString(_valueFormat, CultureInfo.InvariantCulture);
                        int valW = EstimateTextWidth(valTxt, sideFont);
                        int valX = xCenter - leftW - valW -6;
                        int valY = top -2;
                        context.DrawString(valTxt, sideFont, leftColor, valX, valY);

                        if (_showCenterStrikes)
                        {
                            var sTxt = $"({row.StrikeSpy.ToString(_strikeFormat, CultureInfo.InvariantCulture)})";
                            int sW = EstimateTextWidth(sTxt, strikeFont);
                            int sx = valX - sW - _strikeLeftMarginPx; // a la izquierda del valor con margen configurable
                            int sy = y - (_strikeFontSize /2) -1;
                            context.DrawString(sTxt, strikeFont, _strikeColor, sx, sy);
                        }
                    }

                    // right value
                    if (rightW >0)
                    {
                        var rightVal = _callsOnRight ? callsMag : putsMag;
                        var valTxt = rightVal.ToString(_valueFormat, CultureInfo.InvariantCulture);
                        int valX = xCenter + rightW +6;
                        int valY = top -2;
                        context.DrawString(valTxt, sideFont, rightColor, valX, valY);

                        if (_showCenterStrikes)
                        {
                            int valW = EstimateTextWidth(valTxt, sideFont);
                            var sTxt = $"({row.StrikeSpy.ToString(_strikeFormat, CultureInfo.InvariantCulture)})";
                            int sx = valX + valW + _strikeRightMarginPx; // a la derecha del valor con margen configurable
                            int sy = y - (_strikeFontSize /2) -1;
                            context.DrawString(sTxt, strikeFont, _strikeColor, sx, sy);
                        }
                    }
                }
            }

            if (_showTopSummary)
                DrawTopSummary(context, xCenter, snapshot);

            // NUEVO: Dibujar Persistent Big Trades en el gráfico de precios (en coordenadas de strike)
            if (_showPersistentBigTrades && _persistentBigTrades.Count > 0)
            {
                lock (_sync)
                {
                    foreach (var pbt in _persistentBigTrades)
                    {
                        // Verificar que esté dentro del rango visible
                        if (pbt.Bar < FirstVisibleBarNumber || pbt.Bar > LastVisibleBarNumber)
                            continue;

                        // Obtener posición X del bar donde se detectó el big trade
                        int xBar = ChartInfo.PriceChartContainer.GetXByBar(pbt.Bar, false);
                        
                        // Obtener posición Y del strike donde ocurrió
                        int yStrike = ChartInfo.PriceChartContainer.GetYByPrice(pbt.Strike, false);

                        // Calcular radio del círculo en función del valor
                        int radius = _persistentBigTradeBaseRadius + (int)Math.Round((double)((pbt.Value - _bigTradeThreshold) * _persistentBigTradeRadiusPerUnit));
                        radius = Math.Clamp(radius, _persistentBigTradeBaseRadius, _persistentBigTradeMaxRadius);

                        // Color según Call/Put
                        Color markerColor = pbt.IsCall ? _persistentBigTradeCallsColor : _persistentBigTradePutsColor;
                        var ellipseRect = new Rectangle(xBar - radius, yStrike - radius, radius * 2, radius * 2);

                        // Dibujar el círculo del big trade
                        try 
                        { 
                            context.FillEllipse(Color.FromArgb(_persistentBigTradeOpacity, markerColor), ellipseRect);
                            context.DrawEllipse(new RenderPen(markerColor, 2), ellipseRect);
                        }
                        catch 
                        { 
                            context.FillRectangle(Color.FromArgb(_persistentBigTradeOpacity, markerColor), ellipseRect);
                        }

                        // Dibujar valor dentro del círculo
                        if (_persistentBigTradeShowValue)
                        {
                            var fontBt = new RenderFont("Arial", _persistentBigTradeFontSize);
                            string txt = FormatCompact(pbt.Value);
                            int tw = EstimateTextWidth(txt, fontBt);
                            int tx = xBar - tw / 2;
                            int ty = yStrike - (_persistentBigTradeFontSize / 2);
                            context.DrawString(txt, fontBt, _persistentBigTradeTextColor, tx, ty);
                        }

                        // Dibujar hora de detección debajo del círculo
                        if (_persistentBigTradeShowTime)
                        {
                            var fontTime = new RenderFont("Arial", _persistentBigTradeTimeFontSize);
                            string timeStr = pbt.DetectionTime.ToString("HH:mm");
                            int twd = EstimateTextWidth(timeStr, fontTime);
                            int txd = xBar - twd / 2;
                            int tyd = yStrike + radius + 8;
                            context.DrawString(timeStr, fontTime, _persistentBigTradeTimeColor, txd, tyd);
                        }
                    }
                }
            }

            // Max lines
            if (snapshot.Count >0)
            {
                var maxCallsRow = snapshot.OrderByDescending(r => _profileType == ProfileDataType.Gex ? Math.Abs(r.Calls) : r.Calls).FirstOrDefault();
                var maxPutsRow = snapshot.OrderByDescending(r => _profileType == ProfileDataType.Gex ? Math.Abs(r.Puts) : r.Puts).FirstOrDefault();
                if (_showMaxCallsLine && maxCallsRow != null && (_profileType == ProfileDataType.Gex ? Math.Abs(maxCallsRow.Calls) : maxCallsRow.Calls) >0)
                {
                    var yC = ChartInfo.PriceChartContainer.GetYByPrice(maxCallsRow.Strike, false);
                    var penC = new RenderPen(_maxCallsLineColor, _maxLinesThickness) { DashStyle = _maxLinesDash };
                    context.DrawLine(penC,0, yC, fullWidth, yC);
                }
                if (_showMaxPutsLine && maxPutsRow != null && (_profileType == ProfileDataType.Gex ? Math.Abs(maxPutsRow.Puts) : maxPutsRow.Puts) >0)
                {
                    var yP = ChartInfo.PriceChartContainer.GetYByPrice(maxPutsRow.Strike, false);
                    var penP = new RenderPen(_maxPutsLineColor, _maxLinesThickness) { DashStyle = _maxLinesDash };
                    context.DrawLine(penP,0, yP, fullWidth, yP);
                }
            }

            if (_lastLoad.HasValue)
            {
                context.DrawString($"Last load: {_lastLoad.Value:HH:mm:ss}", new RenderFont("Arial",8), Color.Gray,10,26);
            }

            // Draw NET change panel (top-left)
            if (_showNetChangePanel)
            {
                try
                {
                    var alertFont = new RenderFont("Arial", _netChangeFontSize);
                    int panelY = 42; // under the 'Last load' line
                    int panelX = 10;

                    // Build change list comparing to previous snapshot by SPY strike
                    var changes = new List<(decimal spyStrike, decimal changePct, decimal prevNet, decimal currNet)>();
                    foreach (var row in snapshot)
                    {
                        if (!_prevBySpy.TryGetValue(row.StrikeSpy, out var prevVals))
                            continue;
                        decimal prevNet = _profileType == ProfileDataType.Gex
                            ? (prevVals.calls + prevVals.puts)
                            : (prevVals.calls - prevVals.puts);
                        decimal currNet = _profileType == ProfileDataType.Gex
                            ? (row.Calls + row.Puts)
                            : (row.Calls - row.Puts);

                        if (prevNet == 0)
                            continue; // avoid div by zero / noisy items

                        var pct = (currNet - prevNet) / prevNet * 100m;
                        if (Math.Abs(pct) >= _netChangeThresholdPercent)
                            changes.Add((row.StrikeSpy, pct, prevNet, currNet));
                    }

                    if (changes.Count > 0)
                    {
                        // Order by magnitude desc and take max items
                        var ordered = changes
                            .OrderByDescending(c => Math.Abs(c.changePct))
                            .Take(_netChangeMaxItems)
                            .ToList();

                        // Header
                        string hdr = $"Cambios NET >= {_netChangeThresholdPercent}%:";
                        context.DrawString(hdr, alertFont, _netChangeTextColor, panelX, panelY);
                        panelY += _netChangeFontSize + 4;

                        foreach (var it in ordered)
                        {
                            string dir = it.changePct > 0 ? "aumenta" : "disminuye";
                            string side = it.currNet > 0 ? "Neto Calls" : (it.currNet < 0 ? "Neto Puts" : "Neto");
                            string line = $"Strike {it.spyStrike.ToString(_strikeFormat, CultureInfo.InvariantCulture)} {side} {dir} {Math.Abs(it.changePct):0.##}%";
                            context.DrawString(line, alertFont, _netChangeTextColor, panelX, panelY);
                            panelY += _netChangeFontSize + 2;
                        }
                    }
                }
                catch { }
            }
        }

        private Color GetSummaryTextColorValue() => Color.White;
        private Color GetSummaryTotalColorValue() => Color.SteelBlue;

        private void DrawTopSummary(RenderContext context, int xCenter, List<StrikeRow> snapshot)
        {
            // Sumas base
            decimal sumCallsRaw = snapshot.Sum(r => r.Calls);
            decimal sumPutsRaw = snapshot.Sum(r => r.Puts);
            decimal sumCallsAbs = snapshot.Sum(r => Math.Abs(r.Calls));
            decimal sumPutsAbs = snapshot.Sum(r => Math.Abs(r.Puts));

            // Valores por defecto (no GEX)
            decimal callsRowValue = sumCallsRaw;
            decimal putsRowValue = sumPutsRaw;
            decimal rowsDenom = callsRowValue + putsRowValue;
            if (_profileType == ProfileDataType.Gex)
            {
                // En GEX, las Puts suelen ser negativas. Usamos magnitudes para filas Calls/Puts
                callsRowValue = sumCallsAbs;
                putsRowValue = sumPutsAbs;
                rowsDenom = callsRowValue + putsRowValue;
            }
            if (rowsDenom <= 0) rowsDenom = 1;

            // Total: en GEX usamos NET (suma con signo), en otros perfiles, suma normal
            decimal totalValue = _profileType == ProfileDataType.Gex ? (sumCallsRaw + sumPutsRaw) : (sumCallsRaw + sumPutsRaw);
            decimal totalDenom = _profileType == ProfileDataType.Gex ? (callsRowValue + putsRowValue) : totalValue;
            if (totalDenom <= 0) totalDenom = 1;

            var font = new RenderFont("Arial", _topFontSize);
            int rowH = _summaryRowHeightPx;
            int gap = _summaryRowSpacingPx;
            int barW = _summaryBarWidthPx;
            int labelW = _summaryLabelWidthPx;

            // Origen X: bloque centrado en la línea
            int xBar = xCenter - barW /2;
            int xLabel = xBar - labelW -8;
            int xRightText = xBar + barW +8;

            int y = _topMarginPx;

            // Colores
            Color summaryTextColorLocal = _topSummaryTextColor;
            Color summaryTotalColorValue = Color.SteelBlue;

            string callsLabel = _profileType switch
            {
                ProfileDataType.Cash => "Calls $$$",
                ProfileDataType.IV => "IV Call",
                ProfileDataType.Delta => "Delta Call",
                ProfileDataType.Gex => "GEX Call",
                _ => "Calls"
            };
            string putsLabel = _profileType switch
            {
                ProfileDataType.Cash => "Puts $$$",
                ProfileDataType.IV => "IV Put",
                ProfileDataType.Delta => "Delta Put",
                ProfileDataType.Gex => "GEX Put",
                _ => "Puts"
            };
            string totalLabel = _profileType switch
            {
                ProfileDataType.Cash => "TOTAL $$$",
                ProfileDataType.Delta => "TOTAL DELTA",
                ProfileDataType.Gex => "TOTAL GEX",
                _ => "TOTAL"
            };

            // Selección de colores de barras del resumen según perfil
            Color sumCallsCol, sumPutsCol;
            switch (_profileType)
            {
                case ProfileDataType.IV:
                    sumCallsCol = _summaryCallsColorIv; sumPutsCol = _summaryPutsColorIv; break;
                case ProfileDataType.Delta:
                    sumCallsCol = _summaryCallsColorDelta; sumPutsCol = _summaryPutsColorDelta; break;
                case ProfileDataType.Gex:
                    sumCallsCol = _summaryCallsColorGex; sumPutsCol = _summaryPutsColorGex; break;
                case ProfileDataType.Cash:
                case ProfileDataType.Custom:
                default:
                    sumCallsCol = _summaryCallsColor; sumPutsCol = _summaryPutsColor; break;
            }

            // helper local
            void DrawRow(string label, decimal value, Color color, decimal total)
            {
                // etiqueta izquierda
                context.DrawString(label, font, summaryTextColorLocal, xLabel, y + (rowH - _topFontSize) /2);

                // barra de fondo
                var back = _summaryBackBar;
                context.FillRectangle(back, new Rectangle(xBar, y, barW, rowH));

                // barra de valor proporcional
                double ratio = (total >0 ? (double)(value / total) :0.0);
                ratio = Math.Clamp(ratio,0.0,1.0);
                int valW = (int)Math.Round(barW * ratio);
                if (valW >0)
                    context.FillRectangle(color, new Rectangle(xBar, y, valW, rowH));

                // texto derecha: valor compacto y %
                var valTxt = $"{FormatCompact(value)} ({Math.Round(ratio *100)}%)";
                context.DrawString(valTxt, font, summaryTextColorLocal, xRightText, y + (rowH - _topFontSize) /2);

                y += rowH + gap;
            }

            // Filas Calls/Puts
            DrawRow(callsLabel, callsRowValue, sumCallsCol, rowsDenom);
            DrawRow(putsLabel, putsRowValue, sumPutsCol, rowsDenom);

            // TOTAL: color del signo en GEX, o dominante en otros casos
            Color totalColor = _profileType == ProfileDataType.Gex
                ? (totalValue > 0 ? sumCallsCol : (totalValue < 0 ? sumPutsCol : summaryTotalColorValue))
                : (sumCallsRaw > sumPutsRaw ? sumCallsCol : (sumPutsRaw > sumCallsRaw ? sumPutsCol : summaryTotalColorValue));

            DrawRow(totalLabel, totalValue, totalColor, totalDenom);

            // SPY implicado centrado debajo del panel superior
            if (_showSpyCurrent)
            {
                decimal? spyNowVal = null;

                // Si hay autoconversión y tenemos factor activo, usar ES en tiempo real dividido por el factor activo
                if (_enableAutoConversion && _activeConvFactor.HasValue && _activeConvFactor.Value > 0)
                {
                    decimal esNow = 0m;
                    if (_useChartEs && _lastEsPrice > 0)
                        esNow = _lastEsPrice;
                    // fallback a último ES del CSV de quotes si existe
                    if (esNow <= 0 && !string.IsNullOrWhiteSpace(_quotesCsvPath) && File.Exists(_quotesCsvPath))
                    {
                        if (TryGetLatestSpyEsFromQuotes(_quotesCsvPath, out var spyQ, out var esQ) && esQ > 0)
                            esNow = esQ;
                    }

                    if (esNow > 0)
                        spyNowVal = SafeDiv(esNow, _activeConvFactor.Value);
                }

                // Si no hay autoconversión o no tenemos factor, aplicar lógicas previas
                if (spyNowVal == null)
                {
                    decimal esNow = 0m;
                    if (_autoConvUseTimestamp && _lastCsvTimestamp.HasValue && !string.IsNullOrWhiteSpace(_quotesCsvPath) && File.Exists(_quotesCsvPath))
                    {
                        if (!TryGetClosestEsByTimestamp(_quotesCsvPath, _lastCsvTimestamp.Value, _autoConvToleranceSec, out esNow))
                            esNow = _useChartEs && _lastEsPrice > 0 ? _lastEsPrice : 0m;
                    }
                    else
                    {
                        esNow = _useChartEs && _lastEsPrice > 0 ? _lastEsPrice : 0m;
                    }

                    if (esNow <= 0 && !string.IsNullOrWhiteSpace(_quotesCsvPath) && File.Exists(_quotesCsvPath))
                    {
                        if (TryGetLatestSpyEsFromQuotes(_quotesCsvPath, out var spyL, out var esL) && esL > 0)
                            esNow = esL;
                    }

                    if (esNow > 0)
                    {
                        decimal ratio = 0m;
                        if (_enableConversion && _manualEsPrice > 0 && _manualSpyPrice > 0)
                            ratio = SafeDiv(_manualEsPrice, _manualSpyPrice);
                        else if (TryGetLatestSpyEsFromQuotes(_quotesCsvPath, out var spyL, out var esL) && spyL > 0)
                            ratio = SafeDiv(esL, spyL);
                        if (ratio > 0)
                            spyNowVal = SafeDiv(esNow, ratio);
                    }
                }

                if (spyNowVal.HasValue)
                {
                    var text = $"SPY: {spyNowVal.Value.ToString(_spyValueFormat, CultureInfo.InvariantCulture)}";
                    int textW = EstimateTextWidth(text, font);
                    int cx = xCenter - textW / 2;
                    int spyY = y;
                    context.DrawString(text, font, summaryTextColorLocal, cx, spyY + (rowH - _topFontSize) / 2);
                    y += rowH + gap;
                }
            }
            // end SPY summary
        }

        private void ApplyProfilePreset()
        {
            switch (_profileType)
            {
                case ProfileDataType.Cash:
                    _callsColumnKey = "Cash Call";
                    _putsColumnKey = "Cash Put";
                    break;
                case ProfileDataType.IV:
                    _callsColumnKey = "IV Call";
                    _putsColumnKey = "IV Put";
                    break;
                case ProfileDataType.Delta:
                    _callsColumnKey = "Call Delta";
                    _putsColumnKey = "Delta Put"; // nombre habitual en CSV
                    break;
                case ProfileDataType.Gex:
                    _callsColumnKey = _gexCallsColumnKey;
                    _putsColumnKey = _gexPutsColumnKey;
                    break;
                case ProfileDataType.Custom:
                default:
                    // Mantener claves actuales
                    break;
            }
            // Refrescar después de aplicar preset
            ForceReload();
        }

        private void ResetTimer()
        {
            _timer.Stop();
            _timer.Interval = Math.Max(5, _refreshSeconds) *1000;
            _timer.Start();
        }

        private void ResetAutoConvTimer()
        {
            if (_autoConvTimer == null)
                return;

            _autoConvTimer.Stop();
            if (_enableAutoConversion)
            {
                _autoConvTimer.Interval = Math.Max(1, _autoConversionSeconds) * 1000;
                _autoConvTimer.Start();
            }
        }

        private void OnTimer(object? sender, System.Timers.ElapsedEventArgs e)
        {
            LoadCsvSafe();
        }

        private void ForceReload()
        {
            LoadCsvSafe();
            RedrawChart();
        }

        private void LoadCsvSafe()
        {
            try
            {
                // Capturar snapshot previo para marcadores
                List<StrikeRow> prevSnapshot;
                lock (_sync)
                {
                    prevSnapshot = _rows.ToList();
                }
                var prevBySpyLocal = prevSnapshot.ToDictionary(r => r.StrikeSpy, r => (r.Calls, r.Puts));
                var prevByStrikeLocal = prevSnapshot.ToDictionary(r => r.Strike, r => (r.Calls, r.Puts));

                var data = LoadCsv(FilePath, UseLatestTimestamp, out var err,
                    EnableConversion, EnableAutoConversion, QuotesCsvPath, ManualSpyPrice, ManualEsPrice, PriceStep,
                    CallsColumnKey, PutsColumnKey, out var lastPriceFromCsv, out var lastTsFromCsv);

                bool firstAutoInitNeeded;
                lock (_sync)
                {
                    _prevBySpy = prevBySpyLocal;
                    _prevByStrike = prevByStrikeLocal;

                    // Actualizar bases con SPY y valores
                    _baseRows.Clear();
                    _baseRows.AddRange(data.Select(r => new StrikeRow
                    {
                        Strike = r.Strike, // aquí r.Strike es SPY si autoconv está activo
                        StrikeSpy = r.StrikeSpy,
                        Calls = r.Calls,
                        Puts = r.Puts
                    }));

                    _lastCsvPrice = lastPriceFromCsv;
                    _lastCsvTimestamp = lastTsFromCsv;

                    firstAutoInitNeeded = _enableAutoConversion && _rows.Count == 0;

                    if (!_enableAutoConversion)
                    {
                        // Sin autoconversión: actualizar directamente lo visible
                        _rows.Clear();
                        _rows.AddRange(data);
                    }
                }

                _error = err ?? string.Empty;
                _lastLoad = DateTime.Now;

                // Autoconversión
                if (_enableAutoConversion)
                {
                    if (firstAutoInitNeeded)
                    {
                        // Calcular factor inicial
                        if (_autoConvUseTimestamp)
                        {
                            var f = EnsureTimestampFactor(true);
                            if (f.HasValue && f.Value > 0)
                            {
                                _activeConvFactor = f.Value;
                                ApplyConversionWithFactor(f.Value);
                            }
                        }
                        else if (_lastCsvPrice > 0 && _lastEsPrice > 0)
                        {
                            var f = SafeDiv(_lastEsPrice, _lastCsvPrice);
                            if (f > 0)
                            {
                                _activeConvFactor = f;
                                ApplyConversionWithFactor(f);
                            }
                        }
                    }
                    else
                    {
                        // Mantener posiciones: re-aplicar conversión con el factor activo si existe (actualiza cantidades)
                        if (_activeConvFactor.HasValue && _activeConvFactor.Value > 0)
                        {
                            ApplyConversionWithFactor(_activeConvFactor.Value);
                        }
                    }
                }

                RedrawChart();
            }
            catch (Exception ex)
            {
                _error = $"Load error: {ex.Message}";
            }
        }

        private void ApplyConversionWithFactor(decimal factor)
        {
            List<StrikeRow> baseSnapshot;
            decimal step;
            lock (_sync)
            {
                baseSnapshot = _baseRows.ToList();
                step = _priceStep;
            }

            var agg = new Dictionary<decimal, (decimal calls, decimal puts, decimal spyStrike)>();
            foreach (var br in baseSnapshot)
            {
                var outStrike = br.StrikeSpy * factor;
                if (step > 0)
                    outStrike = RoundToStep(outStrike, step);

                if (!agg.TryGetValue(outStrike, out var tuple))
                    agg[outStrike] = (br.Calls, br.Puts, br.StrikeSpy);
                else
                    agg[outStrike] = (tuple.calls + br.Calls, tuple.puts + br.Puts, tuple.spyStrike);
            }

            var converted = new List<StrikeRow>(agg.Count);
            foreach (var kv in agg)
            {
                converted.Add(new StrikeRow
                {
                    Strike = kv.Key,
                    StrikeSpy = kv.Value.spyStrike,
                    Calls = kv.Value.calls,
                    Puts = kv.Value.puts
                });
            }

            lock (_sync)
            {
                _rows.Clear();
                _rows.AddRange(converted);
            }
        }

        private void RecalculateConversionPositions()
        {
            if (!_enableAutoConversion)
                return;

            decimal basePrice = _lastCsvPrice;
            if (basePrice <= 0)
                return;

            decimal factor;

            if (_autoConvUseTimestamp)
            {
                var f = EnsureTimestampFactor(false);
                if (!f.HasValue)
                    return;
                factor = f.Value;
            }
            else
            {
                // Usar precio del gráfico como ES en tiempo real para fijar un nuevo factor en esta ventana
                var chartPrice = _lastEsPrice;
                if (chartPrice <= 0)
                    return;
                factor = SafeDiv(chartPrice, basePrice);
            }

            _activeConvFactor = factor;
            ApplyConversionWithFactor(factor);
            RedrawChart();
        }

        private void OnAutoConvTimer(object? sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                if (!_enableAutoConversion)
                    return;

                if (_autoConvUseTimestamp)
                {
                    // Timestamp mode: solo recalcular posiciones usando factor (que se refresca al expirar tolerancia internamente)
                    RecalculateConversionPositions();
                }
                else
                {
                    // Period mode sin timestamp: al vencer el periodo recargamos CSV y luego fijamos NUEVO factor con precio actual y re-aplicamos
                    LoadCsvSafe(); // mantiene factor anterior hasta que aquí se re-fija
                    RecalculateConversionPositions();
                }
            }
            catch { }
        }

        private static List<StrikeRow> LoadCsv(
            string path,
            bool useLatestTimestamp,
            out string? error,
            bool enableConversion,
            bool autoConversionEnabled,
            string? quotesCsvPath,
            decimal manualSpy,
            decimal manualEs,
            decimal priceStep,
            string callsColumnKey,
            string putsColumnKey,
            out decimal lastPrice,
            out DateTime? lastTimestamp)
        {
            error = null;
            lastPrice = 0m;
            lastTimestamp = null;
            var result = new List<StrikeRow>();

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = $"CSV not found: {path}";
                return result;
            }

            try
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length ==0)
                {
                    error = "CSV empty";
                    return result;
                }

                // Parse header
                var headers = SplitCsvLine(lines[0]);
                int idxStrike = FindIndex(headers, "strike");

                // Elegir columnas calls/puts según claves proporcionadas; fallback a genérico
                int idxCalls = -1;
                if (!string.IsNullOrWhiteSpace(callsColumnKey))
                    idxCalls = FindIndex(headers, callsColumnKey);
                if (idxCalls <0)
                    idxCalls = FindIndex(headers, "call");

                int idxPuts = -1;
                if (!string.IsNullOrWhiteSpace(putsColumnKey))
                    idxPuts = FindIndex(headers, putsColumnKey);
                if (idxPuts <0)
                    idxPuts = FindIndex(headers, "put");

                int idxTs = FindIndex(headers, "time"); // matches Timestamp
                int idxSpy = FindIndex(headers, "spy");
                int idxEs = FindIndex(headers, "es");
                int idxPrice = FindIndex(headers, "price");

                if (idxStrike <0 || idxCalls <0 || idxPuts <0)
                {
                    error = "CSV headers not recognized. Configure 'Calls column key' y 'Puts column key'.";

                    return result;
                }

                // If only latest timestamp requested, find max
                DateTime? maxTs = null;
                string? maxTsRaw = null;
                if (useLatestTimestamp && idxTs >=0)
                {
                    for (int i =1; i < lines.Length; i++)
                    {
                        if (string.IsNullOrWhiteSpace(lines[i])) continue;
                        var cols = SplitCsvLine(lines[i]);
                        if (cols.Length <= idxTs) continue;
                        var tsRaw = cols[idxTs]?.Trim('"', ' ');
                        if (string.IsNullOrEmpty(tsRaw)) continue;

                        if (DateTime.TryParse(tsRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var ts))
                        {
                            if (maxTs == null || ts > maxTs)
                            {
                                maxTs = ts;
                                maxTsRaw = tsRaw;
                            }
                        }
                        else
                        {
                            // fallback lexicographic
                            if (maxTsRaw == null || string.CompareOrdinal(tsRaw, maxTsRaw) >0)
                                maxTsRaw = tsRaw;
                        }
                    }
                }

                // Load external quotes if needed
                Dictionary<string, decimal>? factorByTs = null;
                string? quotesErr = null;
                if (enableConversion && !autoConversionEnabled && (idxSpy <0 || idxEs <0) && !string.IsNullOrWhiteSpace(quotesCsvPath) && File.Exists(quotesCsvPath))
                {
                    factorByTs = LoadQuotesFactors(quotesCsvPath!, out quotesErr);
                    if (quotesErr != null && error == null) error = $"Quotes CSV: {quotesErr}";
                }

                decimal manualFactor = (enableConversion && !autoConversionEnabled && manualSpy >0 && manualEs >0) ? SafeDiv(manualEs, manualSpy) :1m;

                var agg = new Dictionary<decimal, (decimal calls, decimal puts, decimal spyStrike)>();
                for (int i =1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var cols = SplitCsvLine(line);
                    if (cols.Length <= Math.Max(idxStrike, Math.Max(idxCalls, Math.Max(idxPuts, idxTs))))
                        continue;

                    string? tsRaw = idxTs >=0 && idxTs < cols.Length ? cols[idxTs]?.Trim('"', ' ') : null;

                    if (useLatestTimestamp && idxTs >=0)
                    {
                        if (!string.Equals(tsRaw, maxTsRaw, StringComparison.Ordinal))
                            continue;
                    }

                    if (!TryParseDecimal(cols[idxStrike], out var strikeSpy)) continue; // original SPY
                    if (!TryParseDecimal(cols[idxCalls], out var calls)) calls =0;
                    if (!TryParseDecimal(cols[idxPuts], out var puts)) puts =0;

                    // Capturar último Price del CSV (si existe). Se usará para autoconversión
                    if (idxPrice >= 0 && idxPrice < cols.Length && TryParseDecimal(cols[idxPrice], out var pr) && pr > 0)
                    {
                        lastPrice = pr;
                        if (idxTs >= 0 && tsRaw != null && DateTime.TryParse(tsRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var tsdt))
                            lastTimestamp = tsdt;
                    }

                    // determine conversion factor (solo si NO es autoconversión)
                    decimal factor =1m;
                    if (!autoConversionEnabled && enableConversion)
                    {
                        if (idxSpy >=0 && idxEs >=0 && idxSpy < cols.Length && idxEs < cols.Length &&
                            TryParseDecimal(cols[idxSpy], out var spyVal) && TryParseDecimal(cols[idxEs], out var esVal) && spyVal >0)
                        {
                            factor = SafeDiv(esVal, spyVal);
                        }
                        else if (factorByTs != null && tsRaw != null && factorByTs.TryGetValue(tsRaw, out var fByTs))
                        {
                            factor = fByTs;
                        }
                        else if (manualFactor >0)
                        {
                            factor = manualFactor;
                        }
                    }

                    var outStrike = autoConversionEnabled ? strikeSpy : (enableConversion ? (strikeSpy * factor) : strikeSpy);
                    if (!autoConversionEnabled && priceStep >0)
                        outStrike = RoundToStep(outStrike, priceStep);

                    if (!agg.TryGetValue(outStrike, out var tuple))
                        agg[outStrike] = (calls, puts, strikeSpy);
                    else
                        agg[outStrike] = (tuple.calls + calls, tuple.puts + puts, tuple.spyStrike); // conservar SPY original
                }

                // Si usamos latest timestamp y lo encontramos, establecerlo si no se estableció antes
                if (useLatestTimestamp && maxTs.HasValue && lastTimestamp == null)
                    lastTimestamp = maxTs;

                foreach (var kv in agg)
                {
                    result.Add(new StrikeRow
                    {
                        Strike = kv.Key,
                        StrikeSpy = kv.Value.spyStrike,
                        Calls = kv.Value.calls,
                        Puts = kv.Value.puts
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return result;
            }
        }

        private static Dictionary<string, decimal> LoadQuotesFactors(string path, out string? error)
        {
            error = null;
            var dict = new Dictionary<string, decimal>(StringComparer.Ordinal);
            try
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length ==0)
                {
                    error = "Quotes CSV empty";
                    return dict;
                }

                var headers = SplitCsvLine(lines[0]);
                int idxTs = FindIndex(headers, "time");
                int idxSpy = FindIndex(headers, "spy");
                int idxEs = FindIndex(headers, "es");
                if (idxTs <0 || idxSpy <0 || idxEs <0)
                {
                    error = "Quotes CSV headers not recognized. Expect Timestamp, SPY, ES";
                    return dict;
                }

                for (int i =1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var cols = SplitCsvLine(line);
                    if (cols.Length <= Math.Max(idxTs, Math.Max(idxSpy, idxEs))) continue;
                    var tsRaw = cols[idxTs]?.Trim('"', ' ');
                    if (!TryParseDecimal(cols[idxSpy], out var spy) || !TryParseDecimal(cols[idxEs], out var es) || spy <=0)
                        continue;
                    dict[tsRaw ?? string.Empty] = SafeDiv(es, spy);
                }

                return dict;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return dict;
            }
        }

        private static decimal SafeDiv(decimal a, decimal b)
        {
            if (b ==0) return 0;
            return a / b;
        }

        private static decimal RoundToStep(decimal price, decimal step)
        {
            if (step <=0) return price;
            var q = price / step;
            var rounded = Math.Round(q,0, MidpointRounding.AwayFromZero);
            return rounded * step;
        }

        private static int FindIndex(string[] headers, string key)
        {
            key = key ?? string.Empty;
            var knorm = new string(key.Where(ch => char.IsLetterOrDigit(ch)).ToArray()).ToLowerInvariant();
            for (int i =0; i < headers.Length; i++)
            {
                var h = headers[i] ?? string.Empty;
                var norm = new string(h.Where(ch => char.IsLetterOrDigit(ch)).ToArray()).ToLowerInvariant();
                if (norm.Contains(knorm))
                    return i;
            }
            return -1;
        }

        private static string[] SplitCsvLine(string line)
        {
            // Simple CSV splitter supporting quoted fields
            var list = new List<string>();
            bool inQuotes = false;
            var cur = new System.Text.StringBuilder();
            for (int i =0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i +1 < line.Length && line[i +1] == '"')
                    {
                        cur.Append('"');
                        i++; // skip escaped quote
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    list.Add(cur.ToString());
                    cur.Clear();
                }
                else
                {
                    cur.Append(c);
                }
            }
            list.Add(cur.ToString());
            return list.ToArray();
        }

        private static bool TryParseDecimal(string? s, out decimal value)
        {
            value =0m;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().Trim('"').Replace("$", string.Empty).Replace(" ", string.Empty);
            // Remove thousand separators if present
            if (s.Contains(',') && s.Contains('.'))
            {
                // Try to guess culture: assume comma as thousand sep and dot as decimal
                s = s.Replace(",", string.Empty);
            }
            else if (s.Count(ch => ch == ',') ==1 && !s.Contains('.'))
            {
                // maybe decimal comma
                s = s.Replace(',', '.');
            }

            return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        private bool TryGetLatestSpyEsFromQuotes(string path, out decimal spy, out decimal es)
        {
            spy =0m; es =0m;
            try
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length <=1) return false;
                var headers = SplitCsvLine(lines[0]);
                int idxSpy = FindIndex(headers, "spy");
                int idxEs = FindIndex(headers, "es");
                if (idxSpy <0 || idxEs <0) return false;
                for (int i = lines.Length -1; i >=1; i--)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var cols = SplitCsvLine(line);
                    if (cols.Length <= Math.Max(idxSpy, idxEs)) continue;
                    if (TryParseDecimal(cols[idxSpy], out var s) && TryParseDecimal(cols[idxEs], out var e) && s >0)
                    { spy = s; es = e; return true; }
                }
                return false;
            }
            catch { return false; }
        }

        private static bool TryParseDateTime(string? s, out DateTime value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            return DateTime.TryParse(s.Trim('"', ' '), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out value);
        }

        private static bool TryGetClosestEsByTimestamp(string path, DateTime target, int toleranceSec, out decimal es)
        {
            es = 0m;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return false;

                var lines = File.ReadAllLines(path);
                if (lines.Length <= 1) return false;

                var headers = SplitCsvLine(lines[0]);
                int idxTs = FindIndex(headers, "time");
                int idxEs = FindIndex(headers, "es");
                if (idxTs < 0 || idxEs < 0) return false;

                double bestDiff = double.MaxValue;
                decimal bestEs = 0m;
                for (int i = 1; i < lines.Length; i++)
                {
                    var cols = SplitCsvLine(lines[i]);
                    if (cols.Length <= Math.Max(idxTs, idxEs)) continue;

                    if (!TryParseDateTime(cols[idxTs], out var ts)) continue;
                    if (!TryParseDecimal(cols[idxEs], out var esVal) || esVal <= 0) continue;

                    var diff = Math.Abs((ts - target).TotalSeconds);
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        bestEs = esVal;
                    }
                }

                if (bestDiff <= toleranceSec)
                {
                    es = bestEs;
                    return es > 0;
                }
                return false;
            }
            catch { return false; }
        }

        // Obtiene/actualiza el factor sticky de autoconversión por timestamp.
        // Si force=true o si expiró la ventana de tolerancia, recalcula el factor usando QuotesCsv y _lastCsvTimestamp.
        private decimal? EnsureTimestampFactor(bool force)
        {
            if (!_autoConvUseTimestamp)
                return null;

            var nowUtc = DateTime.UtcNow;
            lock (_sync)
            {
                bool expired = !_tsStickyAtUtc.HasValue || (nowUtc - _tsStickyAtUtc.Value).TotalSeconds >= _autoConvToleranceSec;
                if (!force && _tsStickyFactor.HasValue && !expired)
                {
                    return _tsStickyFactor;
                }
            }

            // Recalcular fuera del lock para evitar bloqueo en IO
            if (!_lastCsvTimestamp.HasValue || _lastCsvPrice <= 0 || string.IsNullOrWhiteSpace(_quotesCsvPath) || !File.Exists(_quotesCsvPath))
            {
                lock (_sync)
                {
                    return _tsStickyFactor; // devolver el último si existe
                }
            }

            if (TryGetClosestEsByTimestamp(_quotesCsvPath, _lastCsvTimestamp.Value, _autoConvToleranceSec, out var esVal) && esVal > 0)
            {
                var newFactor = SafeDiv(esVal, _lastCsvPrice);
                if (newFactor > 0)
                {
                    lock (_sync)
                    {
                        _tsStickyFactor = newFactor;
                        _tsStickyAtUtc = DateTime.UtcNow;
                        return _tsStickyFactor;
                    }
                }
            }

            // Si no se pudo calcular uno nuevo, devolver el último válido
            lock (_sync)
            {
                return _tsStickyFactor;
            }
        }

        private static string FormatCompact(decimal value)
        {
            var abs = Math.Abs(value);
            string suffix;
            decimal num;
            if (abs >=1_000_000_000m)
            {
                suffix = "B";
                num = value /1_000_000_000m;
            }
            else if (abs >=1_000_000m)
            {
                suffix = "M";
                num = value /1_000_000m;
            }
            else if (abs >=1_000m)
            {
                suffix = "K";
                num = value /1_000m;
            }
            else
            {
                return value.ToString("0,0", CultureInfo.CurrentCulture);
            }

            return num.ToString("0.##", CultureInfo.CurrentCulture) + suffix;
        }

        private static int EstimateTextWidth(string text, RenderFont font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            double factor =0.58; // heurística
            return (int)Math.Ceiling(text.Length * (font.Size * factor));
        }

        protected override void OnDispose()
        {
            try
            {
                _timer.Stop();
                _timer.Dispose();
                if (_hotkeyTimer != null)
                {
                    _hotkeyTimer.Stop();
                    _hotkeyTimer.Elapsed -= OnHotkeyTimer;
                    _hotkeyTimer.Dispose();
                    _hotkeyTimer = null;
                }
                if (_autoConvTimer != null)
                {
                    _autoConvTimer.Stop();
                    _autoConvTimer.Elapsed -= OnAutoConvTimer;
                    _autoConvTimer.Dispose();
                    _autoConvTimer = null;
                }
            }
            catch { }
            base.OnDispose();
        }

        private void OnHotkeyTimer(object? sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                if (CheckAndConsumeHotkey())
                {
                    CycleProfileType();
                }

                // Manual: Ctrl+U -> recalcula y aplica conversión inmediata
                if (CheckAndConsumeManualConvHotkey())
                {
                    RecalculateConversionPositions();
                }
            }
            catch
            {
                // ignorar errores de hotkey para no romper el hilo del timer
            }
        }

        //10. Hotkeys: configuración para alternar perfil
        private bool _enableProfileHotkey = true;
        [Display(GroupName = "10. Hotkeys", Name = "Enable profile hotkey", Order =10)]
        public bool EnableProfileHotkey
        {
            get => _enableProfileHotkey;
            set { _enableProfileHotkey = value; }
        }

        // Tecla base para el ciclo (A-Z,0-9). Se usa junto con Ctrl
        private string _profileHotkeyKey = "P";
        [Display(GroupName = "10. Hotkeys", Name = "Profile key (A-Z/0-9)", Order =20)]
        public string ProfileCycleKey
        {
            get => _profileHotkeyKey;
            set { _profileHotkeyKey = string.IsNullOrWhiteSpace(value) ? "P" : value.Trim(); }
        }

        // Requerir la tecla Ctrl como modificador
        private bool _requireCtrlModifier = true;
        [Display(GroupName = "10. Hotkeys", Name = "Require Ctrl modifier", Order =30)]
        public bool RequireCtrlModifier
        {
            get => _requireCtrlModifier;
            set { _requireCtrlModifier = value; }
        }

        // NUEVO: Hotkey manual para actualizar conversión: Ctrl + U
        private bool _enableManualConvHotkey = true;
        [Display(GroupName = "10. Hotkeys", Name = "Enable manual conversion hotkey (Ctrl+U)", Order =40)]
        public bool EnableManualConversionHotkey
        {
            get => _enableManualConvHotkey;
            set { _enableManualConvHotkey = value; }
        }

        // Estado anti-rebote
        private bool _hotkeyPrevDown;
        private DateTime _hotkeyLast = DateTime.MinValue;
        private const int HotkeyDebounceMs =250;

        // Anti-rebote para Ctrl+U
        private bool _manualHotkeyPrevDown;
        private DateTime _manualHotkeyLast = DateTime.MinValue;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static int ResolveVkFromChar(char key)
        {
            key = char.ToUpperInvariant(key);
            if (key >= 'A' && key <= 'Z') return key; // VK A..Z
            if (key >= '0' && key <= '9') return key; // VK0..9
            return 0; // unsupported
        }

        private bool CheckAndConsumeHotkey()
        {
            if (!_enableProfileHotkey)
                return false;

            char keyChar = 'P';
            if (!string.IsNullOrWhiteSpace(_profileHotkeyKey))
                keyChar = _profileHotkeyKey.Trim()[0];

            int vKey = ResolveVkFromChar(keyChar);
            if (vKey ==0)
                return false; // tecla no soportada

            const int VK_CONTROL =0x11;
            bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) &0x8000) !=0;
            if (_requireCtrlModifier && !ctrlDown)
            {
                // si se requiere Ctrl y no está presionado, no hay hotkey
                _hotkeyPrevDown = false;
                return false;
            }

            bool keyDown = (GetAsyncKeyState(vKey) &0x8000) !=0;
            bool comboDown = (_requireCtrlModifier ? ctrlDown : true) && keyDown;

            var now = DateTime.UtcNow;
            bool fired = false;
            if (comboDown && !_hotkeyPrevDown)
            {
                if ((now - _hotkeyLast).TotalMilliseconds >= HotkeyDebounceMs)
                {
                    fired = true;
                    _hotkeyLast = now;
                }
            }
            _hotkeyPrevDown = comboDown;
            return fired;
        }

        // NUEVO: Ctrl+U para refrescar conversión manualmente
        private bool CheckAndConsumeManualConvHotkey()
        {
            if (!_enableManualConvHotkey)
                return false;

            const int VK_CONTROL = 0x11;
            bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            if (!ctrlDown)
            {
                _manualHotkeyPrevDown = false;
                return false;
            }

            int vKey = ResolveVkFromChar('U');
            if (vKey == 0)
                return false;

            bool keyDown = (GetAsyncKeyState(vKey) & 0x8000) != 0;
            bool comboDown = ctrlDown && keyDown;

            var now = DateTime.UtcNow;
            bool fired = false;
            if (comboDown && !_manualHotkeyPrevDown)
            {
                if ((now - _manualHotkeyLast).TotalMilliseconds >= HotkeyDebounceMs)
                {
                    fired = true;
                    _manualHotkeyLast = now;
                }
            }

            _manualHotkeyPrevDown = comboDown;
            return fired;
        }

        private void CycleProfileType()
        {
            var next = _profileType;
            switch (_profileType)
            {
                case ProfileDataType.Cash: next = ProfileDataType.IV; break;
                case ProfileDataType.IV: next = ProfileDataType.Delta; break;
                case ProfileDataType.Delta: next = ProfileDataType.Gex; break;
                case ProfileDataType.Gex: next = ProfileDataType.Cash; break;
                case ProfileDataType.Custom: next = ProfileDataType.Cash; break;
                default: next = ProfileDataType.Cash; break;
            }
            _profileType = next;
            ApplyProfilePreset();
        }
    }
}
