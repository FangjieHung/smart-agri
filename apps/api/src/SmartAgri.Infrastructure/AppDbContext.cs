using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Jobs;
using SmartAgri.Infrastructure.Knowledge;
using SmartAgri.Infrastructure.Persistence;
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

    /// <summary>Same set as Identity's <see cref="IdentityUserContext{TUser,TKey,TUserClaim,TUserLogin,TUserToken}.Users"/>.</summary>
    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<AccountPermissionGrant> AccountPermissions => Set<AccountPermissionGrant>();

    public DbSet<KnowledgeBase> KnowledgeBases => Set<KnowledgeBase>();

    public DbSet<KnowledgeBaseShare> KnowledgeBaseShares => Set<KnowledgeBaseShare>();

    public DbSet<KnowledgeActivity> KnowledgeActivities => Set<KnowledgeActivity>();

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
        // context (DI, design-time factory, tests) gets the write guard.
        optionsBuilder.AddInterceptors(OrganizationWriteGuard);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Reserved for M2's pgvector-backed embedding tables (M1 slice 2).
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
            organization.ToTable("Organizations");
            organization.HasKey(o => o.Id);
            organization.Property(o => o.Id).ValueGeneratedNever();
            organization.Property(o => o.Name).HasMaxLength(Organization.NameMaxLength).IsRequired();
            organization.Property(o => o.Code).HasMaxLength(Organization.CodeMaxLength).IsRequired();
            organization.HasIndex(o => o.Code).IsUnique();
            organization.Property(o => o.TeamPermissionsSavedAt);
        });

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
