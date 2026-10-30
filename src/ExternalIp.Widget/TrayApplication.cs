using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using ExternalIp.Core;
using Microsoft.Win32;

namespace ExternalIp.Widget;

internal sealed class TrayApplication : ApplicationContext
{
    private readonly Control _dispatcher = new();
    private readonly WidgetForm _widget = new();
    private readonly NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _visibility;
    private readonly ToolStripMenuItem _copy;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly System.Windows.Forms.Timer _networkDelay = new() { Interval = 2000 };
    private readonly SettingsStore _store;
    private readonly StateStore _stateStore;
    private readonly Ipv4HttpTransport _transport = new();
    private readonly ServiceLookups _lookups;
    private readonly UpdateEngine _engine;
    private readonly RegisteredWaitHandle _activation;
    private readonly RegisteredWaitHandle _settingsActivation;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _cycle;
    private Task _refreshTask = Task.CompletedTask;
    private AppSettings _settings;
    private SettingsForm? _settingsForm;
    private bool _refreshing, _pending, _exiting, _applying;
    private string? _storageWarning;

    public TrayApplication(string profile, EventWaitHandle signal, EventWaitHandle settingsSignal, bool openSettings = false)
    {
        _ = _dispatcher.Handle;
        _store = new SettingsStore(Path.Combine(profile, "settings.json"));
        _stateStore = new StateStore(Path.Combine(profile, "last-data.json"));
        _settings = _store.Load();
        _storageWarning = _store.Warning;
        try { _settings.AutoStart = StartupManager.IsEnabled(); } catch (System.Security.SecurityException) { }
        _lookups = new ServiceLookups(_transport, new RateLimiter(Path.Combine(profile, "api-usage.json")));
        _engine = new UpdateEngine(_lookups, _lookups);
        if (_stateStore.Load() is { } cached) _engine.Restore(cached);
        if (!_settings.GeoEnabled) _engine.DisableGeo();
        _icon = CreateTrayIcon(); _widget.Icon = _icon;
        _visibility = new ToolStripMenuItem("", null, (_, _) => ToggleWidget());
        _copy = new ToolStripMenuItem("Скопировать IP-адрес", null, (_, _) => CopyIp());
        _menu.Items.AddRange([_visibility, _copy,
            new ToolStripMenuItem("Обновить сейчас", null, (_, _) => RequestRefresh()),
            new ToolStripMenuItem("Настройки", null, (_, _) => ShowSettings()),
            new ToolStripSeparator(), new ToolStripMenuItem("Выход", null, (_, _) => ExitThread())]);
        _tray = new NotifyIcon { Icon = _icon, Visible = true, ContextMenuStrip = _menu, Text = "External IP Widget" };
        _tray.DoubleClick += (_, _) => ShowWidget();
        _widget.CopyRequested += CopyIp; _widget.RefreshRequested += RequestRefresh;
        _widget.HideRequested += ToggleWidget; _widget.SettingsRequested += ShowSettings;
        _widget.PositionCommitted += SavePosition;
        _engine.Changed += StateChanged;
        _widget.TopMost = _settings.AlwaysOnTop;
        var area = Screen.PrimaryScreen!.WorkingArea;
        _widget.Location = new Point(_settings.WidgetX ?? area.Right - _widget.Width - 24,
            _settings.WidgetY ?? area.Bottom - _widget.Height - 24);
        _widget.ClampToScreens();
        if (_settings.WidgetVisible) { _widget.Show(); _widget.ClampToScreens(); }
        _timer.Interval = _settings.RefreshSeconds * 1000;
        _timer.Tick += (_, _) => RequestRefresh(); _timer.Start();
        _networkDelay.Tick += (_, _) => { _networkDelay.Stop(); RequestRefresh(); };
        NetworkChange.NetworkAddressChanged += NetworkChanged;
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.SessionEnding += SessionEnding;
        _activation = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => Post(ShowWidget), null, Timeout.Infinite, false);
        _settingsActivation = ThreadPool.RegisterWaitForSingleObject(settingsSignal, (_, _) => Post(ShowSettings), null, Timeout.Infinite, false);
        Render(_engine.State);
        Post(RequestRefresh);
        if (openSettings) Post(ShowSettings);
    }

    private void Post(Action action)
    {
        if (_exiting || _dispatcher.IsDisposed) return;
        try { _dispatcher.BeginInvoke(() => { if (!_exiting) action(); }); }
        catch (InvalidOperationException) { }
    }
    private void StateChanged(WidgetState state) => Post(() => Render(state));
    private void Render(WidgetState state)
    {
        _widget.Render(state, _settings.GeoEnabled);
        var tip = state.Tooltip(_settings.GeoEnabled);
        _tray.Text = tip.Length <= 127 ? tip : tip[..126] + "…";
        _visibility.Text = _widget.Visible ? "Скрыть виджет" : "Показать виджет";
        _copy.Enabled = state.Ip is not null;
    }
    private void ShowWidget()
    {
        _settings.WidgetVisible = true; _widget.ClampToScreens(); _widget.Show();
        Render(_engine.State); SaveSettings();
    }
    private void ToggleWidget()
    {
        if (!_widget.Visible) { ShowWidget(); return; }
        _widget.Hide(); _settings.WidgetVisible = false; Render(_engine.State); SaveSettings();
    }
    private void SavePosition()
    {
        _settings.WidgetX = _widget.Left; _settings.WidgetY = _widget.Top; SaveSettings();
    }
    private void SaveSettings()
    {
        try { _store.Save(_settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        { ReportStorage(e); }
    }
    private void ReportStorage(Exception e)
    {
        if (_storageWarning is null) _tray.ShowBalloonTip(4000, "Настройки не сохранены", e.Message, ToolTipIcon.Warning);
        _storageWarning = e.Message;
    }
    private void CopyIp()
    {
        if (_engine.State.Ip is not { } ip) return;
        try { Clipboard.SetText(ip, TextDataFormat.UnicodeText); }
        catch (ExternalException) { _tray.ShowBalloonTip(2500, "Буфер обмена занят", "Повторите копирование.", ToolTipIcon.Info); }
    }
    private void RequestRefresh()
    {
        if (_exiting || _applying) return;
        if (_refreshing) { _pending = true; return; }
        _refreshTask = RefreshLoopAsync();
    }
    private async Task RefreshLoopAsync()
    {
        _refreshing = true; _timer.Stop();
        try
        {
            do
            {
                _pending = false;
                _cycle?.Dispose(); _cycle = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                try
                {
                    await _engine.RefreshAsync(_settings.Copy(), _cycle.Token);
                    try { _stateStore.Save(_engine.State); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ReportStorage(e); }
                }
                catch (OperationCanceledException) { }
            } while (_pending && !_exiting && !_applying);
        }
        finally { _refreshing = false; if (!_exiting && !_applying) _timer.Start(); }
    }
    private void NetworkChanged(object? sender, EventArgs e) => Post(() => { _networkDelay.Stop(); _networkDelay.Start(); });
    private void DisplayChanged(object? sender, EventArgs e) => Post(() => { _widget.ClampToScreens(); SavePosition(); });
    private void SessionEnding(object? sender, SessionEndingEventArgs e) => Post(ExitThread);

    private string Diagnostics() => $"Настройки: {_store.Path}\r\n\r\n{_engine.State.Details}\r\n\r\n{_storageWarning}\r\n{_lookups.Limiter.Warning}";
    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false }) { _settingsForm.Show(); _settingsForm.Activate(); return; }
        _settingsForm = new SettingsForm(_settings.Copy(), _lookups, () => _engine.State.Ip, Diagnostics, ApplyAsync) { Icon = _icon };
        _settingsForm.Show();
    }
    private async Task ApplyAsync(AppSettings updated)
    {
        updated.Validate();
        updated.WidgetX = _widget.Left; updated.WidgetY = _widget.Top;
        bool startupChanged = updated.AutoStart != _settings.AutoStart;
        if (startupChanged) StartupManager.SetEnabled(updated.AutoStart);
        try { _store.Save(updated); }
        catch { if (startupChanged) StartupManager.SetEnabled(_settings.AutoStart); throw; }
        _applying = true; _pending = false; _timer.Stop(); _cycle?.Cancel();
        try
        {
            await _refreshTask;
            _settings = updated.Copy();
            if (!_settings.GeoEnabled) _engine.DisableGeo();
            _widget.TopMost = _settings.AlwaysOnTop;
            if (_settings.WidgetVisible) { _widget.ClampToScreens(); _widget.Show(); } else _widget.Hide();
            _timer.Interval = _settings.RefreshSeconds * 1000;
            Render(_engine.State);
        }
        finally { _applying = false; if (!_exiting) { _timer.Start(); RequestRefresh(); } }
    }

    protected override void ExitThreadCore()
    {
        if (_exiting) return;
        SavePosition(); _exiting = true;
        _lifetime.Cancel(); _cycle?.Cancel(); _activation.Unregister(null); _settingsActivation.Unregister(null);
        NetworkChange.NetworkAddressChanged -= NetworkChanged;
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        SystemEvents.SessionEnding -= SessionEnding;
        _engine.Changed -= StateChanged;
        _timer.Stop(); _networkDelay.Stop();
        _settingsForm?.Close();
        _tray.Visible = false; _tray.Dispose();
        _widget.Dispose(); _menu.Dispose(); _icon.Dispose();
        _transport.Dispose(); _dispatcher.Dispose();
        base.ExitThreadCore();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _networkDelay.Dispose(); _lifetime.Dispose(); _cycle?.Dispose(); }
        base.Dispose(disposing);
    }
    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32); using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.FromArgb(19, 28, 44));
        using var pen = new Pen(Color.FromArgb(114, 235, 194), 2);
        graphics.DrawEllipse(pen, 4, 4, 24, 24); graphics.DrawEllipse(pen, 10, 4, 12, 24);
        graphics.DrawLine(pen, 4, 16, 28, 16); graphics.DrawArc(pen, 4, 9, 24, 14, 0, 180);
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint handle);
}
