// Program.cs
using LineraOrchestrator.Services;
using LineraOrchestrator.Models;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5290");

//=========DI=============
var lineraConfig = new LineraConfig
{
    LineraCliPath = "linera",
    UserChainPath = EnvironmentService.GetUserChainPath(),

    // DOCKER WASM PATH
    XFighterPath = "./wasm",
    LeaderboardPath = "./wasm",
    TournamentPath = "./wasm",
    FungiblePath = "./wasm",
    UserXFighterPath = "./wasm",
    FriendPath = "./wasm",

    // Conway Testnet Path
    UseRemoteTestnet = true,
    StartServiceWhenRemote = true,
    FaucetUrl = "https://faucet.testnet-conway.linera.net",
};

Console.WriteLine($"[PROGRAM] Is Docker: {EnvironmentService.IsRunningInDocker()}");
Console.WriteLine($"[PROGRAM] Set up UserChainPath: {lineraConfig.UserChainPath}");

builder.Services.AddSingleton(lineraConfig);
builder.Services.AddSingleton<LineraCliRunner>();
builder.Services.AddSingleton<LineraOrchestratorService>();
builder.Services.AddSingleton<MatchChainService>();
builder.Services.AddSingleton<LeaderboardService>();
builder.Services.AddSingleton<TournamentService>();
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<FriendService>();
builder.Services.AddSingleton<BettingService>();

var socketsHandler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = 1000,
    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    EnableMultipleHttp2Connections = true
};

var httpClient = new HttpClient(socketsHandler)
{
    Timeout = TimeSpan.FromSeconds(120)
};
builder.Services.AddSingleton<HttpClient>(httpClient);

// ---- Thêm CORS ở đây ----
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy
            .WithOrigins(
                // API Header của Client
                "http://localhost:5173",
                "http://127.0.0.1:5173",
                "http://localhost:5174",
                "http://127.0.0.1:5174",

                // API Header của Server
                "http://localhost:5173/",
                "http://127.0.0.1:5173/",
                "http://localhost:5174/",
                "http://127.0.0.1:5174/",

                "https://xfighterzone.com",
                "https://api.xfighterzone.com",
                "https://live-demo.xfighterzone.com"
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});
// -------------------------

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

// routing + cors middleware
app.UseRouting();
app.UseCors("AllowAll");
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => new { status = "ok", message = "Linera Orchestrator Node is running" });

app.Run();