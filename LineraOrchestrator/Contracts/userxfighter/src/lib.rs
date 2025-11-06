// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// userxfighter/src/lib.rs

#![cfg_attr(target_arch = "wasm32", no_main)]

use async_graphql::{Request, Response};
use linera_sdk::linera_base_types::{ContractAbi, ServiceAbi, ChainId, ModuleId};
use serde::{Deserialize, Serialize};


#[derive(Clone, Debug, Deserialize, Serialize)]
pub enum Operation {
    Deposit { amount: u64 },
    Withdraw { amount: u64 },
    Transfer { to: String, amount: u64 },
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct Parameters {
    pub tournament_id: String,
    pub user_xfighter_module: ModuleId,
	pub tournament_chain_id: ChainId,
}

/// Message mà UserXfighter nhận từ Tournament (phải giống hệt với BettingMessage trong tournament)
#[derive(Clone, Debug, Deserialize, Serialize)]
pub enum BettingMessage {
    DebitForBet {
        amount: u64,
        tournament_id: String,
        bet_id: String,
        match_id: String,
    },
    CreditForWin {
        amount: u64,
        tournament_id: String,
        bet_id: String,
        match_id: String,
    },
    RefundBet {
        amount: u64,
        tournament_id: String,
        bet_id: String,
        match_id: String,
    },
}

// UserXfighter nhận BettingMessage từ tournament
pub type Message = BettingMessage;

pub struct UserXfighterAbi;

impl ContractAbi for UserXfighterAbi {
    type Operation = Operation;
    type Response = ();
}

impl ServiceAbi for UserXfighterAbi {
    type Query = Request;
    type QueryResponse = Response;
}