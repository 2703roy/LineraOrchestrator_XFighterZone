// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// leaderboard/lib.rs

#![cfg_attr(target_arch = "wasm32", no_main)]

use serde::{Deserialize, Serialize};
use async_graphql::{Request, Response, SimpleObject};
use linera_sdk::linera_base_types::{ContractAbi, ServiceAbi};

/// Định nghĩa dữ liệu cho một mục trong bảng xếp hạng.
#[derive(SimpleObject, Clone, Debug, Serialize, Deserialize)]
pub struct LeaderboardEntry {
    pub user_name: String,
    pub total_matches: u64,
    pub total_wins: u64,
    pub total_losses: u64,
    pub score: u64,
}

#[derive(SimpleObject, Clone, Debug, Serialize, Deserialize)]
pub struct UserGlobalStats {
    pub user_name: String,
    pub total_matches: u64,
    pub total_wins: u64, 
    pub total_losses: u64,
    pub score: u64,
    pub last_play: Option<u64>, // Timestamp của match cuối cùng
}

#[derive(SimpleObject, Clone, Debug, Serialize, Deserialize)]
pub struct SeasonInfo {
    pub number: u64,
    pub name: String,
    pub start_time: u64,
    pub end_time: Option<u64>,
	pub duration_days: Option<f64>,
    pub status: String,
    pub total_players: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SeasonMetadata {
    pub name: String,
    pub start_time: u64,  // micros
    pub end_time: Option<u64>,  // None = đang active
    pub status: SeasonStatus,
	pub duration_days: Option<f64>,
}

#[derive(Clone, Debug, Serialize, PartialEq, Deserialize)]
pub enum SeasonStatus {
    Active,
    Ended,
}

/// Operation của leaderboard: dùng enum để chứa nhiều loại thao tác.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub enum Operation {
    RecordScore { 
        user_name: String, 
        is_winner: bool, 
        match_id: String,
    },
	StartSeason { 
        name: String, 
    },
    EndSeason,
}

/// Message (cross-chain) triển khai dưới dạng struct để nhất quán.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct RecordScoreMessage {
    pub user_name: String,
    pub is_winner: bool,
    pub match_id: String,
}

pub struct LeaderboardAbi;

impl ContractAbi for LeaderboardAbi {
    type Operation = Operation;
    type Response = ();
}

impl ServiceAbi for LeaderboardAbi {
    type Query = Request;
    type QueryResponse = Response;
}