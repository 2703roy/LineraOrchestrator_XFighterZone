// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// tournament/src/state.rs

use linera_sdk::views::{linera_views, MapView, RegisterView, RootView, ViewStorageContext};
use linera_sdk::linera_base_types::ChainId;
use serde::{Deserialize, Serialize};
use tournament_shared::TournamentMetadata;

//=== Tournament Betting ===
/// Entry thông tin một cược
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct BetEntry {
    pub bet_id: String,
    pub match_id: String,
    pub bettor: String,
    pub bettor_public_key: String, //AccountOwner
    pub predicted: String,
    pub amount: u64,
    pub user_chain: ChainId,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub enum MatchStatus {
    Open,
    Closed,
    Settled,
}

/// Metadata cho một trận đấu (match)
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct MatchMetadata {
    pub match_id: String,
    pub betting_deadline_unix: u64,
    pub match_start_unix: Option<u64>,
    pub status: MatchStatus,
}

// Struct cho tournament match onchain
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct TournamentMatch {
    pub match_id: String,
    pub player1: String,
    pub player2: String,
    pub winner: Option<String>,
    pub round: String,
    pub status: String,
}

/// Định nghĩa trạng thái của hợp đồng Tournament
#[derive(RootView)]
#[view(context = ViewStorageContext)]
pub struct TournamentState {
	// === Tournament Management State ===
    // Tournament current season
    pub tournament_name: RegisterView<String>,
    pub start_time: RegisterView<u64>,
    pub end_time: RegisterView<u64>,
    pub participants: RegisterView<Vec<String>>,
    pub results: MapView<String, (String, String)>,
    pub status: RegisterView<String>,
    pub current_round: RegisterView<String>,
    pub bracket: MapView<String, TournamentMatch>,
    
    // Quản lý tournament theo season (giống leaderboard)
    pub current_tournament: RegisterView<u64>,
	pub current_champion: RegisterView<String>,
    pub current_runner_up: RegisterView<String>,
    pub tournament_metadata: MapView<u64, TournamentMetadata>,
	pub tournament_leaderboard: MapView<String, u64>,
    
    // Lưu trữ dữ liệu tournament cũ
    pub past_tournament_leaderboards: MapView<String, u64>, // Key: "tournament_{number}:{username}"

    // === Tournament Betting State ===
    pub bets: MapView<String, Vec<BetEntry>>, 
    pub matches: MapView<String, MatchMetadata>,
    pub bet_counter: RegisterView<u64>,
	pub user_xfighter_app_ids: MapView<ChainId, linera_sdk::linera_base_types::ApplicationId<userxfighter::UserXfighterAbi>>,
	
	// Betting analytics
	pub total_bets_placed: RegisterView<u64>, // Total bets placed
	pub total_bets_settled: RegisterView<u64>, // Total bets settled
	pub total_payouts: RegisterView<u64>, // Total payouts
	
	// Airdrop system
    //pub airdrop_amount: RegisterView<u64>, // Số token airdrop mặc định
    //pub airdrop_recipients: SetView<String>, // Danh sách user đã nhận airdrop (format: "chain_id:public_key")
}