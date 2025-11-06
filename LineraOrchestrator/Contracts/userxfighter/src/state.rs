// userxfighter/src/state.rs
// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0

use linera_sdk::views::{linera_views, MapView, RegisterView, RootView, ViewStorageContext};
use serde::{Deserialize, Serialize};
use linera_sdk::linera_base_types::ChainId;

#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct Transaction {
    pub tx_id: String,
    pub tx_type: String, // "deposit", "withdraw", "bet", "win", "refund"
    pub amount: u64,
    pub timestamp: u64,
    pub related_id: Option<String>, // tournament_id or bet_id
    pub status: String, // "pending", "completed", "failed"
}

#[derive(RootView)]
#[view(context = ViewStorageContext)]
pub struct UserXfighterState {
    pub balance: RegisterView<u64>,
    pub transactions: MapView<String, Transaction>,
    pub tournament_app_id: RegisterView<Option<String>>,
    pub processed_bets: MapView<String, bool>, // bet_id -> processed
	pub user_chains: MapView<String, ChainId>, // THÊM: user_id -> chain_id  tournament_id -> tournament_chain_id
    pub tournament_chain_id: RegisterView<Option<ChainId>>, // THÊM: chain ID của tournament
}