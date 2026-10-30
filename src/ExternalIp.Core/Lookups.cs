using System.Text.Json;

namespace ExternalIp.Core;

public sealed record LookupResult<T>(T? Value, string Details = "", bool Deferred = false, bool HadErrors = false) where T : class;
public interface IIpLookup
{
    Task<LookupResult<string>> FindAsync(AppSettings settings, CancellationToken token);
}
public interface IGeoLookup
{
    Task<LookupResult<GeoLocation>> FindAsync(string ip, AppSettings settings, CancellationToken token);
}

public sealed class ServiceLookups(IHttpTransport transport, RateLimiter limiter) : IIpLookup, IGeoLookup
{
    public RateLimiter Limiter { get; } = limiter;

    private async Task<LookupResult<T>> Request<T>(string name, string url, int pause, int limit,
        int timeout, Func<string, T> parse, CancellationToken token) where T : class
    {
        Uri uri;
        try { uri = AppSettings.ValidateHttps(url); }
        catch (FormatException e) { return new(null, name + ": " + e.Message, HadErrors: true); }
        if (!Limiter.TryAcquire(uri, pause, limit, out var reason)) return new(null, name + ": " + reason, Deferred: true);
        try
        {
            var body = await transport.GetAsync(uri, TimeSpan.FromSeconds(timeout), token).ConfigureAwait(false);
            var value = parse(body);
            Limiter.Complete(uri, true);
            return new(value, name + ": успешно.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException or JsonException or
            FormatException or InvalidOperationException or ServiceHttpException)
        {
            Limiter.Complete(uri, false, (e as ServiceHttpException)?.RetryAfter);
            return new(null, name + ": " + e.Message, HadErrors: true);
        }
    }

    public Task<LookupResult<string>> TestIpAsync(IpService service, int timeout, CancellationToken token) =>
        Request(service.Name, service.Url, service.MinIntervalSeconds, service.DailyLimit, timeout,
            body => ResponseParser.Ip(body, service), token);

    public Task<LookupResult<GeoLocation>> TestGeoAsync(GeoService service, string ip, int timeout, CancellationToken token)
    {
        if (!PublicIpv4.IsValid(ip)) return Task.FromResult(new LookupResult<GeoLocation>(null, "Нет публичного IPv4 для проверки.", HadErrors: true));
        if (!service.UrlTemplate.Contains("{ip}", StringComparison.Ordinal))
            return Task.FromResult(new LookupResult<GeoLocation>(null, "В URL отсутствует {ip}.", HadErrors: true));
        return Request(service.Name, service.UrlTemplate.Replace("{ip}", ip), service.MinIntervalSeconds, service.DailyLimit,
            timeout, body => ResponseParser.Geo(body, service, ip), token);
    }

    public async Task<LookupResult<string>> FindAsync(AppSettings settings, CancellationToken token)
    {
        List<string> errors = []; bool hadErrors = false; bool deferred = false;
        foreach (var service in settings.IpServices.Where(s => s.Enabled))
        {
            token.ThrowIfCancellationRequested();
            var result = await TestIpAsync(service, settings.TimeoutSeconds, token).ConfigureAwait(false);
            errors.Add(result.Details);
            if (result.Value is not null) return result with { Details = string.Join(Environment.NewLine, errors) };
            hadErrors |= result.HadErrors; deferred |= result.Deferred;
        }
        return new(null, errors.Count == 0 ? "Нет включённых IP-сервисов." : string.Join(Environment.NewLine, errors), deferred, hadErrors);
    }

    public async Task<LookupResult<GeoLocation>> FindAsync(string ip, AppSettings settings, CancellationToken token)
    {
        if (!settings.GeoEnabled) return new(null, "Геолокация выключена.");
        List<string> errors = []; bool hadErrors = false; bool deferred = false;
        foreach (var service in settings.GeoServices.Where(s => s.Enabled))
        {
            token.ThrowIfCancellationRequested();
            var result = await TestGeoAsync(service, ip, settings.TimeoutSeconds, token).ConfigureAwait(false);
            errors.Add(result.Details);
            if (result.Value is not null) return result with { Details = string.Join(Environment.NewLine, errors) };
            hadErrors |= result.HadErrors; deferred |= result.Deferred;
        }
        return new(null, errors.Count == 0 ? "Нет включённых геосервисов." : string.Join(Environment.NewLine, errors), deferred, hadErrors);
    }
}
