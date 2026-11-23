using System.Text.RegularExpressions;

namespace Multiplex.Web.HtmlSites;

/// <summary>
/// A parsed robots.txt, read the way RFC 9309 says: groups of user agents, <c>Allow</c> and
/// <c>Disallow</c> rules with <c>*</c> and a closing <c>$</c>, the longest match winning and
/// <c>Allow</c> winning a tie.
/// </summary>
public sealed class RobotsRules
{
    private readonly List<(List<string> Agents, List<(bool Allow, string Pattern)> Rules)> _groups;

    private RobotsRules(List<(List<string>, List<(bool, string)>)> groups) => _groups = groups;

    /// <summary>Rules that allow everything: what a missing robots.txt means.</summary>
    public static RobotsRules AllowAll { get; } = new([]);

    public static RobotsRules Parse(string text)
    {
        var groups = new List<(List<string> Agents, List<(bool, string)> Rules)>();
        (List<string> Agents, List<(bool, string)> Rules)? current = null;
        var lastWasAgent = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (field)
            {
                case "user-agent":
                    // Consecutive User-agent lines share the rules that follow them.
                    if (!lastWasAgent || current is null)
                    {
                        current = ([], []);
                        groups.Add(current.Value);
                    }
                    current.Value.Agents.Add(value.ToLowerInvariant());
                    lastWasAgent = true;
                    break;
                case "allow" or "disallow":
                    lastWasAgent = false;
                    // An empty Disallow allows everything, which is what having no rule does.
                    if (current is not null && value.Length > 0) current.Value.Rules.Add((field == "allow", value));
                    break;
                default:
                    lastWasAgent = false;
                    break;
            }
        }
        return new RobotsRules(groups);
    }

    /// <summary>
    /// Whether <paramref name="pathAndQuery"/> may be fetched by a client that goes by any of
    /// <paramref name="agentNames"/>. The groups naming it apply; when none does, the <c>*</c> groups.
    /// </summary>
    public bool IsAllowed(string pathAndQuery, params string[] agentNames)
    {
        var names = agentNames.Select(n => n.ToLowerInvariant()).ToArray();
        var named = _groups.Where(g => g.Agents.Any(names.Contains)).ToList();
        var applicable = named.Count > 0 ? named : _groups.Where(g => g.Agents.Contains("*")).ToList();

        var bestLength = -1;
        var allowed = true;
        try
        {
            foreach (var (allow, pattern) in applicable.SelectMany(g => g.Rules))
            {
                if (!Matches(pattern, pathAndQuery)) continue;
                if (pattern.Length > bestLength || (pattern.Length == bestLength && allow))
                {
                    bestLength = pattern.Length;
                    allowed = allow;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A rule too tangled to evaluate in a second: when in doubt, the path is refused.
            return false;
        }
        return allowed;
    }

    private static bool Matches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$');
        if (anchored) pattern = pattern[..^1];
        var regex = "^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + (anchored ? "$" : "");
        return Regex.IsMatch(path, regex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
