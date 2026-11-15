namespace BrokenArrowSquadTracker;

public sealed class ReasonDialog : Form
{
    private static readonly Color Canvas = Color.FromArgb(14, 17, 16), Surface = Color.FromArgb(23, 27, 25);
    private static readonly Color Border = Color.FromArgb(57, 65, 60), Primary = Color.FromArgb(239, 241, 239), Muted = Color.FromArgb(151, 160, 154);
    private static readonly Color Accent = Color.FromArgb(196, 220, 84), Danger = Color.FromArgb(224, 112, 96);
    private readonly TextBox reason;
    public bool RemoveRequested { get; private set; }
    public string Reason => reason.Text.Trim();

    public ReasonDialog(string playerName, PlayerNote? existing)
    {
        Text = "비추천 기록"; ClientSize = new Size(470, 270); MinimumSize = MaximumSize = Size;
        StartPosition = FormStartPosition.CenterParent; BackColor = Canvas; ForeColor = Primary;
        Font = new Font("Bahnschrift", 10f); FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;

        Controls.Add(new Label { Text = playerName, AutoSize = true, Location = new Point(24, 22), ForeColor = Primary,
            Font = new Font("Bahnschrift SemiBold", 14f) });
        Controls.Add(new Label { Text = "다음에 같은 플레이어를 만났을 때 표시할 사유", AutoSize = true,
            Location = new Point(25, 55), ForeColor = Muted });
        reason = new TextBox { Multiline = true, Location = new Point(24, 84), Size = new Size(422, 104),
            BackColor = Surface, ForeColor = Primary, BorderStyle = BorderStyle.FixedSingle,
            Text = existing?.Reason ?? string.Empty, MaxLength = 300, ScrollBars = ScrollBars.Vertical };
        Controls.Add(reason);

        var save = Button("저장", Accent, Canvas); save.Location = new Point(366, 210);
        save.Click += (_, _) => { if (Reason.Length > 0) { DialogResult = DialogResult.OK; Close(); } };
        var cancel = Button("취소", Surface, Primary); cancel.Location = new Point(276, 210);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.AddRange([cancel, save]);
        if (existing is not null)
        {
            var remove = Button("기록 삭제", Surface, Danger); remove.Location = new Point(24, 210); remove.Width = 96;
            remove.FlatAppearance.BorderColor = Danger;
            remove.Click += (_, _) => { RemoveRequested = true; DialogResult = DialogResult.OK; Close(); };
            Controls.Add(remove);
        }
        AcceptButton = save; CancelButton = cancel;
    }

    private static Button Button(string text, Color back, Color fore)
    {
        var button = new Button { Text = text, Size = new Size(80, 34), FlatStyle = FlatStyle.Flat,
            BackColor = back, ForeColor = fore, Font = new Font("Bahnschrift SemiBold", 9f) };
        button.FlatAppearance.BorderColor = Border;
        return button;
    }
}
