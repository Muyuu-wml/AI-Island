using System.Globalization;
using System.Text.Json;

namespace AIIsland.Core;

public sealed record WeatherNow(
    double Temperature,
    double FeelsLike,
    double Humidity,
    double Wind,
    int Code,
    bool IsDay);

public sealed record WeatherDay(
    DateOnly Date,
    int Code,
    double Max,
    double Min,
    double? Precipitation,
    string Sunrise = "",
    string Sunset = "",
    double? Uv = null);

public sealed record WeatherReading(
    SourceState State,
    string City = "",
    WeatherNow? Now = null,
    IReadOnlyList<WeatherDay>? Days = null,
    double? Aqi = null,
    string? Error = null,
    string Status = "",
    DateTimeOffset At = default)
{
    public static WeatherReading Unavailable(string status) => new(SourceState.Unavailable, Status: status);
    public static WeatherReading Failure(string error) => new(SourceState.Error, Error: error);
}

/// <summary>
/// Open-Meteo weather: geocoding, current conditions and a 3-day outlook. No key is needed,
/// so the only configuration is a city name or a "lat,lon" pair.
/// </summary>
public sealed class WeatherService
{
    private const string GeocodeUrl = "https://geocoding-api.open-meteo.com/v1/search";
    private const string ForecastUrl = "https://api.open-meteo.com/v1/forecast";
    private const string AirUrl = "https://air-quality-api.open-meteo.com/v1/air-quality";

    private static readonly Dictionary<string, (double Lat, double Lon, string Label)> Places = new(StringComparer.OrdinalIgnoreCase);

    public async Task<WeatherReading> ReadAsync(string? place, bool withAqi, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(place)) return WeatherReading.Unavailable("请填写城市");

        try
        {
            var (lat, lon, label) = await LocateAsync(place.Trim(), token);
            var latText = lat.ToString("0.####", CultureInfo.InvariantCulture);
            var lonText = lon.ToString("0.####", CultureInfo.InvariantCulture);

            var current = "temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,is_day,wind_speed_10m";
            var daily = "weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset,uv_index_max";
            var root = await GetAsync(
                $"{ForecastUrl}?latitude={latText}&longitude={lonText}" +
                $"&current={current}&daily={daily}&timezone=auto&forecast_days=3&wind_speed_unit=ms",
                token);

            var reading = Parse(root);
            if (reading is null) return WeatherReading.Failure("未解析到天气数据");

            double? aqi = null;
            if (withAqi)
            {
                try
                {
                    var air = await GetAsync($"{AirUrl}?latitude={latText}&longitude={lonText}&current=european_aqi", token);
                    if (air.TryGetProperty("current", out var airNow) && airNow.ValueKind == JsonValueKind.Object)
                        aqi = Numbers.Double(airNow, "european_aqi");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { /* AQI is a bonus; a failure must not hide the forecast. */ }
            }

            return reading with { City = label, Aqi = aqi, At = DateTimeOffset.Now };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) { return WeatherReading.Failure("读取天气失败：" + e.Message); }
    }

    /// <summary>Accepts "39.9,116.4" directly, otherwise resolves the name through Open-Meteo geocoding.</summary>
    private static async Task<(double Lat, double Lon, string Label)> LocateAsync(string place, CancellationToken token)
    {
        if (Places.TryGetValue(place, out var cached)) return cached;

        var parts = place.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            && lat is >= -90 and <= 90 && lon is >= -180 and <= 180)
        {
            var direct = (lat, lon, lat.ToString("0.##") + ", " + lon.ToString("0.##"));
            Places[place] = direct;
            return direct;
        }

        var root = await GetAsync($"{GeocodeUrl}?name={Uri.EscapeDataString(place)}&count=1&language=zh&format=json", token);
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            throw new InvalidOperationException("找不到这个城市");

        var first = results[0];
        var found = Numbers.Double(first, "latitude") ?? throw new InvalidOperationException("城市坐标缺失");
        var foundLon = Numbers.Double(first, "longitude") ?? throw new InvalidOperationException("城市坐标缺失");
        var label = Numbers.Text(first, "name") ?? place;
        if (Numbers.Text(first, "country_code") is { Length: > 0 } code && code != "CN") label += " · " + code;

        var place3 = (found, foundLon, label);
        Places[place] = place3;
        return place3;
    }

    private static async Task<JsonElement> GetAsync(string url, CancellationToken token)
    {
        using var response = await Http.Client.GetAsync(url, token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("返回 HTTP " + (int)response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return document.RootElement.Clone();
    }

    /// <summary>Parses a forecast response; null when the required blocks are missing.</summary>
    internal static WeatherReading? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("current", out var now) || now.ValueKind != JsonValueKind.Object) return null;
        if (Numbers.Double(now, "temperature_2m") is not { } temperature) return null;

        var current = new WeatherNow(
            temperature,
            Numbers.Double(now, "apparent_temperature") ?? temperature,
            Numbers.Double(now, "relative_humidity_2m") ?? 0,
            Numbers.Double(now, "wind_speed_10m") ?? 0,
            (int)(Numbers.Double(now, "weather_code") ?? 0),
            Numbers.Double(now, "is_day") is not 0);

        var days = new List<WeatherDay>();
        if (root.TryGetProperty("daily", out var daily) && daily.ValueKind == JsonValueKind.Object
            && daily.TryGetProperty("time", out var times) && times.ValueKind == JsonValueKind.Array)
        {
            var codes = ReadArray(daily, "weather_code");
            var max = ReadArray(daily, "temperature_2m_max");
            var min = ReadArray(daily, "temperature_2m_min");
            var rain = ReadArray(daily, "precipitation_probability_max");
            var uv = ReadArray(daily, "uv_index_max");

            for (var index = 0; index < times.GetArrayLength(); index++)
            {
                if (!DateOnly.TryParseExact(times[index].GetString(), "yyyy-MM-dd", out var date)) continue;
                days.Add(new WeatherDay(
                    date,
                    At(codes, index) is { } code ? (int)code : 0,
                    At(max, index) ?? 0,
                    At(min, index) ?? 0,
                    At(rain, index),
                    Clock(daily, "sunrise", index),
                    Clock(daily, "sunset", index),
                    At(uv, index)));
            }
        }

        return new WeatherReading(SourceState.Ok, Now: current, Days: days);
    }

    private static double[] ReadArray(JsonElement daily, string name)
    {
        if (!daily.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array) return Array.Empty<double>();
        var result = new double[values.GetArrayLength()];
        for (var index = 0; index < result.Length; index++)
        {
            var item = values[index];
            result[index] = item.ValueKind == JsonValueKind.Number ? item.GetDouble() : double.NaN;
        }
        return result;
    }

    private static double? At(double[] values, int index) =>
        index < values.Length && !double.IsNaN(values[index]) ? values[index] : null;

    /// <summary>Reads a "2026-11-02T06:41" style daily string and keeps only HH:mm.</summary>
    private static string Clock(JsonElement daily, string name, int index)
    {
        if (!daily.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array) return "";
        if (index >= values.GetArrayLength()) return "";
        var text = values[index].GetString();
        return text is { Length: >= 16 } ? text.Substring(11, 5) : "";
    }

    /// <summary>WMO weather code → short Chinese text plus an emoji for the card.</summary>
    public static (string Text, string Icon) Describe(int code, bool isDay = true) => code switch
    {
        0 => (isDay ? "晴" : "晴夜", isDay ? "☀️" : "🌙"),
        1 => ("少云", isDay ? "🌤️" : "🌙"),
        2 => ("多云", "⛅"),
        3 => ("阴", "☁️"),
        45 or 48 => ("雾", "🌫️"),
        51 or 53 or 55 => ("毛毛雨", "🌦️"),
        56 or 57 => ("冻毛毛雨", "🌧️"),
        61 => ("小雨", "🌧️"),
        63 => ("中雨", "🌧️"),
        65 => ("大雨", "🌧️"),
        66 or 67 => ("冻雨", "🌧️"),
        71 => ("小雪", "🌨️"),
        73 => ("中雪", "🌨️"),
        75 => ("大雪", "❄️"),
        77 => ("雪粒", "❄️"),
        80 or 81 => ("阵雨", "🌦️"),
        82 => ("强阵雨", "⛈️"),
        85 or 86 => ("阵雪", "🌨️"),
        95 => ("雷阵雨", "⛈️"),
        96 or 99 => ("雷暴冰雹", "⛈️"),
        _ => ("未知", "❓"),
    };

    /// <summary>European AQI 0–100+ → 等级文字, matching the official bands.</summary>
    public static string AqiText(double? aqi) => aqi switch
    {
        null => "",
        <= 20 => "优",
        <= 40 => "良",
        <= 60 => "中等",
        <= 80 => "较差",
        <= 100 => "很差",
        _ => "极差",
    };

    /// <summary>AQI band colour for the chip.</summary>
    public static string AqiColor(double? aqi) => aqi switch
    {
        null => "#8B93A7",
        <= 20 => "#31C48D",
        <= 40 => "#8BC34A",
        <= 60 => "#F5A524",
        <= 80 => "#F07C22",
        _ => "#F05252",
    };

    /// <summary>
    /// Hero card palette per condition: a sky colour that fades into the dark panel,
    /// plus an accent for the temperature and highlights.
    /// </summary>
    public static (string Top, string Bottom, string Accent) Palette(int code, bool isDay)
    {
        if (!isDay) return ("#CC3A4C86", "#12101E3C", "#AFC4F2");

        return code switch
        {
            0 => ("#D9FFB347", "#1F2E5C9E", "#FFD166"),
            1 or 2 => ("#C98CB6E6", "#21223F6B", "#D6E6FA"),
            3 => ("#C97C8794", "#21222B36", "#D5DCE6"),
            45 or 48 => ("#C9A8B2BE", "#21323C48", "#E2E8EF"),
            51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67
                => ("#C94E86B8", "#211C3554", "#9CCBF7"),
            80 or 81 or 82 => ("#C93E6E9E", "#21192D45", "#8FC0EE"),
            71 or 73 or 75 or 77 or 85 or 86 => ("#D9D5EBF7", "#23506C82", "#F2FAFF"),
            95 or 96 or 99 => ("#C97A5FBF", "#211B1438", "#D3BCFF"),
            _ => ("#C95B6B84", "#21313A48", "#C7D2E3"),
        };
    }
}
