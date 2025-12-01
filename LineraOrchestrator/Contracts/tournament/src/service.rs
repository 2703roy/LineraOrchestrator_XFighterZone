// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// tournament/src/service.rs

#![cfg_attr(target_arch = "wasm32", no_main)]

mod state;
use log::info;
use std::sync::Arc;
use async_graphql::{EmptySubscription, Object, Request, Response, Schema, SimpleObject};
use linera_sdk::{abi::WithServiceAbi, views::View, Service, ServiceRuntime};
use tournament_shared::{LeaderboardEntry, TournamentInfo, TournamentStatus};
use tournament::{TournamentAbi, Operation, TournamentMatchInput, Parameters};
use self::state::TournamentState;

linera_sdk::service!(TournamentService);

#[derive(SimpleObject, Clone)]
struct TournamentMatchOutput {
    pub match_id: String,
    pub player1: String,
    pub player2: String,
    pub winner: Option<String>,
    pub round: String,
    pub status: String,
}

#[derive(SimpleObject, Clone)]
struct MatchResultOutput {
    match_id: String,
    winner: String,
    loser: String,
}

#[derive(SimpleObject, Clone)]
struct BetEntryOutput {
    pub bet_id: String,
	pub match_id: String,
    pub bettor: String,
	pub bettor_public_key: String,
    pub predicted: String,
    pub amount: u64,
    pub user_chain: String,
}

#[derive(SimpleObject, Clone)]
struct TournamentBalance {
    pub balance: u64,
    pub ticker_symbol: String,
}

#[derive(SimpleObject, Clone)]
struct MatchMetadataOutput {
    pub match_id: String,
    pub betting_deadline_unix: u64,
    pub match_start_unix: Option<u64>,
    pub status: String,
}

#[derive(SimpleObject, Clone)]
struct BettingAnalytics {
    pub total_bets_placed: u64,
    pub total_bets_settled: u64,
    pub total_payouts: u64,
}

#[derive(SimpleObject, Clone)]
struct AirdropInfo {
    pub amount: u64,
}

pub struct TournamentService {
    state: Arc<TournamentState>,
    runtime: Arc<ServiceRuntime<Self>>,
}

impl WithServiceAbi for TournamentService {
    type Abi = TournamentAbi;
}

impl Service for TournamentService {
    type Parameters = Parameters;

    async fn new(runtime: ServiceRuntime<Self>) -> Self {
        let state = TournamentState::load(runtime.root_view_storage_context())
            .await
            .expect("Failed to load state");
        TournamentService {
            state: Arc::new(state),
            runtime: Arc::new(runtime),
        }
    }

    async fn handle_query(&self, request: Request) -> Response {
        let schema = Schema::build(
            QueryRoot { 
                state: self.state.clone(),
                //runtime: self.runtime.clone(),
            },
            MutationRoot { runtime: self.runtime.clone() },
            EmptySubscription,
        ).finish();
        schema.execute(request).await
    }
}

struct MutationRoot {
    runtime: Arc<ServiceRuntime<TournamentService>>,
}

#[Object]
impl MutationRoot {
	// === Tournament Season Management ===
     async fn start_tournament_season(&self, name: String) -> bool {
        let op = Operation::StartTournamentSeason { name };
        self.runtime.schedule_operation(&op);
        true
    }

    async fn end_tournament_season(&self) -> bool {
		let op = Operation::EndTournamentSeason;
		self.runtime.schedule_operation(&op);
		true
	}

    async fn record_match(&self, match_id: String, winner: String, loser: String) -> bool {
        let op = Operation::RecordMatch { match_id, winner, loser };
        self.runtime.schedule_operation(&op);
        true
    }
    
    async fn set_bracket(&self, matches: Vec<TournamentMatchInput>) -> bool {
        let op = Operation::SetBracket { matches };
        self.runtime.schedule_operation(&op);
        true
    }
    
    async fn set_participants(&self, participants: Vec<String>) -> bool {
        let op = Operation::SetParticipants { participants };
        self.runtime.schedule_operation(&op);
        true
    }
	
	// === Tournament Betting  ===
    async fn settle_match(&self, match_id: String, winner: String) -> bool {
        let op = Operation::SettleMatch { match_id, winner };
        self.runtime.schedule_operation(&op);
        true
    }
    
	/// Open bet, set match data
    async fn set_match_metadata(
        &self,
        match_id: String,
        betting_deadline_unix: Option<u64>,
        match_start_unix: Option<u64>,
        status: Option<String>,
    ) -> bool {
        let deadline = betting_deadline_unix.unwrap_or_else(|| {
            use std::time::{SystemTime, UNIX_EPOCH};
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_secs()
                + 60
        });
        
        let op = Operation::SetMatchMetadata {
            match_id,
            betting_deadline_unix: deadline,
            match_start_unix,
            status,
        };
        self.runtime.schedule_operation(&op);
        true
    }
	
	/*
	/// Manual airdrop to specific user
    async fn airdrop_tokens(&self, user_chain: String, user_public_key: String, amount: u64) -> bool {
        let op = Operation::AirdropTokens {
            user_chain: user_chain.parse().expect("Invalid chain ID"),
            user_public_key,
            amount,
        };
        self.runtime.schedule_operation(&op);
        true
    }
    
    /// Set default airdrop amount for new users
    async fn set_airdrop_amount(&self, amount: u64) -> bool {
        let op = Operation::SetAirdropAmount { amount };
        self.runtime.schedule_operation(&op);
        true
    }
	*/
	
}

struct QueryRoot {
    state: Arc<TournamentState>,
    //runtime: Arc<ServiceRuntime<TournamentService>>,
}

#[Object]
impl QueryRoot {
	// === Tournament Management Service === 
	/// Tournament check info current season
    async fn current_tournament_info(&self) -> Option<TournamentInfo> {
        let tournament_number = *self.state.current_tournament.get();
        self.get_tournament_info(tournament_number).await
    }
	
	/// Tournament check info via number
	 async fn tournament_info(&self, tournament_number: u64) -> Option<TournamentInfo> {
        self.get_tournament_info(tournament_number).await
    }
	
	/// DEBUG Lấy danh sách tất cả tournament numbers
    async fn tournament_numbers(&self) -> Vec<u64> {
        match self.state.tournament_metadata.indices().await {
            Ok(numbers) => numbers.into_iter().collect(),
            Err(_) => vec![],
        }
    }
	/// Tournament check past leaderboard data
	async fn past_tournament_leaderboard(&self, tournament_number: u64) -> Vec<LeaderboardEntry> {
        self.get_past_tournament_entries(tournament_number).await
    }
	
	/// Tournament check participants
    async fn participants(&self) -> Vec<String> {
        self.state.participants.get().clone()
    }
	
	/// Match result for leaderboard tournament
    async fn results(&self) -> Vec<MatchResultOutput> {
        let mut output = vec![];
        if let Ok(ids) = self.state.results.indices().await {
            for id in ids {
                if let Ok(Some((winner, loser))) = self.state.results.get(&id).await {
                    output.push(MatchResultOutput { match_id: id, winner, loser });
                }
            }
        }
        output
    }

	/// Get data Leaderboard tournament
    async fn tournament_leaderboard(&self) -> Vec<LeaderboardEntry> {
        let mut results = vec![];
        if let Ok(keys) = self.state.tournament_leaderboard.indices().await {
            for k in keys {
                if let Ok(Some(score)) = self.state.tournament_leaderboard.get(&k).await {
                    results.push(LeaderboardEntry { username: k, score });
                }
            }
        }
        results.sort_by(|a, b| b.score.cmp(&a.score));
        results
    }
	
	/// Set bracket cho tournament
    async fn bracket(&self) -> Vec<TournamentMatchOutput> {
        let mut matches = vec![];
        if let Ok(ids) = self.state.bracket.indices().await {
            for id in ids {
                if let Ok(Some(m)) = self.state.bracket.get(&id).await {
                    matches.push(TournamentMatchOutput {
                        match_id: m.match_id,
                        player1: m.player1,
                        player2: m.player2,
                        winner: m.winner,
                        round: m.round,
                        status: m.status,
                    });
                }
            }
        }
        matches
    }
	
	// === Tournament Betting Service === 
	/// Tournament check participants
	async fn get_bets(&self, match_id: String) -> Vec<BetEntryOutput> {
		let mut bets_for_match = Vec::new();
		match self.state.bets.get(&match_id).await {
			Ok(Some(entries)) => {
				info!("[SERVICE] Found {} bets for match {}", entries.len(), match_id);
				for bet in entries {
					bets_for_match.push(BetEntryOutput {
						bet_id: bet.bet_id,
						match_id: bet.match_id.clone(),
						bettor: bet.bettor,
						bettor_public_key: bet.bettor_public_key.clone(),
						predicted: bet.predicted,
						amount: bet.amount,
						user_chain: bet.user_chain.to_string(),
					});
				}
			}
			_ => {
				info!("[SERVICE] No bets found for match {}", match_id);
			}
		}

		bets_for_match
	}

	/// Check info match metadata
	async fn match_metadata(&self, match_id: String) -> Option<MatchMetadataOutput> {
        match self.state.matches.get(&match_id).await {
            Ok(Some(metadata)) => Some(MatchMetadataOutput {
                match_id: metadata.match_id,
                betting_deadline_unix: metadata.betting_deadline_unix,
                match_start_unix: metadata.match_start_unix,
                status: format!("{:?}", metadata.status),
            }),
            _ => None,
        }
    }
	
	/// Debug query để kiểm tra registered UserXFighter apps
	async fn registered_userxfighter_apps(&self) -> Vec<String> {
        let mut apps = Vec::new();
        if let Ok(chain_ids) = self.state.user_xfighter_app_ids.indices().await {
            for chain_id in chain_ids {
                if let Ok(Some(app_id)) = self.state.user_xfighter_app_ids.get(&chain_id).await {
                    apps.push(format!("Chain: {} -> App: {:?}", chain_id, app_id));
                }
            }
        }
        apps
    }
	/*
	 // Wave 4: User get airdrop from tournament
	 /// Tournament betting analytics
    async fn betting_analytics(&self) -> async_graphql::Result<BettingAnalytics> {
        Ok(BettingAnalytics {
            total_bets_placed: *self.state.total_bets_placed.get(),
            total_bets_settled: *self.state.total_bets_settled.get(),
            total_payouts: *self.state.total_payouts.get(),
        })
    }
	
		/// Get current airdrop settings
    async fn airdrop_info(&self) -> AirdropInfo {
        AirdropInfo {
            amount: *self.state.airdrop_amount.get(),
        }
    }
	*/
}

impl QueryRoot {
    async fn get_tournament_info(&self, tournament_number: u64) -> Option<TournamentInfo> {
		if let Some(metadata) = self.state.tournament_metadata.get(&tournament_number).await.ok().flatten() {
			
			 // TÍNH duration_days từ start_time và end_time
            let duration_days = if let Some(end_time) = metadata.end_time {
                let duration_micros = end_time - metadata.start_time;
                Some(duration_micros as f64 / (24.0 * 60.0 * 60.0 * 1_000_000.0))
            } else {
                None
            };
			
			Some(TournamentInfo {
				number: tournament_number,
				name: metadata.name,
				start_time: metadata.start_time,
				end_time: metadata.end_time,
				duration_days,
				status: match metadata.status {
					TournamentStatus::Active => "active".to_string(),
					TournamentStatus::Ended => "ended".to_string(),
				},
				champion: metadata.champion,
				runner_up: metadata.runner_up,
			})
		} else {
			// Fallback old seasons if do not have metadata
			Some(TournamentInfo {
				number: tournament_number,
				name: format!("Tournament {}", tournament_number),
				start_time: 0,
				end_time: None,
				duration_days: None,
				status: if tournament_number < *self.state.current_tournament.get() {
					"ended".to_string()
				} else {
					"active".to_string()
				},
				champion: None,
				runner_up: None,
			})
		}
	}
    
    async fn get_past_tournament_entries(&self, tournament_number: u64) -> Vec<LeaderboardEntry> {
        let mut entries = Vec::new();
        let prefix = format!("{}:", tournament_number);
        
        let all_keys = self.state.past_tournament_leaderboards.indices().await.unwrap_or_default();
        
        for key in &all_keys {
            if key.starts_with(&prefix) {
                if let Some(username) = key.strip_prefix(&prefix) {
                    if let Some(score) = self.state.past_tournament_leaderboards.get(&**key).await.ok().flatten() {
                        entries.push(LeaderboardEntry {
                            username: username.to_string(),
                            score,
                        });
                    }
                }
            }
        }
        
        entries.sort_by(|a, b| b.score.cmp(&a.score));
        entries
    }
	

	
}