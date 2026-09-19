using System.Reflection;

#pragma warning disable IDE0305 // Keep the explicit array materialization for a stable migration list API.

namespace CryptoTrading.Persistence;

/// <summary>Loads ordered, embedded SQL migrations without selecting a runner.</summary>
public static class PersistenceMigrations
{
    private const string ResourcePrefix = "CryptoTrading.Persistence.Migrations.";

    public static IReadOnlyList<MigrationScript> ReadAll()
    {
        var assembly = typeof(PersistenceMigrations).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(static name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(name => Read(assembly, name))
            .OrderBy(static migration => migration.Version, StringComparer.Ordinal)
            .ThenBy(static migration => migration.Direction, StringComparer.Ordinal)
            .ToArray();
    }

    private static MigrationScript Read(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Migration resource was not found: {resourceName}");
        using var reader = new StreamReader(stream);
        var fileName = resourceName[ResourcePrefix.Length..];
        var parts = fileName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var version = parts[0].Split('_', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return new MigrationScript(version, parts[^2], fileName, reader.ReadToEnd());
    }
}

public sealed record MigrationScript(string Version, string Direction, string FileName, string Sql);
