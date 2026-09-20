using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CryptoTrading.Observability.Tests;

public sealed class LocalOpenTelemetryOptionsTests
{
    [Fact]
    public void BlankEndpointKeepsOtlpExporterDisabled()
    {
        var options = new LocalOpenTelemetryOptions();

        options.Validate();

        Assert.False(options.IsEnabled);
        Assert.Null(options.ValidatedEndpoint);
    }

    [Theory]
    [InlineData("https://collector.example.test:4317")]
    public void NonLoopbackEndpointRequiresExplicitAllowlist(string endpoint, string protocol = "grpc")
    {
        var options = new LocalOpenTelemetryOptions { Endpoint = endpoint, Protocol = protocol };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void LoopbackHttpProtobufEndpointIsAccepted()
    {
        var options = new LocalOpenTelemetryOptions
        {
            Endpoint = "http://127.0.0.1:4318",
            Protocol = "http/protobuf"
        };

        options.Validate();

        Assert.Equal("127.0.0.1", options.ValidatedEndpoint!.Host);
    }

    [Fact]
    public void ExplicitAllowlistAcceptsNonLoopbackCollectorWithoutNetworkCall()
    {
        var options = new LocalOpenTelemetryOptions
        {
            Endpoint = "https://collector.example.test:4317",
            AllowedHosts = "collector.example.test"
        };

        options.Validate();

        Assert.Equal("collector.example.test", options.ValidatedEndpoint!.Host);
    }

    [Theory]
    [InlineData("http://127.0.0.1:4317/path?token=secret")]
    [InlineData("http://user@127.0.0.1:4317")]
    [InlineData("http://127.0.0.1:4318", "grpc")]
    [InlineData("http://127.0.0.1:4317", "http/protobuf")]
    public void UnsafeOrMismatchedEndpointIsRejected(string endpoint, string protocol = "grpc")
    {
        var options = new LocalOpenTelemetryOptions { Endpoint = endpoint, Protocol = protocol };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void RegistrationWithBlankEndpointDoesNotRequireCollector()
    {
        var services = new ServiceCollection();

        LocalOpenTelemetryOptions.AddLocalOpenTelemetry(
            services,
            new LocalOpenTelemetryOptions(),
            "CryptoTrading.Tests");

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider);
    }
}
