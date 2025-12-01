// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// userxfighter/src/state.rs

use linera_sdk::views::{linera_views, MapView, RootView, ViewStorageContext};
use serde::{Deserialize, Serialize};

#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct Transaction {
    pub tx_id: String,
    pub tx_type: String,
    pub amount: u64,
    pub timestamp: u64,
    pub related_id: Option<String>,
    pub status: String,
}

#[derive(RootView)]
#[view(context = ViewStorageContext)]
pub struct UserXfighterState {
    pub transactions: MapView<String, Transaction>,
}