using ExternalIp.Core;

namespace ExternalIp.Widget;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings _draft;
    private readonly Func<AppSettings, Task> _apply;
    private readonly NumericUpDown _interval = new() { Minimum = 10, Maximum = 86400, Width = 140 };
    private readonly NumericUpDown _timeout = new() { Minimum = 1, Maximum = 60, Width = 140 };
    private readonly CheckBox _geo = new() { Text = "Определять геолокацию публичного IP", AutoSize = true };
    private readonly CheckBox _visible = new() { Text = "Показывать виджет", AutoSize = true };
    private readonly CheckBox _top = new() { Text = "Поверх всех окон", AutoSize = true };
    private readonly CheckBox _startup = new() { Text = "Запускать при входе в Windows (для текущего пользователя)", AutoSize = true };
    private readonly Button _save = new() { Text = "Сохранить и закрыть", AutoSize = true };
    private readonly Button _applyButton = new() { Text = "Применить", AutoSize = true };
    private readonly Button _close = new() { Text = "Закрыть", AutoSize = true };
    private readonly Label _message = new() { AutoSize = true, ForeColor = Color.DarkGreen };
    private bool _saving;

    public SettingsForm(AppSettings settings, ServiceLookups lookups, Func<string?> currentIp,
        Func<string> diagnostics, Func<AppSettings, Task> apply)
    {
        _draft = settings; _apply = apply;
        Text = "External IP Widget · Настройки"; Name = "SettingsWindow";
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(850, 660);
        MinimumSize = new Size(740, 620);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), RowCount = 3, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var general = new TabPage("Общие") { Padding = new Padding(16), AutoScroll = true };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        _interval.Value = settings.RefreshSeconds; _timeout.Value = settings.TimeoutSeconds;
        _geo.Checked = settings.GeoEnabled; _visible.Checked = settings.WidgetVisible;
        _top.Checked = settings.AlwaysOnTop; _startup.Checked = settings.AutoStart;
        flow.Controls.Add(Paragraph("ДАННЫЕ И ПОДКЛЮЧЕНИЕ", true));
        flow.Controls.Add(NumberRow("Обновлять каждые", _interval, "секунд (10–86400)"));
        flow.Controls.Add(NumberRow("Тайм-аут запроса", _timeout, "секунд (1–60)"));
        flow.Controls.Add(_geo);
        flow.Controls.Add(Paragraph("Сервисам геолокации передаётся определённый публичный IPv4. Это приблизительное расположение адреса, а не компьютера. GPS и службы местоположения Windows не используются."));
        flow.Controls.Add(Paragraph("ВИДЖЕТ И ЗАПУСК", true));
        flow.Controls.Add(_visible); flow.Controls.Add(_top); flow.Controls.Add(_startup);
        flow.Controls.Add(Paragraph("Адрес относится к выходу конкретного запроса в интернет. VPN и маршруты разных интерфейсов могут давать разные адреса. Запросы идут напрямую через IPv4; системный HTTP-прокси не используется."));
        flow.Controls.Add(Paragraph("Паузы и лимиты сервисов действуют и для ручных проверок. Геоданные того же IP сохраняются на время обязательной паузы API; при ошибке они отмечаются как устаревшие. Настройки применяются без перезапуска."));
        general.Controls.Add(flow); tabs.TabPages.Add(general);
        var ipTab = new TabPage("IP-сервисы");
        ipTab.Controls.Add(new ServiceList<IpService>(_draft.IpServices, false, () => _geo.Checked,
            () => (int)_timeout.Value, currentIp, lookups)); tabs.TabPages.Add(ipTab);
        var geoTab = new TabPage("Геосервисы");
        geoTab.Controls.Add(new ServiceList<GeoService>(_draft.GeoServices, true, () => _geo.Checked,
            () => (int)_timeout.Value, currentIp, lookups)); tabs.TabPages.Add(geoTab);
        var diagnosticTab = new TabPage("Диагностика") { Padding = new Padding(10) };
        var diagnosticText = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Text = diagnostics() };
        var reload = new Button { Text = "Обновить диагностику", Dock = DockStyle.Bottom, Height = 36 };
        reload.Click += (_, _) => diagnosticText.Text = diagnostics();
        diagnosticTab.Controls.Add(diagnosticText); diagnosticTab.Controls.Add(reload); tabs.TabPages.Add(diagnosticTab);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange([_close, _save, _applyButton]);
        _applyButton.Click += async (_, _) => await SaveAsync(false);
        _save.Click += async (_, _) => await SaveAsync(true);
        _close.Click += (_, _) => Close(); CancelButton = _close;
        FormClosing += (_, e) => { if (_saving && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
        root.Controls.Add(tabs, 0, 0); root.Controls.Add(_message, 0, 1); root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
    }

    private static Label Paragraph(string text, bool heading = false) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(720, 0),
        Margin = new Padding(0, heading ? 16 : 8, 0, 10),
        Font = new Font("Segoe UI", heading ? 9 : 10, heading ? FontStyle.Bold : FontStyle.Regular),
        ForeColor = heading ? Color.FromArgb(34, 94, 118) : SystemColors.ControlText
    };
    private static FlowLayoutPanel NumberRow(string text, Control number, string units)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
        panel.Controls.Add(new Label { Text = text, Width = 175, AutoSize = false, Height = 30, TextAlign = ContentAlignment.MiddleLeft });
        panel.Controls.Add(number); panel.Controls.Add(new Label { Text = units, AutoSize = true, Margin = new Padding(8, 5, 0, 0) });
        return panel;
    }
    private async Task SaveAsync(bool close)
    {
        _saving = true; _save.Enabled = _applyButton.Enabled = _close.Enabled = false;
        try
        {
            _draft.RefreshSeconds = (int)_interval.Value; _draft.TimeoutSeconds = (int)_timeout.Value;
            _draft.GeoEnabled = _geo.Checked; _draft.WidgetVisible = _visible.Checked;
            _draft.AlwaysOnTop = _top.Checked; _draft.AutoStart = _startup.Checked;
            _draft.Validate(); await _apply(_draft.Copy());
            _message.Text = "Настройки применены.";
            if (close) { _saving = false; Close(); }
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { MessageBox.Show(this, e.Message, "Не удалось сохранить настройки", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { _saving = false; _save.Enabled = _applyButton.Enabled = _close.Enabled = true; }
    }
}
