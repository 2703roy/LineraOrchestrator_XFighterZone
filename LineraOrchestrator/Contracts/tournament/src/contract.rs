// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// tournament/src/contract.rs

#![cfg_attr(target_arch = "wasm32", no_main)]

mod state;

use log::info;
use std::str::FromStr; 
use linera_sdk::{
    abi::WithContractAbi,
    views::{RootView, View},
    Contract, ContractRuntime,
};
use tournament::{TournamentAbi, Operation, Parameters};
use tournament_shared::{TournamentOperation, TournamentMetadata, TournamentStatus};
use crate::state::{TournamentState, BetEntry};

use linera_sdk::linera_base_types::{ChainId, AccountOwner, Amount};
use linera_sdk::abis::fungible::{self, FungibleOperation, FungibleTokenAbi};
linera_sdk::contract!(TournamentContract);

pub struct TournamentContract {
    state: TournamentState,
    runtime: ContractRuntime<Self>,
}

impl WithContractAbi for TournamentContract {
    type Abi = TournamentAbi;
}

impl Contract for TournamentContract {
    type Parameters =Parameters;
    type InstantiationArgument = Option<Vec<String>>;
    type Message = TournamentOperation;
    type EventValue = ();

    async fn load(runtime: ContractRuntime<Self>) -> Self {
        let state = TournamentState::load(runtime.root_view_storage_context())
            .await
            .expect("Failed to load state");
        Self { state, runtime }
    }

    async fn instantiate(&mut self, argument: Self::InstantiationArgument) {
        if let Some(usernames) = argument {
            self.state.participants.set(usernames);
        }

		// initialize tournament season 0 with status ended
		let tournament_0_metadata = TournamentMetadata {
			name: "Tournament 0".to_string(),
			start_time: 0,
			end_time: Some(0),
			status: TournamentStatus::Ended,
			champion: None,
			runner_up: None,
		};
		
		self.state.tournament_metadata.insert(&0, tournament_0_metadata)
			.expect("Failed to save tournament 0 metadata");
		self.state.current_tournament.set(0);
		
		self.state.total_bets_placed.set(0);
		self.state.total_bets_settled.set(0);
		self.state.total_payouts.set(0);
		info!("Tournament contract instantiated with Tournament 0 (Ended)");
		
		/*
		// Wave 4: User get airdrop from tournament
		let airdrop_amount = self.runtime.application_parameters().airdrop_amount.unwrap_or(10000);
		self.state.airdrop_amount.set(airdrop_amount);
		info!("Tournament contract instantiated with airdrop amount: 10000");
		*/
    }

    async fn execute_operation(&mut self, operation: Operation) {
        match operation {		
			// === Tournament management opeation ===
			// Start tournament season
			Operation::StartTournamentSeason { name } => {
                info!("[TOURNAMENT] Operation::StartTournamentSeason name={}", name);
                self.start_tournament_season(name).await;
            }
			
			// End tournament season
			Operation::EndTournamentSeason { } => {
                info!("[TOURNAMENT] Operation::EndTournamentSeason");
                self.end_tournament_season().await;
            }
			
			// Set participant for tournament data
            Operation::SetParticipants { participants } => {
				info!("[TOURNAMENT] Operation::SetParticipants");
				self.state.participants.set(participants.clone());
				
				for participant in &participants {
					self.state.tournament_leaderboard.insert(participant, 0)
						.expect("Failed to initialize participant in leaderboard");
				}
				info!("[TOURNAMENT] Initialized {} participants in leaderboard", participants.len());
			}
            
			// SetBracket for tournament matchlist
            Operation::SetBracket { matches } => {
                for input_match in matches {
                    let match_id = input_match.match_id.clone();
                    let storage_match = crate::state::TournamentMatch {
                        match_id: match_id.clone(),
                        player1: input_match.player1,
                        player2: input_match.player2,
                        winner: input_match.winner,
                        round: input_match.round,
                        status: input_match.status,
                    };
					info!("[TOURNAMENT] Operation::SetBracket");
                    self.state.bracket.insert(&match_id, storage_match).unwrap();
                }
            }
				
           Operation::RecordMatch { match_id, winner, loser } => {
				if match_id.is_empty() || winner.is_empty() || loser.is_empty() {
					info!("[TOURNAMENT] Invalid match record data");
					return;
				}   
				self.state.results.insert(&match_id, (winner.clone(), loser)).unwrap();
				
				if let Ok(Some(mut match_data)) = self.state.bracket.get(&match_id).await {
					match_data.winner = Some(winner.clone());
					match_data.status = "completed".to_string();
					self.state.bracket.insert(&match_id, match_data).unwrap();
				}
				
				self.update_tournament_leaderboard(&winner).await;
			}
			
			// === Tournament betting opeation ===
            Operation::SettleMatch { match_id, winner } => {
                self.settle_match_simple(match_id, winner).await;
            }
			
			// Open match for betting
            Operation::SetMatchMetadata { match_id, betting_deadline_unix, match_start_unix, status } => {
                let status_enum = match status.as_deref() {
                    Some("Closed") => crate::state::MatchStatus::Closed,
                    Some("Settled") => crate::state::MatchStatus::Settled,
                    _ => crate::state::MatchStatus::Open,
                };

                let metadata = crate::state::MatchMetadata {
                    match_id: match_id.clone(),
                    betting_deadline_unix,
                    match_start_unix,
                    status: status_enum,
                };
                
                self.state.matches.insert(&match_id, metadata).unwrap();
                info!("Match metadata set for: {}", match_id);
            }
				
            Operation::PlaceBet { bet_id, match_id, player, amount, bettor, bettor_public_key, user_chain } => {
				let publisher_chain: ChainId = self.runtime.application_creator_chain_id();
				info!("[LOCAL→MAIN] Received PlaceBet op: {}, forwarding to publisher main chain: {:?}", bet_id, publisher_chain);
				let bet_message = TournamentOperation::PlaceBet {
						bet_id,
						match_id,
						player,
						amount,
						bettor,
						bettor_public_key,
						user_chain,
					};	
				self.runtime
						.prepare_message(bet_message)
						.with_authentication()
						.with_tracking()
						.send_to(publisher_chain);
				}
			
			Operation::Payout { bet_id: _, match_id: _, amount: _, user_public_key: _, user_chain: _ , is_win: _ }=> {
				info!("[TOURNAMENT] Received Payout operation - ignoring, should only be received by UserXfighter");
			}
			
			// UserXFighter Registration / User Chain
			Operation::RegisterUserXFighter { user_xfighter_app_id } => {
                let user_chain = self.runtime.chain_id();
                let user_xfighter_app_id = user_xfighter_app_id.with_abi::<userxfighter::UserXfighterAbi>();              
                self.state.user_xfighter_app_ids.insert(&user_chain, user_xfighter_app_id)
                    .expect("Failed to register UserXFighter app ID");
                info!("Registered UserXFighter app: {:?} for chain: {:?}", user_xfighter_app_id, user_chain);
            }			
        }
    }

    async fn execute_message(&mut self, message: Self::Message) {
        match message {				
            TournamentOperation::PlaceBet { bet_id, match_id, player, amount, bettor, bettor_public_key, user_chain } => {
				 // Handling bouncing place bet message
				 let is_bouncing = self
					.runtime
					.message_is_bouncing()
					.expect("Delivery status is available when executing a message");
				
				if is_bouncing {
					info!("[TOURNAMENT-LOCAL] PLACEBET MESSAGE BOUNCING - bet: {}", bet_id);
					// Refund or recovery action
					return;
				}
				info!("[TOURNAMENT-LOCAL] Received PlaceBet message: {}", bet_id);
                self.store_bet(bet_id.clone(), match_id, player, amount, bettor, bettor_public_key, user_chain).await;
				info!("[TOURNAMENT-LOCAL] Bet stored via cross-chain message: {}", bet_id);
            }
			
			TournamentOperation::Payout { bet_id, match_id, amount, user_public_key, user_chain, is_win } => {
				// Handling bouncing payout message 
				let is_bouncing = self
                .runtime
                .message_is_bouncing()
                .expect("Delivery status is available when executing a message");
				
				if is_bouncing {
					info!("[TOURNAMENT-LOCAL] payout message bouncing - bet: {}, chain: {:?}", bet_id, user_chain);
					// Refund or recovery action
					info!("[TOURNAMENT-LOCAL] payout notification was rejected by target chain");
					return;
				}
				
				info!("[TOURNAMENT-LOCAL] Received payout notification - bet: {}, win: {}, amount: {}", 
                  bet_id, is_win, amount);
				info!("[TOURNAMENT-LOCAL] Current chain: {:?}", self.runtime.chain_id());
				
				//  GỌI TRỰC TIẾP USERXFIGHTER
				if user_chain == self.runtime.chain_id() {
					info!("[TOURNAMENT-LOCAL] Directly calling UserXFighter for bet: {}", bet_id);
					self.forward_payout_to_userxfighter(bet_id, match_id, amount, user_public_key, is_win).await;
				} else {
					info!("[TOURNAMENT-LOCAL] Ignoring payout for different chain: {}", user_chain);
				}
			}
			_ => {
				info!("[TOURNAMENT] Ignoring non-bet message: {:?}", message);
			}
	
        }
    }
	
	async fn store(mut self) {
		self.state.save().await.expect("Failed to save state");
	}
}

impl TournamentContract {
	// === Tournament Management Methos ===
	// Start tournament
	async fn start_tournament_season(&mut self, name: String) {
		let current_tournament = *self.state.current_tournament.get();
		
		// check current tournament - if active then end first
		if let Some(metadata) = self.state.tournament_metadata.get(&current_tournament).await.ok().flatten() {
			if metadata.status == TournamentStatus::Active {
				info!("[TOURNAMENT] Current tournament {} is still active, ending it first", current_tournament);
				self.end_tournament_season().await;
			}
		}
	
		//1. Clear all tournament data
		self.clear_tournament_data(current_tournament).await;
		
		//2. Reset tournament state
		self.state.participants.set(Vec::new());
		self.state.status.set("active".to_string());
		self.state.current_round.set(String::new());
		self.state.current_champion.set(String::new());
		self.state.current_runner_up.set(String::new());
		
		// 3. Create new tournament season
		let new_tournament = current_tournament + 1;
		let start_time = self.runtime.system_time().micros();
		
		// 4. Reset metadata
		let new_metadata = TournamentMetadata {
			name: name.clone(),
			start_time,
			end_time: None,
			status: TournamentStatus::Active,
			champion: None,
			runner_up: None,
		};
		
		self.state.tournament_metadata.insert(&new_tournament, new_metadata)
			.expect("Failed to save new tournament metadata");
		self.state.current_tournament.set(new_tournament);
		info!("Started new tournament season {}: {} at {}", new_tournament, name, start_time);
	}
	
    //  End tournament
    async fn end_tournament_season(&mut self) {
		let current_tournament = *self.state.current_tournament.get();
		let end_time = self.runtime.system_time().micros();
		
		// Read champion/runner-up from RegisterView
		let champion = self.state.current_champion.get().clone();
		let runner_up = self.state.current_runner_up.get().clone();
		
		info!("[TOURNAMENT] Ending tournament {} - Champion: {}, Runner-up: {}", 
			  current_tournament, champion, runner_up);

		// 1. Update tournament metadata
		if let Some(mut metadata) = self.state.tournament_metadata.get(&current_tournament).await.ok().flatten() {
			
			metadata.status = TournamentStatus::Ended;
			metadata.end_time = Some(end_time);
			metadata.champion = if champion.is_empty() { None } else { Some(champion.clone()) };
			metadata.runner_up = if runner_up.is_empty() { None } else { Some(runner_up.clone()) };
			
			self.state.tournament_metadata.insert(&current_tournament, metadata)
				.expect("Failed to update tournament metadata");
				
			info!("[TOURNAMENT] Tournament {} metadata updated", current_tournament);	
		}

		// 2. Save current tournament data to past tournaments
		 if let Ok(usernames) = self.state.tournament_leaderboard.indices().await {
			for username in &usernames {
				let tournament_key = format!("{}:{}", current_tournament, username);
				
				if let Some(score) = self.state.tournament_leaderboard.get(&**username).await.ok().flatten() {
					if let Err(e) = self.state.past_tournament_leaderboards.insert(&tournament_key, score) {
						info!("[ERROR] Failed to save past tournament leaderboard for {}: {:?}", tournament_key, e);
					}
				}
			}
			
			info!("[TOURNAMENT] Saved tournament {} leaderboard for {} usernames", 
                  current_tournament, usernames.len());
		}
		// 3. Update current state
        self.state.status.set("ended".to_string());
        
        info!("[TOURNAMENT] Tournament {} ended successfully - Champion: {}, Runner-up: {}", 
              current_tournament, champion, runner_up);
	}
	
	// === Tournament Leaderboard Helper Methods ===
	async fn update_tournament_leaderboard(&mut self, winner: &str) {
        // 1. Update score for winner
        let current_score = self.state.tournament_leaderboard.get(winner).await.ok().flatten().unwrap_or(0);
        let new_score = current_score + 1;
        self.state.tournament_leaderboard.insert(winner, new_score)
            .expect("Failed to update tournament leaderboard");

        let current_champion = self.state.current_champion.get().clone();
        
		
        //2. Logic champion/runner-up
        if current_champion.is_empty() {
            self.state.current_champion.set(winner.to_string());
        } else {
            let champ_score = self.state.tournament_leaderboard.get(&current_champion).await.ok().flatten().unwrap_or(0);
            
            if new_score > champ_score {
                self.state.current_runner_up.set(current_champion);
                self.state.current_champion.set(winner.to_string());
            } else if new_score > self.state.tournament_leaderboard.get(self.state.current_runner_up.get()).await.ok().flatten().unwrap_or(0) {
                if winner != current_champion {
                    self.state.current_runner_up.set(winner.to_string());
                }
            }
        }

        //3. Update metadata
        let current_tournament = *self.state.current_tournament.get();
        if let Some(mut metadata) = self.state.tournament_metadata.get(&current_tournament).await.ok().flatten() {
            metadata.champion = if self.state.current_champion.get().is_empty() { 
                None 
            } else { 
                Some(self.state.current_champion.get().clone())
            };
            metadata.runner_up = if self.state.current_runner_up.get().is_empty() { 
                None 
            } else { 
                Some(self.state.current_runner_up.get().clone())
            };
            
            self.state.tournament_metadata.insert(&current_tournament, metadata)
                .expect("Failed to update tournament metadata");
        }

        info!("[LEADERBOARD] Updated {} -> {} points (champ: {}, runner: {})", 
              winner, new_score, self.state.current_champion.get(), self.state.current_runner_up.get());
    }
	
	//  Clear all tournament data
	async fn clear_tournament_data(&mut self, tournament_number: u64) {
		// Clear leaderboard
		if let Ok(usernames) = self.state.tournament_leaderboard.indices().await {
			for username in &usernames {
				let _ = self.state.tournament_leaderboard.remove(&**username);
			}
			info!("Cleared tournament {} leaderboard for {} usernames", tournament_number, usernames.len());
		}
		
		// Clear bracket
		if let Ok(match_ids) = self.state.bracket.indices().await {
			for match_id in &match_ids {
				let _ = self.state.bracket.remove(&**match_id);
			}
			info!("Cleared tournament {} bracket with {} matches", tournament_number, match_ids.len());
		}
		
		// Clear results
		if let Ok(result_ids) = self.state.results.indices().await {
			for result_id in &result_ids {
				let _ = self.state.results.remove(&**result_id);
			}
			info!("Cleared tournament {} results with {} entries", tournament_number, result_ids.len());
		}
		
		// Clear bets (nếu muốn reset cả betting data)
		if let Ok(match_ids) = self.state.bets.indices().await {
			for match_id in &match_ids {
				let _ = self.state.bets.remove(&**match_id);
			}
			info!("Cleared tournament {} bets for {} matches", tournament_number, match_ids.len());
		}
	}
	
	// === Tournament Betting Methos ===
	// Betting store bet to tournament state
    async fn store_bet(&mut self, bet_id: String, match_id: String, player: String, amount: u64, 
	bettor: String, bettor_public_key: String, user_chain: ChainId) {
		let bet_entry = BetEntry {
			bet_id: bet_id.clone(),
			match_id: match_id.clone(),
			bettor: bettor.clone(),
			bettor_public_key: bettor_public_key.clone(),
			predicted: player.clone(),
			amount,
			user_chain,
		};

		// Lấy danh sách cược hiện tại (Option<Vec<BetEntry>>)
		let mut bets_for_match = match self.state.bets.get(&match_id).await {
            Ok(Some(entries)) => entries,
            Ok(None) => Vec::new(),
            Err(_) => Vec::new(),
        };

		// Add new bet to list
		bets_for_match.push(bet_entry);
		
		// Update totalBetsPlaced when recieve bets from userchain
		let current_bets = *self.state.total_bets_placed.get();
		self.state.total_bets_placed.set(current_bets + 1);
		// Save list to state
		self.state.bets.insert(&match_id, bets_for_match).expect("Failed to insert bet");
		info!("Bet {} stored for match {} with public key {}", bet_id, match_id, bettor_public_key);
	}
	
	// Betting Settle match
    async fn settle_match_simple(&mut self, match_id: String, winner: String) {
		info!("[TOURNAMENT] Starting settlement for match {} - winner: {}", match_id, winner);
		
		if match_id.is_empty() || winner.is_empty() {
			info!("[TOURNAMENT] Invalid settle match parameters");
			return;
		}
			
        info!("[TOURNAMENT] Settling match {} - winner: {}", match_id, winner);
		// 1. Update total payout processed
        if let Some(entries) = self.state.bets.get(&match_id).await.unwrap_or_default() {	
			// Update total_bets_settled
			let current_settled = *self.state.total_bets_settled.get();
			self.state.total_bets_settled.set(current_settled + entries.len() as u64);
			
			let mut total_payout = 0u64;
			let mut win_count = 0;
			let mut lose_count = 0;
			
			info!("[TOURNAMENT] Processing {} bets for match {}", entries.len(), match_id);
			
			// Basic payout Testing for 1 user (no pool)
			 for bet in &entries {
				let is_win = bet.predicted == winner;
				
				if is_win {
					win_count += 1;
					total_payout += bet.amount;
					let payout_amount = bet.amount;

					info!(" [TOURNAMENT] WINNING BET: {} tokens to {}", payout_amount, bet.bettor);
					
					// 1. Send fungible token payout for winners
					self.send_payout_via_fungible(
						&bet.bettor_public_key,
						bet.user_chain,
						payout_amount,
						&bet.bet_id,
						&match_id,
					).await;

					info!("[TOURNAMENT] Payout message sent to winner: {} with player {}", bet.bettor, bet.predicted);
				} else {
					lose_count += 1;
					info!("[TOURNAMENT] Payout message: {} lost on player {}", bet.bettor, bet.predicted);
				}
					
					// 2. Send cross-chain message to notify Tournament on user chain
					let payout_message = TournamentOperation::Payout {
						bet_id: bet.bet_id.clone(),
						match_id: match_id.clone(),   
						amount: if is_win { bet.amount } else { 0 },
						user_public_key: bet.bettor_public_key.clone(),
						user_chain: bet.user_chain,
						is_win,
					};
					
					self.runtime
						.prepare_message(payout_message)
						.with_authentication()
						.with_tracking()
						.send_to(bet.user_chain);
						
					info!("[PUBLISHER] Cross-chain message sent to Tournament on user chain for bet: {}", bet.bet_id);
			}	
            /*
			// Wave 4
			// Advance payout with pool ( total payout = (total_pool * bet amount) / total_winner)
			let total_pool: u64 = entries.iter().map(|e| e.amount).sum();
            let winning_bets: Vec<&BetEntry> = entries.iter()
                .filter(|e| e.predicted == winner)
                .collect();
            
            let total_winner: u64 = winning_bets.iter().map(|e| e.amount).sum();
            if total_winner > 0 {
                for bet in winning_bets {
                    let payout_amount = ((bet.amount as u128 * total_pool as u128) / total_winner as u128) as u64;
                    
                    if payout_amount > 0 {
						// Gửi payout qua Fungible Token thay vì cross-chain message thông thường
						self.send_payout_via_fungible(
							&bet.bettor_public_key,
							bet.user_chain,
							payout_amount,
							&bet.bet_id
						).await;
                        
						info!("Sent fungible payout {} to {}", payout_amount, bet.bettor);
                    }
                }
            }*/
			
			// Update total paid reward amount
			let current_payouts = *self.state.total_payouts.get();
			self.state.total_payouts.set(current_payouts + total_payout);
			
			// Clear bets
            self.state.bets.insert(&match_id, Vec::new()).unwrap();
			
			info!("[TOURNAMENT] Settlement completed for match {}", match_id);
			info!("[TOURNAMENT] Result - Winners: {}, Losers: {}, Total payout: {}", win_count, lose_count, total_payout);
		} else {
			info!("[TOURNAMENT] No bets found for match {}", match_id);
		}
    }
	
	// Betting: Send payout via fungible tokens
	async fn send_payout_via_fungible(&mut self, user_public_key: &str, 
		user_chain: ChainId, amount: u64, bet_id: &str, match_id: &str) {
			
		let params = self.runtime.application_parameters();
		let fungible_app_id = params.fungible_app_id;
		let current_signer = self.runtime.authenticated_signer()
			.expect("Must have authenticated signer for payout");
		
		// Secure account parsing with comprehensive error handling
		let user_account_owner = match AccountOwner::from_str(user_public_key) {
			Ok(owner) => {
				info!("[TOURNAMENT-FUNGIBLE] Successfully parsed user account: {}", user_public_key);
				owner
			}
			Err(e) => {
				info!("Failed to parse user public key {}: {}", user_public_key, e);
				return;
			}
		};
		let amount_attos = amount as u128 * 1_000_000_000_000_000_000; // 1 token = 10^18 attos
		
		let target_account = fungible::Account {
			chain_id: user_chain,
			owner: user_account_owner,
		};
		
		info!("[TOURNAMENT-FUNGIBLE] ] To Initiating fungible transfer:");
		info!("From: {}", current_signer);
		info!("To: {} on chain {:?}", user_public_key, user_chain);
		info!("Amount: {} tokens ({} attos)", amount, amount_attos);
		info!("Bet: {}, Match: {}", bet_id, match_id);

		let transfer_op = FungibleOperation::Transfer {
			owner: current_signer,
			amount: Amount::from_attos(amount_attos),
			target_account,
		};
		
		// Fungible calling to transfer to userchain
		self.runtime.call_application::<FungibleTokenAbi>(
			true,
			fungible_app_id,
			&transfer_op,
		);
			
		info!("Fungible transfer initiated by {}: {} tokens ({} attos) to {} on chain {:?}", 
			  current_signer, amount, amount_attos, user_public_key, user_chain);	
		info!("[TOURNAMENT-FUNGIBLE] Completed payout for bet {} (match {})",  bet_id, match_id);	  
	}
	
	// Forward payout to UserXFighter app on the same chain
	async fn forward_payout_to_userxfighter(&mut self, bet_id: String, 
		match_id: String, amount: u64, user_public_key: String, is_win: bool) {
		
		let user_chain = self.runtime.chain_id();	
		info!("[TOURNAMENT-LOCAL] Looking for UserXFighter app ID for chain: {:?}", user_chain);
		info!("[TOURNAMENT-LOCAL] Forwarding payout to UserXFighter - bet: {}, win: {}", bet_id, is_win);
		
		// Get UserXFighter app ID from state
		match self.state.user_xfighter_app_ids.get(&user_chain).await {
			Ok(Some(user_xfighter_app_id)) => {
				info!("[TOURNAMENT-LOCAL] Found UserXFighter app: {:?}", user_xfighter_app_id);
				
				let op = userxfighter::Operation::RecordPayout {
					bet_id: bet_id.clone(),
					match_id: match_id.clone(),
					amount,
					user_public_key: user_public_key.clone(),
					is_win,
				};
				
				// Call UserXFighter application on same chain
				self.runtime.call_application::<userxfighter::UserXfighterAbi>(
					true,
					user_xfighter_app_id,
					&op,
				);
				
				info!("[TOURNAMENT-LOCAL] Forwarded to UserXFighter - bet: {}, win: {}", bet_id, is_win);
				info!("[TOURNAMENT] Payout details: Match: {}, Amount: {}, User: {}", 
					  match_id, amount, user_public_key);
			}
			Ok(None) => {
				info!("[TOURNAMENT-LOCAL] UserXFighter app not registered for chain: {:?}", user_chain);
				
				// Debug: print all registered app IDs
				if let Ok(keys) = self.state.user_xfighter_app_ids.indices().await {
					info!("[TOURNAMENT-LOCAL] Registered app IDs: {:?}", keys);
				}
			}
			Err(e) => {
					info!("[TOURNAMENT-LOCAL] Error getting UserXFighter app ID: {:?}", e);		
				}
			}
		}
	
	 /*
	 // Wave 4: User get airdrop from tournament
	 // Airdrop tokens to new users
	async fn airdrop_tokens(&mut self, user_chain: ChainId, user_public_key: String, amount: u64) {
		let recipient_key = format!("{}:{}", user_chain, user_public_key);
		
		// Check if user already received airdrop
		if self.state.airdrop_recipients.contains(&recipient_key).await.unwrap_or(false) {
			info!("[AIRDROP] User {} already received airdrop", recipient_key);
			return;
		}
		
		info!("[AIRDROP] Airdropping {} tokens to {} on chain {:?}", 
			  amount, user_public_key, user_chain);
		
		// Get system_time before calling send_payout_via_fungible
		let timestamp = self.runtime.system_time().micros();
		let bet_id = format!("airdrop_{}", timestamp);
		
		// Send tokens via fungible
		self.send_payout_via_fungible(
			&user_public_key,
			user_chain,
			amount,
			&bet_id,
			"welcome_airdrop",
		).await;
		
		// Mark as received
		self.state.airdrop_recipients.insert(&recipient_key)
			.expect("Failed to mark airdrop recipient");
		
		info!("[AIRDROP] Successfully airdropped {} tokens to {}", amount, user_public_key);
	}
    
    // Set default airdrop amount
    async fn set_airdrop_amount(&mut self, amount: u64) {
        self.state.airdrop_amount.set(amount);
        info!("[AIRDROP] Default airdrop amount set to: {}", amount);
    }*/
	
}

