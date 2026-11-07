using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TripIt;

var builder = Host.CreateApplicationBuilder(args);

// MCP servers using stdio must log to stderr only
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<TripItSession>();

// The session's Akamai bot cookies last ~2 hours. When TripIt rejects the session the
// client first re-reads ~/.tripit-mcp/session.json, then runs the refresher (by default
// `node tools/extract-session.js`, found beside/above this assembly; override with
// TRIPIT_SESSION_REFRESH_COMMAND, or set it to "none" to disable).
builder.Services.AddSingleton(sp => new TripItClient(
    sp.GetRequiredService<TripItSession>(),
    sp.GetRequiredService<ILogger<TripItClient>>(),
    CommandSessionRefresher.FromEnvironment(
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("TripIt.SessionRefresh"))));

builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "tripit", Version = "0.2.0" })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
