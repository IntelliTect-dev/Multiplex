using System.Text.Json;
using System.Text.Json.Serialization;

namespace TripIt.Auth;

/// <summary>
/// Cross-platform session store that mirrors the layout used by <c>dotnet user-secrets</c>.
/// Reads/writes <c>~/.microsoft/usersecrets/{userSecretsId}/secrets.json</c>
/// (or the Windows equivalent under %APPDATA%).
///
/// Keys: <c>TripIt:Cookie</c>, <c>TripIt:CsrfToken</c>.
/// </summary>
public sealed class UserSecretsSessionStore : ISessionStore
{
    public const string DefaultUserSecretsId = "TripItEx";
    private const string CookieKey = "TripIt:Cookie";
    private const string CsrfKey = "TripIt:CsrfToken";

    private readonly string _path;

    public UserSecretsSessionStore(string userSecretsId = DefaultUserSecretsId)
    {
        _path = ResolveSecretsPath(userSecretsId);
    }

    public string Description => $"user-secrets:{_path}";

    public bool TryLoad(out SessionCredentials credentials)
    {
        credentials = new SessionCredentials("", "");
        if (!File.Exists(_path)) return false;
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
            if (dict is null) return false;
            if (!dict.TryGetValue(CookieKey, out var cookie) || string.IsNullOrEmpty(cookie)) return false;
            dict.TryGetValue(CsrfKey, out var csrf);
            credentials = new SessionCredentials(cookie, csrf ?? "");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Save(SessionCredentials credentials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Dictionary<string, string> dict;
        if (File.Exists(_path))
        {
            try
            {
                dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
                       ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch
            {
                dict = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }
        else
        {
            dict = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        dict[CookieKey] = credentials.Cookie;
        dict[CsrfKey] = credentials.CsrfToken;
        var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        File.WriteAllText(_path, json);
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static string ResolveSecretsPath(string userSecretsId)
    {
        // Mirrors the path used by Microsoft.Extensions.Configuration.UserSecrets.PathHelper.
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        var root = !string.IsNullOrEmpty(appData)
            ? Path.Combine(appData, "Microsoft", "UserSecrets")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".microsoft", "usersecrets");
        return Path.Combine(root, userSecretsId, "secrets.json");
    }
}
