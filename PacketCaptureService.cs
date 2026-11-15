using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace BrokenArrowSquadTracker;

public sealed class PacketCaptureService : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly string workDir = Path.Combine(Path.GetTempPath(), "BrokenArrowTracker");
    private readonly string? localPlayerName = SteamIdentity.TryGetPersonaName();
    private readonly HashSet<string> currentPlayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> pendingPlayers = new(StringComparer.OrdinalIgnoreCase);
    private Task? worker;
    private string? currentRoomName;
    private DateTime lastLobbyPacketUtc = DateTime.MinValue;
    private DateTime lastLobbyMarkerUtc = DateTime.MinValue;
    private DateTime lastPendingPlayersUtc = DateTime.MinValue;
    public event Action<string>? StatusChanged;
    public event Action<LobbySnapshot>? LobbyChanged;
    public bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public void Start()
    {
        if (worker is not null) return;
        worker = Task.Run(() => CaptureLoopAsync(stop.Token));
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        if (!IsAdministrator) { StatusChanged?.Invoke("관리자 권한이 필요합니다"); return; }
        Directory.CreateDirectory(workDir);
        await RunAsync("pktmon", "stop", ct, true);
        await ConfigureFiltersAsync(ct);

        var slot = 0;
        var activeEtl = CapturePath(slot, "etl");
        TryDelete(activeEtl);
        if (!await StartCaptureWithRetryAsync(activeEtl, ct)) return;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(1500), ct); }
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
                StatusChanged?.Invoke("패킷 분석 복구 중: " + ex.Message);
            }
        }
        await RunAsync("pktmon", "stop", CancellationToken.None, true);
    }

    private string CapturePath(int slot, string extension) => Path.Combine(workDir, $"lobby-{slot}.{extension}");

    private async Task ConfigureFiltersAsync(CancellationToken ct)
    {
        await RunAsync("pktmon", "filter remove", ct, true);
        await RunAsync("pktmon", "filter add BrokenArrowNet1 -t UDP -i 38.180.0.0/15", ct, true);
        await RunAsync("pktmon", "filter add BrokenArrowNet2 -t UDP -i 38.244.0.0/16", ct, true);
        StatusChanged?.Invoke("Broken Arrow 로비 서버 감시 중");
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
        if (lobby.HasLobbyClosed)
        {
            currentPlayers.Clear();
            pendingPlayers.Clear();
            currentRoomName = null;
            LobbyChanged?.Invoke(new LobbySnapshot(null, Array.Empty<string>()));
            return;
        }
        if (lobby.HasLobbyMarker) lastLobbyMarkerUtc = now;
        var names = lobby.Players.ToList();
        if (lobby.HasLobbyMarker && now - lastPendingPlayersUtc <= TimeSpan.FromSeconds(6))
        {
            names.AddRange(pendingPlayers);
            pendingPlayers.Clear();
        }
        var belongsToLobby = lobby.HasLobbyMarker || currentPlayers.Count > 0 ||
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

            if (lobby.HasLobbyMarker)
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
            LobbyChanged?.Invoke(new LobbySnapshot(currentRoomName, visiblePlayers, lobby.HasLobbyMarker));
        }
        else if (currentPlayers.Count > 0 && DateTime.UtcNow - lastLobbyPacketUtc >= TimeSpan.FromMinutes(2))
        {
            currentPlayers.Clear();
            currentRoomName = null;
            LobbyChanged?.Invoke(new LobbySnapshot(null, Array.Empty<string>()));
        }
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
    public static string? TryGetPersonaName()
    {
        try
        {
            var steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "config", "loginusers.vdf");
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
        var roomTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? roomName = null;
        var sawLobby = false;
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
                eventOrder++;
                if (value.Contains("Lobby", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("Join Instance channel", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.Contains("Lobby", StringComparison.OrdinalIgnoreCase)) sawLobby = true;
                    lastLobbyOrder = eventOrder;
                }
                else if (value.StartsWith("Close channel", StringComparison.OrdinalIgnoreCase) ||
                         value.StartsWith("Join World channel", StringComparison.OrdinalIgnoreCase))
                    lastCloseOrder = eventOrder;
            }
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
        return new LobbySnapshot(roomName, players, sawLobby, lastCloseOrder > lastLobbyOrder);
    }

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
