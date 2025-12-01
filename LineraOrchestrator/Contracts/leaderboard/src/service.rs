// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// leaderboard/src/service.rs

#![cfg_attr(target_arch = "wasm32", no_main)]

mod state;

use log::info;
use std::sync::Arc;
use async_graphql::{EmptySubscription, Object, Request, Response, Schema};
use linera_sdk::{
    abi::WithServiceAbi,
    views::View,
    Service, ServiceRuntime,
};
use leaderboard::{LeaderboardAbi, LeaderboardEntry, Operation, UserGlobalStats, SeasonInfo, SeasonStatus  };
use self::state::LeaderboardState;

linera_sdk::service!(LeaderboardService);

pub struct LeaderboardService {
    state: Arc<LeaderboardState>,
    runtime: Arc<ServiceRuntime<Self>>,
}

impl WithServiceAbi for LeaderboardService {
    type Abi = LeaderboardAbi;
}

impl Service for LeaderboardService {
    type Parameters = ();

    async fn new(runtime: ServiceRuntime<Self>) -> Self {
        let state = LeaderboardState::load(runtime.root_view_storage_context())
            .await
            .expect("Failed to load state");
        LeaderboardService {
            state: Arc::new(state),
            runtime: Arc::new(runtime),
        }
    }

    async fn handle_query(&self, request: Request) -> Response {
        let schema = Schema::build(
            QueryRoot {
                state: self.state.clone(),
            },
            MutationRoot {
                runtime: self.runtime.clone(),
            },
            EmptySubscription,
        )
        .finish();
        schema.execute(request).await
    }
}

struct MutationRoot {
    runtime: Arc<ServiceRuntime<LeaderboardService>>,
}

#[Object]
impl MutationRoot {
    async fn record_score(&self, user_name: String, is_winner: bool, match_id: String) -> bool {
        info!("[LEADERBOARD] Recording score mutation");
        let op = Operation::RecordScore { user_name, is_winner, match_id };
        self.runtime.schedule_operation(&op);
        true
    }
	  async fn start_season(&self, name: String) -> bool {
        info!("[LEADERBOARD] Starting season mutation: name={}", name);
        let op = Operation::StartSeason { name };
        self.runtime.schedule_operation(&op);
        true
    }

    async fn end_season(&self) -> bool {
        info!("[LEADERBOARD] Ending season mutation (current season)");
        let op = Operation::EndSeason;
        self.runtime.schedule_operation(&op);
        true
    }
	
}

struct QueryRoot {
    state: Arc<LeaderboardState>,
}

#[Object]
impl QueryRoot {
    async fn global_leaderboard(&self, limit: Option<u64>) -> Vec<LeaderboardEntry> {
        self.get_leaderboard_entries("global", limit).await
    }

    // Season leaderboard hiện tại (mặc định)
    async fn current_season_leaderboard(&self, limit: Option<u64>) -> Vec<LeaderboardEntry> {
        self.get_leaderboard_entries("season", limit).await
    }

    // Season leaderboard cụ thể
    async fn season_leaderboard(&self, season_number: u64, limit: Option<u64>) -> Vec<LeaderboardEntry> {
        self.get_past_season_entries(season_number, limit).await
    }
    
    async fn tournament_top_8(&self) -> Vec<LeaderboardEntry> {
        self.get_leaderboard_entries("season", Some(8)).await
    }
    
	async fn current_season_info(&self) -> Option<SeasonInfo> {
        let season_number = *self.state.current_season.get();
        self.get_season_info(season_number).await
    }
	
	async fn season_info(&self, season_number: u64) -> Option<SeasonInfo> {
        self.get_season_info(season_number).await
    }
    
	// User in global
    async fn user_global_stats(&self, user_name: String) -> Option<UserGlobalStats> {
        let total_matches = self.state.global_total_matches.get(&*user_name).await.ok().flatten().unwrap_or(0);
        let total_wins = self.state.global_total_wins.get(&*user_name).await.ok().flatten().unwrap_or(0);
        let total_losses = self.state.global_total_losses.get(&*user_name).await.ok().flatten().unwrap_or(0);
        let score = self.state.global_scores.get(&*user_name).await.ok().flatten().unwrap_or(0);
        let last_play = self.state.last_play_timestamps.get(&*user_name).await.ok().flatten();

        if total_matches == 0 {
            return None;
        }

        Some(UserGlobalStats {
            user_name,
            total_matches,
            total_wins,
            total_losses,
            score,
            last_play,
        })
    }
}

impl QueryRoot {
	// Lấy leaderboard data  của season hiện tại
    async fn get_leaderboard_entries(&self, leaderboard_type: &str, limit: Option<u64>) -> Vec<LeaderboardEntry> {
        let mut entries = Vec::new();

        match leaderboard_type {
            "global" => {
                let user_names = self.state.global_scores.indices().await.unwrap_or_default();
                for user_name in &user_names {
                    let score = self.state.global_scores.get(&**user_name).await.ok().flatten().unwrap_or(0);
                    let total_matches = self.state.global_total_matches.get(&**user_name).await.ok().flatten().unwrap_or(0);
                    let total_wins = self.state.global_total_wins.get(&**user_name).await.ok().flatten().unwrap_or(0);
                    let total_losses = self.state.global_total_losses.get(&**user_name).await.ok().flatten().unwrap_or(0);
                    
                    entries.push(LeaderboardEntry {
                        user_name: user_name.clone(),
                        score,
                        total_matches,
                        total_wins,
                        total_losses,
                    });
                }
            }
            "season" => {
                let user_names = self.state.season_scores.indices().await.unwrap_or_default();
                for user_name in &user_names {
                    let score = self.state.season_scores.get(&**user_name).await.ok().flatten().unwrap_or(0);
                    let total_matches = self.state.season_total_matches.get(&**user_name).await.ok().flatten().unwrap_or(0);
                    let total_wins = self.state.season_total_wins.get(&**user_name).await.ok().flatten().unwrap_or(0);
                    let total_losses = self.state.season_total_losses.get(&**user_name).await.ok().flatten().unwrap_or(0);
                    
                    entries.push(LeaderboardEntry {
                        user_name: user_name.clone(),
                        score,
                        total_matches,
                        total_wins,
                        total_losses,
                    });
                }
            }
            _ => panic!("Invalid leaderboard type: {}", leaderboard_type),
        }

        entries.sort_by(|a, b| b.score.cmp(&a.score));
        let limit = limit.unwrap_or(entries.len() as u64) as usize;
        entries.into_iter().take(limit).collect()
    }
	
	// Get leaderboard data from old season
    async fn get_past_season_entries(&self, season_number: u64, limit: Option<u64>) -> Vec<LeaderboardEntry> {
        let mut entries = Vec::new();
        let prefix = format!("{}:", season_number);

		// Get all kes from past_season_scores and filter by season
        let all_keys = self.state.past_season_scores.indices().await.unwrap_or_default();
        
        for key in &all_keys {
            if key.starts_with(&prefix) {
                if let Some(user_name) = key.strip_prefix(&prefix) {
                    let score = self.state.past_season_scores.get(&**key).await.ok().flatten().unwrap_or(0);
                    let total_matches = self.state.past_season_matches.get(&**key).await.ok().flatten().unwrap_or(0);
                    let total_wins = self.state.past_season_wins.get(&**key).await.ok().flatten().unwrap_or(0);
                    let total_losses = self.state.past_season_losses.get(&**key).await.ok().flatten().unwrap_or(0);
                    
                    entries.push(LeaderboardEntry {
                        user_name: user_name.to_string(),
                        score,
                        total_matches,
                        total_wins,
                        total_losses,
                    });
                }
            }
        }

        entries.sort_by(|a, b| b.score.cmp(&a.score));
        let limit = limit.unwrap_or(entries.len() as u64) as usize;
        entries.into_iter().take(limit).collect()
    }
	
	// Get info leaderboard from old season
	async fn get_season_info(&self, season_number: u64) -> Option<SeasonInfo> {
        if let Some(metadata) = self.state.season_metadata.get(&season_number).await.ok().flatten(){
            // Tính total players cho season
            let total_players = if season_number == *self.state.current_season.get() {
                self.state.season_scores.indices().await.unwrap_or_default().len() as u64
            } else {
                // Đếm players từ past seasons
                let prefix = format!("{}:", season_number);
                let keys = self.state.past_season_scores.indices().await.unwrap_or_default();
                keys.iter().filter(|k| k.starts_with(&prefix)).count() as u64
            };
			
			// TÍNH TOÁN DURATION_DAYS Ở SERVICE
           let duration_days = if let Some(end_time) = metadata.end_time {
					let duration_micros = end_time - metadata.start_time;
					Some(duration_micros as f64 / (24.0 * 60.0 * 60.0 * 1_000_000.0))
					} else {
						None
					};       
            
            Some(SeasonInfo {
                number: season_number,
                name: metadata.name,
                start_time: metadata.start_time,
                end_time: metadata.end_time,
				duration_days,
                status: match metadata.status {
                    SeasonStatus::Active => "active".to_string(),
                    SeasonStatus::Ended => "ended".to_string(),
                },
                total_players,
            })
        } else {
            // Fallback old seasons if do not have metadata
            Some(SeasonInfo {
                number: season_number,
                name: format!("Season {}", season_number),
                start_time: 0,
                end_time: None,
				duration_days: None,
                status: if season_number < *self.state.current_season.get() {
                    "ended".to_string()
                } else {
                    "active".to_string()
                },
                total_players: 0,
            })
        }
    }
}