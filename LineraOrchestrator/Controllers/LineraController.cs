//LineraController.cs
using System.Text.Json;
using LineraOrchestrator.Services;
using LineraOrchestrator.Models;
using Microsoft.AspNetCore.Mvc;


namespace LineraOrchestrator.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class LineraController : ControllerBase
    {
        private readonly LineraOrchestratorService _svc;
        // Khởi tạo Controller với service Linera
        public LineraController(LineraOrchestratorService svc)
        {
            _svc = svc;
        }

        #region Start Linera Node/ Service/ Config

        // API để khởi động Linera Node
        [HttpPost("start-linera-node")]
        public async Task<IActionResult> StartLineraNode()
        {
            try
            {
                var config = await _svc.StartLineraNodeAsync();
                // Xác định chế độ hiện tại
                var mode = config.UseRemoteTestnet ? "Conway (Remote Testnet)" : "Local Net Backup";

                return Ok(new
                {
                    success = true,
                    message = $"Linera Node đã thành công khởi động ở chế độ {mode} và các biến môi trường đã được trích xuất.",
                    linera_wallet = config.LineraWallet,
                    linera_keystore = config.LineraKeystore,
                    linera_storage = config.LineraStorage,
                    publisher_chain_id = config.PublisherChainId,
                    xfighter_module_id = config.XFighterModuleId,
                    xfighter_app_id = config.XFighterAppId,
                    leaderboard_app_id = config.LeaderboardAppId,
                    tournament_app_id = config.TournamentAppId,
                    isReady = config.IsReady

                });
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }
        // DEBUG
        [HttpPost("start-linera-service")]
        public async Task<IActionResult> StartLineraService([FromQuery] int port = 8080)
        {
            try
            {
                var pid = await _svc.StartLineraServiceAsync(port);
                return Ok(new { success = true, pid });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpGet("linera-config")]
        public IActionResult GetLineraConfig()
        {
            try
            {
                var config = _svc.GetCurrentConfig();
                // Xác định chế độ hiện tại
                var mode = config.UseRemoteTestnet ? "Conway Testnet" : "Local Net Backup";

                return Ok(new
                {
                    success = true,
                    mode,
                    linera_wallet = config.LineraWallet,
                    linera_storage = config.LineraStorage,
                    linera_keystore = config.LineraKeystore,
                    xfighter_module_id = config.XFighterModuleId,
                    leaderboard_app_id = config.LeaderboardAppId,
                    xfighter_app_id = config.XFighterAppId,
                    publisher_chain_id = config.PublisherChainId,
                    tournament_app_id = config.TournamentAppId,
                    isReady = config.IsReady
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }
#endregion

        #region helper AllOpenedChains, submit, get leaderboard global
        // DEBUG HELPER
        [HttpPost("all-opened-chains")]
        public async Task<IActionResult> AllOpenedChains()
        {
            try
            {
                var chains = await _svc.GetAllOpenedChainsAsync();
                return Ok(new { success = true, count = chains.Count, chains });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message, details = ex.InnerException?.Message });
            }
        }

        [HttpPost("open-and-create")]
        public async Task<IActionResult> OpenAndCreate([FromBody] CreateMatchRequest req)
        {
            try
            {
                var (chainId, appId) = await _svc.EnqueueOpenAsync(req.Player1, req.Player2);

                return Ok(new { success = true, matchId = chainId, chainId, appId });
            }
            catch (TimeoutException tex)
            {
                // nếu service hoặc operation time out (ví dụ quá lâu trong queue / post)
                Console.WriteLine($"[API] OpenAndCreate timeout: {tex.Message}");
                return StatusCode(504, new { success = false, error = tex.Message });
            }
            catch (Exception ex)
            {
                // Nếu bug bất ngờ
                return StatusCode(500, new { success = false, error = ex.Message, details = ex.InnerException?.Message });
            }
        }

        // submit-match-result endpoint
        [HttpPost("submit-match-result")]
        public async Task<IActionResult> SubmitMatchResult([FromBody] SubmitMatchRequest request)
        {
            try
            {
                if (request == null || request.MatchResult == null)
                    return BadRequest(new { success = false, message = "Invalid payload" });

                // Nếu có chainId mà chưa có matchId → dùng chainId làm matchId
                if (string.IsNullOrWhiteSpace(request.MatchResult.MatchId) && !string.IsNullOrWhiteSpace(request.ChainId))
                    request.MatchResult.MatchId = request.ChainId;

                // auto-fill timestamp nếu 0
                if (request.MatchResult.Timestamp == 0)
                    request.MatchResult.Timestamp = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Gọi service EnqueueSubmitAsync (đợi worker xử lý xong)
                var json = await _svc.EnqueueSubmitAsync(request.ChainId, request.MatchResult);

                // Parse JSON gốc từ service
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Bóc tách các field chính để trả về cho client
                var response = new
                {
                    success = root.GetProperty("success").GetBoolean(),
                    matchId = root.GetProperty("matchId").GetString(),
                    chainId = root.GetProperty("chainId").GetString(),
                    opId = root.TryGetProperty("opId", out var op) ? op.GetString() : null,
                    verified = root.TryGetProperty("verified", out var ver) && ver.GetBoolean(),
                    queued = root.TryGetProperty("queued", out var q) && q.GetBoolean()
                };

                Console.WriteLine($"[DEBUG] Raw Linera response: {json}");

                return Ok(response);
            }
            catch (InvalidOperationException ioe)
            {
                Console.WriteLine($"[API] Submit failed: {ioe.Message}");
                return StatusCode(503, new { success = false, error = ioe.Message });
            }
            catch (TimeoutException tex)
            {
                Console.WriteLine($"[API] Submit timeout: {tex.Message}");
                return StatusCode(504, new { success = false, error = tex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message, details = ex.InnerException?.Message });
            }
        }

        [HttpPost("get-leaderboard-data")]
        public async Task<IActionResult> GetLeaderboardData()
        {
            /// Đọc phương thức trả body.data.leaderboard
            try
            {
                var json = await _svc.GetLeaderboardDataAsync();
                return Ok(new { success = true, body = JsonDocument.Parse(json) });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        //Leaderboard check crosschain messages
        [HttpGet("verify-match-result")]
        public async Task<IActionResult> VerifyMatchResult(
             string userId,
             int expectedMatches,
             int timeoutMs = 8000,
             int pollIntervalMs = 500)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return BadRequest(new { success = false, error = "userId is required" });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    var json = await _svc.GetLeaderboardDataAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("leaderboard", out var lb) &&
                        lb.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in lb.EnumerateArray())
                        {
                            if (e.GetProperty("userId").GetString() == userId &&
                                e.GetProperty("totalMatches").GetInt32() >= expectedMatches)
                            {
                                return Ok(new { success = true, userId, message = "Leaderboard updated" });
                            }
                        }
                    }
                }
                catch { /* bỏ qua lỗi tạm thời */ }

                await Task.Delay(pollIntervalMs);
            }

            return Ok(new
            {
                success = false,
                userId,
                message = $"Không thấy totalMatches >= {expectedMatches} trong {timeoutMs}ms"
            });
        }
        //DEBUG: Match Mapping
        [HttpGet("match-mapping/{matchId}")]
        public IActionResult GetMatchMapping(string matchId)
        {
            if (string.IsNullOrWhiteSpace(matchId))
                return BadRequest(new { success = false, error = "matchId is required" });

            var mapping = _svc.GetMappingForChain(matchId);
            if (mapping == null)
                return NotFound(new { success = false, message = $"No mapping found for matchId={matchId}" });

            return Ok(new
            {
                success = true,
                matchId,
                chainId = mapping.ChainId,
            });
        }
        //DEBUG: Match Mapping
        [HttpGet("match-mapping/all")]
        public IActionResult GetAllMatchMappings()
        {
            var allMappings = _svc.GetAllMappings();
            return Ok(new
            {
                success = true,
                count = allMappings.Count,
                mappings = allMappings.Select(kv => new
                {
                    matchId = kv.Key,
                    chainId = kv.Value.ChainId,
                }).ToList()
            });
        }
        // =Config Status Linera Service on Unity =====================
        //DEBUG Checking Service  [HttpPost("linera-service-status")]
        [HttpGet("linera-service-status")]
        public IActionResult GetLineraServiceStatus()
        {
            try
            {
                var running = _svc.IsServiceRunning();   // check service có chạy không
                var pid = _svc.GetServicePid();          // lấy PID (null nếu chưa có)
                //Console.WriteLine($"[LINERA-SERVICE] Unity Status check via API => Running={running}, PID={pid}");
                return Ok(new
                {
                    success = true,
                    isRunning = running,
                    pid
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Linera] Error in service-status endpoint: {ex.Message}");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }




        // API: Lấy lịch sử 20 trận gần nhất của một player
        [HttpGet("player-history/{username}")]
        public async Task<IActionResult> GetPlayerHistory(string username, [FromQuery] int limit = 20)
        {
            if (string.IsNullOrWhiteSpace(username))
                return BadRequest(new { success = false, error = "username is required" });

            try
            {
                var history = await _svc.GetPlayerHistoryAsync(username, limit);

                if (history == null || history.Count == 0) // username tồn tại
                    return NotFound(new { success = false, error = $"Player '{username}' not found" });

                return Ok(new
                {
                    success = true,
                    username,
                    count = history.Count,
                    matches = history.Select(e => new {
                        chainId = e.ChainId,
                        matchdetails = e.MatchResult // nếu bạn muốn giữ matchResult gốc nguyên vẹn, trả whole object:
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message,
                    details = ex.InnerException?.Message
                });
            }
        }
        // API: Lấy tất cả match trong 1 chainId
        [HttpGet("match-history/{chainId}")]
        public async Task<IActionResult> GetMatchHistoryByChain(string chainId)
        {
            if (string.IsNullOrWhiteSpace(chainId))
                return BadRequest(new { success = false, error = "chainId is required" });

            try
            {
                var matches = await _svc.GetMatchesByChainAsync(chainId);

                if (matches == null || matches.Count == 0) // check chainId tồn tại
                    return NotFound(new { success = false, error = $"Chain '{chainId}' not found" });

                return Ok(new
                {
                    success = true,
                    chainId,
                    count = matches.Count,
                    matches
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message,
                    details = ex.InnerException?.Message
                });
            }
        }
        [HttpGet("match-list")]
        public IActionResult GetMatchList()
        {
            try
            {
                var allMappings = _svc.GetAllMappings();
                return Ok(new
                {
                    success = true,
                    count = allMappings.Count,
                    matches = allMappings.Select(kv => new
                    {
                        chainId = kv.Value.ChainId,
                        appId = kv.Value.AppId,
                        matchId = kv.Value.MatchId,
                        status = kv.Value.Status,
                        player1 = kv.Value.Player1,
                        player2 = kv.Value.Player2,
                        submittedAt = kv.Value.SubmittedAt,
                        submittedOpId = kv.Value.SubmittedOpId
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }
        #endregion

        #region Tournament APIs
        [HttpPost("tournament/leaderboard")]
        public async Task<IActionResult> GetTournamentLeaderboard()
        {
            try
            {
                var json = await _svc.GetTournamentLeaderboardDataAsync();
                if (string.IsNullOrWhiteSpace(json))
                    return StatusCode(500, new { success = false, error = "Empty response from Linera tournament leaderboard" });

                return Ok(new { success = true, body = JsonDocument.Parse(json) });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// Thông tin giải đấu
        [HttpPost("tournament/meta")]
        public async Task<IActionResult> GetTournamentMeta()
        {
            try
            {
                var json = await _svc.GetTournamentMetaDataAsync();
                if (string.IsNullOrWhiteSpace(json))
                    return StatusCode(500, new { success = false, error = "Empty response from Linera tournament meta" });

                return Ok(new { success = true, body = JsonDocument.Parse(json) });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }
        // Snapshot leaderboard
        [HttpPost("leaderboard/create-snapshot")]
        public async Task<IActionResult> CreateLeaderboardSnapshot()
        {
            try
            {
                var snapshot = await _svc.CreateAndSaveLeaderboardSnapshotAsync();
                if (snapshot == null)
                    return StatusCode(500, new { success = false, message = "Không thể tạo snapshot. Kiểm tra lại dữ liệu leaderboard." });

                return Ok(new
                {
                    success = true,
                    snapshotId = snapshot.SnapshotId,
                    createdAt = snapshot.CreatedAt,
                    topPlayers = snapshot.TopPlayers,
                    allPlayers = snapshot.AllPlayers,
                    onChainOpId = snapshot.OnChainOpId
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Lỗi khi tạo snapshot leaderboard",
                    error = ex.Message,
                    details = ex.InnerException?.Message
                });
            }
        }

        // Tournament Lifecycle API
        [HttpPost("tournament/create")]
        public async Task<IActionResult> CreateTournament([FromQuery] string? name = null, [FromQuery] long? startTime = null, [FromQuery] long? endTime = null)
        {
            try
            {
                var result = await _svc.CreateTournamentAsync(name, startTime, endTime);
                return Ok(new { success = true, result });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpGet("tournament/draw")]
        public IActionResult GetTournamentDraw()
        {
            try
            {
                var (shuffled, hash) = _svc.GetShuffledTop8FromLatestSnapshot();
                return Ok(new { success = true, hash, shuffled });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }
        /// <summary>
        /// 1️: Khởi tạo danh sách trận tứ kết (shuffle top8).
        /// </summary>
        [HttpPost("tournament/init")]
        public IActionResult InitTournamentMatches([FromBody] InitTournamentRequest? req)
        {
            try
            {
                // If req?.order provided -> use that ordering; else service will fallback to deterministic shuffle
                var order = req?.Order;
                var ok = _svc.InitTournamentMatches(order);
                if (!ok) return StatusCode(500, new { success = false, message = "InitTournamentMatches failed" });
                return Ok(new { success = true, message = "Tournament initialized", orderUsed = order != null });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 2️: Tiến lên vòng kế tiếp (Semifinal hoặc Final).
        /// Unity Server truyền round hiện tại vào body: { "round": "Quarterfinal" } hoặc { "round": "Semifinal" }.
        /// </summary>
        [HttpPost("tournament/advance")]
        public IActionResult AdvanceTournamentRound([FromBody] JsonElement body)
        {
            try
            {
                if (!body.TryGetProperty("round", out var roundEl))
                    return BadRequest(new { success = false, message = "Missing 'round' field in body." });

                var completedRound = roundEl.GetString()?.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(completedRound))
                    return BadRequest(new { success = false, message = "Invalid round name." });

                bool ok;
                switch (completedRound)
                {
                    case "quarterfinal":
                        ok = _svc.AdvanceToSemiFinals();
                        break;
                    case "semifinal":
                        ok = _svc.AdvanceToFinal();
                        break;
                    default:
                        return BadRequest(new { success = false, message = $"Unsupported round: {completedRound}" });
                }

                if (!ok)
                    return BadRequest(new { success = false, message = "Advance step failed. Check tournament state." });

                return Ok(new { success = true, message = $"Advanced tournament to next round after {completedRound}." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 3: Kết thúc giải đấu, truyền vào champion và runnerUp.
        /// Body ví dụ: { "champion": "PlayerA", "runnerUp": "PlayerB" }.
        /// </summary>
        [HttpPost("tournament/complete")]
        public IActionResult CompleteTournament([FromBody] JsonElement body)
        {
            try
            {
                if (!body.TryGetProperty("champion", out var champEl) ||
                    !body.TryGetProperty("runnerUp", out var runnerEl))
                {
                    return BadRequest(new { success = false, message = "Both champion and runnerUp are required." });
                }

                var champion = champEl.GetString();
                var runnerUp = runnerEl.GetString();
                if (string.IsNullOrWhiteSpace(champion) || string.IsNullOrWhiteSpace(runnerUp))
                    return BadRequest(new { success = false, message = "Champion or runnerUp cannot be empty." });

                var ok = _svc.CompleteTournament(champion, runnerUp);
                if (!ok)
                    return BadRequest(new { success = false, message = "Failed to complete tournament. Check snapshot or matches." });

                return Ok(new { success = true, message = $"Tournament completed. Champion: {champion}, RunnerUp: {runnerUp}" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpPost("tournament/submit-match")]
        public async Task<IActionResult> SubmitTournamentMatch([FromBody] MatchResult match)
        {
            if (match == null || string.IsNullOrWhiteSpace(match.MatchId))
                return BadRequest(new { success = false, message = "match.MatchId is required" });

            try
            {
                var resultJson = await _svc.SubmitTournamentMatchResultAsync(match);
                Console.WriteLine($"[TOURNAMENT] Submitted match {match.MatchId}: {resultJson}");
                return Ok(JsonDocument.Parse(resultJson).RootElement);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpGet("tournament/match-list")]
        public async Task<IActionResult> GetTournamentMatchList()
        {
            try
            {
                var json = await _svc.GetTournamentMatchListAsync();
                var obj = JsonDocument.Parse(json).RootElement;
                return Ok(new
                {
                    success = true,
                    matches = obj.GetProperty("matches")
                });
            }
            catch (FileNotFoundException ex)
            {
                return NotFound(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }


        #endregion
    }
}
