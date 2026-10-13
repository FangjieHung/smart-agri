using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartAgri.Api.Tests.Infrastructure;

/// <summary>
/// An in-process SMTP server on a loopback port (#355): enough of RFC 5321 for .NET's
/// <c>SmtpClient</c> — <c>EHLO</c>, <c>AUTH LOGIN</c>, <c>MAIL</c>, <c>RCPT</c>, <c>DATA</c>, <c>QUIT</c>, no
/// TLS — so notification Emails are tested end to end without any real mailbox. Records every accepted
/// message; <see cref="FailNext"/> makes the next <c>MAIL FROM</c>s answer <c>451</c> (a temporary
/// failure, as a busy or unreachable provider would).
/// </summary>
public sealed partial class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentQueue<SmtpMessage> _messages = new();
    private readonly Task _accepting;
    private int _failNext;
    private int _rejected;

    public FakeSmtpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The messages accepted so far, in order.</summary>
    public IReadOnlyList<SmtpMessage> Messages => [.. _messages];

    /// <summary>How many <c>MAIL FROM</c>s were refused by <see cref="FailNext"/>.</summary>
    public int Rejected => Volatile.Read(ref _rejected);

    /// <summary>The messages to <paramref name="address"/>.</summary>
    public IReadOnlyList<SmtpMessage> To(string address) =>
        [.. _messages.Where(message => message.Recipients.Contains(address, StringComparer.OrdinalIgnoreCase))];

    /// <summary>The next <paramref name="count"/> transactions are refused with <c>451</c>.</summary>
    public void FailNext(int count) => Interlocked.Exchange(ref _failNext, count);

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 fake.smtp ESMTP ready");
                string? from = null;
                string? username = null;
                var recipients = new List<string>();
                while (await reader.ReadLineAsync(_stopping.Token) is { } line)
                {
                    var verb = line.Split(' ', 2)[0].ToUpperInvariant();
                    switch (verb)
                    {
                        case "EHLO":
                            await writer.WriteAsync("250-fake.smtp\r\n250-AUTH LOGIN\r\n250 OK\r\n");
                            break;
                        case "HELO":
                            await writer.WriteLineAsync("250 fake.smtp");
                            break;
                        case "AUTH":
                            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            var user = parts.Length > 2 ? parts[2] : null;
                            if (user is null)
                            {
                                await writer.WriteLineAsync("334 VXNlcm5hbWU6");
                                user = await reader.ReadLineAsync(_stopping.Token);
                            }

                            await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
                            await reader.ReadLineAsync(_stopping.Token);
                            username = Encoding.UTF8.GetString(Convert.FromBase64String(user ?? string.Empty));
                            await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                            break;
                        case "MAIL":
                            if (Interlocked.Decrement(ref _failNext) >= 0)
                            {
                                Interlocked.Increment(ref _rejected);
                                await writer.WriteLineAsync("451 4.3.0 Temporary failure, try again later");
                                break;
                            }

                            Interlocked.Exchange(ref _failNext, 0);
                            from = Address(line);
                            recipients.Clear();
                            await writer.WriteLineAsync("250 2.1.0 OK");
                            break;
                        case "RCPT":
                            recipients.Add(Address(line));
                            await writer.WriteLineAsync("250 2.1.5 OK");
                            break;
                        case "DATA":
                            await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                            var data = new StringBuilder();
                            while (await reader.ReadLineAsync(_stopping.Token) is { } dataLine && dataLine != ".")
                            {
                                data.Append(dataLine.StartsWith("..", StringComparison.Ordinal) ? dataLine[1..] : dataLine).Append("\r\n");
                            }

                            _messages.Enqueue(new SmtpMessage(from ?? string.Empty, [.. recipients], username, data.ToString()));
                            await writer.WriteLineAsync("250 2.0.0 Queued");
                            break;
                        case "RSET":
                        case "NOOP":
                            await writer.WriteLineAsync("250 OK");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 Bye");
                            return;
                        default:
                            await writer.WriteLineAsync("502 Command not implemented");
                            break;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    private static string Address(string line) =>
        AddressPattern().Match(line) is { Success: true } match ? match.Groups[1].Value : string.Empty;

    [GeneratedRegex("<([^>]*)>")]
    private static partial Regex AddressPattern();
}

/// <summary>One message <see cref="FakeSmtpServer"/> accepted: the envelope and the raw <c>DATA</c>.</summary>
public sealed partial record SmtpMessage(string From, IReadOnlyList<string> Recipients, string? Username, string Data)
{
    /// <summary>The <c>Subject</c> header, its RFC 2047 encoded words decoded.</summary>
    public string Subject => DecodeWords(Header("Subject") ?? string.Empty);

    /// <summary>The <c>From</c> header, decoded.</summary>
    public string FromHeader => DecodeWords(Header("From") ?? string.Empty);

    /// <summary>The body, decoded from base64 when the message says so.</summary>
    public string Body
    {
        get
        {
            var separator = Data.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var raw = separator < 0 ? string.Empty : Data[(separator + 4)..];
            return string.Equals(Header("Content-Transfer-Encoding"), "base64", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(raw.Split("\r\n").Select(line => line.Trim()))))
                : raw;
        }
    }

    /// <summary>A header's value, folded lines joined.</summary>
    public string? Header(string name)
    {
        var separator = Data.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headers = (separator < 0 ? Data : Data[..separator]).Replace("\r\n ", " ", StringComparison.Ordinal).Replace("\r\n\t", " ", StringComparison.Ordinal);
        return headers.Split("\r\n")
            .Where(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            .Select(line => line[(name.Length + 1)..].Trim())
            .FirstOrDefault();
    }

    /// <summary>Decodes RFC 2047 <c>B</c> words (what .NET writes for UTF-8 headers); white space between
    /// two encoded words is not part of the text (§6.2).</summary>
    private static string DecodeWords(string value)
    {
        var joined = AdjacentWords().Replace(value, "?==?");
        var text = new StringBuilder();
        var bytes = new List<byte>();
        Encoding? encoding = null;
        var position = 0;
        foreach (Match match in EncodedWord().Matches(joined))
        {
            if (match.Index > position)
            {
                Flush();
                text.Append(joined, position, match.Index - position);
            }

            encoding = Encoding.GetEncoding(match.Groups[1].Value);
            bytes.AddRange(Convert.FromBase64String(match.Groups[2].Value));
            position = match.Index + match.Length;
        }

        Flush();
        text.Append(joined, position, joined.Length - position);
        return text.ToString();

        // A multi-byte character may be split across two words: decode adjacent words' bytes together.
        void Flush()
        {
            if (encoding is not null && bytes.Count > 0)
            {
                text.Append(encoding.GetString([.. bytes]));
            }

            bytes.Clear();
        }
    }

    [GeneratedRegex(@"=\?([^?]+)\?[Bb]\?([^?]*)\?=")]
    private static partial Regex EncodedWord();

    [GeneratedRegex(@"\?=\s+=\?")]
    private static partial Regex AdjacentWords();
}
