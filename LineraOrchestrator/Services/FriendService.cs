// FriendService.cs
using System.Text;
using System.Text.Json;
using LineraOrchestrator.Models;

namespace LineraOrchestrator.Services
{
    public class FriendService
    {
        private readonly LineraConfig _config;
        private readonly HttpClient _httpClient;
        private readonly UserService _userService;

        public FriendService(LineraConfig config, HttpClient httpClient, UserService userService)
        {
            _config = config;
            _httpClient = httpClient;
            _userService = userService;
        }

        #region User Friend
        public async Task<string> SendFriendRequestAsync(string userName, string toUserChain)
        {
            var userChainId = _userService.GetUserChainIdFromLocal(userName);
            var userPort = _userService.GetUserPortFromStorage(userName);

            var url = $"http://localhost:{userPort}/chains/{userChainId}/applications/{_config.FriendAppId}";

            var graphql = $@"mutation {{
                sendFriendRequest(
                    toUserChain: ""{toUserChain}""
                )
            }}";

            var payload = new { query = graphql };

            var resp = await _userService.PostSingleWithUserServiceWaitAsync(
                userName,
                url,
                () => new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json"),
                waitSeconds: 8,
                postTimeoutSeconds: 30);

            var text = await resp.Content.ReadAsStringAsync();

            Console.WriteLine($"[USER-FRIEND] User {userName} sent friend request to {toUserChain}");
            Console.WriteLine($"[LINERA-CLI-OUTPUT] SendFriendRequest respone: \n{text}");
            resp.EnsureSuccessStatusCode();
            return text;
        }

        public async Task<string> AcceptFriendRequestAsync(string userName, string requestId)
        {
            var userChainId = _userService.GetUserChainIdFromLocal(userName);
            var userPort = _userService.GetUserPortFromStorage(userName);

            var url = $"http://localhost:{userPort}/chains/{userChainId}/applications/{_config.FriendAppId}";

            var graphql = $@"mutation {{
                acceptFriendRequest(
                    requestId: ""{requestId}""
                )
            }}";

            var payload = new { query = graphql };
            var resp = await _userService.PostSingleWithUserServiceWaitAsync(
                userName,
                url,
                () => new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json"),
                waitSeconds: 8,
                postTimeoutSeconds: 30);

            var text = await resp.Content.ReadAsStringAsync();

            Console.WriteLine($"[USER-FRIEND] User {userName} accepted friend request {requestId}");
            Console.WriteLine($"[LINERA-CLI-OUTPUT] AcceptFriendRequest respone: \n{text}");
            resp.EnsureSuccessStatusCode();
            return text;
        }

        /// Từ chối yêu cầu kết bạn
        public async Task<string> RejectFriendRequestAsync(string userName, string requestId)
        {
            var userChainId = _userService.GetUserChainIdFromLocal(userName);
            var userPort = _userService.GetUserPortFromStorage(userName);

            var url = $"http://localhost:{userPort}/chains/{userChainId}/applications/{_config.FriendAppId}";

            var graphql = $@"mutation {{
                rejectFriendRequest(
                    requestId: ""{requestId}""
                )
            }}";

            var payload = new { query = graphql };
            var resp = await _userService.PostSingleWithUserServiceWaitAsync(
                  userName,
                  url,
                  () => new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json"),
                  waitSeconds: 8,
                  postTimeoutSeconds: 30);

                    var text = await resp.Content.ReadAsStringAsync();

            Console.WriteLine($"[USER-FRIEND] User {userName} rejected friend request {requestId}");
            Console.WriteLine($"[LINERA-CLI-OUTPUT] RejectFriendRequest respone: \n{text}");
            resp.EnsureSuccessStatusCode();
            return text;
        }

        /// Xóa bạn bè
        public async Task<string> RemoveFriendAsync(string userName, string friendChain)
        {
            var userChainId = _userService.GetUserChainIdFromLocal(userName);
            var userPort = _userService.GetUserPortFromStorage(userName);

            if (!await _userService.WaitForUserServiceViaMonitorAsync(userName, timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                throw new InvalidOperationException($"User service {userName} not stable after wait");
            }

            var url = $"http://localhost:{userPort}/chains/{userChainId}/applications/{_config.FriendAppId}";

            var graphql = $@"mutation {{
                removeFriend(
                    friendChain: ""{friendChain}""
                )
            }}";

            var payload = new { query = graphql };
            var resp = await _userService.PostSingleWithUserServiceWaitAsync(
                  userName,
                  url,
                  () => new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json"),
                  waitSeconds: 8,
                  postTimeoutSeconds: 30);

            var text = await resp.Content.ReadAsStringAsync();

            Console.WriteLine($"[USER-FRIEND] User {userName} removed friend {friendChain}");
            Console.WriteLine($"[LINERA-CLI-OUTPUT] RemoveFriend respone: \n{text}");
            resp.EnsureSuccessStatusCode();
            return text;
        }

        /// Lấy danh sách bạn bè
        public async Task<string> GetFriendsAsync(string userName)
        {
            var userChainId = _userService.GetUserChainIdFromLocal(userName);
            var userPort = _userService.GetUserPortFromStorage(userName);

            if (!await _userService.WaitForUserServiceViaMonitorAsync(userName, timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                throw new InvalidOperationException($"User service {userName} not stable after wait");
            }

            var url = $"http://localhost:{userPort}/chains/{userChainId}/applications/{_config.FriendAppId}";

            var graphql = @"
                query {
                    friends {
                        chainId
                    }
                    friendsCount
                }";

            var payload = new { query = graphql };
            var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(url, content);
            var text = await resp.Content.ReadAsStringAsync();

            Console.WriteLine($"[USER-FRIEND] User {userName} check friends list");
            Console.WriteLine($"[LINERA-CLI-OUTPUT] GetFriends respone: \n{text}");

            resp.EnsureSuccessStatusCode();
            return text;
        }

        /// Lấy danh sách yêu cầu kết bạn đang chờ (incoming)
        public async Task<string> GetPendingRequestsAsync(string userName)
        {
            var userChainId = _userService.GetUserChainIdFromLocal(userName);
            var userPort = _userService.GetUserPortFromStorage(userName);

            if (!await _userService.WaitForUserServiceViaMonitorAsync(userName, timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                throw new InvalidOperationException($"User service {userName} not stable after wait");
            }

            var url = $"http://localhost:{userPort}/chains/{userChainId}/applications/{_config.FriendAppId}";

            var graphql = @"
                query {
                    pendingRequests {
                        requestId
                        fromChain
                        timestamp
                        status
                    }
                    pendingRequestsCount
                }";

            var payload = new { query = graphql };
            var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(url, content);
            var text = await resp.Content.ReadAsStringAsync();

            Console.WriteLine($"[USER-FRIEND] User {userName} Check pending requests");
            Console.WriteLine($"[LINERA-CLI-OUTPUT] PendingRequests respone: \n{text}");
            resp.EnsureSuccessStatusCode();
            return text;
        }

        /// Lấy danh sách yêu cầu đã gửi đi (outgoing)
        public async Task<string> GetSentRequestsAsync(string userName)
        {
            var userChainId = _userService.GetUserChainIdFromLocal(userName);
            var userPort = _userService.GetUserPortFromStorage(userName);

            if (!await _userService.WaitForUserServiceViaMonitorAsync(userName, timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                throw new InvalidOperationException($"User service {userName} not stable after wait");
            }

            var url = $"http://localhost:{userPort}/chains/{userChainId}/applications/{_config.FriendAppId}";

            var graphql = @"
            query {
                sentRequests {
                    requestId
                    fromChain
                    timestamp
                    status
                }
                sentRequestsCount
            }";

            var payload = new { query = graphql };
            var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(url, content);
            var text = await resp.Content.ReadAsStringAsync();

            Console.WriteLine($"[USER-FRIEND] User {userName} sent requests");
            Console.WriteLine($"[LINERA-CLI-OUTPUT] SentRequests respone: \n{text}");
            resp.EnsureSuccessStatusCode();
            return text;
        }

        /// Kiểm tra xem có phải bạn bè với user khác không
        public async Task<string> IsFriendAsync(string userName, string targetChainId)
        {
            var userChainId = _userService.GetUserChainIdFromLocal(userName);
            var userPort = _userService.GetUserPortFromStorage(userName);

            if (!await _userService.WaitForUserServiceViaMonitorAsync(userName, timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                throw new InvalidOperationException($"User service {userName} not stable after wait");
            }

            var url = $"http://localhost:{userPort}/chains/{userChainId}/applications/{_config.FriendAppId}";

            var graphql = $@"
                query {{
                    isFriend(chainId: ""{targetChainId}"")
                }}";

            var payload = new { query = graphql };
            var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(url, content);
            var text = await resp.Content.ReadAsStringAsync();

            Console.WriteLine($"[USER-FRIEND] User {userName} checked friendship with {targetChainId}");
            Console.WriteLine($"[LINERA-CLI-OUTPUT] IsFriend respone: \n{text}");
            resp.EnsureSuccessStatusCode();
            return text;
        }

        /// Lấy thông tin chi tiết của một yêu cầu kết bạn
        public async Task<string> GetFriendRequestAsync(string userName, string requestId)
        {
            var userChainId = _userService.GetUserChainIdFromLocal(userName);
            var userPort = _userService.GetUserPortFromStorage(userName);

            if (!await _userService.WaitForUserServiceViaMonitorAsync(userName, timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                throw new InvalidOperationException($"User service {userName} not stable after wait");
            }

            var url = $"http://localhost:{userPort}/chains/{userChainId}/applications/{_config.FriendAppId}";

            var graphql = $@"
                query {{
                    friendRequest(requestId: ""{requestId}"") {{
                        requestId
                        fromChain
                        timestamp
                        status
                    }}
                }}";

            var payload = new { query = graphql };
            var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(url, content);
            var text = await resp.Content.ReadAsStringAsync();

            Console.WriteLine($"[USER-FRIEND] User {userName} check friend request details {requestId}");
            Console.WriteLine($"[LINERA-CLI-OUTPUT] FriendRequest details respone: \n{text}");
            resp.EnsureSuccessStatusCode();
            return text;
        }
        #endregion
    }
}