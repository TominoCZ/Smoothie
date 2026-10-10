using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Smoothie;

// The game sends every backend request through UnityWebSender, which makes a fresh
// UnityWebRequest (and a fresh certificate handler) each time, so every request pays for a
// new TCP + TLS handshake. Against a remote SPT server that is several round trips per
// request, and the menu loads dozens of them one after another. This does the same job on
// one HttpClient that keeps the connection open, like SPT's own Client does.
internal sealed class BackendSender : ISender
{
    // Close idle connections ourselves before the server does, so a request is never
    // written into a connection the server is about to drop. SPT's server (Kestrel) drops
    // them after 130 s idle; measured 131 s against a live 4.1.6 server.
    private const int MaxIdleMs = 90_000;

    private readonly HttpClient _client;
    private readonly HashSet<string> _tunedHosts = new();

    public BackendSender()
    {
        var handler = new HttpClientHandler
        {
            // The cookie comes in the request headers, the same as the vanilla sender.
            UseCookies = false,

            // The game pins BSG's keys; SPT's server has its own certificate, and SPT's
            // Client accepts it the same way.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,

            // An SPT server is always reached directly; proxy auto-detection only adds delay.
            UseProxy = false,
        };

        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        // Mono waits for a "100 Continue" before sending a POST body: one more round trip.
        _client.DefaultRequestHeaders.ExpectContinue = false;
    }

    public async Task<ResponseData> WaitResponse(string url, byte[] data, Dictionary<string, string> headers, int timeoutSeconds)
    {
        var stopwatch = Stopwatch.StartNew();
        TuneConnection(url);

        // Failures go back to the game as-is, and its own retry rules decide, the same as
        // with the vanilla sender. A request that may have reached the server is never
        // resent from here.
        try
        {
            return await SendOnce(url, data, headers, timeoutSeconds, stopwatch);
        }
        catch (OperationCanceledException)
        {
            return new ResponseData("Backend error: Request timeout");
        }
        catch (Exception ex)
        {
            return new ResponseData(ex.Message);
        }
    }

    private async Task<ResponseData> SendOnce(string url, byte[] data, Dictionary<string, string> headers, int timeoutSeconds, Stopwatch stopwatch)
    {
        using var timeout = timeoutSeconds > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
            : new CancellationTokenSource();

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new ByteArrayContent(data ?? Array.Empty<byte>());
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        foreach (var header in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        using var response = await _client.SendAsync(request, timeout.Token);
        var body = await response.Content.ReadAsByteArrayAsync();
        var code = (int)response.StatusCode;

        // HTTP/2 lowercases header names; the game looks some up by their usual casing.
        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers) responseHeaders[header.Key] = string.Join(", ", header.Value);
        foreach (var header in response.Content.Headers) responseHeaders[header.Key] = string.Join(", ", header.Value);

        var text = Encoding.UTF8.GetString(body);

        // Same split as UnityWebRequest: 4xx/5xx are errors, anything else (including the
        // 304 the item cache relies on) is a response. Server errors are never retried here.
        if (code >= 400)
        {
            var error = $"HTTP/1.1 {code} {response.ReasonPhrase}";
            SenderHelper.Logger.LogError("<--- Error! HTTPS: {0}, responseCode:{1}\nerror text: {2}", url, code, error);
            return new ResponseData("Backend error: " + error + "\n" + text, code);
        }

        return new ResponseData(code, string.Empty, responseHeaders, body, body.Length, text, stopwatch);
    }

    // Per server, once: TCP keep-alive probes keep NAT and mesh mappings from silently
    // dropping the idle connection (and notice when one has died), and Nagle is off so a
    // small request isn't held back waiting for an ACK on a reused connection.
    private void TuneConnection(string url)
    {
        Uri uri;
        try { uri = new Uri(url); }
        catch (UriFormatException) { return; }

        lock (_tunedHosts)
        {
            if (!_tunedHosts.Add(uri.GetLeftPart(UriPartial.Authority))) return;
        }

        try
        {
            var point = ServicePointManager.FindServicePoint(uri);
            point.MaxIdleTime = MaxIdleMs;

            // Requests from different parts of the menu can overlap. Set here because
            // Mono's HttpClientHandler.MaxConnectionsPerServer throws NotImplementedException.
            point.ConnectionLimit = 8;
            point.UseNagleAlgorithm = false;
            point.Expect100Continue = false;
            point.SetTcpKeepAlive(true, 15_000, 5_000);
        }
        catch (Exception ex) when (ex is NotSupportedException or NotImplementedException or PlatformNotSupportedException)
        {
            // Without these the connection still works, it just isn't tuned.
            Plugin.Log.LogInfo($"connection tuning unavailable: {ex.Message}");
        }
    }
}
