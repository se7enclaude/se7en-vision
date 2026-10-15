using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using PhoneRemote.Capture;
using PhoneRemote.Input;

namespace PhoneRemote.Server;

/// <summary>
/// Local web server (127.0.0.1 only) that the Cloudflare tunnel forwards to.
/// Serves the phone page under the secret token and runs the control WebSocket.
/// </summary>
public sealed class RemoteServer : IAsyncDisposable
{
    private const string CookieName = "relais_session";

    private readonly SessionManager _session;
    private readonly InputInjector _input = new();
    private readonly ScreenCapturer _capturer = new();
    private readonly IFileProvider _phoneFiles;
    private WebApplication? _app;
    private PhoneConnection? _current;

    public int Port { get; private set; }

    public RemoteServer(SessionManager session)
    {
        _session = session;
        _phoneFiles = new ManifestEmbeddedFileProvider(typeof(RemoteServer).Assembly, "wwwroot/phone");
        _session.SessionRevoked += () => _current?.Close(4001, "revoked");
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Frame-Options"] = "DENY";
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            await next();
        });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = _phoneFiles, RequestPath = "/phone" });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new ManifestEmbeddedFileProvider(typeof(RemoteServer).Assembly, "wwwroot/ds"),
            RequestPath = "/ds",
        });

        app.MapGet("/s/{token}", (HttpContext ctx, string token) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (!_session.IsToken(token)) return ServeFile(ctx, "expired.html", StatusCodes.Status404NotFound);
            return ServeFile(ctx, "index.html", StatusCodes.Status200OK);
        });

        app.MapGet("/s/{token}/me", (HttpContext ctx, string token) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (!_session.IsToken(token)) return Results.NotFound();
            return Results.Json(new MeResponse(_session.IsSession(ctx.Request.Cookies[CookieName])), AppJson.Default.MeResponse);
        });

        app.MapPost("/s/{token}/pin", async (HttpContext ctx, string token) =>
        {
            if (!_session.IsToken(token)) return Results.NotFound();
            PinRequest? req = null;
            try { req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AppJson.Default.PinRequest); }
            catch (JsonException) { }

            var result = _session.TryPin(req?.Pin ?? "", out var sessionId);
            if (result == PinResult.Ok)
            {
                ctx.Response.Cookies.Append(CookieName, sessionId!, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict,
                    Secure = ctx.Request.Headers["X-Forwarded-Proto"] == "https",
                    Path = "/s/" + token,
                    IsEssential = true,
                });
            }
            else
            {
                await Task.Delay(600); // slow down guessing
            }
            int left = Math.Max(0, SessionManager.MaxAttempts - _session.FailedAttempts);
            return Results.Json(new PinResponse(result.ToString().ToLowerInvariant(), left), AppJson.Default.PinResponse);
        });

        app.Map("/s/{token}/ws", async (HttpContext ctx, string token) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest || !_session.IsToken(token) ||
                !_session.IsSession(ctx.Request.Cookies[CookieName]) || !SameOrigin(ctx.Request))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await RunConnection(ws, ctx.Request.Headers.UserAgent.ToString(), ctx.RequestAborted);
        });

        app.MapFallback((HttpContext ctx) => ServeFile(ctx, "expired.html", StatusCodes.Status404NotFound));

        await app.StartAsync();
        _app = app;
        Port = new Uri(app.Urls.First()).Port;
    }

    private IResult ServeFile(HttpContext ctx, string name, int status)
    {
        var file = _phoneFiles.GetFileInfo(name);
        using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream);
        return Results.Content(reader.ReadToEnd(), "text/html; charset=utf-8", Encoding.UTF8, status);
    }

    /// <summary>Rejects WebSocket connections opened by another site (cross-site WebSocket hijacking).</summary>
    private static bool SameOrigin(HttpRequest req)
    {
        var origin = req.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return false;
        return Uri.TryCreate(origin, UriKind.Absolute, out var o) &&
               string.Equals(o.Authority, req.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunConnection(WebSocket ws, string userAgent, CancellationToken aborted)
    {
        var conn = new PhoneConnection(ws, _input, _capturer);
        var previous = Interlocked.Exchange(ref _current, conn);
        previous?.Close(4002, "replaced");
        _session.SetConnected(true, DeviceName(userAgent));
        try
        {
            await conn.RunAsync(aborted);
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _current, null, conn) == conn)
            {
                lock (_input) _input.ReleaseAll();
                if (_session.PhoneConnected) _session.SetConnected(false);
            }
        }
    }

    private static string DeviceName(string ua) =>
        ua.Contains("iPhone") ? "iPhone" :
        ua.Contains("iPad") ? "iPad" :
        ua.Contains("Android") ? "Android" : "Navigateur";

    public async ValueTask DisposeAsync()
    {
        _current?.Close(1001, "closing");
        if (_app != null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _app.StopAsync(cts.Token);
            await _app.DisposeAsync();
        }
    }
}

public sealed record PinRequest(string? Pin);
public sealed record PinResponse(string Result, int AttemptsLeft);
public sealed record MeResponse(bool Authed);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(PinRequest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(PinResponse))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MeResponse))]
internal partial class AppJson : System.Text.Json.Serialization.JsonSerializerContext;
