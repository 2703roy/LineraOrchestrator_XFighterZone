//Program.cs
using LineraOrchestrator.Services;
using LineraOrchestrator.Models;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5290");

//=========DI=============
// Testnet Conway Config 
var lineraConfig = new LineraConfig
{
    LineraCliPath = "/home/roycrypto/.cargo/bin/linera",
    XFighterPath = "/mnt/d/workspace/linera-protocol/examples/target/wasm32-unknown-unknown/release",
    LeaderboardPath = "/mnt/d/workspace/linera-protocol/examples/target/wasm32-unknown-unknown/release",

    // === testnet CONWAY mode + Backup local mode ===
    UseRemoteTestnet = false,          // true Setup Node CONWAY mode, false Setup Node Backup mode
    StartServiceWhenRemote = false,   // true Setup Service CONWAY mode, false Setup Service Backup mode
    FaucetUrl = "https://faucet.testnet-conway.linera.net",

    // chỉ định wallet/keystore/storage đã tạo bằng faucet testnet
    LineraWallet = "/home/roycrypto/.linera_testnet/wallet_0.json",
    LineraKeystore = "/home/roycrypto/.linera_testnet/keystore_0.json",
    LineraStorage = "rocksdb:/home/roycrypto/.linera_testnet/client_0.db"
};

builder.Services.AddSingleton(lineraConfig);
builder.Services.AddSingleton<LineraCliRunner>();
builder.Services.AddSingleton<LineraOrchestratorService>();

builder.Services.AddSingleton(new HttpClient(
    new HttpClientHandler
    {
        MaxConnectionsPerServer = 100 // tăng limit song song 26 Sep 25
    })
);

//builder.Services.AddSingleton<LineraConfig>();
//=========DI=============

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseHttpsRedirection();
}

app.UseHttpsRedirection();
app.MapControllers();
app.MapGet("/health", () => new { status = "ok", message = "Linera Orchestrator Node is running" });

app.Run();
