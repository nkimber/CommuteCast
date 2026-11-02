using System.Text.Json.Serialization;
using CommuteCast.Web;
using CommuteCast.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5188");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
builder.Services.AddRazorPages();
builder.Services.AddDataProtection().SetApplicationName("CommuteCast.Web");
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddAntiforgery(options => { options.HeaderName = "X-CSRF-TOKEN"; options.Cookie.Name = "CommuteCast.Csrf"; options.Cookie.SameSite = SameSiteMode.Strict; });
builder.Services.AddHttpClient("Microsoft365", client => client.Timeout = TimeSpan.FromMinutes(10)).RemoveAllLoggers();
builder.Services.AddSingleton<IM365Publisher>(services => new M365Publisher(services.GetRequiredService<IHttpClientFactory>().CreateClient("Microsoft365"), d => WindowsWebSecrets.Get(WindowsWebSecrets.Target(d))));
builder.Services.AddSingleton<WebRuntime>();
builder.Services.AddSingleton<IWebSpeechFactory, WebSpeechFactory>();
builder.Services.AddHostedService(services => services.GetRequiredService<WebRuntime>());
builder.Services.AddCommuteCastAuthentication(builder.Configuration);
var app = builder.Build();
if (!string.IsNullOrWhiteSpace(app.Configuration["Cloudflare:TeamDomain"]))
    _ = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>().Get("Bearer");
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error ?? new Exception();
    context.Response.StatusCode = error is TimeoutException ? 503 : error is ArgumentException or IOException or AntiforgeryValidationException ? 400 : 500;
    await context.Response.WriteAsJsonAsync(new { error = error is AntiforgeryValidationException
        ? "Your form session expired. Copy any unsaved text, then refresh the page before trying again."
        : WebRuntime.SafeError(error) });
}));
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; media-src 'none'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
    await next();
});
// Audio and workspaces are never exposed through static-file providers.
app.UseStaticFiles();
app.MapRazorPages().RequireAuthorization();
app.MapCommuteCastApi();
app.Run();

public partial class Program;
