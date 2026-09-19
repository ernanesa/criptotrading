using System.Xml.Linq;
using Xunit;

namespace CryptoTrading.Domain.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void DomainProjectHasNoOutwardProjectReferences()
    {
        var projectPath = Path.Combine(RepositoryRoot(), "src", "CryptoTrading.Domain", "CryptoTrading.Domain.csproj");
        var document = XDocument.Load(projectPath);
        var references = document
            .Descendants("ProjectReference")
            .Select(reference => (string?)reference.Attribute("Include"))
            .Where(reference => reference is not null)
            .Select(reference => reference!);

        foreach (var reference in references)
        {
            Assert.DoesNotContain("CryptoTrading.Api", reference, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CryptoTrading.Exchange", reference, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CryptoTrading.Configuration", reference, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DomainSourceDoesNotDependOnApplicationAdaptersOrConfiguration()
    {
        var sourceDirectory = Path.Combine(RepositoryRoot(), "src", "CryptoTrading.Domain");
        var forbiddenNamespaces = new[]
        {
            "CryptoTrading.Api",
            "CryptoTrading.Exchange",
            "CryptoTrading.Configuration"
        };

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(sourceFile);
            foreach (var forbiddenNamespace in forbiddenNamespaces)
            {
                Assert.DoesNotContain(forbiddenNamespace, source, StringComparison.Ordinal);
            }
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CryptoTrading.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
