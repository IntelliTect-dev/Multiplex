using Multiplex.Web.Auth;
using Multiplex.Web.HtmlSites;
using Multiplex.Web.Identity;
using Multiplex.Web.Mcp;
using Multiplex.Web.Services.Amazon;
using Multiplex.Web.Services.GoogleFlights;
using Multiplex.Web.Services.LinkedIn;
using Multiplex.Web.Services.LinkedIn.SignIn;
using Multiplex.Web.Services.TripIt;
using Multiplex.Web.Services.TripIt.SignIn;
using Multiplex.Web.SignIn;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Multiplex.Web.Browser.BrowserService>();
// Every service needs sign-in (#80). The ones that act on a person's site account connect by signing
// in to that site; Time and Amazon act for no one, so connecting them is a consent page.
builder.Services.AddMultiplexAuth(builder.Configuration)
    .RequireAuthFor("tripit", "TripIt")
    .RequireAuthFor("linkedin", "LinkedIn")
    .RequireAuthFor("time", "Time", needsSiteAccount: false)
    .RequireAuthFor("amazon", "Amazon", needsSiteAccount: false);
builder.Services.AddTripItService(builder.Configuration);
builder.Services.AddLinkedInService(builder.Configuration);
builder.Services.AddAmazonService();
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

// A service served without sign-in would be open to the internet once a tunnel publishes the server.
McpEndpointRouting.EnsureEveryEndpointRequiresSignIn(app.Services.GetRequiredService<AuthenticatedEndpoints>());

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
app.MapMultiplexSignOut();
app.MapMcpEndpoints();
app.MapConnectPages();
app.MapSiteHandOverPages();

app.Run();

// Exposed so WebApplicationFactory<Program> can reach the entry point from the test project.
public partial class Program;
