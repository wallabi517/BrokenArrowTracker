using System.Diagnostics;

namespace BrokenArrowSquadTracker;

public sealed class MainForm : Form
{
    private static readonly HttpClient ImageHttp = CreateImageClient();
    private static readonly Color Canvas = Color.FromArgb(14, 17, 16), Surface = Color.FromArgb(23, 27, 25), Raised = Color.FromArgb(29, 34, 31);
    private static readonly Color Border = Color.FromArgb(57, 65, 60), Primary = Color.FromArgb(239, 241, 239), Muted = Color.FromArgb(151, 160, 154);
    private static readonly Color Accent = Color.FromArgb(196, 220, 84), Danger = Color.FromArgb(224, 112, 96);
    private readonly PlayerApi api = new();
    private readonly PacketCaptureService capture = new();
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
    private readonly Dictionary<string, LobbyRow> lobbyRows = new(StringComparer.OrdinalIgnoreCase);

    public MainForm()
    {
        Text = "Broken Arrow Player Tracker"; Width = 1080; Height = 680; MinimumSize = new Size(900, 560);
        BackColor = Canvas; ForeColor = Primary; Font = new Font("Bahnschrift", 10f); StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(content); Controls.Add(BuildHeader());
        capture.StatusChanged += text => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => SetCaptureStatus(text)); };
        capture.LobbyChanged += lobby =>
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(() => _ = LoadPlayersSafelyAsync(lobby));
        };
        FormClosed += (_, _) => { lobbyLookup?.Cancel(); lobbyLookup?.Dispose(); capture.Dispose(); };
        ShowDashboard(); capture.Start();
    }

    private Control BuildHeader()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Surface };
        bar.Paint += (_, e) => { using var pen = new Pen(Border); e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1); };
        bar.Controls.Add(new Panel { Location = new Point(20, 15), Size = new Size(4, 28), BackColor = Accent });
        bar.Controls.Add(new Label { Text = "BROKEN ARROW TRACKER", AutoSize = true, Location = new Point(36, 18), Font = new Font("Bahnschrift SemiBold", 12f), ForeColor = Primary });
        var source = new Label { Text = "DATA BY BATRACE", AutoSize = true, ForeColor = Muted, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        source.Location = new Point(Width - 190, 21); bar.Resize += (_, _) => source.Left = bar.ClientSize.Width - source.Width - 22;
        bar.Controls.Add(source); return bar;
    }

    private void ShowDashboard()
    {
        ClearContent();
        lobbyPage = null; lobbyTitle = null; lobbySummary = null; lobbyTable = null; lobbyRows.Clear();
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
        page.Resize += (_, _) => { state.Width = page.ClientSize.Width - 56; body.Width = page.ClientSize.Width - 56; };
        page.Controls.AddRange([state, sectionTitle, body]); content.Controls.Add(page);
    }

    private void SetCaptureStatus(string text) { connection.Text = text; connection.ForeColor = text.Contains("실패") || text.Contains("필요") ? Danger : Muted; }

    private async Task LoadPlayersSafelyAsync(LobbySnapshot lobby)
    {
        try { await LoadPlayersAsync(lobby); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetCaptureStatus("플레이어 표시 복구 중: " + ex.Message); }
    }

    private async Task LoadPlayersAsync(LobbySnapshot lobby)
    {
        var names = lobby.Players;
        if (names.Count == 0)
        {
            lobbyLookup?.Cancel();
            lobbyLoadVersion++;
            if (lastNames.Length == 0) return;
            lastNames = [];
            lastRoomName = null;
            ShowDashboard();
            SetCaptureStatus("Broken Arrow 패킷 수신 중");
            return;
        }
        var normalized = names.Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray();
        if (normalized.SequenceEqual(lastNames, StringComparer.OrdinalIgnoreCase) &&
            string.Equals(lobby.RoomName, lastRoomName, StringComparison.OrdinalIgnoreCase) &&
            DateTime.UtcNow - lastLookupUtc < TimeSpan.FromSeconds(30)) return;

        var roomChanged = !string.Equals(lobby.RoomName, lastRoomName, StringComparison.OrdinalIgnoreCase);
        if (lobby.HasLobbyMarker && roomChanged && lobbyTable is not null)
            ShowLobby(lobby.RoomName, [], removeMissing: true);
        var loadVersion = ++lobbyLoadVersion;
        lobbyLookup?.Cancel();
        lobbyLookup?.Dispose();
        lobbyLookup = new CancellationTokenSource();
        var lookupToken = lobbyLookup.Token;
        lastLookupUtc = DateTime.UtcNow;
        lastNames = normalized;
        lastRoomName = lobby.RoomName;
        connection.Text = "플레이어 정보 조회 중";
        connection.ForeColor = Muted;
        var pending = normalized.Select(async name =>
        {
            try { return (Name: name, Player: await api.SearchAsync(name, lookupToken)); }
            catch (OperationCanceledException) { return (Name: name, Player: (PlayerResult?)null); }
            catch { return (Name: name, Player: (PlayerResult?)null); }
        }).ToList();
        var verified = new List<(string Name, PlayerResult? Player)>();
        var completedResults = new Dictionary<string, PlayerResult?>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            var result = await completed;
            if (lookupToken.IsCancellationRequested || loadVersion != lobbyLoadVersion) return;
            completedResults[result.Name] = result.Player;
            if (result.Player is not null && verified.Count < 12)
            {
                verified.Add(result);
                ShowLobby(lobby.RoomName, verified.ToArray(), removeMissing: roomChanged && verified.Count == 1);
            }
        }
        var orderedRoster = normalized.Select(name =>
        {
            completedResults.TryGetValue(name, out var player);
            return (Name: name, Player: player);
        }).Where(item => item.Player is not null).Take(12).ToArray();
        verified.Clear();
        verified.AddRange(orderedRoster);
        if (verified.Count == 0)
        {
            connection.Text = "BATrace 조회 결과 없음";
            connection.ForeColor = Muted;
            return;
        }
        connection.Text = "로비 연결됨";
        connection.ForeColor = Accent;
        ShowLobby(lobby.RoomName, verified.ToArray(), removeMissing: true);
    }

    private void ShowLobby(string? roomName, (string Name, PlayerResult? Player)[] players, bool removeMissing)
    {
        EnsureLobbyView();
        var table = lobbyTable!;
        var verified = players.Where(item => item.Player is not null).Select(item => item.Player!).ToArray();
        if (!removeMissing)
        {
            table.SuspendLayout();
            foreach (var player in verified)
            {
                var key = PlayerKey(player);
                if (!lobbyRows.TryGetValue(key, out var row))
                {
                    row = CreateLobbyRow(player);
                    lobbyRows[key] = row;
                    var targetRow = lobbyRows.Count;
                    for (var column = 0; column < row.Controls.Length; column++) table.Controls.Add(row.Controls[column], column, targetRow);
                    table.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
                }
                UpdateLobbyRow(row, player);
            }
            table.RowCount = lobbyRows.Count + 1;
            table.Height = 47 + lobbyRows.Count * 62;
            lobbyTitle!.Text = string.IsNullOrWhiteSpace(roomName) ? "현재 로비" : roomName;
            lobbySummary!.Text = $"{lobbyRows.Count}명 · BATrace 확인 완료";
            lobbyPage!.AutoScrollMinSize = new Size(0, table.Bottom + 28);
            table.ResumeLayout(true);
            return;
        }
        var desiredKeys = verified.Select(PlayerKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        table.SuspendLayout();
        foreach (var key in lobbyRows.Keys.Where(key => !desiredKeys.Contains(key)).ToArray())
        {
            var stale = lobbyRows[key];
            foreach (var control in stale.Controls) { table.Controls.Remove(control); control.Dispose(); }
            lobbyRows.Remove(key);
        }

        table.RowStyles.Clear();
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        for (var index = 0; index < verified.Length; index++)
        {
            var player = verified[index];
            var key = PlayerKey(player);
            if (!lobbyRows.TryGetValue(key, out var row))
            {
                row = CreateLobbyRow(player);
                lobbyRows[key] = row;
                for (var column = 0; column < row.Controls.Length; column++) table.Controls.Add(row.Controls[column], column, index + 1);
            }
            UpdateLobbyRow(row, player);
            for (var column = 0; column < row.Controls.Length; column++) table.SetCellPosition(row.Controls[column], new TableLayoutPanelCellPosition(column, index + 1));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        }

        table.RowCount = verified.Length + 1;
        table.Height = 47 + verified.Length * 62;
        lobbyTitle!.Text = string.IsNullOrWhiteSpace(roomName) ? "현재 로비" : roomName;
        lobbySummary!.Text = $"{verified.Length}명 · BATrace 확인 완료";
        lobbyPage!.AutoScrollMinSize = new Size(0, table.Bottom + 28);
        table.ResumeLayout(true);
    }

    private void EnsureLobbyView()
    {
        if (lobbyTable is not null) return;
        ClearContent();
        lobbyPage = new Panel { Dock = DockStyle.Fill, BackColor = Canvas, AutoScroll = true };
        lobbyTitle = new Label { AutoSize = true, Location = new Point(28, 24), Font = new Font("Bahnschrift SemiBold", 20f), ForeColor = Primary };
        lobbySummary = new Label { AutoSize = true, Location = new Point(30, 61), ForeColor = Muted };
        lobbyTable = new TableLayoutPanel { Location = new Point(28, 100), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Surface, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single, ColumnCount = 9, RowCount = 1,
            Size = new Size(lobbyPage.ClientSize.Width - 56, 47) };
        lobbyTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        foreach (var width in new[] { 24f, 10f, 11f, 8f, 12f, 10f, 10f, 15f }) lobbyTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
        lobbyTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        string[] headers = ["플레이어", "ELO", "세계 순위", "레벨", "랭크 경기", "K/D", "Turtle", "Steam ID", ""];
        for (var column = 0; column < headers.Length; column++) lobbyTable.Controls.Add(TableLabel(headers[column], true), column, 0);
        lobbyPage.Resize += (_, _) => { if (lobbyTable is not null) lobbyTable.Width = lobbyPage.ClientSize.Width - 56; };
        lobbyPage.Controls.AddRange([lobbyTitle, lobbySummary, lobbyTable]);
        content.Controls.Add(lobbyPage);
    }

    private static LobbyRow CreateLobbyRow(PlayerResult player)
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
        var open = MakeButton("보기", (sender, _) => OpenPlayer((sender as Button)?.Tag as PlayerResult));
        open.Margin = new Padding(8, 12, 8, 10);
        return new LobbyRow(panel, name, avatar, values, open);
    }

    private static void UpdateLobbyRow(LobbyRow row, PlayerResult player)
    {
        row.Name.Text = player.Name;
        string[] values = [FormatElo(player.Elo), player.GlobalRank is int rank ? $"#{rank:N0}" : "—",
            player.Level?.ToString() ?? "—", player.RatingGames?.ToString("N0") ?? "—", player.Kd?.ToString("0.00") ?? "—",
            player.TurtleIndex?.ToString("0.0") ?? "—", ShortSteam(player.SteamId)];
        for (var index = 0; index < values.Length; index++) row.Values[index].Text = values[index];
        row.Open.Tag = player;
        row.Open.Enabled = player.Source != "Packet";
        if (!string.Equals(row.AvatarUrl, player.AvatarUrl, StringComparison.Ordinal))
        {
            row.AvatarUrl = player.AvatarUrl;
            row.Avatar.Tag = player.AvatarUrl;
            if (string.IsNullOrWhiteSpace(player.AvatarUrl)) { row.Avatar.Image?.Dispose(); row.Avatar.Image = null; }
            else _ = LoadAvatarAsync(row.Avatar, player.AvatarUrl);
        }
    }

    private static string PlayerKey(PlayerResult player) => player.SteamId ?? player.Id;

    private void ClearContent()
    {
        connection.Parent?.Controls.Remove(connection);
        var controls = content.Controls.Cast<Control>().ToArray();
        content.Controls.Clear();
        foreach (var control in controls) control.Dispose();
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

    private sealed class LobbyRow(Panel playerCell, Label name, PictureBox avatar, Label[] values, Button open)
    {
        public Panel PlayerCell { get; } = playerCell;
        public Label Name { get; } = name;
        public PictureBox Avatar { get; } = avatar;
        public Label[] Values { get; } = values;
        public Button Open { get; } = open;
        public string? AvatarUrl { get; set; }
        public Control[] Controls => [PlayerCell, .. Values, Open];
    }
}
