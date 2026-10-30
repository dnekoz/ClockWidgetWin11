using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ExternalIp.Core;

var suite = new List<(string Name, Func<Task> Test)>();
void Test(string name, Action action) => suite.Add((name, () => { action(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> action) => suite.Add((name, action));
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}");
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
var textService = new IpService { Url = "https://one.test", Name = "one" };
var jsonService = new IpService { Format = IpResponseFormat.Json, IpPath = "data.address" };
var geoService = new GeoService { UrlTemplate = "https://geo.test/{ip}" };

foreach (var ip in new[] { "8.8.8.8", "1.1.1.1", "68.183.73.77", "192.0.0.9", "192.0.0.10", "223.255.255.254" })
    Test("public IPv4 accepts " + ip, () => Check(PublicIpv4.IsValid(ip)));
foreach (var ip in new string?[] { null, "", "1", "127.1", "0x08080808", "134744072", "01.2.3.4", "8.8.8.8 ", "8.8.8.256", "-1.8.8.8", "8.8.8.8.8", "８.8.8.8", "::ffff:8.8.8.8", "2001:4860:4860::8888", "0.2.3.4", "10.1.2.3", "100.64.0.1", "100.127.255.254", "127.0.0.1", "169.254.0.2", "172.16.0.1", "172.31.255.254", "192.0.0.1", "192.0.2.1", "192.88.99.1", "192.168.1.1", "198.18.0.1", "198.19.255.254", "198.51.100.1", "203.0.113.1", "224.0.0.1", "240.1.1.1", "255.255.255.255" })
    Test("public IPv4 rejects " + (ip ?? "null"), () => Check(!PublicIpv4.IsValid(ip)));

Test("text parser trims whitespace", () => Equal("8.8.8.8", ResponseParser.Ip(" \r\n8.8.8.8\n", textService)));
Test("JSON dotted path", () => Equal("8.8.8.8", ResponseParser.Ip("{\"data\":{\"address\":\"8.8.8.8\"}}", jsonService)));
Test("JSON pointer escapes and array", () =>
{
    using var doc = JsonDocument.Parse("{\"a/b\":{\"~name\":[\"8.8.8.8\"]}}");
    Equal("8.8.8.8", ResponseParser.Field(doc.RootElement, "/a~1b/~0name/0")?.GetString());
});
Test("malformed JSON rejected", () => Throws<JsonException>(() => ResponseParser.Ip("{oops", jsonService)));
Test("missing JSON path rejected", () => Throws<FormatException>(() => ResponseParser.Ip("{}", jsonService)));
Test("numeric JSON IP rejected", () => Throws<FormatException>(() => ResponseParser.Ip("{\"data\":{\"address\":123}}", jsonService)));
Test("private IP response rejected", () => Throws<FormatException>(() => ResponseParser.Ip("192.168.1.2", textService)));
Test("geo removes blanks and duplicates", () =>
{
    var geo = ResponseParser.Geo("{\"ip\":\"8.8.8.8\",\"country\":\" Russia \",\"region\":\"Moscow\",\"city\":\"moscow\"}", geoService, "8.8.8.8");
    Equal("Russia, Moscow", geo.Display);
});
Test("country-only geo is valid", () => Equal("USA", ResponseParser.Geo("{\"ip\":\"8.8.8.8\",\"country\":\"USA\"}", geoService, "8.8.8.8").Display));
Test("empty geo rejected", () => Throws<FormatException>(() => ResponseParser.Geo("{\"ip\":\"8.8.8.8\",\"country\":\"unknown\",\"city\":null}", geoService, "8.8.8.8")));
Test("geo for wrong IP rejected", () => Throws<FormatException>(() => ResponseParser.Geo("{\"ip\":\"1.1.1.1\",\"country\":\"USA\"}", geoService, "8.8.8.8")));
Test("API success false rejected", () => Throws<FormatException>(() => ResponseParser.Geo("{\"success\":false,\"country\":\"USA\"}", geoService, "8.8.8.8")));
Test("custom nested geo fields", () =>
{
    var service = new GeoService { ResponseIpPath = "", CountryPath = "data.country", RegionPath = "", CityPath = "/data/city", SuccessPath = "ok" };
    Equal("USA, LA", ResponseParser.Geo("{\"ok\":true,\"data\":{\"country\":\"USA\",\"city\":\"LA\"}}", service, "8.8.8.8").Display);
});

AppSettings OrderedSettings() => new()
{
    IpServices = [new() { Name = "disabled", Url = "https://disabled.test", Enabled = false },
        new() { Name = "one", Url = "https://one.test" }, new() { Name = "two", Url = "https://two.test", Format = IpResponseFormat.Json },
        new() { Name = "three", Url = "https://three.test" }, new() { Name = "four", Url = "https://four.test" }],
    GeoServices = [new() { Name = "g1", UrlTemplate = "https://g1.test/{ip}" }, new() { Name = "g2", UrlTemplate = "https://g2.test/{ip}" }]
};
AsyncTest("IP sequential fallback: timeout, JSON, success; stops and skips disabled", async () =>
{
    var transport = new FakeTransport((uri, _) => uri.Host switch
    {
        "one.test" => throw new TimeoutException("timeout"), "two.test" => Task.FromResult("{broken"),
        "three.test" => Task.FromResult("8.8.8.8"), _ => throw new Exception("Should not be requested")
    });
    var result = await new ServiceLookups(transport, new()).FindAsync(OrderedSettings(), default);
    Equal("8.8.8.8", result.Value); Equal("one.test,two.test,three.test", string.Join(',', transport.Calls.Select(u => u.Host)));
});
AsyncTest("invalid public IP falls back", async () =>
{
    var t = new FakeTransport((u, _) => Task.FromResult(u.Host == "one.test" ? "10.0.0.1" : "{\"ip\":\"1.1.1.1\"}"));
    Equal("1.1.1.1", (await new ServiceLookups(t, new()).FindAsync(OrderedSettings(), default)).Value);
});
AsyncTest("all IP errors return explanatory result", async () =>
{
    var t = new FakeTransport((_, _) => throw new HttpRequestException("offline"));
    var result = await new ServiceLookups(t, new()).FindAsync(OrderedSettings(), default);
    Check(result.Value is null && result.HadErrors && result.Details.Contains("offline")); Equal(4, t.Calls.Count);
});
AsyncTest("geo fallback explicitly passes the IP", async () =>
{
    var t = new FakeTransport((u, _) => Task.FromResult(u.Host == "g1.test" ? "{\"success\":false}" : "{\"ip\":\"8.8.8.8\",\"city\":\"Test City\"}"));
    var result = await new ServiceLookups(t, new()).FindAsync("8.8.8.8", OrderedSettings(), default);
    Equal("Test City", result.Value?.Display); Check(t.Calls.All(u => u.AbsolutePath == "/8.8.8.8")); Equal(2, t.Calls.Count);
});
AsyncTest("disabled geo sends no requests", async () =>
{
    var t = new FakeTransport((_, _) => throw new Exception("No request allowed")); var s = OrderedSettings(); s.GeoEnabled = false;
    await new ServiceLookups(t, new()).FindAsync("8.8.8.8", s, default); Equal(0, t.Calls.Count);
});
AsyncTest("cancellation propagates and does not try fallback", async () =>
{
    using var cts = new CancellationTokenSource();
    var t = new FakeTransport((_, _) => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
    await ThrowsAsync<OperationCanceledException>(() => new ServiceLookups(t, new()).FindAsync(OrderedSettings(), cts.Token)); Equal(1, t.Calls.Count);
});
Test("rate limiter floor, rolling quota and API Retry-After", () =>
{
    var clock = new ManualClock(); var limiter = new RateLimiter(clock: clock); var uri = new Uri("https://quota.test");
    Check(limiter.TryAcquire(uri, 10, 2, out _)); Check(!limiter.TryAcquire(uri, 10, 2, out _));
    clock.Advance(TimeSpan.FromSeconds(10)); Check(limiter.TryAcquire(uri, 10, 2, out _));
    clock.Advance(TimeSpan.FromMinutes(1)); Check(!limiter.TryAcquire(uri, 10, 2, out _));
    clock.Advance(TimeSpan.FromHours(24)); Check(limiter.TryAcquire(uri, 10, 2, out _));
    limiter.Complete(uri, false, TimeSpan.FromHours(3)); clock.Advance(TimeSpan.FromHours(2));
    Check(!limiter.TryAcquire(uri, 10, 2, out _)); clock.Advance(TimeSpan.FromHours(1)); Check(limiter.TryAcquire(uri, 10, 2, out _));
});
Test("built-in geo limits cannot be lowered", () =>
{
    var clock = new ManualClock(); var limiter = new RateLimiter(clock: clock); var uri = new Uri("https://ipwho.is/8.8.8.8");
    Check(limiter.TryAcquire(uri, 10, 0, out _)); clock.Advance(TimeSpan.FromSeconds(60)); Check(!limiter.TryAcquire(uri, 10, 0, out _));
    clock.Advance(TimeSpan.FromSeconds(60)); Check(limiter.TryAcquire(uri, 10, 0, out _));
});
AsyncTest("429 fallback honors Retry-After on following cycles", async () =>
{
    var clock = new ManualClock(); var limiter = new RateLimiter(clock: clock);
    var s = OrderedSettings(); s.IpServices = s.IpServices.Take(3).ToList();
    var t = new FakeTransport((u, _) => u.Host == "one.test" ? throw new ServiceHttpException("429", TimeSpan.FromHours(1)) : Task.FromResult("{\"ip\":\"8.8.8.8\"}"));
    var lookup = new ServiceLookups(t, limiter); Equal("8.8.8.8", (await lookup.FindAsync(s, default)).Value);
    clock.Advance(TimeSpan.FromMinutes(1)); Equal("8.8.8.8", (await lookup.FindAsync(s, default)).Value);
    Equal(1, t.Calls.Count(u => u.Host == "one.test"));
});

AsyncTest("new IP clears previous geo BEFORE geo response completes", async () =>
{
    var ip = new StubIp(); var geo = new StubGeo(); var engine = new UpdateEngine(ip, geo);
    engine.Restore(new() { Ip = "8.8.8.8", Location = new("Old Country", "Old Region", "Old City") });
    ip.Result = new("1.1.1.1");
    var pending = new TaskCompletionSource<LookupResult<GeoLocation>>(TaskCreationOptions.RunContinuationsAsynchronously);
    geo.Handler = (_, _) => pending.Task;
    var seen = new List<WidgetState>(); engine.Changed += seen.Add;
    var refresh = engine.RefreshAsync(new());
    Equal("1.1.1.1", engine.State.Ip); Check(engine.State.Location is null, "Old geo leaked beside new IP before await completed");
    pending.SetResult(new(null, "geo unavailable", HadErrors: true)); await refresh;
    Check(engine.State.Location is null); Check(seen.All(s => s.Ip != "1.1.1.1" || s.Location is null));
});
AsyncTest("same IP geo failure retains location explicitly stale", async () =>
{
    var engine = new UpdateEngine(new StubIp(), new StubGeo { Handler = (_, _) => Task.FromResult(new LookupResult<GeoLocation>(null, "timeout", HadErrors: true)) });
    engine.Restore(new() { Ip = "8.8.8.8", Location = new("USA", null, null) }); await engine.RefreshAsync(new());
    Equal("USA", engine.State.Location?.Display); Check(engine.State.GeoStale); Check(engine.State.LocationDisplay(true).Contains("устарело"));
});
AsyncTest("same IP normal API pause retains fresh cache", async () =>
{
    var geo = new StubGeo(); var engine = new UpdateEngine(new StubIp(), geo); await engine.RefreshAsync(new());
    geo.Handler = (_, _) => Task.FromResult(new LookupResult<GeoLocation>(null, "pause", Deferred: true));
    await engine.RefreshAsync(new()); Check(engine.State.Location is not null && !engine.State.GeoStale);
});
AsyncTest("IP failure retains last IP and marks tooltip stale", async () =>
{
    var ip = new StubIp(); var engine = new UpdateEngine(ip, new StubGeo()); await engine.RefreshAsync(new()); var last = engine.State.LastSuccess;
    ip.Result = new(null, "offline", HadErrors: true); await engine.RefreshAsync(new());
    Equal("8.8.8.8", engine.State.Ip); Check(engine.State.IpStale); Equal(last, engine.State.LastSuccess);
    Check(engine.State.Tooltip(true).Contains("Данные устарели"));
});
AsyncTest("first IP failure displays unavailable", async () =>
{
    var engine = new UpdateEngine(new StubIp { Result = new(null, "offline") }, new StubGeo()); await engine.RefreshAsync(new());
    Equal("IP недоступен", engine.State.IpDisplay); Check(engine.State.LastSuccess is null);
});
AsyncTest("geo disabled at engine level skips lookup and clears cached geo", async () =>
{
    var geo = new StubGeo { Handler = (_, _) => throw new Exception("Must not invoke geo") }; var engine = new UpdateEngine(new StubIp(), geo);
    engine.Restore(new() { Ip = "8.8.8.8", Location = new("USA", null, null) });
    await engine.RefreshAsync(new() { GeoEnabled = false }); Check(engine.State.Location is null);
});
AsyncTest("overlapping refreshes do not run in parallel", async () =>
{
    var pending = new TaskCompletionSource<LookupResult<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var ip = new StubIp { Handler = _ => pending.Task }; var engine = new UpdateEngine(ip, new StubGeo());
    var first = engine.RefreshAsync(new()); Check(!await engine.RefreshAsync(new())); Equal(1, ip.Calls);
    pending.SetResult(new("8.8.8.8")); Check(await first); Check(!engine.State.Busy);
});
AsyncTest("cancelled refresh releases gate and clears busy flag", async () =>
{
    using var cts = new CancellationTokenSource();
    var ip = new StubIp { Handler = async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new("8.8.8.8"); } };
    var engine = new UpdateEngine(ip, new StubGeo()); var run = engine.RefreshAsync(new(), cts.Token); cts.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => run); Check(!engine.State.Busy);
    ip.Handler = null; Check(await engine.RefreshAsync(new()));
});

var tempRoot = Path.Combine(Path.GetTempPath(), "ExternalIpWidget.Tests." + Guid.NewGuid().ToString("N"));
Test("settings roundtrip preserves order, fields, toggles and position", () =>
{
    var store = new SettingsStore(Path.Combine(tempRoot, "settings.json")); var s = OrderedSettings();
    s.RefreshSeconds = 123; s.TimeoutSeconds = 8; s.WidgetX = -1400; s.WidgetY = 300; s.AlwaysOnTop = true; s.WidgetVisible = false; s.AutoStart = true;
    s.GeoServices[0].CountryPath = "data.country"; s.GeoServices.Reverse(); store.Save(s); var copy = store.Load();
    Equal(JsonSerializer.Serialize(s, JsonFile.Options), JsonSerializer.Serialize(copy, JsonFile.Options)); Check(store.Warning is null);
});
Test("settings corrupt main recovers validated backup", () =>
{
    var path = Path.Combine(tempRoot, "backup.json"); var store = new SettingsStore(path); store.Save(new() { RefreshSeconds = 100 }); store.Save(new() { RefreshSeconds = 200 });
    File.WriteAllText(path, "{broken"); Equal(100, store.Load().RefreshSeconds); Check(store.Warning is not null);
});
Test("settings corrupt files fall back safely", () =>
{
    var path = Path.Combine(tempRoot, "bad.json"); File.WriteAllText(path, "{\"IpServices\":null}");
    var store = new SettingsStore(path); Equal(60, store.Load().RefreshSeconds); Check(store.Warning is not null);
});
Test("invalid settings cannot be saved", () =>
{
    var store = new SettingsStore(Path.Combine(tempRoot, "invalid.json"));
    Throws<FormatException>(() => store.Save(new() { RefreshSeconds = 0 }));
    Throws<FormatException>(() => store.Save(new() { TimeoutSeconds = 0 }));
    Throws<FormatException>(() => store.Save(new() { IpServices = [] }));
    var s = new AppSettings(); s.IpServices[0].Url = "http://unsafe.test"; Throws<FormatException>(() => store.Save(s));
    s = new(); s.GeoServices[0].UrlTemplate = "https://geo.test"; Throws<FormatException>(() => store.Save(s));
});
Test("API budgets and Retry-After survive restart", () =>
{
    var path = Path.Combine(tempRoot, "usage.json"); var clock = new ManualClock(); var uri = new Uri("https://persistent.test");
    var limiter = new RateLimiter(path, clock); Check(limiter.TryAcquire(uri, 10, 1, out _)); limiter.Complete(uri, false, TimeSpan.FromHours(2));
    limiter = new RateLimiter(path, clock); clock.Advance(TimeSpan.FromHours(1)); Check(!limiter.TryAcquire(uri, 10, 1, out _));
    clock.Advance(TimeSpan.FromHours(2)); Check(!limiter.TryAcquire(uri, 10, 1, out _));
    clock.Advance(TimeSpan.FromHours(22)); Check(limiter.TryAcquire(uri, 10, 1, out _));
});
Test("cached state is restored as stale", () =>
{
    var store = new StateStore(Path.Combine(tempRoot, "state.json")); store.Save(new() { Ip = "8.8.8.8", Location = new("USA", null, null), LastSuccess = DateTimeOffset.UtcNow });
    var engine = new UpdateEngine(new StubIp(), new StubGeo()); engine.Restore(store.Load()!);
    Check(engine.State.IpStale && engine.State.GeoStale && !engine.State.Busy);
});

AsyncTest("transport rejects a real self-signed HTTPS certificate over IPv4", async () =>
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
    using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var server = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(cts.Token);
        using var ssl = new SslStream(client.GetStream());
        try { await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, cts.Token); }
        catch (Exception e) when (e is AuthenticationException or IOException) { }
    });
    using var transport = new Ipv4HttpTransport();
    await ThrowsAsync<HttpRequestException>(() => transport.GetAsync(new Uri($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"), TimeSpan.FromSeconds(3), default));
    await server;
});
AsyncTest("transport deadline aborts stalled real IPv4 connection", async () =>
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var transport = new Ipv4HttpTransport();
    await ThrowsAsync<TimeoutException>(() => transport.GetAsync(new Uri($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"), TimeSpan.FromMilliseconds(200), default));
});
AsyncTest("transport refuses plaintext HTTPS downgrade", async () =>
{
    using var transport = new Ipv4HttpTransport();
    await ThrowsAsync<FormatException>(() => transport.GetAsync(new Uri("http://127.0.0.1"), TimeSpan.FromSeconds(1), default));
});

if (args.Contains("--live"))
{
    var s = new AppSettings(); var transport = new Ipv4HttpTransport(); var lookups = new ServiceLookups(transport, new RateLimiter());
    foreach (var service in s.IpServices)
        AsyncTest("LIVE HTTPS/IPv4 " + service.Name, async () =>
        {
            var result = await lookups.TestIpAsync(service, 5, default); Check(result.Value is not null, result.Details);
            Console.WriteLine("  " + result.Details + " IPv4=" + result.Value);
        });
    foreach (var service in s.GeoServices)
        AsyncTest("LIVE explicit IP geolocation " + service.Name, async () =>
        {
            var result = await lookups.TestGeoAsync(service, "8.8.8.8", 5, default); Check(result.Value is not null, result.Details);
            Console.WriteLine("  " + result.Details + " " + result.Value?.Display);
        });
}

int failures = 0;
try
{
    foreach (var test in suite)
    {
        try { await test.Test(); Console.WriteLine("PASS " + test.Name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + test.Name + "\n" + e); }
    }
}
finally { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); }
Console.WriteLine($"\n{suite.Count - failures}/{suite.Count} passed; {failures} failed.");
return failures == 0 ? 0 : 1;

sealed class FakeTransport(Func<Uri, CancellationToken, Task<string>> handler) : IHttpTransport
{
    public List<Uri> Calls { get; } = [];
    public Task<string> GetAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken) { Calls.Add(uri); return handler(uri, cancellationToken); }
}
sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}
sealed class StubIp : IIpLookup
{
    public int Calls { get; private set; }
    public LookupResult<string> Result { get; set; } = new("8.8.8.8");
    public Func<CancellationToken, Task<LookupResult<string>>>? Handler { get; set; }
    public Task<LookupResult<string>> FindAsync(AppSettings settings, CancellationToken token)
    { Calls++; return Handler?.Invoke(token) ?? Task.FromResult(Result); }
}
sealed class StubGeo : IGeoLookup
{
    public Func<string, CancellationToken, Task<LookupResult<GeoLocation>>> Handler { get; set; } = (_, _) => Task.FromResult(new LookupResult<GeoLocation>(new("USA", null, null)));
    public Task<LookupResult<GeoLocation>> FindAsync(string ip, AppSettings settings, CancellationToken token) => Handler(ip, token);
}
