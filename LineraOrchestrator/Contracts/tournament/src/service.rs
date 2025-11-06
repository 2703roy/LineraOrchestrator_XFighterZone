// tournament/src/service.rs
// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0

#![cfg_attr(target_arch = "wasm32", no_main)]

mod state;

use std::sync::Arc;
use async_graphql::{EmptySubscription, Object, Request, Response, Schema, SimpleObject};
use linera_sdk::{abi::WithServiceAbi, views::View, Service, ServiceRuntime};
use tournament::{TournamentAbi, Operation};
use self::state::TournamentState;

linera_sdk::service!(TournamentService);

#[derive(SimpleObject, Clone)]
struct MatchResultOutput {
    match_id: String,
    winner: String,
    loser: String,
}

// Dữ liệu sẽ được truyền vào `Schema` để thực hiện truy vấn.
pub struct TournamentService {
    state: Arc<TournamentState>,
    runtime: Arc<ServiceRuntime<Self>>,
}

impl WithServiceAbi for TournamentService {
    type Abi = TournamentAbi;
}

impl Service for TournamentService {
    type Parameters = ();

    async fn new(runtime: ServiceRuntime<Self>) -> Self {
        let state = TournamentState::load(runtime.root_view_storage_context())
            .await
            .expect("Không thể tải trạng thái");
        TournamentService {
            state: Arc::new(state),
            runtime: Arc::new(runtime),
        }
    }

    async fn handle_query(&self, request: Request) -> Response {
        let schema = Schema::build(
            QueryRoot { state: self.state.clone() },
            MutationRoot { runtime: self.runtime.clone() },
            EmptySubscription,
        )
        .finish();
        schema.execute(request).await
    }
}

struct MutationRoot {
    runtime: Arc<ServiceRuntime<TournamentService>>,
}

#[Object]
impl MutationRoot {
    async fn create_tournament(&self, name: String, start_time: u64, end_time: u64) -> bool {
        let op = Operation::CreateTournament { name, start_time, end_time };
        // schedule_operation ở repo của bạn nhận tham chiếu và là synchronous
        self.runtime.schedule_operation(&op);
        true
    }

    async fn register(&self, player: String) -> bool {
        let op = Operation::Register { player };
        self.runtime.schedule_operation(&op);
        true
    }

    async fn record_match(&self, match_id: String, winner: String, loser: String) -> bool {
        let op = Operation::RecordMatch { match_id, winner, loser };
        self.runtime.schedule_operation(&op);
        true
    }

    async fn close_tournament(&self) -> bool {
        let op = Operation::CloseTournament;
        self.runtime.schedule_operation(&op);
        true
    }
}

struct QueryRoot {
    state: Arc<TournamentState>,
}

#[derive(SimpleObject, Clone)]
struct LeaderboardEntry {
    player: String,
    score: u64,
}

#[Object]
impl QueryRoot {
    async fn participants(&self) -> Vec<String> {
        self.state.participants.indices().await.unwrap_or_default()
    }
	
	 async fn tournament_name(&self) -> String {
        self.state.tournament_name.get().clone()
    }
	
    async fn status(&self) -> String {
        self.state.status.get().clone()
    }
	
	 async fn start_time(&self) -> u64 {
        *self.state.start_time.get()
    }

    async fn end_time(&self) -> u64 {
        *self.state.end_time.get()
    }

    async fn results(&self) -> Vec<MatchResultOutput> {
        let ids = self.state.results.indices().await.unwrap_or_default();
        let mut output = vec![];
        for id in ids {
            // Trong service context, MapView::get là async -> await và match Result<Option<_>>
            if let Ok(Some((winner, loser))) = self.state.results.get(&id).await {
                output.push(MatchResultOutput { match_id: id, winner, loser });
            }
        }
        output
    }
   
	 async fn champion(&self) -> Option<String> {
        let champ = self.state.champion.get();
        if champ.is_empty() {
            None
        } else {
            Some(champ.clone())
        }
    }
	// Debug top 2
	async fn runner_up(&self) -> Option<String> {
		let ru = self.state.runner_up.get();
		if ru.is_empty() {
			None
		} else {
			Some(ru.clone())
		}
	}
	   async fn tournament_leaderboard(&self) -> Vec<LeaderboardEntry> {
        let mut results = vec![];
        let keys = self.state.tournament_leaderboard.indices().await.unwrap_or_default();

        for k in keys {
            if let Ok(Some(score)) = self.state.tournament_leaderboard.get(&k).await {
                results.push(LeaderboardEntry { player: k, score });
            }
        }

        // sắp xếp theo điểm giảm dần
        results.sort_by(|a, b| b.score.cmp(&a.score));
        results
    }
	async fn onchain_op_id(&self) -> Option<String> {
        let id = self.state.opid.get();
        if id.is_empty() {
            None
        } else {
            Some(id.clone())
        }
    }
}
