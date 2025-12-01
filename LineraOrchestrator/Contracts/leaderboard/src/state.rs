// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// leaderboard/src/state.rs

use linera_sdk::views::{linera_views, MapView, RegisterView, RootView, ViewStorageContext};
use leaderboard::SeasonMetadata;

/// Định nghĩa trạng thái của hợp đồng Leaderboard.
#[derive(RootView)]
#[view(context = ViewStorageContext)]
pub struct LeaderboardState {
    // Global leaderboard (never reset)
    pub global_scores: MapView<String, u64>,
    pub global_total_matches: MapView<String, u64>,
    pub global_total_wins: MapView<String, u64>,
    pub global_total_losses: MapView<String, u64>,
    
    // Season leaderboard
    pub season_scores: MapView<String, u64>,
    pub season_total_matches: MapView<String, u64>,
    pub season_total_wins: MapView<String, u64>,
    pub season_total_losses: MapView<String, u64>,
    
    // Season management
	pub current_season: RegisterView<u64>,
	pub processed_match_ids: MapView<String, bool>,
	pub last_play_timestamps: MapView<String, u64>,
	
	// Lưu trữ dữ liệu các season cũ
    pub past_season_scores: MapView<String, u64>,        // Key: "season_{number}:{user_name}"
    pub past_season_matches: MapView<String, u64>,       // Key: "season_{number}:{user_name}"
    pub past_season_wins: MapView<String, u64>,          // Key: "season_{number}:{user_name}"
    pub past_season_losses: MapView<String, u64>,        // Key: "season_{number}:{user_name}"
	
	// Season metadata
    pub season_metadata: MapView<u64, SeasonMetadata>,
}