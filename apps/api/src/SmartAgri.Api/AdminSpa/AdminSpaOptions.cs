using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SmartAgri.Api.AdminSpa;

/// <summary>
/// Configuration section <c>Admin</c> (pre-launch plan §3 E, #306): the admin SPA's build, when this Api
/// serves it from its own origin. Unset (the default outside the Docker image), the Api serves no admin
/// at all and behaves exactly as before; the admin is then deployed somewhere else.
/// </summary>
public sealed class AdminSpaOptions
{
    public const string SectionName = "Admin";

    /// <summary>
    /// The admin's build output (<c>dist/smart-agri-admin/browser</c> of
    /// <c>nx build admin --configuration=production-api</c>: <c>index.html</c>, hashed <c>main-*.js</c>,
    /// <c>chunk-*.js</c>, <c>styles-*.css</c>, <c>favicon.ico</c>). A relative path is resolved against the
    /// content root; the Docker image sets <c>wwwroot/admin</c>. When set, the folder and its
    /// <c>index.html</c> must exist, or the Api refuses to start.
    /// </summary>
    public string? RootPath { get; set; }

    /// <summary>Whether the Api serves the admin (<see cref="RootPath"/> is set).</summary>
    public bool IsServed => !string.IsNullOrWhiteSpace(RootPath);

    /// <summary><see cref="RootPath"/> as an absolute path, or <see langword="null"/> when unset.</summary>
    public string? ResolveRoot(IHostEnvironment environment)
    {
        if (!IsServed)
        {
            return null;
        }

        var trimmed = RootPath!.Trim();
        return Path.GetFullPath(Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(environment.ContentRootPath, trimmed));
    }

    /// <summary>Refuses to start with a <see cref="RootPath"/> that is set but is not a folder with an
    /// <c>index.html</c>: an explicit setting that serves nothing is a broken deployment, not "no admin".</summary>
    internal sealed class Validator : IValidateOptions<AdminSpaOptions>
    {
        private readonly IHostEnvironment _environment;

        public Validator(IHostEnvironment environment)
        {
            _environment = environment;
        }

        public ValidateOptionsResult Validate(string? name, AdminSpaOptions options)
        {
            if (options.ResolveRoot(_environment) is not { } root)
            {
                return ValidateOptionsResult.Success;
            }

            if (!Directory.Exists(root))
            {
                return ValidateOptionsResult.Fail(
                    $"Admin:RootPath '{options.RootPath}' (resolved to '{root}') is not a folder. Point it at the admin build "
                    + "(nx build admin --configuration=production-api), or leave it unset to serve no admin.");
            }

            if (!File.Exists(Path.Combine(root, AdminSpaHosting.IndexFileName)))
            {
                return ValidateOptionsResult.Fail(
                    $"Admin:RootPath '{options.RootPath}' (resolved to '{root}') has no {AdminSpaHosting.IndexFileName}. Point it at the admin build "
                    + "(nx build admin --configuration=production-api), or leave it unset to serve no admin.");
            }

            return ValidateOptionsResult.Success;
        }
    }
}
