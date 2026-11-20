using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Multiplex.Sites.GoogleFlights;

/// <summary>One segment of a chosen flight, as Google identifies it: from, date, to, carrier, number.</summary>
public sealed record ChosenSegment(string From, DateOnly Date, string To, string Carrier, string FlightNumber);

/// <summary>
/// The flights chosen so far on a search, and Google's token for the last choice. Every
/// <see cref="FlightOption"/> carries the selection that choosing it makes. Pass it to
/// <see cref="GoogleFlightsClient.GetReturnFlightsAsync"/> for the next leg's flights, or, once every leg
/// is chosen, to <see cref="GoogleFlightsClient.GetBookingOptionsAsync"/>.
/// </summary>
/// <param name="Request">The normalized search the choices were made on.</param>
/// <param name="ChosenLegs">One list of segments per leg chosen so far, in leg order.</param>
/// <param name="Token">Google's token for the last choice. Opaque: it is passed back unchanged.</param>
public sealed partial record FlightSelection(FlightSearchRequest Request, IReadOnlyList<IReadOnlyList<ChosenSegment>> ChosenLegs, string Token)
{
    /// <summary>Whether a flight is chosen for every leg, so booking options can be asked for.</summary>
    public bool IsComplete => ChosenLegs.Count == Request.Legs.Count;

    /// <summary>The index of the next leg to choose, or the leg count when complete.</summary>
    public int NextLeg => ChosenLegs.Count;

    /// <summary>
    /// A compact text form of this selection (URL-safe base64 of JSON), for callers that cannot hold the object
    /// between calls, such as an MCP tool. It holds no secret: the search, the chosen flights and Google's token.
    /// </summary>
    public string ToHandle()
    {
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            JsonSerializer.Serialize(brotli, this, HandleJson.Options);
        return Convert.ToBase64String(compressed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Reads a handle made by <see cref="ToHandle"/>. The search inside is validated again.</summary>
    /// <exception cref="ArgumentException">The handle is not one this client made.</exception>
    public static FlightSelection FromHandle(string handle)
    {
        FlightSelection? selection;
        try
        {
            var base64 = (handle ?? "").Trim().Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            using var brotli = new BrotliStream(new MemoryStream(Convert.FromBase64String(base64)), CompressionMode.Decompress);
            // A handle is a few hundred bytes; stop reading long before a hostile one could exhaust memory.
            var json = new byte[64 * 1024];
            var length = 0;
            int read;
            while ((read = brotli.Read(json, length, json.Length - length)) > 0)
            {
                length += read;
                if (length == json.Length)
                    throw new FormatException("The handle is too large.");
            }
            selection = JsonSerializer.Deserialize<FlightSelection>(json.AsSpan(0, length), HandleJson.Options);
        }
        // BrotliStream reports corrupt input as InvalidOperationException ("Decoder ran into invalid data").
        catch (Exception e) when (e is FormatException or JsonException or NotSupportedException or InvalidDataException
                                      or IOException or InvalidOperationException)
        {
            throw new ArgumentException("That is not a flight selection from this client. Search again and pass a selection it returned.", nameof(handle), e);
        }
        if (selection?.Request is null || selection.ChosenLegs is null || string.IsNullOrEmpty(selection.Token))
            throw new ArgumentException("That is not a flight selection from this client. Search again and pass a selection it returned.", nameof(handle));

        var request = selection.Request.Normalize(selection.Request.Currency ?? "USD", selection.Request.Region ?? "US");
        var checkedSelection = selection with { Request = request };
        checkedSelection.Validate();
        return checkedSelection;
    }

    /// <summary>
    /// Checks that the choices fit the search and look like flights Google listed, before any of it is sent:
    /// a selection can come from outside the process (an MCP client), and a request Google will not take can
    /// earn a refusal that stops the shared client for everyone.
    /// </summary>
    /// <exception cref="ArgumentException">The selection is not one this client could have made.</exception>
    internal void Validate()
    {
        const string Again = " Search again and pass a selection it returned.";
        if (ChosenLegs is null || ChosenLegs.Count == 0 || ChosenLegs.Count > Request.Legs.Count)
            throw new ArgumentException("The selection's chosen flights do not fit its search." + Again);
        if (string.IsNullOrEmpty(Token) || Token.Length > 2048 || !TokenPattern().IsMatch(Token))
            throw new ArgumentException("The selection's token is not one Google issued." + Again);
        foreach (var leg in ChosenLegs)
        {
            if (leg is null || leg.Count is 0 or > 8)
                throw new ArgumentException("The selection's chosen flights do not fit its search." + Again);
            foreach (var s in leg)
            {
                if (s is null || !AirportPattern().IsMatch(s.From ?? "") || !AirportPattern().IsMatch(s.To ?? "")
                    || !CarrierPattern().IsMatch(s.Carrier ?? "") || !FlightNumberPattern().IsMatch(s.FlightNumber ?? ""))
                {
                    throw new ArgumentException("The selection holds a flight that is not in Google's form." + Again);
                }
            }
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9+/=_#-]+$")]
    private static partial System.Text.RegularExpressions.Regex TokenPattern();

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Z]{3}$")]
    private static partial System.Text.RegularExpressions.Regex AirportPattern();

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Z0-9]{2}$")]
    private static partial System.Text.RegularExpressions.Regex CarrierPattern();

    [System.Text.RegularExpressions.GeneratedRegex("^[0-9]{1,4}[A-Z]?$")]
    private static partial System.Text.RegularExpressions.Regex FlightNumberPattern();
}

internal static class HandleJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() },
    };
}
