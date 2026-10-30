using System.Text.Json;
using ExternalIp.Core;

namespace ExternalIp.Widget;

internal sealed class ServiceList<T> : UserControl where T : class, new()
{
    private readonly List<T> _services;
    private readonly bool _geo;
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _result = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button _test = new() { Text = "Проверить выбранный", AutoSize = true };
    private readonly TextBox _testIp = new() { Width = 155 };
    private readonly CancellationTokenSource _cancel = new();

    public ServiceList(List<T> services, bool geo, Func<bool> geoEnabled, Func<int> timeout,
        Func<string?> currentIp, ServiceLookups lookups)
    {
        _services = services; _geo = geo; Dock = DockStyle.Fill; Padding = new Padding(12);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 136));
        root.Controls.Add(new Label
        {
            Text = "Опрос сверху вниз до первого корректного ответа. ● включён, ○ выключен.\n" +
                (geo ? "URL обязан содержать {ip}. Передаются только публичные IPv4." : "HTTPS через IPv4. Поддерживаются текст и JSON."),
            AutoSize = true, Margin = new Padding(0, 0, 0, 10)
        }, 0, 0);
        root.Controls.Add(_list, 0, 1);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        Button AddButton(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true }; button.Click += (_, _) => action(); actions.Controls.Add(button); return button;
        }
        AddButton("Добавить", () => Edit(true)); AddButton("Изменить", () => Edit(false));
        AddButton("Удалить", () => { if (_list.SelectedIndex is var i && i >= 0) { _services.RemoveAt(i); Reload(Math.Min(i, _services.Count - 1)); } });
        AddButton("Вкл. / выкл.", () =>
        {
            if (_list.SelectedItem is IpService ip) ip.Enabled = !ip.Enabled;
            else if (_list.SelectedItem is GeoService gs) gs.Enabled = !gs.Enabled;
            Reload(_list.SelectedIndex);
        });
        AddButton("↑ Выше", () => Reorder(-1)); AddButton("↓ Ниже", () => Reorder(1));
        _list.DoubleClick += (_, _) => Edit(false); root.Controls.Add(actions, 0, 2);
        var testRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8) };
        testRow.Controls.Add(_test);
        if (geo)
        {
            testRow.Controls.Add(new Label { Text = "IPv4 для проверки:", AutoSize = true, Margin = new Padding(14, 7, 4, 0) });
            _testIp.Text = currentIp() ?? "8.8.8.8"; testRow.Controls.Add(_testIp);
        }
        _test.Click += async (_, _) =>
        {
            if (_list.SelectedItem is not T item) return;
            if (geo && !geoEnabled()) { _result.Text = "Включите геолокацию на вкладке «Общие» для проверки. Запрос не отправлен."; return; }
            _test.Enabled = false; _result.Text = "Проверка…";
            try
            {
                string text;
                if (item is IpService ip)
                {
                    var result = await lookups.TestIpAsync(ip, timeout(), _cancel.Token);
                    text = result.Value is { } value ? "IPv4: " + value + "\r\n" + result.Details : result.Details;
                }
                else
                {
                    var result = await lookups.TestGeoAsync((item as GeoService)!, _testIp.Text.Trim(), timeout(), _cancel.Token);
                    text = result.Value is { } value ? "Расположение: " + value.Display + "\r\n" + result.Details : result.Details;
                }
                if (!IsDisposed) _result.Text = text;
            }
            catch (OperationCanceledException) { }
            finally { if (!IsDisposed) _test.Enabled = true; }
        };
        root.Controls.Add(testRow, 0, 3); root.Controls.Add(_result, 0, 4); Controls.Add(root); Reload(0);
    }
    private void Reload(int selected)
    {
        _list.Items.Clear(); foreach (var service in _services) _list.Items.Add(service);
        if (selected >= 0 && selected < _services.Count) _list.SelectedIndex = selected;
    }
    private void Reorder(int direction)
    {
        int index = _list.SelectedIndex, target = index + direction;
        if (index < 0 || target < 0 || target >= _services.Count) return;
        (_services[index], _services[target]) = (_services[target], _services[index]); Reload(target);
    }
    private void Edit(bool add)
    {
        var selected = _list.SelectedIndex;
        if (!add && selected < 0) return;
        T item = add ? new T() : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(_services[selected], JsonFile.Options), JsonFile.Options)!;
        using var editor = new ServiceEditor(item, _geo);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        if (add) { _services.Add(item); selected = _services.Count - 1; } else _services[selected] = item;
        Reload(selected);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _cancel.Cancel(); _cancel.Dispose(); }
        base.Dispose(disposing);
    }
}
