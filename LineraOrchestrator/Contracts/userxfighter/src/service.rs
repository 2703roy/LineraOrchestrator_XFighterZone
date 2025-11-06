// userxfighter/src/service.rs
// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0

#![cfg_attr(target_arch = "wasm32", no_main)]

mod state;

use std::sync::Arc;
use self::state::UserXfighterState;
use async_graphql::{EmptySubscription, Object, Request, Response, Schema, SimpleObject};
use linera_sdk::{abi::WithServiceAbi, views::View, Service, ServiceRuntime};

// Sửa import: dùng userxfighter::
use userxfighter::{Operation, UserXfighterAbi};

linera_sdk::service!(UserXfighterService);

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
                runtime: self.runtime.clone(),
            },
            MutationRoot { 
                runtime: self.runtime.clone(),
            },
            EmptySubscription,
        ).finish();
        schema.execute(request).await
    }
}

#[derive(SimpleObject)]
pub struct UserInfo {
    pub balance: u64,
    pub chain_id: String,
}

#[derive(SimpleObject)] 
pub struct TransactionView {
    pub tx_id: String,
    pub tx_type: String,
    pub amount: u64,
    pub timestamp: u64,
    pub related_id: Option<String>,
    pub status: String,
}

struct QueryRoot {
    state: Arc<UserXfighterState>,
    runtime: Arc<ServiceRuntime<UserXfighterService>>,
}

#[Object]
impl QueryRoot {
    async fn user_info(&self) -> UserInfo {
        UserInfo {
            balance: *self.state.balance.get(),
            chain_id: self.runtime.chain_id().to_string(),
        }
    }

    async fn transactions(&self) -> Vec<TransactionView> {
        let mut txs = Vec::new();
        let ids = self.state.transactions.indices().await.unwrap_or_default();
        
        for id in ids {
            if let Some(tx) = self.state.transactions.get(&id).await.ok().flatten() {
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
        txs
    }

    async fn tournament_app_id(&self) -> Option<String> {
        self.state.tournament_app_id.get().clone()
    }
		
	 async fn get_user_chain(&self, user_id: String) -> Option<String> {
        self.state.user_chains.get(&user_id).await
            .ok()
            .flatten()
            .map(|chain_id| chain_id.to_string())
    }
}

struct MutationRoot {
    runtime: Arc<ServiceRuntime<UserXfighterService>>,
}

#[Object]
impl MutationRoot {
    async fn deposit(&self, amount: u64) -> bool {
        let op = Operation::Deposit { amount };
        self.runtime.schedule_operation(&op);
        true
    }

    async fn withdraw(&self, amount: u64) -> bool {
        let op = Operation::Withdraw { amount };
        self.runtime.schedule_operation(&op);
        true
    }
}