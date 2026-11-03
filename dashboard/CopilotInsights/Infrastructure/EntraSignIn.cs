using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// Sign-in through Microsoft Entra ID (Insights:Authentication = Entra): OpenID Connect with the authorization code and
/// PKCE for the pages, kept in a cookie; and, for the data API, a bearer token issued for the app's own API. Windows sign-in
/// stays the default and is untouched by any of this. Who the person is, and what they may see, is settled afterwards
/// exactly as for a Windows sign-in (<see cref="ViewerService"/>).
/// </summary>
public static class EntraSignIn
{
    /// <summary>The scheme a request is signed in with: a bearer token's, else the cookie's.</summary>
    public const string Scheme = "Insights";
    /// <summary>The scheme that asks for sign-in: 401 for the data API and for a bearer token, Entra ID's page for everything else.</summary>
    public const string ChallengeScheme = "InsightsChallenge";
    public const string SignOutPath = "/signout";

    public static void Configure(IServiceCollection services, InsightsOptions options, bool workbench = false)
    {
        var e = options.Entra;
        if (string.IsNullOrWhiteSpace(e.TenantId) || string.IsNullOrWhiteSpace(e.ClientId))
            throw new InvalidOperationException("Insights:Authentication is Entra, but Insights:Entra:TenantId and Insights:Entra:ClientId are not both set. See docs/ENTRA-SIGN-IN.md.");

        var auth = services.AddAuthentication(o =>
        {
            o.DefaultScheme = Scheme;
            o.DefaultChallengeScheme = ChallengeScheme;
            o.DefaultForbidScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        });
        auth.AddPolicyScheme(Scheme, "Entra ID", p => p.ForwardDefaultSelector = ctx =>
            IsBearer(ctx.Request) ? JwtBearerDefaults.AuthenticationScheme : CookieAuthenticationDefaults.AuthenticationScheme);
        auth.AddPolicyScheme(ChallengeScheme, "Entra ID sign-in", p => p.ForwardDefaultSelector = ctx =>
            IsBearer(ctx.Request) || ctx.Request.Path.StartsWithSegments("/api") ? JwtBearerDefaults.AuthenticationScheme : OpenIdConnectDefaults.AuthenticationScheme);

        var web = auth.AddMicrosoftIdentityWebApp(mi =>
            {
                Identity(mi, e);
                mi.CallbackPath = string.IsNullOrWhiteSpace(e.CallbackPath) ? "/signin-oidc" : e.CallbackPath;
                mi.SignedOutCallbackPath = "/signout-callback-oidc";
                mi.ResponseType = "code";
                mi.UsePkce = true;
                mi.SaveTokens = false;   // the pages call nothing as the person: nothing to keep
                mi.ClientCredentials = [Credential(e)];
            },
            cookie =>
            {
                cookie.Cookie.Name = "CopilotInsights.Auth";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
                cookie.SlidingExpiration = true;
            },
            OpenIdConnectDefaults.AuthenticationScheme, CookieAuthenticationDefaults.AuthenticationScheme);
        /* The Governance Workbench's inventory read (Operations › Sync and freshness) calls the Power Platform API as the
           operator: their token is asked for when they run it (more consent on first use), and kept in this server's
           memory. Nothing else calls anything as the person. */
        if (workbench) web.EnableTokenAcquisitionToCallDownstreamApi([]).AddInMemoryTokenCaches();

        auth.AddMicrosoftIdentityWebApi(jwt =>
            {
                var audience = string.IsNullOrWhiteSpace(e.ApiAudience) ? $"api://{e.ClientId!.Trim()}" : e.ApiAudience.Trim();
                jwt.TokenValidationParameters.ValidAudiences = [audience, e.ClientId!.Trim()];
            },
            mi => Identity(mi, e), JwtBearerDefaults.AuthenticationScheme);
    }

    static void Identity(MicrosoftIdentityOptions mi, EntraOptions e)
    {
        mi.Instance = string.IsNullOrWhiteSpace(e.Instance) ? "https://login.microsoftonline.com/" : e.Instance.Trim();
        mi.TenantId = e.TenantId!.Trim();
        mi.ClientId = e.ClientId!.Trim();
    }

    /// <summary>How the app proves itself when it redeems the sign-in's code: no secret at all with a managed identity.</summary>
    public static CredentialDescription Credential(EntraOptions e) => e.ClientCredential.Trim().ToLowerInvariant() switch
    {
        "managedidentity" => new CredentialDescription
        {
            SourceType = CredentialSource.SignedAssertionFromManagedIdentity,
            ManagedIdentityClientId = string.IsNullOrWhiteSpace(e.ManagedIdentityClientId) ? null : e.ManagedIdentityClientId.Trim(),
        },
        "secret" => new CredentialDescription
        {
            SourceType = CredentialSource.ClientSecret,
            ClientSecret = string.IsNullOrWhiteSpace(e.ClientSecret) ? throw new InvalidOperationException("Insights:Entra:ClientSecret is not set.") : e.ClientSecret,
        },
        _ => new CredentialDescription
        {
            SourceType = CredentialSource.StoreWithThumbprint,
            CertificateStorePath = "LocalMachine/My",
            CertificateThumbprint = string.IsNullOrWhiteSpace(e.CertificateThumbprint) ? throw new InvalidOperationException("Insights:Entra:CertificateThumbprint is not set.") : e.CertificateThumbprint.Replace(" ", "").Trim(),
        },
    };

    static bool IsBearer(HttpRequest r) => r.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

    /// <summary>Signs the person out of the dashboard and of Entra ID, and back to the start page.</summary>
    public static IResult SignOut(HttpContext ctx) =>
        Results.SignOut(new AuthenticationProperties { RedirectUri = (ctx.Request.PathBase.Value ?? "") + "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
}
