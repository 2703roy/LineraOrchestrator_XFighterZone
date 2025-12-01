// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// xfighter service.rs

#![cfg_attr(target_arch = "wasm32", no_main)]

mod state;

use log::info;
use std::sync::Arc;
use async_graphql::{EmptySubscription, SimpleObject, Object, Request, Response, Schema};
use linera_sdk::{linera_base_types::WithServiceAbi, views::View, Service, ServiceRuntime};

use xfighter::{MatchResultInput, Operation, XfighterAbi, FactoryOperation};
use xfighter::MatchResult;
use self::state::XfighterState; 

linera_sdk::service!(XfighterService);

pub struct XfighterService {
    state: Arc<XfighterState>,
    runtime: Arc<ServiceRuntime<Self>>,
}

impl WithServiceAbi for XfighterService {
    type Abi = XfighterAbi;
}

impl Service for XfighterService {
    type Parameters = ();

    async fn new(runtime: ServiceRuntime<Self>) -> Self {
        let state = XfighterState::load(runtime.root_view_storage_context())
            .await
            .expect("Failed to load state");
         XfighterService {
            state: Arc::new(state),
            runtime: Arc::new(runtime),
        }
    }

    async fn handle_query(&self, request: Request) -> Response {
        let schema = Schema::build(
            QueryRoot { runtime: self.runtime.clone(), state: self.state.clone() },
            MutationRoot { runtime: self.runtime.clone()},
            EmptySubscription,
        ).finish();
        schema.execute(request).await
    }
}

struct MutationRoot {
    runtime: Arc<ServiceRuntime<XfighterService>>,
}


#[Object]
impl MutationRoot {
    #[graphql(name = "openAndCreate")]
    async fn open_and_create(&self) -> bool {
        info!("open_and_create() called from Orchestrator");
    	let op = Operation::Factory(FactoryOperation::OpenAndCreate);
    	self.runtime.schedule_operation(&op);
    	true
    }

    /// GraphQL mutation recordScore(matchResult) = client Unity.
    async fn record_score(
        &self,
         // Đặt tên tham số đúng `matchResult` để khớp payload client gửi
        #[graphql(name = "matchResult")] match_result: MatchResultInput,
    ) -> bool {
        let op = Operation::RecordScore(match_result);
        self.runtime.schedule_operation(&op); // ServiceRuntime sẽ đóng gói Operation và gửi sang contract (BCS tự động).
        true
    }	
}

struct QueryRoot {
	#[allow(dead_code)]
    runtime: Arc<ServiceRuntime<XfighterService>>,
	state: Arc<XfighterState>,
}

#[derive(SimpleObject)]
struct ChildAppInfo {
    chain_id: String,
    app_id: String,
}

#[derive(SimpleObject)]
struct MatchResultWithChain {
    chain_id: String,
    match_result: MatchResult,
}

#[Object]
impl QueryRoot {
    async fn all_match_results(&self) -> Vec<MatchResult> {
        let mut results = Vec::new();
        let ids = self.state.match_results.indices().await.unwrap_or_default();
        for id in ids {
            if let Some(m) = self.state.match_results.get(&id).await.ok().flatten() {
                results.push(m);
            }
        }
        results
    }
	
	/// Get match history for a specific user
	async fn match_history_by_user(&self, username: String) -> Vec<MatchResultWithChain> {
		let mut user_matches = Vec::new();
		
		let match_ids = self.state.match_results.indices().await.unwrap_or_default();
		
		for entry_chain_id in match_ids {
			if let Some(match_result) = self.state.match_results.get(&entry_chain_id).await.ok().flatten() {
				if match_result.player1_username == username || match_result.player2_username == username {
					// Return chain_id và match_result
					user_matches.push(MatchResultWithChain {
						chain_id: entry_chain_id.clone(),
						match_result,
					});
				}
			}
		}
		
		user_matches.sort_by(|a, b| b.match_result.timestamp.cmp(&a.match_result.timestamp));
		user_matches
	}
	
	/// Get all new chain
    async fn all_opened_chains(&self) -> Vec<String> {
        let mut chains = Vec::new();
        let ids = self.state.opened_chains.indices().await.unwrap_or_default();
        for chain_id in ids {
            chains.push(chain_id.to_string());
        }
        chains
    }

	///Get all new chill app
    async fn all_child_apps(&self) -> Vec<ChildAppInfo> {
        let mut pairs = Vec::new();
        let ids = self.state.child_apps.indices().await.unwrap_or_default();
        for chain_id in ids {
            if let Some(app_id) = self.state.child_apps.get(&chain_id).await.ok().flatten() {
                let hash_bytes: [u8; 32] = app_id.application_description_hash.into();
		pairs.push(ChildAppInfo {
                    chain_id: chain_id.to_string(),
                    app_id: hex::encode(hash_bytes),
                });
            }
        }
        pairs
    }
}
