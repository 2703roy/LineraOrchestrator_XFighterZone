// xfighter lib.rs
// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0

/*! ABI of the Xfighter Example Application */

use async_graphql::{InputObject, Request, Response};
use leaderboard::LeaderboardAbi;
use linera_sdk::linera_base_types::{ApplicationId, ContractAbi, ServiceAbi, ModuleId};
use serde::{Deserialize, Serialize};

/// Input cho kết quả trận đấu (client gửi vào GraphQL).
#[derive(InputObject, Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MatchResultInput {
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

/// Operation của Xfighter (contract/service cùng dùng).
#[derive(Clone, Debug, Deserialize, Serialize)]
pub enum Operation {
    RecordScore(MatchResultInput),
    Factory(FactoryOperation),
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub enum FactoryOperation {
    OpenAndCreate,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct Parameters {
    pub xfighter_module: ModuleId,
	pub leaderboard_id: ApplicationId<LeaderboardAbi>,
}

pub struct XfighterAbi;

impl ContractAbi for XfighterAbi {
    type Operation = Operation;
    type Response = ();
}

impl ServiceAbi for XfighterAbi {
    type Query = Request;
    type QueryResponse = Response;
}
