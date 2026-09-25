using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Setup;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Setup;

/// <summary>The one-time password's strength. Uses the Api host's real Identity options
/// (no database is opened: password validators never touch the store).</summary>
public class OneTimePasswordGeneratorTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const int Samples = 2000;

    private readonly WebApplicationFactory<Program> _factory;

    public OneTimePasswordGeneratorTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Every_generated_password_passes_the_hosts_Identity_password_rules()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var account = Account.Create(new Organization(Guid.NewGuid(), "org", "org"), "admin", "admin", AccountRole.SmbAdmin);

        userManager.Options.Password.RequiredLength.ShouldBeGreaterThanOrEqualTo(12);
        userManager.PasswordValidators.ShouldNotBeEmpty();

        for (var sample = 0; sample < Samples; sample++)
        {
            var password = OneTimePasswordGenerator.Generate();
            foreach (var validator in userManager.PasswordValidators)
            {
                var result = await validator.ValidateAsync(userManager, account, password);
                result.Succeeded.ShouldBeTrue($"{password}: {string.Join("; ", result.Errors.Select(error => error.Code))}");
            }
        }
    }

    [Fact]
    public void Passwords_have_the_full_length_every_character_class_and_only_alphabet_characters()
    {
        for (var sample = 0; sample < Samples; sample++)
        {
            var password = OneTimePasswordGenerator.Generate();

            password.Length.ShouldBe(OneTimePasswordGenerator.Length);
            password.ShouldContain(character => OneTimePasswordGenerator.Upper.Contains(character));
            password.ShouldContain(character => OneTimePasswordGenerator.Lower.Contains(character));
            password.ShouldContain(character => OneTimePasswordGenerator.Digits.Contains(character));
            password.ShouldContain(character => OneTimePasswordGenerator.Symbols.Contains(character));
            password.ShouldAllBe(character => OneTimePasswordGenerator.Alphabet.Contains(character));
        }
    }

    [Fact]
    public void Alphabet_has_64_distinct_symbols_without_look_alikes_or_shell_specials()
    {
        OneTimePasswordGenerator.Alphabet.Distinct().Count().ShouldBe(64);
        OneTimePasswordGenerator.Alphabet.ShouldNotContain(character => "0Oo1lI'\"`$\\ ".Contains(character));
    }

    [Fact]
    public void Passwords_do_not_repeat_and_the_required_classes_move_around()
    {
        var passwords = Enumerable.Range(0, Samples).Select(_ => OneTimePasswordGenerator.Generate()).ToList();

        passwords.Distinct(StringComparer.Ordinal).Count().ShouldBe(Samples);

        // The four guaranteed characters are shuffled in: e.g. the first character is not
        // always upper case.
        passwords.Select(password => password[0]).ShouldContain(character => !OneTimePasswordGenerator.Upper.Contains(character));
        passwords.Select(password => password[3]).ShouldContain(character => !OneTimePasswordGenerator.Symbols.Contains(character));
    }
}
