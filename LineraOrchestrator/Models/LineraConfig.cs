// LineraConfig.cs
namespace LineraOrchestrator.Models
{
    public class LineraConfig
    {
        // Simplify mode
        public bool UseRemoteTestnet { get; set; } = true; // Mặc định local để dễ test
        public bool StartServiceWhenRemote { get; set; } = true;
        public string? FaucetUrl { get; set; } = "https://faucet.testnet-conway.linera.net";

        // Paths linh hoạt
        public string LineraCliPath { get; set; } = "linera"; // Mặc định trong PATH
        public string XFighterPath { get; set; } = "./wasm";
        public string LeaderboardPath { get; set; } = "./wasm";
        public string TournamentPath { get; set; } = "./wasm";

        // Environment-based paths
        public string? LineraWallet { get; set; }
        public string? LineraStorage { get; set; }
        public string? LineraKeystore { get; set; }

        public int? LineraNetPid { get; set; }
        public int? LineraServicePid { get; set; }

        // Deploy Apps
        public string? PublisherChainId { get; set; }
        public string? XFighterModuleId { get; set; }
        public string? XFighterAppId { get; set; }
        public string? LeaderboardAppId { get; set; }
        public string? TournamentAppId { get; set; }

        // Ready check
        public bool IsReady => !string.IsNullOrEmpty(PublisherChainId)
                       && !string.IsNullOrEmpty(XFighterModuleId)
                       && !string.IsNullOrEmpty(LeaderboardAppId)
                       && !string.IsNullOrEmpty(TournamentAppId);
    }
}