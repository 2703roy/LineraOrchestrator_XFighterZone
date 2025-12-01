// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0  
// userxfighter/src/contract.rs

#![cfg_attr(target_arch = "wasm32", no_main)]

mod state;

use linera_sdk::{
    abi::WithContractAbi,
    views::{RootView, View},
    Contract, ContractRuntime,
};
use log::info;
use std::str::FromStr; 
use userxfighter::{Operation, Parameters};
use tournament_shared::TournamentOperation;
use friendxfighter::{Operation as FriendOperation, FriendAbi};
use self::state::{UserXfighterState, Transaction};

use linera_sdk::linera_base_types::{ChainId, AccountOwner, Amount};
use linera_sdk::abis::fungible::{self, FungibleOperation, FungibleTokenAbi};
linera_sdk::contract!(UserXfighterContract);

pub struct UserXfighterContract {
    state: UserXfighterState,
    runtime: ContractRuntime<Self>,
}

impl WithContractAbi for UserXfighterContract {
    type Abi = userxfighter::UserXfighterAbi;
}

impl Contract for UserXfighterContract {
    type Parameters = Parameters;
    type InstantiationArgument = ();
    type Message = TournamentOperation;
    type EventValue = ();

    async fn load(runtime: ContractRuntime<Self>) -> Self {
        let state = UserXfighterState::load(runtime.root_view_storage_context())
            .await
            .expect("Failed to load state");
        UserXfighterContract { state, runtime }
    }

    async fn instantiate(&mut self, _argument: Self::InstantiationArgument) {
        let params: Parameters = self.runtime.application_parameters();
        info!("UserXFighter initialized with local tournament app: {:?}", params.local_tournament_app_id);

        // Register with Tournament local app & Trigger token distribution for test betting
        let register_op = TournamentOperation::RegisterUserXFighter {
            user_xfighter_app_id: self.runtime.application_id().forget_abi(),
        };
        
        self.runtime.call_application::<tournament_shared::TournamentAbi>(
            true,
            params.local_tournament_app_id,
            &register_op,
        );
        
        info!("Registered UserXFighter with local Tournament app");
    }

    async fn store(mut self) {
        self.state.save().await.expect("Failed to save state");
    }
    
    async fn execute_operation(&mut self, operation: Self::Operation) {
        match operation {
			// === Betting Operations Methos		
            Operation::PlaceBet { match_id, player, amount } => {
				info!("[USER-XFIGHTER] PlaceBet - match: {}, player: {}, amount: {}", 
                  match_id, player, amount);
				  
                let signer = self.runtime.authenticated_signer().unwrap();
				
				// USER TRẢ TIỀN BET NGAY KHI PLACE BET
				self.transfer_bet_to_tournament(signer, amount, &match_id).await;
				
                let bet_id = format!("bet_{}_{}", signer, self.runtime.system_time().micros());
				let bettor_public_key = signer.to_string(); // AccountOwner
				
                // SEND TOURNAMENT LOCAL ON SAME USER CHAIN
                let params: Parameters = self.runtime.application_parameters();
                let tournament_call = TournamentOperation::PlaceBet {
                    bet_id: bet_id.clone(),
                    match_id: match_id.clone(),
                    player: player.clone(),
                    amount,
                    bettor: signer.to_string(),
					bettor_public_key: bettor_public_key.clone(), // Gửi public key
                    user_chain: self.runtime.chain_id(),
                };
	
				info!("[USER CHAIN {:?}] Calling LOCAL tournament app with public key: {}",  self.runtime.chain_id(), bettor_public_key);
                
                self.runtime.call_application::<tournament_shared::TournamentAbi>(
					true,
					params.local_tournament_app_id,
					&tournament_call,
				);

                // Lưu transaction local
                let tx = Transaction {
                    tx_id: bet_id.clone(),
                    tx_type: "bet_placed".to_string(),
                    amount,
                    timestamp: self.runtime.system_time().micros(),
                    related_id: Some(match_id.clone()),
                    status: "paid".to_string(),
                };
                self.state.transactions.insert(&tx.tx_id.clone(), tx).unwrap();
                
                info!("Bet sent to tournament with public key: {}", bettor_public_key);
				info!("Bet placed with payment: {} tokens for match {}", amount, match_id);
            }			
			 Operation::RecordPayout { bet_id, match_id, amount, user_public_key: _, is_win } => {
				info!("[USER-XFIGHTER] RecordPayout operation - bet: {}, win: {}", bet_id, is_win);
				self.record_payout(&bet_id, &match_id, amount, is_win).await;
				info!("[USER-XFIGHTER] RecordPayout completed for bet: {}", bet_id);
			} 
			
			// === Friend Operations Methos
            Operation::SendFriendRequest { to_user_chain } => {
                self.send_friend_request(to_user_chain).await;
            }
            Operation::AcceptFriendRequest { request_id } => {
                self.accept_friend_request(request_id).await;
            }
            Operation::RejectFriendRequest { request_id } => {
                self.reject_friend_request(request_id).await;
            }
            Operation::RemoveFriend { friend_chain_id } => {
                self.remove_friend(friend_chain_id).await;
            }
        }
    }

    async fn execute_message(&mut self, message: Self::Message) {
        match message {
            TournamentOperation::PlaceBet { .. } => {
                info!("[USER-XFIGHTER] Received PlaceBet echo - ignoring");
            }
			
			TournamentOperation::Payout { bet_id, match_id, amount, user_public_key: _, user_chain, is_win } => {
                if user_chain == self.runtime.chain_id() {
					info!("[USER-XFIGHTER] Processing payout for bet: {} - amount: {}, win: {}", bet_id, amount, is_win);
					info!("[USER-XFIGHTER] Verified user chain match confirmed: {:?}", user_chain);
                    self.record_payout(&bet_id, &match_id, amount, is_win).await;
                    info!("Direct payout recorded for bet {}", bet_id);
					
				} else {
					info!("[USER-XFIGHTER] Ignoring payout for different chain: {}", user_chain);
				}
            }

            _ => {
                info!("[USER-XFIGHTER] Ignoring non-payout message");
            }		
        }
    }
}

impl UserXfighterContract {
	// === TOURNAMENT PAYOUT METHODS  ===
	async fn record_payout(&mut self, bet_id: &str, match_id: &str, amount: u64, is_win: bool) {
		info!("[USER-XFIGHTER] record_payout - bet: {}, match: {}, amount: {}, win: {}", 
          bet_id, match_id, amount, is_win);
		
        if is_win {
			// Only winner received transaction payout transaction
			let payout_tx_id = format!("payout_{}", bet_id);
			let tx = Transaction {
				tx_id: payout_tx_id.clone(),
				tx_type: "payout_received".to_string(),
				amount,
				timestamp: self.runtime.system_time().micros(),
				related_id: Some(match_id.to_string()),
				status: "received".to_string(),
			};
			self.state.transactions.insert(&tx.tx_id.clone(), tx).unwrap();
			info!("[USER-XFIGHTER] Payout transaction created for winning bet");
		}
		
        // Update original bet status both win & lose
        match self.state.transactions.get(bet_id).await {
			Ok(Some(mut original_tx)) => {
				if is_win {
					original_tx.status = "won".to_string();
					info!("[USER-XFIGHTER] Bet {} updated to WON", bet_id);
				} else {
					original_tx.status = "lost".to_string();
					info!("[USER-XFIGHTER] Bet {} updated to LOST", bet_id);
				}
				
				self.state.transactions.insert(bet_id, original_tx)
					.expect("Failed to update original bet");
			}
			Ok(None) => {
				info!("[USER-XFIGHTER] Original bet not found: {}", bet_id);
			}
			Err(e) => {
				info!("[USER-XFIGHTER] Error retrieving original bet: {:?}", e);
			}
		}

		info!("[USER-XFIGHTER] Payout processing completed for bet: {} (win: {})", bet_id, is_win);
	}
	// === TOURNAMENT TRANSFER BET METHODS  ===
    async fn transfer_bet_to_tournament(&mut self, user: AccountOwner, amount: u64, match_id: &str) {
		info!("[USER-XFIGHTER] Starting transfer - user: {}, amount: {}, match: {}", 
          user, amount, match_id);
		  
        let params: Parameters = self.runtime.application_parameters();
        let fungible_app_id = params.fungible_app_id;
        
		if amount == 0 {
			info!("[USER-XFIGHTER] Error invalid amount: 0");
			return;
		}
		
        let amount_attos = amount as u128 * 1_000_000_000_000_000_000;
        
        // Tournament owner (publisher chain owner)
        let tournament_owner = AccountOwner::from_str(&params.tournament_owner).unwrap();
        let target_account = fungible::Account {
            chain_id: params.publisher_chain_id, // PUBLISHER CHAIN
            owner: tournament_owner,
        };
        
        let transfer_op = FungibleOperation::Transfer {
            owner: user, // User transfer
            amount: Amount::from_attos(amount_attos),
            target_account,
        };
        
		// Call fungible token app
        self.runtime.call_application::<FungibleTokenAbi>(
            true,
            fungible_app_id,
            &transfer_op,
        );
        
         info!("Transferred {} tokens from {} to tournament (chain {:?}) for match {}",
			amount, user, params.publisher_chain_id, match_id);
    }
	
	// === FRIEND METHODS ===
	// Send request to user chain
	async fn send_friend_request(&mut self, to_user_chain: ChainId) {
        let params: Parameters = self.runtime.application_parameters();
        let operation = FriendOperation::SendFriendRequest { to_user_chain };
        
        self.runtime.call_application::<FriendAbi>(
            true,
            params.friend_app_id,
            &operation,
        );
        
        info!("[USER-XFIGHTER] Sent friend request to chain: {}", to_user_chain);
    }
    
	// User chain accept pending request from user chain
    async fn accept_friend_request(&mut self, request_id: String) {
        let params: Parameters = self.runtime.application_parameters();
        let operation = FriendOperation::AcceptFriendRequest { request_id: request_id.clone() };
        
        self.runtime.call_application::<FriendAbi>(
            true,
            params.friend_app_id,
            &operation,
        );
        
        info!("[USER-XFIGHTER] Accepted friend request: {}", request_id);
    }
    
	// User chain reject pending request from user chain
    async fn reject_friend_request(&mut self, request_id: String) {
        let params: Parameters = self.runtime.application_parameters();
        let operation = FriendOperation::RejectFriendRequest { request_id: request_id.clone() };
        
        self.runtime.call_application::<FriendAbi>(
            true,
            params.friend_app_id,
            &operation,
        );
        
        info!("[USER-XFIGHTER] Rejected friend request: {}", request_id);
    }
    
	// User chain remove friend
    async fn remove_friend(&mut self, friend_chain_id: ChainId) {
        let params: Parameters = self.runtime.application_parameters();
        let operation = FriendOperation::RemoveFriend { friend_chain_id };
        
        self.runtime.call_application::<FriendAbi>(
            true,
            params.friend_app_id,
            &operation,
        );
        
        info!("[USER-XFIGHTER] Removed friend: {}", friend_chain_id);
    }
}