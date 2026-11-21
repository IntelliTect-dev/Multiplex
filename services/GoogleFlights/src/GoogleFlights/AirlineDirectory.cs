using System.Text.Json;
using Multiplex.Sites.GoogleFlights.Wire;

namespace Multiplex.Sites.GoogleFlights;

/// <summary>An airline in the data file.</summary>
/// <param name="Alliance">Its alliance as of the file's version; <c>null</c> when the file lists it in none.</param>
public sealed record AirlineInfo(string Code, string Name, Alliance? Alliance);

/// <summary>An alliance and the members the data file lists for it.</summary>
/// <param name="Code">The name Google's airline filter uses, e.g. <c>SKYTEAM</c>.</param>
public sealed record AllianceInfo(Alliance Alliance, string Code, string Name, IReadOnlyList<AirlineInfo> Members);

/// <summary>
/// Airlines and alliances from a versioned data file shipped with the client (<c>Data/airlines.json</c>).
/// Alliance membership is a snapshot as of <see cref="Version"/>, not a live source.
/// </summary>
public sealed class AirlineDirectory
{
    private static readonly Lazy<AirlineDirectory> Shipped = new(Load);

    private readonly Dictionary<string, AirlineInfo> _byCode;

    private AirlineDirectory(string version, IReadOnlyList<AirlineInfo> airlines, IReadOnlyList<AllianceInfo> alliances)
    {
        Version = version;
        All = airlines;
        Alliances = alliances;
        _byCode = airlines.ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The data file shipped with the client.</summary>
    public static AirlineDirectory Default => Shipped.Value;

    /// <summary>The data file's version: the date its alliance memberships were last checked.</summary>
    public string Version { get; }

    public IReadOnlyList<AirlineInfo> All { get; }

    public IReadOnlyList<AllianceInfo> Alliances { get; }

    /// <summary>The airline with an IATA code, or <c>null</c>.</summary>
    public AirlineInfo? Find(string code) => _byCode.GetValueOrDefault((code ?? "").Trim());

    /// <summary>
    /// Airlines matching a query: an IATA code (<c>DL</c>), an alliance (<c>SkyTeam</c>, <c>star alliance</c>,
    /// <c>STAR_ALLIANCE</c>) for its members, or part of a name (<c>delta</c>). An empty query lists every airline.
    /// </summary>
    public IReadOnlyList<AirlineInfo> Lookup(string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0)
            return All;
        if (Find(q) is { } exact)
            return [exact];
        var key = Squash(q);
        if (Alliances.FirstOrDefault(a => Squash(a.Code) == key || Squash(a.Name) == key) is { } alliance)
            return alliance.Members;
        return All.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static string Squash(string s) => new([.. s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant)]);

    private static AirlineDirectory Load()
    {
        using var stream = typeof(AirlineDirectory).Assembly.GetManifestResourceStream("Multiplex.Sites.GoogleFlights.airlines.json")
            ?? throw new InvalidOperationException("The airline data file is missing from the client assembly.");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var byGoogleName = Enum.GetValues<Alliance>().ToDictionary(RequestEncoder.AllianceName, a => a, StringComparer.Ordinal);
        var airlines = root.GetProperty("airlines").EnumerateArray()
            .Select(a => new AirlineInfo(
                a.GetProperty("code").GetString()!,
                a.GetProperty("name").GetString()!,
                a.TryGetProperty("alliance", out var al) ? byGoogleName[al.GetString()!] : null))
            .ToArray();
        var alliances = root.GetProperty("alliances").EnumerateArray()
            .Select(a =>
            {
                var code = a.GetProperty("code").GetString()!;
                var alliance = byGoogleName[code];
                return new AllianceInfo(alliance, code, a.GetProperty("name").GetString()!, airlines.Where(x => x.Alliance == alliance).ToArray());
            })
            .ToArray();
        return new AirlineDirectory(root.GetProperty("version").GetString()!, airlines, alliances);
    }
}
