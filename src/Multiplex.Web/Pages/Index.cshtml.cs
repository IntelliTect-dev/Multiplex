using Microsoft.AspNetCore.Mvc.RazorPages;
using Multiplex.Web.Auth;

namespace Multiplex.Web.Pages;

/// <summary>
/// The home page: each service's MCP address, with a way to add it to Claude or ChatGPT from the browser,
/// on a phone or a laptop alike (#114). A connector added on the account also appears in the phone apps.
/// </summary>
public class IndexModel(AuthenticatedEndpoints endpoints) : PageModel
{
    /// <summary>
    /// ChatGPT's page for adding a custom MCP server. It takes no prefilled address, so the button that
    /// opens it also copies the address.
    /// </summary>
    public const string ChatGptAddPage = "https://chatgpt.com/plugins";

    private const string ClaudeConnectorsPage = "https://claude.ai/customize/connectors";

    /// <summary>Every service, with its full public address, in registration order.</summary>
    public IReadOnlyList<AuthenticatedEndpoint> Services => endpoints.All;

    /// <summary>Claude's own "Add custom connector" dialog, with the service's name and address filled in (as #99 found).</summary>
    public static string ClaudeAddLink(AuthenticatedEndpoint service) =>
        $"{ClaudeConnectorsPage}?modal=add-custom-connector&connectorName={Uri.EscapeDataString(service.DisplayName)}&connectorUrl={Uri.EscapeDataString(service.Resource)}";
}
