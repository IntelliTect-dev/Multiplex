#:package ModelContextProtocol@2.4.0
#:property PublishAot=false
// An independent MCP client for checking a Multiplex endpoint: the official C# MCP SDK does
// discovery from the 401, dynamic client registration, PKCE, token exchange and every MCP
// call. The only thing supplied here is the part a person's browser plays on the connect
// pages (owner passcode, then "Continue as" an account the server already holds).
//
//   $env:MULTIPLEX_PASSCODE = (./scripts/Install-Multiplex.ps1 -ShowPasscode)
//   dotnet run scripts/McpSdkConnect.cs -- http://localhost:5190 tripit tripit_get_profile
//
// Exit code 0 when the tool call succeeds.
using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var baseUrl = (args.ElementAtOrDefault(0) ?? "http://localhost:5190").TrimEnd('/');
var slug = args.ElementAtOrDefault(1) ?? "tripit";
var toolName = args.ElementAtOrDefault(2) ?? "tripit_get_profile";
var passcode = Environment.GetEnvironmentVariable("MULTIPLEX_PASSCODE")
    ?? throw new InvalidOperationException("Set MULTIPLEX_PASSCODE.");

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri($"{baseUrl}/{slug}/mcp"),
    Name = "multiplex-sdk-check",
    OAuth = new ClientOAuthOptions
    {
        RedirectUri = new Uri("http://127.0.0.1:53998/callback"),
        DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "Multiplex SDK check" },
#pragma warning disable MCP9007 // The simpler delegate is enough here: the connect-pages driver below checks the redirect itself.
        AuthorizationRedirectDelegate = (authorizationUri, redirectUri, ct) => ConnectPages.RunAsync(authorizationUri, redirectUri, passcode, ct),
#pragma warning restore MCP9007
    },
}, loggerFactory: null);

await using var client = await McpClient.CreateAsync(transport);
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

/// <summary>Plays the person's browser on the connect pages, without following the final redirect.</summary>
static class ConnectPages
{
    public static async Task<string?> RunAsync(Uri authorizationUri, Uri redirectUri, string passcode, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        var url = authorizationUri;
        Dictionary<string, string>? post = null;
        for (var hop = 0; hop < 12; hop++)
        {
            using var request = new HttpRequestMessage(post is null ? HttpMethod.Get : HttpMethod.Post, url);
            if (post is not null) request.Content = new FormUrlEncodedContent(post);
            post = null;
            using var response = await http.SendAsync(request, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307)
            {
                var next = new Uri(url, response.Headers.Location!);
                if (next.GetLeftPart(UriPartial.Path) == redirectUri.GetLeftPart(UriPartial.Path))
                {
                    var q = HttpUtility.ParseQueryString(next.Query);
                    if (q["error"] is { } error) throw new InvalidOperationException($"authorization error: {error} {q["error_description"]}");
                    return q["code"];
                }
                url = next;
                continue;
            }
            var html = await response.Content.ReadAsStringAsync(ct);
            var back = Regex.Match(html, Regex.Escape(redirectUri.GetLeftPart(UriPartial.Path)) + "[^\"'<\\s]*");
            if (back.Success) { url = new Uri(WebUtility.HtmlDecode(back.Value)); continue; }

            var forms = Regex.Matches(html, "(?is)<form\\b(?<a>[^>]*)>(?<b>.*?)</form>").Select(m => m).ToList();
            var form = forms.FirstOrDefault(f => Regex.IsMatch(f.Groups["b"].Value, "(?i)name\\s*=\\s*\"[^\"]*passcode"))
                       ?? forms.FirstOrDefault(f => Regex.IsMatch(f.Value, "(?i)continue as"))
                       ?? throw new InvalidOperationException($"HTTP {(int)response.StatusCode} at {url} with no passcode or 'Continue as' form");
            post = [];
            foreach (Match input in Regex.Matches(form.Groups["b"].Value, "(?is)<input\\b[^>]*>"))
            {
                var name = Regex.Match(input.Value, "(?i)\\bname\\s*=\\s*\"(?<n>[^\"]*)\"").Groups["n"].Value;
                if (name.Length == 0) continue;
                post[name] = name.Contains("passcode", StringComparison.OrdinalIgnoreCase)
                    ? passcode
                    : WebUtility.HtmlDecode(Regex.Match(input.Value, "(?i)\\bvalue\\s*=\\s*\"(?<v>[^\"]*)\"").Groups["v"].Value);
            }
            var button = Regex.Match(form.Groups["b"].Value, "(?is)<button\\b[^>]*\\bname\\s*=\\s*\"(?<n>[^\"]+)\"[^>]*\\bvalue\\s*=\\s*\"(?<v>[^\"]*)\"");
            if (button.Success) post[button.Groups["n"].Value] = WebUtility.HtmlDecode(button.Groups["v"].Value);
            var action = WebUtility.HtmlDecode(Regex.Match(form.Groups["a"].Value, "(?i)action\\s*=\\s*\"(?<x>[^\"]*)\"").Groups["x"].Value);
            url = action.Length == 0 ? url : new Uri(url, action);
        }
        throw new InvalidOperationException("too many hops on the connect pages");
    }
}
