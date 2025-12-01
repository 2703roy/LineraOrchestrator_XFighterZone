// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0

use linera_sdk::views::{linera_views, MapView, RootView, SetView, ViewStorageContext, RegisterView};
use linera_sdk::linera_base_types::ChainId;
use serde::{Deserialize, Serialize};

#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct FriendRequest {
    pub request_id: String,
    pub from_chain: ChainId,
    pub timestamp: u64,
    pub status: String, // "pending", "accepted", "rejected"
}

#[derive(RootView)]
#[view(context = ViewStorageContext)]
pub struct FriendState {
    /// Danh sách bạn bè (chain IDs)
    pub friends: SetView<ChainId>,
	
    /// Yêu cầu kết bạn đang chờ xử lý (từ user khác)
    pub pending_incoming_requests: MapView<String, FriendRequest>,
    
    /// Yêu cầu kết bạn đã gửi đi đang chờ phản hồi
    pub pending_outgoing_requests: MapView<String, ChainId>,
    
    /// Bộ đếm tạo request_id unique
    pub request_counter: RegisterView<u64>,
}