using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CryptoTrading.Configuration;

public sealed class TradingOptions
{
    public const string SectionName = "Trading";

    public string Environment { get; set; } = string.Empty;
    public BinanceOptions Binance { get; set; } = new();
}

public sealed class BinanceOptions
{
    public Uri? RestBaseUrl { get; set; }
    public Uri? WebSocketBaseUrl { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public bool WithdrawalsEnabled { get; set; }
    public string AllowedSymbols { get; set; } = string.Empty;

    public IReadOnlyList<string> ParsedAllowedSymbols =>
        AllowedSymbols.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

public sealed class TradingOptionsValidator : IValidateOptions<TradingOptions>
{
    public ValidateOptionsResult Validate(string? name, TradingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (!string.Equals(options.Environment, "TESTNET", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("TRADING_ENVIRONMENT must be TESTNET.");
        }

        if (options.Binance.RestBaseUrl is not { IsAbsoluteUri: true } restUri ||
            !string.Equals(restUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(restUri.Host, "testnet.binance.vision", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("BINANCE_REST_BASE_URL must be the HTTPS Binance Spot Testnet endpoint.");
        }

        if (options.Binance.WebSocketBaseUrl is not { IsAbsoluteUri: true } wsUri ||
            !string.Equals(wsUri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(wsUri.Host, "testnet.binance.vision", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("BINANCE_WS_BASE_URL must be the WSS Binance Spot Testnet endpoint.");
        }

        if (options.Binance.WithdrawalsEnabled)
        {
            errors.Add("Withdrawals must remain disabled in the local testnet runtime.");
        }

        if (options.Binance.ParsedAllowedSymbols.Count == 0)
        {
            errors.Add("BINANCE_ALLOWED_SYMBOLS must contain at least one symbol.");
        }

        if (options.Binance.ApiKey.Length == 0 || options.Binance.ApiSecret.Length == 0)
        {
            errors.Add("Binance API credentials are required for the configured runtime.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}

public static class TradingConfigurationExtensions
{
    public static IConfigurationBuilder AddLocalDotEnv(this IConfigurationBuilder builder, string? path = null)
    {
        var filePath = path ?? FindDotEnv();
        if (filePath is null || !File.Exists(filePath))
        {
            return builder;
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(filePath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#', StringComparison.Ordinal))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            // Existing process variables (for example CI-injected values) win over .env.
            if (!Environment.GetEnvironmentVariables().Contains(key))
            {
                values[key] = value;
            }
        }

        return builder.AddInMemoryCollection(values);
    }

    public static IServiceCollection AddTradingConfiguration(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TradingOptions>()
            .Configure(options =>
            {
                options.Environment = configuration["TRADING_ENVIRONMENT"] ?? string.Empty;
                options.Binance = new BinanceOptions
                {
                    RestBaseUrl = Uri.TryCreate(configuration["BINANCE_REST_BASE_URL"], UriKind.Absolute, out var restUri) ? restUri : null,
                    WebSocketBaseUrl = Uri.TryCreate(configuration["BINANCE_WS_BASE_URL"], UriKind.Absolute, out var wsUri) ? wsUri : null,
                    ApiKey = ResolveSecret(configuration, "BINANCE_API_KEY", "BINANCE_API_KEY_FILE"),
                    ApiSecret = ResolveSecret(configuration, "BINANCE_API_SECRET", "BINANCE_API_SECRET_FILE"),
                    WithdrawalsEnabled = bool.TryParse(configuration["BINANCE_WITHDRAWALS_ENABLED"], out var withdrawals) && withdrawals,
                    AllowedSymbols = configuration["BINANCE_ALLOWED_SYMBOLS"] ?? string.Empty
                };
            });
        services.AddSingleton<IValidateOptions<TradingOptions>, TradingOptionsValidator>();
        return services;
    }

    private static string? FindDotEnv()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ResolveSecret(IConfiguration configuration, string valueKey, string fileKey)
    {
        var directValue = configuration[valueKey];
        if (!string.IsNullOrWhiteSpace(directValue))
        {
            return directValue.Trim();
        }

        var path = configuration[fileKey];
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return string.Empty;
        }

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || !HasSafeSecretPermissions(file))
            {
                return string.Empty;
            }

            var value = File.ReadAllText(file.FullName).Trim();
            return value.Length == 0 || value.Any(char.IsWhiteSpace) ? string.Empty : value;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static bool HasSafeSecretPermissions(FileInfo file)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return true;
        }

        var fileMode = File.GetUnixFileMode(file.FullName);
        const UnixFileMode groupOrOtherBits = UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                                               UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                                               UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((fileMode & groupOrOtherBits) != 0)
        {
            return false;
        }

        var directory = file.Directory;
        if (directory is null)
        {
            return false;
        }

        var directoryMode = File.GetUnixFileMode(directory.FullName);
        return (directoryMode & groupOrOtherBits) == 0;
    }
}
