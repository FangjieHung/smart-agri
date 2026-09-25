namespace SmartAgri.Api.Tests.Infrastructure;

/// <summary>
/// Trait values for <c>[Trait("Category", ...)]</c>. Any test that needs Docker
/// (directly, or transitively through <see cref="PostgresFixture"/> or
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// wired to a real database) must carry <see cref="Docker"/> so it can be excluded on
/// machines without a running Docker daemon. See apps/api/README.md for the exact
/// filter command.
/// </summary>
public static class TestCategories
{
    public const string Docker = "Docker";
}
