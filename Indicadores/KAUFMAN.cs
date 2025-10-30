namespace ATAS.Indicators.Technical;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using ATAS.Indicators;
using CrossColor = System.Windows.Media.Color;

[DisplayName("DEI Delta Impact (KER)")]
public class DEI_DeltaImpact_ATAS : Indicator
{
 // Columnas disponibles
 public enum SeriesColumn
 {
 Timestamp,
 Price,
 CallVol,
 PutVol,
 NetDelta,
 NetGex,
 TotalIO,
 NetIO,
 CallOI,
 PutOI,
 NetVol,
 CashCall,
 CashPut,
 IvCall,
 IvPut,
 CallDelta,
 DeltaPut,
 CashNeto,
 IvNeta,
 DeltaNeto,
 CallAsk,
 CallBid,
 PutAsk,
 PutBid,
 TotalCall,
 TotalPut,
 SNeto
 }

 private readonly ValueDataSeries _kerASeries = new("KER A") { Color = CrossColor.FromArgb(255,255,165,0), Width =2 };
 private readonly ValueDataSeries _kerBSeries = new("KER B") { Color = CrossColor.FromArgb(255,128,0,128), Width =2 };
 // Líneas horizontales
 private readonly ValueDataSeries _level1Series = new("Level1") { Color = CrossColor.FromArgb(255,128,128,128), Width =1 };
 private readonly ValueDataSeries _level2Series = new("Level2") { Color = CrossColor.FromArgb(255,100,149,237), Width =1 };

 // Backing fields (para refresco inmediato)
 private string _filePath = @"C:\\data\\SPY_Cash.csv";
 private double _gmtOffsetHours =0d;
 private SeriesColumn _seriesA = SeriesColumn.CallVol;
 private SeriesColumn _seriesB = SeriesColumn.PutVol;
 private bool _showSeriesA = true;
 private bool _showSeriesB = true;
 private int _period =14;
 private int _maxHistoricalBars =500;
 private decimal _level1 =30m;
 private decimal _level2 =50m;

 [Display(Name = "File Path", GroupName = "Input", Order =0)]
 public string FilePath
 {
 get => _filePath;
 set
 {
 var v = value ?? string.Empty;
 if (_filePath == v) return;
 _filePath = v;
 ForceReload();
 }
 }

 [Range(-24,24)]
 [Display(Name = "GMT Offset (hours)", GroupName = "Input", Order =2)]
 public double GmtOffsetHours
 {
 get => _gmtOffsetHours;
 set
 {
 var v = Math.Max(-24d, Math.Min(24d, value));
 if (Math.Abs(_gmtOffsetHours - v) <1e-9) return;
 _gmtOffsetHours = v;
 ForceReload();
 }
 }

 [Display(Name = "Series A", GroupName = "Selection", Order =10)]
 public SeriesColumn SeriesA
 {
 get => _seriesA;
 set
 {
 if (_seriesA == value) return;
 _seriesA = value;
 RecalculateValues();
 RedrawChart();
 }
 }

 [Display(Name = "Series B", GroupName = "Selection", Order =11)]
 public SeriesColumn SeriesB
 {
 get => _seriesB;
 set
 {
 if (_seriesB == value) return;
 _seriesB = value;
 RecalculateValues();
 RedrawChart();
 }
 }

 [Display(Name = "Show Series A", GroupName = "Selection", Order =12)]
 public bool ShowSeriesA
 {
 get => _showSeriesA;
 set
 {
 if (_showSeriesA == value) return;
 _showSeriesA = value;
 RecalculateValues();
 RedrawChart();
 }
 }

 [Display(Name = "Show Series B", GroupName = "Selection", Order =13)]
 public bool ShowSeriesB
 {
 get => _showSeriesB;
 set
 {
 if (_showSeriesB == value) return;
 _showSeriesB = value;
 RecalculateValues();
 RedrawChart();
 }
 }

 [Range(2,10000)]
 [Display(Name = "KER Period", GroupName = "Calculation", Order =20)]
 public int Period
 {
 get => _period;
 set
 {
 var v = Math.Clamp(value,2,10000);
 if (_period == v) return;
 _period = v;
 ForceReload();
 }
 }

 [Range(50,1000000)]
 [Display(Name = "Max Historical Bars", GroupName = "Calculation", Order =21)]
 public int MaxHistoricalBars
 {
 get => _maxHistoricalBars;
 set
 {
 var v = Math.Clamp(value,50,1_000_000);
 if (_maxHistoricalBars == v) return;
 _maxHistoricalBars = v;
 ForceReload();
 }
 }

 [Display(Name = "Level1", GroupName = "Levels", Order =30)]
 public decimal Level1
 {
 get => _level1;
 set
 {
 if (_level1 == value) return;
 _level1 = value;
 RecalculateValues();
 RedrawChart();
 }
 }

 [Display(Name = "Level2", GroupName = "Levels", Order =31)]
 public decimal Level2
 {
 get => _level2;
 set
 {
 if (_level2 == value) return;
 _level2 = value;
 RecalculateValues();
 RedrawChart();
 }
 }

 private readonly TimeSpan _refresh = TimeSpan.FromMinutes(1);
 private DateTime _lastReadUtc = DateTime.MinValue;
 private DateTime _lastWriteUtc = DateTime.MinValue;
 private double _lastOffset = double.NaN;

 // Time series agregadas por timestamp
 private readonly List<DateTime> _times = new();
 private readonly Dictionary<SeriesColumn, List<decimal>> _series;

 // KER pre-calculados por columna
 private readonly Dictionary<SeriesColumn, List<decimal>> _ker;

 public DEI_DeltaImpact_ATAS()
 : base(true)
 {
 DenyToChangePanel = false;

 DataSeries[0] = _kerASeries;
 DataSeries.Add(_kerBSeries);
 DataSeries.Add(_level1Series);
 DataSeries.Add(_level2Series);

 // Inicializar diccionarios para todas las columnas
 _series = new Dictionary<SeriesColumn, List<decimal>>();
 _ker = new Dictionary<SeriesColumn, List<decimal>>();
 foreach (SeriesColumn c in Enum.GetValues(typeof(SeriesColumn)))
 {
 _series[c] = new List<decimal>();
 _ker[c] = new List<decimal>();
 }
 }

 protected override void OnInitialize()
 {
 base.OnInitialize();
 // Carga inicial
 ForceReload();
 }

 protected override void OnCalculate(int bar, decimal value)
 {
 TryRefreshData();

 var candle = GetCandle(bar);
 var time = candle.Time;

 if (_times.Count ==0 || time == default)
 {
 _kerASeries[bar] =0m;
 _kerBSeries[bar] =0m;
 _level1Series[bar] = Level1;
 _level2Series[bar] = Level2;
 return;
 }

 var idx = FindClosestPreviousIndex(time);
 if (idx <0)
 {
 _kerASeries[bar] =0m;
 _kerBSeries[bar] =0m;
 _level1Series[bar] = Level1;
 _level2Series[bar] = Level2;
 return;
 }

 var aVal = GetKerAt(SeriesA, idx);
 var bVal = GetKerAt(SeriesB, idx);

 _kerASeries[bar] = ShowSeriesA ? aVal :0m;
 _kerBSeries[bar] = ShowSeriesB ? bVal :0m;

 // Líneas horizontales
 _level1Series[bar] = Level1;
 _level2Series[bar] = Level2;
 }

 private void ForceReload()
 {
 try
 {
 if (!string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath))
 {
 LoadCsvAggregateAndRecompute();
 _lastWriteUtc = File.GetLastWriteTimeUtc(FilePath);
 }
 else
 {
 _times.Clear();
 foreach (var k in _series.Keys.ToList())
 {
 _series[k].Clear();
 _ker[k].Clear();
 }
 _lastWriteUtc = DateTime.MinValue;
 }
 _lastReadUtc = DateTime.UtcNow;
 _lastOffset = GmtOffsetHours;
 }
 catch { }

 // Forzar recálculo y repintado de todas las barras
 RecalculateValues();
 RedrawChart();
 }

 private decimal GetKerAt(SeriesColumn col, int idx)
 {
 var list = _ker[col];
 if (idx <0 || idx >= list.Count)
 return 0m;
 return list[idx];
 }

 private void TryRefreshData()
 {
 try
 {
 if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
 return;

 var mod = File.GetLastWriteTimeUtc(FilePath);
 if (DateTime.UtcNow - _lastReadUtc >= _refresh || mod > _lastWriteUtc || _lastOffset != GmtOffsetHours)
 {
 _lastWriteUtc = mod;
 _lastOffset = GmtOffsetHours;
 LoadCsvAggregateAndRecompute();
 _lastReadUtc = DateTime.UtcNow;
 }
 }
 catch
 {
 // Ignorar IO intermitente
 }
 }

 private void LoadCsvAggregateAndRecompute()
 {
 _times.Clear();
 foreach (var k in _series.Keys.ToList())
 {
 _series[k].Clear();
 _ker[k].Clear();
 }

 var lines = File.ReadAllLines(FilePath)
 .Where(l => !string.IsNullOrWhiteSpace(l))
 .ToList();
 if (lines.Count ==0)
 return;

 var headerParts = SplitLine(lines[0]);
 var hasHeader = headerParts.Any(h => h.Contains("timestamp", StringComparison.OrdinalIgnoreCase)
 || h.Contains("call", StringComparison.OrdinalIgnoreCase)
 || h.Contains("put", StringComparison.OrdinalIgnoreCase)
 || h.Contains("iv", StringComparison.OrdinalIgnoreCase)
 || h.Contains("dcall", StringComparison.OrdinalIgnoreCase)
 || h.Contains("dput", StringComparison.OrdinalIgnoreCase));

 int start = hasHeader ?1 :0;

 // Mapeo de columnas por nombre
 var map = MapColumns(headerParts);

 // Acumulación por timestamp
 var agg = new Dictionary<DateTime, decimal[]>();
 var ci = CultureInfo.InvariantCulture;
 int colsCount = Enum.GetValues(typeof(SeriesColumn)).Length;

 for (int i = start; i < lines.Count; i++)
 {
 var parts = SplitLine(lines[i]);
 if (parts.Length ==0)
 continue;

 if (!TryParseTimestamp(parts, map.TryGetValue(SeriesColumn.Timestamp, out var idxTs) ? idxTs : -1, out var ts))
 {
 // try common timestamp locations
 if (!TryParseTimestamp(parts, map.TryGetValue(SeriesColumn.SNeto, out idxTs) ? idxTs : -1, out ts))
 continue;
 }

 // Aplicar ajuste GMT en horas
 ts = ts.AddHours(GmtOffsetHours);

 var sums = agg.TryGetValue(ts, out var arr) ? arr : new decimal[colsCount];

 // Añadir cada columna según mapa
 foreach (SeriesColumn sc in Enum.GetValues(typeof(SeriesColumn)))
 {
 if (map.TryGetValue(sc, out var cidx))
 TryAdd(parts, cidx, ref sums[(int)sc], ci);
 }

 agg[ts] = sums;
 }

 // Ordena por timestamp y vuelca a listas
 foreach (var kv in agg.OrderBy(k => k.Key))
 {
 _times.Add(kv.Key);
 var v = kv.Value;
 foreach (SeriesColumn sc in Enum.GetValues(typeof(SeriesColumn)))
 _series[sc].Add(v[(int)sc]);
 }

 // Limitar historial
 if (_times.Count > MaxHistoricalBars)
 {
 var skip = _times.Count - MaxHistoricalBars;
 _times.RemoveRange(0, skip);
 foreach (var key in _series.Keys.ToList())
 _series[key].RemoveRange(0, skip);
 }

 // Recalcular KER para cada columna
 foreach (var key in _series.Keys.ToList())
 {
 var src = _series[key];
 var dst = _ker[key];
 for (int i =0; i < src.Count; i++)
 dst.Add(CalcKER(src, i, Period));
 }
 }

 private static string[] SplitLine(string line)
 => line.Split(new[] { ',', ';', '\t' }, StringSplitOptions.None)
 .Select(s => s.Trim()).ToArray();

 private static void TryAdd(string[] parts, int colIdx, ref decimal acc, CultureInfo ci)
 {
 if (colIdx <0 || colIdx >= parts.Length)
 return;
 if (decimal.TryParse(parts[colIdx], NumberStyles.Any, ci, out var v))
 acc += v;
 }

 private static bool TryParseTimestamp(string[] parts, int tsIdx, out DateTime ts)
 {
 ts = default;
 if (tsIdx <0 || tsIdx >= parts.Length)
 return false;

 // Ejemplo:10/29/202510:18
 var ok = DateTime.TryParse(parts[tsIdx], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out ts);
 return ok;
 }

 private int FindClosestPreviousIndex(DateTime time)
 {
 if (_times.Count ==0)
 return -1;

 int l =0, r = _times.Count -1, res = -1;
 while (l <= r)
 {
 int m = (l + r) >>1;
 if (_times[m] <= time)
 {
 res = m;
 l = m +1;
 }
 else r = m -1;
 }
 return res;
 }

 private static decimal CalcKER(List<decimal> list, int idx, int period)
 {
 if (idx < period || idx >= list.Count)
 return 0m;

 var net = Math.Abs(list[idx] - list[idx - period]);
 decimal vol =0m;
 for (int i =1; i <= period; i++)
 vol += Math.Abs(list[idx - i +1] - list[idx - i]);

 if (vol <=0m)
 return 0m;
 return 100m * net / vol;
 }

 // ---- Mapeo de columnas ----

 private static Dictionary<SeriesColumn,int> MapColumns(string[] header)
 {
 var map = new Dictionary<SeriesColumn,int>();
 if (header == null || header.Length ==0) return map;

 for (int i =0; i < header.Length; i++)
 {
 var norm = Normalize(header[i]);
 if (string.IsNullOrWhiteSpace(norm)) continue;

 // Timestamp
 if (norm == "timestamp" || norm == "time" || norm == "datetime" || norm.Contains("timestamp") || norm.Contains("timestampqqq") || norm.Contains("timestamp(")) { map[SeriesColumn.Timestamp] = i; continue; }

 if (norm.Contains("price") || norm == "precio") map[SeriesColumn.Price] = i;
 if (norm.Contains("callvol") || norm.Contains("call_vol") || norm.Contains("call vol")) map[SeriesColumn.CallVol] = i;
 if (norm.Contains("putvol") || norm.Contains("put_vol") || norm.Contains("put vol")) map[SeriesColumn.PutVol] = i;
 if (norm.Contains("netdelta") || norm.Contains("net_delta") || norm.Contains("net delta")) map[SeriesColumn.NetDelta] = i;
 if (norm.Contains("netgex") || norm.Contains("gex")) map[SeriesColumn.NetGex] = i;
 if (norm.Contains("totalio") || norm.Contains("total_io") || norm.Contains("total io")) map[SeriesColumn.TotalIO] = i;
 if (norm.Contains("netio") || norm.Contains("net_io") || norm.Contains("net io")) map[SeriesColumn.NetIO] = i;
 if (norm.Contains("calloi") || norm.Contains("call_oi") || norm.Contains("call oi")) map[SeriesColumn.CallOI] = i;
 if (norm.Contains("putoi") || norm.Contains("put_oi") || norm.Contains("put oi")) map[SeriesColumn.PutOI] = i;
 if (norm.Contains("netvol") || norm.Contains("net_vol") || norm.Contains("net vol")) map[SeriesColumn.NetVol] = i;
 if (norm.Contains("cashcall") || norm.Contains("cash_call") || norm.Contains("cash call")) map[SeriesColumn.CashCall] = i;
 if (norm.Contains("cashput") || norm.Contains("cash_put") || norm.Contains("cash put")) map[SeriesColumn.CashPut] = i;
 if (norm.Contains("ivcall") || norm.Contains("iv_call") || norm.Contains("iv call")) map[SeriesColumn.IvCall] = i;
 if (norm.Contains("ivput") || norm.Contains("iv_put") || norm.Contains("iv put")) map[SeriesColumn.IvPut] = i;
 if (norm.Contains("calldelta") || norm.Contains("call_delta") || norm.Contains("call delta")) map[SeriesColumn.CallDelta] = i;
 if (norm.Contains("deltaput") || norm.Contains("delta_put") || norm.Contains("delta put")) map[SeriesColumn.DeltaPut] = i;
 if (norm.Contains("cashneto") || norm.Contains("cash_neto") || norm.Contains("cash neto")) map[SeriesColumn.CashNeto] = i;
 if (norm.Contains("ivneta") || norm.Contains("iv_neta") || norm.Contains("iv neta")) map[SeriesColumn.IvNeta] = i;
 if (norm.Contains("deltaneto") || norm.Contains("delta_neto") || norm.Contains("delta neto")) map[SeriesColumn.DeltaNeto] = i;
 if (norm.Contains("callask") || norm.Contains("call_ask") || norm.Contains("call ask")) map[SeriesColumn.CallAsk] = i;
 if (norm.Contains("callbid") || norm.Contains("call_bid") || norm.Contains("call bid")) map[SeriesColumn.CallBid] = i;
 if (norm.Contains("putask") || norm.Contains("put_ask") || norm.Contains("put ask")) map[SeriesColumn.PutAsk] = i;
 if (norm.Contains("putbid") || norm.Contains("put_bid") || norm.Contains("put bid")) map[SeriesColumn.PutBid] = i;
 if (norm.Contains("totalcall") || norm.Contains("total_call") || norm.Contains("total call")) map[SeriesColumn.TotalCall] = i;
 if (norm.Contains("totalput") || norm.Contains("total_put") || norm.Contains("total put")) map[SeriesColumn.TotalPut] = i;
 if (norm.Contains("sn") || norm.Contains("sneto") || norm.Contains("s_neto")) map[SeriesColumn.SNeto] = i;
 }

 return map;
 }

 private static string Normalize(string s)
 {
 if (string.IsNullOrWhiteSpace(s))
 return string.Empty;

 var lower = s.ToLowerInvariant();
 // Quitar espacios y símbolos $
 var filtered = new string(lower.Where(ch => !char.IsWhiteSpace(ch) && ch != '$').ToArray());
 // Reemplazar separadores comunes
 filtered = filtered.Replace("-", "_").Replace("/", "_");
 return filtered;
 }
}