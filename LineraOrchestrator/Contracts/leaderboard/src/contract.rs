// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// leaderboard/src/contract.rs

#![cfg_attr(target_arch = "wasm32", no_main)]

mod state;

use log::info;
use self::state::LeaderboardState;
use linera_sdk::{
    abi::WithContractAbi,
    views::{RootView, View},
    Contract, ContractRuntime,
};
use leaderboard::{LeaderboardAbi, Operation, RecordScoreMessage, SeasonMetadata, SeasonStatus};

linera_sdk::contract!(LeaderboardContract);

pub struct LeaderboardContract {
    state: LeaderboardState,
    runtime: ContractRuntime<Self>,
}

impl WithContractAbi for LeaderboardContract {
    type Abi = LeaderboardAbi;
}

impl Contract for LeaderboardContract {
    type Parameters = ();
    type InstantiationArgument = ();
    type Message = RecordScoreMessage;
    type EventValue = ();

    async fn load(runtime: ContractRuntime<Self>) -> Self {
        let state = LeaderboardState::load(runtime.root_view_storage_context())
            .await
            .expect("Failed to load state");
        Self { state, runtime }
    }

    async fn instantiate(&mut self, _argument: Self::InstantiationArgument) {
		
		   let season_0_metadata = SeasonMetadata {
            name: "Season 0".to_string(),
            start_time: 0,
            end_time: Some(0),
            status: SeasonStatus::Ended,
            duration_days: None,
        };
        
        self.state.season_metadata.insert(&0, season_0_metadata)
            .expect("Failed to save season 0 metadata");
        self.state.current_season.set(0);
       info!("Leaderboard Global / Season contract instantiated with Season 0 (Ended)");
    }

    async fn store(mut self) {
        self.state.save().await.expect("Failed to save state");
    }

    async fn execute_operation(&mut self, operation: Self::Operation) -> Self::Response {
        match operation {
            Operation::RecordScore { user_name, is_winner, match_id } => {
                info!("[LEADERBOARD] Operation::RecordScore user={} is_winner={} match_id={}",
                      user_name, is_winner, match_id);
                self.update_score_and_stats(user_name, is_winner, match_id).await;
            }
			
			Operation::StartSeason { name} => {
                info!("[LEADERBOARD] Operation::StartSeason name={} ", name);
                self.start_season(name).await;
            }
            Operation::EndSeason => {
                info!("[LEADERBOARD] Operation::EndSeason");
                self.end_season().await;
            }
			
        }
    }

    async fn execute_message(&mut self, message: Self::Message) {
        let RecordScoreMessage { user_name, is_winner, match_id } = message;
        info!("[LEADERBOARD] Message::RecordScore user={} winner={} match={}", 
              user_name, is_winner, match_id);
        self.update_score_and_stats(user_name, is_winner, match_id).await;
    }
}

impl LeaderboardContract {
    async fn update_score_and_stats(&mut self, user_name: String, is_winner: bool, match_id: String) {
		let current_season = *self.state.current_season.get();
		info!("[LEADERBOARD] Processing score update - User: {}, Winner: {}, Match: {}", 
          user_name, is_winner, match_id);
		  
		// Validate Season Status
		if let Some(metadata) = self.state.season_metadata.get(&current_season).await.ok().flatten() {
			if metadata.status != SeasonStatus::Active {
				info!("[LEADERBOARD] Season {} is ended, skipping update for match: {}", current_season, match_id);
				return;
			}
		}
		
        // Global Leaderboard Update
        let mut global_wins = self.state.global_total_wins.get(&*user_name).await.ok().flatten().unwrap_or(0);
        let mut global_losses = self.state.global_total_losses.get(&*user_name).await.ok().flatten().unwrap_or(0);
        let mut global_matches = self.state.global_total_matches.get(&*user_name).await.ok().flatten().unwrap_or(0);
        
        if is_winner {
            global_wins += 1;
        } else {
            global_losses += 1;
        }
        global_matches += 1;
        let global_score = global_wins;
        
        self.state.global_total_wins.insert(&*user_name, global_wins)
		.expect("Failed to save global wins");
        self.state.global_total_losses.insert(&*user_name, global_losses)
		.expect("Failed to save global losses");
        self.state.global_total_matches.insert(&*user_name, global_matches)
		.expect("Failed to save global matches");
        self.state.global_scores.insert(&*user_name, global_score)
		.expect("Failed to save global score");

        //Season Leaderboard Update
        let mut season_wins = self.state.season_total_wins.get(&*user_name).await.ok().flatten().unwrap_or(0);
        let mut season_losses = self.state.season_total_losses.get(&*user_name).await.ok().flatten().unwrap_or(0);
        let mut season_matches = self.state.season_total_matches.get(&*user_name).await.ok().flatten().unwrap_or(0);
        
        if is_winner {
            season_wins += 1;
        } else {
            season_losses += 1;
        }
        season_matches += 1;
        let season_score = season_wins;
        
        self.state.season_total_wins.insert(&*user_name, season_wins)
		.expect("Failed to save season wins");
        self.state.season_total_losses.insert(&*user_name, season_losses)
		.expect("Failed to save season losses");
        self.state.season_total_matches.insert(&*user_name, season_matches)
		.expect("Failed to save season matches");
        self.state.season_scores.insert(&*user_name, season_score)
		.expect("Failed to save season score");

        // Mark match as processed
        self.state.processed_match_ids.insert(&*match_id, true)
		.expect("Failed to save processed match id");

        // Update last play timestamp
        let current_time = self.runtime.system_time().micros();
        self.state.last_play_timestamps.insert(&*user_name, current_time)
            .expect("Failed to save last play timestamp");

        info!("[LEADERBOARD] Updated - User: {}, Global: {}/{} wins, Season: {}/{} wins, Last play: {}", 
              user_name, global_wins, global_matches, season_wins, season_matches, current_time);
    }

	// === Leaderboard Start & End Season Methods===
	// Start new season - RESET và tạo season mới
	async fn start_season(&mut self, name: String) {
		let current_season = *self.state.current_season.get();
			
			// Check current season
			if let Some(metadata) = self.state.season_metadata.get(&current_season).await.ok().flatten() {
				if metadata.status == SeasonStatus::Active {
					info!("[LEADERBOARD] Current season {} is still active, ending it first", current_season);
					self.end_season().await;
				}
			}
			
			// 1. Clear current season data
			if let Ok(user_names) = self.state.season_scores.indices().await {
				for user_name in &user_names {
					let _ = self.state.season_scores.remove(&**user_name);
					let _ = self.state.season_total_matches.remove(&**user_name);
					let _ = self.state.season_total_wins.remove(&**user_name);
					let _ = self.state.season_total_losses.remove(&**user_name);
				}
				info!("[LEADERBOARD] Cleared season {} data for {} users", current_season, user_names.len());
			}
			
			// 2. Create new season
			let new_season = current_season + 1;
			let new_metadata = SeasonMetadata {
				name: format!("Season {}", new_season),
				start_time: self.runtime.system_time().micros(),
				end_time: None,
				status: SeasonStatus::Active,
				duration_days: None,
			};
			
			self.state.season_metadata.insert(&new_season, new_metadata)
				.expect("Failed to save new season metadata");    
			self.state.current_season.set(new_season);        
			info!("[LEADERBOARD] Started new season {}: {}", new_season, name);
		}		

	// End current season - mark ended và save data
	async fn end_season(&mut self) {
		let current_season = *self.state.current_season.get();
		let end_time = self.runtime.system_time().micros();
			
		// 1. Mark current season as ended và tính duration
		if let Some(mut metadata) = self.state.season_metadata.get(&current_season).await.ok().flatten() {
	
			// Set Leaderboard Metadata
			metadata.status = SeasonStatus::Ended;
			metadata.end_time = Some(end_time);
				
			self.state.season_metadata.insert(&current_season, metadata)
				.expect("Failed to update season metadata");
				
			info!("[LEADERBOARD] Season: {} end_time: {})", current_season, end_time);
		}						
			
			// 2. Save current season data to past seasons 
			if let Ok(user_names) = self.state.season_scores.indices().await {
				for user_name in &user_names {
					let season_key = format!("{}:{}", current_season, user_name);
					
					if let (Some(score), Some(matches), Some(wins), Some(losses)) = (
						self.state.season_scores.get(&**user_name).await.ok().flatten(),
						self.state.season_total_matches.get(&**user_name).await.ok().flatten(),
						self.state.season_total_wins.get(&**user_name).await.ok().flatten(),
						self.state.season_total_losses.get(&**user_name).await.ok().flatten(),
					) {
						// Save old season data
						self.state.past_season_scores.insert(&season_key, score)
							.expect("Failed to save past season score");
						self.state.past_season_matches.insert(&season_key, matches)
							.expect("Failed to save past season matches");
						self.state.past_season_wins.insert(&season_key, wins)
							.expect("Failed to save past season wins");
						self.state.past_season_losses.insert(&season_key, losses)
							.expect("Failed to save past season losses");
					}
				}
				info!("[LEADERBOARD] Saved season {} data for {} users", current_season, user_names.len());
			}
			
			info!("[LEADERBOARD] Season {} ended at {}", current_season, end_time);
		}		
			
}