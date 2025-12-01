// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// xfighter state.rs

use linera_sdk::{
    linera_base_types::{ApplicationId, ChainId},
    views::{MapView, RegisterView, RootView, ViewStorageContext, SetView},
};
use xfighter::XfighterAbi;
use leaderboard::LeaderboardAbi;
use xfighter::MatchResult;

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