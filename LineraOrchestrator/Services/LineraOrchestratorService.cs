// LineraOrchestratorService.cs
using System.Diagnostics;
using System.Net;
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
       
        private static readonly string ROOT = IsRunningInDocker() ? "/app/data" : "/home/roycrypto/.config/linera_orchestrator";

        // Logic Mapping in-memory + persist trong Orchestrator Vì chainID/appID không phải cố định – mỗi trận là một cặp mới.
        private static readonly Dictionary<string, MatchMapping> _matchMap = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string _matchMappingFile = Path.Combine(ROOT, "match_mapping.json");

        // Logic Searching Onchain PlayerIndex
        private static readonly Dictionary<string, PlayerStats> _playerIndex = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string _playerIndexFile = Path.Combine(ROOT, "player_index.json");

        // --- submit requests file queue (file-based durable queue) ---
        private static readonly object _submitFileLock = new();
        private static readonly string _submitRequestsFile = Path.Combine(ROOT, "submit_requests.json");

        // SAVE - LOAD - ADD Snapshot Leaderboard for Tournament
        private static readonly string _snapshotFile = Path.Combine(ROOT, "snapshot_leaderboard.json");
        private static readonly object _snapshotLock = new();
        private static LeaderboardSnapshot? _currentSnapshot;

        // Concurrency / monitor fields
        private readonly SemaphoreSlim _serviceSemaphore = new(1, 1);
        private CancellationTokenSource? _serviceMonitorCts;
        private Task? _serviceMonitorTask;
        private readonly object _serviceMonitorLock = new(); //Protect Orchestrator + Restart Linera Service
        public LineraConfig GetCurrentConfig() => _config;
        private readonly HashSet<string> _knownChains = [];
        // Channels: riêng cho Open (create chain) và Submit (recordScore)
        private readonly Channel<Func<Task>> _openChannel =
            Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(150) // 150 requests per open queue
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        private readonly Channel<Func<Task>> _submitChannel =
            Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(500) // 500 requests per submit queue
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        // Coordination for priority: async signal that indicates "Open queue is empty"
        private readonly object _openTcsLock = new();
        private TaskCompletionSource<bool> _openEmptyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Priority coordination: nếu openQueue không rỗng -> submit phải chờ
        private int _openPending = 0; // Interlocked operate

        // Cancellation + worker task references for graceful shutdown
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _openWorkerTask = [];
        private readonly List<Task> _submitWorkerTask = [];

        public LineraOrchestratorService(LineraCliRunner cli, LineraConfig config, HttpClient httpClient)
        {
            _cli = cli;
            _config = config;
            _httpClient = httpClient; // tái sử dụng HttpClient using var client = new HttpClient();-> Reuse HttpClient singleton

            _config.LineraWallet = Environment.GetEnvironmentVariable("LINERA_WALLET")
                      ?? "/home/roycrypto/.linera_testnet/wallet_0.json";
            _config.LineraKeystore = Environment.GetEnvironmentVariable("LINERA_KEYSTORE")
                                   ?? "/home/roycrypto/.linera_testnet/keystore_0.json";
            _config.LineraStorage = Environment.GetEnvironmentVariable("LINERA_STORAGE")
                                  ?? "rocksdb:/home/roycrypto/.linera_testnet/client_0.db";

            Console.WriteLine($"[CONFIG] Wallet: {_config.LineraWallet}");
            Console.WriteLine($"[CONFIG] keystone: {_config.LineraKeystore}");
            Console.WriteLine($"[CONFIG] Storage: {_config.LineraStorage}");

            LoadMatchMapping(); // Tải dữ liệu danh sách Match khi khởi động
            LoadPlayerIndex(); // Tải dữ liệu danh sách Player khi khởi động

            // --- Dọn rác khởi đầu, Gom dọn rác cũ hoặc bị bỏ sót ---
            CleanupFailedMappingsAsync().Wait();
            Console.WriteLine("[CLEANUP] Initial cleanup done.");

            // --- Khởi động worker open và submit ---
            for (int i = 0; i < 2; i++) // 4 wokers 4% CPU
            {
                _openWorkerTask.Add(Task.Run(() => ProcessOpenQueueAsync(_cts.Token)));
                _submitWorkerTask.Add(Task.Run(() => ProcessSubmitQueueAsync(_cts.Token)));
            }

        }

        #region Linera Node 
        // Khởi động Linera Net trong nền và trích xuất các biến môi trường
        // Update:  Backup Local Mode + Conway Mode with flag
        public async Task<LineraConfig> StartLineraNodeAsync()
        {
            try
            {
                //1. Clean old setup & Bool change mode
                await StopAllLineraAsync();
                Console.WriteLine("[CLEANUP] Done");

                // TESTNET CONWAY & BACKUP MODE SETUP
                Console.WriteLine($"[LINERA-ORCH] Config: UseRemoteTestnet={_config.UseRemoteTestnet}" +
                    $", StartServiceWhenRemote={_config.StartServiceWhenRemote}");

                if (!_config.UseRemoteTestnet)
                {
                    Console.WriteLine("[LOCAL] Starting linera net up...");

                    if (IsRunningInDocker())
                    {
                        Console.WriteLine("[DOCKER MODE] Using Docker-specific linera setup...");
                        await _cli.StartLineraNetUpInBackgroundForDockerAsync();
                    }
                    else
                    {
                        Console.WriteLine("[LOCAL MODE] Using standard linera setup...");
                        await _cli.StartLineraNetUpInBackgroundAsync();
                    }

                    Console.WriteLine("[LINERA-ORCH] Linera Node Running (localnet).");
                    await Task.Delay(2000);
                }
                else
                {
                    Console.WriteLine("[LINERA-ORCH] TESTNET CONWAY mode -> skipping Backup Node `linera net up` (using ~/.linera_testnet).");
                    if (!string.IsNullOrWhiteSpace(_config.FaucetUrl))
                        Console.WriteLine($"[INFO] Faucet TESTNET CONWAY URL: {_config.FaucetUrl}");
                    Console.WriteLine($"[INFO] Using existing TESTNET CONWAY wallet: {_config.LineraWallet}");
                }

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
                    await Task.Delay(1000); // give service a moment to initialize and register blob-store client
                    StartServiceMonitor(); // Start monitor (watchdog/supervisor) to ensure service stays up
                    Console.WriteLine("[SERVICE] Linera service started and monitored.");
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
        private static bool IsRunningInDocker()
        {
            return Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ||
                   File.Exists("/.dockerenv") ||
                   Environment.GetEnvironmentVariable("LINERA_DOCKER_MODE") == "true";
        }

        #endregion

        #region RetryAsync - Linera Service Lifetime, Monitor Watchdog

        // Node Publish module, leaderboard Safe Guard & Retry
        private static async Task<T> RetryAsync<T>(Func<Task<T>> action, int maxAttempts = 5, int delayMs = 2000)
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
        #endregion

        #region Linera open match chainId
        public async Task<(string ChainId, string? AppId)> EnqueueOpenAsync(string? Player1, string? Player2)
        {
            var tcs = new TaskCompletionSource<(string, string?)>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            // --- Quản lý trạng thái Open Queue ---
            Interlocked.Increment(ref _openPending);
            lock (_openTcsLock)
            {
                if (_openPending == 1)
                    _openEmptyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            // --- Enqueue job Open ---
            try
            {
                await _openChannel.Writer.WriteAsync(async () =>
                {
                    try
                    {

                        var (chainId, appId) = await OpenAndCreateOnContractAsync();

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
                                SubmittedAt = DateTime.UtcNow.ToString("s")
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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OPEN-ENQUEUE][ERROR] {ex.Message}");
                // Nếu enqueue thất bại, rollback increment để không sai lệch
                if (Interlocked.Decrement(ref _openPending) == 0)
                {
                    Console.WriteLine("[OPEN] All open jobs completed -> signaling submit queue");
                    lock (_openTcsLock)
                        _openEmptyTcs.TrySetResult(true);
                }

                throw; // rethrow cho caller
            }

            return await tcs.Task;
        }

  
        /// Gửi mutation openAndCreate, sau đó poll resolveRequest tới khi có chainId mới
        private async Task<(string ChainId, string? AppId)> OpenAndCreateOnContractAsync()
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

            // Tạo placeholder mapping (key = requestId, status = "creating") và SaveMatchMapping().
            var requestId = Guid.NewGuid().ToString("N")[..8];
            lock (_matchMap)
            {
                _matchMap[requestId] = new MatchMapping
                {
                    MatchId = requestId,
                    ChainId = null,
                    AppId = null,
                    Status = "creating",
                    SubmittedAt = DateTime.UtcNow.ToString("s")
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
                Console.WriteLine($"[WARN] {ex} Waiting for Linera service to recover before continuing...");

                return (requestId, seedText);

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
                };
            }

            return (chainId!, childAppId);
        }
        #endregion

        #region SubmitMatchResultAsync 

        public async Task<string> EnqueueSubmitAsync(string? chainId, MatchResult matchResult)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Nếu đang có các job "open" đang chạy, persist request xuống file để xử lý sau
            if (Volatile.Read(ref _openPending) > 0)
            {
                Console.WriteLine($"[SUBMIT] Open pending detected ({_openPending}), persisting submit request to file...");
                var req = new SubmitRequest { ChainId = chainId, MatchResult = matchResult };
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

            // Đợi cho tới khi tất cả job open đã hoàn tất (open-empty)
            while (Volatile.Read(ref _openPending) > 0)
                await _openEmptyTcs.Task.ConfigureAwait(false);

            // Enqueue job submit thực tế
            await _submitChannel.Writer.WriteAsync(async () =>
            {
                try
                {
                    var result = await SubmitMatchResultCoreAsync(chainId, matchResult).ConfigureAwait(false);
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return await tcs.Task.ConfigureAwait(false);
        }

        public async Task<string> SubmitMatchResultCoreAsync(string? chainId, MatchResult matchResult)
        {
            Console.WriteLine($"[REQUEST]: Submit Match started at {DateTime.UtcNow:s}");

            // Validate required parameters
            if (string.IsNullOrWhiteSpace(chainId))
                throw new ArgumentNullException(nameof(chainId));
            ArgumentNullException.ThrowIfNull(matchResult);

            // Đảm bảo matchId = chainId để nhất quán
            matchResult.MatchId = chainId;

            string appId;
            bool needSave = false;
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
                existing.SubmittedAt = DateTime.UtcNow.ToString("s");
                needSave = true;
            }
            if (needSave) SaveMatchMapping();

            // Prevent duplicate submission: atomic check & set using lock on _matchMap
            var matchKey = chainId; // dùng chainId làm key duy nhất
            string? opHex = null;
            string text = string.Empty;
            var url = $"http://localhost:8080/chains/{chainId}/applications/{appId}";
            var graphql = @"
                    mutation recordScore($matchResult: MatchResultInput!) {
                        recordScore(matchResult: $matchResult)
                    }
                ";

            var payload = new { query = graphql, variables = new { matchResult } };
            //CamelCase để property names match với GraphQL (matchId, player, score)
            var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json");

            // ***mở rộng MatchMapping để lưu trạng thái status/ submittedAt / submittedOpId***
            HttpResponseMessage resp = null!;
            try
            {
                // Use monitor-based single-send helper so we rely on monitor/PID
                resp = await PostSingleWithServiceWaitAsync(
                    url,
                    () => new StringContent(
                        JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json"),
                    waitSeconds: 8,
                    postTimeoutSeconds: 30).ConfigureAwait(false);

                text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Console.WriteLine($"[DEBUG] Linera response for chain={chainId}: {text}");

                // 1) HTTP non-2xx -> fail
                if (!resp.IsSuccessStatusCode)
                {
                    UpdateMatchStatus(chainId, "submit failed");

                    Console.WriteLine($"[WARN] HTTP {resp.StatusCode}: {text}");
                    Console.WriteLine("[INFO] Waiting for Linera service to recover before continuing...");

                    return text;
                }
            }
            catch (OperationCanceledException)
            {
                UpdateMatchStatus(chainId, "submit failed");

                // Pause the queue until monitor reports service stable (same behavior as open-and-create)
                Console.WriteLine("[INFO] Submit failed due to timeout. Waiting for Linera service to recover before continuing...");
                //await WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000).ConfigureAwait(false);
                return text;
            }
            catch (Exception ex)
            {
                // mark mapping as failed if relevant
                UpdateMatchStatus(chainId, "submit failed");

                // Pause the queue until monitor reports service stable (same behavior as open-and-create)
                Console.WriteLine($"[INFO] Submit failed: {ex.Message}. Waiting for Linera service to recover before continuing...");
                return text;
            }
            // ***NEW MAPPING  need state so orchestrator knows match has been submitted
            // (prevent duplicate) and save opId for debugging/tracing ***
            // ---- Parse opId/ op hex from response ----
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
            Console.WriteLine($"[DEBUG] Extracted opHex = {opHex ?? "null"}");
            // Kiểm tra appId từ mapping
            if (_matchMap.TryGetValue(matchKey, out var current))
            {
                Console.WriteLine($"[DEBUG] Current mapping before update: appId={current.AppId ?? "null"}, status={current.Status}");
            }

            bool needSave2 = false;
            // Update mapping submitted
            lock (_matchMap)
            {
                if (_matchMap.TryGetValue(matchKey, out var m))
                {
                    m.Status = "submitted";
                    m.SubmittedOpId = string.IsNullOrWhiteSpace(opHex) ? null : opHex;
                    m.SubmittedAt = DateTime.UtcNow.ToString("s");
                    needSave2 = true;
                }
            }
            if (needSave2) SaveMatchMapping();

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


            Console.WriteLine($"[REQUEST]: Submit Match finished at {DateTime.UtcNow:s}");
            Console.WriteLine($"[DEBUG] Start waiting for leaderboard update for match {matchResult.MatchId}");
            await Task.Delay(500).ConfigureAwait(false);

            // --- Đợi leaderboard cập nhật ---
            bool verified = await WaitForLeaderboardUpdateAsync(matchResult.Player1Username, matchResult.Player2Username, timeoutMs: 5000).ConfigureAwait(false);

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
            bool needSave = false;
            lock (_matchMap)
            {
                if (_matchMap.TryGetValue(matchKey, out var m))
                {
                    m.Status = status;
                    m.SubmittedAt = DateTime.UtcNow.ToString("s");
                    needSave = true;
                }
            }
            if (needSave)
            {
                // SaveMatchMapping() will lock internally as needed
                SaveMatchMapping();
            }
        }
        // Helper: DEBUG wait leaderboard confirm after submit
        private async Task<bool> WaitForLeaderboardUpdateAsync(string player1, string player2, int timeoutMs = 8000, int pollIntervalMs = 1000)
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
                // 🩺 Đảm bảo service ổn định trước khi gọi API (thay cho RunWithLineraServiceAsync)
                while (!await WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
                {
                    Console.WriteLine("[LEADERBOARD] Waiting for Linera service to stabilize...");
                    await Task.Delay(500);
                }

                var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.LeaderboardAppId}";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

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
                    resp = await _httpClient.PostAsync(url, content, cts.Token);
                    text = await resp.Content.ReadAsStringAsync();
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("GetLeaderboardDataAsync timed out.");
                }

                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[LEADERBOARD][ERROR] HTTP {resp.StatusCode}: {text}");
                    throw new InvalidOperationException($"Linera service returned HTTP {resp.StatusCode}");
                }

                Console.WriteLine("[LEADERBOARD] Data fetched successfully.");
                return text;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LEADERBOARD][EXCEPTION] {ex.Message}");
                throw;
            }
            finally
            {
                Console.WriteLine($"[REQUEST] Sync & Get Data Leaderboard API at {DateTime.UtcNow:O}");
            }
        }

        #endregion

        #region MatchMapping persistence ánh xạ MAPPING: MatchID → ChainID  & history match data of players // Snapshot leaderboard
        // Logic Mapping in-memory + persist trong Orchestrator Vì chainID/appID không phải cố định – mỗi trận là một cặp mới.
        // Khi Unity nhấn "Submid Record Match" (button)
        // Nếu Unity gửi đầy đủ chainId và appId, orchestrator dùng luôn(không cần lookup).
        // Nếu Unity chỉ gửi matchId, orchestrator tra từ _matchMap để tìm ra chainId/appId.
        // Nếu không lưu ánh xạ này, khi submit sẽ không biết nên gửi vào chain nào → kết quả có thể bị gửi sai nơi
     
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
        private static List<SubmitRequest> LoadSubmitRequestsFromFile()
        {
            lock (_submitFileLock)
            {
                try
                {
                    if (!File.Exists(_submitRequestsFile)) return new List<SubmitRequest>();
                    var fi = new FileInfo(_submitRequestsFile);
                    if (fi.Length == 0) return new List<SubmitRequest>();

                    var json = File.ReadAllText(_submitRequestsFile);
                    var list = JsonSerializer.Deserialize<List<SubmitRequest>>(json, JsonOptions.Read);
                    return list ?? new List<SubmitRequest>();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to load submit requests file: {ex.Message}");
                    // Try to backup if corrupted
                    try
                    {
                        if (File.Exists(_submitRequestsFile))
                            File.Copy(_submitRequestsFile, _submitRequestsFile + ".bak", overwrite: true);
                    }
                    catch { }
                    return new List<SubmitRequest>();
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
                var list = LoadSubmitRequestsFromFile() ?? new List<SubmitRequest>();
                // req đã được kiểm tra non-null, nên không có warning khi Add
                list.Add(req);
                var ok = SaveSubmitRequestsToFileAtomic(list);
                if (!ok) Console.WriteLine("[WARN] AppendSubmitRequestToFile: failed to persist request.");
            }
        }


        // SAVE - LOAD - ADD Player To Tracking
        // giả sử có _playerIndex: Dictionary<string, HashSet<string>> lưu player -> set(chainId)
      
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

        // gọi từ periodic loop: await CleanupFailedMappingsAsync();
        private static async Task CleanupFailedMappingsAsync()
        {
            try
            {
                List<string> toRemove;
                lock (_matchMap)
                {
                    toRemove = _matchMap
                        .Where(kv => string.Equals(kv.Value.Status, "create failed", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(kv.Value.Status, "submit failed", StringComparison.OrdinalIgnoreCase) ||
                                    string.IsNullOrWhiteSpace(kv.Value.ChainId))
                        .Select(kv => kv.Key)
                        .ToList();
                }

                if (toRemove.Count == 0) return;

                bool anyRemoved = false;
                lock (_matchMap)
                {
                    foreach (var k in toRemove)
                    {
                        if (_matchMap.Remove(k))
                        {
                            Console.WriteLine($"[CLEANUP] Removed failed mapping: {k}");
                            anyRemoved = true;
                        }
                    }
                }

                if (anyRemoved)
                {
                    await Task.Run(() => SaveMatchMapping());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CLEANUP-ERROR] Failed to clean mappings: {ex.Message}");
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

        #region Helper Open Queue, Submit Queue, WaitForServiceViaMonitorAsync & PostSingleWithServiceWaitAsync
        // helper cơ bản: POST với retry/backoff và gọi EnsureServiceRunningAsync() nếu cần
        // Replace existing WaitForLineraServiceAsync and PostSingleWithServiceWaitAsync with these.
        // 1) Wait based on monitor PID first (fast & authoritative for your setup)
        // Wait for the monitor to report a running Linera service (via PID) and require a short stability window.
        // Rationale: rely on your monitor (PID) rather than /health or TCP.
        // Chỉ tin vào monitor/PID để biết service đã sẵn sàng
        // TODO 5: 2 Queue open & submit, high priority - low priority

        private async Task ProcessOpenQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var job in _openChannel.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        // ensure service stable via monitor before running (giữ behavior cũ)
                        while (!await WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
                        {
                            // không busy-wait; chỉ delay ngắn để tránh vòng lặp cháy CPU
                            await Task.Delay(500, cancellationToken);
                        }

                        await job().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // graceful cancellation
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[OPEN-WORKER][ERROR] {ex}");
                    }
                    finally
                    {
                        // decrement pending. Nếu về 0 -> signal open-empty
                        if (Interlocked.Decrement(ref _openPending) == 0)
                        {
                            Console.WriteLine("[OPEN] All open jobs completed -> signaling submit queue");

                            lock (_openTcsLock)
                            {
                                // TrySetResult safe nếu đã set rồi
                                _openEmptyTcs.TrySetResult(true);
                            }
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
                        // Wait asynchronously until Open queue is empty (no busy-wait)
                        Task waitTask;
                        lock (_openTcsLock)
                        {
                            // capture the current TCS (either completed or not)
                            waitTask = _openEmptyTcs.Task;
                        }

                        // Wait until open queue is empty OR cancellation requested
                        var completed = await Task.WhenAny(waitTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
                        if (completed != waitTask)
                        {
                            // cancellation requested
                            throw new OperationCanceledException(cancellationToken);
                        }

                        try
                        {
                            // Process persistent queue while open queue is empty
                            await ProcessPersistentSubmitQueueAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[SUBMIT-WORKER][PERSIST] Error processing persistent submit queue: {ex.Message}");
                        }

                        // then continue to process the current in-memory job
                        await job().ConfigureAwait(false);
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

        /// <summary>
        /// Called by submit worker when open-queue is empty.
        /// Reads submit_requests.json and sequentially tries to submit each entry using SubmitMatchResultCoreAsync.
        /// On success: removes request and updates file atomically. On failure: leaves the request for future retry.
        /// </summary>
        private async Task ProcessPersistentSubmitQueueAsync(CancellationToken cancellationToken)
        {
            // Nếu file không tồn tại thì không có gì để làm
            if (!File.Exists(_submitRequestsFile)) return;

            List<SubmitRequest> list;
            lock (_submitFileLock)
            {
                list = LoadSubmitRequestsFromFile();
            }

            if (list == null || list.Count == 0) return;

            Console.WriteLine($"[PERSIST-QUEUE] Found {list.Count} persistent submit request(s). Processing one pass...");

            // Xử lý tối đa một lượt qua toàn bộ phần tử hiện có (để tránh loop vô hạn nếu tất cả đều fail)
            int initialCount = list.Count;
            int processedAttempts = 0;

            while (processedAttempts < initialCount && list.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // luôn lấy phần tử đầu (rotate logic: nếu fail thì đẩy về cuối)
                var req = list[0];

                try
                {
                    Console.WriteLine($"[PERSIST-QUEUE] Trying submit (matchId={req?.MatchResult?.MatchId}, chain={req?.ChainId})");
                    var resultJson = await SubmitMatchResultCoreAsync(req?.ChainId, req?.MatchResult!);
                    Console.WriteLine($"[PERSIST-QUEUE] Submit succeeded for matchId={req?.MatchResult?.MatchId}: {resultJson}");

                    // xóa phần tử đã thành công
                    list.RemoveAt(0);

                    // persist lại danh sách còn lại (atomic)
                    SaveSubmitRequestsToFileAtomic(list);

                    // cập nhật match mapping ở đây
                    // UpdateMatchMappingAfterSubmit(...); 
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PERSIST-QUEUE] Submit failed for matchId={req?.MatchResult?.MatchId}: {ex.Message}");

                    // cập nhật metadata retry nếu SubmitRequest có các trường này (nên có)
                    try
                    {
                        // Nếu bạn đã thêm Attempts/LastError/NextTryAt vào SubmitRequest, cập nhật chúng
                        req!.Attempts = (req.Attempts) + 1; // nếu Attempts mặc định là 0
                        req.LastError = ex.Message;
                        // simple exponential backoff cap 60s (sử dụng Attempts)
                        var backoffSec = Math.Min(60, (int)Math.Pow(2, Math.Min(10, req.Attempts)));
                        req.NextTryAt = DateTime.UtcNow.AddSeconds(backoffSec).ToString("s");
                    }
                    catch
                    {
                        // nếu class SubmitRequest không có trường metadata, chỉ ignore
                    }

                    // move failed item to end of list (rotate)
                    list.RemoveAt(0);
                    list.Add(req!);

                    // persist list changed (atomic)
                    SaveSubmitRequestsToFileAtomic(list);

                    // tránh hot-loop: đợi 200ms (tùy chỉnh theo nhu cầu)
                    await Task.Delay(200, cancellationToken);
                }

                processedAttempts++;
            }

            // Sau một lượt, persist lại (nếu cần) để đảm bảo consistency
            SaveSubmitRequestsToFileAtomic(list);

            Console.WriteLine($"[PERSIST-QUEUE] One-pass processing complete. Remaining requests: {list.Count}");
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
        private async Task<HttpResponseMessage> PostSingleWithServiceWaitAsync(
            string url,
            Func<HttpContent> contentFactory,
            int waitSeconds = 10,
            int postTimeoutSeconds = 30,
            int maxAttempts = 3)    // retry 3
        {
            // 1) Đợi monitor báo service đã có PID ổn định (như trước)
            var ready = await WaitForServiceViaMonitorAsync(waitSeconds, pollMs: 500, stableMs: 1000).ConfigureAwait(false);
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
                        await WaitForServiceViaMonitorAsync(timeoutSeconds: Math.Min(5, waitSeconds), pollMs: 500, stableMs: 1000).ConfigureAwait(false);
                        // // backoff retry  Attempt 1 → delay = 300 ms Attempt 2 → 600 ms Attempt 3 → 900 ms
                        await Task.Delay(2000 + 300 * attempt).ConfigureAwait(false);
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
                    await WaitForServiceViaMonitorAsync(timeoutSeconds: Math.Min(5, waitSeconds), pollMs: 500, stableMs: 1000).ConfigureAwait(false);
                    await Task.Delay(2000 + 300 * attempt).ConfigureAwait(false);
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
                    await WaitForServiceViaMonitorAsync(timeoutSeconds: Math.Min(5, waitSeconds), pollMs: 500, stableMs: 1000).ConfigureAwait(false);
                    await Task.Delay(2000 + 300 * attempt).ConfigureAwait(false);
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
        #endregion

        #region Tournament leaderboard GraphQL query,Meta Create Tournament
        //GET tournament leaderboard(GraphQL query)
        public async Task<string> GetTournamentLeaderboardDataAsync()
        {
            while (!await WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 500, stableMs: 1000))
            {
                Console.WriteLine("[TOURNAMENT] Waiting for Linera service to stabilize before get data...");
                await Task.Delay(500);
            }
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

        }

        public async Task<string> GetTournamentMetaDataAsync()
        {
            while (!await WaitForServiceViaMonitorAsync(timeoutSeconds: 10, pollMs: 300, stableMs: 500))
            {
                Console.WriteLine("[TOURNAMENT] Waiting for Linera service to stabilize before get data...");
                await Task.Delay(500);
            }
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
        }
        // Snapshot leadboard global
        public async Task<LeaderboardSnapshot?> CreateAndSaveLeaderboardSnapshotAsync()
        {
            try
            {
                var leaderboardJson = await GetLeaderboardDataAsync();
                if (string.IsNullOrWhiteSpace(leaderboardJson))
                {
                    Console.WriteLine("[WARN] Empty leaderboard JSON.");
                    return null;
                }

                var (allPlayers, topPlayers) = ParsePlayersFromJson(leaderboardJson);
                if (topPlayers.Count == 0)
                {
                    Console.WriteLine("[WARN] No top players.");
                    return null;
                }


                var snapshot = new LeaderboardSnapshot
                {
                    SnapshotId = Guid.NewGuid().ToString("N"),
                    CreatedAt = DateTime.UtcNow.ToString("s"),
                    AllPlayers = [.. allPlayers],
                    TopPlayers = [.. topPlayers],
                    OnChainOpId = "",
                    Tournament = new TournamentMeta
                    {
                        SnapshotId = Guid.NewGuid().ToString("N"),
                        CreatedAt = DateTime.UtcNow.ToString("s"),
                        TopPlayers = [.. topPlayers],
                        Status = "Creating",
                        Name = $"XFighter_Tournament_{DateTime.UtcNow:yyyyMMdd_HHmm}"
                    }
                };

                SaveLeaderboardSnapshot(snapshot);

                // Gọi Linera mutation để tạo tournament on-chain và lấy opId thật
                var opId = await CreateTournamentAsync();
                if (string.IsNullOrWhiteSpace(opId))
                    throw new InvalidOperationException("Không lấy được opId từ Linera, dừng tiến trình snapshot.");

                // 4️ Cập nhật lại snapshot với opId
                snapshot.OnChainOpId = opId;
                snapshot.Tournament.Status = "Created";

                SaveLeaderboardSnapshot(snapshot);

                Console.WriteLine($"[SNAPSHOT] Created snapshot {snapshot.SnapshotId} (on-chain opId={opId}) with top8 players");
                return snapshot;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Snapshot failed: {ex.Message}");
                return null;
            }
        }
        // lấy top players và all players
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

        /// <summary>
        /// CreateTournament
        /// Lấy snapshot mới nhất (đã lưu trước đó). Chuẩn bị GraphQL mutation, Gửi HTTP 
        /// Save Lưu trạng thái Tournament vào snapshot (Status = "Created")
        /// </summary>

        public async Task<string> CreateTournamentAsync(string? name = null, long? startTime = null, long? endTime = null)
        {
            var snapshot = LoadLeaderboardSnapshot()
                ?? throw new InvalidOperationException("No leaderboard snapshot found. Create snapshot before starting tournament.");

            var top8 = snapshot.TopPlayers ?? throw new InvalidOperationException("Snapshot missing top players.");

            var tournamentName = name ?? $"XFighter_Tournament_{DateTime.UtcNow:yyyyMMdd_HHmm}";
            var start = startTime ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var end = endTime ?? start + 7200; // 2 giờ

            var payload = new
            {
                query = $"mutation {{ createTournament(name:\"{tournamentName}\", startTime:{start}, endTime:{end}) }}"
            };

            var url = $"http://localhost:8080/chains/{_config.PublisherChainId}/applications/{_config.TournamentAppId}";
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(url, content);
            var respText = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Failed to create tournament: {resp.StatusCode} - {respText}");

            string opId = "";
            // Parse đúng format Linera trả về
            using (var doc = JsonDocument.Parse(respText))
            {
                if (doc.RootElement.TryGetProperty("data", out var dataEl))
                {
                    if (dataEl.ValueKind == JsonValueKind.String)
                        opId = dataEl.GetString() ?? "";
                    else if (dataEl.TryGetProperty("createTournament", out var opEl))
                        opId = opEl.GetString() ?? "";
                }
            }
            if (string.IsNullOrWhiteSpace(opId))
                throw new InvalidOperationException($"Linera did not return opId. Aborting snapshot creation. Response: {respText}");

            Console.WriteLine($"[TOURNAMENT] Created new tournament {tournamentName}");

            snapshot.Tournament = new TournamentMeta
            {
                SnapshotId = snapshot.SnapshotId,
                CreatedAt = DateTime.UtcNow.ToString("s"),
                TopPlayers = [.. top8],
                Name = tournamentName,
                Status = "Created"
            };

            snapshot.OnChainOpId = opId;
            SaveLeaderboardSnapshot(snapshot);

            return opId;
        }

        #endregion

        #region tournament Draw system, InitTournamentMatches, submit-match, create matchlist, 
        // STEP 1: Draw
        // Utility: deterministic shuffle (same logic as before)
        public List<string> GenerateDeterministicShuffle(string snapshotHash, List<string> top8)
        {
            ArgumentNullException.ThrowIfNull(top8);

            if (top8.Count < 8) throw new ArgumentException("top8 must contain at least 8 players");

            // SHA256 -> take first 4 bytes -> Int32 seed
            var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(snapshotHash ?? ""));
            int seed = BitConverter.ToInt32([.. hashBytes.Take(4)], 0);
            var rnd = new Random(seed);
            var shuffled = top8.OrderBy(_ => rnd.Next()).ToList();
            return shuffled;
        }

        // Wrapper: generate shuffled top8 from latest snapshot (public so Controller can call)
        public (List<string> Shuffled, string Hash) GetShuffledTop8FromLatestSnapshot()
        {
            var snapshot = LoadLeaderboardSnapshot(); // existing private method used elsewhere
            if (snapshot?.Tournament == null || snapshot.Tournament.TopPlayers == null || snapshot.Tournament.TopPlayers.Count < 8)
                throw new InvalidOperationException("Snapshot invalid or not enough players");

            var top8 = snapshot.Tournament.TopPlayers;
            var hash = snapshot.OnChainOpId ?? DateTime.UtcNow.ToString("O");
            var shuffled = GenerateDeterministicShuffle(hash, top8);
            return (shuffled, hash);
        }

        // STEP 2: helper – Tạo 4 trận Quarterfinal (ngẫu nhiên từ top8)
        public bool InitTournamentMatches(List<string>? forcedOrder = null)
        {
            var snapshot = LoadLeaderboardSnapshot();
            if (snapshot?.Tournament == null || snapshot.Tournament.TopPlayers == null || snapshot.Tournament.TopPlayers.Count < 8)
            {
                Console.WriteLine("[TOURNAMENT][ERROR] Snapshot invalid or not enough players to init matches.");
                return false;
            }
            //var shuffled = top8.OrderBy(_ => rnd.Next()).ToList();
            List<string> shuffled;
            if (forcedOrder != null && forcedOrder.Count >= 8)
            {
                // Respect client-provided order (take first 8)
                shuffled = [.. forcedOrder.Take(8)];
            }
            else
            {
                // old deterministic behavior based on snapshot.Hash
                var top8 = snapshot.Tournament.TopPlayers;
                var seedBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(snapshot.OnChainOpId ?? ""));
                var seed = BitConverter.ToInt32([.. seedBytes.Take(4)], 0);
                var rnd = new Random(seed);
                shuffled = [.. top8.OrderBy(_ => rnd.Next())];
            }

            // Tạo 4 trận tứ kết (Quarterfinals)
            var matches = new List<TournamentMatch>
            {
                new() { MatchId = "QF1", Player1 = shuffled[0], Player2 = shuffled[1], Status = "waiting" },
                new() { MatchId = "QF2", Player1 = shuffled[2], Player2 = shuffled[3], Status = "waiting" },
                new() { MatchId = "QF3", Player1 = shuffled[4], Player2 = shuffled[5], Status = "waiting" },
                new() { MatchId = "QF4", Player1 = shuffled[6], Player2 = shuffled[7], Status = "waiting" }
            };

            snapshot.Tournament.Matches = matches;
            snapshot.Tournament.CurrentRound = "Quarterfinal";
            snapshot.Tournament.Status = "Running";
            SaveLeaderboardSnapshot(snapshot);

            Console.WriteLine("[TOURNAMENT] Initialized tournament matches successfully.");
            return true;
        }

        /// Gửi kết quả trận đấu thuộc giải Tournament.
        /// Không reuse SubmitMatchResultAsync. Không update snapshot.
        public async Task<string> SubmitTournamentMatchResultAsync(MatchResult matchResult)
        {
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
            var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json");
            string responseText;
            string? opHex = null;

            var resp = await PostSingleWithServiceWaitAsync(
                    url,
                    () => new StringContent(JsonSerializer.Serialize(payload, JsonOptions.Write), Encoding.UTF8, "application/json"),
                    waitSeconds: 8,
                    postTimeoutSeconds: 30);

            responseText = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"[TOURNAMENT] HTTP {resp.StatusCode}: {responseText}");

            try
            {
                using var doc = JsonDocument.Parse(responseText);
                if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                    dataEl.TryGetProperty("recordTournamentScore", out var rs) &&
                    rs.ValueKind == JsonValueKind.String)
                    opHex = rs.GetString();
            }
            catch { /* ignore parse errors */ }

            Console.WriteLine($"[TOURNAMENT] Match {matchResult.MatchId} submitted OK. Winner={matchResult.WinnerUsername}");
            Console.WriteLine($"[DEBUG] Raw response: {responseText}");

            // Update local snapshot for UI refresh
            var snapshot = LoadLeaderboardSnapshot();
            if (snapshot?.Tournament?.Matches != null)
            {
                var match = snapshot.Tournament.Matches.FirstOrDefault(m => m.MatchId == matchResult.MatchId);
                if (match != null)
                {
                    match.Status = "completed";
                    match.Winner = matchResult.WinnerUsername;
                    match.Loser = matchResult.LoserUsername;
                    SaveLeaderboardSnapshot(snapshot);
                }
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                matchId = matchResult.MatchId,
                opId = opHex,
                raw = "[tournament submit ok]"
            });
        }

        /// <summary>
        /// Tiến hành tạo vòng bán kết (Semifinals) dựa trên kết quả 4 trận Tứ kết.
        /// </summary>
        public bool AdvanceToSemiFinals()
        {
            var snapshot = LoadLeaderboardSnapshot();
            if (snapshot?.Tournament?.Matches == null)
            {
                Console.WriteLine("[TOURNAMENT][ERROR] No tournament matches found. Cannot advance to semifinals.");
                return false;
            }

            var qfMatches = snapshot.Tournament.Matches
                .Where(m => m.MatchId.StartsWith("QF", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (qfMatches.Count < 4)
            {
                Console.WriteLine("[TOURNAMENT][ERROR] Not enough Quarterfinal matches to advance.");
                return false;
            }

            var winners = qfMatches
                .Where(m => !string.IsNullOrWhiteSpace(m.Winner))
                .Select(m => m.Winner!)
                .ToList();

            if (winners.Count < 4)
            {
                Console.WriteLine("[TOURNAMENT][WARN] Quarterfinals not finished yet.");
                return false;
            }

            // Random để shuffle danh sách 4 người thắng
            var rnd = new Random();
            var shuffled = winners.OrderBy(_ => rnd.Next()).ToList();

            // Tạo 2 trận bán kết
            var semiMatches = new List<TournamentMatch>
            {
                new() { MatchId = "S1", Player1 = shuffled[0], Player2 = shuffled[1], Status = "waiting" },
                new() { MatchId = "S2", Player1 = shuffled[2], Player2 = shuffled[3], Status = "waiting" }
            };

            snapshot.Tournament.Matches.AddRange(semiMatches);
            snapshot.Tournament.CurrentRound = "Semifinal";
            snapshot.Tournament.Status = "Running";

            SaveLeaderboardSnapshot(snapshot);

            Console.WriteLine("[TOURNAMENT] Advanced to Semifinals:");
            foreach (var s in semiMatches)
                Console.WriteLine($" - {s.MatchId}: {s.Player1} vs {s.Player2}");

            return true;
        }

        /// <summary>
        /// Tiến hành tạo vòng chung kết (Final) dựa trên kết quả 2 trận bán kết.
        /// </summary>
        public bool AdvanceToFinal()
        {
            var snapshot = LoadLeaderboardSnapshot();
            if (snapshot?.Tournament?.Matches == null)
            {
                Console.WriteLine("[TOURNAMENT][ERROR] No tournament data found. Cannot advance to Final.");
                return false;
            }

            var sfMatches = snapshot.Tournament.Matches
                .Where(m => m.MatchId.StartsWith("S", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sfMatches.Count < 2)
            {
                Console.WriteLine("[TOURNAMENT][ERROR] Not enough Semifinal matches to advance.");
                return false;
            }

            var winners = sfMatches
                .Where(m => !string.IsNullOrWhiteSpace(m.Winner))
                .Select(m => m.Winner!)
                .ToList();

            if (winners.Count < 2)
            {
                Console.WriteLine("[TOURNAMENT][WARN] Semifinals not finished yet.");
                return false;
            }

            // Random shuffle 2 người thắng cho trận chung kết (nếu muốn đảo vị trí)
            var rnd = new Random();
            var shuffled = winners.OrderBy(_ => rnd.Next()).ToList();

            var finalMatch = new TournamentMatch
            {
                MatchId = "F1",
                Player1 = shuffled[0],
                Player2 = shuffled[1],
                Status = "waiting"
            };

            snapshot.Tournament.Matches.Add(finalMatch);
            snapshot.Tournament.CurrentRound = "Final";
            snapshot.Tournament.Status = "Running";

            SaveLeaderboardSnapshot(snapshot);

            Console.WriteLine($"[TOURNAMENT] Advanced to Final: {finalMatch.Player1} vs {finalMatch.Player2}");
            return true;
        }

        /// <summary>
        /// Khi trận chung kết kết thúc, cập nhật trạng thái giải đấu Completed.
        /// </summary>
        public bool CompleteTournament(string champion, string runnerUp)
        {
            var snapshot = LoadLeaderboardSnapshot();
            if (snapshot?.Tournament == null)
            {
                Console.WriteLine("[TOURNAMENT][ERROR] No tournament found to complete.");
                return false;
            }

            snapshot.Tournament.Champion = champion;
            snapshot.Tournament.RunnerUp = runnerUp;
            snapshot.Tournament.Status = "Completed";
            snapshot.Tournament.CompletedAt = DateTime.UtcNow.ToString("s");

            SaveLeaderboardSnapshot(snapshot);

            Console.WriteLine($"[TOURNAMENT] Tournament completed! Champion: {champion}, Runner-up: {runnerUp}");
            return true;
        }
        public async Task<string> GetTournamentMatchListAsync()
        {
            var snapshotPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config/linera_orchestrator/snapshot_leaderboard.json"
            );

            if (!File.Exists(snapshotPath))
                throw new FileNotFoundException("Snapshot not found");

            var json = await File.ReadAllTextAsync(snapshotPath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("tournament", out var tournament))
                return JsonSerializer.Serialize(new { matches = new List<object>() });

            var matches = tournament.GetProperty("matches").EnumerateArray()
                .Select(m => new
                {
                    matchId = m.GetProperty("matchId").GetString(),
                    player1 = m.GetProperty("player1").GetString(),
                    player2 = m.GetProperty("player2").GetString(),
                    status = m.GetProperty("status").GetString()
                }).ToList();

            return JsonSerializer.Serialize(new { matches });
        }
        #endregion
    }
}