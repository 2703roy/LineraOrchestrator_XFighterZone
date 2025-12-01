// UserService.cs
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using LineraOrchestrator.Models;

namespace LineraOrchestrator.Services
{
    public class UserService
    {
        private readonly LineraConfig _config;
        private readonly LineraCliRunner _cli;
        private readonly HttpClient _httpClient;
        private readonly LineraOrchestratorService _orchestrator;
        public readonly string _baseUsersDir;

        // ==================== USER SERVICE MONITOR ====================
        private CancellationTokenSource? _userServiceMonitorCts;
        private Task? _userServiceMonitorTask;
        private readonly object _userServiceMonitorLock = new(); // Protect user service + restart
        public UserService(LineraConfig config, LineraCliRunner cli, LineraOrchestratorService orchestrator, HttpClient httpClient)
        {
            _config = config;
            _cli = cli;
            _orchestrator = orchestrator;
            _baseUsersDir = config.UserChainPath ?? throw new InvalidOperationException("UserChainPath should not null");
            Console.WriteLine($"[USER-SERVICE] BaseUsersDir: {_baseUsersDir}");
            _httpClient = httpClient;
        }

        #region User Authentication & Registration
        public async Task<(string chainId, string userInfo)> RegisterOrLoginAsync(string userName)
        {
            if (UserExists(userName))
            {
                Console.WriteLine($"[USER-LOGIN] Logging in existing user: {userName}");
                return await LoginExistingUserAsync(userName);
            }
            else
            {
                Console.WriteLine($"[USER-REGISTER] Registering new user: {userName}");
                return await RegisterNewUserAsync(userName);
            }
        }

        private async Task<(string chainId, string userInfo)> LoginExistingUserAsync(string userName)
        {
            /// 1. Tìm kiếm chainId trước, nếu không có sẽ xóa đăng kí tạo lại
            string existingChainId;
            try
            {            
                existingChainId = GetUserChainIdFromLocal(userName);
                Console.WriteLine($"[USER] Checking {userName}. {existingChainId}...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[USER-LOGIN-ERROR] Error getting chainId for user {userName}: {ex.Message}");
                Console.WriteLine($"[USER-LOGIN-DEBUG] Re-registering user...");
                // Xóa user directory cũ và đăng ký lại
                await CleanupUserDirectory(userName);
                return await RegisterNewUserAsync(userName);
            }

            var (wallet, keystore, storage) = CreateUserContext(userName);
            var publicKey = GetPublicKeyFromWallet(wallet);

            Console.WriteLine($"[USER] User {userName} already exists, returning existing chain: {existingChainId}");

            bool needRedeploy = false;
            try
            {
                GetUserAppId(userName);
                Console.WriteLine($"[USER-LOGIN-DEBUG] AppId found, no redeploy needed");
            }
            catch
            {
                Console.WriteLine($"[USER] User {userName} missing AppId, redeploying...");
                await DeployUserXfighterAppAsync(userName, existingChainId);
                needRedeploy = true;
            }

            // LUÔN DỪNG MONITOR TRƯỚC KHI START SERVICE MỚI
            await StopUserServiceMonitorAsync(userName);

            int userPort;
            bool serviceStarted = false;

            try
            {
                userPort = GetUserPortFromStorage(userName);
                bool isServiceRunning = _cli.IsUserServiceRunning(userPort);

                if (!isServiceRunning || needRedeploy)
                {
                    Console.WriteLine($"[USER-SERVICE] {(needRedeploy ? "Redeploy detected" : "Service not running")}, starting on port {userPort}...");

                    // Kill service cũ nếu có
                    try
                    {
                        Process.Start("pkill", $"-f \"linera.*service.*{userPort}\"")?.WaitForExit(1000);
                        await Task.Delay(500); // Đợi process cleanup
                        Console.WriteLine($"[USER-SERVICE-DEBUG] Killed existing service on port {userPort}");
                    }
                    catch { }

                    serviceStarted = await _cli.StartUserBackgroundService(wallet, keystore, storage, userPort);

                    if (serviceStarted)
                    {
                        Console.WriteLine($"[USER-SERVICE] Service started successfully on port {userPort}");
                    }
                    else
                    {
                        Console.WriteLine($"[USER-SERVICE] Failed to start service on port {userPort}");
                    }
                }
                else
                {
                    Console.WriteLine($"[USER-SERVICE] Service already running on port {userPort}");
                    serviceStarted = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[USER-SERVICE] No service info found, starting new service: {ex.Message}");
                userPort = 8082 + Math.Abs(userName.GetHashCode() % 5000);

                // Tìm port trống
                while (_cli.IsPortInUse(userPort))
                {
                    userPort++;
                    if (userPort > 13081) userPort = 8082;
                }

                serviceStarted = await _cli.StartUserBackgroundService(wallet, keystore, storage, userPort);

                if (serviceStarted)
                {
                    SaveUserServiceInfo(userName, userPort);
                    Console.WriteLine($"[USER-SERVICE] New service started on port {userPort}");
                }
            }
            if (serviceStarted)
            {
                await Task.Delay(3000);
                StartUserServiceMonitor(userName);
                Console.WriteLine($"[USER-SERVICE] Monitor started for {userName}");
            }
            else
            {
                Console.WriteLine($"[USER-SERVICE] WARNING: Service not started," +
                    $" monitor will not run for {userName}");
            }

            var userInfo = $"{{\"chainId\": \"{existingChainId}\", \"publicKey\": \"{publicKey}\"}}";
            
            Console.WriteLine($"[USER-LOGIN-OUTPUT] Login completed:");
            Console.WriteLine($"[SUCCESS] UserName: {userName}");
            Console.WriteLine($"[SUCCESS] ChainId: {existingChainId}");
            Console.WriteLine($"[SUCCESS] PublicKey: {publicKey}");
            Console.WriteLine($"[SUCCESS] ServicePort: {userPort}");
            Console.WriteLine($"[SUCCESS] Wallet: {wallet}");
            Console.WriteLine($"[SUCCESS] Keystore: {keystore}");
            Console.WriteLine($"[SUCCESS] Storage: {storage}");

            return (existingChainId, userInfo);
        }

        private async Task<(string chainId, string userInfo)> RegisterNewUserAsync(string userName)
        {
            Console.WriteLine($"[USER-REGISTER] Starting registration for new user: {userName}");

            try
            {
                // 1. Tạo user chain độc lập - lấy cả chainId và publicKey
                var sw = Stopwatch.StartNew(); // Đo lường sức mạnh Linera
                var (chainId, publicKey) = await CreateIndependentUserChainAsync(userName);
                sw.Stop();
                Console.WriteLine($"[USER-REGISTER] Chain created: {chainId}, publicKey: {publicKey}");
                Console.WriteLine($"[LINERA] Chain creation detected in {sw.ElapsedMilliseconds} ms");

                // 2. Deploy UserXFighter lên user chain
                await DeployUserXfighterAppAsync(userName, chainId);
                Console.WriteLine($"[USER-REGISTER] Deployment completed for {userName}");

                // 3. CHỈ START SERVICE & MONITOR SAU KHI DEPLOY THÀNH CÔNG
                var (wallet, keystore, storage) = CreateUserContext(userName);
                int hash = userName.GetHashCode();
                if (hash == int.MinValue) hash = 0;
                var userPort = 8082 + Math.Abs(hash % 5000);
                while (_cli.IsPortInUse(userPort))
                {
                    userPort++; // tăng port +1 nếu đang bận
                    if (userPort > 13081) // giới hạn max port
                        userPort = 8082;  // quay về đầu range
                }
                await _cli.StartUserBackgroundService(wallet, keystore, storage, userPort);

                SaveUserServiceInfo(userName, userPort);
                StartUserServiceMonitor(userName);
                Console.WriteLine($"[USER-REGISTER] Starting User service & Monitor for {userName} on port {userPort}");

                var userInfo = $"{{\"chainId\": \"{chainId}\", \"publicKey\": \"{publicKey}\"}}";

                Console.WriteLine($"[USER-REGISTER-OUTPUT] Registration completed:");
                Console.WriteLine($"[SUCCESS] UserName: {userName}");
                Console.WriteLine($"[SUCCESS] ChainId: {chainId}");
                Console.WriteLine($"[SUCCESS] PublicKey: { publicKey}");
                Console.WriteLine($"[SUCCESS] ServicePort: {userPort}");
                Console.WriteLine($"[SUCCESS] Wallet: {wallet}");
                Console.WriteLine($"[SUCCESS] Keystore: {keystore}");
                Console.WriteLine($"[SUCCESS] Storage: {storage}");

                return (chainId, userInfo);
            }
            catch (Exception ex)
            {
                // Cleanup nếu có lỗi trong quá trình register
                Console.WriteLine($"[USER-REGISTER-ERROR] Registration failed for {userName}: {ex.Message}");
                Console.WriteLine($"[USER-REGISTER-DEBUG] ExitCode: 1");
                await CleanupUserDirectory(userName);
                throw;
            }
        }

        private bool UserExists(string userName)
        {
            var userDir = Path.Combine(_baseUsersDir, userName);
            var walletPath = Path.Combine(userDir, "wallet.json");
            return File.Exists(walletPath);
        }

        public async Task<(string ChainId, string PublicKey)> CreateIndependentUserChainAsync(string userName)
        {
            var (wallet, keystore, storage) = CreateUserContext(userName);

            if (string.IsNullOrEmpty(_config.FaucetUrl))
            {
                throw new InvalidOperationException("FaucetUrl is not configured");
            }

            int maxRetries = 3;

            for (int retryCount = 0; retryCount < maxRetries; retryCount++)
            {
                try
                {
                    // đảm bảo thư mục tồn tại
                    Directory.CreateDirectory(Path.Combine(_baseUsersDir, userName));

                    // Init wallet nếu chưa tồn tại
                    if (!UserExists(userName))
                    {
                        Console.WriteLine($"[USER-CHAIN] Attempt {retryCount + 1}: Initializing wallet for {userName}");
                        await _cli.RunWithOptionsAsync(
                            wallet, keystore, storage,
                            "wallet", "init", "--faucet", _config.FaucetUrl
                        );
                    }

                    // Request chain
                    Console.WriteLine($"[USER-CHAIN] Attempt {retryCount + 1}: Requesting chain for {userName}");
                    var chainIdOutput = await _cli.RunWithOptionsAsync(
                        wallet, keystore, storage,
                        "wallet", "request-chain", "--faucet", _config.FaucetUrl
                    );
                    Console.WriteLine($"[USER-CHAIN] Waiting 5 seconds for faucet to complete...");
                    await Task.Delay(5000);

                    var lines = chainIdOutput.Split('\n');
                    var chainId = lines[0].Trim();
                    var publicKey = lines.Length > 1 ? lines[1].Trim() : "";

                    if (!string.IsNullOrEmpty(chainId) && !string.IsNullOrEmpty(publicKey))
                    {
                        Console.WriteLine($"[USERCHAIN-CLI-OUTPUT] Linera CreateChain response:");
                        Console.WriteLine($"[SUCCESS] ChainId: {chainId}");
                        Console.WriteLine($"[SUCCESS] PublicKey: {publicKey}");
                        return (chainId, publicKey);
                    }

                    throw new InvalidOperationException("Faucet returned empty result");
                }
                catch (Exception ex)
                {
                    if (retryCount == maxRetries - 1)
                    {
                        Console.WriteLine($"[USER-CHAIN-ERROR] All attempts failed, cleaning up...");
                        await CleanupUserDirectory(userName);
                        throw new InvalidOperationException($"Failed to create user chain after {maxRetries} attempts: {ex.Message}");
                    }

                    Console.WriteLine($"[USER-CHAIN] Retrying Creating chain in 3 seconds...");
                    // Đợi trước khi retry, KHÔNG cleanup
                    await Task.Delay(3000);
                }
            }

            throw new InvalidOperationException("Unexpected error");
        }

        public async Task<string> DeployUserXfighterAppAsync(string userName, string userChainId)
        {
            var (wallet, keystore, storage) = CreateUserContext(userName);

            var contractPath = Path.Combine(_config.UserXFighterPath, "userxfighter_contract.wasm");
            var servicePath = Path.Combine(_config.UserXFighterPath, "userxfighter_service.wasm");

            var parameters = $@"
                {{
                    ""local_tournament_app_id"": ""{_config.TournamentAppId}"",
                    ""fungible_app_id"": ""{_config.FungibleAppId}"",
                    ""tournament_owner"": ""{_config.PublisherOwner}"",
                    ""publisher_chain_id"": ""{_config.PublisherChainId}"",
                    ""friend_app_id"": ""{_config.FriendAppId}""
                }}";

            //Console.WriteLine($"[INFO] Deploying UserXfighter ApplicationID for: \n{userName} on microchain: {userChainId}");

            var result = await _cli.RunWithOptionsAsync(
                wallet, keystore, storage,
                "publish-and-create",
                contractPath,
                servicePath,
                userChainId,
                "--json-argument", "null",
                "--json-parameters", parameters
            );

            var userXFighterAppId = result.Trim();
            Console.WriteLine($"[USER-DEPLOY-OUTPUT] Deploy UserXfighterApp response:");
            Console.WriteLine($"[SUCCESS] UserName: {userName}");
            Console.WriteLine($"[SUCCESS] ChainId: {userChainId}");
            Console.WriteLine($"[SUCCESS] AppId: {userXFighterAppId}");


            SaveUserAppId(userName, userXFighterAppId);
            return userXFighterAppId;
        }
        #endregion

        #region User List
        public List<UserInfoResponse> GetUserList()
        {
            try
            {
                if (!Directory.Exists(_baseUsersDir))
                    return [];

                var users = new List<UserInfoResponse>();
                foreach (var userDir in Directory.GetDirectories(_baseUsersDir))
                {
                    string userName = Path.GetFileName(userDir);

                    var userChainId = GetUserChainIdFromLocal(userName);
                    var walletPath = Path.Combine(userDir, "wallet.json");
                    var publicKey = GetPublicKeyFromWallet(walletPath);
                    users.Add(new UserInfoResponse
                    {
                        UserName = userName,
                        UserChainId = userChainId,
                        PublicKey = publicKey
                    });
                }
                Console.WriteLine($"[USER-LIST-OUTPUT] Found {users.Count} users");
                return users;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[USER-LIST-ERROR] GetUserList error: {ex.Message}");
                return [];
            }
        }
        #endregion

        #region Helper Methods
        public static string GetPublicKeyFromWallet(string walletPath)
        {
            //Console.WriteLine($"[USER-WALLET] Getting user public key from: {walletPath}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 5)
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

        public string GetUserChainIdFromLocal(string userName)
        {
            var userDir = Path.Combine(_baseUsersDir, userName);
            var walletPath = Path.Combine(userDir, "wallet.json");

            if (!File.Exists(walletPath))
                throw new Exception($"User {userName} not found at path: {walletPath}");

            return _orchestrator.GetDefaultChainFromWalletFile(walletPath);
        }

        public (string Wallet, string Keystore, string Storage) CreateUserContext(string userName)
        {
            var userDir = Path.Combine(_baseUsersDir, userName);
            Directory.CreateDirectory(userDir);

            return (
                Wallet: Path.Combine(userDir, "wallet.json"),
                Keystore: Path.Combine(userDir, "keystore.json"),
                Storage: $"rocksdb:{Path.Combine(userDir, "client.db")}"
            );
        }
        public void SaveUserAppId(string userName, string appId)
        {
            var userDir = Path.Combine(_baseUsersDir, userName);
            Directory.CreateDirectory(userDir);
            var appIdPath = Path.Combine(userDir, "appid.txt");
            File.WriteAllText(appIdPath, appId);
        }
        public string GetUserAppId(string userName)
        {
            var userDir = Path.Combine(_baseUsersDir, userName);
            var appIdPath = Path.Combine(userDir, "appid.txt");

            if (!File.Exists(appIdPath))
                throw new Exception($"User {userName} AppId not found");

            return File.ReadAllText(appIdPath).Trim();
        }
        private void SaveUserServiceInfo(string userName, int port)
        {
            var userDir = Path.Combine(_baseUsersDir, userName);
            File.WriteAllText(Path.Combine(userDir, "service_port.txt"), port.ToString());
            Console.WriteLine($"[USER-SERVICE-DEBUG] Saved service port for {userName}: {port}");

        }

        public int GetUserPortFromStorage(string userName)
        {
            var portFile = Path.Combine(_baseUsersDir, userName, "service_port.txt");
            var port = int.Parse(File.ReadAllText(portFile));
            Console.WriteLine($"[USER-SERVICE-DEBUG] Retrieved service port for {userName}: {port}");
            return port;

        }
        // Timeout → kill process → retry → nếu vẫn fail → CreateIndependentUserChainAsync gọi CleanupUserDirectory
        private async Task CleanupUserDirectory(string userName)
        {
            Console.WriteLine($"[CLEANUP] Starting cleanup for user: {userName}");
            try
            {
                var userDir = Path.Combine(_baseUsersDir, userName);
                if (Directory.Exists(userDir))
                {
                    Console.WriteLine($"[CLEANUP] Removing user directory: {userDir}");

                    // Dừng service monitor trước
                    await StopUserServiceMonitorAsync(userName);

                    // Kill service đang chạy
                    try
                    {
                        var userPort = GetUserPortFromStorage(userName);
                        Process.Start("pkill", $"-f \"linera.*service.*{userPort}\"")?.WaitForExit(5000);
                        Console.WriteLine($"[USER-CLEANUP-DEBUG] Killed service on port {userPort}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[CLEANUP] Error killing service for {userName}: {ex.Message}");
                    }

                    // Xóa thư mục
                    Directory.Delete(userDir, true);
                    Console.WriteLine($"[CLEANUP] Successfully removed user directory: {userDir}");

                    // Đợi một chút để hệ thống cleanup
                    await Task.Delay(1000);
                }
                else
                {
                    Console.WriteLine($"[USER-CLEANUP-DEBUG] User directory not found: {userDir}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CLEANUP] Error cleaning up user directory for {userName}: {ex.Message}");
            }
        }

        public void StartUserServiceMonitor(string userName)
        {
            int userPort;
            try
            {
                userPort = GetUserPortFromStorage(userName);
            }
            catch
            {
                Console.WriteLine($"[USER-MONITOR] No service info for {userName}, monitor will not start.");
                return;
            }

            lock (_userServiceMonitorLock)
            {
                if (_userServiceMonitorTask != null && !_userServiceMonitorTask.IsCompleted)
                {
                    Console.WriteLine($"[USER-MONITOR] Monitor already running for {userName}.");
                    return;
                }

                _userServiceMonitorCts = new CancellationTokenSource();
                var token = _userServiceMonitorCts.Token;

                _userServiceMonitorTask = Task.Run(async () =>
                {
                    int restartCount = 0;
                    const int maxRestarts = 3;

                    while (!token.IsCancellationRequested && restartCount < maxRestarts)
                    {
                        // KIỂM TRA NHIỀU LẦN ĐỂ TRÁNH FALSE NEGATIVE
                        bool isRunning = false;
                        for (int i = 0; i < 3; i++)
                        {
                            isRunning = _cli.IsUserServiceRunning(userPort);
                            if (isRunning) break;
                            await Task.Delay(1000, token); // Thử lại sau 1 giây
                        }

                        if (!isRunning)
                        {
                            restartCount++;
                            Console.WriteLine($"[USER-MONITOR] Service {userName} not running. Restart #{restartCount}/{maxRestarts}");

                            var (wallet, keystore, storage) = CreateUserContext(userName);
                            bool started = await _cli.StartUserBackgroundService(wallet, keystore, storage, userPort);

                            if (started)
                            {
                                restartCount = 0;
                                Console.WriteLine($"[USER-MONITOR] Service {userName} restarted successfully");
                            }

                            await Task.Delay(5000, token);
                        }
                        else
                        {
                            // SERVICE ĐANG CHẠY → RESET COUNTER
                            if (restartCount > 0)
                            {
                                Console.WriteLine($"[USER-MONITOR] Service {userName} is running. Reset restart count.");
                                restartCount = 0;
                            }
                            await Task.Delay(2000, token);
                        }
                    }

                    if (restartCount >= maxRestarts)
                    {
                        Console.WriteLine($"[USER-MONITOR] Max restarts reached for {userName}. Monitor stopped.");
                    }
                    else
                    {
                        Console.WriteLine($"[USER-MONITOR] Monitor stopped for {userName}");
                    }
                }, token);

                Console.WriteLine($"[USER-MONITOR] Started monitor for {userName} on port {userPort}");
            }
        }

        public async Task StopUserServiceMonitorAsync(string userName, int waitMs = 5000)
        {
            Console.WriteLine($"[USER-MONITOR-DEBUG] Stopping monitor for user: {userName}");
            CancellationTokenSource? ctsCopy;
            Task? taskCopy;

            lock (_userServiceMonitorLock)
            {
                ctsCopy = _userServiceMonitorCts;
                taskCopy = _userServiceMonitorTask;

                if (ctsCopy != null && !ctsCopy.IsCancellationRequested)
                {
                    ctsCopy.Cancel();
                    Console.WriteLine($"[USER-MONITOR-DEBUG] Cancellation requested for {userName}");
                }
            }

            if (taskCopy != null)
            {
                await Task.WhenAny(taskCopy, Task.Delay(waitMs));
                Console.WriteLine($"[USER-MONITOR-DEBUG] Monitor task stopped for {userName}");
            }

            lock (_userServiceMonitorLock)
            {
                try { _userServiceMonitorCts?.Dispose(); } catch { }
                _userServiceMonitorCts = null;
                _userServiceMonitorTask = null;
            }

            Console.WriteLine($"[USER-MONITOR] Monitor stopped for {userName}");
        }


        public async Task<HttpResponseMessage> PostSingleWithUserServiceWaitAsync(
            string userName,
            string url,
            Func<HttpContent> contentFactory,
            int waitSeconds = 10,
            int postTimeoutSeconds = 60,
            int maxAttempts = 3)
        {
            // 1) Đợi user service monitor báo service đã ổn định
            var ready = await WaitForUserServiceViaMonitorAsync(userName, waitSeconds, pollMs: 500, stableMs: 1000).ConfigureAwait(false);
            if (!ready)
                throw new InvalidOperationException($"User service {userName} not ready after wait (monitor).");

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
                        Console.WriteLine($"[USER-WARN] POST to {url} returned 503 (attempt {attempt}/{maxAttempts}). Waiting user service monitor then retrying...");
                        // chờ user service monitor báo ổn định (nhỏ hơn) — cho service 1-2s để hoàn tất restart
                        await WaitForUserServiceViaMonitorAsync(userName, timeoutSeconds: Math.Min(5, waitSeconds), pollMs: 500, stableMs: 1000).ConfigureAwait(false);
                        // backoff retry Attempt 1 → delay = 300 ms Attempt 2 → 600 ms Attempt 3 → 900 ms
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
                        Console.WriteLine($"[USER-ERROR] POST to {url} timed out after attempt {attempt}.");
                        throw; // bubble lên để caller xử lý (existing behavior)
                    }

                    Console.WriteLine($"[USER-WARN] POST to {url} timed out on attempt {attempt}. Waiting user service monitor then retrying...");
                    await WaitForUserServiceViaMonitorAsync(userName, timeoutSeconds: Math.Min(5, waitSeconds), pollMs: 500, stableMs: 1000).ConfigureAwait(false);
                    await Task.Delay(2000 + 300 * attempt).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex) // mạng/IO transient
                {
                    if (attempt >= maxAttempts)
                    {
                        Console.WriteLine($"[USER-ERROR] POST to {url} failed after {attempt} attempts: {ex.Message}");
                        throw;
                    }

                    Console.WriteLine($"[USER-WARN] POST to {url} exception on attempt {attempt}: {ex.Message}. Waiting user service monitor then retrying...");
                    await WaitForUserServiceViaMonitorAsync(userName, timeoutSeconds: Math.Min(5, waitSeconds), pollMs: 500, stableMs: 1000).ConfigureAwait(false);
                    await Task.Delay(2000 + 300 * attempt).ConfigureAwait(false);
                    continue;
                }
            }
        }

        public async Task<bool> WaitForUserServiceViaMonitorAsync(
            string userName,
            int timeoutSeconds = 10,
            int pollMs = 1000,
            int stableMs = 2000)
        {
            Console.WriteLine($"[USER-SERVICE-WAIT-DEBUG] Waiting for service stabilization: {userName}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool lastRunning = false;
            var stableStart = DateTime.MinValue;

            while (sw.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
            {
                int userPort;
                try
                {
                    userPort = GetUserPortFromStorage(userName);
                }
                catch
                {
                    await Task.Delay(pollMs);
                    continue; // chưa có info, đợi lần tiếp theo
                }

                bool running = _cli.IsUserServiceRunning(userPort);

                if (running)
                {
                    if (!lastRunning)
                    {
                        lastRunning = true;
                        stableStart = DateTime.UtcNow;
                        Console.WriteLine($"[USER-SERVICE-WAIT-DEBUG] Service started, waiting for stabilization...");
                    }
                    else if ((DateTime.UtcNow - stableStart).TotalMilliseconds >= stableMs)
                    {
                        Console.WriteLine($"[USER-SERVICE-WAIT-SUCCESS] Service stabilized for {userName}");
                        return true; // đã ổn định
                    }
                }
                else
                {
                    lastRunning = false;
                    stableStart = DateTime.MinValue;
                }

                await Task.Delay(pollMs);
            }
            Console.WriteLine($"[USER-SERVICE-WAIT-ERROR] Timeout waiting for service stabilization: {userName}");
            return false; // timeout mà service chưa ổn định
        }

        /// Leaderboard check user Userchain
        public string GetUserNameByUserChain(string userChain)
        {
            var users = GetUserList();
            var user = users.FirstOrDefault(u => u.UserChainId == userChain);
            var userName = user?.UserName ?? string.Empty;
            Console.WriteLine($"[USER-LOOKUP-DEBUG] UserChain {userChain} -> UserName: {userName}");
            return userName;
        }
        #endregion
    }
}