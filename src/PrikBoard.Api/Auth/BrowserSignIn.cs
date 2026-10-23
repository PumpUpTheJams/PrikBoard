namespace PrikBoard.Api.Auth;

public static class BrowserSignIn
{
    /// <summary>
    /// For pages people open in the browser, such as the API reference: instead of answering 401, send someone
    /// who is not signed in through the default provider and back to the page they asked for.
    /// </summary>
    public static TBuilder RedirectToSignIn<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AllowAnonymous().AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (http.User.Identity?.IsAuthenticated == true) return await next(context);

            // Without any provider there is no way to sign in; the page still works as read-only documentation.
            if (http.RequestServices.GetRequiredService<SignInProviders>().Default is not { } provider) return await next(context);

            var returnUrl = $"{http.Request.PathBase}{http.Request.Path}{http.Request.QueryString}";
            return Results.Redirect($"/api/auth/login/{provider.Id}?returnUrl={Uri.EscapeDataString(returnUrl)}");
        });
}
