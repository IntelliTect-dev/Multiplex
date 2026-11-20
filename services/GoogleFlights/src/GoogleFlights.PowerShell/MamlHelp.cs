using System.ComponentModel;
using System.Management.Automation;
using System.Reflection;
using System.Xml.Linq;

namespace Multiplex.Sites.GoogleFlights.PowerShell;

/// <summary>
/// Builds the module's MAML help (what <c>Get-Help</c> reads) from the commands themselves: each command's
/// <see cref="DescriptionAttribute"/> and each parameter's <c>HelpMessage</c>, both of which are
/// <see cref="GoogleFlightsDocs"/>. The committed help file is this output; a test keeps the two identical.
/// </summary>
public static class MamlHelp
{
    public const string FileName = "Multiplex.Sites.GoogleFlights.PowerShell.dll-Help.xml";

    private static readonly XNamespace Msh = "http://msh";
    private static readonly XNamespace Maml = "http://schemas.microsoft.com/maml/2004/10";
    private static readonly XNamespace Command = "http://schemas.microsoft.com/maml/dev/command/2004/10";
    private static readonly XNamespace Dev = "http://schemas.microsoft.com/maml/dev/2004/10";

    private static readonly Dictionary<string, (string Code, string Remarks)[]> Examples = new()
    {
        ["Find-Flight"] =
        [
            ("Find-Flight SEA DEN 2026-12-15 -ReturnDate 2026-12-22 -MaxStops Nonstop", "Round-trip nonstop flights; the list is the outbound leg."),
            ("Find-Flight -Leg SEA-DEN-2026-12-15, DEN-LAX-2026-12-19 -Alliance SkyTeam", "A multi-city trip on SkyTeam airlines; the list is the first leg."),
        ],
        ["Get-FlightReturn"] =
        [
            ("Find-Flight SEA DEN 2026-12-15 -ReturnDate 2026-12-22 | Select-Object -First 1 | Get-FlightReturn", "Return flights after choosing the first outbound."),
        ],
        ["Get-FlightBookingOption"] =
        [
            ("Find-Flight SEA DEN 2026-12-15 | Select-Object -First 1 | Get-FlightBookingOption", "Where to buy the first one-way option."),
        ],
        ["Get-FlightPriceCalendar"] =
        [
            ("Get-FlightPriceCalendar SEA DEN 2026-12-01 2026-12-31 -TripLengthDays 7 | Sort-Object { $_.Price.Amount } | Select-Object -First 5", "The five cheapest week-long trips departing in December."),
        ],
        ["Find-Airport"] = [("Find-Airport Denver", "Airports for Denver.")],
        ["Get-Airline"] = [("Get-Airline 'star alliance'", "The Star Alliance members in the data file.")],
    };

    public static IEnumerable<Type> Commands =>
        typeof(MamlHelp).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.GetCustomAttribute<CmdletAttribute>() is not null)
            .OrderBy(t => Name(t), StringComparer.Ordinal);

    public static string Generate()
    {
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(Msh + "helpItems", new XAttribute("schema", "maml"),
                Commands.Select(Element)));
        using var writer = new Utf8StringWriter();
        doc.Save(writer);
        return writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static XElement Element(Type cmdlet)
    {
        var attribute = cmdlet.GetCustomAttribute<CmdletAttribute>()!;
        var description = cmdlet.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
        var parameters = Parameters(cmdlet).ToArray();
        var output = cmdlet.GetCustomAttribute<OutputTypeAttribute>()?.Type.FirstOrDefault()?.Type;

        return new XElement(Command + "command",
            new XAttribute(XNamespace.Xmlns + "maml", Maml),
            new XAttribute(XNamespace.Xmlns + "command", Command),
            new XAttribute(XNamespace.Xmlns + "dev", Dev),
            new XElement(Command + "details",
                new XElement(Command + "name", Name(cmdlet)),
                new XElement(Command + "verb", attribute.VerbName),
                new XElement(Command + "noun", attribute.NounName),
                Para(Maml + "description", FirstSentence(description))),
            Para(Maml + "description", description),
            new XElement(Command + "syntax",
                ParameterSets(parameters).Select(set => new XElement(Command + "syntaxItem",
                    new XElement(Maml + "name", Name(cmdlet)),
                    parameters.Where(p => p.Set is null || p.Set == set).Select(p => Parameter(p, set))))),
            new XElement(Command + "parameters", parameters.DistinctBy(p => p.Property.Name).Select(p => Parameter(p, p.Set))),
            output is null ? null : new XElement(Command + "returnValues",
                new XElement(Command + "returnValue", new XElement(Dev + "type", new XElement(Maml + "name", output.FullName)))),
            new XElement(Command + "examples", Examples.GetValueOrDefault(Name(cmdlet), []).Select((e, i) =>
                new XElement(Command + "example",
                    new XElement(Maml + "title", $"-------------------------- Example {i + 1} --------------------------"),
                    new XElement(Dev + "code", e.Code),
                    new XElement(Dev + "remarks", new XElement(Maml + "para", e.Remarks))))));
    }

    private static XElement Parameter((PropertyInfo Property, ParameterAttribute Attribute, string? Set) p, string? set)
    {
        var a = p.Property.GetCustomAttributes<ParameterAttribute>().FirstOrDefault(x => x.ParameterSetName == set || set is null) ?? p.Attribute;
        var type = p.Property.PropertyType;
        var isSwitch = type == typeof(SwitchParameter);
        var pipeline = a.ValueFromPipeline ? "True (ByValue)" : a.ValueFromPipelineByPropertyName ? "True (ByPropertyName)" : "False";
        return new XElement(Command + "parameter",
            new XAttribute("required", a.Mandatory ? "true" : "false"),
            new XAttribute("globbing", "false"),
            new XAttribute("pipelineInput", pipeline),
            new XAttribute("position", a.Position >= 0 ? (a.Position).ToString(System.Globalization.CultureInfo.InvariantCulture) : "named"),
            new XElement(Maml + "name", p.Property.Name),
            Para(Maml + "description", a.HelpMessage ?? ""),
            isSwitch ? null : new XElement(Command + "parameterValue", new XAttribute("required", "true"), TypeName(type)),
            new XElement(Dev + "type", new XElement(Maml + "name", (Nullable.GetUnderlyingType(type) ?? type).FullName)));
    }

    private static IEnumerable<(PropertyInfo Property, ParameterAttribute Attribute, string? Set)> Parameters(Type cmdlet) =>
        cmdlet.GetProperties()
            .SelectMany(p => p.GetCustomAttributes<ParameterAttribute>().Select(a =>
                (p, a, a.ParameterSetName == ParameterAttribute.AllParameterSets ? null : a.ParameterSetName)))
            .OrderBy(x => x.Item2.Position >= 0 ? x.Item2.Position : int.MaxValue)
            .ThenBy(x => x.p.MetadataToken);

    private static IEnumerable<string?> ParameterSets(IEnumerable<(PropertyInfo Property, ParameterAttribute Attribute, string? Set)> parameters)
    {
        var sets = parameters.Select(p => p.Set).Where(s => s is not null).Distinct().ToArray();
        return sets.Length == 0 ? [null] : sets;
    }

    private static string TypeName(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsArray ? t.GetElementType()!.Name + "[]" : t.Name;
    }

    private static string Name(Type cmdlet)
    {
        var a = cmdlet.GetCustomAttribute<CmdletAttribute>()!;
        return $"{a.VerbName}-{a.NounName}";
    }

    private static string FirstSentence(string text)
    {
        for (var end = text.IndexOf(". ", StringComparison.Ordinal); end >= 0; end = text.IndexOf(". ", end + 1, StringComparison.Ordinal))
        {
            // "e.g. " and "i.e. " end no sentence.
            if (!text[..(end + 1)].EndsWith("e.g.", StringComparison.Ordinal) && !text[..(end + 1)].EndsWith("i.e.", StringComparison.Ordinal))
                return text[..(end + 1)];
        }
        return text;
    }

    private static XElement Para(XName name, string text) => new(name, new XElement(Maml + "para", text));

    private sealed class Utf8StringWriter : StringWriter
    {
        public override System.Text.Encoding Encoding => new System.Text.UTF8Encoding(false);
    }
}
