// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// userxfighter/src/contract.rs

#![cfg_attr(target_arch = "wasm32", no_main)]

mod state;

use linera_sdk::linera_base_types::ChainId;
use linera_sdk::{abi::WithContractAbi, views::{RootView, View}, Contract, ContractRuntime};
use self::state::{UserXfighterState, Transaction};
use log::{info, error};

// Sử dụng full path cho các type
use userxfighter::{Operation, BettingMessage, Parameters, UserXfighterAbi};

linera_sdk::contract!(UserXfighterContract);

pub struct UserXfighterContract {
    state: UserXfighterState,
    runtime: ContractRuntime<Self>,
}

impl WithContractAbi for UserXfighterContract {
    type Abi = UserXfighterAbi;
}

impl Contract for UserXfighterContract {
    type Message = BettingMessage;
    type InstantiationArgument = ();
    type Parameters = Parameters;
    type EventValue = ChainId;

    async fn load(runtime: ContractRuntime<Self>) -> Self {
        let state = UserXfighterState::load(runtime.root_view_storage_context())
            .await
            .expect("Failed to load state");
        UserXfighterContract { 
            state, 
            runtime,
        }
    }

    async fn instantiate(&mut self, _argument: ()) {
        let params: Parameters = self.runtime.application_parameters();
        info!("UserXFighter contract initialized with tournament_id: {}", params.tournament_id);
        self.state.tournament_app_id.set(Some(params.tournament_id.clone()));
        self.state.tournament_chain_id.set(Some(params.tournament_chain_id));
    }
	
    async fn store(mut self) {
        self.state.save().await.expect("Failed to save state");
    }
	
    async fn execute_operation(&mut self, operation: Self::Operation) {
        match operation {
            Operation::Deposit { amount } => {
                let current_balance = *self.state.balance.get();
                self.state.balance.set(current_balance + amount);
                let timestamp = u64::from(self.runtime.block_height());
                let tx = Transaction {
                    tx_id: format!("deposit_{}", timestamp),
                    tx_type: "deposit".to_string(),
                    amount,
                    timestamp,
                    related_id: None,
                    status: "completed".to_string(),
                };
                self.state.transactions.insert(&tx.tx_id.clone(), tx)
                    .expect("Failed to insert transaction");
                
                info!("Deposited {} to user chain", amount);
            }
			
            Operation::Withdraw { amount } => {
                let current_balance = *self.state.balance.get();
                if current_balance >= amount {
                    self.state.balance.set(current_balance - amount);
                    let timestamp = u64::from(self.runtime.block_height());
                    
                    let tx = Transaction {
                        tx_id: format!("withdraw_{}", timestamp),
                        tx_type: "withdraw".to_string(),
                        amount,
                        timestamp,
                        related_id: None,
                        status: "completed".to_string(),
                    };
                    self.state.transactions.insert(&tx.tx_id.clone(), tx)
                        .expect("Failed to insert transaction");
                    
                    info!("Withdrew {} from user chain", amount);
                } else {
                    error!("Insufficient balance for withdrawal");
                }
            }
			
            Operation::Transfer { to, amount } => {
                info!("Transfer {} to {}", amount, to);
            }
        }
        
        // Luôn save state sau operation
        if let Err(e) = self.state.save().await {
            error!("Failed to save state after operation: {:?}", e);
        }
    }

    async fn execute_message(&mut self, message: Self::Message) {
        info!("[UserXFighter] Received message: {:?}", message);
        let timestamp = u64::from(self.runtime.block_height());
        
        match message {
            BettingMessage::DebitForBet { amount, tournament_id, bet_id, match_id } => {
                info!("[UserXFighter] Processing debit for bet: {}", bet_id);
                
                if self.state.processed_bets.get(&bet_id).await.unwrap_or(None).is_some() {
                    info!("[UserXFighter] Bet {} already processed", bet_id);
                    return;
                }

                let current_balance = *self.state.balance.get();
                if current_balance >= amount {
                    self.state.balance.set(current_balance - amount);
                    
                    let tx = Transaction {
                        tx_id: bet_id.clone(),
                        tx_type: "bet".to_string(),
                        amount,
                        timestamp,
                        related_id: Some(format!("{}_{}", tournament_id, match_id)),
                        status: "completed".to_string(),
                    };
                    self.state.transactions.insert(&tx.tx_id.clone(), tx)
                        .expect("[UserXFighter] Failed to insert transaction");
                    
                    self.state.processed_bets.insert(&bet_id, true)
                        .expect("[UserXFighter] Failed to mark bet as processed");
                    
                    info!("[UserXFighter] Debited {} for bet {}", amount, bet_id);
                } else {
                    error!("[UserXFighter] Insufficient balance for bet {}", bet_id);
                    
                    // GỬI REFUND MESSAGE TRỰC TIẾP - không đủ balance
                    if let (Some(tournament_app_id), Some(tournament_chain_id)) = (
                        self.state.tournament_app_id.get(),
                        self.state.tournament_chain_id.get()
                    ) {
                        let refund_message = BettingMessage::RefundBet {
                            amount,
                            tournament_id: tournament_app_id.clone(),
                            bet_id: bet_id.clone(),
                            match_id: match_id.clone(),
                        };
                        
                        self.runtime
                            .prepare_message(refund_message)
                            .with_authentication()
                            .with_tracking()
                            .send_to(*tournament_chain_id);
                            
                        info!("[UserXFighter] Sent refund message to tournament chain");
                    }
                }
            }
            
            BettingMessage::CreditForWin { amount, tournament_id, bet_id, match_id } => {
                info!("[UserXFighter] Processing CREDIT for win: {}", bet_id);
                
                if self.state.processed_bets.get(&bet_id).await.unwrap_or(None).is_some() {
                    info!("[UserXFighter] Payout for bet {} already processed", bet_id);
                    return;
                }

                let current_balance = *self.state.balance.get();
                self.state.balance.set(current_balance + amount);
                
                let tx = Transaction {
                    tx_id: format!("win_{}", bet_id),
                    tx_type: "win".to_string(),
                    amount,
                    timestamp,
                    related_id: Some(format!("{}_{}", tournament_id, match_id)),
                    status: "completed".to_string(),
                };
                self.state.transactions.insert(&tx.tx_id.clone(), tx)
                    .expect("Failed to insert transaction");
                
                self.state.processed_bets.insert(&bet_id, true)
                    .expect("Failed to mark payout as processed");
                
                info!("[UserXFighter] Credited {} for winning bet {}", amount, bet_id);
            }
            
            BettingMessage::RefundBet { amount, tournament_id, bet_id, match_id } => {
                info!("[UserXFighter] Processing REFUND: {}", bet_id);
                
                if self.state.processed_bets.get(&bet_id).await.unwrap_or(None).is_some() {
                    info!("Refund for bet {} already processed", bet_id);
                    return;
                }

                let current_balance = *self.state.balance.get();
                self.state.balance.set(current_balance + amount);
                
                let tx = Transaction {
                    tx_id: format!("refund_{}", bet_id),
                    tx_type: "refund".to_string(),
                    amount,
                    timestamp,
                    related_id: Some(format!("{}_{}", tournament_id, match_id)),
                    status: "completed".to_string(),
                };
                self.state.transactions.insert(&tx.tx_id.clone(), tx)
                    .expect("Failed to insert transaction");
                
                self.state.processed_bets.insert(&bet_id, true)
                    .expect("Failed to mark refund as processed");
                
                info!("[UserXFighter] Refunded {} for bet {}", amount, bet_id);
            }
        }
        
        if let Err(e) = self.state.save().await {
            error!("[UserXFighter] Failed to save state after message: {:?}", e);
        }
    }
}