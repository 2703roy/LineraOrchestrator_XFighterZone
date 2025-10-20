// xfighter state.rs
// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0

use linera_sdk::{
    linera_base_types::{ApplicationId, ChainId},
    views::{MapView, RegisterView, RootView, ViewStorageContext, SetView},
};
use serde::{Deserialize, Serialize};
use async_graphql::SimpleObject;
use xfighter::XfighterAbi;
use leaderboard::LeaderboardAbi;

/// Đại diện cho kết quả của một trận đấu.
#[derive(SimpleObject, Clone, Debug, Deserialize, Serialize)]
#[graphql(name = "MatchResult")]
pub struct MatchResult {
    pub match_id: String,
    pub player1_username: String,
    pub player2_username: String,
    pub winner_username: String,
    pub loser_username: String,
    pub duration_seconds: u64,
    pub timestamp: u64,
    pub player1_score: u64,
    pub player2_score: u64,
    pub map_name: String,
    pub match_type: String,
	pub afk: Option<String>,
}

/// State của Xfighter
#[derive(RootView)]
#[view(context = ViewStorageContext)]
pub struct XfighterState {
    pub match_results: MapView<String, MatchResult>,
    pub leaderboard_id: RegisterView<Option<ApplicationId<LeaderboardAbi>>>,
    pub opened_chains: SetView<ChainId>,
    pub child_apps: MapView<ChainId, ApplicationId<XfighterAbi>>,
	pub sent_messages: MapView<String, bool>, //flag check duplication sent_messages
}