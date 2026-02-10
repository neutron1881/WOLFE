# API Documentation: Options Exposure Data

This API provides access to real-time options exposure data (GEX, DEX, Gamma Walls) for use in trading platforms and custom indicators (e.g., ATAS, NinjaTrader, TradingView).

## Base Configuration

- **Base URL:** `https://nel.tutela2.org/trading`
- **Authentication:** Bearer Token
- **Rate Limit:** Standard server limits apply.

## Authentication

All requests must include the `Authorization` header with a valid API token.

```http
Authorization: Bearer YOUR_API_TOKEN_HERE
```

*To obtain a token, navigate to the **Settings > API Tokens** section in the web dashboard.*

---

## Endpoint: Get Options Exposure

Retrieves options exposure data including Net Gamma, Delta Exposure, Open Interest, and Volume, aggregated by strike and expiration.

**URL:** `GET /api/v1/options/exposure`

### Query Parameters

| Parameter | Type   | Required | Description                                                                 |
|-----------|--------|----------|-----------------------------------------------------------------------------|
| `symbol`  | String | **Yes**  | The ticker symbol to fetch (e.g., `SPX`, `QQQ`, `SPY`, `IWM`). Case insensitive. |
| `dte`     | Number | No       | Filter expirations by Days To Expiration (DTE). Returns all expirations ≤ `dte`. <br>Examples: `0` (0DTE), `1` (0+1DTE), `7` (Weeklies). If omitted, returns all available expirations. |

### Response Structure

Returns a JSON object containing the market state and an array of expirations.

```json
{
  "symbol": "QQQ",
  "dte": 7,                       // Requested DTE filter (null if not filtered)
  "spotPrice": 495.32,            // Current underlying spot price
  "timestamp": "2024-05-20T14:30:00Z", // Data generation timestamp
  "totalExpirations": 4,          // Number of expirations returned
  "expirations": [
    {
      "expiration": "2024-05-21", // Expiration Date (YYYY-MM-DD)
      "dte": 1,                   // Days to Expiration
      "strikes": [                // Array of Strike Prices (Strings)
        "490", "491", "492", "493", "494", "495" 
      ],
      "netGamma": [               // Net Gamma per strike
        -1500, -800, 200, 5000, 1200, -300
      ],
      "call": {                   // Call Side Data
        "absDelta": [...],        // Absolute Delta Exposure
        "absGamma": [...],        // Absolute Gamma Exposure
        "openInterest": [...],    // Open Interest
        "volume": [...]           // Volume
      },
      "put": {                    // Put Side Data
        "absDelta": [...],
        "absGamma": [...],
        "openInterest": [...],
        "volume": [...]
      }
    }
  ]
}
```

### Data Definitions

- **Net Gamma (GEX):** The net gamma exposure at each strike. Positive values suggest stability (dealers hedging against moves), negative values suggest volatility (dealers hedging with moves).
- **Abs Delta (DEX):** The absolute delta exposure, representing the notional value of shares dealers must buy/sell.
- **Strikes:** The strike prices corresponding to the data arrays. All arrays (`netGamma`, `call.*`, `put.*`) are aligned by index to this `strikes` array.

---

## Integration Examples

### 1. CURL Request

```bash
curl -X GET "https://nel.tutela2.org/trading/api/v1/options/exposure?symbol=QQQ&dte=7" \
     -H "Authorization: Bearer cmlgvefk70000yw7jh4e98v7w"
```

### 2. C# (ATAS / NinjaTrader)

```csharp
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;

public class OptionsDataClient
{
    private static readonly HttpClient client = new HttpClient();
    private const string ApiUrl = "https://nel.tutela2.org/trading/api/v1/options/exposure";
    private string _token;

    public OptionsDataClient(string token)
    {
        _token = token;
        client.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
    }

    public async Task<string> GetExposureData(string symbol, int dte)
    {
        string url = $"{ApiUrl}?symbol={symbol}&dte={dte}";
        
        try
        {
            HttpResponseMessage response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException e)
        {
            return $"Error: {e.Message}";
        }
    }
}
```
