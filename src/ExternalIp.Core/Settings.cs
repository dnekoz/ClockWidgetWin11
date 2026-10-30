using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExternalIp.Core;

public enum IpResponseFormat { Text, Json }

public sealed class IpService
{
    public string Name { get; set; } = "Новый IP-сервис";
    public bool Enabled { get; set; } = true;
    public string Url { get; set; } = "https://";
    public IpResponseFormat Format { get; set; }
    public string IpPath { get; set; } = "ip";
    public int MinIntervalSeconds { get; set; } = 10;
    public int DailyLimit { get; set; }
    public override string ToString() => $"{(Enabled ? "●" : "○")}  {Name}";
}

public sealed class GeoService
{
    public string Name { get; set; } = "Новый геосервис";
    public bool Enabled { get; set; } = true;
    public string UrlTemplate { get; set; } = "https://example.com/{ip}";
    public string CountryPath { get; set; } = "country";
    public string RegionPath { get; set; } = "region";
    public string CityPath { get; set; } = "city";
    public string ResponseIpPath { get; set; } = "ip";
    public string SuccessPath { get; set; } = "";
    public int MinIntervalSeconds { get; set; } = 60;
    public int DailyLimit { get; set; }
    public override string ToString() => $"{(Enabled ? "●" : "○")}  {Name}";
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public int RefreshSeconds { get; set; } = 60;
    public int TimeoutSeconds { get; set; } = 5;
    public bool GeoEnabled { get; set; } = true;
    public bool WidgetVisible { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool AutoStart { get; set; }
    public int? WidgetX { get; set; }
    public int? WidgetY { get; set; }
    public List<IpService> IpServices { get; set; } =
    [
        new() { Name = "ipify", Url = "https://api.ipify.org?format=json", Format = IpResponseFormat.Json },
        new() { Name = "icanhazip · Cloudflare", Url = "https://ipv4.icanhazip.com" },
        new() { Name = "Amazon CheckIP", Url = "https://checkip.amazonaws.com" }
    ];
    public List<GeoService> GeoServices { get; set; } =
    [
        new() { Name = "GeoJS", UrlTemplate = "https://get.geojs.io/v1/ip/geo/{ip}.json" },
        new() { Name = "ipwho.is", UrlTemplate = "https://ipwho.is/{ip}", SuccessPath = "success",
            MinIntervalSeconds = 120, DailyLimit = 900 }
    ];

    public AppSettings Copy() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonFile.Options), JsonFile.Options)!;

    public void Validate()
    {
        if (Version != 1) throw new FormatException("Неизвестная версия настроек.");
        if (RefreshSeconds is < 10 or > 86400) throw new FormatException("Интервал обновления: от 10 до 86400 секунд.");
        if (TimeoutSeconds is < 1 or > 60) throw new FormatException("Тайм-аут: от 1 до 60 секунд.");
        if (IpServices is null || GeoServices is null || IpServices.Count > 50 || GeoServices.Count > 50)
            throw new FormatException("Допустимо до 50 сервисов в каждом списке.");
        if (!IpServices.Any(s => s is not null && s.Enabled)) throw new FormatException("Включите хотя бы один IP-сервис.");
        foreach (var s in IpServices)
        {
            if (s is null) throw new FormatException("Пустой IP-сервис.");
            ValidateService(s.Name, s.Url, s.MinIntervalSeconds, s.DailyLimit);
            if (!Enum.IsDefined(s.Format)) throw new FormatException("Неизвестный формат IP-ответа.");
            if (s.Format == IpResponseFormat.Json && string.IsNullOrWhiteSpace(s.IpPath))
                throw new FormatException("Укажите путь к IPv4 в JSON.");
        }
        foreach (var s in GeoServices)
        {
            if (s is null) throw new FormatException("Пустой геосервис.");
            if (s.UrlTemplate is null || !s.UrlTemplate.Contains("{ip}", StringComparison.Ordinal))
                throw new FormatException("URL геосервиса должен содержать {ip}.");
            ValidateService(s.Name, s.UrlTemplate.Replace("{ip}", "8.8.8.8"), s.MinIntervalSeconds, s.DailyLimit);
            if (string.IsNullOrWhiteSpace(s.CountryPath) && string.IsNullOrWhiteSpace(s.RegionPath) && string.IsNullOrWhiteSpace(s.CityPath))
                throw new FormatException("Укажите хотя бы одно поле расположения.");
        }
    }

    public static void ValidateService(string name, string url, int interval, int dailyLimit)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new FormatException("Название сервиса: 1–80 символов.");
        ValidateHttps(url);
        if (interval is < 10 or > 86400) throw new FormatException("Пауза сервиса: от 10 до 86400 секунд.");
        if (dailyLimit is < 0 or > 1000000) throw new FormatException("Лимит за 24 часа: 0–1000000 (0 — без лимита).");
    }

    public static Uri ValidateHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new FormatException("Нужен полный HTTPS URL без логина, пароля и фрагмента #.");
        return uri;
    }
}

public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value, Options);
                file.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class SettingsStore(string path)
{
    public string Path { get; } = path;
    public string? Warning { get; private set; }
    public AppSettings Load()
    {
        Warning = null;
        if (!File.Exists(Path) && !File.Exists(Path + ".bak")) return new();
        foreach (var candidate in new[] { Path, Path + ".bak" })
        {
            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(candidate), JsonFile.Options)
                    ?? throw new FormatException("Пустой файл настроек.");
                settings.Validate();
                if (candidate != Path) Warning = "Настройки восстановлены из резервной копии: " + Path + ".bak";
                return settings;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException)
            { Warning = "Не удалось прочитать настройки; использованы значения по умолчанию. " + e.Message; }
        }
        return new();
    }
    public void Save(AppSettings settings) { settings.Validate(); JsonFile.Save(Path, settings); }
}
