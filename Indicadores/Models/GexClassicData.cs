namespace GexBotA.Models;

/// <summary>
/// Represents the full classic GEX data response from the Gexbot API.
/// Endpoint: GET https://api.gexbot.com/{TICKER}/classic/{AGGREGATION_PERIOD}?key={API_KEY}
/// </summary>
public sealed class GexClassicData
{
    public long Timestamp { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public int MinDte { get; set; }
    public int SecMinDte { get; set; }
    public double Spot { get; set; }
    public double ZeroGamma { get; set; }
    public double MajorPosVol { get; set; }
    public double MajorPosOi { get; set; }
    public double MajorNegVol { get; set; }
    public double MajorNegOi { get; set; }
    public List<StrikeData> Strikes { get; set; } = new();
    public double SumGexVol { get; set; }
    public double SumGexOi { get; set; }
    public List<MaxChangeEntry> MaxPriors { get; set; } = new();

    public DateTime UpdateTime => DateTimeOffset.FromUnixTimeSeconds(Timestamp).LocalDateTime;
}

/// <summary>
/// Represents a single strike's GEX data.
/// Each strike has: [strike_price, gex_by_volume, gex_by_oi, [prior1, prior5, prior10, prior15, prior30]]
/// </summary>
public sealed class StrikeData
{
    public double Strike { get; set; }
    public double GexByVolume { get; set; }
    public double GexByOi { get; set; }
    /// <summary>
    /// Historical GEX values at 1, 5, 10, 15, 30 min intervals (lookback dots).
    /// </summary>
    public List<double> Priors { get; set; } = new();
}

/// <summary>
/// Represents a max change entry: [strike, gex_change_value].
/// Order in the API: current, 1min, 5min, 10min, 15min, 30min.
/// </summary>
public sealed class MaxChangeEntry
{
    public double Strike { get; set; }
    public double GexChange { get; set; }
}

/// <summary>
/// Lightweight data from the /majors endpoint.
/// Contains only key levels without the full strike histogram.
/// Endpoint: GET https://api.gexbot.com/{TICKER}/classic/{AGGREGATION}/majors?key={API_KEY}
/// </summary>
public sealed class GexMajorsData
{
    public long Timestamp { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public double Spot { get; set; }
    public double MajorPosVol { get; set; }
    public double MajorPosOi { get; set; }
    public double MajorNegVol { get; set; }
    public double MajorNegOi { get; set; }
    public double ZeroGamma { get; set; }
    public double NetGexVol { get; set; }
    public double NetGexOi { get; set; }

    public DateTime UpdateTime => DateTimeOffset.FromUnixTimeSeconds(Timestamp).LocalDateTime;
}
