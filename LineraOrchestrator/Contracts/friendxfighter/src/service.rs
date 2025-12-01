// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0
// friendxfighter/src/service.rs

#![cfg_attr(target_arch = "wasm32", no_main)]

mod state;

use std::sync::Arc;

use async_graphql::{EmptySubscription, Object, Request, Response, Schema, SimpleObject};
use linera_sdk::{abi::WithServiceAbi, views::View, Service, ServiceRuntime};
use friendxfighter::{Operation, FriendAbi};
use state::FriendState;

pub struct FriendService {
    state: Arc<FriendState>,
    runtime: Arc<ServiceRuntime<Self>>,
}

linera_sdk::service!(FriendService);

impl WithServiceAbi for FriendService {
    type Abi = FriendAbi;
}

impl Service for FriendService {
    type Parameters = ();

    async fn new(runtime: ServiceRuntime<Self>) -> Self {
        let state = FriendState::load(runtime.root_view_storage_context())
            .await
            .expect("Failed to load state");
        FriendService {
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
        )
        .finish();
        schema.execute(request).await
    }
}

// ==================== GRAPHQL TYPES ====================

#[derive(SimpleObject)]
pub struct FriendRequestView {
    pub request_id: String,
    pub from_chain: String,
    pub timestamp: u64,
    pub status: String,
}

#[derive(SimpleObject)] 
pub struct FriendView {
    pub chain_id: String,
}

// ==================== QUERIES ====================

struct QueryRoot {
    state: Arc<FriendState>,
    runtime: Arc<ServiceRuntime<FriendService>>,
}

#[Object]
impl QueryRoot {
    /// Lấy danh sách bạn bè
    async fn friends(&self) -> Vec<FriendView> {
        let mut friends = Vec::new();
		if let Ok(chain_ids) = self.state.friends.indices().await {
					for chain_id in chain_ids {
						friends.push(FriendView {
							chain_id: chain_id.to_string(),
				});      
            }
        }
        friends
    }
	
   /// Lấy danh sách yêu cầu kết bạn đang chờ xử lý (incoming)
	async fn pending_requests(&self) -> Vec<FriendRequestView> {
		let mut requests = Vec::new();
		
		if let Ok(request_ids) = self.state.pending_incoming_requests.indices().await {
			for request_id in request_ids {
				if let Ok(Some(request)) = self.state.pending_incoming_requests.get(&request_id).await {
					requests.push(FriendRequestView {
						request_id: request.request_id,
						from_chain: request.from_chain.to_string(),	
						timestamp: request.timestamp,
						status: request.status,
					});
				}
			}
		}
		requests
	}

    /// Lấy danh sách yêu cầu đã gửi đi đang chờ phản hồi (outgoing)
    async fn sent_requests(&self) -> Vec<FriendRequestView> {
        let mut requests = Vec::new();
        
        if let Ok(request_ids) = self.state.pending_outgoing_requests.indices().await {
            for request_id in request_ids {
                if let Ok(Some(_target_chain)) = self.state.pending_outgoing_requests.get(&request_id).await {
                    requests.push(FriendRequestView {
						request_id: request_id.clone(),
						from_chain: self.runtime.chain_id().to_string(),
						timestamp: 0,
						status: "pending".to_string(),
					});
                }
            }
        }
        requests
    }

    /// Kiểm tra xem có phải bạn bè với user khác không
    async fn is_friend(&self, chain_id: String) -> bool {
        match chain_id.parse() {
            Ok(chain_id) => {
                self.state.friends.contains(&chain_id).await.unwrap_or(false)
            }
            Err(_) => false,
        }
    }

    /// Đếm số lượng bạn bè
    async fn friends_count(&self) -> usize {
        self.state.friends.indices().await.unwrap_or_default().len()
    }

    /// Đếm số yêu cầu đang chờ xử lý (incoming)
    async fn pending_requests_count(&self) -> usize {
        self.state.pending_incoming_requests.indices().await.unwrap_or_default().len()
    }

    /// Đếm số yêu cầu đã gửi đi (outgoing)
    async fn sent_requests_count(&self) -> usize {
        self.state.pending_outgoing_requests.indices().await.unwrap_or_default().len()
    }

    /// Lấy thông tin chi tiết của một yêu cầu kết bạn
    async fn friend_request(&self, request_id: String) -> Option<FriendRequestView> {
        if let Ok(Some(request)) = self.state.pending_incoming_requests.get(&request_id).await {
            Some(FriendRequestView {
                request_id: request.request_id,
                from_chain: request.from_chain.to_string(),
                timestamp: request.timestamp,
                status: request.status,
            })
        } else {
            None
        }
    }
}

// ==================== MUTATIONS ====================

struct MutationRoot {
    runtime: Arc<ServiceRuntime<FriendService>>,
}

#[Object] 
impl MutationRoot {
    /// Gửi yêu cầu kết bạn
    async fn send_friend_request(&self, to_user_chain: String) -> bool {
        match to_user_chain.parse() {
            Ok(chain_id) => {
                let operation = Operation::SendFriendRequest {
                    to_user_chain: chain_id,
                };
                self.runtime.schedule_operation(&operation);
                true
            }
            Err(_) => false,
        }
    }

    /// Chấp nhận yêu cầu kết bạn
    async fn accept_friend_request(&self, request_id: String) -> bool {
        let operation = Operation::AcceptFriendRequest { request_id };
        self.runtime.schedule_operation(&operation);
        true
    }

    /// Từ chối yêu cầu kết bạn  
    async fn reject_friend_request(&self, request_id: String) -> bool {
        let operation = Operation::RejectFriendRequest { request_id };
        self.runtime.schedule_operation(&operation);
        true
    }

    /// Xóa bạn bè
    async fn remove_friend(&self, friend_chain: String) -> bool {
        match friend_chain.parse() {
            Ok(chain_id) => {
                let operation = Operation::RemoveFriend { friend_chain_id: chain_id };
                self.runtime.schedule_operation(&operation);
                true
            }
            Err(_) => false,
        }
    }
}