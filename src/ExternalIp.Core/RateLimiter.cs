using System.Text.Json;

namespace ExternalIp.Core;

public sealed class UsageEntry
{
    public DateTimeOffset NextAllowed { get; set; }
    public int Failures { get; set; }
    public List<DateTimeOffset> Requests { get; set; } = [];
}

// Host-scoped budget shared by automatic refresh, manual refresh and service tests.
// A rolling 24-hour window avoids quota resets on application restart or midnight.
public sealed class RateLimiter
{
    private readonly Dictionary<string, UsageEntry> _entries;
    private readonly string? _path;
    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    public string? Warning { get; private set; }

    public RateLimiter(string? path = null, TimeProvider? clock = null)
    {
        _path = path; _clock = clock ?? TimeProvider.System;
        _entries = new(StringComparer.OrdinalIgnoreCase);
        if (path is null) return;
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var entries = JsonSerializer.Deserialize<Dictionary<string, UsageEntry>>(File.ReadAllText(candidate), JsonFile.Options)
                    ?? throw new FormatException("Пустые счётчики запросов.");
                if (entries.Any(e => e.Value is null || e.Value.Requests is null)) throw new FormatException("Повреждённые счётчики.");
                _entries = new(entries, StringComparer.OrdinalIgnoreCase);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException)
            { Warning = "Не удалось прочитать счётчики API: " + e.Message; }
        }
        // If persisted budgets were damaged, avoid immediately exhausting a daily API.
        if (Warning is not null) _entries["ipwho.is"] = new() { NextAllowed = _clock.GetUtcNow().AddHours(24) };
    }

    private void Persist()
    {
        if (_path is null) return;
        try { JsonFile.Save(_path, _entries); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Warning = "Не удалось сохранить счётчики API: " + e.Message; }
    }

    public bool TryAcquire(Uri uri, int minSeconds, int dailyLimit, out string reason)
    {
        lock (_sync)
        {
            var key = uri.IdnHost;
            if (key.Equals("ipwho.is", StringComparison.OrdinalIgnoreCase))
            { minSeconds = Math.Max(120, minSeconds); dailyLimit = dailyLimit == 0 ? 900 : Math.Min(900, dailyLimit); }
            if (key.Equals("get.geojs.io", StringComparison.OrdinalIgnoreCase)) minSeconds = Math.Max(60, minSeconds);
            minSeconds = Math.Max(10, minSeconds);
            var now = _clock.GetUtcNow();
            if (!_entries.TryGetValue(key, out var entry)) _entries[key] = entry = new();
            entry.Requests.RemoveAll(t => t <= now.AddHours(-24));
            if (now < entry.NextAllowed)
            { reason = $"Пауза API до {entry.NextAllowed.ToLocalTime():dd.MM HH:mm:ss}."; return false; }
            if (dailyLimit > 0 && entry.Requests.Count >= dailyLimit)
            { reason = "Достигнут лимит запросов за последние 24 часа."; return false; }
            entry.NextAllowed = now.AddSeconds(minSeconds);
            if (dailyLimit > 0) entry.Requests.Add(now);
            Persist(); reason = ""; return true;
        }
    }

    public void Complete(Uri uri, bool success, TimeSpan? retryAfter = null)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(uri.IdnHost, out var entry)) return;
            if (success) entry.Failures = 0;
            else
            {
                entry.Failures = Math.Min(10, entry.Failures + 1);
                var delay = retryAfter ?? TimeSpan.FromSeconds(Math.Min(900, 15 * Math.Pow(2, entry.Failures - 1)));
                if (delay < TimeSpan.FromSeconds(10)) delay = TimeSpan.FromSeconds(10);
                if (delay > TimeSpan.FromDays(7)) delay = TimeSpan.FromDays(7);
                var next = _clock.GetUtcNow() + delay;
                if (next > entry.NextAllowed) entry.NextAllowed = next;
            }
            Persist();
        }
    }
}
