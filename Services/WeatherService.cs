using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VajraPulse.Services;

public sealed record WeatherSnapshot(
    bool IsAvailable,
    string City,
    double? Temperature,
    bool IsFahrenheit,
    string Condition,
    string Glyph,
    string Detail,
    DateTimeOffset FetchedAt)
{
    public static WeatherSnapshot Unavailable(string city, string detail) =>
        new(false, city, null, false, "", "", detail, DateTimeOffset.MinValue);

    public string DisplayText => IsAvailable
        ? $"{Glyph} {Math.Round(Temperature ?? 0)}°{(IsFahrenheit ? "F" : "C")} · {City}"
        : Detail;

    public string FullDescription => IsAvailable
        ? $"{Condition} in {City}. {Detail}"
        : Detail;
}

public sealed class WeatherService : IDisposable
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(10);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private WeatherSnapshot? _cached;

    public WeatherSnapshot? Cached => _cached;

    public async Task<WeatherSnapshot> FetchAsync(string city, bool useFahrenheit, CancellationToken token)
    {
        city = (city ?? "").Trim();
        if (city.Length == 0)
        {
            _cached = WeatherSnapshot.Unavailable("", "Add a city in Settings");
            return _cached;
        }
        if (_cached != null && _cached.City.Equals(city, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow - _lastAttempt < MinimumInterval)
            return _cached;

        _lastAttempt = DateTimeOffset.UtcNow;
        try
        {
            string url = $"https://wttr.in/{Uri.EscapeDataString(city)}?format=j1";
            using HttpResponseMessage response = await _http.GetAsync(url, token);
            if (!response.IsSuccessStatusCode)
            {
                _cached = WeatherSnapshot.Unavailable(city, "Weather service unavailable");
                return _cached;
            }
            string json = await response.Content.ReadAsStringAsync(token);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement current = document.RootElement.GetProperty("current_condition")[0];
            double tempC = ParseDouble(current, "temp_C");
            double tempF = ParseDouble(current, "temp_F");
            double humidity = ParseDouble(current, "humidity");
            double feelsC = ParseDouble(current, "FeelsLikeC");
            string condition = current.TryGetProperty("weatherDesc", out JsonElement desc)
                && desc.ValueKind == JsonValueKind.Array && desc.GetArrayLength() > 0
                ? desc[0].GetProperty("value").GetString() ?? ""
                : "";
            condition = condition.Trim();
            double temperature = useFahrenheit ? tempF : tempC;
            string glyph = GlyphFor(condition);
            string detail = useFahrenheit
                ? $"{Math.Round(temperature)}°F · feels {Math.Round(feelsC * 9 / 5 + 32)}°F · {Math.Round(humidity)}% humidity"
                : $"{Math.Round(temperature)}°C · feels {Math.Round(feelsC)}°C · {Math.Round(humidity)}% humidity";
            _cached = new WeatherSnapshot(true, city, temperature, useFahrenheit, condition, glyph, detail, DateTimeOffset.Now);
            return _cached;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            _cached = WeatherSnapshot.Unavailable(city, "Weather unavailable");
            return _cached;
        }
    }

    private static double ParseDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && double.TryParse(value.GetString(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;

    private static string GlyphFor(string condition)
    {
        string value = condition.ToLowerInvariant();
        if (value.Contains("thunder")) return "⛈";
        if (value.Contains("snow") || value.Contains("sleet") || value.Contains("blizzard")) return "❄";
        if (value.Contains("rain") || value.Contains("drizzle") || value.Contains("shower")) return "🌧";
        if (value.Contains("fog") || value.Contains("mist") || value.Contains("haze")) return "🌫";
        if (value.Contains("overcast")) return "☁";
        if (value.Contains("cloud") || value.Contains("partly")) return "⛅";
        if (value.Contains("clear") || value.Contains("sunny")) return "☀";
        return "🌤";
    }

    public void Dispose() => _http.Dispose();
}
