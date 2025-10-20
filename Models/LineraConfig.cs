//LineraConfig.cs
namespace LineraOrchestrator.Models
{
    public class LineraConfig
    {
        // Testnet Conway
        public bool UseRemoteTestnet { get; set; } = true; // nếu true: KHÔNG chạy `linera net up` local
        public bool StartServiceWhenRemote { get; set; } = true; // nếu true: vẫn cố gắng start `linera service` sau khi dùng remote testnet
        public string? FaucetUrl { get; set; } // "https://faucet.testnet-conway.linera.net"
        public string? LineraWallet { get; set; }
        public string? LineraStorage { get; set; }
        public string? LineraKeystore { get; set; }
        public int? LineraNetPid { get; set; }
        public string LineraCliPath { get; set; } = "/home/roycrypto/.cargo/bin/linera";
        // Thêm các thuộc tính đường dẫn đến thư mục xfighter và leaderboard chứa wasm
        public string XFighterPath { get; set; } = "/mnt/d/workspace/linera-protocol/examples/target/wasm32-unknown-unknown/release";
        public string LeaderboardPath { get; set; } = "/mnt/d/workspace/linera-protocol/examples/target/wasm32-unknown-unknown/release";
        // Stored IDs (in-memory). These are set by orchestrator when publish/create succeed.
        public string? PublisherChainId { get; set; } // chainId mà cả Leaderboard + Xfighter cùng deploy vào
        public string? XFighterModuleId { get; set; }
        public string? XFighterAppId { get; set; }
        public string? LeaderboardAppId { get; set; }
        public bool IsReady => !string.IsNullOrEmpty(LineraWallet)
                       && !string.IsNullOrEmpty(LineraKeystore)
                       && !string.IsNullOrEmpty(LineraStorage)
                       && !string.IsNullOrEmpty(XFighterModuleId)
                       && !string.IsNullOrEmpty(LeaderboardAppId)
                       && !string.IsNullOrEmpty(PublisherChainId)
                       && !string.IsNullOrEmpty(TournamentAppId);
        // Liên quan đến bật tắt service
        public int? LineraServicePid { get; set; }
        public string? TournamentPath { get; set; } = "/mnt/d/workspace/linera-protocol/examples/target/wasm32-unknown-unknown/release";
        public string? TournamentAppId { get; set; }
    }
}
