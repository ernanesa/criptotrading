using CryptoTrading.Configuration;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddLocalDotEnv();
builder.Services.AddTradingConfiguration(builder.Configuration);
builder.Services.AddOptions<TradingOptions>().ValidateOnStart();
builder.Services.AddHostedService<CryptoTrading.Worker.TradingWorker>();

var host = builder.Build();
host.Run();
