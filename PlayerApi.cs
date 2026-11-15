using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BrokenArrowSquadTracker;

public sealed class PlayerApi
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly ConcurrentDictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim requestSlots = new(4, 4);
    public PlayerApi() => http.DefaultRequestHeaders.UserAgent.ParseAdd("ArrowScope-SquadTracker/0.4 (+local desktop tool)");

    public async Task<PlayerResult?> SearchAsync(string name, CancellationToken ct, string? playerId = null)
    {
        var key = string.IsNullOrWhiteSpace(playerId) ? name.Trim() : $"id:{playerId}";
        if (cache.TryGetValue(key, out var cached) && cached.ExpiresUtc > DateTime.UtcNow) return cached.Result;

        await requestSlots.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out cached) && cached.ExpiresUtc > DateTime.UtcNow) return cached.Result;
            var result = await SearchCoreAsync(name.Trim(), playerId, ct);
            DiagnosticLog.Write("BATRACE", result is null ? $"not-found name={name} id={playerId ?? "-"}" : $"matched query={name} id={playerId ?? "-"} name={result.Name} steam={result.SteamId ?? "-"}");
            cache[key] = new CacheEntry(result, DateTime.UtcNow.Add(result is null ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5)));
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            DiagnosticLog.Write("BATRACE", $"error name={key} message={ex.Message}");
            throw;
        }
        finally { requestSlots.Release(); }
    }

    private async Task<PlayerResult?> SearchCoreAsync(string name, string? playerId, CancellationToken ct)
    {
        var query = string.IsNullOrWhiteSpace(playerId) ? name : playerId;
        var url = $"https://dash.batrace.top/api/players/search?q={Uri.EscapeDataString(query)}&limit=8";
        var root = JsonNode.Parse(await http.GetStringAsync(url, ct));
        var players = root?["players"]?.AsArray();
        if (players is null || players.Count == 0) return null;
        var objects = players.OfType<JsonObject>().ToArray();
        var candidate = objects.FirstOrDefault(p =>
            (!string.IsNullOrWhiteSpace(playerId) && string.Equals(Read(p, "id"), playerId, StringComparison.Ordinal)) ||
            (string.IsNullOrWhiteSpace(playerId) && string.Equals(Read(p, "name"), name, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(Read(p, "steam_id"), name, StringComparison.Ordinal));
        if (candidate is null) return null;
        var id = Read(candidate, "id") ?? "-";
        var details = await ReadDetailedStatsAsync(id, ct);
        var steamId = Read(candidate, "steam_id") ?? details.SteamId;
        var avatarUrl = await ReadSteamAvatarAsync(steamId, ct);
        return new(Read(candidate, "name") ?? name, id, steamId,
            details.Elo ?? Number(candidate, "rating"), details.Rank ?? Int(candidate, "rank"),
            details.Level ?? Int(candidate, "level"), details.RatingGames ?? Int(candidate, "rating_games"),
            details.Kd, details.Turtle, avatarUrl, Date(candidate, "updated_at"), "BATrace");
    }

    private async Task<string?> ReadSteamAvatarAsync(string? steamId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(steamId)) return null;
        try
        {
            var xml = await http.GetStringAsync($"https://steamcommunity.com/profiles/{Uri.EscapeDataString(steamId)}?xml=1", ct);
            var match = Regex.Match(xml, "<avatarMedium><!\\[CDATA\\[(?<url>.*?)\\]\\]></avatarMedium>", RegexOptions.Singleline);
            return match.Success ? match.Groups["url"].Value : null;
        }
        catch { return null; }
    }

    private async Task<DetailedStats> ReadDetailedStatsAsync(string id, CancellationToken ct)
    {
        try
        {
            var html = await http.GetStringAsync($"https://dash.batrace.top/player/{Uri.EscapeDataString(id)}", ct);
            var text = html.Replace("\\\"", "\"");
            var turtle = ParseDouble(Regex.Match(text, "\"maggotStats\":\\{\"score\":\"(?<v>-?[0-9.]+)\"").Groups["v"].Value);
            var info = Regex.Match(text, "\"playerData\":\\{\"info\":\\{(?<body>.*?)\\},\"statInfo\"", RegexOptions.Singleline).Groups["body"].Value;
            var scorePattern = $"\"{Regex.Escape(id)}\":\\{{\"Id\":{Regex.Escape(id)},.*?\"DestructionScore\":(?<dealt>[0-9.]+),\"LossesScore\":(?<lost>[0-9.]+)";
            var matches = Regex.Matches(text, scorePattern, RegexOptions.Singleline).Cast<Match>().Take(30).ToArray();
            var dealt = matches.Sum(match => ParseDouble(match.Groups["dealt"].Value) ?? 0);
            var lost = matches.Sum(match => ParseDouble(match.Groups["lost"].Value) ?? 0);
            double? kd = matches.Length == 0 ? null : lost == 0 ? dealt : dealt / lost;
            var currentRating = matches.Select(match => ParseDouble(
                Regex.Match(match.Value, "\\\"NewRating\\\":(?<v>[0-9.]+)").Groups["v"].Value))
                .Where(value => value is not null).LastOrDefault();
            return new DetailedStats(kd, turtle, DetailDouble(info, "rating") ?? currentRating, DetailInt(info, "rank"),
                DetailInt(info, "level"), DetailInt(info, "ratingGames"), DetailString(info, "steamId"));
        }
        catch { return new DetailedStats(null, null, null, null, null, null, null); }
    }

    private static double? DetailDouble(string body, string name) => ParseDouble(
        Regex.Match(body, $"\"{Regex.Escape(name)}\":(?<v>-?[0-9.]+)").Groups["v"].Value);
    private static int? DetailInt(string body, string name) =>
        int.TryParse(Regex.Match(body, $"\"{Regex.Escape(name)}\":(?<v>-?[0-9]+)").Groups["v"].Value,
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static string? DetailString(string body, string name)
    {
        var value = Regex.Match(body, $"\"{Regex.Escape(name)}\":\"(?<v>[^\"]+)\"").Groups["v"].Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static double? ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static string? Read(JsonObject value, string key) =>
        value.TryGetPropertyValue(key, out var node) && node is not null ? node.ToString() : null;
    private static double? Number(JsonObject value, string key) =>
        double.TryParse(Read(value, key), NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : null;
    private static int? Int(JsonObject value, string key) =>
        int.TryParse(Read(value, key), NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : null;
    private static DateTimeOffset? Date(JsonObject value, string key) =>
        DateTimeOffset.TryParse(Read(value, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result) ? result : null;
    private sealed record CacheEntry(PlayerResult? Result, DateTime ExpiresUtc);
    private sealed record DetailedStats(double? Kd, double? Turtle, double? Elo, int? Rank, int? Level,
        int? RatingGames, string? SteamId);
}
