using ExternalIp.Core;

namespace ExternalIp.Widget;

internal sealed class ServiceEditor : Form
{
    private readonly Dictionary<string, TextBox> _fields = [];
    public ServiceEditor(object service, bool geo)
    {
        Text = geo ? "Геосервис" : "IP-сервис"; Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, geo ? 680 : 540); MinimumSize = new Size(630, 480); ShowInTaskbar = false;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, AutoScroll = true };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int row = 0;
        void Add(string label, Control control)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 8, 10) }, 0, row);
            control.Margin = new Padding(0, 3, 0, 8); root.Controls.Add(control, 1, row++);
        }
        TextBox Field(string key, string label, string value)
        {
            var text = new TextBox { Text = value, Dock = DockStyle.Fill }; _fields.Add(key, text); Add(label, text); return text;
        }
        string name, url; bool enabled; int pause, limit;
        if (service is IpService ip) { name = ip.Name; url = ip.Url; enabled = ip.Enabled; pause = ip.MinIntervalSeconds; limit = ip.DailyLimit; }
        else { var gs = (GeoService)service; name = gs.Name; url = gs.UrlTemplate; enabled = gs.Enabled; pause = gs.MinIntervalSeconds; limit = gs.DailyLimit; }
        Field("name", "Название", name); Field("url", geo ? "HTTPS URL с {ip}" : "HTTPS URL", url);
        var enabledBox = new CheckBox { Checked = enabled, Text = "Включён", AutoSize = true }; Add("Состояние", enabledBox);
        var format = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        format.Items.AddRange(["Обычный текст", "JSON"]);
        if (service is IpService ips)
        {
            format.SelectedIndex = ips.Format == IpResponseFormat.Text ? 0 : 1; Add("Формат ответа", format);
            var path = Field("ip", "JSON-поле IPv4", ips.IpPath);
            path.Enabled = format.SelectedIndex == 1; format.SelectedIndexChanged += (_, _) => path.Enabled = format.SelectedIndex == 1;
        }
        else
        {
            var gs = (GeoService)service;
            Field("country", "JSON-поле страны", gs.CountryPath); Field("region", "JSON-поле региона", gs.RegionPath);
            Field("city", "JSON-поле города", gs.CityPath); Field("responseIp", "IPv4 в ответе (необяз.)", gs.ResponseIpPath);
            Field("success", "Поле true при успехе (необяз.)", gs.SuccessPath);
        }
        var pauseBox = new NumericUpDown { Minimum = 10, Maximum = 86400, Value = pause, Width = 140 };
        var limitBox = new NumericUpDown { Minimum = 0, Maximum = 1000000, Value = limit, Width = 140 };
        Add("Минимальная пауза, секунд", pauseBox); Add("Запросов за 24 ч (0 = без лимита)", limitBox);
        var help = new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(660, 0),
            Text = "Пути: data.ip, items.0.ip или JSON Pointer /data/ip. Пустое поле расположения пропускается. " +
                "IPv4 в геоответе проверяется на совпадение, если указан его путь. Поле успеха должно содержать JSON true.\n\n" +
                "ipwho.is: не чаще 120 с, до 900 запросов за 24 часа; GeoJS: не чаще 60 с. Эти пределы нельзя снизить. HTTP-переадресации не выполняются: задайте конечный HTTPS URL.",
            Margin = new Padding(0, 10, 0, 12)
        };
        root.Controls.Add(help, 0, row++); root.SetColumnSpan(help, 2);
        var actions = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(16, 8, 16, 12)
        };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        var save = new Button { Text = "Готово", AutoSize = true };
        actions.Controls.AddRange([cancel, save]);
        save.Click += (_, _) =>
        {
            string Value(string key) => _fields[key].Text.Trim();
            try
            {
                AppSettings.ValidateService(Value("name"), Value("url").Replace("{ip}", "8.8.8.8"), (int)pauseBox.Value, (int)limitBox.Value);
                if (service is IpService ipService)
                {
                    if (format.SelectedIndex == 1 && Value("ip").Length == 0) throw new FormatException("Укажите JSON-поле IPv4.");
                    ipService.Name = Value("name"); ipService.Url = Value("url"); ipService.Enabled = enabledBox.Checked;
                    ipService.MinIntervalSeconds = (int)pauseBox.Value; ipService.DailyLimit = (int)limitBox.Value;
                    ipService.Format = format.SelectedIndex == 0 ? IpResponseFormat.Text : IpResponseFormat.Json; ipService.IpPath = Value("ip");
                }
                else
                {
                    if (!Value("url").Contains("{ip}", StringComparison.Ordinal)) throw new FormatException("В URL должен быть {ip}.");
                    if (Value("country").Length + Value("region").Length + Value("city").Length == 0)
                        throw new FormatException("Укажите хотя бы одно поле расположения.");
                    var gs = (GeoService)service;
                    gs.Name = Value("name"); gs.UrlTemplate = Value("url"); gs.Enabled = enabledBox.Checked;
                    gs.MinIntervalSeconds = (int)pauseBox.Value; gs.DailyLimit = (int)limitBox.Value;
                    gs.CountryPath = Value("country"); gs.RegionPath = Value("region"); gs.CityPath = Value("city");
                    gs.ResponseIpPath = Value("responseIp"); gs.SuccessPath = Value("success");
                }
                DialogResult = DialogResult.OK; Close();
            }
            catch (FormatException e) { MessageBox.Show(this, e.Message, "Проверьте параметры", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        AcceptButton = save; CancelButton = cancel; Controls.Add(root); Controls.Add(actions);
    }
}
