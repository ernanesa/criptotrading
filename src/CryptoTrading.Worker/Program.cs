using CryptoTrading.Configuration;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});
builder.Configuration.AddLocalDotEnv();
builder.Services.AddTradingConfiguration(builder.Configuration);
builder.Services.AddOptions<TradingOptions>().ValidateOnStart();
builder.Services.AddHostedService<CryptoTrading.Worker.TradingWorker>();

var host = builder.Build();
host.Run();
