using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TripIt;

var builder = Host.CreateApplicationBuilder(args);

// MCP servers using stdio must log to stderr only
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// The session's Akamai bot cookies last ~2 hours. When TripIt rejects the session the
// provider first re-reads ~/.tripit-mcp/session.json, then runs the refresher (by default
// `node tools/extract-session.js`, found beside/above this assembly; override with
// TRIPIT_SESSION_REFRESH_COMMAND, or set it to "none" to disable), at most once per
// cool-down. The client retries only when the cookie actually changed.
builder.Services.AddSingleton<ITripItSessionProvider>(sp =>
{
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("TripIt.Session");
    return new LocalSessionProvider(store: null, CommandSessionRefresher.FromEnvironment(log), log);
});
builder.Services.AddSingleton(sp => new TripItClient(
    sp.GetRequiredService<ITripItSessionProvider>(),
    sp.GetRequiredService<ILogger<TripItClient>>()));

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
            // short messages are written for the caller — valid field lists, the trip's real
            // name, how to refresh a session — so pass them through. Full detail stays in the
            // Debug log (stderr), never in the tool result.
            context.Services?.GetService<ILoggerFactory>()?.CreateLogger("TripItMcp.Tools")
                .LogDebug(ex, "Tool {Tool} failed.", context.Params?.Name);
            return TripItMcp.ToolErrors.ToResult(ex);
        }
    }));

// Write tools (create/update/delete/move) are on by default; TRIPIT_MCP_READONLY=1 serves
// the read tools only, for clients that should never change the account. tripit_raw_get
// goes with the write tools: some TripIt GETs change data (v2/move).
if (Environment.GetEnvironmentVariable("TRIPIT_MCP_READONLY") is not ("1" or "true"))
{
    mcp.WithTools<TripItMcp.TripItWriteTools>();
    mcp.WithTools<TripItMcp.TripItRawTools>();
}

await builder.Build().RunAsync();
