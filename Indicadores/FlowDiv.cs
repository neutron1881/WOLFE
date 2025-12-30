using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using ATAS.Indicators;

namespace ATAS.Indicators.Technical
{
    [DisplayName("Option Flow Strike Divergence")]
    [Category("NewFlow")]
    public sealed class FlowDiv : Indicator
    {
        public enum InputSource
        {
            [Display(Name = "CSV Call ITM Impact")] CallItmImpact,
            [Display(Name = "CSV Put ITM Impact")] PutItmImpact
        }

        private readonly struct Snapshot
        {
            public Snapshot(DateTime time, decimal call, decimal put)
            {
                Time = time;
                Call = call;
                Put = put;
            }

            public DateTime Time { get; }
            public decimal Call { get; }
            public decimal Put { get; }
        }

        // O(1) ring buffer (fixed capacity). We prune by time (>= LookbackSeconds) when advancing.
        private Snapshot[] _buffer = Array.Empty<Snapshot>();
        private int _head;
        private int _count;

        private decimal _lastSignalBar = -1;

        private readonly ValueDataSeries _callDeltaSeries = new("ΔCall120s")
        {
            VisualType = VisualMode.Line,
            Color = System.Windows.Media.Colors.DodgerBlue,
            Width = 2
        };

        private readonly ValueDataSeries _putDeltaSeries = new("ΔPut120s")
        {
            VisualType = VisualMode.Line,
            Color = System.Windows.Media.Colors.IndianRed,
            Width = 2
        };

        private readonly ValueDataSeries _callImpactSeries = new("CallItmImpact")
        {
            VisualType = VisualMode.Line,
            Color = System.Windows.Media.Colors.DeepSkyBlue,
            Width = 1
        };

        private readonly ValueDataSeries _putImpactSeries = new("PutItmImpact")
        {
            VisualType = VisualMode.Line,
            Color = System.Windows.Media.Colors.OrangeRed,
            Width = 1
        };

        // Líneas de niveles (porcentaje de máximos) para Call/Put ITM Impact
        private readonly ValueDataSeries _callLevel100 = new("Call 100%", "Call 100%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.DimGray };
        private readonly ValueDataSeries _callLevel75 = new("Call 75%", "Call 75%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.Gray };
        private readonly ValueDataSeries _callLevel50 = new("Call 50%", "Call 50%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.DarkGray };
        private readonly ValueDataSeries _callLevel25 = new("Call 25%", "Call 25%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.LightGray };

        private readonly ValueDataSeries _putLevel100 = new("Put 100%", "Put 100%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.DimGray };
        private readonly ValueDataSeries _putLevel75 = new("Put 75%", "Put 75%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.Gray };
        private readonly ValueDataSeries _putLevel50 = new("Put 50%", "Put 50%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.DarkGray };
        private readonly ValueDataSeries _putLevel25 = new("Put 25%", "Put 25%") { VisualType = VisualMode.Line, Color = System.Windows.Media.Colors.LightGray };

        private readonly ValueDataSeries _signalSeries = new("Signal")
        {
            VisualType = VisualMode.Line,
            Color = System.Windows.Media.Colors.Transparent,
            IsHidden = true
        };

        private int _lookbackSeconds = 120;
        private decimal _spikeThreshold = 1m;
        private decimal _proximityBuffer = 0.15m;
        private decimal _slBuffer = 0.05m;
        private decimal _minHardStop = 0.15m;
        private decimal _targetOffset = 0.30m;

        private decimal _maxCallImpact;
        private decimal _maxPutImpact;

        // CSV mapping/config
        private string _csvFileName = string.Empty;
        private int _sessionDaysOffset = 0;
        private string _cachedCsvPath = string.Empty;
        private DateTime _lastCsvWriteTime = DateTime.MinValue;
        private bool _autoRefreshCsv = true;
        private int _csvRefreshIntervalSeconds = 5;
        private DateTime _lastCsvRefresh = DateTime.MinValue;
        private int _gmtOffset = 0;
        private int _timeToleranceSeconds = 10;

        private readonly SortedList<DateTime, (decimal CallItm, decimal PutItm)> _csv = new();

        private InputSource _callImpactSource = InputSource.CallItmImpact;
        private InputSource _putImpactSource = InputSource.PutItmImpact;

        private decimal _lastCallImpact;
        private decimal _lastPutImpact;

        [Display(GroupName = "0. CSV", Name = "CSV File Name", Order = 5, Description = "Nombre del CSV dentro de UnifiedInstrument (ej: UnifiedInstrument.csv)")]
        public string CsvFileName
        {
            get => _csvFileName;
            set
            {
                _csvFileName = value ?? string.Empty;
                ReloadCsv(force: true);
                RecalculateValues();
            }
        } // Added missing closing brace for CsvFileName property

        [Display(GroupName = "0. CSV", Name = "Session Days Offset", Order = 7, Description = "0 = hoy, 1 = ayer, 2 = anteayer...")]
        [Range(0, 30)]
        public int SessionDaysOffset
        {
            get => _sessionDaysOffset;
            set
            {
                _sessionDaysOffset = Math.Clamp(value, 0, 30);
                ReloadCsv(force: true);
                RecalculateValues();
            }
        }

        [Display(GroupName = "0. CSV", Name = "Auto Refresh CSV", Order = 10)]
        public bool AutoRefreshCsv
        {
            get => _autoRefreshCsv;
            set { _autoRefreshCsv = value; }
        }

        [Display(GroupName = "0. CSV", Name = "CSV Refresh Interval (sec)", Order = 20)]
        [Range(1, 60)]
        public int CsvRefreshIntervalSeconds
        {
            get => _csvRefreshIntervalSeconds;
            set { _csvRefreshIntervalSeconds = Math.Clamp(value, 1, 60); }
        }

        [Display(GroupName = "0. CSV", Name = "GMT Offset (hours)", Order = 30)]
        [Range(-12, 12)]
        public int GmtOffset
        {
            get => _gmtOffset;
            set { _gmtOffset = Math.Clamp(value, -12, 12); RecalculateValues(); }
        }

        [Display(GroupName = "0. CSV", Name = "Time Tolerance (sec)", Order = 40, Description = "Tolerancia para matchear timestamp (±segundos)")]
        [Range(1, 120)]
        public int TimeToleranceSeconds
        {
            get => _timeToleranceSeconds;
            set { _timeToleranceSeconds = Math.Clamp(value, 1, 120); RecalculateValues(); }
        }

        [Display(GroupName = "1. Mapping", Name = "Call Impact", Order = 10)]
        public InputSource CallItmImpactSource
        {
            get => _callImpactSource;
            set { _callImpactSource = value; RecalculateValues(); }
        }

        [Display(GroupName = "1. Mapping", Name = "Put Impact", Order = 20)]
        public InputSource PutItmImpactSource
        {
            get => _putImpactSource;
            set { _putImpactSource = value; RecalculateValues(); }
        }

        [Display(GroupName = "1. Engine", Name = "Lookback Seconds", Order = 10)]
        [Range(10, 600)]
        public int LookbackSeconds
        {
            get => _lookbackSeconds;
            set
            {
                _lookbackSeconds = Math.Clamp(value, 10, 600);
                ResetBuffer();
                RecalculateValues();
            }
        }

        [Display(GroupName = "1. Engine", Name = "Spike Threshold", Order = 20, Description = "Umbral absoluto para ΔCall y ΔPut")]
        public decimal SpikeThreshold
        {
            get => _spikeThreshold;
            set { _spikeThreshold = value; RecalculateValues(); }
        }

        [Display(GroupName = "2. Strike Filter", Name = "Proximity Buffer", Order = 10, Description = "Distancia máxima a nearest 0.50")]
        public decimal ProximityBuffer
        {
            get => _proximityBuffer;
            set { _proximityBuffer = Math.Max(0m, value); RecalculateValues(); }
        }

        [Display(GroupName = "3. Risk", Name = "SL Buffer", Order = 10, Description = "Offset desde el strike para stop")]
        public decimal SLBuffer
        {
            get => _slBuffer;
            set { _slBuffer = Math.Max(0m, value); RecalculateValues(); }
        }

        [Display(GroupName = "3. Risk", Name = "Min Hard Stop", Order = 20, Description = "Stop mínimo en puntos")]
        public decimal MinHardStop
        {
            get => _minHardStop;
            set { _minHardStop = Math.Max(0m, value); RecalculateValues(); }
        }

        [Display(GroupName = "3. Risk", Name = "Target Offset", Order = 30, Description = "Take profit fijo en puntos")]
        public decimal TargetOffset
        {
            get => _targetOffset;
            set { _targetOffset = Math.Max(0m, value); RecalculateValues(); }
        }

        public FlowDiv() : base(true)
        {
            DenyToChangePanel = false;
            Panel = IndicatorDataProvider.NewPanel;

            DataSeries[0] = _callDeltaSeries;
            DataSeries.Add(_putDeltaSeries);
            DataSeries.Add(_callImpactSeries);
            DataSeries.Add(_putImpactSeries);
            DataSeries.Add(_callLevel100);
            DataSeries.Add(_callLevel75);
            DataSeries.Add(_callLevel50);
            DataSeries.Add(_callLevel25);
            DataSeries.Add(_putLevel100);
            DataSeries.Add(_putLevel75);
            DataSeries.Add(_putLevel50);
            DataSeries.Add(_putLevel25);
            DataSeries.Add(_signalSeries);

            ResetBuffer();
        }

        protected override void OnInitialize()
        {
            // No-op
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            var candle = GetCandle(bar);
            if (candle == null)
                return;

            // refresh CSV if needed
            if (_autoRefreshCsv && (DateTime.Now - _lastCsvRefresh).TotalSeconds >= _csvRefreshIntervalSeconds)
            {
                ReloadCsv(force: false);
                _lastCsvRefresh = DateTime.Now;
            }

            var instrumentTimeZoneOffset = InstrumentInfo?.TimeZone ?? 0;
            var tNow = candle.Time.AddHours(instrumentTimeZoneOffset).AddHours(_gmtOffset);

            // Price is underlying last price
            var price = candle.Close;

            // Map custom ITM impacts from CSV.
            if (!TryGetCsvImpactsClosest(tNow, out var callImpactNow, out var putImpactNow))
            {
                callImpactNow = _lastCallImpact;
                putImpactNow = _lastPutImpact;
            }
            else
            {
                _lastCallImpact = callImpactNow;
                _lastPutImpact = putImpactNow;
            }

            _callImpactSeries[bar] = callImpactNow;
            _putImpactSeries[bar] = putImpactNow;

            if (callImpactNow > _maxCallImpact)
                _maxCallImpact = callImpactNow;
            if (putImpactNow > _maxPutImpact)
                _maxPutImpact = putImpactNow;

            _callLevel100[bar] = _maxCallImpact;
            _callLevel75[bar] = _maxCallImpact * 0.75m;
            _callLevel50[bar] = _maxCallImpact * 0.50m;
            _callLevel25[bar] = _maxCallImpact * 0.25m;

            _putLevel100[bar] = _maxPutImpact;
            _putLevel75[bar] = _maxPutImpact * 0.75m;
            _putLevel50[bar] = _maxPutImpact * 0.50m;
            _putLevel25[bar] = _maxPutImpact * 0.25m;

            PushSnapshot(tNow, callImpactNow, putImpactNow);

            if (!TryGetLookbackSnapshot(tNow.AddSeconds(-_lookbackSeconds), out var past))
            {
                _callDeltaSeries[bar] = 0m;
                _putDeltaSeries[bar] = 0m;
                return;
            }

            var dCall = callImpactNow - past.Call;
            var dPut = putImpactNow - past.Put;

            _callDeltaSeries[bar] = dCall;
            _putDeltaSeries[bar] = dPut;

            // Strike proximity filter
            var nearestStrike = Math.Round(price * 2m) / 2m;
            var nearStrike = Math.Abs(price - nearestStrike) <= _proximityBuffer;
            if (!nearStrike)
                return;

            // Dual-spike divergence
            bool shortTrigger = dCall > _spikeThreshold && dPut < -_spikeThreshold;
            bool longTrigger = dCall < -_spikeThreshold && dPut > _spikeThreshold;

            if (!shortTrigger && !longTrigger)
                return;

            // De-duplicate per bar
            if (_lastSignalBar == bar)
                return;
            _lastSignalBar = bar;

            // Risk model
            if (longTrigger)
            {
                var entry = price;
                var slCandidate = nearestStrike - _slBuffer;
                var sl = Math.Min(slCandidate, entry - _minHardStop);
                var tp = entry + _targetOffset;

                DrawSignal(bar, isLong: true);
                FireAlert("FlowDiv_LONG", $"LONG @ {entry:F2} | SL {sl:F2} | TP {tp:F2} | Strike {nearestStrike:F2}");
            }
            else if (shortTrigger)
            {
                var entry = price;
                var slCandidate = nearestStrike + _slBuffer;
                var sl = Math.Max(slCandidate, entry + _minHardStop);
                var tp = entry - _targetOffset;

                DrawSignal(bar, isLong: false);
                FireAlert("FlowDiv_SHORT", $"SHORT @ {entry:F2} | SL {sl:F2} | TP {tp:F2} | Strike {nearestStrike:F2}");
            }
        }

        private void ReloadCsv(bool force)
        {
            if (string.IsNullOrWhiteSpace(_csvFileName))
                return;

            var userDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var sessionDate = DateTime.Now.AddDays(-_sessionDaysOffset).ToString("yyyy_MM_dd", CultureInfo.InvariantCulture);
            var path = Path.Combine(userDocuments, "dashcsv", "Dashdata", $"session_{sessionDate}", "UnifiedInstrument", _csvFileName);

            if (!File.Exists(path))
                return;

            DateTime writeTime;
            try
            {
                writeTime = File.GetLastWriteTime(path);
            }
            catch
            {
                return;
            }

            if (!force && path == _cachedCsvPath && writeTime == _lastCsvWriteTime)
                return;

            _cachedCsvPath = path;
            _lastCsvWriteTime = writeTime;

            try
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length < 2)
                    return;

                var headers = SplitCsvLine(lines[0]);
                int idxTime = FindIndex(headers, "iso_time");
                if (idxTime < 0)
                    idxTime = FindIndex(headers, "timestamp_ms");

                int idxCallItm = FindIndex(headers, "call_itm_impact");
                int idxPutItm = FindIndex(headers, "put_itm_impact");

                if (idxTime < 0 || idxCallItm < 0 || idxPutItm < 0)
                    return;

                _csv.Clear();
                for (int i = 1; i < lines.Length; i++)
                {
                    var cols = SplitCsvLine(lines[i]);
                    if (cols.Length <= Math.Max(idxTime, Math.Max(idxCallItm, idxPutItm)))
                        continue;

                    if (!TryParseDateTime(cols[idxTime], out var ts))
                    {
                        if (!TryParseTimestamp(cols[idxTime], out ts))
                            continue;
                    }

                    if (!TryParseDecimal(cols[idxCallItm], out var cItm))
                        cItm = 0m;
                    if (!TryParseDecimal(cols[idxPutItm], out var pItm))
                        pItm = 0m;

                    if (!_csv.ContainsKey(ts))
                        _csv.Add(ts, (cItm, pItm));
                    else
                        _csv[ts] = (cItm, pItm);
                }
            }
            catch
            {
                // ignore parse errors
            }
        }

        private bool TryGetCsvImpactsClosest(DateTime target, out decimal callItm, out decimal putItm)
        {
            callItm = 0m;
            putItm = 0m;

            if (_csv.Count == 0)
                return false;

            // Find nearest timestamp in ±TimeToleranceSeconds using binary search
            int idx = FindNearestIndex(target);
            if (idx < 0)
                return false;

            var ts = _csv.Keys[idx];
            if (Math.Abs((ts - target).TotalSeconds) > _timeToleranceSeconds)
                return false;

            (callItm, putItm) = _csv.Values[idx];
            return true;
        }

        private int FindNearestIndex(DateTime target)
        {
            if (_csv.Count == 0)
                return -1;

            int lo = 0;
            int hi = _csv.Count - 1;

            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) / 2);
                var cmp = _csv.Keys[mid].CompareTo(target);
                if (cmp == 0)
                    return mid;
                if (cmp < 0)
                    lo = mid + 1;
                else
                    hi = mid - 1;
            }

            // hi is last < target, lo is first > target
            if (hi < 0) return 0;
            if (lo >= _csv.Count) return _csv.Count - 1;

            var t1 = _csv.Keys[hi];
            var t2 = _csv.Keys[lo];
            return (target - t1) <= (t2 - target) ? hi : lo;
        }

        private static int FindIndex(string[] headers, string key)
        {
            key = key ?? string.Empty;
            var knorm = NormalizeHeader(key);
            for (int i = 0; i < headers.Length; i++)
            {
                var norm = NormalizeHeader(headers[i] ?? string.Empty);
                if (norm.Contains(knorm))
                    return i;
            }
            return -1;
        }

        private static string NormalizeHeader(string s)
        {
            return new string(s.Where(ch => char.IsLetterOrDigit(ch)).ToArray()).ToLowerInvariant();
        }

        private static string[] SplitCsvLine(string line)
        {
            var list = new List<string>();
            bool inQuotes = false;
            var cur = new System.Text.StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        cur.Append('"');
                        i++;
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
            value = 0m;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().Trim('"').Replace("$", string.Empty).Replace(" ", string.Empty);
            return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseDateTime(string? s, out DateTime value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            return DateTime.TryParse(s.Trim('"', ' '), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out value);
        }

        private static bool TryParseTimestamp(string? s, out DateTime value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (long.TryParse(s.Trim(), out long ms))
            {
                value = new DateTime(1970, 1, 1).AddMilliseconds(ms);
                return true;
            }
            return false;
        }

        private void DrawSignal(int bar, bool isLong)
        {
            // Draw markers on price chart
            var candle = GetCandle(bar);
            if (candle == null)
                return;

            var color = isLong ? Color.LimeGreen : Color.Red;

            // Draw in price panel. We keep it lightweight using Drawings API.
            var tag = $"FlowDiv_{(isLong ? "L" : "S")}_{bar}";

            // Use Chart drawing primitives exposed by Indicator base.
            // Fallback: mark on hidden series so at least something exists.
            _signalSeries[bar] = isLong ? 1m : -1m;

            try
            {
                var text = isLong ? "▲" : "▼";
                var y = isLong ? candle.Low : candle.High;
                // Match the AddText signature used in this workspace (see InitialBalance).
                AddText(tag, text, true, bar, y, 0, 0, color, Color.Transparent, Color.Transparent, 12f, ATAS.Indicators.Drawing.DrawingText.TextAlign.Right);
            }
            catch
            {
                // ignore if drawing API not available in this environment
            }
        }

        private void FireAlert(string id, string message)
        {
            try
            {
                AddAlert(id, message);
            }
            catch
            {
                // ignore if alerts are not supported in this build
            }
        }

        private void ResetBuffer()
        {
            // Capacity sized to cover at least lookback window at high tick rates.
            // 4096 keeps memory modest and provides room; ring buffer is O(1).
            _buffer = new Snapshot[4096];
            _head = 0;
            _count = 0;
        }

        private void PushSnapshot(DateTime time, decimal call, decimal put)
        {
            if (_buffer.Length == 0)
                ResetBuffer();

            _buffer[_head] = new Snapshot(time, call, put);
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length)
                _count++;

            // prune too-old snapshots: advance logical tail by reducing count.
            // We only prune enough to ensure oldest >= now-lookback-5s (small margin)
            var cutoff = time.AddSeconds(-_lookbackSeconds - 5);
            while (_count > 0)
            {
                var tailIndex = (_head - _count + _buffer.Length) % _buffer.Length;
                if (_buffer[tailIndex].Time >= cutoff)
                    break;
                _count--;
            }
        }

        private bool TryGetLookbackSnapshot(DateTime targetTime, out Snapshot snapshot)
        {
            snapshot = default;
            if (_count == 0)
                return false;

            // We need the snapshot closest to (<= targetTime). Scan from tail forward.
            // N is bounded by ring size and by 120s retention; for performance this is typically small.
            int tailIndex = (_head - _count + _buffer.Length) % _buffer.Length;
            Snapshot best = default;
            bool found = false;

            for (int i = 0; i < _count; i++)
            {
                int idx = (tailIndex + i) % _buffer.Length;
                var s = _buffer[idx];
                if (s.Time <= targetTime)
                {
                    best = s;
                    found = true;
                    continue;
                }

                // As soon as we pass targetTime, stop.
                break;
            }

            if (!found)
                return false;

            snapshot = best;
            return true;
        }
    }
}
