namespace BrokenArrowSquadTracker;

public sealed record PlayerResult(string Name, string Id, string? SteamId, double? Elo,
    int? GlobalRank, int? Level, int? RatingGames, double? Kd, double? TurtleIndex,
    string? AvatarUrl, DateTimeOffset? UpdatedAt, string Source);

public sealed record LobbySnapshot(string? RoomName, IReadOnlyList<string> Players,
    bool HasLobbyMarker = false, bool HasLobbyClosed = false);
