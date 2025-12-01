// LineraOrchestratorService.cs
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LineraOrchestrator.Models;


namespace LineraOrchestrator.Services
{
    public class LineraOrchestratorService
    {
        private readonly LineraCliRunner _cli;
        private readonly LineraConfig _config;
        private readonly HttpClient _httpClient;


        private static readonly string ROOT = EnvironmentService.GetDataPath();
        private readonly string _stateFile = Path.Combine(ROOT, "linera_orchestrator_state.json");

        /// load state Orchestrator
        private OrchestratorState? _state;

        /// Concurrency / monitor fields
        private readonly SemaphoreSlim _serviceSemaphore = new(1, 1);
        private CancellationTokenSource? _serviceMonitorCts;
        private Task? _serviceMonitorTask;
        private readonly object _serviceMonitorLock = new(); //Protect Orchestrator + Restart Linera Service
        public LineraConfig GetCurrentConfig() => _config;
      
        public LineraOrchestratorService(LineraCliRunner cli, LineraConfig config, HttpClient httpClient)
        {
            _cli = cli;
            _config = config;
            _httpClient = httpClient;

            _config.LineraWallet = Environment.GetEnvironmentVariable("LINERA_WALLET")
            ?? Path.Combine(EnvironmentService.GetPublisherPath(), "wallet.json");
            _config.LineraKeystore = Environment.GetEnvironmentVariable("LINERA_KEYSTORE")
                ?? Path.Combine(EnvironmentService.GetPublisherPath(), "keystore.json");
            _config.LineraStorage = Environment.GetEnvironmentVariable("LINERA_STORAGE")
                ?? $"rocksdb:{Path.Combine(EnvironmentService.GetPublisherPath(), "client.db")}";


            Console.WriteLine($"[CONFIG] Publisher Path: {EnvironmentService.GetPublisherPath()}");
            Console.WriteLine($"[CONFIG] Wallet: {_config.LineraWallet}");
            Console.WriteLine($"[CONFIG] Keystore: {_config.LineraKeystore}");
            Console.WriteLine($"[CONFIG] Storage: {_config.LineraStorage}");

            LoadState(); // load state Linera PublisherChain
        }

        #region Linera Node Get DefaultChain Deploy appchain
        // Update:  Backup Local Mode + Conway Mode with flag
        public async Task<LineraConfig> StartLineraNodeAsync()
        {
            try
            {
                //1. Clean old setup & Bool change mode
                await StopAllLineraAsync();
                await StopServiceMonitorAsync();
                Console.WriteLine("[CLEANUP] Done");

                // TESTNET CONWAY & BACKUP MODE SETUP
                Console.WriteLine($"[LINERA-ORCH] Config: UseRemoteTestnet={_config.UseRemoteTestnet}" +
                    $", StartServiceWhenRemote={_config.StartServiceWhenRemote}");

                if (!_config.UseRemoteTestnet)
                {
                    Console.WriteLine("[LOCAL] Starting linera net up...");

                    if (EnvironmentService.IsRunningInDocker())
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
                    Console.WriteLine("[LINERA-ORCH] TESTNET CONWAY mode.");
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

                //4. PUBLISHER CHAIN lấy chain 0 (admin chain)
                var publisherchainId = GetDefaultChainFromWalletFile(_config.LineraWallet!);
                _config.PublisherChainId = publisherchainId;
                Console.WriteLine($"Publisher Chain ID : \n{publisherchainId}");

                var publisherOwner = GetPublicKeyFromWallet(_config.LineraWallet!);
                _config.PublisherOwner = publisherOwner;
                Console.WriteLine($"Publisher Owner : \n{publisherOwner}");

                // KIỂM TRA STATE - CHỈ THỰC HIỆN DEPLOY KHI STATE CHƯA CÓ
                if (_state == null || !_state.IsValid)
                {
                    Console.WriteLine("[STATE] No valid state found, performing fresh deployment...");
                    //4.1. PUBLISHER Module
                    //var moduleXfighter = await PublishXfighterModuleAsync();
                    var moduleXfighter = await RetryAsync(() => PublishXfighterModuleAsync());
                    await Task.Delay(1500);
                    Console.WriteLine($"[DEPLOY] Module Factory Match XFighter : \n{moduleXfighter}");

                    //4.2. PUBLISHER Leaderboard
                    //var leaderboardAppId = await PublishAndCreateLeaderboardAppAsync();
                    var leaderboardAppId = await RetryAsync(() => PublishAndCreateLeaderboardAppAsync());
                    await Task.Delay(1500);
                    Console.WriteLine($"[DEPLOY] Leaderboard App ID :\n{leaderboardAppId}");

                    //4.3. PUBLISHER XFighter Factory
                    //var xfighterAppID = await DeployXfighterFactoryAsync();
                    var xfighterAppID = await RetryAsync(() => DeployXfighterFactoryAsync());
                    await Task.Delay(1500);
                    Console.WriteLine($"[DEPLOY] XFighter App ID : \n{xfighterAppID}");

                    //4.4.Tournament
                    var tournamentAppId = await RetryAsync(() => PublishAndCreateTournamentAppAsync());
                    await Task.Delay(1500);
                    Console.WriteLine($"[DEPLOY] Tournament App ID :\n{tournamentAppId}");

                    //4.5. PUBLISHER Module
                    //var moduleUserXfighter = await PublishUserXfighterModuleAsync();
                    var moduleUserXfighter = await RetryAsync(() => PublishUserXfighterModuleAsync());
                    await Task.Delay(1500);
                    Console.WriteLine($"[DEPLOY] Module UserXFighter : \n{moduleUserXfighter}");

                    // 4.6.PUBLISHER admin UserXFighter
                    var fungibleAppId = await RetryAsync(() => PublishAndCreateFungibleAppAsync());
                    await Task.Delay(1500);
                    Console.WriteLine($"[DEPLOY] Fungible AppId: \n{fungibleAppId}");

                    // 4.7.PUBLISHER admin UserXFighter
                    var friendAppId = await RetryAsync(() => PublishAndCreateFriendAppAsync());
                    await Task.Delay(1500);
                    Console.WriteLine($"[DEPLOY] Friend App ID :\n{friendAppId}");

                    //5. Set vào cấu hình LineraConfig
                    _config.LeaderboardAppId = leaderboardAppId;
                    _config.XFighterModuleId = moduleXfighter;
                    _config.XFighterAppId = xfighterAppID;
                    _config.TournamentAppId = tournamentAppId;
                    _config.UserXFighterModuleId = moduleUserXfighter;
                    _config.FungibleAppId = fungibleAppId;
                    _config.FriendAppId = friendAppId;

                    // Save persistent orchestrator state
                    _state = new OrchestratorState
                    {
                        PublisherChainId = _config.PublisherChainId,
                        PublisherOwner = _config.PublisherOwner,
                        XFighterModuleId = _config.XFighterModuleId,
                        XFighterAppId = _config.XFighterAppId,
                        LeaderboardAppId = _config.LeaderboardAppId,
                        TournamentAppId = _config.TournamentAppId,
                        UserXFighterModuleId = _config.UserXFighterModuleId,
                        FungibleAppId = _config.FungibleAppId,
                        FriendAppId = _config.FriendAppId,
                    };
                    SaveState();

                }
                else
                {
                    Console.WriteLine("[STATE] Using existing state, skipping deployment...");

                    // Load config từ state đã có
                    _config.LeaderboardAppId = _state.LeaderboardAppId;
                    _config.XFighterModuleId = _state.XFighterModuleId;
                    _config.XFighterAppId = _state.XFighterAppId;
                    _config.TournamentAppId = _state.TournamentAppId;
                    _config.UserXFighterModuleId = _state.UserXFighterModuleId;
                    _config.FungibleAppId = _state.FungibleAppId;
                    _config.FriendAppId = _state.FriendAppId;

                    Console.WriteLine($"[STATE] Loaded - PublisherChain: {_state.PublisherChainId}");
                    Console.WriteLine($"[STATE] Loaded - XFighterModuleApp: {_state.XFighterModuleId}");
                    Console.WriteLine($"[STATE] Loaded - XFighterApp: {_state.XFighterAppId}");
                    Console.WriteLine($"[STATE] Loaded - Leaderboard: {_state.LeaderboardAppId}");
                    Console.WriteLine($"[STATE] Loaded - TournamentAppId: {_state.TournamentAppId}");
                    Console.WriteLine($"[STATE] Loaded - UserXFighterModuleId: {_state.UserXFighterModuleId}");
                    Console.WriteLine($"[STATE] Loaded - Fungible AppId: {_state.FungibleAppId}");
                    Console.WriteLine($"[STATE] Loaded - Friend AppId: {_state.FriendAppId}");
                }

                //6. START SERVICE CONWAY / BACKUP MODE (critical)
                if (!_config.UseRemoteTestnet || _config.StartServiceWhenRemote)
                {
                    await StartLineraServiceAsync();
                    bool serviceConfirmed = false;
                    for (int i = 0; i < 5; i++)
                    {
                        if (IsServiceRunning())
                        {
                            serviceConfirmed = true;
                            Console.WriteLine($"[DEBUG] Service confirmed running (PID {_config.LineraServicePid})");
                            break;
                        }
                        await Task.Delay(1000);
                        Console.WriteLine($"[DEBUG] Waiting for service to stabilize... attempt {i + 1}/10");
                    }

                    if (!serviceConfirmed)
                    {
                        Console.WriteLine("[WARN] Service not confirmed running, but starting monitor anyway...");
                    }

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
                Console.WriteLine($"[ERROR] StartLineraNodeAsync failed: {ex}");
                throw; // preserve original stack trace for debugging
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
            Console.WriteLine($"[LINERA-CLI-OUTPUT] Deploy PublishXfighterModule respone: \n{result}");
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

            Console.WriteLine($"[LINERA-CLI-OUTPUT] Deploy PublishAndCreateLeaderboardApp respone: \n{result}");
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
            Console.WriteLine($"[LINERA-CLI-OUTPUT] Deploy XfighterFactory respone: \n{result}");
            return xfighterAppId;
        }

        public async Task<string> PublishAndCreateTournamentAppAsync()
        {
            var contractPath = Path.Combine(_config.TournamentPath, "tournament_contract.wasm");
            var servicePath = Path.Combine(_config.TournamentPath, "tournament_service.wasm");

            var result = await _cli.RunAndCaptureOutputAsync(
                "publish-and-create",
                contractPath,
                servicePath,
                _config.PublisherChainId!,
                "--json-argument", "null"
            );

            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("Failed to publish and create tournament app: no output returned.");

            var tournamentAppId = result.Trim();
            _config.TournamentAppId = tournamentAppId;

            Console.WriteLine($"[LINERA-CLI-OUTPUT] Deploy PublishAndCreateTournamentApp respone: \n{result}");
            return tournamentAppId;
        }

        // Phương thức sử dụng publish để tạo UserXfighter Module 
        public async Task<string> PublishUserXfighterModuleAsync()
        {
            var contractPath = Path.Combine(_config.UserXFighterPath, "userxfighter_contract.wasm");
            var servicePath = Path.Combine(_config.UserXFighterPath, "userxfighter_service.wasm");

            var result = await _cli.RunAndCaptureOutputAsync(
                "publish-module",
                contractPath,
                servicePath,
                _config.PublisherChainId!
            );
            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("No output returned when publishing UserXfighter module.");

            var moduleUserXFighter = result.Trim();
            _config.UserXFighterModuleId = moduleUserXFighter;

            Console.WriteLine($"[LINERA-CLI-OUTPUT] Deploy PublishUserXfighterModule respone: \n{result}");
            return moduleUserXFighter;
        }
        public async Task<string> PublishAndCreateFungibleAppAsync()
        {
            var contractPath = Path.Combine(_config.FungiblePath, "fungible_contract.wasm");
            var servicePath = Path.Combine(_config.FungiblePath, "fungible_service.wasm");

            // Lấy owner từ wallet
            var walletPath = _config.LineraWallet;
            Console.WriteLine($"[DEBUG] Wallet path: {walletPath}");

            if (string.IsNullOrEmpty(walletPath))
                throw new InvalidOperationException("Wallet path is not configured");

            var owner = GetPublicKeyFromWallet(walletPath);
            Console.WriteLine($"[INFO] Using owner: {owner}");
            // Format argument JSON đúng như bash success
            var argumentJson = $"{{\"accounts\":{{\"{owner}\":\"1000000.\"}}}}";
            Console.WriteLine($"[DEBUG] Argument JSON: {argumentJson}");

            var result = await _cli.RunAndCaptureOutputAsync(
                   "publish-and-create",
                   contractPath,
                   servicePath,
                   _config.PublisherChainId!,
                   "--json-parameters", "{\"ticker_symbol\":\"BET\"}",
                   "--json-argument", argumentJson
               );

            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("Failed to publish and create fungible app: no output returned.");

            var fungibleAppId = result.Trim();
            _config.FungibleAppId = fungibleAppId;

            Console.WriteLine($"[LINERA-CLI-OUTPUT] Deploy PublishAndCreateFungibleApp respone: \n{result}");
            return fungibleAppId;
        }

        // Phương thức sử dụng publish-and-create để tạo Leaderboard APPID (raw output fallback)
        public async Task<string> PublishAndCreateFriendAppAsync()
        {
            var contractPath = Path.Combine(_config.FriendPath, "friendxfighter_contract.wasm");
            var servicePath = Path.Combine(_config.FriendPath, "friendxfighter_service.wasm");

            var result = await _cli.RunAndCaptureOutputAsync(
                "publish-and-create",
                contractPath,
                servicePath,
                _config.PublisherChainId!,
                "--json-argument",
                "null"
            );

            if (string.IsNullOrWhiteSpace(result))
                throw new Exception("Failed to publish and create FriendXFighter app: no output returned.");

            var friendAppId = result.Trim();
            _config.FriendAppId = friendAppId;

            Console.WriteLine($"[LINERA-CLI-OUTPUT] Deploy PublishAndCreateFriendApp respone: \n{result}");
            return friendAppId;
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

        private static string GetPublicKeyFromWallet(string walletPath)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 5)
            {
                try
                {
                    if (!File.Exists(walletPath))
                    {
                        Console.WriteLine($"[DEBUG] Wallet file not found, waiting...");
                        Thread.Sleep(500);
                        continue;
                    }

                    var json = File.ReadAllText(walletPath);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        Console.WriteLine($"[DEBUG] Wallet file empty, waiting...");
                        Thread.Sleep(500);
                        continue;
                    }
                    using var doc = JsonDocument.Parse(json);

                    // Lấy chainId default trước
                    if (doc.RootElement.TryGetProperty("default", out var defaultChain))
                    {
                        var chainId = defaultChain.GetString();
                        if (!string.IsNullOrWhiteSpace(chainId))
                        {
                            // "chains" là Object, không phải Array
                            if (doc.RootElement.TryGetProperty("chains", out var chains) &&
                                chains.ValueKind == JsonValueKind.Object)
                            {
                                // Tìm chain bằng chainId
                                if (chains.TryGetProperty(chainId, out var chain) &&
                                    chain.TryGetProperty("owner", out var ownerEl))
                                {
                                    var publicKey = ownerEl.GetString();
                                    if (!string.IsNullOrWhiteSpace(publicKey))
                                        return publicKey!;
                                }
                                else
                                {
                                    Console.WriteLine($"[DEBUG] Chain {chainId} not found in chains");
                                }
                            }
                        }
                    }

                    Thread.Sleep(500);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DEBUG] GetPublicKeyFromWallet error: {ex.Message}");
                    Thread.Sleep(500);
                }
            }

            throw new InvalidOperationException($"Timeout: không tìm thấy public key trong {walletPath}");
        }
        #endregion

        #region RetryAsync - Linera Service Lifetime, Monitor Watchdog
        // Node Publish module, leaderboard Safe Guard & Retry
        private static async Task<T> RetryAsync<T>(Func<Task<T>> action, int maxAttempts = 10, int delayMs = 2000)
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
        public async Task StopAllLineraAsync()
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
                        Console.WriteLine($"Killing Linera PID={proc.Id}");
                        proc.Kill(entireProcessTree: true);
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
            await Task.Delay(500);
            Console.WriteLine("[DEBUG] RocksDB Unlocked - All Linera processes stopped.");
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
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LINERA-ORCH] Error checking service: {ex.Message} ");
                return false;
            }
        }
        // Service Guard Monitor (watchdog) implementation 
        private void StartServiceMonitor()
        {
            Console.WriteLine("[MONITOR] Service monitor started");
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
                            if (!IsServiceRunning())
                            {
                                Console.WriteLine("[MONITOR] Linera service not running. Attempting restart...");
                                try
                                {
                                    await StartLineraServiceAsync();
                                    consecutiveFailures = 0; // reset on success
                                    // small pause after successful start
                                    await Task.Delay(2000, token);
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
                                consecutiveFailures = 0;
                                await Task.Delay(3000, token);
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[MONITOR] Unexpected monitor error: {ex.Message}");
                            try { await Task.Delay(3000, token); } catch { break; }
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

        public async Task<HttpResponseMessage> PostSingleWithServiceWaitAsync(
         string url,
         Func<HttpContent> contentFactory,
         int waitSeconds = 10,
         int postTimeoutSeconds = 60,
         int maxAttempts = 3)
        {
            // 1) Đợi monitor báo service đã có PID ổn định
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
                        // backoff retry  Attempt 1 → delay = 300 ms Attempt 2 → 600 ms Attempt 3 → 900 ms
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
     

        #region State Orchestrator
        private void SaveState()
        {
            var json = JsonSerializer.Serialize(_state, JsonOptions.Write);
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            File.WriteAllText(_stateFile, json);
            Console.WriteLine($"[STATE] Saved to {_stateFile}");
        }
        private void LoadState()
        {
            if (!File.Exists(_stateFile))
            {
                Console.WriteLine("[STATE] No existing state file found. Fresh bootstrap required.");
                _state = null;
                return;
            }

            try
            {
                var json = File.ReadAllText(_stateFile);
                _state = JsonSerializer.Deserialize<OrchestratorState>(json, JsonOptions.Read);

                if (_state == null || !_state.IsValid)
                {
                    Console.WriteLine("[STATE] Invalid state file. Starting fresh.");
                    _state = null;
                    return;
                }

                Console.WriteLine($"[STATE] Loaded. PublisherChain={_state.PublisherChainId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[STATE ERROR] Failed to load: {ex.Message}. Starting fresh.");
                _state = null;
            }
        }
        #endregion

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
}