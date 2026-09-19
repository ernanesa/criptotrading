using CryptoTrading.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddLocalDotEnv();
builder.Services.AddTradingConfiguration(builder.Configuration);
builder.Services.AddOptions<TradingOptions>().ValidateOnStart();

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
    .WithName("GetLiveness");

app.MapGet("/health/ready", () => Results.Ok(new { status = "ready", environment = app.Environment.EnvironmentName }))
    .WithName("GetReadiness");

app.Run();
