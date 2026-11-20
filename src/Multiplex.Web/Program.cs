using Multiplex.Web.Auth;
using Multiplex.Web.Mcp;
using Multiplex.Web.Services.GoogleFlights;
using Multiplex.Web.Services.LinkedIn;
using Multiplex.Web.Services.LinkedIn.SignIn;
using Multiplex.Web.Services.TripIt;
using Multiplex.Web.Services.TripIt.SignIn;
using Multiplex.Web.SignIn;

// `hash-passcode` prints a connect-passcode hash for the installer and exits; no web server.
if (args is [PasscodeCli.Command, ..])
    return PasscodeCli.Run(Console.In, Console.Out, Console.Error, interactive: !Console.IsInputRedirected);

// `adopt-profile <site> <dir>` copies a signed-in browser profile into the data folder and exits.
var adoptArgs = args is [SiteProfileAdopter.Command, ..] ? args : null;

var builder = WebApplication.CreateBuilder(adoptArgs is null ? args : []);

builder.Services.AddRazorPages();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Multiplex.Web.Browser.BrowserService>();
builder.Services.AddMultiplexAuth(builder.Configuration)
    .RequireAuthFor("tripit", "TripIt")
    .RequireAuthFor("linkedin", "LinkedIn");
builder.Services.AddTripItService(builder.Configuration);
builder.Services.AddLinkedInService(builder.Configuration);
builder.Services.AddGoogleFlightsService(builder.Configuration);
builder.Services.AddSiteSignIn(builder.Configuration)
    .AddSite<TripItSignInDefinition>()
    .AddSite<LinkedInSignInDefinition>();

// Stateless Streamable HTTP: no Mcp-Session-Id, safe behind a load balancer without sticky sessions.
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.Stateless = true;
        options.ConfigureSessionOptions = McpEndpointRouting.ScopeToolsToEndpoint;
    })
    .WithToolsFromAssembly();

var app = builder.Build();

if (adoptArgs is not null)
    return await SiteProfileAdopter.RunCliAsync(app.Services, adoptArgs, Console.Out, Console.Error);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseWebSockets();

app.UseRouting();

app.UseMultiplexAuth();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();
app.MapHealthChecks("/healthz");
app.MapMultiplexAuth();
app.MapMcpEndpoints();
app.MapConnectPages();

app.Run();
return 0;

// Exposed so WebApplicationFactory<Program> can reach the entry point from the test project.
public partial class Program;
