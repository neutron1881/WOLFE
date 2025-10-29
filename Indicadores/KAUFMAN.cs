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
 CallsUsd,
 PutsUsd,
 IvCalls,
 IvPuts,
 DCall,
 DPuts
 }

 private readonly ValueDataSeries _kerASeries = new("KER A") { Color = CrossColor.FromArgb(255,255,165,0), Width =2 };
 private readonly ValueDataSeries _kerBSeries = new("KER B") { Color = CrossColor.FromArgb(255,128,0,128), Width =2 };
 // Líneas horizontales
 private readonly ValueDataSeries _level1Series = new("Level1") { Color = CrossColor.FromArgb(255,128,128,128), Width =1 };
 private readonly ValueDataSeries _level2Series = new("Level2") { Color = CrossColor.FromArgb(255,100,149,237), Width =1 };

 // Backing fields (para refresco inmediato)
 private string _filePath = @"C:\\data\\SPY_Cash.csv";
 private double _gmtOffsetHours =0d;
 private SeriesColumn _seriesA = SeriesColumn.CallsUsd;
 private SeriesColumn _seriesB = SeriesColumn.PutsUsd;
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
 private readonly Dictionary<SeriesColumn, List<decimal>> _series = new()
 {
 [SeriesColumn.CallsUsd] = new(),
 [SeriesColumn.PutsUsd] = new(),
 [SeriesColumn.IvCalls] = new(),
 [SeriesColumn.IvPuts] = new(),
 [SeriesColumn.DCall] = new(),
 [SeriesColumn.DPuts] = new()
 };

 // KER pre-calculados por columna
 private readonly Dictionary<SeriesColumn, List<decimal>> _ker = new()
 {
 [SeriesColumn.CallsUsd] = new(),
 [SeriesColumn.PutsUsd] = new(),
 [SeriesColumn.IvCalls] = new(),
 [SeriesColumn.IvPuts] = new(),
 [SeriesColumn.DCall] = new(),
 [SeriesColumn.DPuts] = new()
 };

 public DEI_DeltaImpact_ATAS()
 : base(true)
 {
 DenyToChangePanel = false;

 DataSeries[0] = _kerASeries;
 DataSeries.Add(_kerBSeries);
 DataSeries.Add(_level1Series);
 DataSeries.Add(_level2Series);
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

 for (int i = start; i < lines.Count; i++)
 {
 var parts = SplitLine(lines[i]);
 if (parts.Length ==0)
 continue;

 if (!TryParseTimestamp(parts, map.Timestamp, out var ts))
 continue;

 // Aplicar ajuste GMT en horas
 ts = ts.AddHours(GmtOffsetHours);

 var sums = agg.TryGetValue(ts, out var arr) ? arr : new decimal[6];

 TryAdd(parts, map.CallsUsd, ref sums[0], ci);
 TryAdd(parts, map.PutsUsd, ref sums[1], ci);
 TryAdd(parts, map.IvCalls, ref sums[2], ci);
 TryAdd(parts, map.IvPuts, ref sums[3], ci);
 TryAdd(parts, map.DCall, ref sums[4], ci);
 TryAdd(parts, map.DPuts, ref sums[5], ci);

 agg[ts] = sums;
 }

 // Ordena por timestamp y vuelca a listas
 foreach (var kv in agg.OrderBy(k => k.Key))
 {
 _times.Add(kv.Key);
 var v = kv.Value;
 _series[SeriesColumn.CallsUsd].Add(v[0]);
 _series[SeriesColumn.PutsUsd].Add(v[1]);
 _series[SeriesColumn.IvCalls].Add(v[2]);
 _series[SeriesColumn.IvPuts].Add(v[3]);
 _series[SeriesColumn.DCall].Add(v[4]);
 _series[SeriesColumn.DPuts].Add(v[5]);
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

 private sealed class ColumnMap
 {
 public int Timestamp = -1;
 public int CallsUsd = -1;
 public int PutsUsd = -1;
 public int IvCalls = -1;
 public int IvPuts = -1;
 public int DCall = -1;
 public int DPuts = -1;
 }

 private static ColumnMap MapColumns(string[] header)
 {
 var map = new ColumnMap();
 if (header == null || header.Length ==0)
 return map;

 for (int i =0; i < header.Length; i++)
 {
 var norm = Normalize(header[i]);

 if (norm == "timestamp" || norm == "time" || norm == "datetime")
 map.Timestamp = i;

 else if (norm == "ivcalls" || norm == "callsiv" || norm == "ivcall")
 map.IvCalls = i;
 else if (norm == "ivputs" || norm == "putsiv" || norm == "ivput")
 map.IvPuts = i;

 else if (norm == "dcall" || norm == "dcalls" || norm == "delta_call" || norm == "call_delta")
 map.DCall = i;
 else if (norm == "dputs" || norm == "dput" || norm == "delta_put" || norm == "put_delta")
 map.DPuts = i;

 else if (norm.Contains("call"))
 map.CallsUsd = i;
 else if (norm.Contains("put"))
 map.PutsUsd = i;
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