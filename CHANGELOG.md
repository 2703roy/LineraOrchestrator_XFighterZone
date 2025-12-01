# Changelog
All notable changes to this project will be documented in this file.
## Development Roadmap
| Wave | Focus | Status |
|------|--------|--------|
| **Wave 1** | MVP Foundation: Core Gameplay, On-chain Integration | ✅ Complete |
| **Wave 2** | Multiplatform Support, Friend System, Hero System, Normal/Rank Modes | ✅ Complete |
| **Wave 3** | Tournament Expansion, User Chains, Social on-chain features & Cross-chain Betting | ✅ Complete |
| **Wave 4** | Metaverse Lobby, Prediction Bet System & Cross-chain Asset Management  | 🔄 In Progress |
| **Wave 5** | Marketplace, Quest System & Advanced Prediction Pools | 🔄 In Progress |
| **Wave 6** | Full Metaverse: Decentralization & Social Features | ⏳ Planned |

## Wave 1 — Completed
- Initial Buildathon submission: Orchestrator, Contracts (xfighter, leaderboard, tournament).
- Included precompiled .wasm artifacts and Quick Demo resources.

## Wave 2 — Completed:
**Core Architecture**
- Xfighter-Leaderboard Integration - Cross-app real-time communication
- Real-time Ranking System - Dynamic score calculation & cross-chain queries
- Enhanced Multi-Queue Architecture - 150 high-priority + 500 low-priority slots → Optimized large request throughput
- Enhanced Monitoring & Recovery - Real-time queue metrics, detailed logging, and full state restoration

**Advanced Tournament System**
- Leaderboard Snapshot & Deterministic Bracket Generation
- Progressive Rounds: Quarterfinals → Semifinals → Finals & Dedicated `recordTournamentScore` operations

**Gameplay & Social Features**
- Multiplatform Support (Windows & macOS)
- Friend System & New Hero Keylsey
- Normal & Ranked competitive modes

## Wave 3 — Major Upgrades:
- Environment 1: Live Demo Production
- Environment 2: Local Development & Testing
- Multi-chain architecture with user chains
- Complete cross-chain betting system
- Tournament management with multi-season support
- Social features with friend system
- Decentralized economy with Fungible Token integration
- Real-time gameplay with good performance for WebGL

### Details Wave 3 new modules:
**1. Update Xfighter Application: Real-time combat engine + cross-chain result propagation**
- Matchmaking: Creates and manages real-time battles between players.
- Result Recording: Captures match outcomes (winner/loser, duration, map details).
- Cross-chain Coordination: Sends match results to the Leaderboard apps via cross-chain messages.
- Factory Operations: Automatically deploys new user chains and applications using OpenAndCreate.

**2. Update Leaderboard Application: Tracks player performance and global rankings across seasons.**
- Global & Season Stats: Maintains separate leaderboards for all-time and seasonal performance.
- Score Updates: Processes RecordScore operations from Xfighter to update wins, losses, and matches.
- Season Management: Allows admins to start and end seasons with metadata (name, duration, status).
- Cross-chain Queries: Supports real-time GraphQL ranking queries for userchain client.
- Data Points: Wins, losses, total matches, scores, and last play timestamps per player.

**3. Update Tournament Application: Manages tournament seasons, brackets, and cross-chain betting.**
- Season Lifecycle: Start / End tournament seasons with metadata storage.
- Bracket Management: Set participant list, set bracket tree, match assignments.
- Betting Engine: Processes PlaceBet cross-chain messages from user chains and stores bets with user chain info.
- Payout System: Automatically settles matches, calculates winnings, and triggers call payouts via Fungible transfer.
- Multi-chain: Uses Linera's crosschain messaging for bet placement and payout notifications

**4. New UserXFighter Application: User-centric application for asset management and social interactions.**
- Bet Placement: Initiates bets via PlaceBet operation, which triggers token transfers and cross-chain messages to Tournament.
- Transaction History: Maintains a ledger of all bets, payouts, and friend interactions.
- Friend Integration: Forwards friend requests to FriendXFighter app for social features.
- Local Tournament Registration: Automatically registers with the local Tournament app on user chain instantiation.
- User Experience: Serves as the primary interface for users to participate in tournaments, manage funds, and connect with friends.

**5. New Fungible Token Application: Manages the platform's native token for betting and rewards.**
- Cross-chain Transfers: Supports Transfer operations between user chains and publisher chain for bet payments and payouts.
- Balance Management: Tracks token balances and allowances for each account.
- Transaction Logging: Records all transfers for transparency and auditing.
- Integration: Seamlessly interfaces with Tournament for payouts and UserXFighter for bet payments, enabling a fluid economic ecosystem.

**6. FriendXFighter Application: Social layer for managing friendships and interactions across chains.**
- Friend Requests: Handles SendFriendRequest, AcceptFriendRequest, and RejectFriendRequest operations.
- Cross-chain Messaging: Uses Linera messages to propagate friend requests and updates between user chains.
- Friend List Management: Maintains a persistent list of friends and pending requests.

**7. Update Environment for full testing and production live demo.**
- Cloudflare Tunnel (no open ports required) for public access
- Automatic SSL/TLS encryption
- Global CDN & DDoS protection
- 24/7 availability
- Admin at tournament (localhost:5174)
- Docker containerization



