//OrchestratorState.cs
namespace LineraOrchestrator.Models
{
    public class OrchestratorState
    {
        public string? PublisherChainId { get; set; }
        public string? UserXFighterModuleId { get; set; }
        public string? UserXFighterAppId { get; set; }
        public string? XFighterModuleId { get; set; }
        public string? XFighterAppId { get; set; }
        public string? LeaderboardAppId { get; set; }
        public string? TournamentAppId { get; set; }

        public bool IsValid =>
            !string.IsNullOrEmpty(PublisherChainId)
            && !string.IsNullOrEmpty(UserXFighterModuleId)
            && !string.IsNullOrEmpty(UserXFighterAppId)
            && !string.IsNullOrEmpty(XFighterModuleId)
            && !string.IsNullOrEmpty(XFighterAppId)
            && !string.IsNullOrEmpty(LeaderboardAppId)
            && !string.IsNullOrEmpty(TournamentAppId);
    }
}
