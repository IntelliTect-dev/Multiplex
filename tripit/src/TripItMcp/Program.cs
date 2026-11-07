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

var mcp = builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "tripit", Version = "0.3.0" })
    .WithStdioServerTransport()
    .WithTools<TripItMcp.TripItTools>()
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) =>
    {
        try
        {
            return await next(context, ct);
        }
        catch (Exception ex) when (TripItMcp.ToolErrors.IsReportable(ex))
        {
            // The SDK hides exception text by default ("An error occurred invoking …"). These
            // messages are written for the caller — valid field lists, the trip's real name,
            // how to refresh a session — so pass them through.
            return TripItMcp.ToolErrors.ToResult(ex);
        }
    }));

// Write tools (create/update/delete/move) are on by default; TRIPIT_MCP_READONLY=1 serves
// the read tools only, for clients that should never change the account.
if (Environment.GetEnvironmentVariable("TRIPIT_MCP_READONLY") is not ("1" or "true"))
{
    mcp.WithTools<TripItMcp.TripItWriteTools>();
}

await builder.Build().RunAsync();
