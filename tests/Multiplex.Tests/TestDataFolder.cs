using System.Runtime.CompilerServices;

namespace Multiplex.Tests;

/// <summary>
/// Points every test host at a throwaway data folder before any host starts, so no test ever
/// writes encryption keys, sessions or registrations into the owner's real
/// <c>%LOCALAPPDATA%\Multiplex\data</c>. Hosts that set <c>Multiplex:DataPath</c> themselves
/// still win (an in-memory setting overrides an environment variable).
/// </summary>
internal static class TestDataFolder
{
    public static string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "multiplex-tests", "default-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("Multiplex__DataPath")))
            Environment.SetEnvironmentVariable("Multiplex__DataPath", Path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
    }
}
