// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0  
// userxfighter/src/service.rs

#![cfg_attr(target_arch = "wasm32", no_main)]

mod state;

use std::sync::Arc;
use async_graphql::{EmptySubscription, Object, Request, Response, Schema, SimpleObject};
use linera_sdk::{abi::WithServiceAbi, views::View, Service, ServiceRuntime};

use userxfighter::{Operation, UserXfighterAbi};
use self::state::UserXfighterState;

linera_sdk::service!(UserXfighterService);

#[derive(SimpleObject)]
pub struct TransactionView {
    pub tx_id: String,
    pub tx_type: String,
    pub amount: u64,
    pub timestamp: u64,
    pub related_id: Option<String>,
    pub status: String,
}

pub struct UserXfighterService {
    state: Arc<UserXfighterState>,
    runtime: Arc<ServiceRuntime<Self>>,
}

impl WithServiceAbi for UserXfighterService {
    type Abi = UserXfighterAbi;
}

impl Service for UserXfighterService {
    type Parameters = ();

    async fn new(runtime: ServiceRuntime<Self>) -> Self {
        let state = UserXfighterState::load(runtime.root_view_storage_context())
            .await
            .expect("Failed to load state");
        UserXfighterService {
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
        ).finish();
        schema.execute(request).await
    }
}

struct QueryRoot {
    state: Arc<UserXfighterState>,
}

#[Object]
impl QueryRoot {
    async fn transactions(&self) -> Vec<TransactionView> {
        let mut txs = Vec::new();
        if let Ok(ids) = self.state.transactions.indices().await {
            for id in ids {
                if let Ok(Some(tx)) = self.state.transactions.get(&id).await {
                    txs.push(TransactionView {
                        tx_id: tx.tx_id,
                        tx_type: tx.tx_type,
                        amount: tx.amount,
                        timestamp: tx.timestamp,
                        related_id: tx.related_id,
                        status: tx.status,
                    });
                }
            }
        }
        txs
    }
}

struct MutationRoot {
    runtime: Arc<ServiceRuntime<UserXfighterService>>,
}

#[Object]
impl MutationRoot {
    async fn place_bet(&self, match_id: String, player: String, amount: u64) -> bool {
        let op = Operation::PlaceBet {
				match_id,
				player,
				amount 
		};
        self.runtime.schedule_operation(&op);
        true
    }

	// Friend mutations
    async fn send_friend_request(&self, to_user_chain: String) -> bool {
        match to_user_chain.parse() {
            Ok(chain_id) => {
                let op = Operation::SendFriendRequest { 
                    to_user_chain: chain_id,
                };
                self.runtime.schedule_operation(&op);
                true
            }
            Err(_) => false,
        }
    }
    
	/// Accept friend service
    async fn accept_friend_request(&self, request_id: String) -> bool {
        let op = Operation::AcceptFriendRequest { request_id };
        self.runtime.schedule_operation(&op);
        true
    }
    
	/// Từ chối yêu cầu kết bạn 
    async fn reject_friend_request(&self, request_id: String) -> bool {
        let op = Operation::RejectFriendRequest { request_id };
        self.runtime.schedule_operation(&op);
        true
    }
    
	/// Xóa bạn bè 
    async fn remove_friend(&self, friend_chain: String) -> bool {
        match friend_chain.parse() {
            Ok(chain_id) => {
                let op = Operation::RemoveFriend { friend_chain_id: chain_id };
                self.runtime.schedule_operation(&op);
                true
            }
            Err(_) => false,
        }
    }
}