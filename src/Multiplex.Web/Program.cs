var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddHealthChecks();

// Stateless Streamable HTTP: no Mcp-Session-Id, safe behind a load balancer without sticky sessions.
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithToolsFromAssembly();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();
app.MapHealthChecks("/healthz");
app.MapMcp("/mcp");

app.Run();

// Exposed so WebApplicationFactory<Program> can reach the entry point from the test project.
public partial class Program;
