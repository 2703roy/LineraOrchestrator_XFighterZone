//Program.cs
using LineraOrchestrator.Services;
using LineraOrchestrator.Models;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5290");

//=========DI=============
var lineraConfig = new LineraConfig
{
    LineraCliPath = "linera",

    // DOCKER PATH
    XFighterPath = "./wasm",
    LeaderboardPath = "./wasm",
    TournamentPath = "./wasm",

    // DEV PATH
    //XFighterPath = "/mnt/d/workspace/linera-protocol/examples/target/wasm32-unknown-unknown/release",
    //LeaderboardPath = "/mnt/d/workspace/linera-protocol/examples/target/wasm32-unknown-unknown/release",
    //UserXFighterPath = "/mnt/d/workspace/linera-protocol/examples/target/wasm32-unknown-unknown/release",
    //TournamentPath = "/mnt/d/workspace/linera-protocol/examples/target/wasm32-unknown-unknown/release",

    // CONWAY MODE
    UseRemoteTestnet = true,          // true Setup Node CONWAY mode, false Setup Node Backup mode
    StartServiceWhenRemote = true,   // true Setup Service CONWAY mode, false Setup Service Backup mode
    FaucetUrl = "https://faucet.testnet-conway.linera.net",
};

builder.Services.AddSingleton(lineraConfig);
builder.Services.AddSingleton<LineraCliRunner>();
builder.Services.AddSingleton<LineraOrchestratorService>();


var socketsHandler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = 1000, // nhiều kết nối đồng thời đến cùng host
    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    EnableMultipleHttp2Connections = true
};

var httpClient = new HttpClient(socketsHandler)
{
    Timeout = TimeSpan.FromSeconds(120)
};
//=========DI=============
builder.Services.AddSingleton<HttpClient>(httpClient);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// register graceful shutdown
var orchestrator = app.Services.GetRequiredService<LineraOrchestratorService>();
var lifetime = app.Lifetime;
lifetime.ApplicationStopping.Register(() =>
{
    Console.WriteLine("[SHUTDOWN] ApplicationStopping called. Cleaning Linera processes.");
    orchestrator.StopAllLineraAsync().Wait();
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

//app.UseHttpsRedirection();
app.MapControllers();
app.MapGet("/health", () => new { status = "ok", message = "Linera Orchestrator Node is running" });

app.Run();
