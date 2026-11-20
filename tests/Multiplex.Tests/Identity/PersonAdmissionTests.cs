using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Multiplex.Web.Auth.Store;
using Multiplex.Web.Identity;

namespace Multiplex.Tests.Identity;

/// <summary>Who gets in (#80, D2): only an allowlisted, verified email, bound on first sign-in to the account that used it.</summary>
public sealed class PersonAdmissionTests
{
    private readonly InMemoryAuthStore _store = new(TimeProvider.System);
    private readonly Dictionary<string, string?> _settings = new() { [AllowedPeople.Key] = TestPeople.AllowList };

    private PersonAdmission Admission() =>
        new(_store, new AllowedPeople(new ConfigurationBuilder().AddInMemoryCollection(_settings).Build()),
            TimeProvider.System, NullLogger<PersonAdmission>.Instance);

    private static ExternalIdentity Microsoft(string? email, bool verified = true, string? subject = null) =>
        new(MicrosoftAccounts.Provider, subject ?? TestPeople.SubjectOf(email ?? "nobody"), email, verified);

    [Fact]
    public async Task A_verified_allowlisted_email_is_admitted_and_bound_on_first_sign_in()
    {
        var admission = await Admission().AdmitAsync(Microsoft(TestPeople.Owner), CancellationToken.None);

        var person = Assert.IsType<Admission.Admitted>(admission).Person;
        Assert.Equal(TestPeople.Owner, person.Email);
        Assert.Equal(TestPeople.OwnerOf(TestPeople.Owner), person.Owner);
        var binding = await _store.ReadAsync(s => s.People[TestPeople.Owner], CancellationToken.None);
        Assert.Equal(TestPeople.SubjectOf(TestPeople.Owner), binding.Subject);
        Assert.True(await Admission().IsAllowedAsync(person.Owner, CancellationToken.None));
    }

    [Fact]
    public async Task An_unverified_email_is_refused_even_on_the_list()
    {
        var admission = await Admission().AdmitAsync(Microsoft(TestPeople.Owner, verified: false), CancellationToken.None);

        Assert.IsType<Admission.Refused>(admission);
        Assert.Empty(await _store.ReadAsync(s => s.People.Keys.ToList(), CancellationToken.None));
    }

    [Fact]
    public async Task An_email_not_on_the_list_is_refused()
    {
        var admission = await Admission().AdmitAsync(Microsoft("stranger@example.test"), CancellationToken.None);

        Assert.IsType<Admission.Refused>(admission);
    }

    [Fact]
    public async Task An_empty_list_lets_no_one_in()
    {
        _settings[AllowedPeople.Key] = "";

        Assert.IsType<Admission.Refused>(await Admission().AdmitAsync(Microsoft(TestPeople.Owner), CancellationToken.None));
    }

    [Fact]
    public async Task Once_bound_another_account_with_the_same_verified_address_is_refused()
    {
        await Admission().AdmitAsync(Microsoft(TestPeople.Owner), CancellationToken.None);

        var other = await Admission().AdmitAsync(
            Microsoft(TestPeople.Owner, subject: "72f988bf-86f1-41af-91ab-2d7cd011db47/00000000-0000-0000-0000-00000000beef"),
            CancellationToken.None);

        Assert.IsType<Admission.Refused>(other);
    }

    [Fact]
    public async Task Once_bound_the_account_gets_in_even_when_its_email_claim_changes()
    {
        var first = (Admission.Admitted)await Admission().AdmitAsync(Microsoft(TestPeople.Owner), CancellationToken.None);

        var later = await Admission().AdmitAsync(
            new ExternalIdentity(MicrosoftAccounts.Provider, TestPeople.SubjectOf(TestPeople.Owner), "renamed@example.test", false),
            CancellationToken.None);

        Assert.Equal(first.Person, Assert.IsType<Admission.Admitted>(later).Person);
    }

    [Fact]
    public async Task Taking_an_address_off_the_list_shuts_its_person_out_and_adding_it_back_binds_afresh()
    {
        var first = (Admission.Admitted)await Admission().AdmitAsync(Microsoft(TestPeople.Owner), CancellationToken.None);

        _settings[AllowedPeople.Key] = TestPeople.Friend;
        Assert.False(await Admission().IsAllowedAsync(first.Person.Owner, CancellationToken.None));
        Assert.IsType<Admission.Refused>(await Admission().AdmitAsync(Microsoft(TestPeople.Owner), CancellationToken.None));

        _settings[AllowedPeople.Key] = TestPeople.AllowList;
        var newAccount = "72f988bf-86f1-41af-91ab-2d7cd011db47/00000000-0000-0000-0000-00000000beef";
        var again = await Admission().AdmitAsync(Microsoft(TestPeople.Owner, subject: newAccount), CancellationToken.None);
        Assert.Equal(PersonAdmission.OwnerFor(MicrosoftAccounts.Provider, newAccount), Assert.IsType<Admission.Admitted>(again).Person.Owner);
    }

    [Theory]
    [InlineData("Owner@Example.TEST; friend@example.test")]
    [InlineData("owner@example.test,friend@example.test")]
    [InlineData(" owner@example.test  friend@example.test ")]
    public void The_list_is_one_string_separated_by_semicolons_commas_or_spaces_ignoring_case(string value)
    {
        var list = new AllowedPeople(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [AllowedPeople.Key] = value }).Build()).Current();

        Assert.Equal(["friend@example.test", "owner@example.test"], list.Order());
    }

    [Fact]
    public void The_list_can_also_be_an_array()
    {
        var list = new AllowedPeople(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AllowedPeople.Key + ":0"] = "owner@example.test",
            [AllowedPeople.Key + ":1"] = "Friend@example.test",
        }).Build()).Current();

        Assert.Equal(["friend@example.test", "owner@example.test"], list.Order());
    }

    [Fact]
    public void A_Microsoft_account_is_keyed_by_tenant_and_object_id_and_trusted_only_with_xms_edov()
    {
        ClaimsPrincipal Token(object? edov) => new(new ClaimsIdentity(
            new JsonWebToken(new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Claims = edov is null
                    ? new Dictionary<string, object> { ["tid"] = FakeMicrosoft.Tenant, ["oid"] = "6f1a7f5a-0000-4000-8000-000000000001", ["email"] = "a@b.test" }
                    : new Dictionary<string, object> { ["tid"] = FakeMicrosoft.Tenant, ["oid"] = "6f1a7f5a-0000-4000-8000-000000000001", ["email"] = "a@b.test", ["xms_edov"] = edov },
            })).Claims));

        var verified = MicrosoftAccounts.From(Token(true))!;
        Assert.Equal($"{FakeMicrosoft.Tenant}/6f1a7f5a-0000-4000-8000-000000000001", verified.Subject);
        Assert.True(verified.EmailVerified);
        Assert.True(MicrosoftAccounts.From(Token("1"))!.EmailVerified);
        Assert.False(MicrosoftAccounts.From(Token(false))!.EmailVerified);
        Assert.False(MicrosoftAccounts.From(Token(null))!.EmailVerified);
        Assert.Null(MicrosoftAccounts.From(new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "a@b.test")]))));
    }

    [Theory]
    [InlineData(FakeMicrosoft.Tenant, "https://login.microsoftonline.com/" + FakeMicrosoft.Tenant + "/v2.0", true)]
    [InlineData(FakeMicrosoft.PersonalTenant, "https://login.microsoftonline.com/" + FakeMicrosoft.PersonalTenant + "/v2.0", true)]
    [InlineData(FakeMicrosoft.Tenant, "https://login.microsoftonline.com/" + FakeMicrosoft.PersonalTenant + "/v2.0", false)]
    [InlineData(FakeMicrosoft.Tenant, "https://login.microsoftonline.com/{tenantid}/v2.0", false)]
    [InlineData(FakeMicrosoft.Tenant, "https://evil.example/" + FakeMicrosoft.Tenant + "/v2.0", false)]
    [InlineData("not-a-guid", "https://login.microsoftonline.com/not-a-guid/v2.0", false)]
    public void The_issuer_must_be_exactly_the_tokens_own_tenant(string tenant, string issuer, bool valid)
    {
        var token = new JsonWebToken(new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Claims = new Dictionary<string, object> { ["tid"] = tenant },
        }));

        if (valid) Assert.Equal(issuer, MicrosoftAccounts.ValidateIssuer(issuer, token, new TokenValidationParameters()));
        else Assert.Throws<SecurityTokenInvalidIssuerException>(() => MicrosoftAccounts.ValidateIssuer(issuer, token, new TokenValidationParameters()));
    }
}
