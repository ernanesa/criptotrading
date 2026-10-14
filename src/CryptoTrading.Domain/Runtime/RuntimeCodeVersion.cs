using System.Reflection;

namespace CryptoTrading.Domain.Runtime;

/// <summary>
/// Provides point-in-time code and git commit traceability embedded during build.
/// </summary>
public static class RuntimeCodeVersion
{
    private static readonly Lazy<string> CurrentLazy = new(() =>
    {
        var env = Environment.GetEnvironmentVariable("GIT_COMMIT_HASH") ??
                  Environment.GetEnvironmentVariable("APP_VERSION");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        var informationalVersion = typeof(RuntimeCodeVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "unknown";
        }

        var plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plusIndex >= 0 ? informationalVersion[(plusIndex + 1)..] : informationalVersion;
    });

    public static string Current => CurrentLazy.Value;
}
