# ⚔️ XFighterZone — Real-Time Gaming & Prediction Metaverse on Linera

## 🎬 Live Demo
<p align="center">
  <a href="https://www.youtube.com/watch?v=121FG4qHrTo">
    <img src="https://img.youtube.com/vi/121FG4qHrTo/maxresdefault.jpg" width="720" alt="Watch the demo">
  </a>
</p>
Production Status: Full test on Conway Testnet with 8 demo accounts, kindly use test1 to test8 (same username/password) for multiplayer battles

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

## Tech Stack
| Layer | Technology |
|-------|-------------|
| **Blockchain** | Linera Protocol (Conway Testnet) |
| **Smart Contracts** | Rust 1.86.0, Linera SDK v0.15.3 |
| **Orchestrator** | C#, ASP.NET Core, GraphQL Client |
| **Game Server** | Custom UDP Server, Real-time Matchmaking |
| **Infrastructure** | Docker, Multi-wallet Management |

## Development Roadmap

| Wave | Focus | Status |
|------|--------|--------|
| **Wave 1** | MVP Foundation: Core Gameplay, On-chain Integration | ✅ Complete |
| **Wave 2** | Multiplatform Support, Friend System, Hero System, Normal/Rank Modes | ✅ Complete |
| **Wave 3** | Tournament Expansion, User Chains & Cross-chain Betting | 🔄 In Progress |
| **Wave 4** | Metaverse Lobby, Prediction Bet System & Cross-chain Asset Management  | 🔄 In Progress |
| **Wave 5** | Marketplace, Quest System & Advanced Prediction Pools | ⏳ Planned |
| **Wave 6** | Full Metaverse: Decentralization & Social Features | ⏳ Planned |

### Wave 2 — Major Upgrades: 
`CHANGELOG.md`

## System Architecture
Multi-Chain Gaming Infrastructure
```text
┌──────────────────────────────────────────────────────────────────────┐
│                    PUBLISHER CHAIN (Wave 2)                          │
├─────────────────┬─────────────────┬─────────────────┬────────────────┤
│   TOURNAMENT    │   USER-XFIGHTER │    XFIGHTER     │  GLOBAL        │
│     APP         │     MODULE      │     APP         │ LEADERBOARD    │
│                 │                 │                 │   APP          │
├─────────────────┼─────────────────┼─────────────────┼────────────────┤
│ - Tournament    │ - Bytecode for  │ - Matchmaking   │ - Real-time    │
│   management    │   user chain    │ - Real-time     │ rankings       │
│ - Betting       │   deployment    │   match         │ - Cross-chain  │
│   engine        │                 │ - Battle results│   statistics   │
│ - Cross-chain   │                 │   recording     │ - Player stats │
│   messaging     │                 │ - Cross-chain   │                │
│                 │                 │   coordination  │                │
└─────────────────┴─────────────────┴─────────────────┴────────────────┘
         │                   │              │              │
         │ Cross-chain       │ Module       │ Cross-app    │ Cross-chain
         │ messages          │ reference    │ calls        │ queries
         │ (Wave 3)          │ (Wave 3)     │ (Active)     │ (Active)
         ▼                   ▼              ▼              ▼
┌──────────────────────────────────────────────────────────────────────┐
│                    USER CHAINS (Wave 3 - Planned)                    │
├─────────────────┬─────────────────┬─────────────────┬────────────────┤
│   USER 1        │   USER 2        │    USER N       │   BATTLE FLOW  │
│   CHAIN         │   CHAIN         │    CHAIN        │   (Wave 3)     │
├─────────────────┼─────────────────┼─────────────────┼────────────────┤
│  USER-XFIGHTER  │  USER-XFIGHTER  │  USER-XFIGHTER  │ 1. User Chain  │
│     APP         │     APP         │     APP         │    sends       │
│                 │                 │                 │    RecordScore │
│ - Asset mgmt    │ - Asset mgmt    │ - Asset mgmt    │ 2. Xfighter    │
│ - Bet processing│ - Bet processing│ - Bet processing│    receives &  │
│ - Transaction   │ - Transaction   │ - Transaction   │    processes   │
│   history       │   history       │   history       │ 3. Leaderboard │
│ - Battle auth   │ - Battle auth   │ - Battle auth   │    updates     │
└─────────────────┴─────────────────┴─────────────────┴────────────────┘
```
## Real-Time Gaming Flow 
```text
Unity Client → Game Server → Orchestrator API → Linera Microchains (Rust WASM)

1. Player Login → User chain authentication
2. Start Matchmaking → XFighter App on Publisher Chain  
3. Leaderboard Snapshot → Tournament chain coordination
4. Real-time Battle → Unity gameplay with live physics
5. Result Verification → On-chain score recording
6. Automatic Payouts → Cross-chain betting settlements
7. Leaderboard Update → Global ranking aggregation
```
---
### Testing Notes
- **Database Access**: Due to the SQL service provider’s security policy, the friend system requires access from an authorized public IP. If you encounter any issues connecting to the MySqlConnector host during testing, please provide your public IP so it can be whitelisted for the best experience (to be replaced by user chains in Wave 3)

### Media & Technical Visuals
- **XFighterZone Files:** [Google Drive](https://drive.google.com/drive/folders/1LuaF3wnbUNSHbUYezlq1Em-Vj9wC2cMF?usp=sharing)  
- **Full Playlists Buildathon Demo:** [https://youtu.be/tf6PkybCmtI?si=ZZ2fSCO7kMLJCqa5 ](https://youtu.be/tf6PkybCmtI?si=ZZ2fSCO7kMLJCqa5 )
  
## 📞 Support
**Team:** Roystudios / **Discord:** @roycrypto  
**Author:** [roycrypto](https://x.com/AriesLLC1)





















