// tournament/src/contract.rs
// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0

#![cfg_attr(target_arch = "wasm32", no_main)]

mod state;

use log::info;
use self::state::TournamentState;
use linera_sdk::{
    abi::WithContractAbi,
    views::{RootView, View},
    Contract, ContractRuntime,
};
use tournament::{TournamentAbi, Operation};

linera_sdk::contract!(TournamentContract);

pub struct TournamentContract {
    state: TournamentState,
    #[allow(dead_code)]
    runtime: ContractRuntime<Self>,
}

impl WithContractAbi for TournamentContract {
    type Abi = TournamentAbi;
}

impl Contract for TournamentContract {
    type Parameters = ();
    type InstantiationArgument = Option<Vec<String>>;// top8 players passed when creating the app
    type Message = (); // chưa dùng cross-chain
    type EventValue = ();

    async fn load(runtime: ContractRuntime<Self>) -> Self {
        let state = TournamentState::load(runtime.root_view_storage_context())
            .await
            .expect("Không thể tải trạng thái");
        Self { state, runtime }
    }

    /// instantiate: nhận top8 (Vec<String>) và lưu vào participants
    async fn instantiate(&mut self, argument: Self::InstantiationArgument) {
        // Set basic meta (you can change defaults)
        self.state.tournament_name.set("Tournament".to_string());
        self.state.start_time.set(0);
        self.state.end_time.set(0);
        self.state.status.set("Open".to_string());
		self.state.current_round.set("Quarterfinal".to_string());
        self.state.champion.set("".to_string());
		
		let player_count = argument.as_ref().map_or(0, |v| v.len());
        info!("Khởi tạo TournamentContract mới với top{} players", player_count);

        // Insert players from instantiation argument
        if let Some(players) = argument {
            for player in players {
                info!("Adding participant at instantiate: {}", player);
                self.state.participants.insert(&player, true)
                    .expect("Lỗi lưu participant");
            }
        } else {
            info!("TournamentContract khởi tạo mà không có danh sách người chơi (argument=null)");
        }

        self.state.save().await.expect("Không thể lưu trạng thái sau instantiate");
    }

    /// Xử lý operation từ service
    async fn execute_operation(&mut self, operation: Operation) {
        match operation {
            Operation::CreateTournament { name, start_time, end_time } => {
                info!("Tạo giải đấu: {}", name);
                self.state.tournament_name.set(name);
                self.state.start_time.set(start_time);
                self.state.end_time.set(end_time);
                self.state.status.set("Open".to_string());
				
				// Lấy operation_id từ runtime (đại diện cho transaction id trên chain)
				let chain_id = self.runtime.chain_id();
				let app_id = self.runtime.application_id();
				let opid = format!("{:?}-{:?}", chain_id, app_id);
				self.state.opid.set(opid);
            }
            Operation::Register { player } => {
                info!("Đăng ký người chơi: {}", player);
                // insert trả về Result<(), ViewError> (synchronous) => không .await
                self.state.participants.insert(&player, true).expect("Lỗi lưu người chơi");
            }
            Operation::RecordMatch { match_id, winner, loser } => {
				info!("Ghi kết quả trận {}: {} thắng {}", match_id, winner, loser);
				self.state.results.insert(&match_id, (winner.clone(), loser.clone()))
					.expect("Lỗi lưu kết quả");
					
				let win_score = self.state.tournament_leaderboard.get(&winner).await.ok().flatten().unwrap_or(0);
				self.state.tournament_leaderboard.insert(&winner, win_score + 1).expect("Lỗi cập nhật leaderboard");

				let lose_score = self.state.tournament_leaderboard.get(&loser).await.ok().flatten().unwrap_or(0);
				self.state.tournament_leaderboard.insert(&loser, lose_score).expect("Lỗi cập nhật leaderboard");

				if match_id == "F1" || match_id.starts_with('F') {
					self.state.champion.set(winner.clone());
					self.state.runner_up.set(loser.clone());
				}
			},
            Operation::CloseTournament => {
				info!("Đóng giải đấu, trạng thái: Finished");
				self.state.status.set("Finished".to_string());
			}
        }

        // save() trong contract là async -> dùng .await
        self.state.save().await.expect("Không lưu được trạng thái");
    }

    /// Hiện tại chưa dùng message cross-chain
    async fn execute_message(&mut self, _message: Self::Message) {
        // Không có xử lý
    }

    async fn store(mut self) {
        self.state.save().await.expect("Không thể lưu trạng thái");
    }
}
