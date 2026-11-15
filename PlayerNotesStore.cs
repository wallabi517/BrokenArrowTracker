using System.Text.Json;

namespace BrokenArrowSquadTracker;

public sealed class PlayerNotesStore
{
    private readonly object sync = new();
    private readonly string path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BrokenArrowSquadTracker", "player-notes.json");
    private Dictionary<string, PlayerNote> notes = new(StringComparer.OrdinalIgnoreCase);

    public PlayerNotesStore() => Load();

    public PlayerNote? Find(PlayerResult player)
    {
        lock (sync) return notes.GetValueOrDefault(Key(player));
    }

    public void Save(PlayerResult player, string reason)
    {
        reason = reason.Trim();
        if (reason.Length == 0) return;
        lock (sync)
        {
            var key = Key(player);
            var created = notes.TryGetValue(key, out var existing) ? existing.CreatedAt : DateTimeOffset.Now;
            notes[key] = new PlayerNote(player.Name, player.SteamId, reason, created, DateTimeOffset.Now);
            Persist();
        }
        DiagnosticLog.Write("NOTE", $"비추천 저장 name={player.Name} steam={player.SteamId ?? "-"}");
    }

    public void Remove(PlayerResult player)
    {
        lock (sync)
        {
            if (!notes.Remove(Key(player))) return;
            Persist();
        }
        DiagnosticLog.Write("NOTE", $"비추천 삭제 name={player.Name} steam={player.SteamId ?? "-"}");
    }

    private static string Key(PlayerResult player) => !string.IsNullOrWhiteSpace(player.SteamId)
        ? "steam:" + player.SteamId
        : "name:" + player.Name.Trim().ToUpperInvariant();

    private void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, PlayerNote>>(File.ReadAllText(path));
            if (loaded is not null) notes = new Dictionary<string, PlayerNote>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { DiagnosticLog.Write("NOTE", "비추천 기록 읽기 실패: " + ex.Message); }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(notes, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}

public sealed record PlayerNote(string Name, string? SteamId, string Reason,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
