//OrchestratorState.cs
namespace LineraOrchestrator.Models
{
    public class OrchestratorState
    {
        public string? PublisherChainId { get; set; }
        public string? PublisherOwner { get; set; }
        public string? UserXFighterModuleId { get; set; }
        public string? XFighterModuleId { get; set; }
        public string? XFighterAppId { get; set; }
        public string? LeaderboardAppId { get; set; }
        public string? TournamentAppId { get; set; }
        public string? FungibleAppId { get; set; }
        public string? FriendAppId { get; set; }
        public bool IsValid =>
            !string.IsNullOrEmpty(PublisherChainId)
            && !string.IsNullOrEmpty(PublisherOwner)
            && !string.IsNullOrEmpty(UserXFighterModuleId)
            && !string.IsNullOrEmpty(XFighterModuleId)
            && !string.IsNullOrEmpty(XFighterAppId)
            && !string.IsNullOrEmpty(LeaderboardAppId)
            && !string.IsNullOrEmpty(TournamentAppId)
            && !string.IsNullOrEmpty(FungibleAppId)
            && !string.IsNullOrEmpty(FriendAppId);
    }
}
