using System.Text.Json;

namespace ExternalIp.Core;

public sealed record WidgetState
{
    public string? Ip { get; init; }
    public GeoLocation? Location { get; init; }
    public bool IpStale { get; init; }
    public bool GeoStale { get; init; }
    public DateTimeOffset? LastSuccess { get; init; }
    public DateTimeOffset? GeoSuccess { get; init; }
    public bool Busy { get; init; }
    public string Details { get; init; } = "";
    public string IpDisplay => Ip ?? "IP недоступен";
    public string LocationDisplay(bool enabled) => !enabled ? "Геолокация выключена" :
        Location is null ? "Местоположение не определено" : Location.Display + (GeoStale ? " · устарело" : "");
    public string Tooltip(bool enabled) => $"{IpDisplay}{(IpStale ? " · Данные устарели" : "")}\n{LocationDisplay(enabled)}";
}

public sealed class UpdateEngine(IIpLookup ipLookup, IGeoLookup geoLookup, TimeProvider? clock = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public WidgetState State { get; private set; } = new();
    public event Action<WidgetState>? Changed;

    public void Restore(WidgetState state) => Publish(state with { Busy = false, IpStale = state.Ip is not null, GeoStale = state.Location is not null });
    public void DisableGeo() => Publish(State with { Location = null, GeoSuccess = null, GeoStale = false });
    private void Publish(WidgetState state) { State = state; Changed?.Invoke(state); }

    public async Task<bool> RefreshAsync(AppSettings settings, CancellationToken token = default)
    {
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false)) return false;
        try
        {
            Publish(State with { Busy = true });
            var ip = await ipLookup.FindAsync(settings, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (ip.Value is null)
            {
                Publish(State with { IpStale = State.Ip is not null, Details = ip.Details });
                return true;
            }
            var sameIp = State.Ip == ip.Value;
            // Publish the new address with NO previous address's location before the geo await.
            Publish(State with
            {
                Ip = ip.Value, IpStale = false, LastSuccess = _clock.GetUtcNow(), Details = ip.Details,
                Location = sameIp && settings.GeoEnabled ? State.Location : null,
                GeoSuccess = sameIp && settings.GeoEnabled ? State.GeoSuccess : null,
                GeoStale = sameIp && settings.GeoEnabled && State.GeoStale
            });
            if (!settings.GeoEnabled) return true;
            var geo = await geoLookup.FindAsync(ip.Value, settings, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (geo.Value is not null)
                Publish(State with { Location = geo.Value, GeoStale = false, GeoSuccess = _clock.GetUtcNow(), Details = ip.Details + "\n" + geo.Details });
            else
                Publish(State with
                {
                    GeoStale = State.Location is not null && (geo.HadErrors || !geo.Deferred || State.GeoStale),
                    Details = ip.Details + "\n" + geo.Details
                });
            return true;
        }
        finally { Publish(State with { Busy = false }); _gate.Release(); }
    }
}

public sealed class StateStore(string path)
{
    public WidgetState? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var state = JsonSerializer.Deserialize<WidgetState>(File.ReadAllText(path), JsonFile.Options);
            return state is not null && PublicIpv4.IsValid(state.Ip) ? state : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public void Save(WidgetState state) => JsonFile.Save(path, state with { Busy = false });
}
