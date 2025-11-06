# ⚔️ XFighterZone — Real-Time Gaming & Prediction Metaverse on Linera

## 🎬 Live Demo
[![Watch the demo](https://img.youtube.com/vi/YOUR_VIDEO_ID/0.jpg)](https://www.youtube.com/watch?v=YOUR_VIDEO_ID)
- Frontend: [Unity Game Client (Windows, MacOS, Linux)](https://drive.google.com/drive/folders/1c2bNHDPvi4NdZPiV9lNEmqXDyuo8FHiS?usp=sharing)
- Backend: Linera Orchestrator - http://localhost:5290, Game Server: UDP `your-ip`:1111

Production Status: Full test on Conway Testnet with 8 demo accounts

## ⚡ Quick Start 
```text
# Clone repository
git clone https://github.com/2703roy/LineraOrchestrator_XFighterZone.git
cd LineraOrchestrator_XFighterZone

# Run complete system (Docker Server + LineraOrchestrator)
chmod +x start-docker.sh
./start-docker.sh

# After 15-20 minutes, system will be ready.
```
Test Accounts: Use test1 to test8 (same username/password) for multiplayer battles

## 🗓️ Development Roadmap

| Wave | Focus | Status |
|------|--------|--------|
| **Wave 1** | MVP Foundation Gameplay, Onchain Integration | ✅ Complete |
| **Wave 2** | Multiplatform easy for tester, Friend List, Hero System, Normal/Rank Mode | ✅ Complete |
| **Wave 3** | Tournament Bracket Expansion, Users chain & Cross-chain Betting | 🔄 In Progress |
| **Wave 4** | Shaping the Metaverse, Betting System & Cross-chain Assets  | 🔄 Planned |
| **Wave 5** | Marketplace, Quest System & Advanced Prediction Pools | ⏳ Planned |
| **Wave 6** | Metaverse Foundation, Optimization, Full Decentralization & Social Features | ⏳ Planned |

## 📤 Buildathon Submission Checklist 
- [x] Public repo with contracts
- [x] Demo videos & builds
- [x] Conway Testnet deployed
- [x] Docker setup Quick start guide
- [ ] Tournament + UserChain
- [ ] Betting System UI
- [ ] Marketplace, Quest, Metaverse System

## 🛠️ Tech Stack
| Layer | Technology |
|-------|-------------|
| **Blockchain** | Linera Protocol (Conway Testnet) |
| **Smart Contracts** | Rust 1.86.0, Linera SDK v0.15.3 |
| **Orchestrator** | C#, ASP.NET Core, GraphQL Client |
| **Game Server** | Custom UDP Server, Matchmaking & Real-time Networking |
| **Infrastructure** | Docker, Multi-wallet Management |

## 🏗️ System Architecture
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
Unity Client → Game Server → Orchestrator API (C#) → Linera Microchains (Rust WASM)

1. Player Login → User chain authentication
2. Matchmaking → Tournament chain coordination  
3. Real-time Battle → Unity gameplay with live physics
4. Result Verification → On-chain score recording
5. Automatic Payouts → Cross-chain betting settlements
6. Leaderboard Update → Global ranking aggregation
```
---

### 🎥 Media & Technical Visuals
- **XFighterZone Files:** [Google Drive](https://drive.google.com/drive/folders/1LuaF3wnbUNSHbUYezlq1Em-Vj9wC2cMF?usp=sharing)  
- **Full Playlists Buildathon Demo:** [https://youtu.be/tf6PkybCmtI?si=ZZ2fSCO7kMLJCqa5 ](https://youtu.be/tf6PkybCmtI?si=ZZ2fSCO7kMLJCqa5 )
  
## 📞 Support
**Team:** Roystudios / **Discord:** @roycrypto  
**Author:** [roycrypto](https://x.com/AriesLLC1)


































