namespace SmartAgri.Api.Setup;

/// <summary>
/// Values given to <c>setup</c> on the command line; any left <see langword="null"/> is
/// asked for interactively (or is an error when stdin is not a terminal).
/// </summary>
public sealed record SetupArguments(
    string? OrganizationName,
    string? OrganizationCode,
    string? AdminLogin,
    string? AdminDisplayName,
    bool HelpRequested = false)
{
    public const string OrganizationNameFlag = "--organization-name";
    public const string OrganizationCodeFlag = "--organization-code";
    public const string AdminLoginFlag = "--admin-login";
    public const string AdminDisplayNameFlag = "--admin-display-name";

    public const string Usage = """
        用法：setup [--organization-name <名稱>] [--organization-code <代碼>]
                    [--admin-login <帳號名稱>] [--admin-display-name <顯示名稱>]

        建立第一個組織與它的管理者（角色 smb-admin，擁有全部權限），並印出只顯示一次的
        一次性密碼；管理者首次登入後必須設定新密碼。資料庫已有任何組織時拒絕執行。
        須先執行 migrate（容器的 entrypoint 會自動執行）。

        沒有用參數提供的值會在終端機詢問；標準輸入不是終端機時，組織名稱、組織代碼與
        管理者帳號名稱都必須以參數提供（顯示名稱省略時等於帳號名稱）。
          --organization-name   組織名稱（1-200 字）
          --organization-code   組織代碼：a-z、0-9、-，1-32 字，登入時使用，建立後不可修改
          --admin-login         管理者帳號名稱：英文字母、數字與 - . _ @ +，1-64 字
          --admin-display-name  管理者顯示名稱（1-200 字）
        結束代碼：0 完成；1 拒絕執行或失敗；2 參數或輸入錯誤。
        """;

    private static readonly string[] Flags = [OrganizationNameFlag, OrganizationCodeFlag, AdminLoginFlag, AdminDisplayNameFlag];

    /// <summary>
    /// Parses <c>--flag value</c> and <c>--flag=value</c>. Unknown flags, positional
    /// arguments, repeated flags and flags without a value are errors, so a typo never
    /// silently falls through to a prompt or a default.
    /// </summary>
    public static SetupArgumentsParseResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg is "--help" or "-h")
            {
                return SetupArgumentsParseResult.Success(new SetupArguments(null, null, null, null, HelpRequested: true));
            }

            string flag;
            string? value;
            var equals = arg.IndexOf('=', StringComparison.Ordinal);
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            {
                flag = arg[..equals];
                value = arg[(equals + 1)..];
            }
            else
            {
                flag = arg;
                value = null;
            }

            if (!Flags.Contains(flag, StringComparer.Ordinal))
            {
                return SetupArgumentsParseResult.Failure($"不認得的參數：{flag}");
            }

            if (value is null)
            {
                if (index + 1 >= args.Count || Flags.Contains(args[index + 1], StringComparer.Ordinal))
                {
                    return SetupArgumentsParseResult.Failure($"{flag} 需要一個值。");
                }

                value = args[++index];
            }

            if (!values.TryAdd(flag, value))
            {
                return SetupArgumentsParseResult.Failure($"{flag} 只能指定一次。");
            }
        }

        return SetupArgumentsParseResult.Success(new SetupArguments(
            values.GetValueOrDefault(OrganizationNameFlag),
            values.GetValueOrDefault(OrganizationCodeFlag),
            values.GetValueOrDefault(AdminLoginFlag),
            values.GetValueOrDefault(AdminDisplayNameFlag)));
    }
}

/// <summary>Either parsed <see cref="SetupArguments"/> or an error message.</summary>
public sealed record SetupArgumentsParseResult(SetupArguments? Arguments, string? Error)
{
    public static SetupArgumentsParseResult Success(SetupArguments arguments) => new(arguments, null);

    public static SetupArgumentsParseResult Failure(string error) => new(null, error);
}
