using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SmartAgri.Api.AdminSpa;

/// <summary>
/// Configuration section <c>Shop</c> (online-ordering design D1, #398): the customer ordering app's build
/// (<c>apps/shop</c>), when this Api serves it under <c>/order/</c> (<see cref="AdminSpaHosting"/>). Unset (the default
/// outside the Docker image), <c>/order/**</c> is the Api's <c>401</c> like any other path without an endpoint.
/// </summary>
public sealed class ShopSpaOptions
{
    public const string SectionName = "Shop";

    /// <summary>
    /// The ordering app's build output (<c>dist/smart-agri-shop/browser</c> of <c>nx build shop</c>: <c>index.html</c> with
    /// base href <c>/order/</c>, hashed <c>main-*.js</c>, <c>chunk-*.js</c>, <c>styles-*.css</c>). A relative path is resolved
    /// against the content root; the Docker image sets <c>wwwroot/shop</c>. When set, the folder and its <c>index.html</c>
    /// must exist, or the Api refuses to start.
    /// </summary>
    public string? RootPath { get; set; }

    /// <summary>Whether the Api serves the ordering app (<see cref="RootPath"/> is set).</summary>
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

    /// <summary>Refuses to start with a <see cref="RootPath"/> that is set but is not a folder with an <c>index.html</c>.</summary>
    internal sealed class Validator : IValidateOptions<ShopSpaOptions>
    {
        private readonly IHostEnvironment _environment;

        public Validator(IHostEnvironment environment)
        {
            _environment = environment;
        }

        public ValidateOptionsResult Validate(string? name, ShopSpaOptions options)
        {
            if (options.ResolveRoot(_environment) is not { } root)
            {
                return ValidateOptionsResult.Success;
            }

            if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, AdminSpaHosting.IndexFileName)))
            {
                return ValidateOptionsResult.Fail(
                    $"Shop:RootPath '{options.RootPath}' (resolved to '{root}') is not a folder with {AdminSpaHosting.IndexFileName}. Point it at "
                    + "the ordering app's build (nx build shop), or leave it unset to serve no ordering app.");
            }

            return ValidateOptionsResult.Success;
        }
    }
}
