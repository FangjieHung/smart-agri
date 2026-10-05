using Shouldly;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Databases;
using SmartAgri.Domain.Answers;

namespace SmartAgri.Application.Tests.Answers;

public class AnswerKindsTests
{
    [Theory]
    [InlineData(ChatDatabaseQueryStatus.Answered, AnswerDatabaseQueryResult.Answered)]
    [InlineData(ChatDatabaseQueryStatus.NoData, AnswerDatabaseQueryResult.InsufficientRecords)]
    [InlineData(ChatDatabaseQueryStatus.InsufficientData, AnswerDatabaseQueryResult.InsufficientRecords)]
    [InlineData(ChatDatabaseQueryStatus.NotAvailable, AnswerDatabaseQueryResult.NotPermitted)]
    [InlineData(ChatDatabaseQueryStatus.Rejected, AnswerDatabaseQueryResult.Failed)]
    [InlineData(ChatDatabaseQueryStatus.Failed, AnswerDatabaseQueryResult.Failed)]
    public void A_database_query_status_maps_to_one_of_the_four_outcome_results(
        ChatDatabaseQueryStatus status, AnswerDatabaseQueryResult expected) =>
        AnswerKinds.ToDatabaseQueryResult(status).ShouldBe(expected);

    [Fact]
    public void Every_database_query_status_has_a_result_and_every_result_is_reachable() =>
        Enum.GetValues<ChatDatabaseQueryStatus>().Select(AnswerKinds.ToDatabaseQueryResult).Distinct().Order()
            .ShouldBe(Enum.GetValues<AnswerDatabaseQueryResult>().Order());

    [Fact]
    public void The_wire_names_are_the_contracts() =>
        Enum.GetValues<AnswerDatabaseQueryResult>().Select(result => System.Text.Json.JsonSerializer.Serialize(result))
            .ShouldBe(["\"answered\"", "\"not-permitted\"", "\"insufficient-records\"", "\"failed\""]);
}
