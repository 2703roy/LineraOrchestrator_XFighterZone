// tournament/lib.rs
// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0

#![cfg_attr(target_arch = "wasm32", no_main)]

use serde::{Deserialize, Serialize};
use async_graphql::{Request, Response};
use linera_sdk::linera_base_types::{ContractAbi, ServiceAbi};

/// Operation của Tournament dùng enum để chứa nhiều loại thao tác.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub enum Operation {
    CreateTournament { name: String, start_time: u64, end_time: u64 },
    Register { player: String },
    RecordMatch { match_id: String, winner: String, loser: String },
    CloseTournament,
}

/// Message (cross-chain) triển khai dưới dạng struct để nhất quán.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct RecordScoreMessage {
    pub user_id: String,
    pub is_winner: bool,
    pub match_id: String,
}

pub struct TournamentAbi;

impl ContractAbi for TournamentAbi {
    type Operation = Operation;
    type Response = ();
}

impl ServiceAbi for TournamentAbi {
    type Query = Request;
    type QueryResponse = Response;
}
