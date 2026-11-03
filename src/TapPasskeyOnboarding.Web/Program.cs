using System.Security.Claims;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using TapPasskeyOnboarding.Core.Abstractions;
using TapPasskeyOnboarding.Core.Eligibility;
using TapPasskeyOnboarding.Core.Graph;
using TapPasskeyOnboarding.Core.Security;
using TapPasskeyOnboarding.Core.Services;
using TapPasskeyOnboarding.Core.Telemetry;
using TapPasskeyOnboarding.Web.Graph;
using TapPasskeyOnboarding.Web.Reconciliation;
using TapPasskeyOnboarding.Web.Security;
using TapPasskeyOnboarding.Web.Services;

var builder = WebApplication.CreateBuilder(args);
var useMock = builder.Environment.IsEnvironment("Testing") ||
    (builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Graph:UseMock"));
var tenantId = ReadGuid(builder.Configuration, "AzureAd:TenantId", useMock);
var clientId = ReadGuid(builder.Configuration, "AzureAd:ClientId", useMock);
var groupId = ReadGuid(builder.Configuration, "Graph:OnboardingGroupId", useMock);
var completionGroupId = ReadGuid(builder.Configuration, "Graph:CompletionGroupId", useMock);
var graphEndpoint = GraphEndpointOptions.Create(builder.Configuration["Graph:BaseUrl"]);
var passwordAuthenticationMaxAge = TimeSpan.FromMinutes(15);

builder.Services.AddRazorPages(options => options.Conventions.AuthorizeFolder("/"));
builder.Services.AddAntiforgery(options =>
{
    var secureEnvironment = !builder.Environment.IsDevelopment() &&
        !builder.Environment.IsEnvironment("Testing");
    options.Cookie.Name = secureEnvironment
        ? "__Host-TapOnboarding.AntiForgery"
        : "TapOnboarding.AntiForgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = secureEnvironment
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    // Container Apps ingress addresses are platform-managed and not known at build time.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var authentication = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = useMock ? TestAuthHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = useMock ? TestAuthHandler.SchemeName : OpenIdConnectDefaults.AuthenticationScheme;
}).AddCookie(options =>
{
    options.Cookie.Name = "__Host-TapOnboarding.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.SlidingExpiration = false;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(15);
});

if (useMock)
{
    authentication.AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
}
else
{
    authentication.AddOpenIdConnect(options =>
    {
        options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        options.ClientId = clientId;
        options.MapInboundClaims = false;
        options.CallbackPath = builder.Configuration["AzureAd:CallbackPath"] ?? "/signin-oidc";
        // Sign-in-only id_token flow avoids storing a client secret or certificate.
        // The middleware validates signature, issuer, audience, lifetime, and nonce.
        options.ResponseType = OpenIdConnectResponseType.IdToken;
        options.ResponseMode = OpenIdConnectResponseMode.FormPost;
        options.UsePkce = false;
        options.Prompt = OpenIdConnectPrompt.Login;
        options.MaxAge = passwordAuthenticationMaxAge;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidateAudience = true;
        options.TokenValidationParameters.ValidateLifetime = true;
        options.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(1);
        options.Events.OnTokenValidated = context =>
        {
            var validator = context.HttpContext.RequestServices.GetRequiredService<PasswordAuthenticationValidator>();
            if (!validator.Validate(context.Principal!, tenantId, out var reason))
                context.Fail(reason);
            return Task.CompletedTask;
        };
        options.Events.OnRemoteFailure = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("OpenIdConnect");
            logger.LogWarning(context.Failure, "Remote authentication failed.");
            context.HandleResponse();
            context.Response.Redirect("/auth-error");
            return Task.CompletedTask;
        };
        options.Events.OnAuthenticationFailed = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("OpenIdConnect");
            logger.LogWarning(context.Exception, "Token authentication failed.");
            context.HandleResponse();
            context.Response.Redirect("/auth-error");
            return Task.CompletedTask;
        };
    });
}

builder.Services.AddAuthorization();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(graphEndpoint);
builder.Services.AddSingleton(new PasswordAuthenticationValidator(TimeProvider.System, passwordAuthenticationMaxAge));
builder.Services.AddSingleton(new EligibilityOptions
{
    OnboardingGroupId = groupId,
    CompletionGroupId = completionGroupId,
    BreakGlassObjectIds = ReadSet(builder.Configuration, "Security:BreakGlassObjectIds"),
    ServiceAccountObjectIds = ReadSet(builder.Configuration, "Security:ServiceAccountObjectIds"),
    ExclusionGroupIds = ReadSet(builder.Configuration, "Security:ExclusionGroupIds")
});
var tapLifetimeMinutes = builder.Configuration.GetValue(
    "Tap:LifetimeMinutes",
    TapIssuanceOptions.RequiredLifetimeMinutes);
if (tapLifetimeMinutes != TapIssuanceOptions.RequiredLifetimeMinutes)
    throw new InvalidOperationException($"Tap:LifetimeMinutes must be {TapIssuanceOptions.RequiredLifetimeMinutes}.");
builder.Services.AddSingleton(new TapIssuanceOptions { LifetimeMinutes = tapLifetimeMinutes });
builder.Services.AddSingleton<IActionNonceStore, MemoryActionNonceStore>();
builder.Services.AddSingleton<IRateLimitStore, MemoryRateLimitStore>();
builder.Services.AddSingleton<IAuditSink, LoggingAuditSink>();
builder.Services.AddScoped<EligibilityService>();
builder.Services.AddScoped<OnboardingService>();
builder.Services.AddScoped<ReconciliationService>();
builder.Services.AddOptions<ReconciliationOptions>()
    .Bind(builder.Configuration.GetSection(ReconciliationOptions.SectionName))
    .Validate(options => options.Interval >= TimeSpan.FromMinutes(1), "Reconciliation interval must be at least one minute.")
    .Validate(options => options.MaxConcurrency is >= 1 and <= 16, "Reconciliation max concurrency must be between 1 and 16.")
    .ValidateOnStart();
builder.Services.AddHostedService<ReconciliationHostedService>();

if (useMock)
{
    var mock = new MockSafeGraphDirectoryClient(TimeProvider.System);
    mock.AddEligibleUser(TestAuthHandler.TestObjectId);
    builder.Services.AddSingleton<IGraphDirectoryClient>(mock);
    builder.Services.AddSingleton<ICompletionTransitionDispatcher, InMemoryCompletionTransitionDispatcher>();
}
else
{
    var managedIdentityClientId = builder.Configuration["Graph:ManagedIdentityClientId"];
    TokenCredential credential;
    if (builder.Environment.IsDevelopment())
    {
        credential = string.IsNullOrWhiteSpace(managedIdentityClientId)
            ? new DefaultAzureCredential()
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = managedIdentityClientId });
    }
    else
    {
        var identityClientId = ReadGuid(builder.Configuration, "Graph:ManagedIdentityClientId", false);
        credential = string.IsNullOrWhiteSpace(builder.Configuration["Graph:ManagedIdentityClientId"])
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(identityClientId));
    }
    builder.Services.AddSingleton(credential);
    var transitionQueueUri = builder.Configuration["CompletionQueue:Uri"];
    if (!Uri.TryCreate(transitionQueueUri, UriKind.Absolute, out var queueUri) ||
        queueUri.Scheme != Uri.UriSchemeHttps)
        throw new InvalidOperationException("CompletionQueue:Uri must be a valid HTTPS URI.");
    builder.Services.AddSingleton(new QueueClient(
        queueUri,
        credential,
        new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }));
    builder.Services.AddSingleton<ICompletionTransitionDispatcher, QueueCompletionTransitionDispatcher>();
    builder.Services.AddTransient<GraphTokenHandler>();
    builder.Services.AddHttpClient<IGraphDirectoryClient, GraphHttpDirectoryClient>(client =>
    {
        client.BaseAddress = graphEndpoint.BaseUri;
        client.Timeout = TimeSpan.FromSeconds(30);
    }).AddHttpMessageHandler<GraphTokenHandler>().AddStandardResilienceHandler();
}

if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddOpenTelemetry().UseAzureMonitor();

var app = builder.Build();
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing")) app.UseExceptionHandler("/Error");
app.UseForwardedHeaders();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<DualRateLimitMiddleware>();
app.UseAuthorization();

app.MapGet("/auth-error", () => Results.Content(
    """
    <!doctype html>
    <html lang="pt-BR">
    <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Falha na autenticação</title></head>
    <body style="font-family:Segoe UI,Arial,sans-serif;max-width:640px;margin:64px auto;padding:24px">
      <h1>Não foi possível concluir a autenticação</h1>
      <p>Feche outras sessões Microsoft, abra uma janela InPrivate e tente novamente informando sua senha.</p>
      <p><a href="/">Tentar novamente</a></p>
    </body>
    </html>
    """, "text/html; charset=utf-8")).AllowAnonymous();
app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapGet("/health/ready", async (
    IGraphDirectoryClient graph,
    IAuditSink audit,
    EligibilityOptions options,
    CancellationToken ct) =>
{
    var checks = new Dictionary<string, bool>
    {
        ["configuration"] = Guid.TryParse(options.OnboardingGroupId, out _) &&
            Guid.TryParse(options.CompletionGroupId, out _) &&
            !string.Equals(options.OnboardingGroupId, options.CompletionGroupId, StringComparison.OrdinalIgnoreCase),
        ["graph"] = await SafeAsync(() => graph.IsReachableAsync(ct)),
        ["audit"] = await SafeAsync(() => audit.IsAvailableAsync(ct))
    };
    return checks.Values.All(v => v)
        ? Results.Ok(new { status = "healthy", checks })
        : Results.Json(new { status = "unhealthy", checks }, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();
app.MapRazorPages();
app.Run();

static HashSet<string> ReadSet(IConfiguration config, string section) =>
    config.GetSection(section).Get<string[]>()?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);

static string ReadGuid(IConfiguration config, string key, bool allowLocalDefault)
{
    var value = config[key];
    if (key == "Graph:ManagedIdentityClientId" && string.IsNullOrWhiteSpace(value))
        return string.Empty;
    if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty)
        return parsed.ToString();
    if (allowLocalDefault)
        return key switch
        {
            "Graph:OnboardingGroupId" => "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            "Graph:CompletionGroupId" => "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            _ => "11111111-1111-1111-1111-111111111111"
        };
    throw new InvalidOperationException($"{key} must be configured with a non-empty GUID.");
}

static async Task<bool> SafeAsync(Func<Task<bool>> operation)
{
    try { return await operation(); }
    catch { return false; }
}

public partial class Program;
