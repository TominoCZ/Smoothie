using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppHeaders = Il2CppSystem.Collections.Generic.Dictionary<string, string>;

namespace Smoothie;

// The game sends every backend request through UnityWebSender, which makes a fresh
// UnityWebRequest (and a fresh certificate handler) each time, so every request pays for a
// new TCP + TLS handshake. Against a remote SPT server that is several round trips per
// request, and the menu loads dozens of them one after another. This does the same job on
// one HttpClient that keeps the connection open, like SPT's own Client does.
// IL2CPP: an IL2CPP class of its own (registered with the game's ISender interface) that the
// game's SenderHelper calls like its own senders. What it does mirrors UnityWebSender in game
// 1.2 (read from its compiled code, UnityWebSender+<WaitResponse>d__0 and
// <ExecuteOperationCoroutine>d__1 in GameAssembly.dll): the method as given, a body unless it's
// GET, Content-Type application/json, then the caller's headers; 4xx/5xx come back as
// "Backend error: ...". Headers-only requests (Unity's HeadersOnlyDownloadHandler, which drops
// the body mid-download) go to the game's own sender, so their result stays exactly the game's.
// Bodies are shuffled both ways (live's protocol, which SPT's server speaks): BSG's own Unity build
// does it inside UnityWebRequest (its UnityPlayer.dll has the shuffle; game code never sees it), so
// here it's done with SPT's Shuffle (SPTushonka.Common), for the paths SPT's server shuffles
// (SptHttpListener.ShouldShuffleRequest/Response).
internal sealed class BackendSender : Il2CppSystem.Object, ISender
{
    // Close idle connections ourselves before the server does, so a request is never
    // written into a connection the server is about to drop. SPT's server (Kestrel) drops
    // them after 130 s idle; measured 131 s against a live 4.1.6 server.
    private static readonly TimeSpan MaxIdle = TimeSpan.FromSeconds(90);

    private static HttpClient? _client;
    private static ISender? _fallback;

    public BackendSender(IntPtr pointer) : base(pointer) { }

    private BackendSender() : base(ClassInjector.DerivedConstructorPointer<BackendSender>()) =>
        ClassInjector.DerivedConstructorBody(this);

    // fallback: the game's own sender, for what this one passes on (headers-only requests).
    internal static BackendSender Create(ISender fallback)
    {
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<BackendSender>())
            ClassInjector.RegisterTypeInIl2Cpp<BackendSender>(new RegisterTypeOptions { Interfaces = new[] { typeof(ISender) } });
        _fallback = fallback;
        _client ??= new HttpClient(new SocketsHttpHandler
        {
            // The cookie comes in the request headers, the same as the vanilla sender.
            UseCookies = false,
            // An SPT server is always reached directly; proxy auto-detection only adds delay.
            UseProxy = false,
            PooledConnectionIdleTimeout = MaxIdle,
            // Requests from different parts of the menu can overlap.
            MaxConnectionsPerServer = 8,
            // The game pins BSG's keys; SPT's server has its own certificate, and SPT's
            // Client accepts it the same way.
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            ConnectCallback = Connect,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        return new BackendSender();
    }

    // TCP keep-alive probes keep NAT and mesh mappings from silently dropping the idle connection
    // (and notice when one has died); Nagle off so a small request isn't held back waiting for an
    // ACK on a reused connection.
    private static async ValueTask<System.IO.Stream> Connect(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
        }
        catch (SocketException)
        {
            // Without these the connection still works, it just isn't tuned.
        }

        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // ISender, called by the game on the main thread.
    public Il2CppSystem.Threading.Tasks.Task<ResponseData> WaitResponse(string url, Il2CppStructArray<byte> data, Il2CppHeaders headers,
        int timeoutSeconds, string method, bool headersOnly)
    {
        if (headersOnly && _fallback != null) return _fallback.WaitResponse(url, data, headers, timeoutSeconds, method, headersOnly);

        // Copied out on the main thread: the request itself runs off it.
        var body = method == "GET" || data == null ? null : (byte[])data;
        var path = PathOf(url);
        if (body is { Length: > 0 } && ShuffledRequest(path)) body = SPTushonka.Common.Http.Shuffle.Pack(body);
        var sent = new List<KeyValuePair<string, string>>();
        if (headers != null)
            foreach (var header in headers) sent.Add(new KeyValuePair<string, string>(header.Key, header.Value));
        var stopwatch = Il2CppSystem.Diagnostics.Stopwatch.StartNew();
        var source = new Il2CppSystem.Threading.Tasks.TaskCompletionSource<ResponseData>();
        _ = Answer(source, url, method, body, sent, timeoutSeconds, stopwatch);
        return source.Task;
    }

    private static string PathOf(string url)
    {
        try
        {
            return new Uri(url).AbsolutePath;
        }
        catch (UriFormatException)
        {
            return "";
        }
    }

    // As SPT's server decides (SptHttpListener): everything but the launcher, metadata, shop and
    // files is shuffled; responses too, except /singleplayer's.
    private static bool ShuffledRequest(string path) =>
        !Under(path, "/launcher") && !Under(path, "/client/metadata") && !Under(path, "/v2/shop") && !Under(path, "/files");

    private static bool ShuffledResponse(string path) => ShuffledRequest(path) && !Under(path, "/singleplayer");

    // Path segment prefix, as ASP.NET's StartsWithSegments.
    private static bool Under(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (path.Length == prefix.Length || path[prefix.Length] == '/');

    // Resumes on the main thread (SPT's context), where the game's objects are made.
    private static async Task Answer(Il2CppSystem.Threading.Tasks.TaskCompletionSource<ResponseData> source, string url, string method,
        byte[]? body, List<KeyValuePair<string, string>> headers, int timeoutSeconds, Il2CppSystem.Diagnostics.Stopwatch stopwatch)
    {
        ResponseData response;
        try
        {
            var reply = await Send(url, method, body, headers, timeoutSeconds);
            response = reply.Failure != null ? new ResponseData(reply.Failure, 0) : Response(url, reply, stopwatch);
        }
        catch (Exception ex)
        {
            response = new ResponseData(ex.Message, 0);
        }

        source.SetResult(response);
    }

    private static ResponseData Response(string url, (int Code, string? Failure, List<KeyValuePair<string, string>> Headers, byte[] Body, string Reason) reply,
        Il2CppSystem.Diagnostics.Stopwatch stopwatch)
    {
        var text = Encoding.UTF8.GetString(reply.Body);

        // Same split as UnityWebRequest: 4xx/5xx are errors, anything else (including the
        // 304 the item cache relies on) is a response. Server errors are never retried here.
        if (reply.Code >= 400)
        {
            var error = $"HTTP/1.1 {reply.Code} {reply.Reason}";
            var log = new Il2CppReferenceArray<Il2CppSystem.Object>(1);
            log[0] = (Il2CppSystem.String)$"<--- Error! HTTPS: {url}, responseCode:{reply.Code}\nerror text: {error}";
            SenderHelper.Logger.LogError("{0}", log);
            return new ResponseData("Backend error: " + error + "\n" + text, reply.Code);
        }

        // HTTP/2 lowercases header names; the game looks some up by their usual casing.
        var headers = new Il2CppHeaders(Il2CppSystem.StringComparer.OrdinalIgnoreCase.Cast<Il2CppSystem.Collections.Generic.IEqualityComparer<string>>());
        foreach (var header in reply.Headers) headers[header.Key] = header.Value;
        return new ResponseData(reply.Code, string.Empty, headers, reply.Body, reply.Body.Length, text, stopwatch);
    }

    private static async Task<(int Code, string? Failure, List<KeyValuePair<string, string>> Headers, byte[] Body, string Reason)> Send(
        string url, string method, byte[]? body, List<KeyValuePair<string, string>> headers, int timeoutSeconds)
    {
        // Failures go back to the game as-is, and its own retry rules decide, the same as
        // with the vanilla sender. A request that may have reached the server is never
        // resent from here.
        var none = new List<KeyValuePair<string, string>>();
        try
        {
            using var timeout = timeoutSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
                : new CancellationTokenSource();
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (body != null) request.Content = new ByteArrayContent(body);
            request.Content?.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            foreach (var header in headers)
            {
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value)) request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await _client!.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (bytes.Length >= 4 && ShuffledResponse(PathOf(url)))
            {
                // Error pages may come back plain: those stay as they are.
                try
                {
                    bytes = SPTushonka.Common.Http.Shuffle.Unpack(bytes);
                }
                catch (InvalidOperationException)
                {
                }
            }
            var replyHeaders = new List<KeyValuePair<string, string>>();
            foreach (var header in response.Headers) replyHeaders.Add(new KeyValuePair<string, string>(header.Key, string.Join(", ", header.Value)));
            foreach (var header in response.Content.Headers) replyHeaders.Add(new KeyValuePair<string, string>(header.Key, string.Join(", ", header.Value)));
            return ((int)response.StatusCode, null, replyHeaders, bytes, response.ReasonPhrase ?? "");
        }
        catch (OperationCanceledException)
        {
            return (0, "Backend error: Request timeout", none, Array.Empty<byte>(), "");
        }
        catch (Exception ex)
        {
            return (0, ex.Message, none, Array.Empty<byte>(), "");
        }
    }
}
