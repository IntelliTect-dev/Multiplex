#:package ModelContextProtocol@2.4.0
#:property PublishAot=false
// An independent MCP client for checking a Multiplex endpoint: the official C# MCP SDK does
// discovery from the 401, dynamic client registration, PKCE, token exchange and every MCP
// call. The only part supplied here is the person's browser on the authorization request.
// Since #80 that request sends a browser that is not signed in to Microsoft sign-in, which a
// script cannot complete for a person, so this check confirms the redirect (to
// login.microsoftonline.com, with this server's /signin-microsoft redirect URI and a client
// id) and stops there, saying so. A server where Microsoft sign-in is not set up answers 503.
//
//   dotnet run scripts/McpSdkConnect.cs -- http://localhost:5190 tripit tripit_get_profile
//
// Exit code 0 when every step it can run passes.
using System.Web;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var baseUrl = (args.ElementAtOrDefault(0) ?? "http://localhost:5190").TrimEnd('/');
var slug = args.ElementAtOrDefault(1) ?? "tripit";
var toolName = args.ElementAtOrDefault(2) ?? "tripit_get_profile";

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri($"{baseUrl}/{slug}/mcp"),
    Name = "multiplex-sdk-check",
    OAuth = new ClientOAuthOptions
    {
        RedirectUri = new Uri("http://127.0.0.1:53998/callback"),
        DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "Multiplex SDK check" },
#pragma warning disable MCP9007 // The simpler delegate is enough here: the authorization driver below checks the redirect itself.
        AuthorizationRedirectDelegate = (authorizationUri, redirectUri, ct) => MicrosoftSignIn.RunAsync(authorizationUri, ct),
#pragma warning restore MCP9007
    },
}, loggerFactory: null);

McpClient client;
try
{
    client = await McpClient.CreateAsync(transport);
}
catch (Exception) when (MicrosoftSignIn.Reached)
{
    Console.WriteLine("STOP  the remaining steps need a person: signing in with Microsoft, with an allowed email, ends with the code " +
        "that the token exchange and every MCP call need. This check cannot sign in for a person, so it ends here.");
    return 0;
}
// Not reached since #80 (above): connecting needs the code a person's Microsoft sign-in ends with.
await using (client)
{
    Console.WriteLine($"PASS  connected: {client.ServerInfo?.Name} {client.ServerInfo?.Version}");
    var tools = await client.ListToolsAsync();
    Console.WriteLine($"PASS  tools/list: {tools.Count} tools");
    var result = await client.CallToolAsync(toolName, new Dictionary<string, object?>());
    var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
    if (result.IsError == true)
    {
        Console.WriteLine($"FAIL  {toolName}: {text[..Math.Min(300, text.Length)]}");
        return 1;
    }
    Console.WriteLine($"PASS  {toolName}: {text.Length} characters of result");
    return 0;
}

/// <summary>
/// Plays a browser that is not signed in on the authorization request, without following the
/// redirect off this server: it must go to Microsoft sign-in, which a script cannot complete.
/// </summary>
static class MicrosoftSignIn
{
    /// <summary>Whether the authorization request was seen to send the person to Microsoft sign-in.</summary>
    public static bool Reached { get; private set; }

    public static async Task<string?> RunAsync(Uri authorizationUri, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new System.Net.CookieContainer() };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var expectedRedirect = $"{authorizationUri.GetLeftPart(UriPartial.Authority)}/signin-microsoft";
        var url = authorizationUri;
        for (var hop = 0; hop < 5; hop++)
        {
            using var response = await http.GetAsync(url, ct);
            var status = (int)response.StatusCode;
            if (status == 503)
                throw new InvalidOperationException(
                    $"HTTP 503 at {url}: the server says Microsoft sign-in is not set up. Set Multiplex__Identity__Microsoft__ClientId, " +
                    "Multiplex__Identity__Microsoft__ClientSecret and Multiplex__Identity__AllowedEmails for the Windows account that runs " +
                    "Multiplex (the installer shows how), then restart the Multiplex task.");
            if (status is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is null)
                throw new InvalidOperationException($"HTTP {status} at {url}, not a redirect to Microsoft sign-in.");
            var next = new Uri(url, response.Headers.Location);
            if (next.Authority == url.Authority) { url = next; continue; }   // a hop on this server
            if (!next.AbsoluteUri.StartsWith("https://login.microsoftonline.com/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Redirected to {next}, not to Microsoft sign-in.");
            var q = HttpUtility.ParseQueryString(next.Query);
            if (!string.Equals(q["redirect_uri"], expectedRedirect, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Microsoft sign-in is asked to send the person back to '{q["redirect_uri"]}', not to '{expectedRedirect}'.");
            if (string.IsNullOrEmpty(q["client_id"]))
                throw new InvalidOperationException("The redirect to Microsoft sign-in names no client_id.");
            Console.WriteLine("PASS  authorize sends the person to Microsoft sign-in");
            Console.WriteLine($"      Microsoft client_id {q["client_id"]}; redirect_uri {q["redirect_uri"]}");
            Reached = true;
            throw new InvalidOperationException("Microsoft sign-in needs a person.");
        }
        throw new InvalidOperationException("Too many hops on the authorization request.");
    }
}
