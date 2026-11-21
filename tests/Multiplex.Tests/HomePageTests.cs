using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Multiplex.Web.Auth;

namespace Multiplex.Tests;

/// <summary>
/// The home page lets a signed-in person add each service to Claude or ChatGPT from the browser,
/// on a phone or a laptop alike, with no script to run (#114). It is opened here from the host itself,
/// which needs no sign-in; a signed-in person sees the same list.
/// </summary>
public class HomePageTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    private IReadOnlyList<AuthenticatedEndpoint> Services => factory.Services.GetRequiredService<AuthenticatedEndpoints>().All;

    /// <summary>The list item for one service.</summary>
    private static string ItemFor(string html, string slug)
    {
        var item = Regex.Match(html, $"(?s)<li data-service=\"{Regex.Escape(slug)}\">(.*?)</li>");
        Assert.True(item.Success, $"no list item for {slug}");
        return item.Groups[1].Value;
    }

    /// <summary>The opening tag of the item's element whose text is <paramref name="text"/>.</summary>
    private static string TagWithText(string item, string element, string text)
    {
        var tag = Regex.Match(item, $"(?s)(<{element}\\b[^>]*>)\\s*{Regex.Escape(text)}\\s*</{element}>");
        Assert.True(tag.Success, $"no <{element}> reading \"{text}\" in:\n{item}");
        return tag.Groups[1].Value;
    }

    private static string? Attribute(string tag, string name) =>
        Regex.Match(tag, $"\\s{Regex.Escape(name)}=\"([^\"]*)\"") is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    [Fact]
    public async Task Lists_each_service_by_name_with_its_full_public_address()
    {
        var html = await _client.GetStringAsync("/");

        Assert.True(Services.Count >= 2, "the app registers more than one service");
        foreach (var service in Services)
        {
            var item = ItemFor(html, service.Slug);
            Assert.Contains(WebUtility.HtmlEncode(service.DisplayName), item);
            Assert.Contains($"<code>{WebUtility.HtmlEncode(service.Resource)}</code>", item);
        }
    }

    [Fact]
    public async Task Add_to_Claude_opens_Claudes_add_connector_dialog_filled_in_with_the_service()
    {
        var html = await _client.GetStringAsync("/");

        foreach (var service in Services)
        {
            var link = TagWithText(ItemFor(html, service.Slug), "a", "Add to Claude");
            var href = new Uri(Attribute(link, "href")!);
            Assert.Equal("https://claude.ai/customize/connectors", href.GetLeftPart(UriPartial.Path));
            var query = QueryHelpers.ParseQuery(href.Query);
            Assert.Equal("add-custom-connector", query["modal"].ToString());
            Assert.Equal(service.DisplayName, query["connectorName"].ToString());
            Assert.Equal(service.Resource, query["connectorUrl"].ToString());
            Assert.Equal("_blank", Attribute(link, "target"));
            Assert.Contains("noopener", Attribute(link, "rel"));
        }
    }

    [Fact]
    public async Task Add_to_ChatGPT_opens_ChatGPTs_add_page_and_copies_the_address_which_it_cannot_prefill()
    {
        var html = await _client.GetStringAsync("/");

        foreach (var service in Services)
        {
            var link = TagWithText(ItemFor(html, service.Slug), "a", "Add to ChatGPT");
            Assert.Equal("https://chatgpt.com/plugins", Attribute(link, "href"));
            Assert.Equal(service.Resource, Attribute(link, "data-copy"));
            Assert.Equal("_blank", Attribute(link, "target"));
            Assert.Contains("noopener", Attribute(link, "rel"));
        }
    }

    [Fact]
    public async Task Copy_address_copies_the_services_full_address()
    {
        var html = await _client.GetStringAsync("/");

        foreach (var service in Services)
        {
            var button = TagWithText(ItemFor(html, service.Slug), "button", "Copy address");
            Assert.Equal("button", Attribute(button, "type"));
            Assert.Equal(service.Resource, Attribute(button, "data-copy"));
        }
    }

    [Fact]
    public async Task The_page_loads_the_script_that_copies_each_data_copy_address_to_the_clipboard()
    {
        var html = await _client.GetStringAsync("/");
        // MapStaticAssets fingerprints the name (home.<hash>.js) so browsers can cache it for good.
        var src = Regex.Match(html, "<script src=\"(/js/home(?:\\.[a-z0-9]+)?\\.js[^\"]*)\"");
        Assert.True(src.Success, $"the page loads /js/home.js; its scripts: {string.Join(" ", Regex.Matches(html, "<script[^>]*>").Select(m => m.Value))}");

        var script = await _client.GetAsync(WebUtility.HtmlDecode(src.Groups[1].Value));

        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        var js = await script.Content.ReadAsStringAsync();
        Assert.Contains("data-copy", js);
        Assert.Contains("navigator.clipboard.writeText", js);
    }

    [Fact]
    public async Task Each_action_is_described_by_its_services_name_so_a_screen_reader_can_tell_them_apart()
    {
        // The visible text stays the accessible name, so "Copied" is heard too (review of #114).
        var html = await _client.GetStringAsync("/");

        foreach (var service in Services)
        {
            var item = ItemFor(html, service.Slug);
            foreach (var (element, text) in new[] { ("a", "Add to Claude"), ("a", "Add to ChatGPT"), ("button", "Copy address") })
            {
                var id = Attribute(TagWithText(item, element, text), "aria-describedby");
                Assert.False(string.IsNullOrEmpty(id), $"\"{text}\" for {service.Slug} names no description");
                Assert.Matches($"<[a-z]+ id=\"{Regex.Escape(id!)}\">{Regex.Escape(WebUtility.HtmlEncode(service.DisplayName))}</", item);
            }
        }
    }

    [Fact]
    public async Task The_addresses_come_from_the_configured_public_address_never_from_the_requests_Host()
    {
        // A loopback Host keeps the page open without sign-in; only its port differs from the server's own.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "localhost:47123";

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("47123", html);
        foreach (var service in Services) Assert.Contains($"<code>{WebUtility.HtmlEncode(service.Resource)}</code>", html);
    }

    [Fact]
    public async Task Says_what_to_click_in_each_assistant()
    {
        var html = await _client.GetStringAsync("/");

        Assert.Contains("click Continue", html);
        Assert.Contains("paste the address", html);
        Assert.Contains("phone apps", html);
    }
}
