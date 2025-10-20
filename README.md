XFighterZone
XFighterZone is an on-chain gaming platform built on the Linera blockchain framework, seamlessly integrating with a Unity-based game client. The project leverages a decentralized, multi-chain architecture to ensure game state, player stats, and leaderboards are transparent, verifiable, and trustless.

Introduction
This project demonstrates a robust, decentralized application (dApp) built on Linera, where each match is a separate microchain. The system orchestrates interaction between a Unity game client, a Node.js orchestrator, and multiple Linera services and contracts to manage the full lifecycle of a game match, from matchmaking to final leaderboard updates.

Core Technologies
Linera: A multi-chain blockchain framework that provides the infrastructure for decentralized, high-performance applications.

Unity: The game client, responsible for the user interface, real-time gameplay, and user experience.

GraphQL: The primary API for client-server communication, used to query and mutate on-chain data.

Node.js: Acts as the orchestrator backend, automating complex Linera CLI commands.

Architecture and Workflow
The system is designed with a clear separation of concerns, ensuring scalability and decentralization.

Unity Server (Coordinator): Handles matchmaking and coordinates the creation of new Linera microchains for each match by communicating with the Orchestrator. It also manages real-time, low-latency gameplay logic.

Orchestrator Backend (Node.js): Automates the Linera-specific tasks, such as creating new microchains for each match and instantiating the xfighter_contract on them. It is the crucial bridge between the Unity server and the Linera blockchain.

Linera Contracts:

xfighter_contract.wasm: A smart contract deployed on each match's dedicated microchain. It records the final match results and sends a cross-chain message to the Leaderboard contract.

leaderboard_contract.wasm: A central smart contract on a fixed, dedicated microchain. It receives and processes messages from xfighter_contract instances to update the global player leaderboard.

Game Flow: From Match to Leaderboard
Matchmaking: The Unity Server finds two players and coordinates the match setup.

Chain Creation: The Unity Server calls the Orchestrator, which programmatically executes Linera CLI commands to create a new microchain (MATCH_CHAIN_ID) and instantiate a new xfighter app on it (MATCH_APP_ID).

In-Game: The Unity clients connect to the game via the Unity Server for real-time gameplay.

Recording Results: At the end of a match, the Unity Server sends the final match data to the Orchestrator, which in turn submits a GraphQL mutation to the xfighter app on the match chain.

Cross-Chain Messaging: The xfighter contract, after processing the mutation, sends a cross-chain message to the leaderboard contract, updating player statistics in a trustless manner.

Displaying Leaderboard: Unity clients query the leaderboard_service directly to fetch and display up-to-date rankings.

Getting Started
To run and contribute to this project, you need to set up the following:

Linera Environment: Follow the official Linera documentation to set up your local development environment.

Rust and Cargo: Ensure you have the necessary tools to build the Linera contracts.

Node.js: For the Orchestrator backend.

Unity: For the game client.

Contributing
We welcome contributions! Please feel free to open issues or submit pull requests.

License
This project is licensed under the Apache License 2.0.