using Shouldly;
using SmartAgri.Application.Databases;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Tests.Databases;

/// <summary>
/// Who may see a database and who may read its consented records (M4 #144): reading needs the
/// designation <b>and</b> the account permission; owning a database implies neither; a data
/// manager sees the database but never manages it.
/// </summary>
public class DatabaseRecordAccessTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Manager = Guid.CreateVersion7();

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void Reading_needs_both_the_designation_and_the_permission(bool designated, bool permitted, bool expected) =>
        DatabaseRecordAccess.CanRead(designated, permitted).ShouldBe(expected);

    [Fact]
    public void ReadableBy_needs_a_designation_on_that_database_and_the_permission()
    {
        var database = CreateDatabase();
        var other = CreateDatabase();
        var designations = new[] { DatabaseDataManager.Create(database, Manager, Owner, DateTimeOffset.UtcNow) };

        IsReadable(database, Manager, true, designations).ShouldBeTrue();
        IsReadable(database, Manager, false, designations).ShouldBeFalse("designated but without the permission");
        IsReadable(other, Manager, true, designations).ShouldBeFalse("designated on a different database");
        IsReadable(database, Owner, true, designations).ShouldBeFalse("owning it is not being designated");
        IsReadable(database, Manager, true, []).ShouldBeFalse("designation removed");
    }

    [Fact]
    public void ListedFor_is_the_owner_or_a_reader_but_ManageableBy_stays_owner_only()
    {
        var database = CreateDatabase();
        var designations = new[] { DatabaseDataManager.Create(database, Manager, Owner, DateTimeOffset.UtcNow) };
        bool Listed(Guid viewer, bool permitted, IEnumerable<DatabaseDataManager> rows) =>
            DatabaseAccess.ListedFor(viewer, permitted, rows.AsQueryable()).Compile()(database);

        Listed(Owner, false, []).ShouldBeTrue("the owner always sees their own database");
        Listed(Manager, true, designations).ShouldBeTrue();
        Listed(Manager, false, designations).ShouldBeFalse("designated but the account permission is gone");
        Listed(Manager, true, []).ShouldBeFalse("permitted but not designated");

        DatabaseAccess.ManageableBy(Manager).Compile()(database).ShouldBeFalse("a data manager never manages");
        DatabaseAccess.ManageableBy(Owner).Compile()(database).ShouldBeTrue();
        DatabaseAccess.CanManage(database, Manager).ShouldBeFalse();
    }

    private static Database CreateDatabase() =>
        Database.Create(Organization, Owner, "客戶資料", "收集客戶資料", DatabaseTemplateId.CustomerProfile, DateTimeOffset.UtcNow);

    private static bool IsReadable(
        Database database, Guid accountId, bool permitted, IEnumerable<DatabaseDataManager> designations) =>
        DatabaseRecordAccess.ReadableBy(accountId, permitted, designations.AsQueryable()).Compile()(database);
}
