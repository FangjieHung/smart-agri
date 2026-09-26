using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Knowledge.Evaluation;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Seeding;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Seeding;

/// <summary>
/// With <see cref="ConfigurationKey"/> set to <c>true</c>, puts the retrieval evaluation's demo
/// documents (<see cref="RetrievalEvalSet.DefaultDirectory"/>, i.e. <c>apps/api/eval/retrieval/</c>:
/// 商品使用指南, 退換貨政策 with versions 1 and 2, 配送常見問題) into 安心商行, owned by its
/// <c>admin</c>, through the normal upload → process → approve pipeline
/// (<see cref="KnowledgeSetImporter"/>), so local development and API-mode demos have real
/// knowledge to search (M2 plan Slice 16; ticket #50). Runs after <see cref="DevelopmentSeeder"/>
/// in <c>migrate</c>, in Development only (<see cref="DevelopmentSeedingServiceCollectionExtensions"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Off unless asked for.</b> Without the setting it returns before touching the database, so
/// nothing changes for anyone who does not opt in. It needs 安心商行 and its <c>admin</c>, which
/// <see cref="DevelopmentSeeder"/> creates when <c>SEED_DEMO_PASSWORD</c> is set; without them
/// it logs a warning and does nothing.
/// </para>
/// <para>
/// <b>Idempotent</b> like <see cref="DevelopmentSeeder"/>: it only fills in what is missing — a
/// knowledge base by owner and name, a document by name, a version by content — and never
/// touches what is there, including a document the developer deleted versions of, disabled or
/// replaced by hand. <c>migrate</c> has no job worker, so it runs the queue once itself and
/// approves what is processed; anything that is not (e.g. the embedding model was unreachable
/// and the queue will retry) stays pending review, and running <c>migrate</c> again once the
/// worker has processed it approves it.
/// </para>
/// </remarks>
public sealed class DemoKnowledgeSeeder
{
    /// <summary>Configuration key (also an environment variable name); <c>true</c> turns it on.</summary>
    public const string ConfigurationKey = "SEED_DEMO_KNOWLEDGE";

    /// <summary>The account that owns the demo knowledge: 安心商行管理者 (<see cref="DevelopmentSeedData"/>).</summary>
    public const string AdminLoginName = "admin";

    private readonly IConfiguration _configuration;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;
    private readonly IServiceProvider _services;
    private readonly ILogger<DemoKnowledgeSeeder> _logger;

    public DemoKnowledgeSeeder(
        IConfiguration configuration,
        DbContextOptions<AppDbContext> dbContextOptions,
        IServiceProvider services,
        ILogger<DemoKnowledgeSeeder> logger)
    {
        _configuration = configuration;
        _dbContextOptions = dbContextOptions;
        _services = services;
        _logger = logger;
    }

    /// <summary>Whether <paramref name="configuration"/> asks for the demo knowledge.</summary>
    public static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return string.Equals(configuration[ConfigurationKey]?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <exception cref="RetrievalEvalSetException">The demo set shipped with the build is broken.</exception>
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled(_configuration))
        {
            return;
        }

        var set = RetrievalEvalSet.Load(RetrievalEvalSet.DefaultDirectory);

        Guid organizationId;
        await using (var dbContext = new AppDbContext(_dbContextOptions, FixedOrganizationContext.None))
        {
            var organization = await dbContext.Organizations.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Code == DevelopmentSeedData.AnxinOrganizationCode, cancellationToken);
            if (organization is null)
            {
                _logger.LogWarning(
                    "{ConfigurationKey} is set but organization {OrganizationCode} does not exist; skipping the demo knowledge. " +
                    "Set SEED_DEMO_PASSWORD too, so the demo organizations and accounts are seeded first.",
                    ConfigurationKey,
                    DevelopmentSeedData.AnxinOrganizationCode);
                return;
            }

            organizationId = organization.Id;
        }

        Guid ownerId;
        await using (var dbContext = new AppDbContext(_dbContextOptions, new FixedOrganizationContext(organizationId)))
        {
            var normalized = Account.NormalizeLoginName(AdminLoginName);
            var owner = await dbContext.Accounts.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.NormalizedLoginName == normalized, cancellationToken);
            if (owner is null)
            {
                _logger.LogWarning(
                    "{ConfigurationKey} is set but {OrganizationCode} has no account {LoginName}; skipping the demo knowledge.",
                    ConfigurationKey,
                    DevelopmentSeedData.AnxinOrganizationCode,
                    AdminLoginName);
                return;
            }

            ownerId = owner.Id;
        }

        var importer = ActivatorUtilities.CreateInstance<KnowledgeSetImporter>(_services);
        var import = await importer.ImportAsync(organizationId, ownerId, set.KnowledgeBases, TimeSpan.Zero, cancellationToken);
        _logger.LogInformation(
            "Demo knowledge in {OrganizationCode}: {KnowledgeBases} knowledge base(s), {Uploaded} version(s) uploaded, {Approved} approved.",
            DevelopmentSeedData.AnxinOrganizationCode,
            import.KnowledgeBaseIds.Count,
            import.Uploaded,
            import.Approved);
        foreach (var version in import.NotApproved)
        {
            _logger.LogWarning(
                "Demo knowledge {DocumentName} version {SetVersion} is not approved: {Status} {Issue}",
                version.DocumentName,
                version.SetVersion,
                version.Status,
                version.Issue ?? version.Skipped);
        }
    }
}
