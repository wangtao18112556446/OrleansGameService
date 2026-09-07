using GameServer.Gateway.Hosting;
using GameServer.SampleGameplay;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddGameGateway(builder.Configuration)
    .AddSampleVampirism();
builder.Host.UseGameOrleans(builder.Configuration);

var app = builder.Build();
app.UseGameGateway();
app.Run();
