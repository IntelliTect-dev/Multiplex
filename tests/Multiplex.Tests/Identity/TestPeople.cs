using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Multiplex.Web.Identity;
using Multiplex.Web.Sites;

namespace Multiplex.Tests.Identity;

/// <summary>
/// People on the test allowlist, and a way to sign one in without Microsoft: they are admitted through the
/// real <see cref="PersonAdmission"/> (so the binding exists and the cookie check passes), and the app's own
/// cookie format makes their sign-in cookie. The Microsoft round trip itself is covered by
/// <see cref="MicrosoftSignInTests"/>.
/// </summary>
public static class TestPeople
{
    public const string Owner = "owner@example.test";
    public const string Friend = "friend@example.test";

    /// <summary>The allowlist test hosts use (<c>Multiplex:Identity:AllowedEmails</c>).</summary>
    public const string AllowList = Owner + ";" + Friend;

    /// <summary>Microsoft's account key for a test person: a fixed tenant and an object id per address.</summary>
    public static string SubjectOf(string email) =>
        $"72f988bf-86f1-41af-91ab-2d7cd011db47/{new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(email))):D}";

    /// <summary>The Multiplex user id <paramref name="email"/> gets when admitted (it is derived from the account key).</summary>
    public static string OwnerOf(string email = Owner) => PersonAdmission.OwnerFor(MicrosoftAccounts.Provider, SubjectOf(email));

    /// <summary>Admits <paramref name="email"/> and returns the person and a <c>Cookie</c> header value carrying their sign-in.</summary>
    public static async Task<(MultiplexPerson Person, string Cookie)> SignInAsync(IServiceProvider services, string email = Owner)
    {
        var admission = await services.GetRequiredService<PersonAdmission>()
            .AdmitAsync(new ExternalIdentity(MicrosoftAccounts.Provider, SubjectOf(email), email, EmailVerified: true), CancellationToken.None);
        var person = admission is Admission.Admitted a ? a.Person : throw new InvalidOperationException($"{email} was not admitted: {admission}");
        return (person, CookieFor(services, person));
    }

    /// <summary>The sign-in cookie the app would issue for <paramref name="person"/>, as a <c>Cookie</c> header value.</summary>
    public static string CookieFor(IServiceProvider services, MultiplexPerson person)
    {
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(MultiplexIdentity.CookieScheme);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(SiteClaims.Owner, person.Owner), new Claim("email", person.Email)],
            MultiplexIdentity.CookieScheme, "email", null));
        var ticket = new AuthenticationTicket(principal,
            new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now + MultiplexIdentity.SessionLifetime, IsPersistent = true },
            MultiplexIdentity.CookieScheme);
        return $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}";
    }

    /// <summary>A client that sends <paramref name="cookie"/> on every request.</summary>
    public static HttpClient WithCookie(this HttpClient client, string cookie)
    {
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }
}
