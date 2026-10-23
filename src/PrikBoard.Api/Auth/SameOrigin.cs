namespace PrikBoard.Api.Auth;

/// <summary>
/// Whether a request comes from this site's own pages. Used where SameSite alone is not enough: the form post that links
/// a sign-in, approving an app or Discord connection, WebSocket upgrades (which browsers send cross-site without any CORS
/// check), and requests to the MCP endpoint that carry an Origin.
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
