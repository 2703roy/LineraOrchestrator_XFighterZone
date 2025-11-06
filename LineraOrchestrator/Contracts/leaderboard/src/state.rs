// leaderboard/src/state.rs
// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0

use linera_sdk::views::{linera_views, MapView, RootView, ViewStorageContext};

/// Định nghĩa trạng thái của hợp đồng Leaderboard.
#[derive(RootView)]
#[view(context = ViewStorageContext)]
pub struct LeaderboardState {
    pub total_wins: MapView<String, u64>, // Lưu trữ tổng số trận thắng của mỗi người chơi.
    pub total_losses: MapView<String, u64>,  // Lưu trữ tổng số trận thua của mỗi người chơi.
    pub total_matches: MapView<String, u64>,  // Lưu trữ tổng số trận đấu của mỗi người chơi.
    pub scores: MapView<String, u64>, // Lưu trữ điểm số chính của người chơi (thắng - thua).
    pub processed_match_ids: MapView<String, bool>, // Lưu trữ các ID trận đấu đã được xử lý để tránh trùng lặp.
}
