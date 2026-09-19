var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<CryptoTrading.Worker.TradingWorker>();

var host = builder.Build();
host.Run();
