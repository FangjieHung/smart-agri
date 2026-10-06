using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Answers;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Chat;
using SmartAgri.Domain.Databases;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;
using SmartAgri.Domain.Reports;
using Pgvector.EntityFrameworkCore;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Ai;
using SmartAgri.Infrastructure.Answers;
using SmartAgri.Infrastructure.Assistants;
using SmartAgri.Infrastructure.Chat;
using SmartAgri.Infrastructure.Databases;
using SmartAgri.Infrastructure.Jobs;
using SmartAgri.Infrastructure.Knowledge;
using SmartAgri.Infrastructure.Organizations;
using SmartAgri.Infrastructure.Persistence;
using SmartAgri.Infrastructure.Reports;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Infrastructure;

/// <summary>
/// The application's single EF Core context (backend-stack ADR: PostgreSQL is the single
/// store), including ASP.NET Core Identity's user tables.
/// </summary>
/// <remarks>
/// <para>
/// Organization isolation (deployment-and-tenancy ADR) is enforced here for every entity
/// implementing <see cref="IOrganizationScoped"/>, without each entity opting in:
/// </para>
/// <list type="bullet">
/// <item><b>Reads</b> — the named query filter <see cref="OrganizationFilter"/> limits
/// rows to the current organization; with no current organization it matches nothing.
/// Being named, later filters (e.g. soft delete) can be added and ignored independently.</item>
/// <item><b>Writes</b> — <see cref="OrganizationSaveChangesInterceptor"/> fills in and
/// checks <c>OrganizationId</c> before anything reaches the database.</item>
/// <item><b>Updates/deletes by key</b> — <c>OrganizationId</c> is a concurrency token, so
/// every generated <c>UPDATE</c>/<c>DELETE</c> also matches on it; a row of another
/// organization attached under a forged <c>OrganizationId</c> affects zero rows.</item>
/// </list>
/// <para>
/// Identity's role tables are deliberately not mapped: an account's role is the
/// <see cref="Account.Role"/> column, not an Identity role.
/// </para>
/// </remarks>
public class AppDbContext : IdentityUserContext<Account, Guid, AccountClaim, AccountLogin, AccountToken>
{
    /// <summary>Name of the query filter applied to every organization-scoped entity.</summary>
    public const string OrganizationFilter = "Organization";

    private static readonly OrganizationSaveChangesInterceptor OrganizationWriteGuard = new();
    private static readonly TimestampPrecisionInterceptor TimestampPrecision = new();

    private static readonly PropertyInfo HasCurrentOrganizationProperty =
        typeof(AppDbContext).GetProperty(nameof(HasCurrentOrganization), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly PropertyInfo CurrentOrganizationIdProperty =
        typeof(AppDbContext).GetProperty(nameof(CurrentOrganizationId), BindingFlags.Instance | BindingFlags.NonPublic)!;

    public AppDbContext(DbContextOptions<AppDbContext> options, IOrganizationContext organizationContext)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(organizationContext);
        OrganizationContext = organizationContext;
    }

    /// <summary>The organization this context reads and writes for.</summary>
    public IOrganizationContext OrganizationContext { get; }

    public DbSet<Organization> Organizations => Set<Organization>();

    /// <summary>Organization-level settings changes (M6 plan §3 E): only ever added.</summary>
    public DbSet<OrganizationActivity> OrganizationActivities => Set<OrganizationActivity>();

    /// <summary>Same set as Identity's <see cref="IdentityUserContext{TUser,TKey,TUserClaim,TUserLogin,TUserToken}.Users"/>.</summary>
    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<AccountPermissionGrant> AccountPermissions => Set<AccountPermissionGrant>();

    public DbSet<KnowledgeBase> KnowledgeBases => Set<KnowledgeBase>();

    public DbSet<KnowledgeBaseShare> KnowledgeBaseShares => Set<KnowledgeBaseShare>();

    public DbSet<KnowledgeActivity> KnowledgeActivities => Set<KnowledgeActivity>();

    public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();

    public DbSet<KnowledgeDocumentVersion> KnowledgeDocumentVersions => Set<KnowledgeDocumentVersion>();

    /// <summary>Original files (<c>bytea</c>). Only downloads and background processing
    /// query this set; nothing navigates to it, so no other query loads file bytes.</summary>
    public DbSet<KnowledgeFileContent> KnowledgeFileContents => Set<KnowledgeFileContent>();

    /// <summary>What processing read from each version: pages, sections, worksheets.</summary>
    public DbSet<KnowledgeExtractedUnit> KnowledgeExtractedUnits => Set<KnowledgeExtractedUnit>();

    /// <summary>Retrievable passages, cut from readable units.</summary>
    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();

    public DbSet<Assistant> Assistants => Set<Assistant>();

    /// <summary>A knowledge base connected to an assistant (M3 plan §4).</summary>
    public DbSet<AssistantKnowledgeBase> AssistantKnowledgeBases => Set<AssistantKnowledgeBase>();

    /// <summary>Databases connected to assistants (M4 #148).</summary>
    public DbSet<AssistantDatabase> AssistantDatabases => Set<AssistantDatabase>();

    /// <summary>In-progress wizard drafts, one or more per account (M3 plan §3, §4; #72).</summary>
    public DbSet<AssistantDraft> AssistantDrafts => Set<AssistantDraft>();

    /// <summary>An assistant's saved test set (M3.5 plan §4, issue #123).</summary>
    public DbSet<AssistantTestCase> AssistantTestCases => Set<AssistantTestCase>();

    /// <summary>Executions of an assistant's test set, 「全部重跑」 (M3.5 plan §4, issue #124).</summary>
    public DbSet<AssistantTestRun> AssistantTestRuns => Set<AssistantTestRun>();

    /// <summary>A test run's per-question outcomes (M3.5 plan §4, issue #124).</summary>
    public DbSet<AssistantTestResult> AssistantTestResults => Set<AssistantTestResult>();

    /// <summary>處理事項 about an assistant's answers (M3.5 plan §4, issue #126).</summary>
    public DbSet<AssistantIssue> AssistantIssues => Set<AssistantIssue>();

    /// <summary>Each <see cref="AssistantIssue"/>'s handling history.</summary>
    public DbSet<AssistantIssueEvent> AssistantIssueEvents => Set<AssistantIssueEvent>();

    /// <summary>An account an assistant is shared with — "平台內分享" (M3 plan §4/§5 Slice 3).</summary>
    public DbSet<AssistantShare> AssistantShares => Set<AssistantShare>();

    /// <summary>An assistant's website embedding channel, at most one each (M5a plan §4; #194).</summary>
    public DbSet<AssistantWebsiteChannel> AssistantWebsiteChannels => Set<AssistantWebsiteChannel>();

    /// <summary>The host names allowed to embed an assistant's website channel (M5a plan §4; #194).</summary>
    public DbSet<AssistantWebsiteDomain> AssistantWebsiteDomains => Set<AssistantWebsiteDomain>();

    /// <summary>An assistant's LINE channel, at most one each; its credentials are
    /// <c>ProtectedSecret</c>s (M5b plan §4; #229).</summary>
    public DbSet<AssistantLineChannel> AssistantLineChannels => Set<AssistantLineChannel>();

    /// <summary>One account's private conversation with one assistant (M3 plan §4; #76).</summary>
    public DbSet<ChatThread> ChatThreads => Set<ChatThread>();

    /// <summary>A thread's turns, saved only for a validated assistant reply (#76/#77).</summary>
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    /// <summary>A <c>company-data</c> message's cited passages, as a snapshot (#76).</summary>
    public DbSet<ChatMessageCitation> ChatMessageCitations => Set<ChatMessageCitation>();

    /// <summary>Closed in-conversation forms (M4 #171): event, assistant, form and time only.</summary>
    public DbSet<ChatFormDismissal> ChatFormDismissals => Set<ChatFormDismissal>();

    /// <summary>數據庫: structured, consented collections created from a template (M4, #142).
    /// The fully qualified name avoids <see cref="DbContext.Database"/>.</summary>
    public DbSet<SmartAgri.Domain.Databases.Database> Databases => Set<SmartAgri.Domain.Databases.Database>();

    /// <summary>Each database's immutable form versions; the highest number is the current form.</summary>
    public DbSet<DatabaseFormVersion> DatabaseFormVersions => Set<DatabaseFormVersion>();

    /// <summary>Accounts designated as a database's data managers (M4, #144).</summary>
    public DbSet<DatabaseDataManager> DatabaseDataManagers => Set<DatabaseDataManager>();

    /// <summary>Append-only history of data-manager designations and removals (M4, #144).</summary>
    public DbSet<DatabaseDataManagerChange> DatabaseDataManagerChanges => Set<DatabaseDataManagerChange>();

    /// <summary>The content-free trail of consented submissions (M4, #145): who, when, which
    /// form version, the consent terms and the receipt number.</summary>
    public DbSet<DatabaseSubmission> DatabaseSubmissions => Set<DatabaseSubmission>();

    /// <summary>The content of each submission, one row per field (deleted on withdrawal, #146).</summary>
    public DbSet<DatabaseSubmissionEntry> DatabaseSubmissionEntries => Set<DatabaseSubmissionEntry>();

    /// <summary>Assistants' periodic report settings, one per assistant (M4, #150).</summary>
    public DbSet<ReportSchedule> ReportSchedules => Set<ReportSchedule>();

    /// <summary>Snapshots of periodic reports: statistics apart from the AI summary (M4, #150).</summary>
    public DbSet<DatabaseReport> DatabaseReports => Set<DatabaseReport>();

    /// <summary>The model-call audit log (M2 plan, Slice 7): no content, ever.</summary>
    public DbSet<ModelInvocation> ModelInvocations => Set<ModelInvocation>();

    /// <summary>The answer-pipeline result log (M3.5 plan §3, §4, Slice 6): no content, ever.</summary>
    public DbSet<AnswerOutcome> AnswerOutcomes => Set<AnswerOutcome>();

    /// <summary>The background job queue (M2 plan, Slice 4). Enqueue by adding a
    /// <see cref="BackgroundJob"/> in the same save as the rows it is about.</summary>
    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();

    /// <summary>
    /// Pinned so the model (and therefore the migrations) never depends on whatever
    /// <c>IdentityOptions.Stores.SchemaVersion</c> the host happens to configure.
    /// Version 1 has no passkey table; moving to a later version must add an
    /// organization-scoped passkey entity (the tenancy model test will insist).
    /// </summary>
    protected override Version SchemaVersion => IdentitySchemaVersions.Version1;

    // Read by the query filter on every query (EF Core parameterizes members of the
    // context), so each context instance filters by its own organization.
    private bool HasCurrentOrganization => OrganizationContext.OrganizationId.HasValue;

    // Guid.Empty when there is no organization; no row can ever carry it because the save
    // interceptor refuses such writes, so this alone would also match nothing.
    private Guid CurrentOrganizationId => OrganizationContext.OrganizationId ?? Guid.Empty;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        // Registered here rather than by the host so that every way of constructing the
        // context (DI, design-time factory, tests) gets the write guard and the timestamp
        // truncation.
        optionsBuilder.AddInterceptors(OrganizationWriteGuard, TimestampPrecision);

        // pgvector's type mapping and distance functions (KnowledgeChunks.Embedding, M2 Slice 7),
        // for the same reason. The host adds it where it configures Npgsql; this covers every
        // other construction (keeping its connection settings), once per options instance.
        if (optionsBuilder.Options.FindExtension<VectorDbContextOptionsExtension>() is null)
        {
            optionsBuilder.UseNpgsql(npgsql => npgsql.UseVector());
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // pgvector: KnowledgeChunks.Embedding (created in M1 slice 2, used from M2 Slice 7).
        modelBuilder.HasPostgresExtension("vector");

        // OpenIddict's four tables (applications, authorizations, scopes, tokens). They
        // describe OAuth clients and grants keyed by subject, not organization data, so
        // they are deliberately not IOrganizationScoped (whitelisted by name in
        // OrganizationModelTests). Mapped here rather than on DbContextOptions so every
        // way of constructing the context — including the design-time factory that
        // generates migrations — sees the same model.
        modelBuilder.UseOpenIddict();

        modelBuilder.Entity<Organization>(organization =>
        {
            organization.ToTable("Organizations", table =>
                table.HasCheckConstraint("CK_Organizations_MonthlyTokenLimit", "\"MonthlyTokenLimit\" >= 0"));
            organization.HasKey(o => o.Id);
            organization.Property(o => o.Id).ValueGeneratedNever();
            organization.Property(o => o.Name).HasMaxLength(Organization.NameMaxLength).IsRequired();
            organization.Property(o => o.Code).HasMaxLength(Organization.CodeMaxLength).IsRequired();
            organization.HasIndex(o => o.Code).IsUnique();
            organization.Property(o => o.TeamPermissionsSavedAt);
            organization.Property(o => o.MonthlyTokenLimit);
            organization.Property(o => o.ChatModelId).HasMaxLength(Organization.ChatModelIdMaxLength);

            // A concurrency token, so two settings PUTs that both read the same revision cannot
            // both write: the second UPDATE matches no row and the API answers 409.
            organization.Property(o => o.SettingsRevision).IsConcurrencyToken();
        });

        // Organization-level activity log (M6 plan §3 E).
        modelBuilder.ApplyConfiguration(new OrganizationActivityConfiguration());

        modelBuilder.Entity<Account>(account =>
        {
            account.Property(a => a.LoginName).HasMaxLength(Account.LoginNameMaxLength).IsRequired();
            account.Property(a => a.NormalizedLoginName).HasMaxLength(Account.LoginNameMaxLength).IsRequired();
            account.Property(a => a.DisplayName).HasMaxLength(Account.DisplayNameMaxLength).IsRequired();
            account.Property(a => a.Role).HasConversion<WireNameConverter<AccountRole>>().HasMaxLength(32).IsRequired();

            // A login name only has to be unique within its organization.
            account.HasIndex(a => new { a.OrganizationId, a.NormalizedLoginName }).IsUnique();

            // Target of the composite foreign key from AccountPermissions, so the database
            // itself refuses a permission row whose organization differs from its account's.
            account.HasAlternateKey(a => new { a.Id, a.OrganizationId });
        });

        modelBuilder.Entity<AccountPermissionGrant>(grant =>
        {
            grant.ToTable("AccountPermissions");
            grant.HasKey(g => new { g.AccountId, g.Permission });
            grant.Property(g => g.Permission).HasConversion<WireNameConverter<AccountPermission>>().HasMaxLength(64);
            grant.HasOne<Account>()
                .WithMany()
                .HasForeignKey(g => new { g.AccountId, g.OrganizationId })
                .HasPrincipalKey(a => new { a.Id, a.OrganizationId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Knowledge bases (M2 plan §4). Domain entities are POCOs; their mapping lives in
        // Knowledge/, one IEntityTypeConfiguration per entity, applied explicitly so the
        // model is readable from here.
        modelBuilder.ApplyConfiguration(new KnowledgeBaseConfiguration());
        modelBuilder.ApplyConfiguration(new KnowledgeBaseShareConfiguration());
        modelBuilder.ApplyConfiguration(new KnowledgeActivityConfiguration());
        modelBuilder.ApplyConfiguration(new KnowledgeDocumentConfiguration());
        modelBuilder.ApplyConfiguration(new KnowledgeDocumentVersionConfiguration());
        modelBuilder.ApplyConfiguration(new KnowledgeFileContentConfiguration());
        modelBuilder.ApplyConfiguration(new KnowledgeExtractedUnitConfiguration());
        modelBuilder.ApplyConfiguration(new KnowledgeChunkConfiguration());

        // Assistants (M3 plan §4).
        modelBuilder.ApplyConfiguration(new AssistantConfiguration());
        modelBuilder.ApplyConfiguration(new AssistantKnowledgeBaseConfiguration());
        modelBuilder.ApplyConfiguration(new AssistantDatabaseConfiguration());
        modelBuilder.ApplyConfiguration(new AssistantDraftConfiguration());
        modelBuilder.ApplyConfiguration(new AssistantShareConfiguration());
        modelBuilder.ApplyConfiguration(new AssistantWebsiteChannelConfiguration());
        modelBuilder.ApplyConfiguration(new AssistantWebsiteDomainConfiguration());
        modelBuilder.ApplyConfiguration(new AssistantLineChannelConfiguration());
        modelBuilder.ApplyConfiguration(new AssistantTestCaseConfiguration());
        modelBuilder.ApplyConfiguration(new AssistantTestRunConfiguration());
        modelBuilder.ApplyConfiguration(new AssistantTestResultConfiguration());
        modelBuilder.ApplyConfiguration(new AssistantIssueConfiguration());
        modelBuilder.ApplyConfiguration(new AssistantIssueEventConfiguration());

        // Conversations (M3 plan §4; #76).
        modelBuilder.ApplyConfiguration(new ChatThreadConfiguration());
        modelBuilder.ApplyConfiguration(new ChatMessageConfiguration());
        modelBuilder.ApplyConfiguration(new ChatMessageCitationConfiguration());
        modelBuilder.ApplyConfiguration(new ChatFormDismissalConfiguration());

        // Databases and their form versions (M4, #142).
        modelBuilder.ApplyConfiguration(new DatabaseConfiguration());
        modelBuilder.ApplyConfiguration(new DatabaseFormVersionConfiguration());
        modelBuilder.ApplyConfiguration(new DatabaseDataManagerConfiguration());
        modelBuilder.ApplyConfiguration(new DatabaseDataManagerChangeConfiguration());
        modelBuilder.ApplyConfiguration(new DatabaseSubmissionConfiguration());
        modelBuilder.ApplyConfiguration(new DatabaseSubmissionEntryConfiguration());

        // Periodic reports (M4, #150).
        modelBuilder.ApplyConfiguration(new ReportScheduleConfiguration());
        modelBuilder.ApplyConfiguration(new DatabaseReportConfiguration());

        // Model-call audit log (M2 plan, Slice 7).
        modelBuilder.ApplyConfiguration(new ModelInvocationConfiguration());

        // Answer-pipeline result log (M3.5 plan §3, §4, Slice 6).
        modelBuilder.ApplyConfiguration(new AnswerOutcomeConfiguration());

        // Background jobs (M2 plan, Slice 4): organization scoped like everything else;
        // only Jobs/JobClaimer reads the table across organizations.
        modelBuilder.ApplyConfiguration(new BackgroundJobConfiguration());

        ApplyOrganizationScope(modelBuilder);
    }

    /// <summary>
    /// Applies the organization filter, the <c>Organizations</c> foreign key and the
    /// concurrency token to every <see cref="IOrganizationScoped"/> entity in the model.
    /// Runs last so entities mapped above (and by Identity) are all covered.
    /// </summary>
    private void ApplyOrganizationScope(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            if (!typeof(IOrganizationScoped).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            // Query filters can only be declared on the root of a hierarchy; derived types
            // inherit it.
            if (entityType.BaseType is not null)
            {
                continue;
            }

            var organizationIdProperty = entityType.ClrType.GetProperty(nameof(IOrganizationScoped.OrganizationId))
                ?? throw new InvalidOperationException(
                    $"{entityType.ClrType.Name} must expose a public {nameof(IOrganizationScoped.OrganizationId)} property " +
                    "(not an explicit interface implementation) so it can be filtered.");

            // row => HasCurrentOrganization && row.OrganizationId == CurrentOrganizationId
            var row = Expression.Parameter(entityType.ClrType, "row");
            var context = Expression.Constant(this);
            var filter = Expression.Lambda(
                Expression.AndAlso(
                    Expression.Property(context, HasCurrentOrganizationProperty),
                    Expression.Equal(
                        Expression.Property(row, organizationIdProperty),
                        Expression.Property(context, CurrentOrganizationIdProperty))),
                row);

            var entity = modelBuilder.Entity(entityType.ClrType);
            entity.HasQueryFilter(OrganizationFilter, filter);
            entity.Property(organizationIdProperty.Name).IsConcurrencyToken();
            entity.HasOne(typeof(Organization))
                .WithMany()
                .HasForeignKey(organizationIdProperty.Name)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
