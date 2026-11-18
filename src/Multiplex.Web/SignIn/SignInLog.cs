namespace Multiplex.Web.SignIn;

/// <summary>How the sign-in area names secrets-by-possession in its logs.</summary>
internal static class SignInLog
{
    /// <summary>
    /// A pending authorization id is a capability (holding it is enough to cancel that
    /// authorization), so logs carry only the first 8 hex digits of its SHA-256: enough to
    /// match one request's log lines with another's, never enough to replay.
    /// </summary>
    public static string PendingId(string pendingId) => SignInPaths.HashName(pendingId)[..8];
}
