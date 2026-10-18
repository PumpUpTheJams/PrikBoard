namespace PrikBoard.Api.Auth;

/// <summary>
/// Whether a cookie-carrying request comes from this site's own pages. Used where SameSite alone is not enough:
/// form posts that start a sign-in, and WebSocket upgrades, which browsers send cross-site without any CORS check.
/// </summary>
public static class SameOrigin
{
    /// <summary>The Origin names this host, or Sec-Fetch-Site says same-origin when a browser omits Origin.</summary>
    public static bool Check(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        // Compare hosts only: behind a TLS-terminating proxy the request scheme can differ from the browser's.
        if (origin.Length > 0)
        {
            return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                   && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
        }
        return request.Headers["Sec-Fetch-Site"].ToString() == "same-origin";
    }
}
