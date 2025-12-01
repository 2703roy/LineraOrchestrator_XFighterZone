## Quick Test Commands Linera Orchestrator
- Local: `http://localhost:5290`
- Live: `https://api.xfighterzone.com`

🟦 1. NODE SETUP
```bash
# Start Linera node
curl -sS -X POST http://localhost:5290/linera/start-linera-node | jq .
# Check configuration
curl -sS -X GET http://localhost:5290/linera/linera-config | jq .
# Check service status
curl -sS -X GET http://localhost:5290/linera/linera-service-status | jq .
```

🟩 2. MATCH OPERATIONS
```bash
🔹 Open & Create Match Chain
curl -sS -X POST http://localhost:5290/linera/open-and-create \
  -H "Content-Type: application/json" \
  -d '{
    "test1": "test1",
    "test2": "test2",
    "matchType": "rank"
  }' | jq

🔹 Submit Match Result
curl -sS -X POST http://localhost:5290/linera/submit-match-result \
  -H "Content-Type: application/json" \
  -d '{
    "chainId": "CHAIN_ID_HERE",
    "appId": "APP_ID_HERE",
    "matchResult": {
      "matchId": "match-test",
      "player1Username": "test1",
      "player2Username": "test2",
      "player1Userchain": "USER_CHAIN_1",
      "player2Userchain": "USER_CHAIN_2",
      "player1Heroname": "Doom",
      "player2Heroname": "Kelsey",
      "winnerUsername": "test1",
      "loserUsername": "test2",
      "durationSeconds": 15,
      "mapName": "arena02",
      "matchType": "Rank"
    }
  }' | jq
```

🟨 3. SEASON / LEADERBOARD
```bash
⭐ Current season info
curl -sS -X GET http://localhost:5290/linera/get-current-season-info | jq .

⭐ Current leaderboard
curl -sS -X GET http://localhost:5290/linera/get-leaderboard-data | jq .

⭐ Get past season info
curl -sS -X GET "http://localhost:5290/linera/get-season-info?seasonNumber=0" | jq .

⭐ Leaderboard for specific season
curl -sS -X GET "http://localhost:5290/linera/get-season-leaderboard?seasonNumber=2" | jq .

⭐ Start & End leaderboard
curl -sS -X POST "http://localhost:5290/linera/leaderboard/start?name=Leaderboard_Season_2" | jq
curl -sS -X POST "http://localhost:5290/linera/leaderboard/end" | jq .
```

🟧 4. USER OPERATIONS
```bash
🔸 User List
curl -s -X GET http://localhost:5290/linera/user/list | jq .

🔸 Auth user
curl -s -X POST "http://localhost:5290/linera/user/auth" \
  -H "Content-Type: application/json" \
  -d '{"userName": "test5"}' | jq .

🔸 Auto register 8 users
for i in {1..8}; do
  curl -s -X POST "http://localhost:5290/linera/user/auth" \
    -H "Content-Type: application/json" \
    -d "{\"userName\": \"test$i\"}" | jq .
  sleep 15
done

🔸 Check / Start / Stop user service
curl -s -X GET http://localhost:5290/linera/user/check-service/test2 | jq .
curl -s -X POST http://localhost:5290/linera/user/start-service/test2 | jq .
curl -s -X POST http://localhost:5290/linera/user/stop-service/test5 | jq .

🔸 User global stats
curl -sS -X GET "http://localhost:5290/linera/get-user-global-stats?userName=test1" | jq

🔸 User match history
curl -sS -X GET "http://localhost:5290/linera/get-user-match-history?userName=test1" | jq

🔸 UserChain match history
curl -sS -X GET "http://localhost:5290/linera/get-userchain-match-history?userChain=USER_CHAIN" | jq

🔸 MatchChain match history
curl -sS -X GET "http://localhost:5290/linera/get-chainid-match-history?chainId=MATCH_CHAIN" | jq
```

🟥 5. TOURNAMENT MANAGEMENT
```bash
🔸 Current tournament leaderboard
curl -s -X GET http://localhost:5290/linera/tournament/leaderboard | jq .

🔸 Current tournament season info
curl -s -X GET http://localhost:5290/linera/tournament/season-info | jq .

🔸 Tournament participants
curl -s -X GET http://localhost:5290/linera/tournament/participants | jq .

🔸 Match list
curl -s -X GET http://localhost:5290/linera/tournament/match-list | jq .

🔸 Set participants
curl -s -X POST http://localhost:5290/linera/tournament/set-participants \
  -H "Content-Type: application/json" \
  -d '{
    "participants": ["test1","test2","test3","test4","test5","test6","test7","test8"]
  }' | jq

🔸 Start tournament
curl -s -X POST "http://localhost:5290/linera/tournament/start?name=Tournament_Season1" | jq

🔸 Init bracket
curl -s -X POST http://localhost:5290/linera/tournament/init | jq .

🔸 Submit match
curl -s -X POST http://localhost:5290/linera/tournament/submit-match \
  -H "Content-Type: application/json" \
  -d '{"matchId": "QF1", "winnerUsername": "test1", "loserUsername": "test2"}' | jq .

🔸 Advance bracket
curl -s -X POST http://localhost:5290/linera/tournament/advance-bracket | jq

🔸 End tournament
curl -s -X POST http://localhost:5290/linera/tournament/end | jq .

🔸 Get past tournament info
curl -sS -X GET "http://localhost:5290/linera/get-tournament-info?tournamentNumber=2" | jq .
curl -sS -X GET "http://localhost:5290/linera/get-tournament-leaderboard?tournamentNumber=2" | jq .
```

🟫 6. BETTING SYSTEM
```bash
🔸 Transfer tokens
curl -s -X POST http://localhost:5290/linera/user/transfer-tokens \
  -H "Content-Type: application/json" \
  -d '{"userName":"test1","amount":50000}' | jq

🔸 Check balances
curl -s -X GET http://localhost:5290/linera/user/test1/balance | jq
curl -s -X GET http://localhost:5290/linera/publisher/balance | jq

🔸 Bet metadata
curl -s -X POST http://localhost:5290/linera/tournament/set-match-metadata \
  -H "Content-Type: application/json" \
  -d '{"matchId":"BET5"}' | jq

🔸 Place bet
curl -s -X POST http://localhost:5290/linera/user/place-bet \
  -H "Content-Type: application/json" \
  -d '{"userName":"test1","matchId":"BET5","player":"A","amount":3000}' | jq

🔸 Settle bet
curl -s -X POST http://localhost:5290/linera/tournament/settle \
  -H "Content-Type: application/json" \
  -d '{"matchId":"BET2","winner":"A"}' | jq .

🔸 Get bets in match
curl -s -X GET "http://localhost:5290/linera/tournament/get-bets?matchId=BET5" | jq .

🔸 View user betting history
curl -s -X GET http://localhost:5290/linera/user/test1/betting-transactions | jq .
```

🟪 7. FRIEND SYSTEM
```bash
🔸 Check friend list
curl -s "http://localhost:5290/linera/user/test7/friends" | jq
curl -s "http://localhost:5290/linera/user/test6/friends" | jq

🔸 Send friend request
curl -s -X POST http://localhost:5290/linera/user/send-friend-request \
  -H "Content-Type: application/json" \
  -d '{"userName": "test6","toUserChain": "CHAIN2"}' | jq

🔸 View pending requests
curl -s http://localhost:5290/linera/user/test1/pending-requests | jq

🔸 Accept friend request
curl -s -X POST http://localhost:5290/linera/user/accept-friend-request \
  -H "Content-Type: application/json" \
  -d '{"userName":"test1","requestId":"REQ_ID"}' | jq

🔸 Reject
curl -s -X POST http://localhost:5290/linera/user/reject-friend-request \
  -H "Content-Type: application/json" \
  -d '{"userName":"test7","requestId":"REQ_ID"}' | jq

🔸 Remove friend
curl -s -X POST http://localhost:5290/linera/user/remove-friend \
  -H "Content-Type: application/json" \
  -d '{"userName":"test6","friendChain":"CHAIN2"}' | jq

🔸 Check friendship
curl -s http://localhost:5290/linera/user/test6/is-friend/CHAIN2 | jq
```
