# ⚔️ XFighterZone — Linera Microchains Game

**XFighterZone** is a real-time esports and prediction metaverse powered by **Linera Microchains**.  
Each match runs on its own chain, securing gameplay state, rewards, and bets instantly — creating a new category of **real-time on-chain experiences** where blockchain feels alive.

Unity delivers immersive real-time gameplay. Linera provides transparent, verifiable, and trustless synchronization — from match creation to leaderboard aggregation, tokenized rewards, and prediction settlements.

## 📤 Buildathon Submission Checklist 
- [x] **Repository:** Public and includes full README + Contracts (.wasm) -
- [x] **Drive Folder:** Docs & Videos publicly accessible ([View](https://drive.google.com/drive/u/0/folders/1LuaF3wnbUNSHbUYezlq1Em-Vj9wC2cMF))
- [X] **Demo Videos:** Uploaded (Battle Demo, Stress Test, Chapter 0 Recap)
- [X] **Quick Demo Commands:** Validated on a clean environment (Localnet + Conway Testnet)
- [X] **Unity Build:** Public prebuilt package ([Download](https://drive.google.com/drive/folders/1ZiQi6FmIcawcz1K0RHRV2Ysc5XxgAciP?usp=sharing))
- [X] **CHANGELOG.md:** Included to track milestone updates
- [X] **Conway Testnet Verification:** Fully supported via UseRemoteTestnet = true

---

## ✅ Requirements
- `dotnet` (6/7+)
- `rust` + `wasm32-unknown-unknown` target (for building contracts)
- `linera` CLI (if running localnet)
- `curl`, `jq` (optional but used in examples)
- Unity prebuilt binaries (download via Drive link below)
- If testing Conway Testnet: network access and faucet credentials
- If testing with GraphQL service, please use `manual-deploy.txt`
  
## 🧠 Tech Stack
- **Blockchain:** Linera Microchains (Rust)
- **Backend:** ASP.NET Core (C#)
- **Frontend / Game Engine:** Unity (client stress test, client, server lobby, and server battle instances)
- **Networking:** GraphQL / REST  
- **Modes:** Linera Localnet & Conway Testnet

## ⚙️ Overview

This repository contains the full backend and contract stack:
- **Linera Orchestrator (C# / ASP.NET Core):**
  - Manages node lifecycle and Linera CLI operations (`net up`, `service`, `publish`, `create-application`, `open match chainId` and `submit match`).  
  - Coordinates match creation, updates, and tournament progress.  
- **Smart Contracts (Rust):**
  - `xfighter` — per-match state machine (combat results, players, outcomes).  
  - `leaderboard` — global ranking aggregator (cross-chain updates).  
  - `tournament` — manages bracket progression, entry, and reward flow.  
- **Prebuilt Unity Applications:**  
  ServerLobby / ServerBattle / Client / StressTest — ready for live testing (see Releases).  
- **GraphQL Layer:**  
  Primary API for client–server interaction (matchmaking, state updates, and tournament management).  

---

## 🧩 Core Features

 ✅ **Per-match microchain** — every game instance runs independently.  
 🌐 **Global leaderboard** — real-time aggregation via cross-chain messages.  
 💰 **Tournament Simulation/ Leaderboard** — tournament-based brackets.  
 🔍 **Searching Onchain** — searching and verifying match results via ChainId / Username.  
 🎮 **Unity integration** — seamless orchestration between gameplay and blockchain logic.

Unity (Client/Server) → Orchestrator API (C#) → Linera Microchains (Rust WASM)
                                       ↘︎ Leaderboard / Tournament Simulation
                                  
- **Unity Lobby / Battle Server:** Off-chain UX; communicates via REST/GraphQL with the orchestrator.  
- **Orchestrator:** Creates and manages per-match microchains, submits results, and coordinates tournament flow.  
- **Linera Contracts:** Manage match logic, leaderboard aggregation, and tournament rules.  
- **Cross-chain Messaging:** Connects matches → leaderboard.

---

## 📦 Repository Structure
```text
LineraOrchestrator_XFighterZone/
├── Contracts/
│   ├── xfighter/
│   ├── leaderboard/
│   └── tournament/
├── Orchestrator/
│   ├── Controllers/
│   ├── Models/
│   ├── Services/
│   └── Program.cs
└── README.md
```
## 🧱 Build Environment (contracts)
Contracts are compiled inside the Linera Protocol examples workspace.
Each contract (`xfighter`, `leaderboard`, `tournament`) must be listed under the `members` section of the Linera examples `Cargo.toml`, e.g.:
members = [
"amm",
"replaceable",
"game-of-life-challenge",
"xfighter",
"leaderboard",
"tournament"
]

### ⚙️ Orchestrator Contract Loading
For convenience, these precompiled artifacts are already copied into this repository under:
```
Contracts/xfighter/
Contracts/leaderboard/
Contracts/tournament/
```
Please copy all contract folder (`xfighter`, `leaderboard`, `tournament`) from Contracts folder into the linera-protocol/examples, then ~cd examples. 
If you prefer manual builds from your Linera SDK directory:
```
cd examples/xfighter && cargo build --release --target wasm32-unknown-unknown
cd ../leaderboard && cargo build --release --target wasm32-unknown-unknown
cd ../tournament && cargo build --release --target wasm32-unknown-unknown
```
The orchestrator automatically loads these .wasm files when deploying or initializing microchains.
No additional build steps are required for evaluation.

This produces all six .wasm artifacts under:
```/workspace/linera-protocol/examples/target/wasm32-unknown-unknown/release/```
Including:
```
xfighter_contract.wasm
xfighter_service.wasm
leaderboard_contract.wasm
leaderboard_service.wasm
tournament_contract.wasm
tournament_service.wasm
```
### 🧩 Orchestrator Configuration (LineraConfig.cs)
The orchestrator uses the LineraConfig model (Models/LineraConfig.cs) to locate the Linera CLI, wallet, and .wasm modules. 
By default, it points to a local Linera SDK build:
```
public string LineraCliPath = "/home/roycrypto/.cargo/bin/linera"; // <-- change your computer name or replace your path here
public string XFighterPath = "/mnt/d/workspace/linera-protocol/examples/target/wasm32-unknown-unknown/release";
public string LeaderboardPath = "/mnt/d/workspace/linera-protocol/examples/target/wasm32-unknown-unknown/release";
public string TournamentPath = "/mnt/d/workspace/linera-protocol/examples/target/wasm32-unknown-unknown/release";
```
You can change these paths if your environment differs (for example, when running on a different Linux user or build folder).
The orchestrator supports both:
Localnet mode — builds and runs linera net up locally
Testnet Conway mode — set UseRemoteTestnet = true to connect directly to Linera’s public Conway testnet
All other parameters (wallet, storage, keystore) can remain empty if using remote mode — the orchestrator will auto-fetch them when available.

---

## 🧭 Notes for Reviewers
✅ **Precompiled `.wasm` modules** are already included in this repository.  
✅ The **Orchestrator automatically loads and deploys** them — no manual linking required.  
✅ **Fully compatible** with both Linera Localnet and Conway Testnet environments.  
✅ **Unity builds** (Lobby, Battle servers, Client & Client StressTest) are provided separately for evaluation.  
✅ **Important:** Two Unity clients cannot run on the same machine — please run them on two separate computers for multiplayer testing.

### 🧠 Developer Notes (Optional Manual Interaction)
For developers who want to interact directly with the Linera GraphQL endpoint (port 8080),
standard CLI-based deployment and queries can be used instead of the Orchestrator service.

Example operations include:
- linera publish-module to register the xfighter, leaderboard, and tournament modules
- linera publish-and-create to deploy applications
- curl GraphQL queries to inspect child apps and match results
However, these steps are not required for evaluation, since the Orchestrator (port 5290) automates:
- Module publishing
- Application creation
- Cross-chain message handling
- Match and leaderboard synchronization
### ⚙️ Node Setup (Local vs Testnet Conway)
Before running the orchestrator, make sure your Linera node configuration matches your environment.
### 🧩 Program.cs Configuration

In `Program.cs`, adjust the following flags inside your configuration:

```csharp
UseRemoteTestnet = false,          // true = Conway Testnet mode, false = Local Backup mode
StartServiceWhenRemote = false,    // true = Conway Service mode, false = Local Service mode
```
✅ If using Local Mode:
Set both flags to false. The node will run locally and the orchestrator will automatically start linera net up.

✅ If using Testnet Conway:
Set both flags to true. The orchestrator will skip starting a local network and connect directly to the remote Conway testnet.

Conway Testnet Wallet Setup
Before running: ```curl -sS -X POST http://localhost:5290/linera/start-linera-node | jq .```
You must create and initialize your wallet for the Conway Testnet:
```
mkdir -p ~/.linera_testnet
export LINERA_WALLET=$HOME/.linera_testnet/wallet_0.json
export LINERA_KEYSTORE=$HOME/.linera_testnet/keystore_0.json
export LINERA_STORAGE=rocksdb:$HOME/.linera_testnet/client_0.db

linera wallet init --faucet https://faucet.testnet-conway.linera.net
linera wallet request-chain --faucet https://faucet.testnet-conway.linera.net
```

---
## 🎥 Demo Package (Buildathon Submission)
A full demonstration package for **XFighterZone** is available here:  
[Google Drive Folder — XFighterZone Buildathon Submission](https://drive.google.com/drive/u/0/folders/1LuaF3wnbUNSHbUYezlq1Em-Vj9wC2cMF)
This folder includes:

### 📄 Documents
1. **XFighterZone Overview (PDF)** —  
   Detailed introduction to the project, including architecture, gameplay concept, and technical roadmap.  
2. **XFighter Battle Plan (Application)** —  
   Explains the setup, fighter logic, keyboard inputs, Login Sence, Battle Scene, Lobby Sense and code integration behind.

### 🎬 Videos
1. **XFighter Battle Demo** — Live gameplay showing on-chain combat and synchronization via Linera microchains.  
2. **Full Chapter 0 Recap** — Overview of the first development phase, including milestones and completed features.  
3. **Stress Test (100 Clients / 50 Matches)** — Demonstrates orchestrator scalability, handling multiple simultaneous matches in real time.

## 🚀 Quick Demo & Playtest Resources
```
git clone https://github.com/2703roy/LineraOrchestrator_XFighterZone.git
cd LineraOrchestrator_XFighterZone
dotnet run || dotnet run --project LineraOrchestrator -- --demo
```
Then start node and test the endpoints as described below 👇
### 1. Start the Linera Node and Orchestrator
In WSL (Ubuntu) or terminal, start the orchestrator and local Linera node:
```bash
cd ~/LineraOrchestrator
curl -sS -X POST http://localhost:5290/linera/start-linera-node | jq .
```
This command boots a Linera localnet node and initializes wallets and storage automatically.
Unity apps will connect directly to this orchestrator endpoint (http://localhost:5290/).

2. Verify Leaderboard and Match APIs
You can check current leaderboard data:
`curl -sS -X POST http://localhost:5290/linera/get-leaderboard-data | jq .`

4. Create a Match (Microchain)
```
curl -sS -X POST http://localhost:5290/linera/open-and-create \
  -H "Content-Type: application/json" \
  -d '{"query":"mutation { openAndCreate }"}' | jq .
```
4. Submit Match Results
Replace the chainId (REPLACE_WITH_CHAIN_ID returned by open-and-create) below with the one returned returned from open-and-create:
```
curl -sS -X POST http://localhost:5290/linera/submit-match-result \
  -H "Content-Type: application/json" \
  -d '{
    "chainId": "ee9caeb62c462274f4b00e7e33526f86ae80fc7278436140b47bdf5a3a94fe0f",
    "matchResult": {
      "matchId": "match-test",
      "player1Username": "player3",
      "player2Username": "player4",
      "winnerUsername": "player3",
      "loserUsername": "player4",
      "durationSeconds": 10,
      "timestamp": 30,
      "player1Score": 0,
      "player2Score": 0,
      "mapName": "arena02",
      "matchType": "Rank"
    }
  }' | jq .
```
After submission, the result is committed on-chain and broadcasted to the leaderboard.
5. Polling / verify — Check leaderboard received results
After submitting, the system may need a cycle to verify & replicate to the leaderboard. Use the verify (polling) command:
```
curl -sS -X POST http://localhost:5290/linera/verify-match-result -d '{"matchId":"match-test"}' | jq .
```
If the API returns verified: true (or similar), the record has been posted to the leaderboard.
If not, poll every 3–5 seconds until it is verified.

🧰 Helper Debug Commands (Optional)
```
#Check service & config if you see "is_true" ready to go
curl -sS -X POST http://localhost:5290/linera/stop-linera-service | jq .
curl -sS -X POST http://localhost:5290/linera/linera-service-status | jq .
curl -sS http://localhost:5290/linera/linera-config | jq .
#Check all chains
curl -sS -X POST http://localhost:5290/linera/all-opened-chains | jq .
#Check player history
curl -sS "http://localhost:5290/linera/player-history/test?limit=20" | jq .
```

### Tournament Simulation
You can simulate an entire tournament with the following API calls:
```
# Start a new tournament simulation
curl -sS -X POST http://localhost:5290/linera/tournament/start

# Retrieve tournament metadata
curl -sS -X POST http://localhost:5290/linera/tournament/meta -d '{}' | jq .

# View tournament leaderboard
curl -X POST http://localhost:5290/linera/tournament/leaderboard \
  -H "Content-Type: application/json" \
  -d '{"query": "{ tournamentLeaderboard { player score } champion runnerUp }"}' | jq .

# Create a global leaderboard snapshot
curl -sS -X POST http://localhost:5290/linera/leaderboard/create-snapshot -d '{}' | jq .

# Create a new tournament manually
curl -X POST "http://localhost:5290/linera/tournament/create?name=XFighter_Test2h&startTime=0&endTime=7200"

# Check the match list
curl http://localhost:5290/tournament/match-list

# Or request via POST (returns JSON)
curl -X POST http://localhost:5290/linera/tournament/match-list -d '{}' | jq .
```
- The Tournament Simulator automatically:
- Creates mock brackets for 8 players,
- Runs through quarter/semi/final rounds,
- Publishes the Champion and Runner-Up to the on-chain leaderboard.

### 🕹️ Unity demo (prebuilt)
You can directly test the **Unity-powered Linera Orchestrator demo** without building the Unity project.
### Download the Prebuilt Unity App
Download from Google Drive:  👉 [XFighterZone_Unity_Build.zip](https://drive.google.com/drive/folders/1ZiQi6FmIcawcz1K0RHRV2Ysc5XxgAciP?usp=sharing)
Unzip the folder to your **C:** drive:  `C:\XFighterZone.LDW1\` and `C:\XFighterZone_StressTest.LDW1\`
The package contains:
- `ServerLobby.w1\ServerLobby.exe` — Launches the lobby, matchmaking, and leaderboard services.
- `ServerBattle.w1\ServerBattle.exe` — Spawns headless matches after matchmaking is complete; handles result submission.
- `Client.w1\XFighterZone.exe` — Player client for testing matchmaking and leaderboard sync.
- `ClientStressTest.w1\XFighterZone.exe` — Stress test match-making system (100 clients / 50 matches concurrently).
- `ManagerDashboard.w1\Linera GraphQL.exe` — Dashboard for match list, global/tournament leaderboards, and on-chain searches.

Flow:
```
1. Launch ServerLobby.exe
2. Open XFighterZone.exe (Client) — e.g. account: test1/test1 and test2/test2.
3. Matchmaking automatically finds opponents.
4. When a match is found, Orchestrator creates a match ChainId (microchain) → spawns ServerBattle headless.
5. After battle, ServerBattle → ServerLobby → Orchestrator → On-chain submit → Global Leaderboard update.
```
⚠️Note: 
- We've setup the Unity demo so that it can test local matches directly on the same machine.
- If Orchestrator runs in WSL and Unity runs on Windows, ensure Orchestrator binds to 0.0.0.0 or use host networking so clients can reach it.
 
### 🎮 Player Account Setup
👉Before launching the Unity client, please create a game account on the official portal:
1. Open & Click **“Signup”** at the [Signup page](https://xfighterzone.com/register/)
2. Confirm your registration via email (if prompted)
3. Once your account is created, you can use the same credentials to log in inside the Unity game client.

### Demo Flow Summary
`Unity Client → Orchestrator API (C# @ port 5290) → Linera Microchains (Rust WASM)  
               ↘︎ Leaderboard / Tournament      `

## 🧪 Quick Testing Guide
This section assumes you already have the Linera environment (CLI, localnet, or Conway Testnet) ready.
1. Run Orchestrator
```bash
cd LineraOrchestrator
dotnet build
dotnet run
```
2. Start Linera Node (Localnet)
`curl -sS -X POST http://localhost:5290/linera/start-linera-node | jq .`
Check for "isReady": true — meaning node + wallets are ready.

3. Create and Submit a Match
```
# create
curl -sS -X POST http://localhost:5290/linera/open-and-create | jq .
# submit
curl -sS -X POST http://localhost:5290/linera/submit-match-result \
  -H "Content-Type: application/json" \
  -d '{"matchResult": {...}}' | jq .

```
4. Verify Leaderboard Sync
Poll until verified:
`curl -sS -X POST http://localhost:5290/linera/verify-match-result -d '{"matchId":"match-test"}' |`
Check leaderboard data:
`curl -sS -X POST http://localhost:5290/linera/get-leaderboard-data | jq .`

### Here the link full testing if you see any failures on setup: 
[Full testing video](https://drive.google.com/file/d/1fgY-iQbCjfWdmJfzpwYZSVIfsukXsv_m/view?usp=sharing)
---

Contributing
We welcome contributions! Please feel free to open issues or submit pull requests.

### 📚 Credits & License
- Linera Protocol: [https://github.com/linera-io/linera-protocol](https://github.com/linera-io/linera-protocol?)
- License: Apache-2.0 components from Linera base code.

### 🔗 Resource Links
- **GitHub Repository:** [https://github.com/2703roy/LineraOrchestrator_XFighterZone](https://github.com/2703roy/LineraOrchestrator_XFighterZone)
- **X Fighter Contract Flow:** Demonstrates `setup Linera Node`, `Setup Linera Service` `open chain`, `create application`,  `recordScore` sends updates → leaderboard  
- **GitBook (Technical Docs):** [https://unitygame.gitbook.io/xfighterzone](https://unitygame.gitbook.io/xfighterzone)
- **Official Website:** [https://xfighterzone.com](https://xfighterzone.com)
- **Project Updates:** [@AriesLLC1](https://x.com/AriesLLC1) & [@XFighterZone](https://x.com/XFighterZone)

### 🔗 Linera Developer References
- [Linera.dev — Microchains & Architecture](https://linera.dev)  
- [Cross-chain Messaging APIs (prepare_message / send_to)](https://linera.dev/developers/backend/messages.html)
- [Deploying the Application](https://linera.dev/developers/backend/deploy.html)
- [Applications That Handle Assets (temporary chains / close_chain pattern)](https://linera.dev/developers/advanced_topics/assets.html)

### 🎥 Media & Technical Visuals
- **XFighterZone Draw Flow:** [Google Drive](https://drive.google.com/file/d/1HzD-v5oaNvf1aohTNV2mWNM5SQ0OI2QJ/view?usp=sharing)  
- **Full Playlists:** [https://youtu.be/tf6PkybCmtI?si=ZZ2fSCO7kMLJCqa5 ](https://youtu.be/tf6PkybCmtI?si=ZZ2fSCO7kMLJCqa5 )
  
Included
```
Demo — Server Battle
Stress Test (100 Players / 50 Matches)
Planning Management Full Chaper 0 (Buildathon Demo)
```

## 👥 Team & Contact
**Team:** Roystudios / roycrypto  
**Author:** [roycrypto](https://x.com/AriesLLC1)
**Discord:** @roycrypto  
**X (Twitter):** @AriesLLC1  
**Email:** tanlocn282@gmail.com

“We believe Linera’s Microchains are not just a performance innovation —
they are a new canvas for human interaction. XFighterZone connects real-time esports, prediction logic, and metaverse economies — where blockchain becomes truly alive. 
Thank you”


