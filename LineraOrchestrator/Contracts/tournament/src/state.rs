// tournament/src/state.rs
// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0

use linera_sdk::views::{linera_views, MapView, RootView, RegisterView, ViewStorageContext};

/// Định nghĩa trạng thái của hợp đồng Tournament
#[derive(RootView)]
#[view(context = ViewStorageContext)]
pub struct TournamentState {
    pub tournament_name: RegisterView<String>,
    pub start_time: RegisterView<u64>,
    pub end_time: RegisterView<u64>,
    pub participants: MapView<String, bool>, // username => registered
    pub results: MapView<String, (String, String)>, // match_id => (winner, loser)
    pub status: RegisterView<String>,
    pub current_round: RegisterView<String>,
    pub champion: RegisterView<String>, 
    pub runner_up: RegisterView<String>, // top 2
    pub tournament_leaderboard: MapView<String, u64>,
	pub opid: RegisterView<String>,
}
