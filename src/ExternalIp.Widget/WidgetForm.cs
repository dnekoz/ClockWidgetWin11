using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ExternalIp.Core;

namespace ExternalIp.Widget;

internal sealed class WidgetForm : Form
{
    private readonly Label _ip = new();
    private readonly Label _location = new();
    private readonly Label _time = new();
    private readonly Label _status = new();
    private readonly Button _copy = new();
    private readonly ToolTip _tip = new();
    private readonly Font _ipFont = new("Segoe UI", 24, FontStyle.Bold);
    private readonly Font _smallFont = new("Segoe UI", 8.5f);
    public event Action? CopyRequested;
    public event Action? RefreshRequested;
    public event Action? HideRequested;
    public event Action? SettingsRequested;
    public event Action? PositionCommitted;
    private static readonly Color Muted = Color.FromArgb(149, 165, 188);
    private static readonly Color Mint = Color.FromArgb(114, 235, 194);

    public WidgetForm()
    {
        Text = "External IP Widget"; Name = "DesktopWidget";
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual; AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(376, 208); BackColor = Color.FromArgb(19, 28, 44);
        ForeColor = Color.FromArgb(236, 242, 250); Font = new Font("Segoe UI", 9);
        DoubleBuffered = true;

        var header = new Label { Text = "●   ВНЕШНИЙ IPv4", Location = new(20, 16), Size = new(230, 20), ForeColor = Mint, Font = _smallFont };
        var settings = ActionButton("⚙", "Настройки", new(292, 9), new(34, 30));
        var hide = ActionButton("—", "Скрыть виджет", new(330, 9), new(30, 30));
        settings.Anchor = hide.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        hide.Click += (_, _) => HideRequested?.Invoke();
        _ip.SetBounds(17, 40, 343, 47); _ip.Font = _ipFont; _ip.Text = "IP недоступен";
        _ip.AutoEllipsis = true; _tip.SetToolTip(_ip, "Адрес выхода этого запроса в интернет. Перетащите виджет мышью.");
        _location.SetBounds(21, 96, 334, 38); _location.ForeColor = Muted; _location.AutoEllipsis = true;
        _location.Text = "Местоположение не определено";
        _status.SetBounds(21, 136, 334, 19); _status.Font = _smallFont; _status.ForeColor = Mint;
        _time.SetBounds(21, 171, 217, 20); _time.Font = _smallFont; _time.ForeColor = Muted;
        _copy.Text = "Копировать"; _copy.AccessibleName = "Скопировать IP-адрес";
        _copy.SetBounds(251, 163, 106, 29); _copy.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        StyleButton(_copy); _copy.ForeColor = Mint;
        _copy.Click += (_, _) => CopyRequested?.Invoke();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Скопировать IP-адрес", null, (_, _) => CopyRequested?.Invoke());
        menu.Items.Add("Обновить сейчас", null, (_, _) => RefreshRequested?.Invoke());
        menu.Items.Add("Настройки", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add("Скрыть виджет", null, (_, _) => HideRequested?.Invoke());
        ContextMenuStrip = menu;
        Controls.AddRange([header, settings, hide, _ip, _location, _status, _time, _copy]);
        foreach (var control in new Control[] { this, header, _ip, _location, _status, _time }) control.MouseDown += Drag;
        _ip.DoubleClick += (_, _) => CopyRequested?.Invoke();
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideRequested?.Invoke(); }
        };
    }

    private Button ActionButton(string text, string hint, Point at, Size size)
    {
        var button = new Button { Text = text, Location = at, Size = size, AccessibleName = hint };
        StyleButton(button); _tip.SetToolTip(button, hint); return button;
    }
    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0;
        button.BackColor = Color.FromArgb(30, 42, 61); button.ForeColor = Muted;
        button.Cursor = Cursors.Hand; button.UseVisualStyleBackColor = false;
    }
    public void Render(WidgetState state, bool geoEnabled)
    {
        _ip.Text = state.IpDisplay;
        _location.Text = state.LocationDisplay(geoEnabled);
        _tip.SetToolTip(_location, _location.Text + (state.GeoSuccess is { } geoTime ? $"\nГеоданные: {geoTime.ToLocalTime():dd.MM.yyyy HH:mm:ss}" : ""));
        _copy.Enabled = state.Ip is not null;
        _time.Text = state.LastSuccess is { } date ? $"Обновлено {date.ToLocalTime():dd.MM HH:mm:ss}" : "Успешных обновлений ещё нет";
        _status.Text = state.IpStale ? "Данные устарели" : state.Busy ? "Обновление…" : state.Ip is null ? "Ожидание подключения" : "Публичный IP · приблизительная геолокация";
        _status.ForeColor = state.IpStale ? Color.FromArgb(255, 198, 111) : Mint;
        _tip.SetToolTip(_status, state.Details);
        Invalidate();
    }

    public void ClampToScreens()
    {
        var area = Screen.FromRectangle(Bounds).WorkingArea;
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x80; p.ClassStyle |= 0x20000; return p; }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(49, 65, 86));
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        using var line = new Pen(Color.FromArgb(36, 51, 71));
        e.Graphics.DrawLine(line, 20 * DeviceDpi / 96, 156 * DeviceDpi / 96, ClientSize.Width - 20 * DeviceDpi / 96, 156 * DeviceDpi / 96);
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); ClampToScreens(); }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x232) { ClampToScreens(); PositionCommitted?.Invoke(); }
    }
    private void Drag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tip.Dispose(); _ipFont.Dispose(); _smallFont.Dispose(); ContextMenuStrip?.Dispose(); }
        base.Dispose(disposing);
    }
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int msg, nint wparam, nint lparam);
}
