using System.Diagnostics;

namespace BrokenArrowSquadTracker;

public sealed class MainForm : Form
{
    private static readonly HttpClient ImageHttp = CreateImageClient();
    private static readonly Color Canvas = Color.FromArgb(14, 17, 16), Surface = Color.FromArgb(23, 27, 25), Raised = Color.FromArgb(29, 34, 31);
    private static readonly Color Border = Color.FromArgb(57, 65, 60), Primary = Color.FromArgb(239, 241, 239), Muted = Color.FromArgb(151, 160, 154);
    private static readonly Color Accent = Color.FromArgb(196, 220, 84), Danger = Color.FromArgb(224, 112, 96);
    private readonly PlayerApi api = new();
    private readonly PacketCaptureService capture;
    private readonly PlayerNotesStore playerNotes = new();
    private readonly TrackerSettingsStore trackerSettings;
    private readonly ToolTip noteToolTip = new();
    private readonly Panel content = new() { Dock = DockStyle.Fill };
    private readonly Label connection = new();
    private string[] lastNames = [];
    private string? lastRoomName;
    private int lobbyLoadVersion;
    private CancellationTokenSource? lobbyLookup;
    private DateTime lastLookupUtc = DateTime.MinValue;
    private Panel? lobbyPage;
    private Label? lobbyTitle;
    private Label? lobbySummary;
    private TableLayoutPanel? lobbyTable;
    private Label? enemyTitle;
    private Label? enemySummary;
    private TableLayoutPanel? enemyTable;
    private Panel? mapPanel;
    private string[] lastMapCandidates = [];
    private readonly Dictionary<string, LobbyRow> lobbyRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LobbyRow> enemyRows = new(StringComparer.OrdinalIgnoreCase);
    private PlayerResult[] displayedPlayers = [];
    private LobbySnapshot? lastLobbySnapshot;
    private bool matchView;
    private bool settingsOpen;
    private bool rosterViewIsMatch;

    public MainForm()
    {
        trackerSettings = new TrackerSettingsStore();
        capture = new PacketCaptureService(trackerSettings.SteamPath);
        Text = "Broken Arrow Player Tracker"; Width = 1280; Height = 900; MinimumSize = new Size(1000, 720);
        BackColor = Canvas; ForeColor = Primary; Font = new Font("Bahnschrift", 10f); StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(content); Controls.Add(BuildHeader());
        capture.StatusChanged += text => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => SetCaptureStatus(text)); };
        capture.LobbyChanged += lobby =>
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(() => _ = LoadPlayersSafelyAsync(lobby));
        };
        FormClosed += (_, _) => { lobbyLookup?.Cancel(); lobbyLookup?.Dispose(); capture.Dispose(); noteToolTip.Dispose(); };
        DiagnosticLog.Write("APP", "프로그램 시작");
        ShowDashboard(); capture.Start();
    }

    private Control BuildHeader()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Surface };
        bar.Paint += (_, e) => { using var pen = new Pen(Border); e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1); };
        bar.Controls.Add(new Panel { Location = new Point(20, 15), Size = new Size(4, 28), BackColor = Accent });
        bar.Controls.Add(new Label { Text = "BROKEN ARROW TRACKER", AutoSize = true, Location = new Point(36, 18), Font = new Font("Bahnschrift SemiBold", 12f), ForeColor = Primary });
        var home = HeaderButton("대시보드"); home.Location = new Point(238, 12);
        home.Click += (_, _) => ShowHome();
        var settings = HeaderButton("설정"); settings.Location = new Point(330, 12);
        settings.Click += (_, _) => ShowSettings();
        bar.Controls.AddRange([home, settings]);
        var source = new Label { Text = "DATA BY BATRACE", AutoSize = true, ForeColor = Muted, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        source.Location = new Point(Width - 190, 21); bar.Resize += (_, _) => source.Left = bar.ClientSize.Width - source.Width - 22;
        bar.Controls.Add(source); return bar;
    }

    private static Button HeaderButton(string text)
    {
        var button = new Button { Text = text, Size = new Size(86, 34), FlatStyle = FlatStyle.Flat,
            BackColor = Surface, ForeColor = Muted, Font = new Font("Bahnschrift SemiBold", 9f) };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private void ShowHome()
    {
        settingsOpen = false;
        if (lastLobbySnapshot is { Players.Count: > 0 }) _ = LoadPlayersSafelyAsync(lastLobbySnapshot, force: true);
        else ShowDashboard();
    }

    private void ShowSettings()
    {
        settingsOpen = true;
        ClearContent();
        ResetRosterControls();
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
        page.Controls.Add(new Label { Text = "설정", AutoSize = true, Location = new Point(28, 24),
            Font = new Font("Bahnschrift SemiBold", 20f), ForeColor = Primary });
        page.Controls.Add(new Label { Text = "진단", AutoSize = true, Location = new Point(28, 88),
            Font = new Font("Bahnschrift SemiBold", 12f), ForeColor = Primary });
        var section = new Panel { Location = new Point(28, 120), Height = 92, BackColor = Surface,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        section.Width = page.ClientSize.Width - 56; section.Paint += DrawBorder;
        section.Controls.Add(new Label { Text = "문제 확인용 로그를 TXT 파일로 저장합니다.", AutoSize = true,
            Location = new Point(20, 19), ForeColor = Primary });
        section.Controls.Add(new Label { Text = "현재 실행 중 수집된 로비 감지와 BATrace 조회 결과가 포함됩니다.", AutoSize = true,
            Location = new Point(20, 48), ForeColor = Muted });
        var export = MakeButton("로그 추출", (_, _) => ExportLog());
        export.Size = new Size(96, 36); export.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        export.Location = new Point(section.Width - 116, 28);
        section.Resize += (_, _) => export.Left = section.ClientSize.Width - export.Width - 20;
        section.Controls.Add(export);

        var steamSection = new Panel { Location = new Point(28, 228), Height = 112, BackColor = Surface,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        steamSection.Width = page.ClientSize.Width - 56; steamSection.Paint += DrawBorder;
        steamSection.Controls.Add(new Label { Text = "Steam 위치", AutoSize = true, Location = new Point(20, 14),
            Font = new Font("Bahnschrift SemiBold", 11f), ForeColor = Primary });
        var steamPath = new TextBox { Location = new Point(20, 44), Height = 32, ReadOnly = true,
            BackColor = Raised, ForeColor = trackerSettings.SteamPathFound ? Primary : Danger,
            BorderStyle = BorderStyle.FixedSingle, Text = trackerSettings.SteamPath ?? "Steam 위치를 찾지 못했습니다." };
        var browseSteam = MakeButton("폴더 선택", (_, _) => BrowseSteamPath());
        browseSteam.Size = new Size(96, 34); browseSteam.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        steamPath.Width = steamSection.Width - browseSteam.Width - 60;
        browseSteam.Location = new Point(steamSection.Width - browseSteam.Width - 20, 43);
        steamSection.Resize += (_, _) =>
        {
            browseSteam.Left = steamSection.ClientSize.Width - browseSteam.Width - 20;
            steamPath.Width = browseSteam.Left - steamPath.Left - 12;
        };
        steamSection.Controls.AddRange([steamPath, browseSteam]);

        page.Resize += (_, _) => section.Width = page.ClientSize.Width - 56;
        page.Resize += (_, _) => steamSection.Width = page.ClientSize.Width - 56;
        page.Controls.AddRange([section, steamSection]); content.Controls.Add(page);
    }

    private void BrowseSteamPath()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "steam.exe가 있는 Steam 설치 폴더를 선택하세요.",
            UseDescriptionForTitle = true,
            SelectedPath = trackerSettings.SteamPath ?? string.Empty,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!trackerSettings.SetSteamPath(dialog.SelectedPath))
        {
            MessageBox.Show(this, "선택한 폴더에서 steam.exe와 config 폴더를 찾지 못했습니다.",
                "Steam 위치 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        MessageBox.Show(this, "Steam 위치를 저장했습니다. 프로그램을 다시 실행하면 적용됩니다.",
            "Steam 위치 저장", MessageBoxButtons.OK, MessageBoxIcon.Information);
        ShowSettings();
    }

    private void ExportLog()
    {
        using var dialog = new SaveFileDialog { Filter = "텍스트 파일 (*.txt)|*.txt", AddExtension = true,
            DefaultExt = "txt", FileName = $"BrokenArrowTracker-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            DiagnosticLog.Export(dialog.FileName);
            MessageBox.Show(this, "로그 파일을 저장했습니다.", "로그 추출", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "로그 저장에 실패했습니다.\n" + ex.Message, "로그 추출", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowDashboard()
    {
        ClearContent();
        ResetRosterControls();
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Canvas, Padding = new Padding(28, 24, 28, 28) };
        page.Controls.Add(new Label { Text = "대시보드", AutoSize = true, Location = new Point(28, 24), Font = new Font("Bahnschrift SemiBold", 20f), ForeColor = Primary });
        page.Controls.Add(new Label { Text = "Broken Arrow 로비 연결을 기다리고 있습니다.", AutoSize = true, Location = new Point(30, 61), ForeColor = Muted });

        var state = new Panel { Location = new Point(28, 100), Height = 78, BackColor = Surface, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        state.Width = page.ClientSize.Width - 56; state.Paint += DrawBorder;
        state.Controls.Add(new Panel { Location = new Point(20, 18), Size = new Size(3, 42), BackColor = capture.IsAdministrator ? Accent : Danger });
        state.Controls.Add(new Label { Text = "패킷 모니터", AutoSize = true, Location = new Point(38, 14), Font = new Font("Bahnschrift SemiBold", 11f), ForeColor = Primary });
        connection.Text = capture.IsAdministrator ? "초기화 중" : "관리자 권한 필요"; connection.AutoSize = true; connection.Location = new Point(38, 42); connection.ForeColor = capture.IsAdministrator ? Muted : Danger;
        state.Controls.Add(connection);

        var sectionTitle = new Label { Text = "현재 로비", AutoSize = true, Location = new Point(28, 206), Font = new Font("Bahnschrift SemiBold", 14f), ForeColor = Primary };
        var body = new Panel { Location = new Point(28, 242), Height = 230, BackColor = Surface, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        body.Width = page.ClientSize.Width - 56; body.Paint += DrawBorder;
        body.Controls.Add(new Label { Text = "플레이어가 감지되면 이곳에 ELO와 전적 정보가 표시됩니다.", AutoSize = true, ForeColor = Muted, Location = new Point(24, 28) });
        body.Controls.Add(new Label { Text = "Broken Arrow에서 멀티플레이 방에 입장하세요.", AutoSize = true, ForeColor = Primary, Font = new Font("Bahnschrift SemiBold", 13f), Location = new Point(24, 66) });
        body.Controls.Add(new Label { Text = "로비가 확인되면 플레이어 정보가 자동으로 갱신됩니다.", AutoSize = true, ForeColor = Muted, Location = new Point(24, 100) });
        if (!trackerSettings.SteamPathFound)
        {
            var steamWarning = new Panel { Location = new Point(24, 142), Height = 62, BackColor = Raised,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            steamWarning.Width = body.ClientSize.Width - 48;
            steamWarning.Controls.Add(new Label { Text = "Steam 위치 설정 필요", AutoSize = true,
                Location = new Point(16, 11), ForeColor = Danger, Font = new Font("Bahnschrift SemiBold", 10f) });
            steamWarning.Controls.Add(new Label { Text = "Steam을 찾지 못했습니다. 설정에서 설치 폴더를 지정하세요.",
                AutoSize = true, Location = new Point(16, 34), ForeColor = Muted });
            var openSettings = MakeButton("설정 열기", (_, _) => ShowSettings());
            openSettings.Size = new Size(88, 34); openSettings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            openSettings.Location = new Point(steamWarning.Width - 104, 14);
            steamWarning.Resize += (_, _) => openSettings.Left = steamWarning.ClientSize.Width - openSettings.Width - 16;
            steamWarning.Controls.Add(openSettings);
            body.Controls.Add(steamWarning);
            body.Resize += (_, _) => steamWarning.Width = body.ClientSize.Width - 48;
        }
        page.Resize += (_, _) => { state.Width = page.ClientSize.Width - 56; body.Width = page.ClientSize.Width - 56; };
        page.Controls.AddRange([state, sectionTitle, body]); content.Controls.Add(page);
    }

    private void SetCaptureStatus(string text) { connection.Text = text; connection.ForeColor = text.Contains("실패") || text.Contains("필요") ? Danger : Muted; }

    private async Task LoadPlayersSafelyAsync(LobbySnapshot lobby, bool force = false)
    {
        try { await LoadPlayersAsync(lobby, force); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetCaptureStatus("플레이어 표시 복구 중: " + ex.Message); }
    }

    private async Task LoadPlayersAsync(LobbySnapshot lobby, bool force = false)
    {
        lastLobbySnapshot = lobby;
        var incomingMaps = (lobby.CandidateMaps ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var mapsChanged = !incomingMaps.SequenceEqual(lastMapCandidates, StringComparer.OrdinalIgnoreCase);
        if (mapsChanged) lastMapCandidates = incomingMaps;
        var matchPlayers = lobby.MatchPlayers?.Where(player => player.Team is 1 or 2).ToArray() ?? [];
        var localName = SteamIdentity.TryGetPersonaName(trackerSettings.SteamPath);
        var localTeam = matchPlayers.FirstOrDefault(player =>
            player.Name.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Team;
        if (lobby.IsMatch && localTeam is null)
        {
            connection.Text = "내 팀 확인 중";
            connection.ForeColor = Muted;
            return;
        }
        var selectedMatchPlayers = lobby.IsMatch && localTeam is not null
            ? matchPlayers.Where(player => player.Team == localTeam || trackerSettings.ShowEnemyPlayers).ToArray()
            : matchPlayers;
        var names = lobby.IsMatch ? selectedMatchPlayers.Select(player => player.Name).ToArray() : lobby.Players;
        if (names.Count == 0)
        {
            lobbyLookup?.Cancel();
            lobbyLoadVersion++;
            if (lastNames.Length == 0) return;
            lastNames = [];
            lastRoomName = null;
            displayedPlayers = [];
            lastLobbySnapshot = null;
            DiagnosticLog.Write("LOBBY", "로비 종료");
            if (!settingsOpen) ShowDashboard();
            SetCaptureStatus("Broken Arrow 패킷 수신 중");
            return;
        }
        var normalized = names.Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray();
        DiagnosticLog.Write(lobby.IsMatch ? "MATCH" : "LOBBY", $"room={lobby.RoomName ?? "-"} marker={lobby.HasLobbyMarker} candidates=[{string.Join(" | ", normalized)}]");
        if (!force && !mapsChanged && normalized.SequenceEqual(lastNames, StringComparer.OrdinalIgnoreCase) &&
            string.Equals(lobby.RoomName, lastRoomName, StringComparison.OrdinalIgnoreCase) &&
            DateTime.UtcNow - lastLookupUtc < TimeSpan.FromSeconds(30)) return;

        var roomChanged = matchView != lobby.IsMatch || !string.Equals(lobby.RoomName, lastRoomName, StringComparison.OrdinalIgnoreCase);
        if (lobby.HasLobbyMarker && roomChanged) displayedPlayers = [];
        var loadVersion = ++lobbyLoadVersion;
        lobbyLookup?.Cancel();
        lobbyLookup?.Dispose();
        lobbyLookup = new CancellationTokenSource();
        var lookupToken = lobbyLookup.Token;
        lastLookupUtc = DateTime.UtcNow;
        lastNames = normalized;
        lastRoomName = lobby.RoomName;
        matchView = lobby.IsMatch;
        connection.Text = "플레이어 정보 조회 중";
        connection.ForeColor = Muted;

        var packetPlayers = selectedMatchPlayers.GroupBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group =>
            {
                var player = group.First();
                return new PlayerResult(player.Name, player.Id.ToString(), SteamId: null, Elo: null,
                GlobalRank: null, Level: null, RatingGames: null, Kd: null, TurtleIndex: null,
                AvatarUrl: null, UpdatedAt: null, Source: "Packet");
            }, StringComparer.OrdinalIgnoreCase);
        var existingPlayers = displayedPlayers.GroupBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var trustedLobbyPlayers = !lobby.IsMatch && lobby.PlayersAreTrusted
            ? normalized.ToDictionary(name => name, name => existingPlayers.GetValueOrDefault(name) ??
                new PlayerResult(name, name, SteamId: null, Elo: null,
                    GlobalRank: null, Level: null, RatingGames: null, Kd: null, TurtleIndex: null,
                    AvatarUrl: null, UpdatedAt: null, Source: "Lobby"), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PlayerResult>(StringComparer.OrdinalIgnoreCase);
        var identifiedPlayerIds = (lobby.IdentifiedPlayers ?? lobby.MatchPlayers ?? Array.Empty<MatchPlayer>())
            .GroupBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Id.ToString(), StringComparer.OrdinalIgnoreCase);
        if (lobby.IsMatch && !settingsOpen)
            ShowMatchRoster(packetPlayers.Values.ToArray(), selectedMatchPlayers, localTeam, removeMissing: true);
        else if (!lobby.IsMatch && trustedLobbyPlayers.Count > 0 && !settingsOpen)
        {
            var immediateRoster = normalized
                .Where(trustedLobbyPlayers.ContainsKey)
                .Select(name => (Name: name, Player: (PlayerResult?)trustedLobbyPlayers[name]))
                .ToArray();
            displayedPlayers = immediateRoster.Select(item => item.Player!).ToArray();
            ShowLobby(lobby.RoomName, immediateRoster, removeMissing: true);
            connection.Text = "플레이어 정보 조회 중";
            connection.ForeColor = Muted;
        }

        var pending = normalized.Select(async name =>
        {
            identifiedPlayerIds.TryGetValue(name, out var playerId);
            try { return (Name: name, Player: await api.SearchAsync(name, lookupToken, playerId)); }
            catch (OperationCanceledException) { return (Name: name, Player: (PlayerResult?)null); }
            catch { return (Name: name, Player: (PlayerResult?)null); }
        }).ToList();
        var completedResults = new Dictionary<string, PlayerResult?>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            var result = await completed;
            if (lookupToken.IsCancellationRequested || loadVersion != lobbyLoadVersion) return;
            completedResults[result.Name] = result.Player;
        }

        var orderedRoster = normalized.Select(name =>
        {
            completedResults.TryGetValue(name, out var player);
            if (player is null && lobby.IsMatch) packetPlayers.TryGetValue(name, out player);
            if (player is null && !lobby.IsMatch) trustedLobbyPlayers.TryGetValue(name, out player);
            if (player is not null && !string.Equals(player.Name, name, StringComparison.Ordinal))
                player = player with { Name = name };
            return (Name: name, Player: player);
        }).Where(item => item.Player is not null).Take(lobby.IsMatch ? 10 : 12).ToArray();
        if (!lobby.IsMatch) capture.SetVerifiedLobbyPlayers(orderedRoster.Select(item => item.Name));
        DiagnosticLog.Write("BATRACE", $"verified=[{string.Join(" | ", orderedRoster.Select(item => item.Name))}] rejected=[{string.Join(" | ", normalized.Except(orderedRoster.Select(item => item.Name), StringComparer.OrdinalIgnoreCase))}]");
        if (orderedRoster.Length == 0)
        {
            connection.Text = "BATrace 조회 결과 없음";
            connection.ForeColor = Muted;
            return;
        }
        connection.Text = "로비 연결됨";
        connection.ForeColor = Accent;
        displayedPlayers = orderedRoster.Select(item => item.Player!).ToArray();
        if (settingsOpen) return;
        if (lobby.IsMatch) ShowMatchRoster(displayedPlayers, selectedMatchPlayers, localTeam, removeMissing: true);
        else ShowLobby(lobby.RoomName, orderedRoster, removeMissing: true);
    }

    private void ShowLobby(string? roomName, (string Name, PlayerResult? Player)[] players, bool removeMissing)
    {
        EnsureLobbyView(false);
        var verified = players.Where(item => item.Player is not null).Select(item => item.Player!).ToArray();
        UpdateRosterTable(lobbyTable!, lobbyRows, verified, removeMissing);
        lobbyTitle!.Text = string.IsNullOrWhiteSpace(roomName) ? "현재 로비" : roomName;
        lobbySummary!.Text = $"{lobbyRows.Count}명 · BATrace 확인 완료";
        lobbyPage!.AutoScrollMinSize = new Size(0, lobbyTable!.Bottom + 28);
    }

    private void ShowMatchRoster(PlayerResult[] players, MatchPlayer[] roster, int? localTeam, bool removeMissing)
    {
        EnsureLobbyView(true);
        UpdateMapPanel();
        var byName = players.GroupBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var allies = roster.Where(player => localTeam is null || player.Team == localTeam)
            .Select(player => byName.GetValueOrDefault(player.Name)).Where(player => player is not null).Cast<PlayerResult>().ToArray();
        var enemies = localTeam is null || !trackerSettings.ShowEnemyPlayers ? [] : roster.Where(player => player.Team != localTeam)
            .Select(player => byName.GetValueOrDefault(player.Name)).Where(player => player is not null).Cast<PlayerResult>().ToArray();

        UpdateRosterTable(lobbyTable!, lobbyRows, allies, removeMissing);
        lobbyTitle!.Visible = false;
        lobbySummary!.Visible = false;
        if (enemyTable is not null)
        {
            UpdateRosterTable(enemyTable, enemyRows, enemies, removeMissing);
            enemyTitle!.Text = "적군";
            enemySummary!.Text = $"{enemies.Length}명";
        }
        LayoutRosterSections();
    }

    private void UpdateMapPanel()
    {
        if (mapPanel is null || lobbyTable is null) return;
        mapPanel.Controls.Clear();
        mapPanel.Visible = lastMapCandidates.Length > 0;
        lobbyTable.Top = mapPanel.Visible ? 156 : 24;
        if (!mapPanel.Visible) return;

        var cards = new FlowLayoutPanel { Location = new Point(14, 10), Height = 86,
            Width = Math.Max(0, mapPanel.ClientSize.Width - 28), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            WrapContents = false, AutoScroll = true, BackColor = Surface };
        foreach (var name in lastMapCandidates)
        {
            var stats = MapWinRateCatalog.Find(name);
            if (stats is null) continue;
            var card = new Panel { Width = 215, Height = 70, BackColor = Raised, Margin = new Padding(0, 0, 10, 0) };
            card.Controls.Add(new Label { Text = stats.Name, AutoSize = true, Location = new Point(12, 9),
                Font = new Font("Bahnschrift SemiBold", 10f), ForeColor = Primary });
            var rateText = stats.RuWinRate is double ru && stats.UsWinRate is double us
                ? $"US {us:P1}   RU {ru:P1}"
                : "승률 통계 없음";
            card.Controls.Add(new Label { Text = rateText, AutoSize = true,
                Location = new Point(12, 33), Font = new Font("Bahnschrift SemiBold", 10f), ForeColor = Accent });
            card.Controls.Add(new Label { Text = stats.Matches > 0 ? $"표본 {stats.Matches:N0}경기" : "BArmory 목록 확인", AutoSize = true, Location = new Point(12, 53),
                Font = new Font("Bahnschrift", 8f), ForeColor = Muted });
            cards.Controls.Add(card);
        }
        mapPanel.Controls.Add(cards);
    }

    private void UpdateRosterTable(TableLayoutPanel table, Dictionary<string, LobbyRow> rows,
        PlayerResult[] verified, bool removeMissing)
    {
        if (!removeMissing)
        {
            table.SuspendLayout();
            foreach (var player in verified)
            {
                var key = PlayerKey(player);
                if (!rows.TryGetValue(key, out var row))
                {
                    row = CreateLobbyRow(player);
                    rows[key] = row;
                    var targetRow = rows.Count;
                    for (var column = 0; column < row.Controls.Length; column++) table.Controls.Add(row.Controls[column], column, targetRow);
                    table.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
                }
                UpdateLobbyRow(row, player);
            }
            table.RowCount = rows.Count + 1;
            table.Height = 47 + rows.Count * 62;
            table.ResumeLayout(true);
            return;
        }
        var desiredKeys = verified.Select(PlayerKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        table.SuspendLayout();
        foreach (var key in rows.Keys.Where(key => !desiredKeys.Contains(key)).ToArray())
        {
            var stale = rows[key];
            foreach (var control in stale.Controls) { table.Controls.Remove(control); control.Dispose(); }
            rows.Remove(key);
        }

        table.RowStyles.Clear();
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        for (var index = 0; index < verified.Length; index++)
        {
            var player = verified[index];
            var key = PlayerKey(player);
            if (!rows.TryGetValue(key, out var row))
            {
                row = CreateLobbyRow(player);
                rows[key] = row;
                for (var column = 0; column < row.Controls.Length; column++) table.Controls.Add(row.Controls[column], column, index + 1);
            }
            UpdateLobbyRow(row, player);
            for (var column = 0; column < row.Controls.Length; column++) table.SetCellPosition(row.Controls[column], new TableLayoutPanelCellPosition(column, index + 1));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        }

        table.RowCount = verified.Length + 1;
        table.Height = 47 + verified.Length * 62;
        table.ResumeLayout(true);
    }

    private void EnsureLobbyView(bool isMatch)
    {
        var wantsEnemyTable = isMatch && trackerSettings.ShowEnemyPlayers;
        if (lobbyTable is not null && rosterViewIsMatch == isMatch && (enemyTable is not null) == wantsEnemyTable) return;
        ClearContent();
        ResetRosterControls();
        rosterViewIsMatch = isMatch;
        lobbyPage = new Panel { Dock = DockStyle.Fill, BackColor = Canvas, AutoScroll = true };
        lobbyTitle = new Label { AutoSize = true, Location = new Point(28, 24), Font = new Font("Bahnschrift SemiBold", 20f), ForeColor = Primary };
        lobbySummary = new Label { AutoSize = true, Location = new Point(30, 61), ForeColor = Muted };
        mapPanel = new Panel { Location = new Point(28, 24), Height = 108, BackColor = Surface,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Visible = false };
        mapPanel.Width = lobbyPage.ClientSize.Width - 56;
        mapPanel.Paint += DrawBorder;
        lobbyTable = BuildRosterTable(new Point(28, 100));
        lobbyPage.Controls.AddRange([lobbyTitle, lobbySummary, mapPanel, lobbyTable]);
        if (wantsEnemyTable)
        {
            enemyTitle = new Label { AutoSize = true, Font = new Font("Bahnschrift SemiBold", 20f), ForeColor = Primary };
            enemySummary = new Label { AutoSize = true, ForeColor = Muted };
            enemyTable = BuildRosterTable(Point.Empty);
            lobbyPage.Controls.AddRange([enemyTitle, enemySummary, enemyTable]);
        }
        lobbyPage.Resize += (_, _) =>
        {
            if (lobbyTable is not null) lobbyTable.Width = lobbyPage.ClientSize.Width - 56;
            if (enemyTable is not null) enemyTable.Width = lobbyPage.ClientSize.Width - 56;
            if (mapPanel is not null) mapPanel.Width = lobbyPage.ClientSize.Width - 56;
        };
        content.Controls.Add(lobbyPage);
    }

    private TableLayoutPanel BuildRosterTable(Point location)
    {
        var table = new TableLayoutPanel { Location = location, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Surface, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single, ColumnCount = 9, RowCount = 1,
            Size = new Size(Math.Max(0, (lobbyPage?.ClientSize.Width ?? ClientSize.Width) - 56), 47) };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        foreach (var width in new[] { 28f, 10f, 12f, 8f, 12f, 10f, 10f, 10f })
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        string[] headers = ["플레이어", "ELO", "세계 순위", "레벨", "랭크 경기", "K/D", "Turtle", "Steam ID", ""];
        for (var column = 0; column < headers.Length; column++) table.Controls.Add(TableLabel(headers[column], true), column, 0);
        return table;
    }

    private void LayoutRosterSections()
    {
        if (lobbyPage is null || lobbyTable is null) return;
        if (enemyTable is null)
        {
            lobbyPage.AutoScrollMinSize = new Size(0, lobbyTable.Bottom + 28);
            return;
        }
        enemyTitle!.Location = new Point(28, lobbyTable.Bottom + 24);
        enemySummary!.Location = new Point(30, enemyTitle.Bottom + 7);
        enemyTable.Location = new Point(28, enemySummary.Bottom + 22);
        lobbyPage.AutoScrollMinSize = new Size(0, enemyTable.Bottom + 28);
    }

    private LobbyRow CreateLobbyRow(PlayerResult player)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Margin = Padding.Empty };
        var name = new Label { Dock = DockStyle.Fill, Padding = new Padding(58, 0, 4, 0), TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Primary, Font = new Font("Bahnschrift SemiBold", 11f) };
        var avatar = new PictureBox { Location = new Point(14, 11), Size = new Size(40, 40), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Raised };
        avatar.Disposed += (_, _) => avatar.Image?.Dispose();
        var indicator = new Panel { Dock = DockStyle.Left, Width = 3, BackColor = Accent };
        panel.Controls.AddRange([name, avatar, indicator]);
        avatar.BringToFront();
        var values = Enumerable.Range(0, 7).Select(_ => TableLabel("—")).ToArray();
        var steamLink = new LinkLabel
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = Padding.Empty,
            BackColor = Surface,
            ForeColor = Accent,
            LinkColor = Accent,
            ActiveLinkColor = Primary,
            VisitedLinkColor = Accent,
            Font = new Font("Bahnschrift", 10.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        steamLink.LinkClicked += (_, _) => OpenSteamProfile(steamLink.Tag as PlayerResult);
        values[^1] = steamLink;
        var actions = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Margin = Padding.Empty };
        var dislike = MakeButton("비추", (sender, _) => EditPlayerNote((sender as Button)?.Tag as PlayerResult));
        dislike.Size = new Size(62, 34); dislike.Location = new Point(9, 13);
        actions.Controls.Add(dislike);
        return new LobbyRow(panel, name, avatar, values, actions, dislike);
    }

    private void UpdateLobbyRow(LobbyRow row, PlayerResult player)
    {
        var note = playerNotes.Find(player);
        row.Name.Text = note is null ? player.Name : "⚠ " + player.Name;
        row.Name.ForeColor = note is null ? Primary : Danger;
        noteToolTip.SetToolTip(row.PlayerCell, note?.Reason ?? string.Empty);
        noteToolTip.SetToolTip(row.Name, note?.Reason ?? string.Empty);
        string[] values = [FormatElo(player.Elo), player.GlobalRank is int rank ? $"#{rank:N0}" : "—",
            player.Level?.ToString() ?? "—", player.RatingGames?.ToString("N0") ?? "—", player.Kd?.ToString("0.00") ?? "—",
            player.TurtleIndex?.ToString("0.0") ?? "—", ShortSteam(player.SteamId)];
        for (var index = 0; index < values.Length; index++) row.Values[index].Text = values[index];
        var steamLink = (LinkLabel)row.Values[^1];
        var hasSteamProfile = !string.IsNullOrWhiteSpace(player.SteamId);
        steamLink.Tag = hasSteamProfile ? player : null;
        steamLink.LinkBehavior = hasSteamProfile ? LinkBehavior.HoverUnderline : LinkBehavior.NeverUnderline;
        steamLink.Cursor = hasSteamProfile ? Cursors.Hand : Cursors.Default;
        row.Dislike.Tag = player;
        row.Dislike.Text = note is null ? "비추" : "기록됨";
        row.Dislike.ForeColor = note is null ? Muted : Danger;
        row.Dislike.FlatAppearance.BorderColor = note is null ? Border : Danger;
        if (!string.Equals(row.AvatarUrl, player.AvatarUrl, StringComparison.Ordinal))
        {
            row.AvatarUrl = player.AvatarUrl;
            row.Avatar.Tag = player.AvatarUrl;
            if (string.IsNullOrWhiteSpace(player.AvatarUrl)) { row.Avatar.Image?.Dispose(); row.Avatar.Image = null; }
            else _ = LoadAvatarAsync(row.Avatar, player.AvatarUrl);
        }
    }

    private static string PlayerKey(PlayerResult player) => player.Name;

    private void EditPlayerNote(PlayerResult? player)
    {
        if (player is null) return;
        var existing = playerNotes.Find(player);
        using var dialog = new ReasonDialog(player.Name, existing);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (dialog.RemoveRequested) playerNotes.Remove(player);
        else playerNotes.Save(player, dialog.Reason);
        if (lobbyRows.TryGetValue(PlayerKey(player), out var row)) UpdateLobbyRow(row, player);
        if (enemyRows.TryGetValue(PlayerKey(player), out var enemyRow)) UpdateLobbyRow(enemyRow, player);
    }

    private void ClearContent()
    {
        connection.Parent?.Controls.Remove(connection);
        var controls = content.Controls.Cast<Control>().ToArray();
        content.Controls.Clear();
        foreach (var control in controls) control.Dispose();
    }

    private void ResetRosterControls()
    {
        lobbyPage = null;
        lobbyTitle = null;
        lobbySummary = null;
        lobbyTable = null;
        enemyTitle = null;
        enemySummary = null;
        enemyTable = null;
        mapPanel = null;
        lobbyRows.Clear();
        enemyRows.Clear();
        rosterViewIsMatch = false;
    }

    private static Label TableLabel(string text, bool header = false) => new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty,
        BackColor = header ? Raised : Surface, ForeColor = header ? Muted : Primary, Font = new Font("Bahnschrift", header ? 9f : 10.5f, header ? FontStyle.Regular : FontStyle.Bold) };

    private static HttpClient CreateImageClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ArrowScope-SquadTracker/0.3");
        return client;
    }

    private static async Task LoadAvatarAsync(PictureBox target, string url)
    {
        try
        {
            var bytes = await ImageHttp.GetByteArrayAsync(url);
            using var stream = new MemoryStream(bytes);
            using var source = Image.FromStream(stream);
            var image = new Bitmap(source);
            if (target.IsDisposed || !string.Equals(target.Tag as string, url, StringComparison.Ordinal)) image.Dispose();
            else
            {
                var previous = target.Image;
                target.Image = image;
                previous?.Dispose();
                target.BringToFront();
                target.Refresh();
            }
        }
        catch { }
    }
    private static Button MakeButton(string text, EventHandler click) { var b = new Button { Text = text, AutoSize = true, Height = 34, Padding = new Padding(9, 0, 9, 0), FlatStyle = FlatStyle.Flat, BackColor = Raised, ForeColor = Primary, Font = new Font("Bahnschrift SemiBold", 9f) }; b.FlatAppearance.BorderColor = Border; b.Click += click; return b; }
    private static string Age(DateTimeOffset? value) { if (value is null) return "—"; var days = Math.Max(0, (DateTimeOffset.UtcNow - value.Value).Days); return days == 0 ? "오늘" : $"{days}일 전"; }
    private static string ShortSteam(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Length > 8 ? "…" + value[^8..] : value;
    private static string FormatElo(double? value) => value is null ? "—" : Math.Floor(value.Value).ToString("N0");
    private static void DrawBorder(object? sender, PaintEventArgs e) { if (sender is Control c) { using var pen = new Pen(Border); e.Graphics.DrawRectangle(pen, 0, 0, c.Width - 1, c.Height - 1); } }
    private static void OpenPlayer(PlayerResult? p)
    {
        if (p is null) return;
        var url = !string.IsNullOrWhiteSpace(p.SteamId)
            ? $"https://steamcommunity.com/profiles/{Uri.EscapeDataString(p.SteamId)}"
            : $"https://dash.batrace.top/player/{Uri.EscapeDataString(p.Id)}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static void OpenSteamProfile(PlayerResult? player)
    {
        if (string.IsNullOrWhiteSpace(player?.SteamId)) return;
        var url = $"https://steamcommunity.com/profiles/{Uri.EscapeDataString(player.SteamId)}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private sealed class LobbyRow(Panel playerCell, Label name, PictureBox avatar, Label[] values,
        Panel actions, Button dislike)
    {
        public Panel PlayerCell { get; } = playerCell;
        public Label Name { get; } = name;
        public PictureBox Avatar { get; } = avatar;
        public Label[] Values { get; } = values;
        public Panel Actions { get; } = actions;
        public Button Dislike { get; } = dislike;
        public string? AvatarUrl { get; set; }
        public Control[] Controls => [PlayerCell, .. Values, Actions];
    }
}
