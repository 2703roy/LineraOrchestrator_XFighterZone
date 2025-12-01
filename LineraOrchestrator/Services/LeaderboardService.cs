// LeaderboardService.cs
using System.Text;
using System.Text.Json;
using LineraOrchestrator.Models;

namespace LineraOrchestrator.Services
{
    public class LeaderboardService
    {
        private readonly LineraConfig _config;
        private readonly HttpClient _httpClient;
        private readonly LineraOrchestratorService _orchestrator;
        private readonly UserService _userService;

        public LeaderboardService(LineraConfig config, HttpClient httpClient,
            LineraOrchestratorService orchestrator, UserService userService)
        {
            _config = config;
            _httpClient = httpClient;
            _orchestrator = orchestrator;
            _userService = userService;
        }

        #region Helper wait leaderboard confirm after submit
        public async Task<bool> WaitForLeaderboardUpdateAsync(string player1, string player2, int timeoutMs = 8000, int pollIntervalMs = 1000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    var json = await GetLeaderboardDataAsync();
                    Console.WriteLine($"[DEBUG] Polling & Searching current leaderboard…\n elapsed {sw.ElapsedMilliseconds} ms");
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("currentSeasonLeaderboard", out var lb) &&
                        lb.ValueKind == JsonValueKind.Array)
                    {
                        bool p1Found = false, p2Found = false;
                        foreach (var e in lb.EnumerateArray())
                        {
                            var uid = e.GetProperty("userName").GetString();
                            if (uid == player1) p1Found = true;
                            if (uid == player2) p2Found = true;
                        }
                        if (p1Found && p2Found)
                        {
                            Console.WriteLine($"[DEBUG] Both players found on leaderboard: p1={player1}, p2={player2}");
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] polling failed: {ex.Message}");
                }

                await Task.Delay(pollIntervalMs);
            }
            Console.WriteLine("[DEBUG] Timeout waiting for leaderboard update");
            return false;
        }
        #endregion

        #region Get data leaderboard Global / Season

        public async Task<string> GetLeaderboardDataAsync()
        {
            try
            {
                // Đảm bảo service ổn định trước khi gọi API
                while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
                {
                    Console.WriteLine("[LEADERBOARD] Waiting for Linera service to stabilize...");
                    await Task.Delay(500);
                }

                var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.LeaderboardAppId}";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                var graphql = @"
                    query {
                        currentSeasonLeaderboard {
                            userName
                            score
                            totalMatches
                            totalWins
                            totalLosses
                        }
                    }";

                var payload = new { query = graphql };
                var json = JsonSerializer.Serialize(payload, JsonOptions.Write);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var resp = await _httpClient.PostAsync(url, content, cts.Token);

                var text = await resp.Content.ReadAsStringAsync();
                resp.EnsureSuccessStatusCode();
                Console.WriteLine($"[LEADERBOARD] GetLeaderboardData: respone={text}");
                return text;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LEADERBOARD] error {ex.Message}");
                throw;
            }
        }

        /// Get Top 8 Leaderboard current season for Tournament 
        public async Task<string> GetTournamentTop8Async()
        {
            while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                Console.WriteLine("[LEADERBOARD] Waiting for Linera service to stabilize...");
                await Task.Delay(500);
            }

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.LeaderboardAppId}";
            var graphql = @"
                query {
                    tournamentTop8 {
                        userName
                        score
                        totalMatches
                        totalWins
                        totalLosses
                    }
                }";

            var payload = new { query = graphql };
            var json = JsonSerializer.Serialize(payload, JsonOptions.Write);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var resp = await _httpClient.PostAsync(url, content, cts.Token);

            return await resp.Content.ReadAsStringAsync();
        }

        /// Check info leaderboard season hiện tại
        public async Task<string> GetCurrentSeasonInfoAsync()
        {
            while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                Console.WriteLine("[LEADERBOARD] Waiting for Linera service to stabilize...");
                await Task.Delay(500);
            }

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.LeaderboardAppId}";
            var graphql = @"
                query {
                    currentSeasonInfo {
                        number
                        name
                        startTime
                        endTime
                        durationDays
                        status
                        totalPlayers
                    }
                }";

            var payload = new { query = graphql };
            var json = JsonSerializer.Serialize(payload, JsonOptions.Write);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var resp = await _httpClient.PostAsync(url, content, cts.Token);

            var text = await resp.Content.ReadAsStringAsync();
            resp.EnsureSuccessStatusCode();

            Console.WriteLine($"[LEADERBOARD] GetCurrentSeasonInfoAsync success: respone={text}");
            return text;
        }

        /// Check info leaderboard season cũ
        public async Task<string> GetSeasonInfoAsync(ulong seasonNumber)
        {
            while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
                await Task.Delay(500);

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.LeaderboardAppId}";

            var graphql = $@"
                query {{
                    seasonInfo(seasonNumber: {seasonNumber}) {{
                        number
                        name
                        startTime
                        endTime
                        durationDays
                        status
                        totalPlayers
                    }}
                }}";

            var payload = new { query = graphql };
            var json = JsonSerializer.Serialize(payload, JsonOptions.Write);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var resp = await _httpClient.PostAsync(url, content, cts.Token);

            var text = await resp.Content.ReadAsStringAsync();
            resp.EnsureSuccessStatusCode();

            Console.WriteLine($"[LEADERBOARD] GetSeasonInfo success: season={seasonNumber}");
            Console.WriteLine($"[LEADERBOARD] Linera respone ={text}");
            return text;
        }

        /// Check data leaderboard season cũ
        public async Task<string> GetSeasonLeaderboardAsync(ulong seasonNumber, int? limit = null)
        {
            while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
                await Task.Delay(500);

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.LeaderboardAppId}";

            var limitParam = limit.HasValue ? $", limit: {limit.Value}" : "";

            var graphql = $@"
                query {{
                    seasonLeaderboard(seasonNumber: {seasonNumber}{limitParam}) {{
                        userName
                        score
                        totalMatches
                        totalWins
                        totalLosses
                    }}
                }}";

            var payload = new { query = graphql };
            var json = JsonSerializer.Serialize(payload, JsonOptions.Write);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var resp = await _httpClient.PostAsync(url, content, cts.Token);

            var text = await resp.Content.ReadAsStringAsync();
            resp.EnsureSuccessStatusCode();

            Console.WriteLine($"[LEADERBOARD] GetSeasonLeaderboard success: season={seasonNumber}, limit={limit}");
            Console.WriteLine($"[LEADERBOARD] Linera respone ={text}");
            return text;
        }

        /// Start Leaderboard Season 
        public async Task<string> StartSeasonAsync(string? name = null)
        {
            Console.WriteLine($"=== START NEW LEADERBOARD SEASON: {name} ===");
            while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                Console.WriteLine("[LEADERBOARD] Waiting for Linera service to stabilize...");
                await Task.Delay(500);
            }

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.LeaderboardAppId}";
            var graphql = @"
                mutation ($name: String!) {
                    startSeason(name: $name)
                }";

            var payload = new
            {
                query = graphql,
                variables = new { name }
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions.Write);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var resp = await _httpClient.PostAsync(url, content, cts.Token);

            var text = await resp.Content.ReadAsStringAsync();
            resp.EnsureSuccessStatusCode();

            Console.WriteLine($"[LEADERBOARD] StartSeason success: respone={text}");
            return text;
        }

        /// End Leaderboard Season 
        public async Task<string> EndSeasonAsync()
        {
            Console.WriteLine("=== END CURRENT LEADERBOARD SEASON ===");
            //Make sure the service is stable before querying
            while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                Console.WriteLine("[LINERA-ORCH] Waiting for Linera service to stabilize...");
                await Task.Delay(500);
            }

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.LeaderboardAppId}";
            var graphql = @"
                mutation {
                    endSeason
                }";

            var payload = new { query = graphql };
            var json = JsonSerializer.Serialize(payload, JsonOptions.Write);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var resp = await _httpClient.PostAsync(url, content, cts.Token);

            var text = await resp.Content.ReadAsStringAsync();
            resp.EnsureSuccessStatusCode();

            Console.WriteLine($"[LEADERBOARD] EndSeasonAsync success: respone={text}");
            return text;
        }
        /// Check info user
        public async Task<string> GetUserGlobalStatsAsync(string userName)
        {
            try
            {
                //Make sure the service is stable before querying
                while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
                {
                    Console.WriteLine("[LINERA-ORCH] Waiting for Linera service to stabilize...");
                    await Task.Delay(500);
                }

                var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.LeaderboardAppId}";

                var graphql = $@"
                    query {{
                        userGlobalStats(userName: ""{userName}"") {{
                            userName
                            totalMatches
                            totalWins
                            totalLosses
                            score
                            lastPlay
                        }}
                    }}";

                var payload = new { query = graphql };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var resp = await _httpClient.PostAsync(url, content, cts.Token);

                var text = await resp.Content.ReadAsStringAsync();
                resp.EnsureSuccessStatusCode();

                Console.WriteLine($"[LEADERBOARD] GetUserGlobalStats Linera respone ={text}");
                return text;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LEADERBOARD] GetUserGlobalStatsAsync error for {userName}: {ex.Message}");
                throw;
            }
        }

        /// Check lịch sử đấu
        public async Task<string> GetMatchHistoryByUserAsync(string userName)
        {
            try
            {
                //Make sure the service is stable before querying
                while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
                {
                    Console.WriteLine("[LINERA-ORCH] Waiting for Linera service to stabilize...");
                    await Task.Delay(500);
                }

                // 1. Lấy child apps
                var childQuery = new { query = "query { allChildApps { chainId appId } }" };
                var factoryUrl = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.XFighterAppId}";

                using var httpClient = new HttpClient();
                var childResp = await httpClient.PostAsync(factoryUrl, new StringContent(JsonSerializer.Serialize(childQuery), Encoding.UTF8, "application/json"));
                var childText = await childResp.Content.ReadAsStringAsync();

                var childApps = new List<ChildAppInfo>();
                using (var childDoc = JsonDocument.Parse(childText))
                {
                    var childArr = childDoc.RootElement.GetProperty("data").GetProperty("allChildApps");
                    foreach (var el in childArr.EnumerateArray())
                    {
                        childApps.Add(new ChildAppInfo
                        {
                            ChainId = el.GetProperty("chainId").GetString() ?? "",
                            AppId = el.GetProperty("appId").GetString() ?? ""
                        });
                    }
                }

                // 2. Query từng child app
                var allMatches = new List<object>();
                foreach (var childApp in childApps)
                {
                    try
                    {
                        // QUERY CHỈ có chainId và matchResult
                        var graphql = $@"query {{ 
                            matchHistoryByUser(username: ""{userName}"") {{ 
                                chainId 
                                matchResult {{ 
                                    matchId 
                                    player1Username 
                                    player2Username 
                                    player1Userchain
                                    player2Userchain
                                    player1Heroname
                                    player2Heroname
                                    winnerUsername 
                                    loserUsername 
                                    durationSeconds 
                                    timestamp 
                                    mapName 
                                    matchType 
                                    afk 
                                }} 
                            }} 
                        }}";
                        var matchQuery = new { query = graphql };

                        var childUrl = $"http://localhost:8080/chains/{childApp.ChainId}/applications/{childApp.AppId}";
                        var matchResp = await httpClient.PostAsync(childUrl, new StringContent(JsonSerializer.Serialize(matchQuery), Encoding.UTF8, "application/json"));
                        var matchText = await matchResp.Content.ReadAsStringAsync();

                        using var matchDoc = JsonDocument.Parse(matchText);
                        if (matchDoc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("matchHistoryByUser", out var matchHistory))
                        {
                            foreach (var matchWithChain in matchHistory.EnumerateArray())
                            {
                                var matchObj = new Dictionary<string, object>();

                                // CHỈ lấy chainId
                                if (matchWithChain.TryGetProperty("chainId", out var chainId))
                                    matchObj["chainId"] = chainId.GetString() ?? "";

                                // Lấy matchResult
                                if (matchWithChain.TryGetProperty("matchResult", out var matchResult))
                                {
                                    var resultObj = new Dictionary<string, object>();
                                    foreach (var prop in matchResult.EnumerateObject())
                                    {
                                        resultObj[prop.Name] = prop.Value.ValueKind switch
                                        {
                                            JsonValueKind.String => prop.Value.GetString() ?? "",
                                            JsonValueKind.Number => prop.Value.GetDouble(),
                                            JsonValueKind.True => true,
                                            JsonValueKind.False => false,
                                            _ => prop.Value.ToString() ?? ""
                                        };
                                    }
                                    matchObj["matchResult"] = resultObj;
                                }
                                allMatches.Add(matchObj);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WARN] Failed to query child app {childApp.AppId}: {ex.Message}");
                    }
                }

                // 3. Trả về
                return JsonSerializer.Serialize(new { success = true, data = new { matchHistoryByUser = allMatches } }, JsonOptions.Write);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions.Write);
            }
        }

        /// Check lịch sử đấu theo userChain
        public async Task<string> GetMatchHistoryByUserChainAsync(string userChain)
        {
            //Make sure the service is stable before querying
            while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                Console.WriteLine("[LINERA-ORCH] Waiting for Linera service to stabilize...");
                await Task.Delay(500);
            }
            // Tìm userName từ userChain
            var userName = _userService.GetUserNameByUserChain(userChain);

            if (string.IsNullOrEmpty(userName))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"No user found for chain: {userChain}"
                }, JsonOptions.Write);
            }

            // Gọi phương thức hiện có
            return await GetMatchHistoryByUserAsync(userName);
        }

        /// Check lịch sử đấu theo chainId của match
        public async Task<string> GetMatchHistoryByChainIdAsync(string chainId)
        {
            try
            {
                // Make sure the service is stable before querying
                while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
                {
                    Console.WriteLine("[LINERA-ORCH] Waiting for Linera service to stabilize...");
                    await Task.Delay(500);
                }

                // 1. Lấy child apps
                var childQuery = new { query = "query { allChildApps { chainId appId } }" };
                var factoryUrl = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.XFighterAppId}";

                using var httpClient = new HttpClient();
                var childResp = await httpClient.PostAsync(factoryUrl, new StringContent(JsonSerializer.Serialize(childQuery), Encoding.UTF8, "application/json"));
                var childText = await childResp.Content.ReadAsStringAsync();

                var childApps = new List<ChildAppInfo>();
                using (var childDoc = JsonDocument.Parse(childText))
                {
                    var childArr = childDoc.RootElement.GetProperty("data").GetProperty("allChildApps");
                    foreach (var el in childArr.EnumerateArray())
                    {
                        childApps.Add(new ChildAppInfo
                        {
                            ChainId = el.GetProperty("chainId").GetString() ?? "",
                            AppId = el.GetProperty("appId").GetString() ?? ""
                        });
                    }
                }

                // 2. Query từng child app để tìm match theo chainId
                var foundMatch = new Dictionary<string, object>();
                foreach (var childApp in childApps)
                {
                    try
                    {
                        // QUERY tất cả match results từ child app
                        var graphql = @"query { 
                            allMatchResults { 
                                matchId 
                                player1Username 
                                player2Username 
                                player1Userchain
                                player2Userchain
                                player1Heroname
                                player2Heroname
                                winnerUsername 
                                loserUsername 
                                durationSeconds 
                                timestamp 
                                mapName 
                                matchType 
                                afk 
                            } 
                        }";
                        var matchQuery = new { query = graphql };

                        var childUrl = $"http://localhost:8080/chains/{childApp.ChainId}/applications/{childApp.AppId}";
                        var matchResp = await httpClient.PostAsync(childUrl, new StringContent(JsonSerializer.Serialize(matchQuery), Encoding.UTF8, "application/json"));
                        var matchText = await matchResp.Content.ReadAsStringAsync();

                        using var matchDoc = JsonDocument.Parse(matchText);
                        if (matchDoc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("allMatchResults", out var allMatches))
                        {
                            foreach (var match in allMatches.EnumerateArray())
                            {
                                var matchId = match.GetProperty("matchId").GetString();
                                // So sánh matchId với chainId cần tìm
                                if (matchId == chainId)
                                {
                                    // Tạo match result
                                    var resultObj = new Dictionary<string, object>();
                                    foreach (var prop in match.EnumerateObject())
                                    {
                                        resultObj[prop.Name] = prop.Value.ValueKind switch
                                        {
                                            JsonValueKind.String => prop.Value.GetString() ?? "",
                                            JsonValueKind.Number => prop.Value.GetDouble(),
                                            JsonValueKind.True => true,
                                            JsonValueKind.False => false,
                                            _ => prop.Value.ToString() ?? ""
                                        };
                                    }

                                    foundMatch["chainId"] = childApp.ChainId ?? string.Empty;
                                    foundMatch["matchResult"] = resultObj;
                                    break;
                                }
                            }
                        }

                        if (foundMatch.Count > 0) break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WARN] Failed to query child app {childApp.AppId}: {ex.Message}");
                    }
                }

                // 3. Trả về
                if (foundMatch.Count > 0)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        data = new { matchHistoryByChain = foundMatch }
                    }, JsonOptions.Write);
                }
                else
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"No match found for chainId: {chainId}"
                    }, JsonOptions.Write);
                }
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = ex.Message
                }, JsonOptions.Write);
            }
        }
        #endregion
    }
}