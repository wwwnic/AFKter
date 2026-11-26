namespace AFKtor.Core;

/// <summary>The one HTTP policy for talking to League's local API, shared by HTTP and WebSocket requests.</summary>
public static class LcuHttp
{
    /// <summary>
    /// LCU's self-signed certificate is trusted only for loopback. The event socket's upgrade
    /// request keeps its <c>wss</c> scheme when it reaches this callback, so both schemes count.
    /// </summary>
    public static bool IsTrustedEndpoint(Uri? uri) => uri is { Scheme: "https" or "wss", Host: "127.0.0.1" };

    /// <summary>A client with no proxy, no redirects, a short timeout and loopback-only certificate trust.</summary>
    public static HttpClient CreateClient() => new(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (request, _, _, _) => IsTrustedEndpoint(request.RequestUri)
    }) { Timeout = TimeSpan.FromSeconds(3) };
}
