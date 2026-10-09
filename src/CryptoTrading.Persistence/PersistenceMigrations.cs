using System.Reflection;
using System.Security.Cryptography;
using System.Text;

#pragma warning disable IDE0305 // Keep the explicit array materialization for a stable migration list API.

namespace CryptoTrading.Persistence;

/// <summary>Loads ordered, embedded SQL migrations without selecting a runner.</summary>
public static class PersistenceMigrations
{
    private const string ResourcePrefix = "CryptoTrading.Persistence.Migrations.";

    public static IReadOnlyList<MigrationScript> ReadAll()
    {
        var assembly = typeof(PersistenceMigrations).Assembly;
        var migrations = assembly.GetManifestResourceNames()
            .Where(static name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(name => Read(assembly, name))
            .OrderBy(static migration => migration.Version, StringComparer.Ordinal)
            .ThenBy(static migration => migration.Direction, StringComparer.Ordinal)
            .ToArray();
        ValidateSourceIntegrity(migrations, ReadIntegrityManifest(assembly));
        return migrations;
    }

    public static void ValidateInventory(IReadOnlyList<MigrationScript> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        var groups = migrations.GroupBy(static migration => migration.Version, StringComparer.Ordinal).ToArray();
        if (groups.Length == 0 || groups.Any(group => group.Count() != 2
            || !group.Any(static migration => migration.Direction == "up")
            || !group.Any(static migration => migration.Direction == "down")))
        {
            throw new InvalidOperationException("Migration inventory must contain exactly one up and one down script per version.");
        }

        for (var index = 0; index < groups.Length; index++)
        {
            if (groups[index].Key.Length != 3
                || !int.TryParse(groups[index].Key, out var version)
                || version != index + 1)
            {
                throw new InvalidOperationException("Migration inventory versions must be contiguous from 001.");
            }
        }
    }

    public static void ValidateAppliedVersions(IEnumerable<string> appliedVersions, IReadOnlyList<MigrationScript> migrations)
    {
        ArgumentNullException.ThrowIfNull(appliedVersions);
        ValidateInventory(migrations);
        var applied = appliedVersions.Order(StringComparer.Ordinal).ToArray();
        var expected = migrations.Where(static migration => migration.Direction == "up").Select(static migration => migration.Version).ToArray();
        if (applied.Length > expected.Length
            || !applied.SequenceEqual(expected.Take(applied.Length), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("PostgreSQL applied migration versions must be a contiguous known prefix.");
        }
    }

    private static MigrationScript Read(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Migration resource was not found: {resourceName}");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var rawSql = bytes.ToArray();
        var fileName = resourceName[ResourcePrefix.Length..];
        var parts = fileName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var version = parts[0].Split('_', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return new MigrationScript(version, parts[^2], fileName, Encoding.UTF8.GetString(rawSql), rawSql);
    }

    public static void ValidateSourceIntegrity(
        IReadOnlyList<MigrationScript> migrations,
        IReadOnlyDictionary<string, string> expectedHashes)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentNullException.ThrowIfNull(expectedHashes);
        var actualNames = migrations.Select(static migration => $"Migrations/{migration.FileName}").ToArray();
        if (actualNames.Distinct(StringComparer.Ordinal).Count() != actualNames.Length
            || !actualNames.Order(StringComparer.Ordinal).SequenceEqual(expectedHashes.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Migration integrity manifest does not match embedded SQL resources.");
        }

        foreach (var migration in migrations)
        {
            var name = $"Migrations/{migration.FileName}";
            ReadOnlyMemory<byte> bytes = migration.RawSql
                ?? new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(migration.Sql));
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes.Span));
            if (!string.Equals(actualHash, expectedHashes[name], StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Migration source hash mismatch: {name}");
            }
        }
    }

    private static Dictionary<string, string> ReadIntegrityManifest(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(ResourcePrefix + "migration-integrity.sha256")
            ?? throw new InvalidOperationException("Migration integrity manifest was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.StartsWith('#', StringComparison.Ordinal)) continue;
            var parts = line.Split("  ", StringSplitOptions.None);
            if (parts.Length != 2 || parts[0].Length != 64 || parts[0].Any(static character => !char.IsAsciiHexDigit(character) || char.IsUpper(character))
                || !parts[1].StartsWith("Migrations/", StringComparison.Ordinal) || !hashes.TryAdd(parts[1], parts[0]))
            {
                throw new InvalidOperationException("Migration integrity manifest is invalid.");
            }
        }

        return hashes;
    }
}

public sealed record MigrationScript(string Version, string Direction, string FileName, string Sql, ReadOnlyMemory<byte>? RawSql = null);
