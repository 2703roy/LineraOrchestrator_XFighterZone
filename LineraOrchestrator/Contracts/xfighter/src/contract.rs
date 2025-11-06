// xfighter/src/contract.rs
// Copyright (c) Zefchain Labs, Inc.
// SPDX-License-Identifier: Apache-2.0

#![cfg_attr(target_arch = "wasm32", no_main)]

mod state;
use log::{debug, error, info};

use self::state::{MatchResult, XfighterState};
use linera_sdk::linera_base_types::{ApplicationPermissions, Amount, ChainId};
use linera_sdk::{abi::WithContractAbi, views::{RootView, View}, Contract, ContractRuntime};

use xfighter::{Operation, XfighterAbi, FactoryOperation};
use leaderboard::Operation as LeaderboardOperation;
use leaderboard::LeaderboardAbi;
use leaderboard::RecordScoreMessage;

use xfighter::Parameters;

linera_sdk::contract!(XfighterContract);

pub struct XfighterContract {
    state: XfighterState,
    runtime: ContractRuntime<Self>,
    // Optional: store OpenAndCreate info to log once at finalization
    pending_open_and_create: Option<(String, String, String, String)>,
    // Volatile per-transaction outbound messages to send once on store()
    pending_outbound: Vec<(ChainId, RecordScoreMessage)>,
}

impl WithContractAbi for XfighterContract {
    type Abi = XfighterAbi;
}

impl Contract for XfighterContract {
    type Message = RecordScoreMessage;
    type InstantiationArgument = ();
    type Parameters = Parameters;
    type EventValue = ChainId;

    async fn load(runtime: ContractRuntime<Self>) -> Self {
        let state = XfighterState::load(runtime.root_view_storage_context())
            .await
            .expect("Failed to load state");
			//store() được gọi cuối cùng để persist state / side-effects call last
        XfighterContract {
            state, runtime, pending_open_and_create: None, pending_outbound: Vec::new(),
        }
    }

    async fn instantiate(&mut self, _argument: ()) {
        // leaderboard_id is in Parameters; nothing to do here.
    }

    /// Finalize transaction: persist state and perform final side-effects (send outbound messages once).
    async fn store(mut self) {
        // Persist state
        self.state.save().await.expect("Failed to save state");

        // Single final log for OpenAndCreate if present
        if let Some((chain, app, module, leaderboard)) = self.pending_open_and_create.take() {
            info!(
                "[XFighter] OpenAndCreate completed: chain={} app={} module={} leaderboard={}",
                chain, app, module, leaderboard
            );
        }

        // Drain and send pending outbound messages once at finalization.
        // This guarantees cross-chain sends happen once per successful transaction.
        for (dest_chain, msg) in self.pending_outbound.drain(..) {
            // prepare_message consumes the message
            self.runtime.prepare_message(msg).send_to(dest_chain);
            debug!("[XFighter] Sent deferred outbound message to chain={:?}", dest_chain);
        }
    }

    /// Xử lý Operation (Service / Orchestrator)
    async fn execute_operation(&mut self, operation: Self::Operation) {
        match operation {
            // ================= Factory Open Create flow =================
            Operation::Factory(factory_op) => match factory_op {
                FactoryOperation::OpenAndCreate => {
                    // 0. Signer / create basic settings
                    let ownership = self.runtime.chain_ownership();
                    let permissions = ApplicationPermissions::default();
                    let balance = Amount::from_tokens(1);

                    // 1. open a new chain
                    let new_chain_id = self.runtime.open_chain(ownership, permissions, balance);
                    debug!("[XFighter] open_chain requested (returned): {:?}", new_chain_id);

                    // 2. lấy Parameters từ runtime (module id + leaderboard id)
                    let params: Parameters = self.runtime.application_parameters();
                    let module_id = params.xfighter_module.clone();
                    let leaderboard_id = params.leaderboard_id;
					debug!("[XFighter] params: {:?}", params);
					debug!("[XFighter] module_id: {:?}", module_id);
					
                    // 3. build parameters cho app con (module id + leaderboard id)
                    let child_params = Parameters {
                        xfighter_module: module_id.clone(),
                        leaderboard_id,
                    };

                    // 4. Auto-instantiate app con trên chain mới
                    let new_app_id = self.runtime.create_application::<xfighter::XfighterAbi, xfighter::Parameters, ()>(
                        module_id.clone(),
                        &child_params,
                        &(),
                        vec![],
                    );

                    // 5. lưu lại mapping chain/app
                    if let Err(e) = self.state.opened_chains.insert(&new_chain_id) {
                        error!("Failed to insert new_chain_id: {:?}", e);
                    }
                    if let Err(e) = self.state.child_apps.insert(&new_chain_id, new_app_id.clone()) {
                        error!("Failed to insert new_app_id: {:?}", e);
                    }

                    // Store pending info to log once at store()
                    self.pending_open_and_create = Some((
                        format!("{:?}", new_chain_id),
                        format!("{:?}", new_app_id),
                        format!("{:?}", module_id),
                        format!("{:?}", params),
                    ));
                }
            },

            // ================= RecordScore flow =================
            Operation::RecordScore(input) => {
                // Normalize match_id and chain key
                let match_id = input.match_id.clone();
                let chain_id_str = self.runtime.chain_id().to_string();

                // If already recorded for this chain, skip
                if self
                    .state
                    .match_results
                    .contains_key(&chain_id_str)
                    .await
                    .expect("Failed to check if match result exists")
                {
                    info!("[XFighter] Match already recorded for chain={}, skipping.", chain_id_str);
                    return;
                }

                // Validate winner_username is one of players
                if input.winner_username != input.player1_username && input.winner_username != input.player2_username {
                    info!("[XFighter] Invalid winner username for match_id={}, skipping.", match_id);
                    return;
                }

                // 1) Persist match result in state (under chain key)
                let match_result_data = MatchResult {
                    match_id: match_id.clone(),
                    player1_username: input.player1_username.clone(),
                    player2_username: input.player2_username.clone(),
                    winner_username: input.winner_username.clone(),
                    loser_username: input.loser_username.clone(),
                    duration_seconds: input.duration_seconds,
                    timestamp: input.timestamp,
                    player1_score: input.player1_score,
                    player2_score: input.player2_score,
                    map_name: input.map_name.clone(),
                    match_type: input.match_type.clone(),
					afk: input.afk.clone(),
                };

                self.state
                    .match_results
                    .insert(&chain_id_str, match_result_data)
                    .expect("Failed to insert match result");

                // 2) Prepare idempotency composite key "<chainId>:<matchId>"
                let key = chain_id_str.clone();

                // if already marked as sent, skip enqueue
                if self.state.sent_messages.get(&key).await.ok().flatten().unwrap_or(false) {
                    debug!("[XFighter] Outbound messages already marked sent for key={}, skipping.", key);
                    return;
                }

                // Mark sent in state (persist flag) to prevent duplicate sends across re-exec / retries
                self.state
                    .sent_messages
                    .insert(&key, true)
                    .expect("Failed to insert sent_messages flag");

                // Target info
                let params: Parameters = self.runtime.application_parameters();
                let lb_id = params.leaderboard_id;
                let publisher_chain_id = self.runtime.application_creator_chain_id();

                // ENQUEUE outbound messages (do not send now — we'll send in store())
                debug!(
                    "[XFighter] Queued RecordScore messages (deferred) to publisher_chain_id={:?} key={}",
                    publisher_chain_id, key
                );

                // Winner
                self.pending_outbound.push((
                    publisher_chain_id,
                    RecordScoreMessage {
                        user_id: input.winner_username.clone(),
                        is_winner: true,
                        match_id: match_id.clone(),
                    },
                ));

                // Loser
                let loser_username = if input.winner_username == input.player1_username {
                    input.player2_username.clone()
                } else {
                    input.player1_username.clone()
                };
                self.pending_outbound.push((
                    publisher_chain_id,
                    RecordScoreMessage {
                        user_id: loser_username.clone(),
                        is_winner: false,
                        match_id: match_id.clone(),
                    },
                ));

                info!(
                    "[XFighter] Enqueued RecordScore messages for leaderboard_app={:?}, key={}, winner={}, loser={}, match_id={}",
                    lb_id, key, input.winner_username, loser_username, match_id
                );
            }
        }
    }

    /// Cross-chain Message
    async fn execute_message(&mut self, message: Self::Message) {
        // Message delivered to Xfighter instance at publisher chain
        info!("[XFighter] execute_message received RecordScoreMessage user={} is_winner={} match_id={}",
            message.user_id, message.is_winner, message.match_id
        );

        // Get leaderboard app id from parameters
        let params: Parameters = self.runtime.application_parameters();
        let lb_id = params.leaderboard_id;

        // Call local (same-chain) leaderboard app
        let op = LeaderboardOperation::RecordScore {
            user_id: message.user_id.clone(),
            is_winner: message.is_winner,
            match_id: message.match_id.clone(),
        };

        // call_application returns a response;
        let _ = self.runtime.call_application::<LeaderboardAbi>(true, lb_id, &op);

        info!("[XFighter] Forwarded RecordScore to leaderboard app_id={:?} user={} match_id={}",
            lb_id, message.user_id, message.match_id
        );
    }
}
