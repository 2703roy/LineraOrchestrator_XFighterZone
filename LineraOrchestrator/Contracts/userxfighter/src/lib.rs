// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// userxfighter/src/lib.rs

use linera_sdk::abis::fungible;
use async_graphql::{Request, Response};
use linera_sdk::linera_base_types::{ChainId, ContractAbi, ServiceAbi};
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Deserialize, Serialize)]
pub enum Operation {
    PlaceBet {
        match_id: String,
        player: String,
        amount: u64,
    },	
	
	RecordPayout {
        bet_id: String,
        match_id: String,
        amount: u64,
        user_public_key: String,
		is_win: bool,
    },
	
	 SendFriendRequest {
        to_user_chain: ChainId,
    },
    AcceptFriendRequest {
        request_id: String,
    },
    RejectFriendRequest {
        request_id: String,
    },
    RemoveFriend {
        friend_chain_id: ChainId,
    },
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct Parameters {
    pub local_tournament_app_id: linera_sdk::linera_base_types::ApplicationId<tournament_shared::TournamentAbi>,
	pub fungible_app_id: linera_sdk::linera_base_types::ApplicationId<fungible::FungibleTokenAbi>, 
	pub tournament_owner: String,
	pub publisher_chain_id: ChainId,
	pub friend_app_id: linera_sdk::linera_base_types::ApplicationId<friendxfighter::FriendAbi>,
}

pub struct UserXfighterAbi;

impl ContractAbi for UserXfighterAbi {
    type Operation = Operation;
    type Response = ();
}

impl ServiceAbi for UserXfighterAbi {
    type Query = Request;
    type QueryResponse = Response;
}