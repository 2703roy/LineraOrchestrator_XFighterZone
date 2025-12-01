// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// tournament/src/lib.rs
use serde::{Deserialize, Serialize};
use linera_sdk::abis::fungible;
use linera_sdk::linera_base_types::ChainId;

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct Parameters {
	pub fungible_app_id: linera_sdk::linera_base_types::ApplicationId<fungible::FungibleTokenAbi>,
	pub tournament_owner: String,
	pub publisher_chain_id: ChainId,
	pub airdrop_amount: Option<u64>,
}

pub use tournament_shared::{
    TournamentMatchInput, 
    TournamentOperation, 
    TournamentAbi, 
    LeaderboardEntry, 
    TournamentInfo,
    TournamentStatus
};

pub mod state;

pub type Operation = TournamentOperation;
pub type Message = TournamentOperation;