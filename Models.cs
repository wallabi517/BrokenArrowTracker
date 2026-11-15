namespace BrokenArrowSquadTracker;

public sealed record PlayerResult(string Name, string Id, string? SteamId, double? Elo,
    int? GlobalRank, int? Level, int? RatingGames, double? Kd, double? TurtleIndex,
    string? AvatarUrl, DateTimeOffset? UpdatedAt, string Source);

public sealed record MatchPlayer(long Id, string Name, int Team);

public sealed record LobbySnapshot(string? RoomName, IReadOnlyList<string> Players,
    bool HasLobbyMarker = false, bool HasLobbyClosed = false,
    IReadOnlyList<MatchPlayer>? MatchPlayers = null, bool HasInstanceMarker = false,
    bool PlayersAreTrusted = false, IReadOnlyList<MatchPlayer>? IdentifiedPlayers = null)
{
    public bool IsMatch => MatchPlayers is { Count: > 0 };
}
