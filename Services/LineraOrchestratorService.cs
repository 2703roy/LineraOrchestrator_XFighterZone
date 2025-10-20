// LineraOrchestratorService.cs
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LineraOrchestrator.Models;


namespace LineraOrchestrator.Services
{
    public class LineraOrchestratorService
    {
        private readonly LineraCliRunner _cli;
        private readonly LineraConfig _config;
        private readonly HttpClient _httpClient;
        public LineraConfig GetCurrentConfig() => _config;

        // Concurrency / monitor fields
        private readonly SemaphoreSlim _serviceSemaphore = new(1, 1);
        private CancellationTokenSource? _serviceMonitorCts;
        private Task? _serviceMonitorTask;
        private readonly object _serviceMonitorLock = new(); //Protect Orchestrator + Restart Linera Service

        // Queue để đảm bảo OpenAndCreate & Submitmatch (mutation) xử lý tuần tự
        private readonly Channel<Func<Task>> _mutationChannel =
        Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,   // chỉ một thread đọc
            SingleWriter = false   // nhiều thread có thể enqueue
        });

        public LineraOrchestratorService(LineraCliRunner cli, LineraConfig config, HttpClient httpClient)
        {
            _cli = cli;
            _config = config;
            // [DEBUG] deployment file in current working dir (project root). Change if you prefer different path.
            // [DEBUG]_deploymentIdsPath = Path.Combine(Directory.GetCurrentDirectory(), "deployment_ids.json");
            LoadMatchMapping(); // Tải dữ liệu khi khởi động
            LoadPlayerIndex(); // Tải dữ liệu PlayerIndex khi khởi động
            _httpClient = httpClient; // tái sử dụng HttpClient using var client = new HttpClient();-> Reuse HttpClient singleton

            //_ = Task.Run(ProcessMutationQueueAsync);
            // start background worker để xử lý tuần tự                                     
            //Khởi động nhiều worker song song để giảm nghẽn queue
            const int MAX_WORKERS = 3;
            for (int i = 0; i < MAX_WORKERS; i++)
            {
                _ = Task.Run(ProcessMutationQueueAsync);
            }

            // Dọn rác định kì 1 phút (periodic) Gom dọn rác cũ hoặc bị bỏ sót
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromMinutes(3));
                    try
                    {
                        CleanupFailedMappings();
                        Console.WriteLine($"[CLEANUP] Periodic cleanup mapping triggered at {DateTime.UtcNow:O}");

                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WARN] Cleanup failed: {ex.Message}");
                    }
                }
            });
        }

        #region Linera Node + Service lifecycle & Monitor Watch-dog
        // Khởi động Linera Net trong nền và trích xuất các biến môi trường
        // Update:  Backup Local Mode + Conway Mode with flag
        public async Task<LineraConfig> StartLineraNodeAsync()
        {
            try
            {
                //0. Clean old setup & Bool change mode
                await StopAllLineraAsync();
                Console.WriteLine($"[CLEANUP] Cleaning old Linera Node + Service...!"); //Dọn dẹp các tiến trình Linera cũ
                await Task.Delay(500);

                // TESTNET CONWAY & BACKUP MODE SETUP
                Console.WriteLine($"[LINERA-ORCH] Config: UseRemoteTestnet={_config.UseRemoteTestnet}" +
                    $", StartServiceWhenRemote={_config.StartServiceWhenRemote}");
                
                if (!_config.UseRemoteTestnet)
                {
                    //LOCAL Chạy lệnh "linera net up" trong nền và lấy các biến môi trường
                    Console.WriteLine("[LINERA-ORCH] LOCAL mode -> launching localnet (linera net up)...");
                    var message = await _cli.StartLineraNetInBackgroundAsync();
                    Console.WriteLine("[LINERA-ORCH] Linera Node Running (localnet).");
                    await Task.Delay(3000); // Chờ một chút để mạng Linera ổn định
                }
                else
                {
                    Console.WriteLine("[LINERA-ORCH] TESTNET CONWAY mode -> skipping Backup Node `linera net up` (using ~/.linera_testnet).");
                    if (!string.IsNullOrWhiteSpace(_config.FaucetUrl))
                        Console.WriteLine($"[INFO] Faucet TESTNET CONWAY URL: {_config.FaucetUrl}");
                    Console.WriteLine($"[INFO] Using existing TESTNET CONWAY wallet: {_config.LineraWallet}");
                }

                //1. Đảm bảo biến môi trường LINERA_* được thiết lập (bất kể localnet hay remote)
                if (string.IsNullOrWhiteSpace(_config.LineraWallet) ||
                    string.IsNullOrWhiteSpace(_config.LineraKeystore) ||
                    string.IsNullOrWhiteSpace(_config.LineraStorage))
                {
                    throw new InvalidOperationException("LINERA_WALLET, LINERA_KEYSTORE và LINERA_STORAGE phải được cấu hình trong LineraConfig trước khi bắt đầu.");
                }

                //2. Export biến môi trường cho linera BEFORE spawning the process,
                Environment.SetEnvironmentVariable("LINERA_WALLET", _config.LineraWallet);
                Environment.SetEnvironmentVariable("LINERA_KEYSTORE", _config.LineraKeystore);
                Environment.SetEnvironmentVariable("LINERA_STORAGE", _config.LineraStorage);
                Console.WriteLine($"Linera Wallet : {_config.LineraWallet}");
                Console.WriteLine($"Linera KeyStone : {_config.LineraKeystore}");
                Console.WriteLine($"Linera Storage : {_config.LineraStorage}");

                //3. PUBLISHER CHAIN lấy chain 0 (admin chain)
                var publisherchainId = GetDefaultChainFromWalletFile(_config.LineraWallet!);
                _config.PublisherChainId = GetDefaultChainFromWalletFile(_config.LineraWallet!);
                Console.WriteLine($"Publisher Chain ID : \n{publisherchainId}");

                //4. PUBLISHER Module
                //var moduleXfighter = await PublishXfighterModuleAsync();
                var moduleXfighter = await RetryAsync(() => PublishXfighterModuleAsync());
                await Task.Delay(2000);
                Console.WriteLine($"Module XFighter : \n{moduleXfighter}");

                //5. PUBLISHER Leaderboard
                //var leaderboardAppId = await PublishAndCreateLeaderboardAppAsync();
                var leaderboardAppId = await RetryAsync(() => PublishAndCreateLeaderboardAppAsync());
                await Task.Delay(2000);
                Console.WriteLine($"Leaderboard App ID :\n{leaderboardAppId}");

                //6. PUBLISHER XFighter Factory
                //var xfighterAppID = await DeployXfighterFactoryAsync();
                var xfighterAppID = await RetryAsync(() => DeployXfighterFactoryAsync());
                await Task.Delay(2000);
                Console.WriteLine($"XFighter App ID : \n{xfighterAppID}");

                //DEBUG 6.1 Tournament
                var tournamentAppId = await RetryAsync(() => PublishAndCreateTournamentAppAsync());
                await Task.Delay(2000);
                Console.WriteLine($"Tournament App ID :\n{tournamentAppId}");
                

                //7. Set vào cấu hình LineraConfig
                _config.LeaderboardAppId = leaderboardAppId;
                _config.XFighterModuleId = moduleXfighter;
                _config.XFighterAppId = xfighterAppID;
                _config.TournamentAppId = tournamentAppId;

                //8. START SERVICE CONWAY / BACKUP MODE (critical)
                if (!_config.UseRemoteTestnet || _config.StartServiceWhenRemote)
                {
                    await StartLineraServiceAsync();
                    Console.WriteLine("Linera service started after node initialization.");
                    await Task.Delay(1000); // give service a moment to initialize and register blob-store client
                    StartServiceMonitor(); // Start monitor (watchdog/supervisor) to ensure service stays up
                    Console.WriteLine("Linera Service Monitoring On !");
                }
                else
                {
                    Console.WriteLine("[INFO] Skipped linera service (UseRemoteTestnet=true, StartServiceWhenRemote=false)");
                }

                Console.WriteLine("=== Linera Ready For MatchMaking System! === \n [READY]");
                return _config; // Trả về đối tượng LineraConfig đã được cập nhật
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Error: {ex.Message}");
            }
        }

        // Phương thức sử dụng publish để tạo XFighter Module 
        public async Task<string> PublishXfighterModuleAsync()
        {
            var contractPath = Path.Combine(_config.XFighterPath, "xfighter_contract.wasm");
            var servicePath = Path.Combine(_config.XFighterPath, "xfighter_service.wasm");

            var result = await _cli.RunAndCaptureOutputAsync(
                "publish-module",
                contractPath,
                servicePath,          
                _config.PublisherChainId!
            );
            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("No output returned when publishing Xfighter module.");

            var moduleId = result.Trim();

            _config.XFighterModuleId = moduleId;
            Console.WriteLine($"Successfully published Xfighter module with ID: {moduleId}");
            return moduleId;
        }
        // Phương thức sử dụng publish-and-create để tạo Leaderboard APPID (raw output fallback)
        public async Task<string> PublishAndCreateLeaderboardAppAsync()
        {
            var contractPath = Path.Combine(_config.LeaderboardPath, "leaderboard_contract.wasm");
            var servicePath = Path.Combine(_config.LeaderboardPath, "leaderboard_service.wasm");

            var result = await _cli.RunAndCaptureOutputAsync(
                "publish-and-create",
                contractPath,
                servicePath,
                _config.PublisherChainId!,
                "--json-argument",
                "null"
            );

            if (string.IsNullOrWhiteSpace(result))
                throw new Exception("Failed to publish and create leaderboard app: no output returned.");

            var leaderboardAppId = result.Trim();
            _config.LeaderboardAppId = leaderboardAppId;
         
            Console.WriteLine($"Successfully created Leaderboard app with ID: {leaderboardAppId}");
            return leaderboardAppId;
        }
        // Phương thức sử dụng publish-and-create tạo XFighter Factory
        public async Task<string> DeployXfighterFactoryAsync()
        {
            var contractPath = Path.Combine(_config.XFighterPath, "xfighter_contract.wasm");
            var servicePath = Path.Combine(_config.XFighterPath, "xfighter_service.wasm");

            var result = await _cli.RunAndCaptureOutputAsync(
                "publish-and-create",
                contractPath,
                servicePath,
                _config.PublisherChainId!,
                "--json-argument", "null",
                "--json-parameters",
                    $"{{\"xfighter_module\":\"{_config.XFighterModuleId}\"," +
                      $"\"leaderboard_id\":\"{_config.LeaderboardAppId}\"}}"

            );

            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("No output returned when creating Xfighter app.");

            var xfighterAppId = result.Trim();
            _config.XFighterAppId = xfighterAppId;
            Console.WriteLine($"Successfully created Xfighter app with ID: {xfighterAppId}");
            return xfighterAppId;
        }

        public async Task<string> PublishAndCreateTournamentAppAsync()
        {
            var contractPath = Path.Combine(_config.TournamentPath!, "tournament_contract.wasm");
            var servicePath = Path.Combine(_config.TournamentPath!, "tournament_service.wasm");

            // Tournament cần tham số leaderboard_id trong JSON parameters
            var parameters = $"{{\"leaderboard_id\":\"{_config.LeaderboardAppId}\"}}";

            var result = await _cli.RunAndCaptureOutputAsync(
                "publish-and-create",
                contractPath,
                servicePath,
                _config.PublisherChainId!,
                "--json-argument", "[]",
                "--json-parameters", parameters
            );

            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("Failed to publish and create tournament app: no output returned.");

            var tournamentAppId = result.Trim();
            _config.TournamentAppId = tournamentAppId;

            Console.WriteLine($"[INFO] Successfully created EMPTY Tournament app with ID: {tournamentAppId}");
            Console.WriteLine($"[INFO] This tournament will be populated later using snapshot leaderboard.");
            return tournamentAppId;
        }

        // Node Safe Guard & Retry - Get DefaultChain From Conway wallet
        public string GetDefaultChainFromWalletFile(string walletPath, int timeoutSeconds = 5)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < timeoutSeconds)
            {
                try
                {
                    if (!File.Exists(walletPath))
                    {
                        Thread.Sleep(500);
                        continue;
                    }

                    var json = File.ReadAllText(walletPath);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        Thread.Sleep(500);
                        continue;
                    }

                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("default", out var defaultChain))
                    {
                        var chainId = defaultChain.GetString();
                        if (!string.IsNullOrWhiteSpace(chainId))
                            return chainId!;
                    }
                }
                catch (IOException)
                {
                    Thread.Sleep(500); // file chưa ghi xong
                }
                catch (JsonException)
                {
                    Thread.Sleep(500); // file chưa hợp lệ     
                }
            }

            throw new InvalidOperationException($"Timeout: không tìm thấy default chain trong {walletPath}");
        }
        // Node Publish module, leaderboard Safe Guard & Retry
        private static async Task<T> RetryAsync<T>(Func<Task<T>> action, int maxAttempts = 3, int delayMs = 2000)
        {
            Exception? lastEx = null;
            for (int i = 1; i <= maxAttempts; i++)
            {
                try
                {
                    return await action();
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    Console.WriteLine($"[WARN] Attempt {i}/{maxAttempts} failed: {ex.Message}");
                    await Task.Delay(delayMs * i); // backoff
                }
            }
            throw new InvalidOperationException($"[DEBUG] Operation failed after {maxAttempts} attempts", lastEx!);
        }
        // ==================== Start Linera Service Automatic ==================== 
        // Wrapper để gọi LineraCliRunner.StartLineraServiceInBackgroundAsync
        public async Task<int> StartLineraServiceAsync(int port = 8080)
        {
            // prevent concurrent start/stop
            await _serviceSemaphore.WaitAsync();
            try
            {
                if (string.IsNullOrEmpty(_config.LineraWallet) || string.IsNullOrEmpty(_config.LineraStorage))
                    throw new InvalidOperationException("[DEBUG] LINERA_WALLET or LINERA_STORAGE not set. Call StartLineraNetAsync() first.");

                // If PID exists and process alive, do nothing
                if (_config.LineraServicePid.HasValue)
                {
                    try
                    {
                        var existing = Process.GetProcessById(_config.LineraServicePid.Value);
                        if (!existing.HasExited)
                        {
                            Console.WriteLine($"[DEBUG] Linera service already running (PID {_config.LineraServicePid}).");
                            return _config.LineraServicePid.Value;
                        }
                        else
                        {
                            // process exited, clear to allow restart
                            _config.LineraServicePid = null;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // process not found
                        _config.LineraServicePid = null;
                    }
                }

                // If any stray pid existed, try to ensure RocksDB freed
                await Task.Delay(300);

                // start new service
                int pid = await _cli.StartLineraServiceInBackgroundAsync(port);
                _config.LineraServicePid = pid;
                Console.WriteLine($"[DEBUG] Started Linera service (PID {pid}). Logs: /tmp/linera_service.log");

                // attach exit handler so monitor gets faster reaction
             
                return pid;
            }
            finally
            {
                _serviceSemaphore.Release();
            }
        }
        // Stop All Node + Service
        public async Task StopAllLineraAsync(int waitMs = 500)
        {
            Console.WriteLine("Stopping ALL Linera processes...");
            try
            {
                // stop monitor first so it won't restart service while we shutdown
                await StopServiceMonitorAsync();

                foreach (var proc in Process.GetProcessesByName("linera"))
                {
                    try
                    {
                        Console.WriteLine($"Killing Linera PID={proc.Id}, Start={proc.StartTime}");
                        proc.Kill(entireProcessTree: true); // ASP.NET 5+ có hỗ trợ
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WARN] Failed to kill PID={proc.Id}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] StopAllLineraAsync: {ex.Message}");
            }

            _config.LineraServicePid = null;
            _config.LineraNetPid = null;

            // đợi RocksDB unlock
            await Task.Delay(waitMs);
            Console.WriteLine("[DEBUG] RocksDB Unlocked - All Linera processes stopped.");
        }

        private bool IsLineraServiceRunning
        {
            get
            {
                if (!_config.LineraServicePid.HasValue) return false;
                try
                {
                    var process = Process.GetProcessById(_config.LineraServicePid.Value);
                    return !process.HasExited;
                }
                catch
                {
                    return false; // process không tồn tại
                }
            }
        }
        // Helper to call to Unity and Get Linera Service Status Text
        public int? GetServicePid() => _config.LineraServicePid;
        public bool IsServiceRunning()
        {
            if (!_config.LineraServicePid.HasValue) return false;
            try
            {
                var pid = _config.LineraServicePid.Value;
                var proc = Process.GetProcessById(pid);
                Console.WriteLine($"[LINERA-ORCH] Service running with PID={pid}, StartTime={proc.StartTime}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LINERA-ORCH] Error checking service: {ex.Message}");
                return false;
            }
        }
        // Service Guard Monitor (watchdog) implementation 
        private void StartServiceMonitor()
        {
            lock (_serviceMonitorLock)
            {
                if (_serviceMonitorTask != null && !_serviceMonitorTask.IsCompleted)
                {
                    Console.WriteLine("[MONITOR] Service monitor already running.");
                    return;
                }

                _serviceMonitorCts = new CancellationTokenSource();
                var token = _serviceMonitorCts.Token;

                _serviceMonitorTask = Task.Run(async () =>
                {
                    int consecutiveFailures = 0;
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            if (!IsLineraServiceRunning)
                            {                           
                                Console.WriteLine("[MONITOR] Linera service not running. Attempting restart...");
                                try
                                {
                                    await StartLineraServiceAsync();
                                    consecutiveFailures = 0; // reset on success
                                    // small pause after successful start
                                    await Task.Delay(1500, token);
                                }
                                catch (Exception startEx)
                                {
                                    consecutiveFailures++;
                                    var backoffMs = Math.Min(30000, 1000 * (int)Math.Pow(2, Math.Min(6, consecutiveFailures)));
                                    Console.WriteLine($"[MONITOR] Restart attempt failed: {startEx.Message}. Backoff {backoffMs}ms");
                                    try { await Task.Delay(backoffMs, token); } catch { /* canceled */ }
                                }
                            }
                            else
                            {
                                // healthy: check again later
                                await Task.Delay(2000, token);
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[MONITOR] Unexpected monitor error: {ex.Message}");
                            try { await Task.Delay(2000, token); } catch { break; }
                        }
                    }
                    Console.WriteLine("[MONITOR] Service monitor stopped.");
                }, token);
            }
        }
        private async Task StopServiceMonitorAsync(int waitMs = 5000)
        {
            // first request cancellation
            lock (_serviceMonitorLock)
            {
                if (_serviceMonitorCts != null && !_serviceMonitorCts.IsCancellationRequested)
                {
                    _serviceMonitorCts.Cancel();
                }
            }

            // wait for monitor task to finish (with timeout)
            Task? monitorTaskCopy = null;
            lock (_serviceMonitorLock)
            {
                monitorTaskCopy = _serviceMonitorTask;
            }

            if (monitorTaskCopy != null)
            {
                await Task.WhenAny(monitorTaskCopy, Task.Delay(waitMs));
            }

            // final cleanup
            lock (_serviceMonitorLock)
            {
                try { _serviceMonitorCts?.Dispose(); } catch { }
                _serviceMonitorTask = null;
                _serviceMonitorCts = null;
            }
        }
        private async Task EnsureServiceRunningAsync() // Đảm bảo service bật thì mới thực hiện lệnh submit, leaderboard
        {
            if (!IsLineraServiceRunning)
            {
                Console.WriteLine("[INFO] Linera service not running. Starting...");
                await StartLineraServiceAsync();
                await Task.Delay(1500);
            }
        }
        private async Task<T> RunWithLineraServiceAsync<T>(Func<Task<T>> operation)
        {
            await EnsureServiceRunningAsync();
            return await operation();
        }
        #endregion

        #region Linera open chain under contract
        // [NEW] Unified open+create with service stop/start
        // Open-and-create: chỉ một process chạy tại một thời điểm
        // Step 1: If multiple requests arrive, Multiple [QUEUE] open-and-create requests queued,
        // Step 2: Add log as request ID (eg: Guid.NewGuid().ToString("N").Substring(0,6)) to easily track each request
        // Put Linera service in Queue update 3 July 25
        // Poll theo 2 bước:  B1: Resolve chainId từ PublisherChain bằng requestId.B2: Dùng chainId đó query service trên chain mới để lấy appId. 12 Aug 25 
        // Call contract mutation 17 Sep25
        // TODO 1: QUEUE Open-and-create under 5-10 ms
        // TODO 2: Dùng HttpClient tái sử dụng (không new per request) với MaxConnectionsPerServer cao hơn.
        // TODO 3: Nếu muốn: trả 429 từ controller khi đang bận (thay vì chấp nhận và fail).
        // TODO 4: Reduce race when 10-20 request open-and-create at the same time with reuse HttpClient singleton
        // TODO 5: Task channel T RunContinuationsAsynchronously Mỗi request (open chain, submit match) không chạy ngay → được enqueue dưới dạng Func<Task>.

        public Task<(string ChainId, string? AppId)> OpenAndCreateWithServiceControlAsync( string? Player1, string? Player2)
        {
            var tcs = new TaskCompletionSource<(string, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);

            _mutationChannel.Writer.TryWrite(async () =>
            {
                try
                {
                    var (chainId, appId) = await OpenAndCreateOnContractAsync();

                    // Save mappingW
                    lock (_matchMap)
                    {
                        _matchMap[chainId] = new MatchMapping
                        {
                            MatchId = chainId,
                            ChainId = chainId,
                            AppId = appId,
                            Player1 = Player1,
                            Player2 = Player2,
                            Status = "created",
                            SubmittedAt = DateTime.UtcNow.ToString("O")
                        };
                        SaveMatchMapping();
                    }

                    tcs.SetResult((chainId, appId));
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }
        private readonly HashSet<string> _knownChains = [];

        /// Gửi mutation openAndCreate, sau đó poll resolveRequest tới khi có chainId mới
        private async Task<(string ChainId, string? AppId)> OpenAndCreateOnContractAsync()
        {
            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.XFighterAppId}";

            // Snapshot allOpenedChains → build existing.
            var seedPayload = new { query = "query { allOpenedChains }" };
            using var ctsSeed = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var seedResp = await _httpClient.PostAsync(url,
                 new StringContent(JsonSerializer.Serialize(seedPayload), Encoding.UTF8, "application/json"),
                 ctsSeed.Token);
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

            // Tạo placeholder mapping (key = requestId, status = "creating") và SaveMatchMapping().
            var requestId = Guid.NewGuid().ToString("N")[..8];
            lock (_matchMap)
            {
                _matchMap[requestId] = new MatchMapping
                {
                    ChainId = null,
                    AppId = null,

                    Status = "creating",
                    SubmittedAt = DateTime.UtcNow.ToString("O")
                };
                SaveMatchMapping(); // ensure persisted
            }

            //Gọi PostSingleWithServiceWaitAsync — trước khi gửi, đợi monitor báo service có PID ổn định.
            var graphql = @"mutation { openAndCreate }";
            var payload = new { query = graphql };
            HttpResponseMessage resp;
            try
            {
                resp = await PostSingleWithServiceWaitAsync(url, () =>
                    new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                    waitSeconds: 8, postTimeoutSeconds: 30);
            }
            catch (Exception ex)
            {
                // mark creating -> create failed
                // Nếu gửi thất bại: set mapping -> "create failed", thay từ lock (_matchMap) sang UpdateMatchStatus cho giống submit
                UpdateMatchStatus(requestId, "create failed");

                // Sau khi fail: đợi Monitor báo service ổn định rồi mới cho queue tiếp tục  (điều này chặn queue vì job đang await).
                Console.WriteLine("[INFO] Waiting for Linera service to recover before continuing...");
                await WaitForServiceViaMonitorAsync(timeoutSeconds: 20, pollMs: 500, stableMs: 1000);
                Console.WriteLine($"[CLEANUP] Reactive cleanup create mapping triggered at {DateTime.UtcNow:O}");
                throw new InvalidOperationException("openAndCreate failed after waiting for service: " + ex.Message, ex);
                
            }
            var text = await resp.Content.ReadAsStringAsync();

            //2. Poll allOpenedChains vài lần để phát hiện chain mới so với existing + _knownChains.
            const int maxAttempts = 5;
            string? chainId = null;
            var pollPayload = new { query = "query { allOpenedChains }" };

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                await Task.Delay(500); // give service time
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
                        lock (_knownChains)
                        {
                            if (!_knownChains.Contains(cid) && !existing.Contains(cid))
                            {
                                _knownChains.Add(cid);
                                chainId = cid;
                                Console.WriteLine($"[FOUND] New opened chain -> {chainId}");
                                Console.WriteLine($"[DEBUG] Chain creation detected after {attempt} attempt(s).");
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to parse allOpenedChains response (attempt {attempt}): {ex.Message}");
                    Console.WriteLine($"[DEBUG] Raw response: {pollText}");
                }

                if (chainId != null) break;
            }
            // khi poll không tìm thấy chainId (timeout), 
            if (chainId == null)
            { 
                UpdateMatchStatus(requestId, "create failed"); //thay từ lock (_matchMap) sang UpdateMatchStatus cho giống submit
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
            // khi thành công: Xoá placeholder, update mapping: creating -> created và lưu mapping chính keyed by chainId.
            lock (_matchMap)
            {
                _matchMap.Remove(requestId);
                _matchMap[chainId!] = new MatchMapping
                {
                    ChainId = chainId,
                    AppId = childAppId,
                    Status = "created",
                    SubmittedAt = DateTime.UtcNow.ToString("O")
                };
                SaveMatchMapping();
            }

            return (chainId!, childAppId);
        }
        
        #endregion

        #region SubmitMatchResultAsync 
        // Step1: Submit match result via GraphQL mutation recordScore
        // Step2:Gửi mutation recordScore with helper postgraphQL
        // Step3:[ NEW PATCH] SubmitMatchResultAsync – lookup mapping nếu thiếu chainId/appId
        // Step4: Enforce single-result per matchId rước khi gửi mutation, kiểm tra trạng thái mapping:
        // nếu _matchMap[matchId].status == "submitted" => trả lỗi 400 (“match already submitted”).
        // Step5:lock(_matchMap) để đảm bảo check-and-set atomic trên dictionary đơn giản, hiệu quả cho quy mô nhỏ ***
        // Trạng thái chu trình: "created" → "submitting" → "submitted"/"failed".
        // SubmittedAt lưu ISO UTC (DateTime.UtcNow.ToString("o")) dễ đọc và parse
        // TODO 2: EnsureServiceRunningAsync – fixed Task return & wait Đảm bảo service bật khi cần Run operation (SubmitMatchResultAsync, GetLeaderboardDataAsync) under Linera service
        // TODO 3:insurance – overhead chỉ thêm 1 check PID vì Linera service có thể die khi dev/test
        // TODO 4:Task channel T RunContinuationsAsynchronously Mỗi request (open chain, submit match) không chạy ngay → được enqueue dưới dạng Func<Task>.
        public Task<string> SubmitMatchResultAsync(string? chainId, MatchResult matchResult)
        {
            var tcs = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _mutationChannel.Writer.TryWrite(async () =>
            {
                try
                {
                    var result = await SubmitMatchResultCoreAsync(chainId, matchResult);
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }
        public async Task<string> SubmitMatchResultCoreAsync(string? chainId, MatchResult matchResult)
        {
            /// <summary>
            /// START NEW step3 Nếu thiếu chainId hoặc appId, tra từ matchId
            /// 1. Resolve chain/app from mapping if missing
            /// 2. Atomic check-and-set status
            /// 3. After post GraphQL, update Leaderboard -> cập nhật mapping sang submitted / failed
            /// </summary>
            Console.WriteLine($"[REQUEST]: Submit Match started at {DateTime.UtcNow:O}");
            // Resolve matchResult.MatchId -> chainId
            if (string.IsNullOrWhiteSpace(chainId) &&
                !string.IsNullOrWhiteSpace(matchResult.MatchId))
            {
                // vì matchId = chainId -> lấy mapping trực tiếp
                if (_matchMap.TryGetValue(matchResult.MatchId, out var pair))
                {
                    chainId = pair.ChainId;
                    Console.WriteLine($"[MAP] Resolved chainId for matchId={matchResult.MatchId}: {chainId}");
                }
            }

            // Nếu thiếu chainId, tra từ mapping
            if (string.IsNullOrWhiteSpace(chainId))
                throw new ArgumentNullException(nameof(chainId));
            ArgumentNullException.ThrowIfNull(matchResult);



            string appId;
            lock (_matchMap)
            {
                if (!_matchMap.TryGetValue(chainId, out var existing))
                    throw new InvalidOperationException(
                        $"No mapping found for chain {chainId}. use open and create chain before submit .");

                if (string.Equals(existing.Status, "submitted", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Match on chain {chainId} has already been submitted (status=submitted).");

                if (string.IsNullOrWhiteSpace(existing.AppId))
                    throw new InvalidOperationException(
                        $"Mapping for chain {chainId} missing AppId. Sai flow open-and-create.");

                if (string.Equals(existing.Status, "creating", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Match {chainId} is still creating. Please retry later.");

                appId = existing.AppId;

                // update trạng thái, KHÔNG ghi đè AppId
                existing.MatchId = matchResult.MatchId;
                existing.Status = "submitting";
                existing.SubmittedAt = DateTime.UtcNow.ToString("O");

                SaveMatchMapping();
            }

            // Prevent duplicate submission: atomic check & set using lock on _matchMap
            var matchKey = chainId; // dùng chainId làm key duy nhất
            string? opHex = null;
            string text = string.Empty;
            // END NEW step3 Nếu thiếu chainId tra từ matchId
            // MAPPING: MatchID → ChainID
            // Execute the GraphQL mutation under service-run wrapper (ensures Linera service is up)
            await RunWithLineraServiceAsync(async () =>
            {  
                var url = $"http://localhost:8080/chains/{chainId}/applications/{appId}";
                var graphql = @"
                    mutation recordScore($matchResult: MatchResultInput!) {
                        recordScore(matchResult: $matchResult)
                    }
                ";

                var payload = new { query = graphql, variables = new { matchResult } };
                //CamelCase để property names match với GraphQL (matchId, player, score)
                var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write),Encoding.UTF8,"application/json");

                // ***mở rộng MatchMapping để lưu trạng thái status/ submittedAt / submittedOpId***
                HttpResponseMessage resp = null!;
                try
                {
                    // Use monitor-based single-send helper so we rely on monitor/PID
                    resp = await PostSingleWithServiceWaitAsync(
                        url,
                        () => new StringContent(
                            JsonSerializer.Serialize(payload, JsonOptions.Write),Encoding.UTF8,"application/json"),
                        waitSeconds: 8,
                        postTimeoutSeconds: 30).ConfigureAwait(false);

                    text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    // 1) HTTP non-2xx -> fail
                    if (!resp.IsSuccessStatusCode)
                    {
                        // mark mapping failed
                        UpdateMatchStatus(chainId, "submit failed");

                        throw new InvalidOperationException($"HTTP {resp.StatusCode}: {text}");
                    }
                }
                catch (OperationCanceledException)
                {
                    UpdateMatchStatus(chainId, "submit failed");

                    // Pause the queue until monitor reports service stable (same behavior as open-and-create)
                    Console.WriteLine("[INFO] Submit failed due to timeout. Waiting for Linera service to recover before continuing...");
                    await WaitForServiceViaMonitorAsync(timeoutSeconds: 20, pollMs: 500, stableMs: 1000)
                        .ConfigureAwait(false);
                    throw new TimeoutException("Request to linera service timed out.");
                }
                catch (Exception ex)
                {
                    // mark mapping as failed if relevant
                    UpdateMatchStatus(chainId, "submit failed");

                    // Pause the queue until monitor reports service stable (same behavior as open-and-create)
                    Console.WriteLine($"[INFO] Submit failed: {ex.Message}. Waiting for Linera service to recover before continuing...");
                    await WaitForServiceViaMonitorAsync(timeoutSeconds: 20, pollMs: 500, stableMs: 1000)
                        .ConfigureAwait(false);

                    throw new InvalidOperationException($"HTTP request failed: {ex.Message}", ex);
                }
                // ***NEW MAPPING  need state so orchestrator knows match has been submitted
                // (prevent duplicate) and save opId for debugging/tracing ***
                // ---- parse op hex from response ----
                // Parse opId
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("data", out var dataEl))
                    {
                        if (dataEl.ValueKind == JsonValueKind.String)
                        {
                            opHex = dataEl.GetString(); // trường hợp Linera trả về string
                        }
                        else if (dataEl.ValueKind == JsonValueKind.Object &&
                                 dataEl.TryGetProperty("recordScore", out var rs) &&
                                 rs.ValueKind == JsonValueKind.String)
                        {
                            opHex = rs.GetString(); // trường hợp Linera trả về object { recordScore: "..."}
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Parse op hex failed: {ex.Message}");
                }
                Console.WriteLine($"[DEBUG] Successfully Submitted match on chainId {chainId}.");

                // Update mapping submitted
                lock (_matchMap)
                {
                    if (_matchMap.TryGetValue(matchKey, out var m))
                    {
                        m.Status = "submitted";
                        m.SubmittedOpId = string.IsNullOrWhiteSpace(opHex) ? null : opHex;
                        m.SubmittedAt = DateTime.UtcNow.ToString("o");
                        SaveMatchMapping();
                    }
                }

                // Tracking History Update player index cho cả 2 player
                AddPlayerIndex(
                    matchResult.Player1Username,
                    chainId,
                    isWinner: string.Equals(matchResult.WinnerUsername, matchResult.Player1Username, StringComparison.OrdinalIgnoreCase),
                    isLoser: string.Equals(matchResult.LoserUsername, matchResult.Player1Username, StringComparison.OrdinalIgnoreCase)
                );
                AddPlayerIndex(
                    matchResult.Player2Username,
                    chainId,
                    isWinner: string.Equals(matchResult.WinnerUsername, matchResult.Player2Username, StringComparison.OrdinalIgnoreCase),
                    isLoser: string.Equals(matchResult.LoserUsername, matchResult.Player2Username, StringComparison.OrdinalIgnoreCase)
                );
                SavePlayerIndex();

                return true; // return raw service response (text)
            }).ConfigureAwait(false);

            Console.WriteLine($"[REQUEST]: Submit Match finished at {DateTime.UtcNow:O}");
            Console.WriteLine($"[DEBUG] Start waiting for leaderboard update for match {matchResult.MatchId}");
            await Task.Delay(500).ConfigureAwait(false);

            // --- Đợi leaderboard cập nhật ---
            bool verified = await WaitForLeaderboardUpdateAsync(matchResult.Player1Username,matchResult.Player2Username,timeoutMs: 5000).ConfigureAwait(false);
            Console.WriteLine($"[DEBUG] Leaderboard verify result for match {matchResult.MatchId}: {verified}");
            // Trả về JSON gồm cả kết quả verify
            return JsonSerializer.Serialize(new
            {
                success = true,
                matchId = matchResult.MatchId,
                chainId,
                opId = opHex,
                verified,
                raw = text
            });
        }

        // Helper: update status
        private static void UpdateMatchStatus(string matchKey, string status)
        {
            if (string.IsNullOrWhiteSpace(matchKey)) return;
            lock (_matchMap)
            {
                if (_matchMap.TryGetValue(matchKey, out var m))
                {
                    m.Status = status;
                    m.SubmittedAt = DateTime.UtcNow.ToString("o");
                    SaveMatchMapping();
                }
            }
        }
        // Helper: DEBUG wait leaderboard confirm after submit
        private async Task<bool> WaitForLeaderboardUpdateAsync(
            string player1,
            string player2,
            int timeoutMs = 2000,
            int pollIntervalMs = 1000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    var json = await GetLeaderboardDataAsync();
                    Console.WriteLine($"[DEBUG] Polling & Searching leaderboard… elapsed {sw.ElapsedMilliseconds} ms");
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("leaderboard", out var lb) &&
                        lb.ValueKind == JsonValueKind.Array)
                    {
                        bool p1Found = false, p2Found = false;
                        foreach (var e in lb.EnumerateArray())
                        {
                            var uid = e.GetProperty("userId").GetString();
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

        #region Get data leaderboard request graphQL api
        // GraphQL string escape helper Utility helpers
        // Get leaderboard via GraphQL query (use publisher chain + leaderboard app)
        //TODO - Automatic Guard Leaderboard
        //1.create Cache for leaderboard -> private string? _latestLeaderboard;
        //2.private DateTime _lastRefreshTime = DateTime.MinValue;
        //3.private volatile bool _isBusy = false; // guard
        //4.public bool IsBusy() => _isBusy;
        public async Task<string> GetLeaderboardDataAsync()
        {
            try
            {
                return await RunWithLineraServiceAsync(async () =>
                {
                    var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.LeaderboardAppId}";
                    // using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // client -> reuse HttpClient singleton

                    var graphql = @"
                            query {
                                leaderboard {
                                    userId
                                    score
                                    totalMatches
                                    totalWins
                                    totalLosses
                                }
                            }";

                    var payload = new { query = graphql, variables = new { } };
                    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                    HttpResponseMessage resp;
                    string text;
                    try
                    {
                        resp = await _httpClient.PostAsync(url, content, cts.Token); // client -> reuse HttpClient singleton
                        text = await resp.Content.ReadAsStringAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        throw new TimeoutException("GetLeaderboardDataAsync timed out.");
                    }

                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException($"HTTP {resp.StatusCode} from linera service: {text}");

                    return text;
                });
            }
            finally
            {
                Console.WriteLine($"[REQUEST]: Sync & Get Data Leaderboard API at {DateTime.UtcNow:O}");
            }
        }
        #endregion

        #region MatchMapping persistence ánh xạ MAPPING: MatchID → ChainID  & history match data of players // Snapshot leaderboard
        // Logic Mapping in-memory + persist trong Orchestrator Vì chainID/appID không phải cố định – mỗi trận là một cặp mới.
        // Khi Unity nhấn "Submid Record Match" (button)
        // Nếu Unity gửi đầy đủ chainId và appId, orchestrator dùng luôn(không cần lookup).
        // Nếu Unity chỉ gửi matchId, orchestrator tra từ _matchMap để tìm ra chainId/appId.
        // Nếu không lưu ánh xạ này, khi submit sẽ không biết nên gửi vào chain nào → kết quả có thể bị gửi sai nơi
        private static readonly Dictionary<string, MatchMapping> _matchMap = [];
        private static readonly string _matchMappingFile =
            Environment.GetEnvironmentVariable("MATCH_MAPPING_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "linera_orchestrator", "match_mapping.json");
        private static bool SaveMatchMapping()
        {
            try
            {
                lock (_matchMap)// giữ lock trên map để trạng thái consistent
                {
                    // dùng serialize cache JsonOptions thay vì new mỗi lần
                    var json = JsonSerializer.Serialize(_matchMap, JsonOptions.Write);

                    // ensure directory exists
                    var dir = Path.GetDirectoryName(_matchMappingFile);
                    if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var tmp = _matchMappingFile + ".tmp";
                    File.WriteAllText(tmp, json);

                    // New atomic replace
                    if (File.Exists(_matchMappingFile))
                        File.Replace(tmp, _matchMappingFile, null); // atomic replace
                    else
                        File.Move(tmp, _matchMappingFile); // first time

                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Failed to save match mapping: {ex.Message}");
                return false;
            }
        }
        private static void LoadMatchMapping()
        {
            lock (_matchMap)
            {
                try
                {
                    if (!File.Exists(_matchMappingFile))
                    {
                        Console.WriteLine("[INFO] Match mapping file not found. Initializing empty map.");
                        _matchMap.Clear();
                        return;
                    }

                    if (new FileInfo(_matchMappingFile).Length == 0)
                    {
                        Console.WriteLine("[WARN] Match mapping file is empty. Initializing empty map.");
                        _matchMap.Clear();
                        return;
                    }

                    var json = File.ReadAllText(_matchMappingFile);
                    var data = JsonSerializer.Deserialize<Dictionary<string, MatchMapping>>(json, JsonOptions.Read);

                    _matchMap.Clear();
                    if (data != null)
                    {
                        foreach (var kv in data)
                        {
                            var map = kv.Value;
                            var key = string.IsNullOrWhiteSpace(map.ChainId) ? kv.Key : map.ChainId!;
                            _matchMap[key] = map;
                        }
                    }

                    Console.WriteLine($"[INFO] Successfully loaded {_matchMap.Count} match mappings.");
                }
                catch (Exception ex)
                {
                    File.Copy(_matchMappingFile, _matchMappingFile + ".bak", overwrite: true); // Lưu vào file hỏng để debug
                    Console.WriteLine($"[WARN] Failed to load match mapping: {ex.Message}");
                    _matchMap.Clear();
                }
            }
        }
        //DEBUG
        public MatchMapping? GetMappingForChain(string chainId)
        {
            if (string.IsNullOrWhiteSpace(chainId)) return null;
            return _matchMap.TryGetValue(chainId, out var mapping) ? mapping : null;
        }
        //DEBUG
        public Dictionary<string, MatchMapping> GetAllMappings()
        {
            // return a copy to avoid external modification
            lock (_matchMap) return new Dictionary<string, MatchMapping>(_matchMap);
        }

        // SAVE - LOAD - ADD Player To Tracking
        // giả sử có _playerIndex: Dictionary<string, HashSet<string>> lưu player -> set(chainId)
        private static readonly Dictionary<string, PlayerStats> _playerIndex = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string _playerIndexFile =
        Environment.GetEnvironmentVariable("PLAYER_INDEX_PATH")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "linera_orchestrator", "player_index.json");
        /// <summary>Load player index from disk into _playerIndex. Safe to call at startup.</summary>
        private static void LoadPlayerIndex()
        {
            lock (_playerIndex)
            {
                try
                {
                    if (!File.Exists(_playerIndexFile))
                    {
                        Console.WriteLine("[INFO] Player index file not found. Initializing empty player index.");
                        _playerIndex.Clear();
                        return;
                    }

                    var fileInfo = new FileInfo(_playerIndexFile);
                    if (fileInfo.Length == 0)
                    {
                        Console.WriteLine("[WARN] Player index file is empty. Initializing empty player index.");
                        _playerIndex.Clear();
                        return;
                    }

                    var json = File.ReadAllText(_playerIndexFile);
                    // Read again
                    var data = JsonSerializer.Deserialize<Dictionary<string, PlayerStats>>(json, JsonOptions.Read);

                    _playerIndex.Clear();
                    if (data != null)
                    {
                        foreach (var kv in data)
                        {
                            kv.Value.Chains ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            _playerIndex[kv.Key] = kv.Value;
                        }
                    }
                    Console.WriteLine($"[INFO] Loaded {_playerIndex.Count} player entries.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to load player index: {ex.Message}");
                    _playerIndex.Clear();
                }
            }
        }
        /// <summary>Save _playerIndex to disk using atomic replace.</summary>
        private static bool SavePlayerIndex()
        {
            try
            {
                lock (_playerIndex)
                {
                    var dir = Path.GetDirectoryName(_playerIndexFile);
                    if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var json = JsonSerializer.Serialize(_playerIndex, JsonOptions.Write);
                    var tmp = _playerIndexFile + ".tmp";

                    File.WriteAllText(tmp, json);
                    if (File.Exists(_playerIndexFile)) File.Delete(_playerIndexFile);
                    File.Move(tmp, _playerIndexFile);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Failed to save player index: {ex.Message}");
                return false;
            }
        }
        private static void AddPlayerIndex(string username, string chainId, bool isWinner = false, bool isLoser = false)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(chainId)) return;

            lock (_playerIndex)
            {
                if (!_playerIndex.TryGetValue(username, out var stats))
                {
                    stats = new PlayerStats();
                    _playerIndex[username] = stats;
                }

                // thêm chainId vào danh sách
                if (stats.Chains.Add(chainId))
                {
                    stats.TotalMatches++;

                    if (isWinner) stats.Wins++;
                    if (isLoser) stats.Losses++;

                    stats.LastPlayed = DateTime.UtcNow.ToString("s");

                    var ok = SavePlayerIndex();
                    if (!ok)
                    {
                        Console.WriteLine($"[WARN] SavePlayerIndex() failed after adding {username}:{chainId}");
                    }
                }
            }
        }

        // SAVE - LOAD - ADD Snapshot Leaderboard for Tournament
        private static readonly string _snapshotFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "linera_orchestrator", "snapshot_leaderboard.json");

        private static readonly object _snapshotLock = new();
        private static LeaderboardSnapshot? _currentSnapshot;
        public static bool SaveLeaderboardSnapshot(LeaderboardSnapshot snapshot)
        {
            lock (_snapshotLock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_snapshotFile);
                    if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var json = JsonSerializer.Serialize(snapshot, JsonOptions.Write);
                    File.WriteAllText(_snapshotFile, json);

                    // verify immediately
                    if (File.Exists(_snapshotFile))
                    {
                        var verify = new FileInfo(_snapshotFile);
                        Console.WriteLine($"[SNAPSHOT] Saved snapshot to: {_snapshotFile} ({verify.Length} bytes)");
                        _currentSnapshot = snapshot;
                        return true;
                    }

                    Console.WriteLine($"[ERROR] Snapshot file not found after save: {_snapshotFile}");
                    return false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed to save snapshot: {ex.Message}");
                    return false;
                }
            }
        }
        public static LeaderboardSnapshot? LoadLeaderboardSnapshot()
        {
            lock (_snapshotLock)
            {
                try
                {
                    if (!File.Exists(_snapshotFile))
                        return null;

                    var json = File.ReadAllText(_snapshotFile);
                    _currentSnapshot = JsonSerializer.Deserialize<LeaderboardSnapshot>(json, JsonOptions.Read);
                    return _currentSnapshot;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to load snapshot: {ex.Message}");
                    return null;
                }
            }
        }
        #endregion

        #region All match result - Tracking history match player
        /// <summary> 
        /// Lấy 20 trận gần nhất của player (thắng/thua, timestamp) theo username.
        /// Save - Load - Add data moving to Mapping section
        /// </summary>
        public async Task<List<PlayerMatchEntry>> GetPlayerHistoryAsync(string username, int limit = 20)
        {
            var result = new List<PlayerMatchEntry>();
            if (string.IsNullOrWhiteSpace(username)) return result;

            // lấy chainId từ _playerIndex
            HashSet<string>? chains;
            lock (_playerIndex)
            {
                if (!_playerIndex.TryGetValue(username, out var stats) || stats?.Chains == null || stats.Chains.Count == 0)
                    return result;
                chains = [.. stats.Chains]; // copy để tránh bị thay đổi khi đang dùng
            }

            var sem = new SemaphoreSlim(5);
            var tasks = chains.Select(async chainId =>
            {
                await sem.WaitAsync();
                try
                {
                    if (!_matchMap.TryGetValue(chainId, out var map)) return (List<PlayerMatchEntry>?)null;
                    var appId = map.AppId;
                    if (string.IsNullOrWhiteSpace(appId)) return null;

                    var url = $"http://localhost:8080/chains/{chainId}/applications/{appId}";
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // client -> reuse HttpClient singleton
                    var gql = new
                    {
                        query = @"{ allMatchResults {
                            matchId player1Username player2Username 
                            winnerUsername loserUsername 
                            player1Score player2Score
                            mapName matchType timestamp afk 
                            }}"
                    };

                    var content = new StringContent(JsonSerializer.Serialize(gql), Encoding.UTF8, "application/json");
                    var resp = await _httpClient.PostAsync(url, content, cts.Token);  // client -> reuse HttpClient singleton
                    if (!resp.IsSuccessStatusCode) return null;

                    var text = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(text);
                    if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
                    if (!data.TryGetProperty("allMatchResults", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;

                    var list = new List<PlayerMatchEntry>();
                    foreach (var e in arr.EnumerateArray())
                    {
                        var p1 = e.GetProperty("player1Username").GetString();
                        var p2 = e.GetProperty("player2Username").GetString();
                        if (p1 == username || p2 == username)
                        {
                            var mr = JsonSerializer.Deserialize<MatchResult>(
                                 e.GetRawText(),
                                 JsonOptions.Read
                             );
                            if (mr != null)
                            {
                                // DO NOT mutate MatchResult; wrap it with ChainId for response
                                list.Add(new PlayerMatchEntry
                                {
                                    ChainId = chainId,
                                    MatchResult = mr
                                });
                            }
                        }
                    }
                    return list;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    sem.Release();
                }
            }).ToList();  // List<Task<List<PlayerMatchEntry>?>>

            var res = await Task.WhenAll(tasks); // res is List<List<PlayerMatchEntry>?>[]
            foreach (var list in res)
            {
                if (list != null) result.AddRange(list);
            }

           // sort theo timestamp mới nhất, lấy tối đa 'limit'
            var ordered = result
                .OrderByDescending(entry =>
                {
                    try
                    {
                        long ts = entry.MatchResult.Timestamp; // ép sang long
                        // Timestamp may be seconds or milliseconds: handle both
                        if (ts > 9999999999L) // > ~10 digits => milliseconds
                            return DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime;
                        return DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
                    }
                    catch
                    {
                        return DateTime.MinValue;
                    }
                })
                .Take(limit)
                .ToList();

            return ordered;
        }
        /// <summary>
        /// Lấy danh sách tất cả matchResult trong 1 chain cụ thể.
        /// </summary>
        public async Task<List<MatchResult>> GetMatchesByChainAsync(string chainId)
        {
            var result = new List<MatchResult>();
            if (string.IsNullOrWhiteSpace(chainId)) return result;

            if (!_matchMap.TryGetValue(chainId, out var map) || string.IsNullOrWhiteSpace(map.AppId))
                return result;

            var url = $"http://localhost:8080/chains/{chainId}/applications/{map.AppId}";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); // client -> reuse HttpClient singleton
            // client -> reuse HttpClient singleton
            var gql = new
            {
                query = @"{ allMatchResults {
                    matchId player1Username player2Username 
                    winnerUsername loserUsername 
                    player1Score player2Score 
                    mapName matchType timestamp afk 
                }}"
            };

            var content = new StringContent(JsonSerializer.Serialize(gql), Encoding.UTF8, "application/json");
            var resp = await _httpClient.PostAsync(url, content, cts.Token); // client -> reuse HttpClient singleton
            // client -> reuse HttpClient singleton
            if (!resp.IsSuccessStatusCode) return result;

            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return result;
            if (!data.TryGetProperty("allMatchResults", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;

            foreach (var e in arr.EnumerateArray())
            {
                var mr = JsonSerializer.Deserialize<MatchResult>(
                    e.GetRawText(),
                    JsonOptions.Read
                );
                if (mr != null) result.Add(mr);
            }

            return result;
        }
        #endregion

        #region Helper WaitForServiceViaMonitorAsync  & PostSingleWithServiceWaitAsync
        // helper cơ bản: POST với retry/backoff và gọi EnsureServiceRunningAsync() nếu cần
        // Replace existing WaitForLineraServiceAsync and PostSingleWithServiceWaitAsync with these.
        // 1) Wait based on monitor PID first (fast & authoritative for your setup)
        // Wait for the monitor to report a running Linera service (via PID) and require a short stability window.
        // Rationale: rely on your monitor (PID) rather than /health or TCP.
        // Chỉ tin vào monitor/PID để biết service đã sẵn sàng
        private async Task ProcessMutationQueueAsync()
        {
            await foreach (var job in _mutationChannel.Reader.ReadAllAsync())
            {
                // WAIT: nếu Monitor không báo service đang chạy, chặn ở đây
                try
                {
                    // Đợi service ổn định trước khi chạy job
                    while (!await WaitForServiceViaMonitorAsync(timeoutSeconds: 10,pollMs: 300,stableMs: 500))
                    {
                        Console.WriteLine("[INFO] Service không ổn định, tiếp tục chờ...");
                        await Task.Delay(1000);
                    }
                    await job();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Mutation job failed: {ex}");
                }
            }
            // small throttle latency between jobs
            await Task.Delay(300);
        }
        public async Task<bool> WaitForServiceViaMonitorAsync(
            int timeoutSeconds = 10, int pollMs = 300, int stableMs = 500)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int? lastPid = null;
            var stableStart = DateTime.MinValue;

            while (sw.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
            {
                var pid = GetServicePid();
                if (pid.HasValue && pid.Value > 0)
                {
                    try
                    {
                        var proc = Process.GetProcessById(pid.Value);
                        if (!proc.HasExited)
                        {
                            if (lastPid != pid.Value)
                            {
                                lastPid = pid.Value;
                                stableStart = DateTime.UtcNow;
                            }
                            else if ((DateTime.UtcNow - stableStart).TotalMilliseconds >= stableMs)
                            {
                                return true; // đã ổn định
                            }
                        }
                        else
                        {
                            lastPid = null;
                            stableStart = DateTime.MinValue;
                        }
                    }
                    catch
                    {
                        lastPid = null;
                        stableStart = DateTime.MinValue;
                    }
                }

                await Task.Delay(pollMs).ConfigureAwait(false);
            }

            return false; // hết timeout mà service chưa ổn định
        }
        // 2) Single-shot post which uses monitor-based wait then sends once with fresh content
        private async Task<HttpResponseMessage> PostSingleWithServiceWaitAsync(
              string url,
              Func<HttpContent> contentFactory,
              int waitSeconds = 10,
              int postTimeoutSeconds = 30,
              int maxAttempts = 3)    // <-- mới: số lần thử (mặc định 3)
        {
            // 1) Đợi monitor báo service đã có PID ổn định (như trước)
            var ready = await WaitForServiceViaMonitorAsync(waitSeconds, pollMs: 300, stableMs: 500).ConfigureAwait(false);
            if (!ready)
                throw new InvalidOperationException("Linera service not ready after wait (monitor).");

            int attempt = 0;
            while (true)
            {
                attempt++;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(postTimeoutSeconds));
                // mỗi attempt phải tạo content mới vì HttpContent không thể reuse sau gửi
                using var content = contentFactory();

                try
                {
                    var resp = await _httpClient.PostAsync(url, content, cts.Token).ConfigureAwait(false);

                    // Nếu thành công trả luôn
                    if (resp.IsSuccessStatusCode)
                        return resp;

                    // Nếu service trả 503 thì đây rất có khả năng transient (service vừa crash/restarting)
                    if (resp.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < maxAttempts)
                    {
                        Console.WriteLine($"[WARN] POST to {url} returned 503 (attempt {attempt}/{maxAttempts}). Waiting monitor then retrying...");
                        // chờ monitor báo ổn định (nhỏ hơn) — cho service 1-2s để hoàn tất restart
                        await WaitForServiceViaMonitorAsync(timeoutSeconds: Math.Min(5, waitSeconds), pollMs: 200, stableMs: 300).ConfigureAwait(false);
                        // backoff nhỏ
                        await Task.Delay(150 * attempt).ConfigureAwait(false);
                        continue; // retry
                    }

                    // nếu không phải 503 hoặc đã hết attempt -> trả resp cho caller để xử lý (controller sẽ mark failed)
                    return resp;
                }
                catch (OperationCanceledException) // timeout
                {
                    if (attempt >= maxAttempts)
                    {
                        Console.WriteLine($"[ERROR] POST to {url} timed out after attempt {attempt}.");
                        throw; // bubble lên để caller xử lý (existing behavior)
                    }

                    Console.WriteLine($"[WARN] POST to {url} timed out on attempt {attempt}. Waiting monitor then retrying...");
                    await WaitForServiceViaMonitorAsync(timeoutSeconds: Math.Min(5, waitSeconds), pollMs: 200, stableMs: 300).ConfigureAwait(false);
                    await Task.Delay(150 * attempt).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex) // mạng/IO transient
                {
                    if (attempt >= maxAttempts)
                    {
                        Console.WriteLine($"[ERROR] POST to {url} failed after {attempt} attempts: {ex.Message}");
                        throw;
                    }

                    Console.WriteLine($"[WARN] POST to {url} exception on attempt {attempt}: {ex.Message}. Waiting monitor then retrying...");
                    await WaitForServiceViaMonitorAsync(timeoutSeconds: Math.Min(5, waitSeconds), pollMs: 200, stableMs: 300).ConfigureAwait(false);
                    await Task.Delay(150 * attempt).ConfigureAwait(false);
                    continue;
                }
            }
        }
        public static class JsonOptions
        {
            public static readonly JsonSerializerOptions Write = new()
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };

            public static readonly JsonSerializerOptions Read = new()
            {
                PropertyNameCaseInsensitive = true
            };
        }

        ///DEBUG helper test all chains created DEBUG
        public async Task<List<string>> GetAllOpenedChainsAsync(int port = 8080)
        {
            var url = $"http://localhost:{port}/chains/{_config.PublisherChainId}/applications/{_config.XFighterAppId}";
            // Query danh sách chain time out 3s-5s
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));//Reuse HttpClient singleton

            var payload = new { query = "query { allOpenedChains }" };
            var body = JsonSerializer.Serialize(payload, JsonOptions.Write);

            using var resp = await PostSingleWithServiceWaitAsync(
                url,
                () => new StringContent(body, Encoding.UTF8, "application/json"),
                waitSeconds: 4,
                postTimeoutSeconds: 8);

            var text = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[DEBUG] GetAllOpenedChains raw={text}");

            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("allOpenedChains", out var arr))
            {
                throw new InvalidOperationException("Invalid response from linera service when requesting allOpenedChains");
            }

            var result = new List<string>();
            foreach (var el in arr.EnumerateArray())
            {
                var cid = el.GetString();
                if (!string.IsNullOrWhiteSpace(cid)) result.Add(cid);
            }
            return result;
        }

        /// <summary>
        /// Dọn các mapping lỗi hoặc không hợp lệ.
        /// - Xoá các mục có status = "create failed" hoặc "submit failed"
        /// - Xoá các mục thiếu ChainId sau 1 phút (tránh xóa nhầm request đang tạo)
        /// - Không đụng vào status = "creating" (đang được xử lý)
        /// - Lưu file nếu có thay đổi.
        /// </summary>
        private static void CleanupFailedMappings()
        {
            lock (_matchMap)
            {
                var toRemove = _matchMap
                    .Where(kv =>
                        string.Equals(kv.Value.Status, "create failed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(kv.Value.Status, "submit failed", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(kv.Value.ChainId))
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var key in toRemove)
                {
                    _matchMap.Remove(key);
                    Console.WriteLine($"[CLEANUP] Removed failed mapping: {key}");
                }

                if (toRemove.Count > 0)
                {
                    Console.WriteLine($"[CLEANUP] Removed total {toRemove.Count} failed mappings.");
                    if (SaveMatchMapping())
                    {
                        // Tải lại ngay để sync in-memory + file
                        LoadMatchMapping();
                        Console.WriteLine("[CLEANUP] Mapping reloaded after save.");
                    }
                }
            }
        }
        #endregion

        #region Tournament
        public Task StartTournamentAsync()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _mutationChannel.Writer.TryWrite(async () =>
            {
                try
                {
                    await RunTournamentInternalAsync();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Tournament failed: {ex.Message}");
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }

        /// <summary>
        /// Logic mô phỏng tournament đầy đủ: tạo, record, chốt, query champion.
        /// </summary>
        private async Task RunTournamentInternalAsync()
        {
            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";
            Console.WriteLine($"[INFO] Starting tournament simulation on {url}");

            // Step 0: Snapshot leaderboard trước khi mở giải
            var snapshot = LoadLeaderboardSnapshot();
            if (snapshot == null)
            {
                Console.WriteLine("[WARN] Cannot start tournament without snapshot.");
                return;
            }

            if (snapshot.Tournament == null)
            {
                snapshot.Tournament = new TournamentMeta
                {
                    SnapshotId = snapshot.SnapshotId,
                    CreatedAt = DateTime.UtcNow.ToString("s"),
                    TopPlayers = [.. snapshot.TopPlayers],
                    Status = "Created",
                    Name = $"XFighter_Linera_Tournament002"
                };
            }
            // Mark tournament running and persist
            snapshot.Tournament.Status = "Running";
            var okRunSave = SaveLeaderboardSnapshot(snapshot);
            Console.WriteLine($"[SNAPSHOT] Marked tournament Running => saved={okRunSave}");

            Console.WriteLine($"[INFO] Using leaderboard snapshot {snapshot.SnapshotId} with top8:");
            Console.WriteLine("       " + string.Join(", ", snapshot.TopPlayers));

            // Step 1: Tạo giải đấu mới
            var create = new { query = "mutation { createTournament(name:\"XFighter_Linera_Week1\", startTime:0, endTime:999999) }" };
            await PostJsonAsync(url, create, "[STAGE] Tournament created.");

            // Step 2: Shuffle top8 (random bracket)
            var players = snapshot.TopPlayers.ToList();
            var rnd = new Random(BitConverter.ToInt32(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.Hash)).Take(4).ToArray()));
            var shuffled = players.OrderBy(_ => rnd.Next()).ToList();

            // 3️ Tứ kết (4 trận)
            var qf = new (string Id, string P1, string P2)[]
            {
                ("Q1", shuffled[0], shuffled[1]),
                ("Q2", shuffled[2], shuffled[3]),
                ("Q3", shuffled[4], shuffled[5]),
                ("Q4", shuffled[6], shuffled[7])
            };
            var winnersQ = await PlayRoundAsync(url, qf, "Quarterfinal", rnd);

            // 4️ Bán kết (2 trận)
            var sf = new (string Id, string P1, string P2)[]
            {
                ("S1", winnersQ[0], winnersQ[1]),
                ("S2", winnersQ[2], winnersQ[3])
            };
            var winnersS = await PlayRoundAsync(url, sf, "Semifinal", rnd);

            // 5️ Chung kết (1 trận)
            var final = ("F1", winnersS[0], winnersS[1]);
            var winner = rnd.Next(2) == 0 ? final.Item2 : final.Item3;
            var loser = winner == final.Item2 ? final.Item3 : final.Item2;

            var finalPayload = new
            {
                query = $"mutation {{ recordMatch(matchId:\"{final.Item1}\", winner:\"{winner}\", loser:\"{loser}\") }}"
            };

            var resp = await PostSingleWithServiceWaitAsync(url,
                () => new StringContent(JsonSerializer.Serialize(finalPayload), Encoding.UTF8, "application/json"),
                waitSeconds: 3, postTimeoutSeconds: 10);
            var respText = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[ROUND] Final {final.Item1}: {winner} wins {loser}");
            Console.WriteLine($"[DEBUG] recordMatch {final.Item1} resp: {respText}");

            // 6️ Đóng giải đấu
            var close = new { query = "mutation { closeTournament }" };
            await PostJsonAsync(url, close, "[STAGE] Tournament closed.");

            // 7️ Chờ champion xuất hiện
            var champion = await WaitForChampionAsync(url, 15);
            Console.WriteLine($"[RESULT] Champion query result: {JsonSerializer.Serialize(new { champion })}");

            if (snapshot.Tournament == null)
            {
                snapshot.Tournament = new TournamentMeta
                {
                    SnapshotId = snapshot.SnapshotId,
                    CreatedAt = snapshot.CreatedAt,
                    TopPlayers = snapshot.TopPlayers,
                    Name = $"XFighter_Linera_{DateTime.UtcNow:yyyyMMdd_HHmm}"
                };
            }
            // Cập nhật trạng thái giải
            snapshot.Tournament.Status = "Completed";
            snapshot.Tournament.Champion = champion ?? winner;
            snapshot.Tournament.RunnerUp = loser;
            var okCompleteSave = SaveLeaderboardSnapshot(snapshot);
            Console.WriteLine($"[SNAPSHOT] Marked tournament Completed => saved={okCompleteSave}");
        }

        /// Gửi mutation đơn giản có log
        private async Task PostJsonAsync(string url, object payload, string doneMsg)
        {
            var resp = await PostSingleWithServiceWaitAsync(url,
                () => new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                waitSeconds: 5, postTimeoutSeconds: 15);
            Console.WriteLine(doneMsg);
        }

        /// Chơi 1 vòng đấu (Q/S)
        private async Task<List<string>> PlayRoundAsync(string url, (string Id, string P1, string P2)[] matches, string label, Random rnd)
        {
            var winners = new List<string>();
            foreach (var (id, p1, p2) in matches)
            {
                var winner = rnd.Next(2) == 0 ? p1 : p2;
                var loser = winner == p1 ? p2 : p1;

                var payload = new
                {
                    query = $"mutation {{ recordMatch(matchId:\"{id}\", winner:\"{winner}\", loser:\"{loser}\") }}"
                };
                var resp = await PostSingleWithServiceWaitAsync(url,
                    () => new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                    waitSeconds: 3, postTimeoutSeconds: 10);
                var respText = await resp.Content.ReadAsStringAsync();

                Console.WriteLine($"[ROUND] {label} {id}: {winner} thắng {loser}");
                Console.WriteLine($"[DEBUG] recordMatch {id} resp: {respText}");
                winners.Add(winner);
            }
            return winners;
        }

        /// Chờ champion có giá trị trong state
        private async Task<string?> WaitForChampionAsync(string url, int timeoutSeconds = 10)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var queryObj = new { query = "query { champion }" };
            while (sw.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
            {
                var resp = await _httpClient.PostAsync(url,
                    new StringContent(JsonSerializer.Serialize(queryObj), Encoding.UTF8, "application/json"));
                var text = await resp.Content.ReadAsStringAsync();

                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.GetProperty("data").TryGetProperty("champion", out var champEl) &&
                        champEl.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(champEl.GetString()))
                        return champEl.GetString();
                }
                catch { /* ignore parse errors */ }

                await Task.Delay(300);
            }
            return null;
        }
        /// leaderboard cho Tournament
        public async Task<string> GetTournamentLeaderboardDataAsync()
        {
            return await RunWithLineraServiceAsync(async () =>
            {
                var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";
                var graphql = @"
                    query {
                        tournamentLeaderboard {
                            player
                            score
                        }
                        champion
                        runnerUp
                    }";

                var payload = new { query = graphql, variables = new { } };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var resp = await _httpClient.PostAsync(url, content);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync();
            });
        }

        public async Task<string> GetTournamentMetaDataAsync()
        {
            return await RunWithLineraServiceAsync(async () =>
            {
                var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";
                var graphql = @"
                  query {
                        tournamentName
                        startTime
                        endTime
                        status
                        participants
                        champion
                        runnerUp
                    }";

                var payload = new { query = graphql, variables = new { } };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var resp = await _httpClient.PostAsync(url, content);
                resp.EnsureSuccessStatusCode();

                return await resp.Content.ReadAsStringAsync();
            });
        }
        // Snapshot leadboard global
        public async Task<LeaderboardSnapshot?> CreateAndSaveLeaderboardSnapshotAsync()
        {
            try
            {
                // 1. Lấy dữ liệu leaderboard global 
                var leaderboardJson = await GetLeaderboardDataAsync();
                if (string.IsNullOrWhiteSpace(leaderboardJson))
                {
                    Console.WriteLine("[WARN] Empty leaderboard JSON, cannot snapshot.");
                    return null;
                }

                // 2. Parse top8 từ JSON
                var (allPlayers, topPlayers) = ParsePlayersFromJson(leaderboardJson);
                if (topPlayers == null || topPlayers.Count == 0)
                {
                    Console.WriteLine("[WARN] No top players found in leaderboard data.");
                    return null;
                }
                // 3. Tạo snapshot tournament
                var snapshotId = Guid.NewGuid().ToString("N");
                var snapshot = new LeaderboardSnapshot
                {
                    SnapshotId = snapshotId,
                    CreatedAt = DateTime.UtcNow.ToString("s"),
                    TopPlayers = [.. topPlayers],
                    AllPlayers = [.. allPlayers],
                    Hash = ComputeHash(leaderboardJson),
                    Tournament = new TournamentMeta
                    {
                        SnapshotId = snapshotId,
                        CreatedAt = DateTime.UtcNow.ToString("s"),

                        TopPlayers = [.. topPlayers],
                        Status = "Created",
                        Name = $"XFighter_Linera_{DateTime.UtcNow:yyyyMMdd_HHmm}"
                    }
                };

                // 4. Gọi hàm save 
                if (SaveLeaderboardSnapshot(snapshot))
                {
                    Console.WriteLine($"[SNAPSHOT] Created + saved snapshot {snapshot.SnapshotId}");
                    Console.WriteLine($"[SNAPSHOT] Top8 = {string.Join(", ", snapshot.TopPlayers)}");
                    Console.WriteLine($"[SNAPSHOT] Hash = {snapshot.Hash}");
                    return snapshot;
                }
                else
                {
                    Console.WriteLine("[ERROR] Snapshot creation succeeded but save failed.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to create leaderboard snapshot: {ex.Message}");
                return null;
            }
        }

        private static (List<string> AllPlayers, List<string> TopPlayers) ParsePlayersFromJson(string json)
        {
            var list = new List<string>();
            try
            {
                Console.WriteLine($"[DEBUG] leaderboard JSON raw: {json}");
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                    dataEl.TryGetProperty("leaderboard", out var leaderboardEl))
                {
                    foreach (var playerEl in leaderboardEl.EnumerateArray())
                    {
                        if (playerEl.TryGetProperty("userId", out var nameEl))
                            list.Add(nameEl.GetString() ?? "");
                    }
                }
                else
                {
                    Console.WriteLine("[WARN] JSON did not contain leaderboard data");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Failed to parse top players: {ex.Message}");
            }

            var top8 = list.Take(8).ToList();
            return (list, top8); // Top 8
        }

        private static string ComputeHash(string input)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes);
        }
        #endregion
    }
}