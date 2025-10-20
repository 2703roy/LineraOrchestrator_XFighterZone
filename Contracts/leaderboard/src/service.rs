// leaderboard/src/service.rs
// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0

#![cfg_attr(target_arch = "wasm32", no_main)]

mod state;

use std::sync::Arc;
use async_graphql::{EmptySubscription, Object, Request, Response, Schema};
use linera_sdk::{
    abi::WithServiceAbi,
    views::View,
    Service, ServiceRuntime,
    bcs,
};
use leaderboard::{LeaderboardAbi, LeaderboardEntry, Operation};
use self::state::LeaderboardState;
use std::collections::HashSet;

// Dữ liệu sẽ được truyền vào `Schema` để thực hiện truy vấn.
pub struct LeaderboardService {
    state: Arc<LeaderboardState>,
    runtime: Arc<ServiceRuntime<Self>>,
}

linera_sdk::service!(LeaderboardService);

impl WithServiceAbi for LeaderboardService {
    type Abi = LeaderboardAbi;
}

impl Service for LeaderboardService {
    type Parameters = ();

    async fn new(runtime: ServiceRuntime<Self>) -> Self {
        let state = LeaderboardState::load(runtime.root_view_storage_context())
            .await
            .expect("Không thể tải trạng thái");
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
    /// Ghi điểm chính thức
    async fn record_score(&self, user_id: String, is_winner: bool, match_id: String) -> bool {
        // Tạo enum Operation rồi để runtime tự BCS-serialize (không tự serialize thành Vec<u8>)
        let op = Operation::RecordScore { user_id, is_winner, match_id };
        self.runtime.schedule_operation(&op);
        true
    }

    /// Debug: Trả về hex bytes của Operation theo BCS (không gửi)
    async fn debug_operation(&self, user_id: String, is_winner: bool, match_id: String) -> String {
        let op = Operation::RecordScore { user_id, is_winner, match_id };
        let bytes = bcs::to_bytes(&op).expect("Cannot serialize Operation to BCS");
        bytes.iter().map(|b| format!("{:02x}", b)).collect::<Vec<_>>().join("")
    }
}

struct QueryRoot {
    state: Arc<LeaderboardState>,
}

#[Object]
impl QueryRoot {
    // Đã thay đổi kiểu trả về thành u64
    async fn score(&self, user_id: String) -> Option<u64> {
        self.state.scores.get(&user_id).await.ok().flatten()
    }

    async fn leaderboard(&self, limit: Option<u64>) -> Vec<LeaderboardEntry> {
        let mut entries = Vec::new();

        // Thu thập tất cả các ID từ các map
        let mut user_ids = self.state.scores.indices().await.unwrap_or_default().into_iter().collect::<HashSet<_>>();
        let wins_ids = self.state.total_wins.indices().await.unwrap_or_default();
        let losses_ids = self.state.total_losses.indices().await.unwrap_or_default();
        let matches_ids = self.state.total_matches.indices().await.unwrap_or_default();
        
        user_ids.extend(wins_ids.into_iter());
        user_ids.extend(losses_ids.into_iter());
        user_ids.extend(matches_ids.into_iter());

        for user_id in user_ids {
            // Đã sửa lỗi: Dùng 0 thay vì 0.0 để khớp kiểu dữ liệu u64
            let score = self.state.scores.get(&user_id).await.ok().flatten().unwrap_or(0);
            let total_matches = self.state.total_matches.get(&user_id).await.ok().flatten().unwrap_or_default();
            let total_wins = self.state.total_wins.get(&user_id).await.ok().flatten().unwrap_or_default();
            let total_losses = self.state.total_losses.get(&user_id).await.ok().flatten().unwrap_or_default();
            
            entries.push(LeaderboardEntry { user_id, score, total_matches, total_wins, total_losses });
        }

        // Đã sửa lỗi: Sắp xếp bằng cmp thay vì partial_cmp vì score là u64
        entries.sort_by(|a, b| b.score.cmp(&a.score));

        let limit = limit.unwrap_or(entries.len() as u64) as usize;
        entries.into_iter().take(limit).collect()
    }
}
