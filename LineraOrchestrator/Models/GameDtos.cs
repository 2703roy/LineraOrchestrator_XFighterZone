/*DTO = Data Transfer Object (đối tượng truyền dữ liệu)
Cclass đơn giản chỉ chứa dữ liệu (properties), không có hoặc rất ít logic.
Dùng để định nghĩa rõ ràng schema dữ liệu khi truyền qua lại giữa:
Client ↔ API
API ↔ Service
Service ↔ GraphQL
*/

using System.Text.Json.Serialization;

namespace LineraOrchestrator.Models
{
    //Cấu hình global JsonSerializerOptions (camelCase) và dùng lại options đó
    //khi serialize payload GraphQL. Ít phải annotate, tiện khi có nhiều DTO
    //[JsonPropertyName("moduleId")] -> ý nghĩa
    // ===================== Models for new endpoints =====================
    // DTO “ngoại giao”/đặc biệt (những DTO bạn giao tiếp trực tiếp với service bên ngoài
    // và muốn đảm bảo không phụ thuộc cấu hình global)
    // Payload gửi xuống GraphQL recordScore(matchResult: MatchResultInput!)
    public class SubmitMatchRequest
    {
        [JsonPropertyName("chainId")]
        public string? ChainId { get; set; }

        [JsonPropertyName("matchResult")]
        public MatchResult MatchResult { get; set; } = new();
    }
    // Durable submit request model
    public class SubmitRequest
    {
        [JsonPropertyName("chainId")] public string? ChainId { get; set; }
        [JsonPropertyName("matchResult")] public MatchResult? MatchResult { get; set; }

        // Metadata để retry/debug
        [JsonPropertyName("attempts")] public int Attempts { get; set; } = 0;
        [JsonPropertyName("lastError")] public string? LastError { get; set; }
        [JsonPropertyName("nextTryAt")] public string? NextTryAt { get; set; }
        [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("s");
    }

    public class CreateMatchRequest
    {
        [JsonPropertyName("player1")]
        public string? Player1 { get; set; }

        [JsonPropertyName("player2")]
        public string? Player2 { get; set; }
    }
    public class MatchResult
    {
        [JsonPropertyName("matchId")] public string MatchId { get; set; } = string.Empty;

        [JsonPropertyName("player1Username")] public string Player1Username { get; set; } = string.Empty;

        [JsonPropertyName("player2Username")] public string Player2Username { get; set; } = string.Empty;

        [JsonPropertyName("winnerUsername")] public string WinnerUsername { get; set; } = string.Empty;

        [JsonPropertyName("loserUsername")] public string LoserUsername { get; set; } = string.Empty;

        [JsonPropertyName("durationSeconds")] public int DurationSeconds { get; set; } = 0;

        [JsonPropertyName("timestamp")] public int Timestamp { get; set; } = 0;

        [JsonPropertyName("player1Score")] public int Player1Score { get; set; } = 0;

        [JsonPropertyName("player2Score")] public int Player2Score { get; set; } = 0;

        [JsonPropertyName("mapName")] public string? MapName { get; set; }

        [JsonPropertyName("matchType")] public string? MatchType { get; set; }

        [JsonPropertyName("afk")] public string? Afk { get; set; }
    }
    public class MatchMapping
    {
        [JsonPropertyName("matchId")] public string? MatchId { get; set; }
        [JsonPropertyName("chainId")] public string? ChainId { get; set; } = string.Empty;
        [JsonPropertyName("appId")] public string? AppId { get; set; }
        [JsonPropertyName("player1")] public string? Player1 { get; set; } = string.Empty;
        [JsonPropertyName("player2")] public string? Player2 { get; set; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; set; } = "created";
        // opId returned from linera service / GraphQL (hex)
        [JsonPropertyName("submittedOpId")] public string? SubmittedOpId { get; set; }
        [JsonPropertyName("submittedAt")] public string? SubmittedAt { get; set; }
    }
    // 9Sep25 Updated History Tracking
    public class PlayerMatchEntry
    {
        [JsonPropertyName("chainId")] public string ChainId { get; set; } = string.Empty;

        [JsonPropertyName("matchResult")] public MatchResult MatchResult { get; set; } = new();
    }
    // Mapping Save load data player
    public class PlayerStats
    {
        public HashSet<string> Chains { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int TotalMatches { get; set; } = 0;
        public int Wins { get; set; } = 0;
        public int Losses { get; set; } = 0;
        public string? LastPlayed { get; set; } // ISO UTC string; or DateTime? if you prefer
    }
    // Mapping Save load data snapshot leaderboard
    public class LeaderboardSnapshot
    {
        public string SnapshotId { get; set; } = string.Empty;
        public string? CreatedAt { get; set; }
        public List<string> TopPlayers { get; set; } = []; // top 8
        public List<string> AllPlayers { get; set; } = []; // all players
        public string OnChainOpId { get; set; } = string.Empty;
        public TournamentMeta? Tournament { get; set; }
    }
    public class TournamentMeta
    {
        public string SnapshotId { get; set; } = string.Empty;
        public string? CreatedAt { get; set; }
        public string? CompletedAt { get; set; }
        public List<string> TopPlayers { get; set; } = [];
        public string Status { get; set; } = "Created"; // Created | Running | Completed
        public string? Name { get; set; }
        public string? Champion { get; set; }
        public string? RunnerUp { get; set; }
        public string CurrentRound { get; set; } = "None"; // Quarterfinal | Semifinal | Final
        public List<TournamentMatch> Matches { get; set; } = []; // Danh sách trận (QF1..Final)

        // helper info for UI:
        public int TotalMatches => Matches?.Count ?? 0;

        public string? Duration => (CreatedAt != null && CompletedAt != null)
            ? $"{(DateTime.Parse(CompletedAt) - DateTime.Parse(CreatedAt)).TotalMinutes:F0} minutes"
            : null;
    }
    public class TournamentMatch
    {
        public string MatchId { get; set; } = string.Empty;
        public string Player1 { get; set; } = string.Empty;
        public string Player2 { get; set; } = string.Empty;
        public string? Winner { get; set; }
        public string? Loser { get; set; }
        public string Status { get; set; } = "waiting"; // waiting | playing | completed
    }
    public class InitTournamentRequest
    {
        [JsonPropertyName("order")] public List<string>? Order { get; set; }
    }
}
