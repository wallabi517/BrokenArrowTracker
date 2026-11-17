using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace BrokenArrowSquadTracker;

public sealed class PacketCaptureService : IDisposable
{
    // Broken Arrow uses separate lobby, waiting-room/game and relay hosts. Keep
    // these as CIDR filters so direct and proxy routes are captured without
    // recording every UDP packet on the machine. The list is based on endpoints
    // observed in the game's own Network lifecycle/Adaptive routing logs.
    private static readonly string[] NetworkCidrs =
    [
        "38.180.0.0/15",
        "38.244.0.0/16",
        "92.63.224.0/24",
        "185.132.125.0/24",
        "5.129.202.0/24",
        "37.9.33.0/24",
        "74.50.99.0/24",
        "79.174.13.0/24",
        "80.74.26.0/24",
        "104.129.171.0/24",
        "147.45.164.0/24",
        "155.212.168.0/24",
        "195.140.147.0/24"
    ];
    private static readonly int[] DirectServerPorts = Enumerable.Range(30201, 17).ToArray();

    private readonly CancellationTokenSource stop = new();
    private readonly string workDir = Path.Combine(Path.GetTempPath(), "BrokenArrowTracker");
    private readonly string? localPlayerName;
    private readonly HashSet<string> currentPlayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> pendingPlayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> trustedLobbyPlayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, MatchPlayer> currentMatchPlayers = new();
    private readonly Dictionary<long, string> waitingRoomPlayers = new();
    private readonly Dictionary<long, string> preMatchPartyPlayers = new();
    private readonly HashSet<string> currentCandidateMaps = new(StringComparer.OrdinalIgnoreCase);
    private Task? worker;
    private string? currentRoomName;
    private DateTime lastLobbyPacketUtc = DateTime.MinValue;
    private DateTime lastLobbyMarkerUtc = DateTime.MinValue;
    private DateTime lastPendingPlayersUtc = DateTime.MinValue;
    private DateTime? pendingLobbyExitUtc;
    private string? currentLobbyId;
    private bool inMatch;
    private bool currentRosterTrusted;
    private readonly string? gameLogsDirectory;
    private string? monitoredGameLog;
    private long monitoredGameLogPosition;
    public event Action<string>? StatusChanged;
    public event Action<LobbySnapshot>? LobbyChanged;
    public bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public void SetVerifiedLobbyPlayers(IEnumerable<string> names)
    {
        foreach (var name in names.Where(IsUsableLogPlayerName)) trustedLobbyPlayers.TryAdd(name, 0);
    }

    public PacketCaptureService(string? steamPath = null)
    {
        localPlayerName = SteamIdentity.TryGetPersonaName(steamPath);
        gameLogsDirectory = FindGameLogsDirectory(steamPath);
    }

    public void Start()
    {
        if (worker is not null) return;
        worker = Task.Run(() => CaptureLoopAsync(stop.Token));
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        DiagnosticLog.Write("CAPTURE", IsAdministrator ? "패킷 캡처 시작" : "관리자 권한 없음");
        if (!IsAdministrator) { StatusChanged?.Invoke("관리자 권한이 필요합니다"); return; }
        Directory.CreateDirectory(workDir);
        InitializeGameLogMonitor();
        await RunAsync("pktmon", "stop", ct, true);
        await ConfigureFiltersAsync(ct);

        var slot = 0;
        var activeEtl = CapturePath(slot, "etl");
        TryDelete(activeEtl);
        if (!await StartCaptureWithRetryAsync(activeEtl, ct)) return;

        while (!ct.IsCancellationRequested)
        {
            // pktmon is a single global capture session. Every rotation creates
            // a short stop/start gap, so rotating too often can miss the one-time
            // full roster packet sent on lobby entry.
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { break; }

            await RunAsync("pktmon", "stop", CancellationToken.None, true);

            var completedEtl = activeEtl;
            slot = 1 - slot;
            activeEtl = CapturePath(slot, "etl");
            TryDelete(activeEtl);
            if (!await StartCaptureWithRetryAsync(activeEtl, ct)) break;

            var pcap = CapturePath(slot == 0 ? 1 : 0, "pcapng");
            TryDelete(pcap);
            try
            {
                var converted = await RunAsync("pktmon", $"etl2pcap \"{completedEtl}\" --out \"{pcap}\"", ct, true);
                if (converted == 0 && File.Exists(pcap)) ProcessCapture(pcap);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("CAPTURE", "패킷 분석 실패: " + ex.Message);
                StatusChanged?.Invoke("패킷 분석 복구 중: " + ex.Message);
            }
            ProcessGameLogSignals();
        }
        await RunAsync("pktmon", "stop", CancellationToken.None, true);
    }

    private string CapturePath(int slot, string extension) => Path.Combine(workDir, $"lobby-{slot}.{extension}");

    private async Task ConfigureFiltersAsync(CancellationToken ct)
    {
        await RunAsync("pktmon", "filter remove", ct, true);
        var added = 0;
        for (var index = 0; index < NetworkCidrs.Length; index++)
        {
            var code = await RunAsync("pktmon", $"filter add BrokenArrowNet{index + 1} -t UDP -i {NetworkCidrs[index]}", ct, true);
            if (code == 0) added++;
            else DiagnosticLog.Write("CAPTURE", $"필터 등록 실패 cidr={NetworkCidrs[index]} exit={code}");
        }

        for (var index = 0; index < DirectServerPorts.Length; index++)
        {
            var port = DirectServerPorts[index];
            var code = await RunAsync("pktmon", $"filter add BrokenArrowPort{index + 1} -t UDP -p {port}", ct, true);
            if (code == 0) added++;
            else DiagnosticLog.Write("CAPTURE", $"필터 등록 실패 port={port} exit={code}");
        }

        var expected = NetworkCidrs.Length + DirectServerPorts.Length;
        DiagnosticLog.Write("CAPTURE", $"UDP 필터 등록 {added}/{expected}; cidr=[{string.Join(", ", NetworkCidrs)}]; ports=30201-30217");
        StatusChanged?.Invoke(added == expected
            ? "Broken Arrow 로비/게임 서버 감시 중"
            : $"패킷 필터 일부 등록됨 ({added}/{expected})");
    }

    private async Task<bool> StartCaptureWithRetryAsync(string etl, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            StatusChanged?.Invoke("Broken Arrow 패킷 수신 중");
            var code = await RunAsync("pktmon", $"start --capture --pkt-size 0 --file-name \"{etl}\"", ct, true);
            if (code == 0) return true;
            StatusChanged?.Invoke("패킷 캡처 재시도 중");
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return false; }
            await RunAsync("pktmon", "stop", CancellationToken.None, true);
            TryDelete(etl);
        }
        return false;
    }

    private void ProcessCapture(string pcap)
    {
        var lobby = PcapLobbyParser.FindLobby(pcap);
        var now = DateTime.UtcNow;
        if (lobby.CandidateMaps is { Count: > 0 })
        {
            foreach (var map in lobby.CandidateMaps) currentCandidateMaps.Add(map);
            DiagnosticLog.Write("MAP", $"패킷 맵 후보=[{string.Join(" | ", currentCandidateMaps)}]");
        }
        IReadOnlyList<MatchPlayer> structuredPlayers = lobby.MatchPlayers ?? Array.Empty<MatchPlayer>();
        if (lobby.MatchPlayers is { Count: > 0 })
        {
            if (preMatchPartyPlayers.Count > 0)
                foreach (var partyPlayer in preMatchPartyPlayers)
                    currentMatchPlayers[partyPlayer.Key] = new MatchPlayer(partyPlayer.Key, partyPlayer.Value, 1);
            foreach (var player in lobby.MatchPlayers)
            {
                var normalizedPlayer = preMatchPartyPlayers.Count == 0 ? player :
                    player with { Team = IsPreMatchPartyPlayer(player.Id, player.Name) ? 1 : 2 };
                currentMatchPlayers[player.Id] = normalizedPlayer;
            }
            var hasBothTeams = currentMatchPlayers.Values.Any(player => player.Team == 1) &&
                               currentMatchPlayers.Values.Any(player => player.Team == 2);
            if (inMatch || hasBothTeams)
            {
                if (!inMatch)
                {
                    currentPlayers.Clear();
                    pendingPlayers.Clear();
                    currentRoomName = null;
                }
                inMatch = true;
                if (waitingRoomPlayers.Count > 0) CompleteMatchRosterFromWaitingRoom();
                var roster = currentMatchPlayers.Values.OrderBy(player => player.Team).ThenBy(player => player.Name).ToArray();
                DiagnosticLog.Write("MATCH", $"players={roster.Length} roster=[{string.Join(" | ", roster.Select(player => $"{player.Name}:{player.Id}:T{player.Team}"))}]");
                LobbyChanged?.Invoke(new LobbySnapshot(null, roster.Select(player => player.Name).ToArray(),
                    MatchPlayers: roster, CandidateMaps: currentCandidateMaps.ToArray()));
                return;
            }
            DiagnosticLog.Write("LOBBY", $"single-team structured players=[{string.Join(" | ", structuredPlayers.Select(player => $"{player.Name}:{player.Id}:T{player.Team}"))}]");
        }
        // Close-channel packets are also emitted when auxiliary channels rotate.
        // Prefer the game's explicit lobby lifecycle log whenever it is available.
        if (lobby.HasLobbyClosed && !inMatch && gameLogsDirectory is null)
        {
            DiagnosticLog.Write("LOBBY", "종료 패킷 감지");
            currentPlayers.Clear();
            pendingPlayers.Clear();
            currentMatchPlayers.Clear();
            inMatch = false;
            currentRoomName = null;
            LobbyChanged?.Invoke(new LobbySnapshot(null, Array.Empty<string>()));
            return;
        }
        if (lobby.HasLobbyMarker && inMatch)
        {
            currentMatchPlayers.Clear();
            inMatch = false;
        }
        // The server sends the roster beside "Join Instance channel" on entry.
        // A literal "Lobby" marker can arrive much later (often while leaving),
        // so waiting for it makes the table appear at exactly the wrong time.
        var hasLobbySessionMarker = lobby.HasLobbyMarker || (lobby.HasInstanceMarker && !inMatch);
        if (hasLobbySessionMarker)
        {
            lastLobbyMarkerUtc = now;
            pendingLobbyExitUtc = null;
            if (!inMatch && structuredPlayers.Count > 0)
            {
                currentMatchPlayers.Clear();
                foreach (var player in structuredPlayers) currentMatchPlayers[player.Id] = player;
                trustedLobbyPlayers.Clear();
                currentRosterTrusted = false;
            }
        }
        var hasStructuredRoster = structuredPlayers.Count > 0;
        var names = (hasStructuredRoster
                ? structuredPlayers.Select(player => player.Name)
                : currentRosterTrusted ? Array.Empty<string>() : lobby.Players)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (hasStructuredRoster)
        {
            pendingPlayers.Clear();
            currentRosterTrusted = true;
            foreach (var name in names) trustedLobbyPlayers.TryAdd(name, 0);
        }
        if (hasLobbySessionMarker && now - lastPendingPlayersUtc <= TimeSpan.FromSeconds(6))
        {
            names.AddRange(pendingPlayers);
            pendingPlayers.Clear();
        }
        var belongsToLobby = hasLobbySessionMarker || currentPlayers.Count > 0 ||
                             now - lastLobbyMarkerUtc <= TimeSpan.FromSeconds(6);
        if (names.Count > 0 && !belongsToLobby)
        {
            if (now - lastPendingPlayersUtc > TimeSpan.FromSeconds(6)) pendingPlayers.Clear();
            foreach (var name in names) pendingPlayers.Add(name);
            lastPendingPlayersUtc = now;
        }
        if (names.Count > 0 && belongsToLobby)
        {
            if (!string.IsNullOrWhiteSpace(lobby.RoomName) &&
                !string.IsNullOrWhiteSpace(currentRoomName) &&
                !string.Equals(lobby.RoomName, currentRoomName, StringComparison.OrdinalIgnoreCase))
                currentPlayers.Clear();

            if (hasLobbySessionMarker)
            {
                currentPlayers.Clear();
                currentRoomName = lobby.RoomName;
            }

            if (!string.IsNullOrWhiteSpace(lobby.RoomName)) currentRoomName = lobby.RoomName;
            foreach (var name in names) currentPlayers.Add(name);
            if (!string.IsNullOrWhiteSpace(localPlayerName) && !names.Contains(localPlayerName, StringComparer.OrdinalIgnoreCase))
                currentPlayers.Add(localPlayerName);
            lastLobbyPacketUtc = now;
            var visiblePlayers = string.IsNullOrWhiteSpace(localPlayerName)
                ? currentPlayers.Take(64).ToArray()
                : currentPlayers.Where(name => !name.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase))
                    .Take(63).Prepend(localPlayerName).ToArray();
            DiagnosticLog.Write("PACKET", $"room={currentRoomName ?? "-"} marker={lobby.HasLobbyMarker} instance={lobby.HasInstanceMarker} candidates=[{string.Join(" | ", names)}] visible=[{string.Join(" | ", visiblePlayers)}]");
            LobbyChanged?.Invoke(new LobbySnapshot(currentRoomName, visiblePlayers, hasLobbySessionMarker,
                PlayersAreTrusted: currentRosterTrusted,
                IdentifiedPlayers: currentMatchPlayers.Values.ToArray(), CandidateMaps: currentCandidateMaps.ToArray()));
        }
        else if (gameLogsDirectory is null && !inMatch && currentPlayers.Count > 0 &&
                 DateTime.UtcNow - lastLobbyPacketUtc >= TimeSpan.FromMinutes(2))
        {
            currentPlayers.Clear();
            currentRoomName = null;
            LobbyChanged?.Invoke(new LobbySnapshot(null, Array.Empty<string>()));
        }
    }

    private void InitializeGameLogMonitor()
    {
        var latest = GetLatestGameLog();
        if (latest is null) return;
        monitoredGameLog = latest.FullName;
        monitoredGameLogPosition = latest.Length;
        foreach (var line in File.ReadLines(latest.FullName).TakeLast(300))
        {
            var lifecycle = Regex.Match(line,
                @"Stage=Lobby\s*\|\s*Action=(?<action>Entered|Exited).*?LobbyId=(?<id>\d+)",
                RegexOptions.IgnoreCase);
            if (!lifecycle.Success) continue;
            currentLobbyId = lifecycle.Groups["action"].Value.Equals("Entered", StringComparison.OrdinalIgnoreCase)
                ? lifecycle.Groups["id"].Value
                : null;
        }
    }

    private void ProcessGameLogSignals()
    {
        try
        {
            var latest = GetLatestGameLog();
            if (latest is null) return;
            if (!string.Equals(monitoredGameLog, latest.FullName, StringComparison.OrdinalIgnoreCase))
            {
                monitoredGameLog = latest.FullName;
                monitoredGameLogPosition = 0;
            }
            if (latest.Length < monitoredGameLogPosition) monitoredGameLogPosition = 0;
            if (latest.Length == monitoredGameLogPosition)
            {
                FinalizePendingLobbyExit();
                return;
            }

            using var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Position = monitoredGameLogPosition;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096, leaveOpen: true);
            var shouldEndMatch = false;
            var shouldEndLobby = false;
            var sawLobbyEntered = false;
            var joinedPlayers = new List<string>();
            var leftPlayers = new List<string>();
            var leftPlayerIds = new List<long>();
            var rosterChanges = new List<(bool Joined, string Name)>();
            var sawWaitingRoomPlayer = false;
            var sawGameRoomEntered = false;
            var sawGameRoomRoster = false;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var gameRoomPlayer = Regex.Match(line,
                    @"^ID:\s*(?<id>\d+),\s*Name:\s*(?<name>.+),\s*Team:\s*(?<team>Alpha|Bravo)\s*$",
                    RegexOptions.IgnoreCase);
                if (gameRoomPlayer.Success &&
                    long.TryParse(gameRoomPlayer.Groups["id"].Value, out var gameRoomPlayerId))
                {
                    var name = gameRoomPlayer.Groups["name"].Value.Trim();
                    var packetTeam = gameRoomPlayer.Groups["team"].Value.Equals("Alpha", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                    var team = preMatchPartyPlayers.Count > 0
                        ? (IsPreMatchPartyPlayer(gameRoomPlayerId, name) ? 1 : 2)
                        : packetTeam;
                    currentMatchPlayers[gameRoomPlayerId] = new MatchPlayer(gameRoomPlayerId, name, team);
                    waitingRoomPlayers[gameRoomPlayerId] = name;
                    sawGameRoomRoster = true;
                }
                if (line.Contains("Stage=Game", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("Action=Entered", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("RoomType=GameRoom", StringComparison.OrdinalIgnoreCase))
                {
                    currentCandidateMaps.Clear();
                    sawGameRoomEntered = true;
                    DiagnosticLog.Write("MAP", "게임 시작 - 맵 선택 패널 종료");
                }
                var loadingMap = Regex.Match(line, @"Start loading battle\.\.\. map:\s*(?<map>.+?),\s*scenario:",
                    RegexOptions.IgnoreCase);
                if (loadingMap.Success)
                {
                    var detected = MapWinRateCatalog.FindInText(loadingMap.Groups["map"].Value);
                    if (detected.Count > 0)
                    {
                        currentCandidateMaps.Clear();
                        foreach (var map in detected) currentCandidateMaps.Add(map);
                        DiagnosticLog.Write("MAP", $"최종 맵=[{string.Join(" | ", currentCandidateMaps)}]");
                    }
                }
                var waitingRoomPlayer = Regex.Match(line,
                    @"Room: \(WaitingRoom\|\d+\), Client: \((?<name>.+)\|(?<id>\d+)\)",
                    RegexOptions.IgnoreCase);
                if (waitingRoomPlayer.Success &&
                    long.TryParse(waitingRoomPlayer.Groups["id"].Value, out var waitingRoomPlayerId))
                {
                    waitingRoomPlayers[waitingRoomPlayerId] = waitingRoomPlayer.Groups["name"].Value.Trim();
                    sawWaitingRoomPlayer = true;
                }
                var lifecycle = Regex.Match(line,
                    @"Stage=Lobby\s*\|\s*Action=(?<action>Entered|Exited).*?LobbyId=(?<id>\d+)",
                    RegexOptions.IgnoreCase);
                if (lifecycle.Success)
                {
                    var eventLobbyId = lifecycle.Groups["id"].Value;
                    if (lifecycle.Groups["action"].Value.Equals("Entered", StringComparison.OrdinalIgnoreCase))
                    {
                        preMatchPartyPlayers.Clear();
                        if (!string.Equals(currentLobbyId, eventLobbyId, StringComparison.Ordinal))
                        {
                            // A new lobby can follow an Exited event whose LobbyId is 0.
                            // Nothing from the previous lobby is valid in the new room;
                            // its structured roster will arrive again after Entered.
                            trustedLobbyPlayers.Clear();
                            currentPlayers.Clear();
                            pendingPlayers.Clear();
                            currentMatchPlayers.Clear();
                            waitingRoomPlayers.Clear();
                            currentCandidateMaps.Clear();
                            currentRoomName = null;
                            currentRosterTrusted = false;
                            DiagnosticLog.Write("LOBBY", $"새 로비 전환 old={currentLobbyId ?? "-"} new={eventLobbyId}");
                        }
                        currentLobbyId = eventLobbyId;
                        pendingLobbyExitUtc = null;
                        sawLobbyEntered = true;
                    }
                    else if (string.Equals(currentLobbyId, eventLobbyId, StringComparison.Ordinal))
                    {
                        if (line.Contains("Status=Searching", StringComparison.OrdinalIgnoreCase))
                            CapturePreMatchParty();
                        shouldEndLobby = true;
                    }
                }
                var joined = Regex.Match(line, @"Incoming client (?<name>.+?):(?<playerId>\d+) to lobby \(id: (?<lobbyId>\d+)", RegexOptions.IgnoreCase);
                if (joined.Success && (currentLobbyId is null || joined.Groups["lobbyId"].Value == currentLobbyId))
                {
                    var name = joined.Groups["name"].Value.Trim();
                    joinedPlayers.Add(name);
                    if (long.TryParse(joined.Groups["playerId"].Value, out var playerId))
                        currentMatchPlayers[playerId] = new MatchPlayer(playerId, name, 0);
                    rosterChanges.Add((true, name));
                }
                var left = Regex.Match(line, @"Outgoing client (?<name>.+?):(?<playerId>\d+) exit from lobby \(id: (?<lobbyId>\d+)", RegexOptions.IgnoreCase);
                if (left.Success && (currentLobbyId is null || left.Groups["lobbyId"].Value == currentLobbyId))
                {
                    var name = left.Groups["name"].Value.Trim();
                    leftPlayers.Add(name);
                    if (long.TryParse(left.Groups["playerId"].Value, out var playerId)) leftPlayerIds.Add(playerId);
                    rosterChanges.Add((false, name));
                }
                if (line.Contains("Cause=MatchEnded", StringComparison.OrdinalIgnoreCase) ||
                    (line.Contains("Stage=Game", StringComparison.OrdinalIgnoreCase) &&
                     line.Contains("Action=Exited", StringComparison.OrdinalIgnoreCase) &&
                     line.Contains("Cause=ServiceDisposed", StringComparison.OrdinalIgnoreCase)))
                    shouldEndMatch = true;
                else if (inMatch && line.Contains("Stage=Lobby", StringComparison.OrdinalIgnoreCase) &&
                         line.Contains("Action=Entered", StringComparison.OrdinalIgnoreCase))
                    shouldEndMatch = true;
            }
            monitoredGameLogPosition = stream.Position;
            if (sawWaitingRoomPlayer && inMatch) CompleteMatchRosterFromWaitingRoom();
            if (sawGameRoomRoster && currentMatchPlayers.Count > 0)
            {
                inMatch = true;
                EmitCompletedMatchRoster("GameRoom 로그 전체 명단 보완");
            }
            if (sawGameRoomEntered && inMatch && currentMatchPlayers.Count > 0)
                EmitCompletedMatchRoster("게임 시작 화면 전환");
            if (sawLobbyEntered) pendingLobbyExitUtc = null;
            else if (shouldEndLobby && !inMatch) pendingLobbyExitUtc = DateTime.UtcNow;
            FinalizePendingLobbyExit();
            if (!inMatch && (joinedPlayers.Count > 0 || leftPlayers.Count > 0))
            {
                foreach (var change in rosterChanges)
                {
                    if (change.Joined && IsUsableLogPlayerName(change.Name)) trustedLobbyPlayers.TryAdd(change.Name, 0);
                    else if (!change.Joined) trustedLobbyPlayers.TryRemove(change.Name, out _);
                }
                foreach (var playerId in leftPlayerIds) currentMatchPlayers.Remove(playerId);
                currentRosterTrusted = true;
                currentPlayers.Clear();
                foreach (var name in trustedLobbyPlayers.Keys) currentPlayers.Add(name);
                if (!string.IsNullOrWhiteSpace(localPlayerName)) currentPlayers.Add(localPlayerName);
                lastLobbyPacketUtc = DateTime.UtcNow;
                var visiblePlayers = string.IsNullOrWhiteSpace(localPlayerName)
                    ? currentPlayers.Take(64).ToArray()
                    : currentPlayers.Where(name => !name.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase))
                        .Take(63).Prepend(localPlayerName).ToArray();
                DiagnosticLog.Write("LOBBY", $"로그 명단 갱신 joined=[{string.Join(" | ", joinedPlayers)}] left=[{string.Join(" | ", leftPlayers)}]");
                LobbyChanged?.Invoke(new LobbySnapshot(currentRoomName, visiblePlayers, PlayersAreTrusted: true,
                    IdentifiedPlayers: currentMatchPlayers.Values.ToArray(), CandidateMaps: currentCandidateMaps.ToArray()));
            }
            if (shouldEndMatch && inMatch) EndMatch("게임 종료 로그 감지");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("CAPTURE", "게임 로그 감시 실패: " + ex.Message);
        }
    }

    private FileInfo? GetLatestGameLog() => gameLogsDirectory is null ? null :
        new DirectoryInfo(gameLogsDirectory).EnumerateFiles("Gamelog__*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();

    private static bool IsUsableLogPlayerName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64 &&
        !name.Equals("Bot", StringComparison.OrdinalIgnoreCase);

    private void CompleteMatchRosterFromWaitingRoom()
    {
        var missing = waitingRoomPlayers.Where(entry => !currentMatchPlayers.ContainsKey(entry.Key)).ToArray();
        if (missing.Length == 0) return;
        if (preMatchPartyPlayers.Count > 0)
        {
            foreach (var entry in missing)
                currentMatchPlayers[entry.Key] = new MatchPlayer(entry.Key, entry.Value,
                    IsPreMatchPartyPlayer(entry.Key, entry.Value) ? 1 : 2);
            EmitCompletedMatchRoster($"사전 로비 기준 보완 count={missing.Length}");
            return;
        }
        if (missing.Length != 1) return;
        var team1Count = currentMatchPlayers.Values.Count(player => player.Team == 1);
        var team2Count = currentMatchPlayers.Values.Count(player => player.Team == 2);
        var missingTeam = team1Count == 4 && team2Count == 5 ? 1 :
            team2Count == 4 && team1Count == 5 ? 2 : 0;
        if (missingTeam == 0) return;

        var player = new MatchPlayer(missing[0].Key, missing[0].Value, missingTeam);
        currentMatchPlayers[player.Id] = player;
        EmitCompletedMatchRoster($"WaitingRoom 보완 player={player.Name}:{player.Id}:T{player.Team}");
    }

    private void CapturePreMatchParty()
    {
        preMatchPartyPlayers.Clear();
        foreach (var player in currentMatchPlayers.Values)
            if (currentPlayers.Contains(player.Name) || trustedLobbyPlayers.ContainsKey(player.Name))
                preMatchPartyPlayers[player.Id] = player.Name;
        DiagnosticLog.Write("MATCH", $"사전 로비 아군 저장 count={preMatchPartyPlayers.Count} players=[{string.Join(" | ", preMatchPartyPlayers.Select(item => $"{item.Value}:{item.Key}"))}]");
    }

    private bool IsPreMatchPartyPlayer(long id, string name) =>
        preMatchPartyPlayers.ContainsKey(id) ||
        preMatchPartyPlayers.Values.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(localPlayerName) && name.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase));

    private void EmitCompletedMatchRoster(string reason)
    {
        var roster = currentMatchPlayers.Values.OrderBy(item => item.Team).ThenBy(item => item.Name).ToArray();
        DiagnosticLog.Write("MATCH", $"{reason} total={roster.Length}");
        LobbyChanged?.Invoke(new LobbySnapshot(null, roster.Select(item => item.Name).ToArray(), MatchPlayers: roster));
    }

    private void FinalizePendingLobbyExit()
    {
        if (inMatch || pendingLobbyExitUtc is null ||
            DateTime.UtcNow - pendingLobbyExitUtc.Value < TimeSpan.FromSeconds(3)) return;
        pendingLobbyExitUtc = null;
        EndLobby("게임 로그 로비 퇴장 확정");
    }

    private void EndLobby(string reason)
    {
        DiagnosticLog.Write("LOBBY", reason);
        currentPlayers.Clear();
        pendingPlayers.Clear();
        trustedLobbyPlayers.Clear();
        currentMatchPlayers.Clear();
        waitingRoomPlayers.Clear();
        currentCandidateMaps.Clear();
        currentRoomName = null;
        currentRosterTrusted = false;
        currentLobbyId = null;
        pendingLobbyExitUtc = null;
        lastLobbyMarkerUtc = DateTime.MinValue;
        lastLobbyPacketUtc = DateTime.MinValue;
        LobbyChanged?.Invoke(new LobbySnapshot(null, Array.Empty<string>()));
    }

    private void EndMatch(string reason)
    {
        DiagnosticLog.Write("MATCH", reason);
        currentPlayers.Clear();
        pendingPlayers.Clear();
        trustedLobbyPlayers.Clear();
        currentMatchPlayers.Clear();
        waitingRoomPlayers.Clear();
        preMatchPartyPlayers.Clear();
        currentCandidateMaps.Clear();
        currentRoomName = null;
        currentRosterTrusted = false;
        inMatch = false;
        LobbyChanged?.Invoke(new LobbySnapshot(null, Array.Empty<string>()));
    }

    private static string? FindGameLogsDirectory(string? steamPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath)) return null;
        var path = Path.Combine(steamPath, "steamapps", "common", "broken_arrow", "GameLogs");
        return Directory.Exists(path) ? path : null;
    }

    private static async Task<int> RunAsync(string file, string args, CancellationToken ct, bool ignoreError = false)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden });
            if (process is null) return -1;
            await process.WaitForExitAsync(ct); return process.ExitCode;
        }
        catch when (ignoreError) { return -1; }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    public void Dispose() { stop.Cancel(); try { worker?.Wait(1500); } catch { } stop.Dispose(); }
}

internal static class SteamIdentity
{
    public static string? TryGetPersonaName(string? steamPath = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(steamPath)) return null;
            var steam = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(steam)) return null;
            var text = File.ReadAllText(steam);
            string? fallback = null;
            foreach (Match account in Regex.Matches(text, "\"[0-9]+\"\\s*\\{(?<body>.*?)\\n\\s*\\}", RegexOptions.Singleline))
            {
                var body = account.Groups["body"].Value;
                var name = Regex.Match(body, "\"PersonaName\"\\s*\"(?<name>[^\"]+)\"").Groups["name"].Value;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (Regex.IsMatch(body, "\"MostRecent\"\\s*\"1\"") || Regex.IsMatch(body, "\"AutoLogin\"\\s*\"1\"")) return name;
                fallback ??= name;
            }
            return fallback;
        }
        catch { }
        return null;
    }
}

public static class PcapLobbyParser
{
    private static readonly string[] Excluded = ["lobby", "server", "join instance", "channel", "refinery", "pve", "map", "game"];
    // Stable protocol fragments observed across unrelated rooms. These are not
    // player names and must be rejected before they enter the persistent roster.
    private static readonly HashSet<string> KnownNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "_bS", "_Ua", "_uJ", "_?", "?e", "?B", "?F", "?H", "?N",
        "(js", "(Ou", ")(j", ")(x", ")QL4", ")?", "[9I", "[a)k", "[DHDH",
        "].h", "]?B", "]O[", "]u]", "0]LzLS", "0ux", "1fQ", "1h]", "1Vb", "1ZH",
        "18z0a0", "2d_", "2Ejq3", "2y9", "3(F", "31R", "3GQ", "3hw", "3oj", "3q(_",
        "4ug", "4?", "5-Y", "5Rs", "6hBk", "7GeLl", "8.]s", "8dJ", "9O3", "9Wb",
        "9zw", "9?", "A2m2", "AN?", "Ar)", "aZB(s", "b.o", "bZn", "b?h", "c0k",
        "eacX", "Eama", "ehr", "eYw", "Fokk", "GAn)", "gLA4", "h_vA", "jAWp",
        "Mpb?", "mRbmP", "nxg(", "o?ht", "P1Rjr", "TH.z", "u RyI", "u9Eo",
        "Uns?VX", "vGE9k", "VmDw", "WUx]", "yZKD.P", "Bql", "Dk0", "F2S",
        "h5o", "hn2", "IaQ", "JAR", "jU]", "lbi", "MBo", "Diagnostics complete",
        "Mpb?", "o?ht", "Uns?VX", "y7BYkzSu", "UJNo", "G61", "GM7", "M.P2"
    };

    public static IReadOnlyList<string> FindPlayerNames(string path) => FindLobby(path).Players;

    public static LobbySnapshot FindLobby(string path)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var matchPlayers = new Dictionary<long, MatchPlayer>();
        var roomTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? roomName = null;
        var sawLobby = false;
        var sawInstance = false;
        long eventOrder = 0, lastLobbyOrder = -1, lastCloseOrder = -1;
        using var fs = File.OpenRead(path); using var br = new BinaryReader(fs);
        var interfaces = new Dictionary<uint, ushort>();
        while (fs.Position + 12 <= fs.Length)
        {
            var head = br.ReadBytes(8); if (head.Length < 8) break;
            var type = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0, 4));
            var length = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4, 4));
            if (length < 12 || fs.Position - 8 + length > fs.Length) break;
            var body = br.ReadBytes(checked((int)length - 12)); br.ReadUInt32();
            if (type == 1 && body.Length >= 8) { interfaces[(uint)interfaces.Count] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(0, 2)); continue; }
            if (type != 6 || body.Length < 20) continue;
            var iface = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4));
            var capLen = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12, 4)));
            if (capLen <= 0 || 20 + capLen > body.Length) continue;
            var frame = body.AsSpan(20, capLen); var link = interfaces.GetValueOrDefault(iface, (ushort)1);
            var ip = link == 1 ? 14 : 0;
            if (link == 1 && (frame.Length < 14 || BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2)) != 0x0800)) continue;
            if (frame.Length < ip + 20 || (frame[ip] >> 4) != 4) continue;
            var ihl = (frame[ip] & 0x0f) * 4; var l4 = ip + ihl; var proto = frame[ip + 9]; int srcPort, dstPort, data;
            if (proto == 17 && frame.Length >= l4 + 8) { srcPort = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(l4, 2)); dstPort = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(l4 + 2, 2)); data = l4 + 8; }
            else if (proto == 6 && frame.Length >= l4 + 20) { srcPort = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(l4, 2)); dstPort = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(l4 + 2, 2)); data = l4 + (frame[l4 + 12] >> 4) * 4; }
            else continue;
            if (proto != 17 || data >= frame.Length) continue;
            var strings = ExtractStrings(frame[data..].ToArray()).ToArray();
            foreach (var value in strings)
            {
                foreach (var map in MapWinRateCatalog.FindInText(value)) candidateMaps.Add(map);
                eventOrder++;
                if (value.Contains("Lobby", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("Join Instance channel", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.Contains("Lobby", StringComparison.OrdinalIgnoreCase)) sawLobby = true;
                    if (value.StartsWith("Join Instance channel", StringComparison.OrdinalIgnoreCase)) sawInstance = true;
                    lastLobbyOrder = eventOrder;
                }
                else if (value.StartsWith("Close channel", StringComparison.OrdinalIgnoreCase))
                    lastCloseOrder = eventOrder;
            }
            foreach (var player in ExtractMatchPlayers(frame[data..])) matchPlayers[player.Id] = player;
            foreach (var map in ExtractCandidateMaps(frame[data..])) candidateMaps.Add(map);
            var lobbyIndex = Array.FindIndex(strings, x => x.Equals("Lobby", StringComparison.OrdinalIgnoreCase));
            if (lobbyIndex >= 0)
            {
                foreach (var rawTitle in strings.Skip(lobbyIndex + 1).Take(3))
                {
                    var roomTitle = CleanRoomTitle(rawTitle);
                    if (LooksLikeRoomTitle(roomTitle))
                    {
                        var normalizedTitle = Normalize(roomTitle);
                        if (roomTitle.EndsWith("'s lobby", StringComparison.OrdinalIgnoreCase))
                            counts[normalizedTitle] = counts.GetValueOrDefault(normalizedTitle) + 1;
                        else
                            roomTitles.Add(normalizedTitle);
                        roomName ??= roomTitle;
                    }
                }
            }
            foreach (var raw in strings)
            {
                var value = Normalize(raw);
                if (!roomTitles.Contains(value) && IsCandidate(value)) counts[value] = counts.GetValueOrDefault(value) + 1;
            }
        }
        var players = counts.Where(x => !roomTitles.Contains(x.Key))
            .OrderByDescending(x => LooksLikeReadableName(x.Key))
            .ThenByDescending(x => x.Value)
            .ThenBy(x => x.Key).Take(64).Select(x => x.Key).ToArray();
        return new LobbySnapshot(roomName, players, sawLobby,
            matchPlayers.Count == 0 && lastCloseOrder > lastLobbyOrder,
            matchPlayers.Values.ToArray(), sawInstance, CandidateMaps: candidateMaps.ToArray());
    }

    private static IReadOnlyList<MatchPlayer> ExtractMatchPlayers(ReadOnlySpan<byte> payload)
    {
        var players = new List<MatchPlayer>();
        for (var offset = 0; offset < payload.Length - 8; offset++)
        {
            if (payload[offset] != 0x08 || !TryReadVarint(payload, offset + 1, out var rawId, out var cursor)) continue;
            if (rawId is 0 or > 10_000_000 || cursor >= payload.Length || payload[cursor] != 0x12) continue;
            if (!TryReadVarint(payload, cursor + 1, out var rawLength, out var nameStart)) continue;
            if (rawLength is < 2 or > 64 || rawLength > int.MaxValue || nameStart + (int)rawLength > payload.Length) continue;

            string name;
            try { name = new UTF8Encoding(false, true).GetString(payload.Slice(nameStart, (int)rawLength)); }
            catch (DecoderFallbackException) { continue; }
            if (!IsStructuredPlayerName(name)) continue;

            var searchStart = nameStart + (int)rawLength;
            var searchEnd = Math.Min(payload.Length - 1, searchStart + 48);
            var sawPlayerStatusField = false;
            for (var teamOffset = searchStart; teamOffset < searchEnd; teamOffset++)
            {
                if (payload[teamOffset] == 0x30) sawPlayerStatusField = true;
                if (!sawPlayerStatusField || payload[teamOffset] != 0x50 || payload[teamOffset + 1] is not (1 or 2)) continue;
                players.Add(new MatchPlayer((long)rawId, name, payload[teamOffset + 1]));
                break;
            }
        }
        return players;
    }

    private static IReadOnlyList<string> ExtractCandidateMaps(ReadOnlySpan<byte> payload)
    {
        var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var offset = 0; offset < payload.Length - 7; offset++)
        {
            // Map-choice entry observed on the wire:
            // 0A <entry-len> 08 <map-id> 12 <name-len> <short-name> ...
            if (payload[offset] != 0x0A || payload[offset + 2] != 0x08 ||
                payload[offset + 4] != 0x12) continue;
            var entryLength = payload[offset + 1];
            var mapId = payload[offset + 3];
            var nameLength = payload[offset + 5];
            if (entryLength is < 10 or > 80 || nameLength is < 3 or > 40 ||
                offset + 6 + nameLength > payload.Length) continue;
            var map = MapWinRateCatalog.FindByGameId(mapId);
            if (map is null) continue;
            try
            {
                var shortName = new UTF8Encoding(false, true).GetString(payload.Slice(offset + 6, nameLength));
                if (!shortName.All(character => char.IsLetterOrDigit(character) || character is ' ' or '-' or '_'))
                    continue;
                maps.Add(map);
            }
            catch (DecoderFallbackException) { }
        }
        return maps.ToArray();
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, int offset, out ulong value, out int next)
    {
        value = 0;
        var shift = 0;
        while (offset < data.Length && shift < 64)
        {
            var current = data[offset++];
            value |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0) { next = offset; return true; }
            shift += 7;
        }
        next = offset;
        return false;
    }

    private static bool IsStructuredPlayerName(string value) => value.Length is >= 2 and <= 64 &&
        value.Any(char.IsLetterOrDigit) &&
        value.All(character => !char.IsControl(character) && !char.IsSurrogate(character));

    private static string CleanRoomTitle(string value) => value.Trim().Trim('*', '@', ' ', '\'', '"');

    private static string Normalize(string value)
    {
        value = value.Trim().Trim('*', '@', '-', ' ', '\'', '"');
        if (value.EndsWith("'s lobby", StringComparison.OrdinalIgnoreCase)) value = value[..^8].Trim();
        return value;
    }

    private static bool IsCandidate(string value) => value.Length is >= 2 and <= 32 &&
        (value.Any(char.IsLetter) || (value.Length >= 8 && value.All(char.IsDigit))) &&
        IsNameBoundary(value[0]) && IsNameBoundary(value[^1]) &&
        !KnownNoise.Contains(value) &&
        !(value.Length == 2 && value.All(char.IsAscii)) &&
        value.All(IsNameCharacter) &&
        !value.Contains('"') && !value.Contains('\\') &&
        !LooksLikeRoomTitle(value) &&
        !Excluded.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase)) &&
        !Guid.TryParse(value, out _) &&
        (!value.All(c => char.IsAsciiHexDigit(c)) || (value.Length >= 8 && value.All(char.IsDigit)));

    private static bool LooksLikeRoomTitle(string value) =>
        value.EndsWith("'s lobby", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(value, @"(?i)^(?:ru|us|usa|eu|asia)?\s*[+\-]?\s*\d{3,4}(?!\d)(?:\s*elo)?\s*[+\-/±]?.*$");

    private static bool LooksLikeReadableName(string value) =>
        (value.Length >= 8 && value.All(char.IsDigit)) ||
        (value.Length >= 4 && char.IsLetter(value[0]) && value.Count(char.IsLetter) >= 3 &&
         value.All(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c is '_' or '-' or '.' or '[' or ']' or '(' or ')' or '@'));

    private static bool IsNameCharacter(char value) =>
        char.IsLetterOrDigit(value) || char.IsWhiteSpace(value) ||
        value is '_' or '-' or '.' or '[' or ']' or '(' or ')' or '@' or '\'' or
            '【' or '】' or '〖' or '〗' or '〔' or '〕' or '『' or '』' or '「' or '」' or
            '《' or '》' or '〈' or '〉' or '⟦' or '⟧';

    private static bool IsNameBoundary(char value) =>
        char.IsLetterOrDigit(value) || value == '_' ||
        value is '[' or ']' or '(' or ')' or '【' or '】' or '〖' or '〗' or '〔' or '〕' or
            '『' or '』' or '「' or '」' or '《' or '》' or '〈' or '〉' or '⟦' or '⟧';

    private static IEnumerable<string> ExtractStrings(byte[] data)
    {
        var ascii = new StringBuilder();
        foreach (var b in data)
        {
            if (b is >= 0x20 and <= 0x7e) ascii.Append((char)b);
            else { if (ascii.Length >= 2) yield return ascii.ToString(); ascii.Clear(); }
        }
        if (ascii.Length >= 2) yield return ascii.ToString();
        var unicode = new StringBuilder();
        var offset = 0;
        while (offset < data.Length)
        {
            var result = Rune.DecodeFromUtf8(data.AsSpan(offset), out var rune, out var consumed);
            if (result == OperationStatus.Done)
            {
                offset += consumed;
                if (!Rune.IsControl(rune) && (!Rune.IsWhiteSpace(rune) || rune.Value == 0x20)) unicode.Append(rune);
                else
                {
                    if (unicode.Length >= 2 && unicode.ToString().Any(c => c > 127)) yield return unicode.ToString();
                    unicode.Clear();
                }
            }
            else
            {
                if (unicode.Length >= 2 && unicode.ToString().Any(c => c > 127)) yield return unicode.ToString();
                unicode.Clear();
                offset++;
            }
        }
        if (unicode.Length >= 2 && unicode.ToString().Any(c => c > 127)) yield return unicode.ToString();
    }
}
