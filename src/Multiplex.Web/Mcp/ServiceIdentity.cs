using System.Buffers.Binary;
using System.Collections.Concurrent;
using ModelContextProtocol.Protocol;
using Multiplex.Web.Auth;

namespace Multiplex.Web.Mcp;

/// <summary>
/// What a service tells a client about itself (#135), in the <c>serverInfo</c> of its <c>initialize</c>
/// answer (MCP 2025-11-25): its name, the website it wraps and that website's icon. A client such as ChatGPT
/// shows these; without them it shows a placeholder for every service.
/// </summary>
/// <remarks>
/// The name and website come from the service's registration
/// (<see cref="MultiplexAuthBuilder.RequireAuthFor"/>). The icon is the file <c>wwwroot/icons/{slug}.png</c>,
/// served at <c>/icons/{slug}.png</c> on this server's public address: the spec says a client should trust
/// icons from the server's own origin and fetch them without credentials, so that folder is open to anyone
/// (<see cref="Identity.AccessGate"/>). It is a square PNG, the one format every client that shows icons must
/// accept, and its size is read from the file.
/// </remarks>
public sealed class ServiceIdentity(AuthenticatedEndpoints endpoints, AuthIssuer issuer, IWebHostEnvironment environment)
{
    /// <summary>The folder service icons are served from, open to anyone.</summary>
    public const string IconsPath = "/icons";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly string Version = typeof(ServiceIdentity).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    // One instance per service, handed to every request's options: read it, never change it.
    private readonly ConcurrentDictionary<string, Implementation> _bySlug = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The <c>serverInfo</c> for the service at <c>/{slug}/mcp</c>, or null for a slug not registered.</summary>
    public Implementation? ServerInfo(string slug) =>
        endpoints.FindBySlug(slug) is { } endpoint ? _bySlug.GetOrAdd(endpoint.Slug, _ => Build(endpoint)) : null;

    private Implementation Build(AuthenticatedEndpoint endpoint) => new()
    {
        Name = $"multiplex-{endpoint.Slug}",
        Title = endpoint.DisplayName,
        Version = Version,
        WebsiteUrl = endpoint.WebsiteUrl,
        Icons = IconOf(endpoint.Slug) is { } icon ? [icon] : null,
    };

    /// <summary>The service's icon, or null when it has none (or the file is not a PNG).</summary>
    private Icon? IconOf(string slug)
    {
        var file = environment.WebRootFileProvider.GetFileInfo($"{IconsPath.TrimStart('/')}/{slug}.png");
        if (!file.Exists) return null;

        // A PNG starts with its signature and then the IHDR chunk, whose data begins with width and height.
        var header = new byte[24];
        using (var stream = file.CreateReadStream())
        {
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length) return null;
        }
        if (!header.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature)) return null;
        var width = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));

        return new Icon
        {
            Source = $"{issuer.Issuer}{IconsPath}/{slug}.png",
            MimeType = "image/png",
            Sizes = [$"{width}x{height}"],
        };
    }
}
