// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// friend/src/lib.rs
use linera_sdk::linera_base_types::{ChainId, ContractAbi, ServiceAbi};
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Deserialize, Serialize)]
pub enum Operation {
    /// Gửi yêu cầu kết bạn đến user khác
    SendFriendRequest {
        to_user_chain: ChainId,
    },
    /// Chấp nhận yêu cầu kết bạn
    AcceptFriendRequest {
        request_id: String,
    },
    /// Từ chối yêu cầu kết bạn  
    RejectFriendRequest {
        request_id: String,
    },
    /// Xóa bạn bè
    RemoveFriend {
        friend_chain_id: ChainId,
    },
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub enum Message {
    /// Yêu cầu kết bạn từ user khác
    FriendRequest {
        request_id: String,
        from_chain: ChainId,
    },
    /// Phản hồi chấp nhận kết bạn
    FriendRequestAccepted {
        request_id: String,
        from_chain: ChainId, 
    },
    /// Phản hồi từ chối kết bạn
    FriendRequestRejected {
        request_id: String,
        from_chain: ChainId,
    },
    /// Thông báo xóa bạn bè
    FriendRemoved {
        friend_chain_id: ChainId,
    },
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct Parameters {
    //max_friends, TO_DO....
}

pub struct FriendAbi;

impl ContractAbi for FriendAbi {
    type Operation = Operation;
    type Response = ();
}

impl ServiceAbi for FriendAbi {
    type Query = async_graphql::Request;
    type QueryResponse = async_graphql::Response;
}