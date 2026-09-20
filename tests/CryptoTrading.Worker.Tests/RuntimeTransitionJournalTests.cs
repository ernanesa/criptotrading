using System.Text.Json;
using CryptoTrading.Domain.Runtime;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class RuntimeTransitionJournalTests
{
    [Fact]
    public async Task JournalAppendsHashChainedRuntimeEvidenceWithoutSecrets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cryptotrading-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "runtime.jsonl");
        try
        {
            using var journal = new RuntimeTransitionJournal(path);
            var first = new RuntimeStateTransition(RuntimeState.Stopped, RuntimeState.Bootstrapping, 1, "startup", DateTimeOffset.UnixEpoch);
            var second = new RuntimeStateTransition(RuntimeState.Bootstrapping, RuntimeState.Reconciling, 2, "reconcile", DateTimeOffset.UnixEpoch.AddSeconds(1));
            await journal.AppendAsync(first);
            await journal.AppendAsync(second);

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(2, lines.Length);
            using var firstDocument = JsonDocument.Parse(lines[0]);
            using var secondDocument = JsonDocument.Parse(lines[1]);
            Assert.Equal("", firstDocument.RootElement.GetProperty("previousHash").GetString());
            Assert.Equal(
                firstDocument.RootElement.GetProperty("chainSha256").GetString(),
                secondDocument.RootElement.GetProperty("previousHash").GetString());
            Assert.DoesNotContain("secret", string.Join(Environment.NewLine, lines), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task JournalContinuesRevisionAndHashChainAfterRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cryptotrading-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "runtime.jsonl");
        try
        {
            var first = new RuntimeStateTransition(RuntimeState.Stopped, RuntimeState.Bootstrapping, 1, "startup", DateTimeOffset.UnixEpoch);
            var second = new RuntimeStateTransition(RuntimeState.Bootstrapping, RuntimeState.Reconciling, 2, "reconcile", DateTimeOffset.UnixEpoch.AddSeconds(1));
            using (var firstJournal = new RuntimeTransitionJournal(path))
            {
                await firstJournal.AppendAsync(first);
            }

            using var restartedJournal = new RuntimeTransitionJournal(path);
            Assert.Equal(1, restartedJournal.LastRevision);
            await restartedJournal.AppendAsync(second);

            var lines = await File.ReadAllLinesAsync(path);
            using var secondDocument = JsonDocument.Parse(lines[1]);
            Assert.Equal(2, secondDocument.RootElement.GetProperty("transition").GetProperty("revision").GetInt64());
            var previousHash = secondDocument.RootElement.GetProperty("previousHash").GetString();
            Assert.NotNull(previousHash);
            Assert.NotEmpty(previousHash);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
