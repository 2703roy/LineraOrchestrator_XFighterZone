// BettingService.cs
using System.Text;
using System.Text.Json;
using System.Web;
using LineraOrchestrator.Models;

namespace LineraOrchestrator.Services
{
    public class BettingService
    {
        private readonly LineraConfig _config;
        private readonly HttpClient _httpClient;
        private readonly UserService _userService;
        private readonly LineraOrchestratorService _orchestrator;

        public BettingService(LineraConfig config, HttpClient httpClient, UserService userService, LineraOrchestratorService orchestrator)
        {
            _config = config;
            _httpClient = httpClient;
            _userService = userService;
            _orchestrator = orchestrator;
        }

        #region User Asset Management (Place Bet & Transactions)
        public async Task<string> UserPlaceBetAsync(string userName, string matchId, string player, ulong amount)
        {
            var userChainId = _userService.GetUserChainIdFromLocal(userName);
            var userAppId = _userService.GetUserAppId(userName);
            var userPort = _userService.GetUserPortFromStorage(userName);

            if (!await _userService.WaitForUserServiceViaMonitorAsync(userName, timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                throw new InvalidOperationException($"User service {userName} not stable after wait");
            }

            var url = $"http://localhost:{userPort}/chains/{userChainId}/applications/{userAppId}";

            var graphql = $@"mutation {{
                placeBet(
                    matchId: ""{matchId}""
                    player: ""{player}""
                    amount: {amount}
                )
            }}";

            var payload = new { query = graphql };
            var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(url, content);
            var text = await resp.Content.ReadAsStringAsync();

            Console.WriteLine($"[USER-XFIGHTER] [BET] User {userName} placed bet: {text}");
            resp.EnsureSuccessStatusCode();
            return text;
        }

        public async Task<string> GetTransactionsAsync(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName))
                throw new ArgumentNullException(nameof(userName));

            var userChainId = _userService.GetUserChainIdFromLocal(userName);
            var userAppId = _userService.GetUserAppId(userName);
            var userPort = _userService.GetUserPortFromStorage(userName);

            if (string.IsNullOrWhiteSpace(userChainId) || string.IsNullOrWhiteSpace(userAppId))
                throw new InvalidOperationException($"Missing chain/app info for user {userName}");

            if (!await _userService.WaitForUserServiceViaMonitorAsync(userName, timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                throw new InvalidOperationException($"User service {userName} not stable after wait");
            }

            var url = $"http://localhost:{userPort}/chains/{userChainId}/applications/{userAppId}";

            var graphql = @"
                    query GetTransactions {
                        transactions {
                            txId
                            txType
                            amount
                            timestamp
                            relatedId
                            status
                        }
                    }";

            var payload = new { query = graphql };
            var json = JsonSerializer.Serialize(payload, JsonOptions.Write);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var resp = await _httpClient.PostAsync(url, content, cts.Token);

            var text = await resp.Content.ReadAsStringAsync();
            resp.EnsureSuccessStatusCode();

            Console.WriteLine($"[USER-XFIGHTER] GetTransactions success: user={userName}, chain={userChainId}");
            Console.WriteLine($"[LINERA-CLI-OUTPUT] GetTransactions respone: \n{text}");
            return text;
        }
        #endregion

        #region Tournament Betting System
        public async Task<string> SettleMatchAsync(string matchId, string winner)
        {
            if (string.IsNullOrWhiteSpace(winner)) throw new ArgumentNullException(nameof(winner));

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";
            var graphql = $@"mutation {{
                settleMatch(matchId: ""{matchId}"", winner: ""{winner}"")
            }}";

            var payload = new { query = graphql };
            var body = JsonSerializer.Serialize(payload, JsonOptions.Write);

            using var resp = await _orchestrator.PostSingleWithServiceWaitAsync(
                url,
                () => new StringContent(body, Encoding.UTF8, "application/json"),
                waitSeconds: 8,
                postTimeoutSeconds: 20);

            var text = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[TOURNAMENT] [BETTING] Settled {matchId}, winner {winner}");
            resp.EnsureSuccessStatusCode();
            return text;
        }

        public async Task<string> SetMatchMetadataAsync(string matchId, long? bettingDeadlineUnix = null, long? matchStartUnix = null, string? status = null)
        {
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var deadline = bettingDeadlineUnix ?? (nowUnix + 60);
            Console.WriteLine($"[DEBUG] SetMatchMetadata nowUnix={nowUnix}, deadline={deadline} (diff={deadline - nowUnix}s)");

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";

            var statusField = string.IsNullOrWhiteSpace(status) ? "" : $", status: \"{status}\"";
            var startUnixField = matchStartUnix.HasValue ? $", matchStartUnix: {matchStartUnix.Value}" : "";

            var graphql = $"mutation {{ setMatchMetadata(matchId: \"{matchId}\", bettingDeadlineUnix: {deadline}{startUnixField}{statusField}) }}";

            var payload = new { query = graphql };
            var body = JsonSerializer.Serialize(payload, JsonOptions.Write);

            using var resp = await _orchestrator.PostSingleWithServiceWaitAsync(
                url,
                () => new StringContent(body, Encoding.UTF8, "application/json"),
                waitSeconds: 8,
                postTimeoutSeconds: 20);

            var text = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[DEBUG] Local clock after chain response = {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
            resp.EnsureSuccessStatusCode();
            return text;
        }

        /// Tournament Get Bet Information
        public async Task<string> GetBetsAsync(string matchId)
        {
            if (string.IsNullOrWhiteSpace(matchId)) throw new ArgumentNullException(nameof(matchId));

            while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                Console.WriteLine("[TOURNAMENT] Waiting for Linera service to stabilize (GetBets)...");
                await Task.Delay(300);
            }

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";
            var graphql = $@"query {{
                getBets(matchId: ""{matchId}"") {{
                    betId
                    bettor
                    predicted
                    amount
                    userChain
                }}
            }}";
            var payload = new { query = graphql };
            var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var resp = await _httpClient.PostAsync(url, content, cts.Token);
            var text = await resp.Content.ReadAsStringAsync();

            Console.WriteLine($"[TOURNAMENT] GetBets({matchId}) → OK, Linera respone= {text}");
            resp.EnsureSuccessStatusCode();
            return text;
        }

        #region Token Transfer Methods
        public async Task<string> TransferTokensToUserAsync(string userName, ulong amount)
        {
            if (string.IsNullOrEmpty(_config.UserChainPath))
                throw new InvalidOperationException("UserChainPath must be configured before creating LineraCliRunner.");

            var userChainId = _userService.GetUserChainIdFromLocal(userName);
            var userDir = Path.Combine(_config.UserChainPath, userName);
            var walletPath = Path.Combine(userDir, "wallet.json");
            var userPublicKey = UserService.GetPublicKeyFromWallet(walletPath);

            while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                Console.WriteLine("[BETTING] Waiting for Linera service to stabilize...");
                await Task.Delay(300);
            }

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.FungibleAppId}";

            var graphql = $@"mutation {{
                transfer(
                    owner: ""{_config.PublisherOwner}"",
                    amount: ""{amount}."",
                    targetAccount: {{
                        chainId: ""{userChainId}"",
                        owner: ""{userPublicKey}""
                    }}
                )
            }}";

            var payload = new { query = graphql };
            var body = JsonSerializer.Serialize(payload, JsonOptions.Write);

            using var resp = await _orchestrator.PostSingleWithServiceWaitAsync(
                url,
                () => new StringContent(body, Encoding.UTF8, "application/json"),
                waitSeconds: 8,
                postTimeoutSeconds: 20);


            var text = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[BETTING] Transferred {amount} tokens to user {userName}: {text}");
            resp.EnsureSuccessStatusCode();
            return text;
        }

        /// Check User Balance
        public async Task<string> GetUserBalanceAsync(string userName)
        {
            if (string.IsNullOrEmpty(_config.UserChainPath))
                throw new InvalidOperationException("UserChainPath must be configured before creating LineraCliRunner.");

            var userChainId = _userService.GetUserChainIdFromLocal(userName);
            var userPort = _userService.GetUserPortFromStorage(userName);
            var userDir = Path.Combine(_config.UserChainPath, userName);
            var walletPath = Path.Combine(userDir, "wallet.json");
            var userPublicKey = UserService.GetPublicKeyFromWallet(walletPath);

            if (!await _userService.WaitForUserServiceViaMonitorAsync(userName, timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                throw new InvalidOperationException($"User service {userName} not stable after wait");
            }

            var url = $"http://localhost:{userPort}/chains/{userChainId}/applications/{_config.FungibleAppId}";

            var graphql = @"query {
                accounts {
                    entries {
                        key
                        value
                    }
                }
            }";

            var payload = new { query = graphql };
            var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(url, content);
            var text = await resp.Content.ReadAsStringAsync();

            Console.WriteLine($"[BETTING] User {userName} accounts query: {text}");

            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Accounts query failed: {text}");
            }

            // Parse JSON để lấy balance của user
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("accounts", out var accounts) &&
                    accounts.TryGetProperty("entries", out var entries))
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        if (entry.TryGetProperty("key", out var key) &&
                            key.GetString() == userPublicKey &&
                            entry.TryGetProperty("value", out var value))
                        {
                            var balance = value.GetString();
                            return $"{{\"data\": {{\"balance\": \"{balance}\"}}}}";
                        }
                    }
                    // Không tìm thấy user trong accounts
                    return $"{{\"data\": {{\"balance\": \"0.\"}}}}";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BETTING] Error parsing accounts response: {ex.Message}");
            }

            return text;
        }

        /// Check Admin balance
        public async Task<string> GetPublisherBalanceAsync()
        {
            while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                Console.WriteLine("[BETTING] Waiting for Linera service to stabilize...");
                await Task.Delay(300);
            }

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.FungibleAppId}";

            var graphql = @"query {
                accounts {
                    entries {
                        key
                        value
                    }
                }
            }";

            var payload = new { query = graphql };
            var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(url, content);
            var text = await resp.Content.ReadAsStringAsync();

            Console.WriteLine($"[BETTING] Publisher accounts query: {text}");

            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Publisher accounts query failed: {text}");
            }

            // Parse JSON để lấy balance của publisher
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("accounts", out var accounts) &&
                    accounts.TryGetProperty("entries", out var entries))
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        if (entry.TryGetProperty("key", out var key) &&
                            key.GetString() == _config.PublisherOwner &&
                            entry.TryGetProperty("value", out var value))
                        {
                            var balance = value.GetString();
                            return $"{{\"data\": {{\"balance\": \"{balance}\"}}}}";
                        }
                    }
                    // Không tìm thấy publisher trong accounts
                    return $"{{\"data\": {{\"balance\": \"0.\"}}}}";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BETTING] Error parsing publisher accounts response: {ex.Message}");
            }

            return text;
        }

        // phương thức để lấy transactions của user trên fungible 
        public async Task<string> GetUserTransactionsAsync(string userName)
        {

            var userChainId = _userService.GetUserChainIdFromLocal(userName);
            var userPort = _userService.GetUserPortFromStorage(userName);

            if (!await _userService.WaitForUserServiceViaMonitorAsync(userName, timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                throw new InvalidOperationException($"User service {userName} not stable after wait");
            }

            var url = $"http://localhost:{userPort}/chains/{userChainId}/applications/{_config.FungibleAppId}";

            var graphql = @"query {
                transactions {
                    txId
                    from
                    to
                    amount
                    timestamp
                    txType
                }
            }";

            var payload = new { query = graphql };
            var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(url, content);
            var text = await resp.Content.ReadAsStringAsync();

            Console.WriteLine($"[BETTING] User {userName} transactions: {text}");
            resp.EnsureSuccessStatusCode();
            return text;
        }

        // Thêm phương thức để lấy user transactions từ publisher chain
        public async Task<string> GetUserTransactionsFromPublisherAsync(string userName)
        {
            if (string.IsNullOrEmpty(_config.UserChainPath))
                throw new InvalidOperationException("UserChainPath is not configured.");

            var userDir = Path.Combine(_config.UserChainPath, userName);
            var walletPath = Path.Combine(userDir, "wallet.json");
            var userPublicKey = UserService.GetPublicKeyFromWallet(walletPath);

            while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                Console.WriteLine("[BETTING] Waiting for Linera service to stabilize...");
                await Task.Delay(300);
            }

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.FungibleAppId}";

            // Query userTransactions như trong ví dụ
            var graphql = $@"query {{
                userTransactions(user: ""{userPublicKey}"") {{
                    txId
                    from
                    to
                    amount
                    timestamp
                    txType
                }}
            }}";

            var payload = new { query = graphql };
            var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(url, content);
            var text = await resp.Content.ReadAsStringAsync();

            Console.WriteLine($"[BETTING] User {userName} transactions from publisher: {text}");
            resp.EnsureSuccessStatusCode();
            return text;
        }
        #endregion

        //public async Task<string> GetMatchMetadataAsync(string matchId)
        //{
        //    if (string.IsNullOrWhiteSpace(matchId)) throw new ArgumentNullException(nameof(matchId));

        //    while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
        //    {
        //        Console.WriteLine("[TOURNAMENT] Waiting for Linera service to stabilize (GetMatchMetadata)...");
        //        await Task.Delay(300);
        //    }

        //    var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";
        //    var graphql = $"query {{ getMatchMetadata(matchId: \"{matchId}\") {{ matchId bettingDeadlineUnix matchStartUnix status oddsA oddsB }} }}";

        //    var payload = new { query = graphql };
        //    var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json");

        //    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        //    var resp = await _httpClient.PostAsync(url, content, cts.Token);
        //    var text = await resp.Content.ReadAsStringAsync();

        //    resp.EnsureSuccessStatusCode();
        //    return text;
        //}

        //public async Task<string> GetUserBettingInfoAsync(string userName)
        //{
        //    var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";

        //    var graphql = $@"query {{
        //        getUserBettingTotal(userName: ""{userName}"")
        //        getUserBets(userName: ""{userName}"") {{
        //            betId
        //            bettor
        //            predicted
        //            amount
        //            userChain
        //        }}
        //    }}";

        //    var payload = new { query = graphql };
        //    var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write),
        //        Encoding.UTF8, "application/json");

        //    var resp = await _httpClient.PostAsync(url, content);
        //    var text = await resp.Content.ReadAsStringAsync();

        //    Console.WriteLine($"[BETTING-INFO] User {userName} betting info: {text}");
        //    return text;
        //}

        //public async Task<decimal> GetUserTotalBetAmountAsync(string userName)
        //{
        //    var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";

        //    var graphql = $@"query {{
        //        getUserBettingTotal(userName: ""{userName}"")
        //    }}";

        //    var payload = new { query = graphql };
        //    var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write),
        //        Encoding.UTF8, "application/json");

        //    var resp = await _httpClient.PostAsync(url, content);
        //    var json = await resp.Content.ReadAsStringAsync();

        //    // Parse JSON để lấy total amount
        //    using var doc = JsonDocument.Parse(json);
        //    var total = doc.RootElement
        //        .GetProperty("data")
        //        .GetProperty("getUserBettingTotal")
        //        .GetUInt64();

        //    return total;
        //}
        #endregion
    }
}