// leaderboard/src/contract.rs
// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0

#![cfg_attr(target_arch = "wasm32", no_main)]

mod state;

use log::info;
use self::state::LeaderboardState;
use linera_sdk::{
    abi::WithContractAbi,
    views::{RootView, View},
    Contract, ContractRuntime,
};
use leaderboard::{LeaderboardAbi, Operation, RecordScoreMessage};

linera_sdk::contract!(LeaderboardContract);

pub struct LeaderboardContract {
    state: LeaderboardState,
    #[allow(dead_code)]
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
            .expect("Không thể tải trạng thái");
        Self { state, runtime }
    }

    async fn instantiate(&mut self, _argument: Self::InstantiationArgument) {
        // Khởi tạo trạng thái
    }

    async fn store(mut self) {
        self.state.save().await.expect("Không thể lưu trạng thái");
    }

    /// Xử lý operation từ service
    async fn execute_operation(&mut self, operation: Self::Operation) -> Self::Response {
        match operation {
            Operation::RecordScore { user_id, is_winner, match_id } => {
                // Luôn xử lý và ghi nhận kết quả.
                // Việc kiểm tra trùng lặp đã được xử lý ở layer cao hơn (xfighter)
                // đảm bảo mỗi trận đấu chỉ được gửi một lần.
		info!("[LEADERBOARD] Received Operation::RecordScore user={} is_winner={} match_id={}",user_id, is_winner, match_id);
                self.update_score_and_stats(user_id, is_winner, match_id).await;
            }
        }
    }

    /// Xử lý message từ các chain khác.
    async fn execute_message(&mut self, message: Self::Message) {
        let RecordScoreMessage { user_id, is_winner, match_id } = message;
        // Logic này được gọi từ XFighter, nơi đã có logic kiểm tra trùng lặp
        // cho cả trận đấu. Vì thế, không cần kiểm tra lại ở đây.
	info!("[LEADERBOARD] Received Message::RecordScore user={} is_winner={} match_id={}",user_id, is_winner, match_id);
        self.update_score_and_stats(user_id, is_winner, match_id).await;
    }
}

impl LeaderboardContract {
    /// Hàm xử lý logic cập nhật điểm số.
    /// Dùng chung cho cả Operation và Message.
    async fn update_score_and_stats(&mut self, user_id: String, is_winner: bool, match_id: String) {
        let mut current_wins = self.state.total_wins.get(&user_id).await.ok().flatten().unwrap_or_default();
        let mut current_losses = self.state.total_losses.get(&user_id).await.ok().flatten().unwrap_or_default();
        let mut current_matches = self.state.total_matches.get(&user_id).await.ok().flatten().unwrap_or_default();

        if is_winner {
            current_wins += 1;
        } else {
            current_losses += 1;
        }
        current_matches += 1;

        // Công thức mới: Điểm = Số trận thắng (Win = +1, Lose = 0)
        let new_score = current_wins;
        
        // Lưu các giá trị đã cập nhật
        self.state.total_wins.insert(&user_id, current_wins).expect("Lỗi lưu wins");
        self.state.total_losses.insert(&user_id, current_losses).expect("Lỗi lưu losses");
        self.state.total_matches.insert(&user_id, current_matches).expect("Lỗi lưu matches");
        self.state.scores.insert(&user_id, new_score).expect("Lỗi lưu score");
        self.state.processed_match_ids.insert(&match_id, true).expect("Lỗi lưu match_id");
    }
}
