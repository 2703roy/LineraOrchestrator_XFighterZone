// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// friendxfighter/src/contract.rs

#![cfg_attr(target_arch = "wasm32", no_main)]

mod state;

use linera_sdk::{
    linera_base_types::{ChainId, WithContractAbi},
    views::{RootView, View},
    Contract, ContractRuntime,
};
use log::info;
use friendxfighter::{Operation, Message, FriendAbi};
use state::{FriendState, FriendRequest};

pub struct FriendContract {
    state: FriendState,
    runtime: ContractRuntime<Self>,
}

linera_sdk::contract!(FriendContract);

impl WithContractAbi for FriendContract {
    type Abi = FriendAbi;
}

impl Contract for FriendContract {
    type Message = Message;
    type InstantiationArgument = ();
    type Parameters = ();
    type EventValue = ();

    async fn load(runtime: ContractRuntime<Self>) -> Self {
        let state = FriendState::load(runtime.root_view_storage_context())
            .await
            .expect("Failed to load state");
        FriendContract { state, runtime }
    }

    async fn instantiate(&mut self, _argument: ()) {
        self.runtime.application_parameters();
        info!("FriendXFighter contract initialized");
    }

    async fn execute_operation(&mut self, operation: Operation) -> Self::Response {
        match operation {
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

    async fn execute_message(&mut self, message: Message) {
        match message {
            Message::FriendRequest { request_id, from_chain } => {
                self.handle_friend_request(request_id, from_chain).await;
            }
            Message::FriendRequestAccepted { request_id, from_chain } => {
                self.handle_friend_request_accepted(request_id, from_chain).await;
            }
            Message::FriendRequestRejected { request_id, from_chain } => {
                self.handle_friend_request_rejected(request_id, from_chain).await;
            }
            Message::FriendRemoved { friend_chain_id } => {
                self.handle_friend_removed(friend_chain_id).await;
            }
        }
    }

    async fn store(mut self) {
        self.state.save().await.expect("Failed to save state");
    }
}

impl FriendContract {
    // ==================== FRIEND REQUEST ====================

    async fn send_friend_request(&mut self, to_user_chain: ChainId) {
        let current_chain = self.runtime.chain_id();
        
        if to_user_chain == current_chain {
            info!("[FRIEND] Cannot send friend request to own chain");
            return;
        }

        if self.state.friends.contains(&to_user_chain).await.unwrap_or(false) {
            info!("[FRIEND] Already friends with chain {}", to_user_chain);
            return;
        }

        if self.has_pending_request_to(&to_user_chain).await {
            info!("[FRIEND] Already have pending request to chain {}", to_user_chain);
            return;
        }

        let counter = self.state.request_counter.get();
        let request_id = format!("friend_req_{}_{}", current_chain, counter);
        self.state.request_counter.set(counter + 1);

        self.state.pending_outgoing_requests.insert(&request_id, to_user_chain)
            .expect("Failed to insert outgoing request");

        let message = Message::FriendRequest {
            request_id: request_id.clone(),
            from_chain: current_chain,
        };

        self.runtime
            .prepare_message(message)
            .with_authentication()
            .with_tracking()
            .send_to(to_user_chain);

        info!("[FRIEND] Sent friend request to chain {} with ID: {}", to_user_chain, request_id);
    }

    async fn handle_friend_request(&mut self, request_id: String, from_chain: ChainId) {
        let _current_chain = self.runtime.chain_id();
        
        if self.state.friends.contains(&from_chain).await.unwrap_or(false) {
            info!("[FRIEND] Already friends with chain {}, auto-accepting", from_chain);
            self.send_friend_request_accepted(request_id, from_chain).await;
            return;
        }

        if self.state.pending_incoming_requests.contains_key(&request_id).await.unwrap_or(false) {
            info!("[FRIEND] Friend request {} already exists", request_id);
            return;
        }

        let friend_request = FriendRequest {
            request_id: request_id.clone(),
            from_chain,
            timestamp: self.runtime.system_time().micros(),
            status: "pending".to_string(),
        };
        self.state.pending_incoming_requests.insert(&request_id, friend_request)
            .expect("Failed to insert incoming request");

        info!("[FRIEND] Received friend request {} from chain {}", request_id, from_chain);
    }

    // ==================== ACCEPT FRIEND REQUEST ====================

    async fn accept_friend_request(&mut self, request_id: String) {
        if let Ok(Some(request)) = self.state.pending_incoming_requests.get(&request_id).await {
            if request.status == "pending" {
                self.state.friends.insert(&request.from_chain)
					.expect("Failed to add friend");
                
                self.state.pending_incoming_requests.remove(&request_id)
                    .expect("Failed to remove incoming request");
                
                self.send_friend_request_accepted(request_id.clone(), request.from_chain).await;
                
                info!("[FRIEND] Accepted friend request {} from chain {}", request_id, request.from_chain);
            }
        } else {
            info!("[FRIEND] Friend request {} not found or already processed", request_id);
        }
    }

    async fn send_friend_request_accepted(&mut self, request_id: String, to_chain: ChainId) {
        let message = Message::FriendRequestAccepted {
				request_id: request_id.clone(),
				from_chain: self.runtime.chain_id(), // CHỈ CẦN CHAIN ID
			};

            self.runtime
                .prepare_message(message)
                .with_authentication()
                .with_tracking()
                .send_to(to_chain);

            info!("[FRIEND] Sent friend acceptance for request {} to chain {}", request_id, to_chain);
    }
	
	// ==================== HANDLE FRIEND REQUEST ACCEPTED ====================
	
    async fn handle_friend_request_accepted(&mut self, request_id: String, from_chain: ChainId) {
        if let Ok(Some(target_chain)) = self.state.pending_outgoing_requests.get(&request_id).await {
            if target_chain == from_chain {
                self.state.friends.insert(&from_chain)
                .expect("Failed to add friend");
                
                self.state.pending_outgoing_requests.remove(&request_id)
                    .expect("Failed to remove outgoing request");
                
                info!("[FRIEND] Friend request {} accepted by chain {}", request_id, from_chain);
            }
        } else {
            info!("[FRIEND] Outgoing request {} not found", request_id);
        }
    }

    // ==================== REJECT FRIEND REQUEST ====================

    async fn reject_friend_request(&mut self, request_id: String) {
        if let Ok(Some(request)) = self.state.pending_incoming_requests.get(&request_id).await {
            self.state.pending_incoming_requests.remove(&request_id)
                .expect("Failed to remove incoming request");
            
            let message = Message::FriendRequestRejected {
                request_id: request_id.clone(),
                from_chain: self.runtime.chain_id(),
            };

            self.runtime
                .prepare_message(message)
                .with_authentication()
                .with_tracking()
                .send_to(request.from_chain);

            info!("[FRIEND] Rejected friend request {} from chain {}", request_id, request.from_chain);
        } else {
            info!("[FRIEND] Friend request {} not found", request_id);
        }
    }

    async fn handle_friend_request_rejected(&mut self, request_id: String, from_chain: ChainId) {
        self.state.pending_outgoing_requests.remove(&request_id)
            .expect("Failed to remove outgoing request");
        info!("[FRIEND] Friend request {} rejected by chain {}", request_id, from_chain);
    }

    // ==================== REMOVE FRIEND ====================

    async fn remove_friend(&mut self, friend_chain_id: ChainId) {
        self.state.friends.remove(&friend_chain_id)
            .expect("Failed to remove friend");
        
        let message = Message::FriendRemoved {
            friend_chain_id: self.runtime.chain_id(),
        };

        self.runtime
            .prepare_message(message)
            .with_authentication()
            .with_tracking()
            .send_to(friend_chain_id);

        info!("[FRIEND] Removed friend with chain {}", friend_chain_id);
    }

    async fn handle_friend_removed(&mut self, friend_chain_id: ChainId) {
        self.state.friends.remove(&friend_chain_id)
            .expect("Failed to remove friend");
        info!("[FRIEND] Removed by friend with chain {}", friend_chain_id);
    }

    // ==================== HELPER METHODS ====================

    async fn has_pending_request_to(&self, target_chain: &ChainId) -> bool {
        let outgoing_requests = self.state.pending_outgoing_requests.indices().await
            .unwrap_or_default();
        
        for request_id in outgoing_requests {
            if let Ok(Some(chain)) = self.state.pending_outgoing_requests.get(&request_id).await {
                if &chain == target_chain {
                    return true;
                }
            }
        }
        false
    }
}