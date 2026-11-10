using System.Security.Claims;
using CodexGate.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CodexGate.Security;

public static class Claims
{
    public const string UserId = ClaimTypes.NameIdentifier;
    public const string Stamp = "cg:stamp";

    public static ClaimsPrincipal For(UserRow user)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(UserId, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(Stamp, user.SecurityStamp),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    public static long? IdOf(ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirstValue(UserId), out var id) ? id : null;

    /// <summary>
    /// Every panel request re-reads the account: a disabled user, a changed password or role
    /// (all of which rotate the stamp) or a deleted user ends the cookie at once.
    /// </summary>
    public static async Task ValidateCookieAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var id = principal is null ? null : IdOf(principal);
        var users = context.HttpContext.RequestServices.GetRequiredService<UserStore>();
        var user = id is null ? null : await users.GetAsync(id.Value);
        if (user is null || !user.Enabled || user.SecurityStamp != principal!.FindFirstValue(Stamp))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        context.HttpContext.Items[typeof(UserRow)] = user;
    }

    /// <summary>The account loaded by <see cref="ValidateCookieAsync"/> for this request.</summary>
    public static UserRow? CurrentUser(this HttpContext context) => context.Items[typeof(UserRow)] as UserRow;
}

/// <summary>
/// State-changing panel calls must carry a custom header. Browsers cannot add one to a
/// cross-site request without a CORS preflight, which this server never grants.
/// </summary>
public sealed class CsrfHeaderMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-CodexGate";

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api")
            && !HttpMethods.IsGet(context.Request.Method)
            && !HttpMethods.IsHead(context.Request.Method)
            && !context.Request.Headers.ContainsKey(HeaderName))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return context.Response.WriteAsJsonAsync(new { error = "csrf_header_missing" });
        }
        return next(context);
    }
}

/// <summary>Browser hardening for the panel; the gateways (/v1, /chatgpt) are left untouched.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/v1") && !context.Request.Path.StartsWithSegments("/chatgpt"))
        {
            var h = context.Response.Headers;
            h["Content-Security-Policy"] =
                "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; " +
                "frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "same-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        }
        return next(context);
    }
}
