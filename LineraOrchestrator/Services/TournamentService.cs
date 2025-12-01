// TournamentService.cs
using System;
using System.Text;
using System.Text.Json;
using LineraOrchestrator.Models;
namespace LineraOrchestrator.Services
{
    public class TournamentService
    {
        private readonly LineraConfig _config;
        private readonly HttpClient _httpClient;
        private readonly LineraOrchestratorService _orchestrator;
        private readonly LeaderboardService _leaderboardSvc;
        public TournamentService(LineraConfig config, HttpClient httpClient, LineraOrchestratorService orchestrator, LeaderboardService leaderboardSvc)
        {
            _config = config;
            _httpClient = httpClient;
            _orchestrator = orchestrator;
            _leaderboardSvc = leaderboardSvc;
        }

        #region Tournament Meta Data
        // Lấy data leaderboard hiện tại
        public async Task<string> GetTournamentLeaderboardDataAsync()
        {
            while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                Console.WriteLine("[TOURNAMENT] Waiting for Linera service to stabilize before get data...");
                await Task.Delay(500);
            }

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";
            var graphql = @"
                query {
                    tournamentLeaderboard {
                        username
                        score
                    }
                }";

            var payload = new { query = graphql, variables = new { } };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(url, content);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync();
        }

        
        public async Task<string> GetCurrentTournamentInfoAsync()
        {
            while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 300, stableMs: 500))
            {
                Console.WriteLine("[TOURNAMENT] Waiting for Linera service to stabilize before get tournament info...");
                await Task.Delay(500);
            }

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";
            var graphql = @"
                query {
                    currentTournamentInfo {
                        number
                        name
                        startTime
                        endTime
                        durationDays
                        status
                        champion
                        runnerUp
                    }
                }";

            var payload = new { query = graphql, variables = new { } };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(url, content);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync();
        }

        // Lấy thông tin tournament leaderboard cũ
        public async Task<string> GetSeasonTournamentInfoAsync(ulong tournamentNumber)
        {
            while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 300, stableMs: 500))
            {
                Console.WriteLine("[TOURNAMENT] Waiting for Linera service to stabilize...");
                await Task.Delay(500);
            }

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";
            var graphql = $@"
                query {{
                    tournamentInfo(tournamentNumber: {tournamentNumber}) {{
                        number
                        name
                        startTime
                        endTime
                        durationDays
                        status
                        champion
                        runnerUp
                    }}
                }}";

            var payload = new { query = graphql };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var resp = await _httpClient.PostAsync(url, content, cts.Token);

            var text = await resp.Content.ReadAsStringAsync();
            resp.EnsureSuccessStatusCode();

            Console.WriteLine($"[TOURNAMENT] GetTournamentInfo success: tournament={tournamentNumber}");
            return text;
        }

        // Lấy leaderboard của tournament cũ
        public async Task<string> GetSeasonTournamentLeaderboardAsync(ulong tournamentNumber)
        {
            while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 300, stableMs: 500))
            {
                Console.WriteLine("[TOURNAMENT] Waiting for Linera service to stabilize...");
                await Task.Delay(500);
            }

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";
            var graphql = $@"
                query {{
                    pastTournamentLeaderboard(tournamentNumber: {tournamentNumber}) {{
                        username
                        score
                    }}
                }}";

            var payload = new { query = graphql };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var resp = await _httpClient.PostAsync(url, content, cts.Token);

            var text = await resp.Content.ReadAsStringAsync();
            resp.EnsureSuccessStatusCode();

            Console.WriteLine($"[TOURNAMENT] Linera GetTournamentInfo success: tournament={tournamentNumber}");
            return text;
        }
        #endregion

        #region Tournament Management

        public async Task<string> StartTournamentSeasonAsync(string? name = null)
        {
            // 1. Lấy leaderboard trực tiếp và parse top 8 players
            var leaderboardJson = await _leaderboardSvc.GetLeaderboardDataAsync();
            if (string.IsNullOrWhiteSpace(leaderboardJson))
            {
                throw new InvalidOperationException("Leaderboard data is empty");
            }

            var topPlayers = ParseTop8PlayersFromJson(leaderboardJson);

            if (topPlayers.Count < 8)
            {
                throw new InvalidOperationException($"Not enough players for tournament. Need 8, got {topPlayers.Count}");
            }

            var tournamentName = name ?? $"XFighter_Tournament_{DateTime.UtcNow:yyyyMMdd_HHmm}";

            Console.WriteLine($"[TOURNAMENT] Starting tournament season '{tournamentName}' with players: {string.Join(", ", topPlayers)}");

            // 2. Gọi mutation StartTournamentSeason
            Console.WriteLine($"[GRAPHQL-CALL] Running mutation: startTournamentSeason");
            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";
            var graphql = $@"
                mutation {{
                    startTournamentSeason(name: ""{tournamentName}"")
                }}";

            var payload = new { query = graphql };
            var jsonBody = JsonSerializer.Serialize(payload, JsonOptions.Write);

            var resp = await _orchestrator.PostSingleWithServiceWaitAsync(
             url,
             () => new StringContent(jsonBody, Encoding.UTF8, "application/json"),
             waitSeconds: 8,
             postTimeoutSeconds: 30);

            var responseText = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[GRAPHQL-STDOUT]\n{responseText}");
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[GRAPHQL-STDERR]\nHTTP Error: {resp.StatusCode}");
            }          
            // Parse opId
            string? opId = null;
            using (var doc = JsonDocument.Parse(responseText))
            {
                if (doc.RootElement.TryGetProperty("data", out var dataEl))
                {
                    if (dataEl.ValueKind == JsonValueKind.String)
                        opId = dataEl.GetString() ?? "";
                    else if (dataEl.TryGetProperty("startTournamentSeason", out var opEl))
                        opId = opEl.GetString() ?? "";
                }
            }

            if (string.IsNullOrWhiteSpace(opId))
                throw new InvalidOperationException($"Linera did not return opId. Response: {responseText}");

            // FINAL OUTPUT
            Console.WriteLine($"[GRAPHQL-OUTPUT] Linera StartTournamentSeason response:");
            Console.WriteLine($"[SUCCESS] TournamentName: {tournamentName}");
            Console.WriteLine($"[SUCCESS] OpId: {opId}");
            // 3. Set participants
            await SetParticipantsAsync(topPlayers);
            //await InitTournamentMatchesOnChain();

            return opId;
        }
        private static List<string> ParseTop8PlayersFromJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");
            var leaderboard = data.GetProperty("currentSeasonLeaderboard");

            var topPlayers = leaderboard.EnumerateArray()
                .Select(player => player.GetProperty("userName").GetString() ?? "")
                .Where(name => !string.IsNullOrEmpty(name))
                .Take(8)
                .ToList();

            Console.WriteLine($"[TOURNAMENT] Top 8 players: {string.Join(", ", topPlayers)}");

            if (topPlayers.Count < 8)
                throw new InvalidOperationException($"Need 8 players, got {topPlayers.Count}");

            return topPlayers;
        }

        public async Task<string> SetParticipantsAsync(List<string> participants)
        {
            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";

            // Sửa GraphQL mutation - cần escape JSON đúng cách
            var participantsArray = "[" + string.Join(", ", participants.Select(p => $"\"{p}\"")) + "]";
            var graphql = $@"
                mutation {{
                    setParticipants(participants: {participantsArray})
                }}";

            var payload = new { query = graphql };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var resp = await _orchestrator.PostSingleWithServiceWaitAsync(
                url,
                () => new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                waitSeconds: 8,
                postTimeoutSeconds: 30);

            var responseText = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[GRAPHQL-STDOUT]\n{responseText}");
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Failed to set participants: {resp.StatusCode} - {responseText}");

            // Parse opId từ response
            string? opId = null;
            using (var doc = JsonDocument.Parse(responseText))
            {
                if (doc.RootElement.TryGetProperty("data", out var dataEl))
                {
                    if (dataEl.ValueKind == JsonValueKind.String)
                        opId = dataEl.GetString() ?? "";
                    else if (dataEl.TryGetProperty("setParticipants", out var opEl))
                        opId = opEl.GetString() ?? "";
                }
            }

            // FINAL OUTPUT
            Console.WriteLine($"[GRAPHQL-OUTPUT] Linera SetParticipants response:");
            Console.WriteLine($"[SUCCESS] ParticipantsCount: {participants.Count}");
            Console.WriteLine($"[SUCCESS] OpId: {opId}");
            return opId ?? "unknown";
        }

        public async Task<List<string>> GetTournamentParticipantsAsync()
        {
            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";
            var graphql = @"query { participants }";

            var payload = new { query = graphql };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(url, content);
            var text = await resp.Content.ReadAsStringAsync();

            // Parse response để lấy danh sách participants
            using var doc = JsonDocument.Parse(text);
            var participants = doc.RootElement
                .GetProperty("data")
                .GetProperty("participants")
                .EnumerateArray()
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrEmpty(x))
                .ToList();

            return participants!;
        }
        #endregion

        #region Tournament Bracket Management
        public async Task<List<TournamentMatchInput>> GetTournamentBracketAsync()
        {
            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";
            var graphql = @"query { bracket { matchId player1 player2 winner round status } }";

            var payload = new { query = graphql };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(url, content);
            var text = await resp.Content.ReadAsStringAsync();

            // Parse response
            using var doc = JsonDocument.Parse(text);
            var bracket = doc.RootElement
                .GetProperty("data")
                .GetProperty("bracket")
                .EnumerateArray()
                .Select(x => JsonSerializer.Deserialize<TournamentMatchInput>(x.GetRawText(), JsonOptions.Read))
                .Where(x => x != null)
                .ToList();

            return bracket!;
        }

        public async Task<bool> InitTournamentMatchesOnChain()
        {
            // 1. Lấy participants từ onchain (đã được shuffle)
            var participants = await GetTournamentParticipantsAsync();

            Console.WriteLine($"[DEBUG] [TOURNAMENT] Participants from chain: {string.Join(", ", participants)}");
            if (participants == null || participants.Count < 8)
            {
                Console.WriteLine($"[TOURNAMENT][ERROR] Not enough participants on chain: {participants?.Count ?? 0}/8");
                return false;
            }

            // Tạo bracket từ danh sách đã shuffle
            var matches = new List<TournamentMatchInput>
            {
                new() { MatchId = "QF1", Player1 = participants[0], Player2 = participants[1], Round = "Quarterfinal", Status = "waiting", Winner = null },
                new() { MatchId = "QF2", Player1 = participants[2], Player2 = participants[3], Round = "Quarterfinal", Status = "waiting", Winner = null },
                new() { MatchId = "QF3", Player1 = participants[4], Player2 = participants[5], Round = "Quarterfinal", Status = "waiting", Winner = null },
                new() { MatchId = "QF4", Player1 = participants[6], Player2 = participants[7], Round = "Quarterfinal", Status = "waiting", Winner = null }
            };

            Console.WriteLine($"[TOURNAMENT] Created {matches.Count} matches: {JsonSerializer.Serialize(matches)}");

            // Gọi mutation SetBracket để lưu onchain
            return await SetBracketAsync(matches);
        }

        public async Task<bool> AdvanceTournamentBracketAsync()
        {
            try
            {
                // 1. Lấy bracket hiện tại
                var currentBracket = await GetTournamentBracketAsync();
                Console.WriteLine($"[ADVANCE] Current bracket has {currentBracket.Count} matches");

                // 2. Phân tích vòng hiện tại
                var completedMatches = currentBracket.Where(m => m.Status == "completed").ToList();
                var waitingMatches = currentBracket.Where(m => m.Status == "waiting").ToList();

                // 3. Xác định vòng tiếp theo
                if (currentBracket.Any(m => m.Round == "Quarterfinal") &&
                    completedMatches.Count(m => m.Round == "Quarterfinal") == 4 &&
                    !currentBracket.Any(m => m.Round == "Semifinal"))
                {
                    // Tạo Semifinal từ winners của Quarterfinal
                    var qfWinners = currentBracket
                        .Where(m => m.Round == "Quarterfinal" && m.Winner != null)
                        .Select(m => m.Winner!)
                        .ToList();

                    if (qfWinners.Count != 4)
                    {
                        Console.WriteLine($"[ADVANCE] Not enough QF winners: {qfWinners.Count}/4");
                        return false;
                    }

                    var newMatches = new List<TournamentMatchInput>
                    {
                        new() { MatchId = "SF1", Player1 = qfWinners[0], Player2 = qfWinners[1], Round = "Semifinal", Status = "waiting" },
                        new() { MatchId = "SF2", Player1 = qfWinners[2], Player2 = qfWinners[3], Round = "Semifinal", Status = "waiting" }
                    };

                    // Kết hợp matches cũ và mới
                    var allMatches = currentBracket.Select(m => new TournamentMatchInput
                    {
                        MatchId = m.MatchId,
                        Player1 = m.Player1,
                        Player2 = m.Player2,
                        Winner = m.Winner,
                        Round = m.Round,
                        Status = m.Status
                    }).Concat(newMatches).ToList();

                    return await SetBracketAsync(allMatches);
                }
                else if (currentBracket.Any(m => m.Round == "Semifinal") &&
                         completedMatches.Count(m => m.Round == "Semifinal") == 2 &&
                         !currentBracket.Any(m => m.Round == "Final"))
                {
                    // Tạo Final từ winners của Semifinal
                    var sfWinners = currentBracket
                        .Where(m => m.Round == "Semifinal" && m.Winner != null)
                        .Select(m => m.Winner!)
                        .ToList();

                    if (sfWinners.Count != 2)
                    {
                        Console.WriteLine($"[ADVANCE] Not enough SF winners: {sfWinners.Count}/2");
                        return false;
                    }

                    var finalMatch = new TournamentMatchInput
                    {
                        MatchId = "F1",
                        Player1 = sfWinners[0],
                        Player2 = sfWinners[1],
                        Round = "Final",
                        Status = "waiting"
                    };

                    // Kết hợp matches cũ và final
                    var allMatches = currentBracket.Select(m => new TournamentMatchInput
                    {
                        MatchId = m.MatchId,
                        Player1 = m.Player1,
                        Player2 = m.Player2,
                        Winner = m.Winner,
                        Round = m.Round,
                        Status = m.Status
                    }).ToList();

                    allMatches.Add(finalMatch);

                    return await SetBracketAsync(allMatches);
                }
                else
                {
                    Console.WriteLine($"[ADVANCE] Cannot advance bracket - current state not suitable");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ADVANCE] Error: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> SetBracketAsync(List<TournamentMatchInput> matches)
        {
            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";
            Console.WriteLine($"[GRAPHQL-CALL] Running mutation: setBracket");
            var graphql = @"
                mutation SetBracket($matches: [TournamentMatchInput!]!) {
                    setBracket(matches: $matches)
                }";

            var payload = new
            {
                query = graphql,
                variables = new { matches }
            };

            var jsonBody = JsonSerializer.Serialize(payload, JsonOptions.Write);
            Console.WriteLine($"[SET_BRACKET] Setting {matches.Count} matches");

            var resp = await _orchestrator.PostSingleWithServiceWaitAsync(
                url,
                () => new StringContent(jsonBody, Encoding.UTF8, "application/json"),
                waitSeconds: 8,
                postTimeoutSeconds: 30);

            var responseText = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[GRAPHQL-STDOUT]\n{responseText}");
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[SET_BRACKET] Failed: {resp.StatusCode} - {responseText}");
                return false;
            }

            // Parse response để check lỗi GraphQL
            using var doc = JsonDocument.Parse(responseText);
            if (doc.RootElement.TryGetProperty("errors", out var errors))
            {
                Console.WriteLine($"[SET_BRACKET] GraphQL errors: {errors}");
                return false;
            }

            Console.WriteLine($"[GRAPHQL-OUTPUT] Linera SetBracket response:");
            Console.WriteLine($"[SUCCESS] MatchesCount: {matches.Count}");
            Console.WriteLine($"[SUCCESS] Status: true");
            return true;
        }
        #endregion

        #region Tournament Match Results
        public async Task<string> SubmitTournamentMatchResultAsync(MatchResult matchResult)
        {
            Console.WriteLine($"[GRAPHQL-CALL] Running mutation: recordMatch");
            ArgumentNullException.ThrowIfNull(matchResult);

            Console.WriteLine($"[TOURNAMENT][REQUEST] Submitting tournament match {matchResult.MatchId}...");

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";
            var graphql = $@"
                mutation {{
                    recordMatch(
                        matchId: ""{matchResult.MatchId}"",
                        winner: ""{matchResult.WinnerUsername}"",
                        loser: ""{matchResult.LoserUsername}""
                    )
                }}";

            var payload = new { query = graphql, variables = new { matchResult } };

            var resp = await _orchestrator.PostSingleWithServiceWaitAsync(
                url,
                () => new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json"),
                waitSeconds: 8,
                postTimeoutSeconds: 30);

            var responseText = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[GRAPHQL-STDOUT]\n{responseText}");
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"[TOURNAMENT] HTTP {resp.StatusCode}: {responseText}");

            // Trích xuất opId thật từ GraphQL response
            string? opHex = null;
            try
            {
                using var doc = JsonDocument.Parse(responseText);
                if (doc.RootElement.TryGetProperty("data", out var dataEl))
                {
                    if (dataEl.ValueKind == JsonValueKind.String)
                    {
                        opHex = dataEl.GetString();
                    }
                    else if (dataEl.ValueKind == JsonValueKind.Object &&
                             dataEl.TryGetProperty("recordMatch", out var rm) &&
                             rm.ValueKind == JsonValueKind.String)
                    {
                        opHex = rm.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TOURNAMENT][WARN] Could not parse opId: {ex.Message}");
            }

            if (string.IsNullOrEmpty(opHex))
                throw new InvalidOperationException($"[TOURNAMENT] No opId returned in response: {responseText}");

            Console.WriteLine($"[GRAPHQL-OUTPUT] SubmitTournamentMatchResult response:");
            Console.WriteLine($"[SUCCESS] MatchId: {matchResult.MatchId}");
            Console.WriteLine($"[SUCCESS] Winner: {matchResult.WinnerUsername}");
            Console.WriteLine($"[SUCCESS] OpId: {opHex}");

            return JsonSerializer.Serialize(new
            {
                success = true,
                matchId = matchResult.MatchId,
                opId = opHex,
                raw = "[tournament submit ok]"
            });
        }
        public async Task<string> EndTournamentSeasonAsync()
        {
            Console.WriteLine($"[GRAPHQL-CALL] Running mutation: endTournamentSeason");
            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";

            var graphql = @"
                mutation {
                    endTournamentSeason
                }";

            var payload = new { query = graphql };
            var resp = await _orchestrator.PostSingleWithServiceWaitAsync(url,
                () => new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                waitSeconds: 8,
                postTimeoutSeconds: 30);

            var responseText = await resp.Content.ReadAsStringAsync();
            resp.EnsureSuccessStatusCode();
            Console.WriteLine($"[GRAPHQL-STDOUT]\n{responseText}");
            Console.WriteLine($"[GRAPHQL-DEBUG] ExitCode: {(resp.IsSuccessStatusCode ? "0" : ((int)resp.StatusCode).ToString())}");
            string? opId = null;
            using (var doc = JsonDocument.Parse(responseText))
            {
                if (doc.RootElement.TryGetProperty("data", out var dataEl))
                {
                    if (dataEl.ValueKind == JsonValueKind.String)
                        opId = dataEl.GetString() ?? "";
                    else if (dataEl.TryGetProperty("endTournamentSeason", out var opEl))
                        opId = opEl.GetString() ?? "";
                }
            }

            if (string.IsNullOrEmpty(opId))
                throw new InvalidOperationException($"[TOURNAMENT] No opId returned for endTournamentSeason. Response: {responseText}");

            Console.WriteLine($"[GRAPHQL-OUTPUT] Linera \n EndTournamentSeason response:");
            Console.WriteLine($"[SUCCESS] OpId: {opId}");
            return opId;
        }
        #endregion
    }
}