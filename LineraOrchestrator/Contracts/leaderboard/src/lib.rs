// leaderboard/lib.rs
// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0

#![cfg_attr(target_arch = "wasm32", no_main)]

use serde::{Deserialize, Serialize};
use async_graphql::{Request, Response, SimpleObject};
use linera_sdk::linera_base_types::{ContractAbi, ServiceAbi};

/// Định nghĩa dữ liệu cho một mục trong bảng xếp hạng.
#[derive(SimpleObject, Clone, Debug, Serialize, Deserialize)]
pub struct LeaderboardEntry {
    pub user_id: String,
    pub total_matches: u64,
    pub total_wins: u64,
    pub total_losses: u64,
    pub score: u64,
}

/// Operation của leaderboard: dùng enum để chứa nhiều loại thao tác.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub enum Operation {
    RecordScore { user_id: String, is_winner: bool, match_id: String },
}

/// Message (cross-chain) triển khai dưới dạng struct để nhất quán.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct RecordScoreMessage {
    pub user_id: String,
    pub is_winner: bool,
    pub match_id: String,
}

pub struct LeaderboardAbi;

impl ContractAbi for LeaderboardAbi {
    type Operation = Operation;
    // Đã thay đổi về () để tránh lỗi serialization.
    type Response = ();
}

impl ServiceAbi for LeaderboardAbi {
    type Query = Request;
    type QueryResponse = Response;
}
