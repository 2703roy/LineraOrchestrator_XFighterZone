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
                    xfighter_module_id = config.XFighterModuleId,
                    xfighter_app_id = config.XFighterAppId,
                    leaderboard_app_id = config.LeaderboardAppId,
                    publisher_chain_id = config.PublisherChainId,
                    tournament_app_id = config.TournamentAppId,
                    isReady = config.IsReady

                });
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

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

        // DEBUG
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
                var (chainId, appId) = await _svc.OpenAndCreateWithServiceControlAsync(req.Player1, req.Player2);

                // for backward-compatibility we treat matchId == chainId
                var matchId = chainId;
                return Ok(new { success = true, matchId, chainId, appId });
            }
            catch (InvalidOperationException ioe)
            {
                // service chết hoặc chưa start
                // Service not ready -> try waiting for monitor to recover then retry once
                // cứ 250ms kiểm tra 1 lần, trả về true khi PID service tồn tại và ổn định ≥ 400ms
                // Nếu sau 10 giây mà chưa ổn định -> trả về false
                Console.WriteLine("[API] Service not ready, waiting monitor to recover before retrying...");
                bool recovered = await _svc.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 250, stableMs: 400);
                if (!recovered) // Nếu service vẫn chưa phục hồi sau 10s
                {
                    Console.WriteLine("[API] Submit: monitor did not report recovery in time.");
                    return StatusCode(503, new { success = false, error = ioe.Message }); // trả mã 503 cho client kèm lỗi gốc
                }
                // Nếu monitor báo service đã ổn định → Thử gửi lại đúng 1 lần
                // KHÔNG retry mutation nữa -> Tạo dư chainId
                return StatusCode(503, new { success = false, error = "Service recovered late, please retry client-side." });
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

        //submit-match-result endpoint
        [HttpPost("submit-match-result")]
        public async Task<IActionResult> SubmitMatchResult([FromBody] SubmitMatchRequest request)
        {
            try
            {
                if (request == null || request.MatchResult == null)
                    return BadRequest(new { success = false, message = "Invalid payload" });

                // auto-generate matchId nếu trống
                if (string.IsNullOrWhiteSpace(request.MatchResult.MatchId) &&
                        !string.IsNullOrWhiteSpace(request.ChainId))
                {
                    request.MatchResult.MatchId = request.ChainId;
                }

                // auto-fill timestamp nếu 0
                if (request.MatchResult.Timestamp == 0)
                    request.MatchResult.Timestamp = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                try
                {
                    // gọi service (service sẽ handle chainId nếu null/empty)
                    var json = await _svc.SubmitMatchResultAsync(request.ChainId, request.MatchResult);
                    // parse JSON gốc
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // bóc tách các field chính để trả về cho client
                    var response = new
                    {
                        success = root.GetProperty("success").GetBoolean(),
                        matchId = root.GetProperty("matchId").GetString(),
                        chainId = root.GetProperty("chainId").GetString(),
                        opId = root.TryGetProperty("opId", out var op) ? op.GetString() : null,
                        verified = root.TryGetProperty("verified", out var ver) && ver.GetBoolean()
                    };

                    // log full raw để debug if Linera SDK change response way
                    Console.WriteLine($"[DEBUG] Raw Linera response: {json}");

                    return Ok(response);
                }
                catch (InvalidOperationException ioe)
                {
                    // service not ready — wait for monitor -> retry once
                    Console.WriteLine("[API] Submit: service not ready, waiting monitor to recover before retrying...");
                    var recovered = await _svc.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 250, stableMs: 400);
                    if (!recovered)
                    {
                        Console.WriteLine("[API] Submit: monitor did not report recovery in time.");
                        return StatusCode(503, new { success = false, error = ioe.Message });
                    }

                    // retry once
                    try
                    {
                        var json = await _svc.SubmitMatchResultAsync(request.ChainId, request.MatchResult);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        var response = new
                        {
                            success = root.GetProperty("success").GetBoolean(),
                            matchId = root.GetProperty("matchId").GetString(),
                            chainId = root.GetProperty("chainId").GetString(),
                            opId = root.TryGetProperty("opId", out var op) ? op.GetString() : null,
                            verified = root.TryGetProperty("verified", out var ver) && ver.GetBoolean()
                        };
                        Console.WriteLine($"[DEBUG] Raw Linera response (retry): {json}");
                        return Ok(response);
                    }
                    catch (Exception retryEx)
                    {
                        Console.WriteLine($"[API] Submit retry failed: {retryEx.Message}");
                        return StatusCode(503, new { success = false, error = retryEx.Message });
                    }
                }
            }
            catch (TimeoutException tex)
            {
                // Timeout from service — likely long tx commit
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
        [HttpGet("linera-config")]
        public IActionResult GetLineraConfig()
        {
            try
            {
                var cfg = _svc.GetCurrentConfig();
                // Xác định chế độ hiện tại
                var mode = cfg.UseRemoteTestnet ? "Conway Testnet" : "Local Net Backup";

                return Ok(new
                {
                    success = true,
                    mode,
                    linera_wallet = cfg.LineraWallet,
                    linera_storage = cfg.LineraStorage,
                    linera_keystore = cfg.LineraKeystore,
                    xfighter_module_id = cfg.XFighterModuleId,
                    leaderboard_app_id = cfg.LeaderboardAppId,
                    xfighter_app_id = cfg.XFighterAppId,
                    publisher_chain_id = cfg.PublisherChainId,
                    tournament_app_id = cfg.TournamentAppId,
                    isReady = cfg.IsReady
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpGet("ping")]
        public IActionResult Ping() => Ok(new { success = true, service = "linera-orchestrator", now = DateTime.UtcNow });

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

        [HttpPost("tournament/start")]
        public async Task<IActionResult> StartTournament()
        {
            await _svc.StartTournamentAsync();
            return Ok("Tournament completed");
        }

        [HttpPost("tournament/leaderboard")]
        public async Task<IActionResult> GetTournamentLeaderboard()
        {
            try
            {
                var json = await _svc.GetTournamentLeaderboardDataAsync();
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
                // Gọi service tạo snapshot từ leaderboard hiện tại (hoặc live data)
                var snapshot = await _svc.CreateAndSaveLeaderboardSnapshotAsync();

                if (snapshot == null)
                {
                    return StatusCode(500, new
                    {
                        success = false,
                        message = "Không thể tạo snapshot. Kiểm tra lại dữ liệu leaderboard."
                    });
                }

                return Ok(new
                {
                    success = true,
                    snapshotId = snapshot.SnapshotId,
                    createdAt = snapshot.CreatedAt,
                    topPlayers = snapshot.TopPlayers,
                    allPlayers = snapshot.AllPlayers,
                    hash = snapshot.Hash
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
    }
}
