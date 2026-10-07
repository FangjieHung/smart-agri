namespace SmartAgri.Application.Observability;

/// <summary>
/// A log-safe description of an exception for the paths that handle what a person typed (LINE
/// questions, website visitors' questions; pre-launch plan §2.3, issue #307): the exception types,
/// an HTTP status when there is one, and the first stack frame — never a message.
/// </summary>
/// <remarks>
/// Why not log the exception itself: a message and the inner exceptions it carries are written by
/// code this repository does not control. A model provider's error body can quote the request that
/// failed (the question, or the passages of the knowledge base around it), and a logger prints every
/// inner exception's message with it. The privacy policy template (<c>deploy/privacy-policy-template.md</c>)
/// promises that the system's log holds none of it. A stack frame is a method signature and a source
/// position — types and names, no values.
/// </remarks>
public static class ExceptionSummary
{
    /// <summary>
    /// <c>ChatGenerationException &gt; HttpRequestException (HTTP 400) at Namespace.Type.Method() in File.cs:line 12</c>:
    /// the types from the outermost exception to the innermost, the HTTP status of the first one that has
    /// one, and the first stack frame of the innermost. <c>none</c> for <see langword="null"/>.
    /// </summary>
    public static string Of(Exception? exception)
    {
        if (exception is null)
        {
            return "none";
        }

        var types = new List<string>();
        int? status = null;
        var innermost = exception;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            types.Add(current.GetType().Name);
            if (status is null && current is HttpRequestException { StatusCode: { } code })
            {
                status = (int)code;
            }

            innermost = current;
        }

        var text = string.Join(" > ", types);
        if (status is { } httpStatus)
        {
            text += $" (HTTP {httpStatus})";
        }

        var frame = innermost.StackTrace?.Split('\n', 2)[0].Trim();
        return string.IsNullOrEmpty(frame) ? text : $"{text} {frame}";
    }
}
