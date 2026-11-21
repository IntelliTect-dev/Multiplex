using System.Globalization;

namespace Multiplex.Sites.GoogleFlights;

/// <summary>An amount in an ISO 4217 currency.</summary>
public sealed record Money(decimal Amount, string Currency)
{
    public override string ToString() => $"{Amount.ToString("0.00", CultureInfo.InvariantCulture)} {Currency}";
}
