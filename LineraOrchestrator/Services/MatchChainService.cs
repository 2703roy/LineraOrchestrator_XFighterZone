// MatchChainService.cs
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LineraOrchestrator.Models;

namespace LineraOrchestrator.Services
{
    public class MatchChainService
    {
        private readonly LineraConfig _config;
        private readonly HttpClient _httpClient;
        private readonly LeaderboardService _leaderboardSvc;
        private readonly LineraOrchestratorService _orchestrator;

        // Environment Path Service
        private static readonly string ROOT = EnvironmentService.GetDataPath();
        private static readonly string _submitRequestsFile = Path.Combine(ROOT, "submit_requests.json");

        // Queue system
        private readonly Channel<Func<Task>> _openChannel;
        private readonly Channel<Func<Task>> _submitChannel;
        private readonly object _openTcsLock = new();
        private TaskCompletionSource<bool> _openEmptyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _openPending = 0;

        // File persistence
        private static readonly object _submitFileLock = new();

        // Worker tasks  
        private readonly List<Task> _openWorkerTask = [];
        private readonly List<Task> _submitWorkerTask = [];
        private readonly CancellationTokenSource _cts = new();

        // Known chains tracking  
        private static readonly ConcurrentDictionary<string, bool> _knownChains = new();

        public MatchChainService(LineraConfig config, HttpClient httpClient, LeaderboardService leaderboardSvc, LineraOrchestratorService orchestrator)
        {
            _config = config;
            _httpClient = httpClient;
            _leaderboardSvc = leaderboardSvc;
            _orchestrator = orchestrator;

            // Khởi tạo channels 
            _openChannel = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(200)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            _submitChannel = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(500)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            StartWorkers();
        }

        private void StartWorkers()
        {
            for (int i = 0; i < 10; i++) // 20 workers
            {
                _openWorkerTask.Add(Task.Run(() => ProcessOpenQueueAsync(_cts.Token)));
                _submitWorkerTask.Add(Task.Run(() => ProcessSubmitQueueAsync(_cts.Token)));
            }
        }

        #region Linera open match chainId
        public async Task<(string ChainId, string? AppId)> EnqueueOpenAsync()
        {
            var tcs = new TaskCompletionSource<(string, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);

            // --- Quản lý trạng thái Open Queue ---
            var newCount = Interlocked.Increment(ref _openPending);
            lock (_openTcsLock)
            {
                if (newCount == 1 && _openEmptyTcs.Task.IsCompleted)
                {
                    _openEmptyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            // --- Enqueue job Open ---
            try
            {
                await _openChannel.Writer.WriteAsync(async () =>
                {
                    try
                    {
                        var (chainId, appId) = await OpenAndCreateCoreAsync();
                        tcs.SetResult((chainId, appId));
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPEN-ENQUEUE][ERROR] {ex.Message}");
                // Nếu enqueue thất bại, rollback increment
                var rollbackCount = Interlocked.Decrement(ref _openPending);
                if (rollbackCount == 0)
                {
                    lock (_openTcsLock)
                    {
                        if (!_openEmptyTcs.Task.IsCompleted)
                            _openEmptyTcs.TrySetResult(true);
                    }
                }
                throw; // rethrow cho caller
            }

            return await tcs.Task;
        }

        /// Gửi mutation openAndCreate, sau đó poll resolveRequest tới khi có chainId mới
        private async Task<(string ChainId, string? AppId)> OpenAndCreateCoreAsync()
        {
            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.XFighterAppId}";

            // Snapshot allOpenedChains → build existing.
            var seedPayload = new { query = "query { allOpenedChains }" };
            using var ctsSeed = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var seedResp = await _httpClient.PostAsync(url, new StringContent(JsonSerializer.Serialize(seedPayload), Encoding.UTF8, "application/json"), ctsSeed.Token);
            var seedText = await seedResp.Content.ReadAsStringAsync();
            var existing = new HashSet<string>();
            try
            {
                using var seedDoc = JsonDocument.Parse(seedText);
                var seedArr = seedDoc.RootElement.GetProperty("data").GetProperty("allOpenedChains");
                foreach (var el in seedArr.EnumerateArray())
                {
                    var cid = el.GetString();
                    if (!string.IsNullOrWhiteSpace(cid)) existing.Add(cid);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Failed to parse seed allOpenedChains: {ex.Message}");
            }

            Console.WriteLine($"[GRAPHQL-CALL] Running mutation: openAndCreate");
            var graphql = @"mutation { openAndCreate }";
            var payload = new { query = graphql };
            var sw = Stopwatch.StartNew(); // Đo lường sức mạnh Linera

            HttpResponseMessage resp;
            string responseText = "";
            try
            {
                resp = await _orchestrator.PostSingleWithServiceWaitAsync(url, () =>
                    new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                    waitSeconds: 15, postTimeoutSeconds: 45);
                responseText = await resp.Content.ReadAsStringAsync();

                Console.WriteLine($"[GRAPHQL-RESPONSE]\n{responseText}");
                Console.WriteLine($"[GRAPHQL-DEBUG] ExitCode: {(resp.IsSuccessStatusCode ? "0" : resp.StatusCode)}");

                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[ERROR] GraphQL request failed with status: {resp.StatusCode}");
                    throw new InvalidOperationException($"GraphQL request failed: {resp.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] {ex} Waiting for Linera service to recover before continuing...");
                throw;
            }
            var text = await resp.Content.ReadAsStringAsync();

            //2. Poll allOpenedChains vài lần để phát hiện chain mới so với existing + _knownChains.
            const int maxAttempts = 5;
            string? chainId = null;
            var pollPayload = new { query = "query { allOpenedChains }" };

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                await Task.Delay(1000); // give service time
                var pollResp = await _httpClient.PostAsync(url,
                    new StringContent(JsonSerializer.Serialize(pollPayload), Encoding.UTF8, "application/json"),
                    CancellationToken.None);
                var pollText = await pollResp.Content.ReadAsStringAsync();

                try
                {
                    using var doc = JsonDocument.Parse(pollText);
                    var arr = doc.RootElement.GetProperty("data").GetProperty("allOpenedChains");
                    foreach (var el in arr.EnumerateArray())
                    {
                        var cid = el.GetString();
                        if (string.IsNullOrWhiteSpace(cid)) continue;

                        if (_knownChains.TryAdd(cid, true) && !existing.Contains(cid))
                        {
                            chainId = cid;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to parse allOpenedChains response (attempt {attempt}): {ex.Message}");
                    Console.WriteLine($"[LINERA-CLI-OUTPUT] Raw response: {pollText}");
                }

                if (chainId != null)
                {
                    sw.Stop();
                    break;
                }
            }
            // khi poll không tìm thấy chainId (timeout), 
            if (chainId == null)
            {
                throw new TimeoutException("No new chain found after polling allOpenedChains");
            }

            //3. Lấy appId từ allChildApps.
            var childQuery = new { query = "query { allChildApps { chainId appId } }" };
            var childResp = await _httpClient.PostAsync(url,
                new StringContent(JsonSerializer.Serialize(childQuery), Encoding.UTF8, "application/json"),
                CancellationToken.None);
            var childText = await childResp.Content.ReadAsStringAsync();
            string? childAppId = null;
            using (var childDoc = JsonDocument.Parse(childText))
            {
                var childArr = childDoc.RootElement.GetProperty("data").GetProperty("allChildApps");
                foreach (var el in childArr.EnumerateArray())
                {
                    if (el.GetProperty("chainId").GetString() == chainId)
                    {
                        childAppId = el.GetProperty("appId").GetString();
                        break;
                    }
                }
            }

            // khi không tìm thấy appId (timeout)
            if (childAppId == null)
            {
                Console.WriteLine("[WARN] childAppId is null – unexpected contract behavior.");
            }

            Console.WriteLine($"[GRAPHQL-OUTPUT] OpenAndCreate response:");
            Console.WriteLine($"[SUCCESS] Chain: {chainId}");
            Console.WriteLine($"[SUCCESS] AppId: {childAppId}");
            Console.WriteLine($"[LINERA] Chain creation detected in {sw.ElapsedMilliseconds} ms");
            return (chainId!, childAppId);
        }
        #endregion

        #region SubmitMatchResultAsync 
        public async Task<string> EnqueueSubmitAsync(string? chainId, string? appId, MatchResult matchResult)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Kiểm tra nếu có open pending, persist request và return ngay
            if (Volatile.Read(ref _openPending) > 0)
            {
                Console.WriteLine($"[SUBMIT] Open pending detected ({_openPending}), persisting submit request to file...");
                var req = new SubmitRequest { ChainId = chainId, AppId = appId, MatchResult = matchResult };
                AppendSubmitRequestToFile(req);

                var queuedResp = JsonSerializer.Serialize(new
                {
                    success = true,
                    queued = true,
                    message = "Persisted to submit_requests.json due to open queue activity."
                }, JsonOptions.Write);

                tcs.SetResult(queuedResp);
                return queuedResp;
            }

            // Nếu không có open pending, xử lý submit ngay
            await _submitChannel.Writer.WriteAsync(async () =>
            {
                try
                {
                    // Đơn giản: Nếu có open pending bất kỳ lúc nào, persist và return
                    if (Volatile.Read(ref _openPending) > 0)
                    {
                        var req = new SubmitRequest { ChainId = chainId, AppId = appId, MatchResult = matchResult };
                        AppendSubmitRequestToFile(req);
                        tcs.SetResult(JsonSerializer.Serialize(new
                        {
                            success = true,
                            queued = true,
                            message = "Persisted to submit_requests.json - open queue became active during processing."
                        }, JsonOptions.Write));
                        return;
                    }

                    var result = await SubmitMatchResultCoreAsync(chainId, appId, matchResult).ConfigureAwait(false);
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return await tcs.Task.ConfigureAwait(false);
        }

        public async Task<string> SubmitMatchResultCoreAsync(string? chainId, string? appId, MatchResult matchResult)
        {
            // Client MUST provide chainId and appId as parameters
            if (string.IsNullOrWhiteSpace(chainId))
                throw new ArgumentNullException(nameof(chainId));

            if (string.IsNullOrWhiteSpace(appId))
                throw new ArgumentNullException(nameof(appId));

            ArgumentNullException.ThrowIfNull(matchResult);

            var mt = (matchResult.MatchType ?? string.Empty).Trim().ToLowerInvariant();
            Console.WriteLine($"[SUBMIT] Detected MatchType = {mt}");
            bool isTournament = mt == "tournament";
            bool isRank = mt.StartsWith("rank");
            bool isNormal = mt == "normal" || (!isRank && !isTournament);

            if (isTournament)
            {
                Console.WriteLine($"[SUBMIT] Tournament match detected (matchId={matchResult.MatchId}), skipping chain logic completely.");
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    matchId = matchResult.MatchId,
                    message = "Tournament match handled separately in TournamentApp"
                }, JsonOptions.Write);
            }

            string? opHex = null;
            string text = string.Empty;

            Console.WriteLine($"[GRAPHQL-CALL] SubmitMatch Running mutation: recordScore");
            var url = $"http://localhost:8080/chains/{chainId}/applications/{appId}";
            var graphql = @"
                mutation recordScore($matchResult: MatchResultInput!) {
                    recordScore(matchResult: $matchResult)
                }";

            var payload = new { query = graphql, variables = new { matchResult } };
            var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write),
                Encoding.UTF8, "application/json");

            var sw = Stopwatch.StartNew();
            HttpResponseMessage resp = null!;

            try
            {
                resp = await _orchestrator.PostSingleWithServiceWaitAsync(
                    url,
                    () => new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write),
                        Encoding.UTF8, "application/json"),
                    waitSeconds: 8,
                    postTimeoutSeconds: 30).ConfigureAwait(false);

                text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Console.WriteLine($"[GRAPHQL-STDOUT]\n{text}");
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[GRAPHQL-STDERR]\nHTTP Error: {resp.StatusCode}");
                }
                Console.WriteLine($"[GRAPHQL-DEBUG] ExitCode: {(resp.IsSuccessStatusCode ? "0" : ((int)resp.StatusCode).ToString())}");
                sw.Stop();
                Console.WriteLine($"[LINERA] recordScore latency = {sw.ElapsedMilliseconds} ms");

                // Parse operation hex
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("data", out var dataEl))
                    {
                        if (dataEl.ValueKind == JsonValueKind.String)
                        {
                            opHex = dataEl.GetString();
                        }
                        else if (dataEl.ValueKind == JsonValueKind.Object &&
                                 dataEl.TryGetProperty("recordScore", out var rs) &&
                                 rs.ValueKind == JsonValueKind.String)
                        {
                            opHex = rs.GetString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Parse op hex failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GRAPHQL-STDERR]\n{ex.Message}");
                Console.WriteLine($"[GRAPHQL-DEBUG] ExitCode: 1");
                Console.WriteLine($"[ERROR] Submit failed: {ex.Message}");
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }

            Console.WriteLine($"[DEBUG] Successfully Submitted match on chainId {chainId}.");
            Console.WriteLine($"[DEBUG] Extracted opHex = {opHex ?? "null"}");
            Console.WriteLine($"[SUBMIT] Match {matchResult.MatchId} submitted to chain {chainId} with op {opHex}");

            // Wait for leaderboard update
            bool verified = await _leaderboardSvc.WaitForLeaderboardUpdateAsync(
                matchResult.Player1Username,
                matchResult.Player2Username,
                timeoutMs: 5000).ConfigureAwait(false);

            Console.WriteLine($"[GRAPHQL-OUTPUT] SubmitMatchResult response:");
            Console.WriteLine($"[SUCCESS] ChainId: {chainId}");
            Console.WriteLine($"[SUCCESS] AppId: {appId}");
            Console.WriteLine($"[SUCCESS] OpId: {opHex}");
            Console.WriteLine($"[SUCCESS] MatchId: {matchResult.MatchId}");
            Console.WriteLine($"[SUCCESS] Verified: {verified}");
            return JsonSerializer.Serialize(new
            {
                success = true,
                matchId = matchResult.MatchId,
                chainId,
                appId, // Return the appId used
                opId = opHex,
                verified,
                raw = text
            });
        }
        #endregion

        #region ProcessOpenQueueAsync, ProcessSubmitQueueAsync
        public async Task ProcessOpenQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var job in _openChannel.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        // ensure service stable via monitor before running 
                        while (!await _orchestrator.WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
                        {
                            await Task.Delay(500, cancellationToken);
                        }

                        await job().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[OPEN-WORKER][ERROR] {ex}");
                    }
                    finally
                    {
                        // decrement pending. Nếu về 0 -> signal open-empty
                        var newCount = Interlocked.Decrement(ref _openPending);
                        if (newCount == 0)
                        {
                            Console.WriteLine("[OPEN] All open jobs completed -> signaling submit queue");

                            lock (_openTcsLock)
                            {
                                if (!_openEmptyTcs.Task.IsCompleted)
                                    _openEmptyTcs.TrySetResult(true);
                            }

                            // Kích hoạt submit queue bằng cách gửi job rỗng
                            try
                            {
                                await _submitChannel.Writer.WriteAsync(() =>
                                {
                                    Console.WriteLine("[TRIGGER] Activating submit queue after open queue empty");
                                    return Task.CompletedTask;
                                }, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[TRIGGER] Error activating submit queue: {ex}");
                            }
                        }
                        else if (newCount < 0)
                        {
                            // Reset nếu có lỗi underflow
                            Interlocked.Exchange(ref _openPending, 0);
                            Console.WriteLine($"[OPEN-WARN] OpenPending underflow detected, reset to 0");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* canceled */ }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPEN-WORKER][FATAL] {ex}");
            }
        }

        private async Task ProcessSubmitQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var job in _submitChannel.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        // Đợi cho đến khi open queue trống
                        while (Volatile.Read(ref _openPending) > 0)
                        {
                            await Task.Delay(100, cancellationToken);
                        }

                        // Xử lý persistent queue trước
                        await ProcessPersistentSubmitQueueAsync(cancellationToken).ConfigureAwait(false);

                        // Xử lý job submit mới
                        await job().ConfigureAwait(false);

                        // CLEANUP SAU KHI XỬ LÝ: kiểm tra và xóa file nếu rỗng
                        CleanupEmptySubmitFile();
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SUBMIT-WORKER][ERROR] {ex}");
                    }
                }
            }
            catch (OperationCanceledException) { /* canceled */ }
            catch (Exception ex)
            {
                Console.WriteLine($"[SUBMIT-WORKER][FATAL] {ex}");
            }
        }
      
        private async Task ProcessPersistentSubmitQueueAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_submitRequestsFile))
            {
                Console.WriteLine("[PERSIST-QUEUE] No submit requests file found.");
                return;
            }

            List<SubmitRequest> requests;
            lock (_submitFileLock)
            {
                requests = LoadSubmitRequestsFromFile();
            }

            if (requests == null || requests.Count == 0)
            {
                Console.WriteLine("[PERSIST-QUEUE] No pending submit requests.");
                return;
            }

            Console.WriteLine($"[PERSIST-QUEUE] Processing {requests.Count} pending submit requests...");

            var successfulRequests = new List<SubmitRequest>();
            var failedRequests = new List<SubmitRequest>();

            foreach (var req in requests.ToList()) // ToList để tránh modify while iterating
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                await Task.Delay(100, cancellationToken);

                // Kiểm tra lại nếu có open request mới active thì DỪNG và LƯU LẠI
                if (Volatile.Read(ref _openPending) > 0)
                {
                    Console.WriteLine($"[PERSIST-QUEUE] Open queue became active, stopping persistent processing. Remaining: {requests.Count}");
                    break;
                }

                try
                {
                    Console.WriteLine($"[PERSIST-QUEUE] Submitting matchId={req.MatchResult?.MatchId}");

                    // Sử dụng signature mới với chainId và appId
                    var resultJson = await SubmitMatchResultCoreAsync(req.ChainId, req.AppId, req.MatchResult!);

                    // Parse kết quả để xác định success
                    using var doc = JsonDocument.Parse(resultJson);
                    if (doc.RootElement.TryGetProperty("success", out var successProp) &&
                        successProp.GetBoolean())
                    {
                        successfulRequests.Add(req);
                        Console.WriteLine($"[PERSIST-QUEUE] Success: {req.MatchResult?.MatchId}");
                    }
                    else
                    {
                        failedRequests.Add(req);
                        Console.WriteLine($"[PERSIST-QUEUE] Failed: {req.MatchResult?.MatchId}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedRequests.Add(req);
                    Console.WriteLine($"[PERSIST-QUEUE] Error: {req.MatchResult?.MatchId} - {ex.Message}");

                    // Delay ngắn trước khi thử request tiếp theo
                    await Task.Delay(500, cancellationToken);
                }
            }

            // Cập nhật file: chỉ giữ lại các request thất bại
            if (successfulRequests.Count > 0 || failedRequests.Count != requests.Count)
            {
                lock (_submitFileLock)
                {
                    var remainingRequests = requests.Except(successfulRequests).ToList();
                    SaveSubmitRequestsToFileAtomic(remainingRequests);

                    Console.WriteLine($"[PERSIST-QUEUE] Completed: {successfulRequests.Count} successful, {failedRequests.Count} failed, {remainingRequests.Count} remaining");
                }
            }
        }

        public async Task StopAsync()
        {
            try
            {
                // complete writers so ReadAllAsync can finish after draining
                _openChannel.Writer.Complete();
            }
            catch (Exception ex) { Console.WriteLine($"Error completing open channel: {ex}"); }

            try
            {
                _submitChannel.Writer.Complete();
            }
            catch (Exception ex) { Console.WriteLine($"Error completing submit channel: {ex}"); }

            // cancel any blocking waits (monitor checks, etc.)
            _cts.Cancel();

            // wait for workers to finish processing queued items
            var tasks = new List<Task>();
            tasks.AddRange(_openWorkerTask);
            tasks.AddRange(_submitWorkerTask);

            if (tasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error waiting for workers to finish: {ex}");
                }
            }
        }

     
        #endregion

        #region File Persistence Methods
        private static List<SubmitRequest> LoadSubmitRequestsFromFile()
        {
            lock (_submitFileLock)
            {
                try
                {
                    if (!File.Exists(_submitRequestsFile)) return [];
                    var fi = new FileInfo(_submitRequestsFile);
                    if (fi.Length == 0) return [];

                    var json = File.ReadAllText(_submitRequestsFile);
                    var list = JsonSerializer.Deserialize<List<SubmitRequest>>(json, JsonOptions.Read);
                    return list ?? [];
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to load submit requests file: {ex.Message}");
                    return [];
                }
            }
        }

        private static bool SaveSubmitRequestsToFileAtomic(List<SubmitRequest> list)
        {
            lock (_submitFileLock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_submitRequestsFile);
                    if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var json = JsonSerializer.Serialize(list, JsonOptions.Write);
                    var tmp = _submitRequestsFile + ".tmp";
                    File.WriteAllText(tmp, json);

                    // Atomic replace
                    if (File.Exists(_submitRequestsFile))
                        File.Replace(tmp, _submitRequestsFile, null);
                    else
                        File.Move(tmp, _submitRequestsFile);

                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to save submit requests atomically: {ex.Message}");
                    return false;
                }
            }
        }

        private static void AppendSubmitRequestToFile(SubmitRequest req)
        {
            ArgumentNullException.ThrowIfNull(req);

            lock (_submitFileLock)
            {
                var list = LoadSubmitRequestsFromFile() ?? [];
                list.Add(req);
                var ok = SaveSubmitRequestsToFileAtomic(list);
                if (!ok) Console.WriteLine("[WARN] AppendSubmitRequestToFile: failed to persist request.");
            }
        }

        // Helper method để cleanup file rỗng
        private void CleanupEmptySubmitFile()
        {
            lock (_submitFileLock)
            {
                try
                {
                    if (File.Exists(_submitRequestsFile))
                    {
                        var requests = LoadSubmitRequestsFromFile();
                        if (requests == null || requests.Count == 0)
                        {
                            File.Delete(_submitRequestsFile);
                            Console.WriteLine("[CLEANUP] Deleted empty submit_requests.json");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CLEANUP-WARN] Failed to cleanup submit file: {ex.Message}");
                }
            }
        }

        #endregion
    }
}