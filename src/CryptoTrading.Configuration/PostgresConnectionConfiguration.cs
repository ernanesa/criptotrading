using System.Data.Common;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace CryptoTrading.Configuration;

/// <summary>
/// Resolves PostgreSQL credentials at startup without putting file-based
/// passwords in environment variables or argv. Never log the returned DSN.
/// </summary>
public static class PostgresConnectionConfiguration
{
    private const int MaximumPasswordCharacters = 4096;

    public static string Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var direct = configuration["POSTGRES_CONNECTION_STRING"]?.Trim();
        var path = configuration["POSTGRES_PASSWORD_FILE"]?.Trim();
        if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(direct))
        {
            throw new InvalidOperationException("PostgreSQL credential sources are ambiguous; choose file configuration or legacy host DSN.");
        }

        if (!string.IsNullOrWhiteSpace(direct))
        {
            // Compatibility for host-only tooling; base Compose will use files.
            return direct.Trim();
        }

        if (string.IsNullOrEmpty(path))
        {
            if (!string.IsNullOrWhiteSpace(configuration["POSTGRES_PASSWORD"]) ||
                !string.IsNullOrWhiteSpace(configuration["POSTGRES_HOST"]) ||
                !string.IsNullOrWhiteSpace(configuration["POSTGRES_DB"]) ||
                !string.IsNullOrWhiteSpace(configuration["POSTGRES_USER"]) ||
                !string.IsNullOrWhiteSpace(configuration["POSTGRES_PORT"]))
            {
                throw new InvalidOperationException("PostgreSQL file configuration is incomplete.");
            }

            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(configuration["POSTGRES_PASSWORD"]))
        {
            throw new InvalidOperationException("Direct PostgreSQL password configuration is not allowed in file mode.");
        }

        var host = configuration["POSTGRES_HOST"];
        var database = configuration["POSTGRES_DB"];
        var username = configuration["POSTGRES_USER"];
        if (string.IsNullOrWhiteSpace(host) || host != host.Trim() ||
            Uri.CheckHostName(host) == UriHostNameType.Unknown ||
            !IsLocalHost(host) ||
            !IsIdentifier(database) || !IsIdentifier(username) ||
            !int.TryParse(configuration["POSTGRES_PORT"], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            throw new InvalidOperationException("PostgreSQL file configuration contains invalid connection metadata.");
        }

        var password = ReadProtectedPassword(path);
        var builder = new DbConnectionStringBuilder
        {
            ["Host"] = host,
            ["Port"] = port,
            ["Database"] = database!,
            ["Username"] = username!,
            ["Password"] = password,
        };
        return builder.ConnectionString;
    }

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 63 &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool IsLocalHost(string host) =>
        string.Equals(host, "postgres", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    private static string ReadProtectedPassword(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            // No silent ACL bypass on Windows or unverified Unix variants.
            throw UnavailablePassword();
        }

        try
        {
            if (!Path.IsPathFullyQualified(path) ||
                !string.Equals(path, Path.GetFullPath(path), StringComparison.Ordinal) ||
                !File.Exists(path) ||
                HasSymlinkComponent(path))
            {
                throw UnavailablePassword();
            }

            var file = new FileInfo(path);
            if (file.Length == 0 || file.Length > MaximumPasswordCharacters || file.Directory is null)
            {
                throw UnavailablePassword();
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                const UnixFileMode allowedFileBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                const UnixFileMode groupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                    UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                var mode = File.GetUnixFileMode(path);
                if ((mode & UnixFileMode.UserRead) == 0 || (mode & ~allowedFileBits) != 0 ||
                    (File.GetUnixFileMode(file.Directory.FullName) & groupOrOther) != 0)
                {
                    throw UnavailablePassword();
                }
            }

            // Bounded read even if another process grows the file after stat.
            using var reader = File.OpenText(path);
            Span<char> buffer = stackalloc char[MaximumPasswordCharacters + 1];
            var count = reader.ReadBlock(buffer);
            if (count > MaximumPasswordCharacters)
            {
                throw UnavailablePassword();
            }

            var password = new string(buffer[..count]).Trim();
            if (password.Length == 0 || password.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)))
            {
                throw UnavailablePassword();
            }

            return password;
        }
        catch (IOException)
        {
            // No inner exception: filesystem errors may contain sensitive paths.
            throw UnavailablePassword();
        }
        catch (UnauthorizedAccessException)
        {
            throw UnavailablePassword();
        }
    }

    private static bool HasSymlinkComponent(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }

        for (var directory = new FileInfo(path).Directory; directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static InvalidOperationException UnavailablePassword() =>
        new("PostgreSQL password file is unavailable, unsafe or invalid.");
}
