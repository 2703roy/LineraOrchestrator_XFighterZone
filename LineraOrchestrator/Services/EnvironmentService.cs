// Services/EnvironmentService.cs
/// 1 Docker Mode
/// 2 Local Mode
namespace LineraOrchestrator.Services
{
    public static class EnvironmentService
    {
        public static bool IsRunningInDocker()
        {
            return Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ||
                   File.Exists("/.dockerenv") ||
                   Environment.GetEnvironmentVariable("LINERA_DOCKER_MODE") == "true";
        }

        public static string GetPublisherPath()
        {
            if (IsRunningInDocker())
            {
                return "/build/linera-publisher";
            }

            return "/home/roycrypto/linera-publisher";
        }

        public static string GetUserChainPath()
        {
            if (IsRunningInDocker())
            {
                return "/build/linera-users";
            }

            return "/home/roycrypto/linera-users";
        }

        public static string GetDataPath()
        {
            if (IsRunningInDocker())
            {
                return "/build/data";  // Docker
            }

            return "/home/roycrypto/.config/linera_orchestrator";
        }
    }
}