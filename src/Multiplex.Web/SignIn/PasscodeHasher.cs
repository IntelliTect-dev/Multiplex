using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Multiplex.Web.SignIn;

/// <summary>
/// The owner passcode's stored form: <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;</c>.
/// Used by the gate and by the installer (through <c>dotnet run -- hash-passcode</c>).
/// </summary>
public static class PasscodeHasher
{
    public const string Scheme = "pbkdf2-sha256";

    /// <summary>OWASP's 2023 floor for PBKDF2-HMAC-SHA256; weaker stored hashes are refused.</summary>
    public const int MinimumIterations = 210_000;

    public const int DefaultIterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string passcode) => Hash(passcode, DefaultIterations);

    public static string Hash(string passcode, int iterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(passcode);
        if (iterations < MinimumIterations)
            throw new ArgumentOutOfRangeException(nameof(iterations), $"At least {MinimumIterations} iterations.");

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passcode), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Scheme, iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>True when <paramref name="stored"/> is a well-formed, strong-enough hash.</summary>
    public static bool IsValidFormat(string? stored) => TryParse(stored, out _, out _, out _);

    /// <summary>Constant-time check of <paramref name="passcode"/> against <paramref name="stored"/>.</summary>
    public static bool Verify(string? passcode, string? stored)
    {
        if (string.IsNullOrEmpty(passcode) || !TryParse(stored, out var iterations, out var salt, out var expected))
            return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passcode), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool TryParse(string? stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = hash = [];
        var parts = stored?.Trim().Split('$');
        if (parts is not [Scheme, var iter, var saltText, var hashText]) return false;
        if (!int.TryParse(iter, NumberStyles.None, CultureInfo.InvariantCulture, out iterations)
            || iterations < MinimumIterations || iterations > 10_000_000)
            return false;
        try
        {
            salt = Convert.FromBase64String(saltText);
            hash = Convert.FromBase64String(hashText);
        }
        catch (FormatException)
        {
            return false;
        }
        return salt.Length >= 8 && hash.Length >= 16;
    }
}

/// <summary>
/// <c>dotnet run --project src/Multiplex.Web -- hash-passcode</c>: reads a passcode from stdin
/// (masked when typed at a console) and prints its hash for <c>Multiplex:Gate:PasscodeHash</c>.
/// Never starts the web server and never prints the passcode.
/// </summary>
public static class PasscodeCli
{
    public const string Command = "hash-passcode";
    public const int MinimumLength = 8;

    public static int Run(TextReader input, TextWriter output, TextWriter error, bool interactive = false)
    {
        if (interactive) error.Write("Passcode: ");
        var passcode = interactive ? ReadMasked() : input.ReadLine();
        if (interactive) error.WriteLine();

        passcode = passcode?.TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(passcode) || passcode.Length < MinimumLength)
        {
            error.WriteLine($"A passcode of at least {MinimumLength} characters is required on stdin.");
            return 2;
        }

        output.WriteLine(PasscodeHasher.Hash(passcode));
        return 0;
    }

    private static string ReadMasked()
    {
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) return text.ToString();
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0) text.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
    }
}
