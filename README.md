# ⚔️ XFighterZone — Real-Time Gaming & Prediction Metaverse on Linera

## 🎬 Live Demo
<p align="center">
  <a href="https://www.youtube.com/watch?v=121FG4qHrTo">
    <img src="https://img.youtube.com/vi/121FG4qHrTo/maxresdefault.jpg" width="720" alt="Watch the demo">
  </a>
</p>
Production Status: Full test on Conway Testnet with 8 demo accounts

## ⚡ Quick Start 
```text
# Clone repository
git clone https://github.com/2703roy/LineraOrchestrator_XFighterZone.git
cd LineraOrchestrator_XFighterZone

# Run complete system (Docker Server + LineraOrchestrator)
chmod +x start-docker.sh
./start-docker.sh
```
After 15-20 minutes, system will be ready.
[Client Build Link (Windown & MacOS)](https://drive.google.com/drive/folders/1c2bNHDPvi4NdZPiV9lNEmqXDyuo8FHiS?usp=sharing)

## 🗓️ Development Roadmap

| Wave | Focus | Status |
|------|--------|--------|
| **Wave 1** | MVP Foundation Gameplay, Onchain Integration | ✅ Complete |
| **Wave 2** | Multiplatform easy for tester, Friend List, Hero System, Normal/Rank Mode | ✅ Complete |
| **Wave 3** | Tournament Bracket Expansion, Users chain & Cross-chain Betting | 🔄 In Progress |
| **Wave 4** | Shaping the Metaverse, Betting System & Cross-chain Assets  | 🔄 Planned |
| **Wave 5** | Marketplace, Quest System & Advanced Prediction Pools | ⏳ Planned |
| **Wave 6** | Metaverse Foundation, Optimization, Full Decentralization & Social Features | ⏳ Planned |

## Tech Stack
| Layer | Technology |
|-------|-------------|
| **Blockchain** | Linera Protocol (Conway Testnet) |
| **Smart Contracts** | Rust 1.86.0, Linera SDK v0.15.3 |
| **Orchestrator** | C#, ASP.NET Core, GraphQL Client |
| **Game Server** | Custom UDP Server, Matchmaking & Real-time Networking |
| **Infrastructure** | Docker, Multi-wallet Management |

## Major Upgrades (Wave 2)
**Enhanced Architecture**
- Dual Priority Queues: High-priority Open Chain (150 slots) and low-priority Submit Match (500 slots) for optimized task flow.
- Persistent & Atomic Queue: File-based durable storage ensures no data loss and guarantees consistency through atomic .tmp replacements.

**Tournament System**
- Automated Leaderboard Snapshot: Captures top 8 players for bracket creation.
- Deterministic Bracket Generation: Ensures fair and reproducible matchups.
- Progressive Rounds: Quarterfinals → Semifinals → Finals.
- Dedicated Mutations: recordTournamentScore operations ensure precise tournament state updates

**Performance & Stability**
- +500% Throughput: Achieved via multi-queue architecture compared to single-queue models.
- Optimized Worker Management: Two workers per queue with only ~4% CPU usage.
- Graceful Shutdown & Recovery: Safe queue draining on exit with full state restoration.
- Enhanced Monitoring: Real-time queue metrics and detailed logging for diagnostics.

## System Architecture
Multi-Chain Gaming Infrastructure
```text
┌─────────────────────────────────────────────────────────────────┐
│                    PUBLISHER CHAIN (Wave 2)                     │
├─────────────────┬─────────────────┬─────────────────────────────┤
│   TOURNAMENT    │   USER-XFIGHTER │    GLOBAL LEADERBOARD       │
│     APP         │     MODULE      │        APP                  │
│                 │                 │                             │
│ - Tournament    │ - Bytecode for  │ - Real-time rankings        │
│   management    │   user chain    │ - Cross-tournament stats    │
│ - Betting engine│   deployment    │ - Player statistics         │
│ - Cross-chain   │                 │                             │
│   messaging     │                 │                             │
└─────────────────┴─────────────────┴─────────────────────────────┘
         │                   │                        │
         │ Cross-chain       │ Module reference       │ Cross-chain
         │ messages          │ for deployment         │ queries
         ▼                   ▼                        ▼
┌─────────────────────────────────────────────────────────────────┐
│                    USER CHAINS (Independent) (Wave 3)           │
├─────────────────┬─────────────────┬─────────────────────────────┤
│   USER 1        │   USER 2        │    USER N                   │
│   CHAIN         │   CHAIN         │    CHAIN                    │
├─────────────────┼─────────────────┼─────────────────────────────┤
│  USER-XFIGHTER  │  USER-XFIGHTER  │  USER-XFIGHTER              │
│     APP         │     APP         │     APP                     │
│                 │                 │                             │
│ - Asset         │ - Asset         │   - Asset management        │
│management       │ management      │  management                 │
│ - Bet processing│ - Bet processing│ - Bet processing            │
│ - Transaction   │ - Transaction   │ - Transaction               │
│   history       │   history       │   history                   │
└─────────────────┴─────────────────┴─────────────────────────────┘
```
## Real-Time Gaming Flow 
```text
Unity Client → Game Server → Orchestrator API → Linera Microchains (Rust WASM)

1. Player Login → User chain authentication
2. Matchmaking → Tournament chain coordination  
3. Real-time Battle → Unity gameplay with live physics
4. Result Verification → On-chain score recording
5. Automatic Payouts → Cross-chain betting settlements
6. Leaderboard Update → Global ranking aggregation
```
---
### Note for tester/reviewer
- **Test Accounts**: Use test1 to test8 (same username/password) for multiplayer battles.
- **Database Access**: Due to the SQL service provider’s security policy, the friend system requires access from an authorized public IP. If you encounter any issues connecting to the MySqlConnector host during testing, please provide your public IP so it can be whitelisted for the best experience. This will be replace by userchain on next wave.

### Media & Technical Visuals
- **XFighterZone Files:** [Google Drive](https://drive.google.com/drive/folders/1LuaF3wnbUNSHbUYezlq1Em-Vj9wC2cMF?usp=sharing)  
- **Full Playlists Buildathon Demo:** [https://youtu.be/tf6PkybCmtI?si=ZZ2fSCO7kMLJCqa5 ](https://youtu.be/tf6PkybCmtI?si=ZZ2fSCO7kMLJCqa5 )
  
## 📞 Support
**Team:** Roystudios / **Discord:** @roycrypto  
**Author:** [roycrypto](https://x.com/AriesLLC1)
