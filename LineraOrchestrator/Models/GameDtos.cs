/*DTO = Data Transfer Object (đối tượng truyền dữ liệu)
Client ↔ API
API ↔ Service
Service ↔ GraphQL
*/

using System.Text.Json.Serialization;

namespace LineraOrchestrator.Models
{
    public class SubmitMatchRequest
    {
        [JsonPropertyName("chainId")] public string? ChainId { get; set; }
        [JsonPropertyName("appId")] public string? AppId { get; set; }
        [JsonPropertyName("matchResult")] public MatchResult MatchResult { get; set; } = new();
    }
    // Durable submit request model
    public class SubmitRequest
    {
        [JsonPropertyName("chainId")] public string? ChainId { get; set; }
        [JsonPropertyName("appId")] public string? AppId { get; set; }
        [JsonPropertyName("matchResult")] public MatchResult? MatchResult { get; set; }

        // Metadata để retry/debug
        [JsonPropertyName("attempts")] public int Attempts { get; set; } = 0;
        [JsonPropertyName("lastError")] public string? LastError { get; set; }
        [JsonPropertyName("nextTryAt")] public string? NextTryAt { get; set; }
        [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("s");
    }

    public class CreateMatchRequest
    {
        [JsonPropertyName("player1")] public string? Player1 { get; set; }

        [JsonPropertyName("player2")] public string? Player2 { get; set; }

        [JsonPropertyName("matchType")] public string? MatchType { get; set; } = "rank";
    }
    // Class helper
    public class ChildAppInfo
    {
        [JsonPropertyName("chainId")] public string? ChainId { get; set; }
        [JsonPropertyName("appId")] public string? AppId { get; set; }
    }
    public class MatchResult
    {
        [JsonPropertyName("matchId")] public string MatchId { get; set; } = string.Empty;

        [JsonPropertyName("player1Username")] public string Player1Username { get; set; } = string.Empty;

        [JsonPropertyName("player2Username")] public string Player2Username { get; set; } = string.Empty;

        [JsonPropertyName("player1Userchain")] public string Player1Userchain { get; set; } = string.Empty;

        [JsonPropertyName("player2Userchain")] public string Player2Userchain { get; set; } = string.Empty;

        [JsonPropertyName("player1Heroname")] public string Player1Heroname { get; set; } = string.Empty;

        [JsonPropertyName("player2Heroname")] public string Player2Heroname { get; set; } = string.Empty;

        [JsonPropertyName("winnerUsername")] public string WinnerUsername { get; set; } = string.Empty;

        [JsonPropertyName("loserUsername")] public string LoserUsername { get; set; } = string.Empty;

        [JsonPropertyName("durationSeconds")] public int DurationSeconds { get; set; } = 0;

        [JsonPropertyName("mapName")] public string? MapName { get; set; }

        [JsonPropertyName("matchType")] public string? MatchType { get; set; }

        [JsonPropertyName("afk")] public string? Afk { get; set; }
    }
    //public class MatchMapping
    //{
    //    [JsonPropertyName("matchId")] public string? MatchId { get; set; }
    //    [JsonPropertyName("chainId")] public string? ChainId { get; set; } = string.Empty;
    //    [JsonPropertyName("appId")] public string? AppId { get; set; }
    //    [JsonPropertyName("player1")] public string? Player1 { get; set; } = string.Empty;
    //    [JsonPropertyName("player2")] public string? Player2 { get; set; } = string.Empty;
    //    [JsonPropertyName("player1Userchain")] public string? Player1Userchain { get; set; } = string.Empty;
    //    [JsonPropertyName("player2Userchain")] public string? Player2Userchain { get; set; } = string.Empty;
    //    [JsonPropertyName("player1Heroname")] public string? Player1Heroname { get; set; } = string.Empty;
    //    [JsonPropertyName("player2Heroname")] public string? Player2Heroname { get; set; } = string.Empty;
    //    [JsonPropertyName("matchType")] public string? MatchType { get; set; } = "rank"; //Rank || Normal
    //    [JsonPropertyName("status")] public string Status { get; set; } = "created";
    //    // opId returned from linera service / GraphQL (hex)
    //    [JsonPropertyName("submittedOpId")] public string? SubmittedOpId { get; set; }
    //    // ISO 8601 UTC timestamp of submission
    //    [JsonPropertyName("submittedAt")] public string? SubmittedAt { get; set; }
    //}

    #region Tournament Setup DTOs
    public class TournamentMatchInput
    {
        [JsonPropertyName("matchId")]
        public string MatchId { get; set; } = string.Empty;

        [JsonPropertyName("player1")]
        public string Player1 { get; set; } = string.Empty;

        [JsonPropertyName("player2")]
        public string Player2 { get; set; } = string.Empty;

        [JsonPropertyName("winner")]
        public string? Winner { get; set; }

        [JsonPropertyName("round")]
        public string Round { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = "waiting";
    }

    public class SetParticipantsRequest
    {
        public List<string> Participants { get; set; } = [];
    }

    #endregion

    #region User Authentication DTOs
    public class AuthUserRequest
    {
        [JsonPropertyName("userName")] public string UserName { get; set; } = string.Empty;
    }

    public class UserInfoResponse
    {
        public string? UserName { get; set; } = string.Empty;
        public string? UserChainId { get; set; } = string.Empty;
        public string? PublicKey { get; set; } = string.Empty;
    }

    #endregion

    #region Friend DTOs

    public class SendFriendRequestRequest
    {
        public string UserName { get; set; } = string.Empty;
        public string ToUserChain { get; set; } = string.Empty;
    }
    public class AcceptFriendRequestRequest
    {
        public string UserName { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
    }

    public class RejectFriendRequestRequest
    {
        public string UserName { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
    }

    public class RemoveFriendRequest
    {
        public string UserName { get; set; } = string.Empty;
        public string FriendChain { get; set; } = string.Empty;
    }
    #endregion

    #region Betting DTOs
    public class SettleMatchRequest
    {
        public string MatchId { get; set; } = string.Empty;
        public string Winner { get; set; } = string.Empty;
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MatchStatus
    {
        Open,
        Closed,
        Settled
    }
    public class SetMatchMetadataRequest
    {
        [JsonPropertyName("matchId")] public string MatchId { get; set; } = string.Empty;
        [JsonPropertyName("bettingDeadlineUnix")] public long? BettingDeadlineUnix { get; set; } //betting time
        [JsonPropertyName("matchStartUnix")] public long? MatchStartUnix { get; set; } // match opening time “Match QF1 Start at 09:00 UTC”
        [JsonPropertyName("status")] public MatchStatus Status { get; set; } = MatchStatus.Open;
    }
    public class UserPlaceBetRequest
    {
        public string? UserName { get; set; }
        public string? MatchId { get; set; }
        public string? Player { get; set; }
        public ulong Amount { get; set; }
    }
    public class TransferTokensRequest
    {
        public string UserName { get; set; } = string.Empty;
        public ulong Amount { get; set; } = 1000000;
    }
    #endregion
}
