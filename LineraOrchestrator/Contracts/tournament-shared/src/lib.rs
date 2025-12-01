// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// tournament-shared/src/lib.rs

use async_graphql::{InputObject, Request, Response, SimpleObject};
use linera_sdk::linera_base_types::{ContractAbi, ServiceAbi, ChainId, ApplicationId};
use serde::{Deserialize, Serialize};

// === Tournament Management Setting ===
#[derive(SimpleObject, Clone, Debug, Serialize, Deserialize)]
pub struct TournamentInfo {
    pub number: u64,
    pub name: String,
    pub start_time: u64,
    pub end_time: Option<u64>,
    pub duration_days: Option<f64>,
    pub status: String,
    pub champion: Option<String>,
    pub runner_up: Option<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize, SimpleObject)]
pub struct LeaderboardEntry {
    pub username : String,
    pub score: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct TournamentMetadata {
    pub name: String,
    pub start_time: u64,
    pub end_time: Option<u64>,
    pub status: TournamentStatus,
    pub champion: Option<String>,
    pub runner_up: Option<String>,
}

#[derive(Clone, Debug, Serialize, PartialEq, Deserialize)]
pub enum TournamentStatus {
    Active,
    Ended,
}

// === Tournament Betting Setting ===
#[derive(InputObject, Clone, Debug, Deserialize, Serialize)]
pub struct TournamentMatchInput {
    pub match_id: String,
    pub player1: String,
    pub player2: String,
    pub winner: Option<String>,
    pub round: String,
    pub status: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub enum TournamentOperation {
    // Tournament Season Management
    StartTournamentSeason { name: String },
    EndTournamentSeason,
    
    SetParticipants { participants: Vec<String> },
    SetBracket { matches: Vec<TournamentMatchInput> },
    RecordMatch { match_id: String, winner: String, loser: String },
    SettleMatch { match_id: String, winner: String },
    
    // Betting Methods
    SetMatchMetadata {
        match_id: String,
        betting_deadline_unix: u64,
        match_start_unix: Option<u64>,
        status: Option<String>,
    },
    PlaceBet {
        bet_id: String,
        match_id: String,
        player: String,
        amount: u64,
        bettor: String,
        bettor_public_key: String, // AccountOwner
        user_chain: ChainId,
    },
    
    Payout {
        bet_id: String,
        match_id: String, 
        amount: u64,
        user_public_key: String,
        user_chain: ChainId,
		is_win: bool,
    },
	
	RegisterUserXFighter {
        user_xfighter_app_id: ApplicationId<()>,
    },
 
	/*
	 // Airdrop system
    AirdropTokens {
        user_chain: ChainId,
        user_public_key: String,
        amount: u64,
    },
	
	SetAirdropAmount {
        amount: u64,
    },
	
	// Market Creation & Trading
	TournamentOperation::CreatePredictionMarket {
		match_id: String,
		market_type: PredictionMarketType,
		liquidity: u64, // Initial liquidity
	}

	TournamentOperation::BuyShares {
		market_id: String,
		outcome: String, 
		amount: u64,
		max_price: Option<u64>, // Limit order
	}

	TournamentOperation::SellShares {
		market_id: String,
		outcome: String,
		amount: u64,
		min_price: Option<u64>,
	}
	*/
}

/*// Prediction Markets for Match Outcomes (Next Wave)
#[derive(Clone, Debug, Serialize, Deserialize)]
pub enum PredictionMarketType {
	// Basic bet
    MatchWinner { player1: String, player2: String },
	
	// Advanced bet
    TotalRounds { over_under: u8, threshold: u8 },
    ExactScore { score: String }, // "2-1", "2-0", "3-0", "3-2" Final
    FirstBlood { player: String },
    MatchDuration { range: DurationRange },
    
    // Special
    TournamentWinner { player: String },
    UpsetAlert { underdog: String }, // Underdog winner
}
*/

pub struct TournamentAbi;

impl ContractAbi for TournamentAbi {
    type Operation = TournamentOperation;
    type Response = ();
}

impl ServiceAbi for TournamentAbi {
    type Query = Request;
    type QueryResponse = Response;
}