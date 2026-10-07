using System.Net;
using Shouldly;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Observability;

namespace SmartAgri.Application.Tests.Observability;

/// <summary>The log-safe description of an exception (pre-launch plan §2.3, issue #307): types, an HTTP
/// status and a stack frame — never a message, at any depth of the chain.</summary>
public class ExceptionSummaryTests
{
    private const string UserText = "我的訂單編號 A12345 要退貨";

    [Fact]
    public void No_exception_is_none()
    {
        ExceptionSummary.Of(null).ShouldBe("none");
    }

    [Fact]
    public void The_chain_is_listed_by_type_with_the_http_status_and_no_message_at_any_depth()
    {
        var provider = new HttpRequestException($"Invalid request: {UserText}", inner: null, HttpStatusCode.BadRequest);
        var wrapped = new ChatGenerationException(providerNotConfigured: false, provider);

        var summary = ExceptionSummary.Of(wrapped);

        summary.ShouldStartWith("ChatGenerationException > HttpRequestException (HTTP 400)");
        summary.ShouldNotContain(UserText);
        summary.ShouldNotContain("Invalid request");
        summary.ShouldNotContain(wrapped.Message);
    }

    [Fact]
    public void The_first_stack_frame_of_the_innermost_exception_is_kept_so_a_bug_can_be_found()
    {
        var thrown = Capture(() => throw new InvalidOperationException(UserText));

        var summary = ExceptionSummary.Of(new InvalidOperationException(UserText, thrown));

        summary.ShouldStartWith("InvalidOperationException > InvalidOperationException ");
        summary.ShouldContain(nameof(ExceptionSummaryTests));
        summary.ShouldNotContain(UserText);
    }

    [Fact]
    public void An_exception_that_was_never_thrown_has_no_frame()
    {
        ExceptionSummary.Of(new ArgumentException(UserText)).ShouldBe("ArgumentException");
    }

    private static Exception Capture(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The action did not throw.");
    }
}
