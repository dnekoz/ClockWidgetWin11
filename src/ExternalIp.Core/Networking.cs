using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ExternalIp.Core;

public interface IHttpTransport
{
    Task<string> GetAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class ServiceHttpException(string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed class Ipv4HttpTransport : IHttpTransport, IDisposable
{
    private readonly HttpClient _client;
    public Ipv4HttpTransport()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(45),
            MaxConnectionsPerServer = 2, MaxResponseHeadersLength = 16,
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, token);
                Exception? last = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception e) { socket.Dispose(); token.ThrowIfCancellationRequested(); last = e; }
                }
                throw new HttpRequestException("Не удалось подключиться через IPv4.", last);
            }
        };
        // TLS authentication remains with SocketsHttpHandler and Windows; no certificate bypass.
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("ExternalIpWidget/1.0");
    }

    public async Task<string> GetAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        AppSettings.ValidateHttps(uri.AbsoluteUri);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            // HTTP/1.1 forces all TCP connections through the IPv4 ConnectCallback (no QUIC).
            using var request = new HttpRequestMessage(HttpMethod.Get, uri)
            { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var retry = response.Headers.RetryAfter?.Delta;
                if (response.Headers.RetryAfter?.Date is { } date) retry = date - DateTimeOffset.UtcNow;
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new ServiceHttpException("HTTP 429: превышен лимит запросов.", retry ?? TimeSpan.FromHours(24));
                throw new ServiceHttpException($"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).", retry);
            }
            const int maxBytes = 65536;
            if (response.Content.Headers.ContentLength > maxBytes) throw new FormatException("Ответ больше 64 КиБ.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > maxBytes) throw new FormatException("Ответ больше 64 КиБ.");
                memory.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(memory.ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException($"Тайм-аут запроса: {timeout.TotalSeconds:0} с."); }
    }

    public void Dispose() => _client.Dispose();
}
