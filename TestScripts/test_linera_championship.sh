#!/bin/bash

BASE_URL="http://localhost:5290/linera"

echo "=== COMPLETE SEASON TEST (LEADERBOARD + TOURNAMENT) ==="
echo ""

# 1. Kiểm tra season hiện tại
echo "1. Checking current seasons..."
echo "Leaderboard Season:"
leaderboard_response=$(curl -sS -X GET $BASE_URL/get-current-season-info)
leaderboard_season=$(echo "$leaderboard_response" | grep -o '"number":[0-9]*' | cut -d':' -f2)
echo "Current Leaderboard Season: $leaderboard_season"

echo "Tournament Season:"
tournament_response=$(curl -s -X GET $BASE_URL/tournament/season-info)
tournament_season=$(echo "$tournament_response" | grep -o '"number":[0-9]*' | cut -d':' -f2)
echo "Current Tournament Season: $tournament_season"

next_season=$((leaderboard_season + 1))
echo "Next Season: $next_season"

# ========== PHASE 1: LEADERBOARD SEASON ==========
echo "=== PHASE 1: LEADERBOARD SEASON $next_season ==="

# 2. Start new leaderboard season (CHECK STATUS BEFORE START)
echo "2. Checking Leaderboard Season Status..."

leaderboard_status=$(echo "$leaderboard_response" | grep -o '"status":"[^"]*"' | cut -d':' -f2 | tr -d '"')

if [ "$leaderboard_status" = "active" ]; then
    echo "Leaderboard season is already ACTIVE → Skipping start."
else
    echo "Starting Leaderboard Season $next_season..."
    curl -sS -X POST "$BASE_URL/leaderboard/start?name=Leaderboard_Season_$next_season"
    echo ""
fi

echo ""
# 3. Tạo và submit matches test1..test8 
declare -A userchains
userchains[test1]="6221fdca23da2d6054c20321bc73e74fbb8925d527fc48114061995c9a396bc2"
userchains[test2]="1bc6c28b1765f8e96c6d4e2b781edafb4b805fbb760d4aaad90bcb6d532c9e26"
userchains[test3]="2774e24770a613634ad5fdbafacb0d491ca64461d7992e63ba6f74fcdb85b3d6"
userchains[test4]="8c4cde0815b9314e263dd0ee7efdb7e8a31cd7ed08e58f57312729ff850b27cf"
userchains[test5]="a75621622a0e84b2ebd7531f4187dba4e189b40a225f26d89e715f19c13ae998"
userchains[test6]="7e2483bf6d092e906946e093099585bc3e55b34ea75bb3e7a2843353746538c8"
userchains[test7]="d0f4e4290905933f392178b0d186073f1d57d696ad88d87d3b01168ccb9b08b4"
userchains[test8]="5c5dfdf5ce573ad326bbff7baa364c47123429ee0aa316a2a95f332a1a3cb3ea"

echo "3.  Creating and submitting matches for leaderboard..."

if [ "$leaderboard_status" = "active" ]; then
    match_season=$leaderboard_season
else
    match_season=$next_season
fi

for i in {1..4}; do
    test1="test$((i*2-1))"
    test2="test$((i*2))"  
    
    response=$(curl -sS -X POST $BASE_URL/open-and-create \
      -H "Content-Type: application/json" \
      -d "{
		  \"test1\": \"$test1\",
		  \"test2\": \"$test2\",
		  \"test1chain\": \"${userchains[$test1]}\",
		  \"test2chain\": \"${userchains[$test2]}\",
		  \"matchType\": \"rank\"
		}")
    
    chainId=$(echo "$response" | grep -o '"chainId":"[^"]*"' | cut -d'"' -f4)
    appId=$(echo "$response" | grep -o '"appId":"[^"]*"' | cut -d'"' -f4)

    echo "Response: $response"
    echo "Extracted - chainId: $chainId, appId: $appId"
    
    sleep 5

    echo "Submitting result for match-$i ($test1 vs $test2) on chain=$chainId app=$appId"

     curl -sS -X POST $BASE_URL/submit-match-result \
      -H "Content-Type: application/json" \
      -d "{
        \"chainId\": \"$chainId\",
        \"appId\": \"$appId\",
        \"matchResult\": {
          \"matchId\": \"s${match_season}_match-$i\",
          \"player1Username\": \"$test1\",
          \"player2Username\": \"$test2\",
          \"player1Userchain\": \"${userchains[$test1]}\",
          \"player2Userchain\": \"${userchains[$test2]}\",
          \"player1Heroname\": \"Doom\",
          \"player2Heroname\": \"Kelsey\",
          \"winnerUsername\": \"$test1\",
          \"loserUsername\": \"$test2\",
          \"durationSeconds\": 15,
          \"mapName\": \"arena02\",
          \"matchType\": \"Rank\"
        }
      }"
    echo ""
done

# 4. Check leaderboard status
echo "4.  Leaderboard Status Check:"
echo "Current Season Info:"
curl -sS -X GET $BASE_URL/get-current-season-info
echo ""

echo "Current Leaderboard:"
curl -sS -X GET $BASE_URL/get-leaderboard-data
echo ""

echo "Top 8 for Tournament:"
curl -sS -X GET $BASE_URL/get-tournament-top8
echo ""

# 5. End leaderboard season
echo "5. 🏁 Ending Leaderboard Season $next_season..."
curl -sS -X POST $BASE_URL/leaderboard/end
echo ""

# 6. Check leaderboard history
echo "6.  Leaderboard Season $next_season History:"
echo "Season $next_season Info:"
curl -sS -X GET "$BASE_URL/get-season-info?seasonNumber=$next_season"
echo ""

echo "Season $next_season Leaderboard:"
curl -sS -X GET "$BASE_URL/get-season-leaderboard?seasonNumber=$next_season&limit=10"
echo ""

# ========== PHASE 2: TOURNAMENT SEASON ==========
echo "=== PHASE 2: TOURNAMENT SEASON $next_season ==="
echo ""

# 7. Start tournament season
echo "7.  Starting Tournament Season $next_season..."
curl -s -X POST "$BASE_URL/tournament/start?name=Season_${next_season}_Tournament"
echo ""

# 8. Set participants (trong thực tế sẽ lấy từ leaderboard top 8)
echo "8.  Setting tournament participants..."
curl -s -X POST $BASE_URL/tournament/set-participants \
  -H "Content-Type: application/json" \
  -d '{
    "participants": ["test1", "test2", "test3", "test4", "test5", "test6", "test7", "test8"]
  }'
echo ""

# 9. Initialize tournament bracket
echo "9.  Initializing tournament bracket..."
curl -s -X POST $BASE_URL/tournament/init
echo ""
sleep 1

# 10. Check tournament status
echo "10.  Tournament status:"
curl -s -X GET $BASE_URL/tournament/season-info
echo ""

echo "Tournament Match list:"
curl -s -X GET $BASE_URL/tournament/match-list
echo ""

# 11. Quarter Finals
echo "11.  Quarter Finals..."
echo "QF1: test1 vs test2"
curl -s -X POST $BASE_URL/tournament/submit-match \
  -H "Content-Type: application/json" \
  -d '{"matchId": "QF1", "winnerUsername": "test1", "loserUsername": "test2"}'
echo ""

echo "QF2: test3 vs test4"
curl -s -X POST $BASE_URL/tournament/submit-match \
  -H "Content-Type: application/json" \
  -d '{"matchId": "QF2", "winnerUsername": "test3", "loserUsername": "test4"}'
echo ""

echo "QF3: test5 vs test6"
curl -s -X POST $BASE_URL/tournament/submit-match \
  -H "Content-Type: application/json" \
  -d '{"matchId": "QF3", "winnerUsername": "test5", "loserUsername": "test6"}'
echo ""

echo "QF4: test7 vs test8"
curl -s -X POST $BASE_URL/tournament/submit-match \
  -H "Content-Type: application/json" \
  -d '{"matchId": "QF4", "winnerUsername": "test7", "loserUsername": "test8"}'
echo ""

# 12. Advance to Semi Finals
echo "12.  Advancing to Semi Finals..."
curl -s -X POST $BASE_URL/tournament/advance-bracket
echo ""

# 13. Semi Finals
echo "13.  Semi Finals..."
echo "SF1: test1 vs test3"
curl -s -X POST $BASE_URL/tournament/submit-match \
  -H "Content-Type: application/json" \
  -d '{"matchId": "SF1", "winnerUsername": "test1", "loserUsername": "test3"}'
echo ""

echo "SF2: test5 vs test7"
curl -s -X POST $BASE_URL/tournament/submit-match \
  -H "Content-Type: application/json" \
  -d '{"matchId": "SF2", "winnerUsername": "test5", "loserUsername": "test7"}'
echo ""

# 14. Advance to Finals
echo "14.  Advancing to Finals..."
curl -s -X POST $BASE_URL/tournament/advance-bracket
echo ""
sleep 1
# 15. Finals
echo "15.  Finals..."
echo "F1: test1 vs test5"
curl -s -X POST $BASE_URL/tournament/submit-match \
  -H "Content-Type: application/json" \
  -d '{"matchId": "F1", "winnerUsername": "test1", "loserUsername": "test5"}'
echo ""

# 16. End tournament
echo "16. Ending Tournament Season $next_season..."
curl -s -X POST $BASE_URL/tournament/end
echo ""

# ========== FINAL RESULTS ==========
echo "===  FINAL RESULTS - SEASON $next_season ==="
echo ""

# 17. Final leaderboard stats
echo "17.  Global Statistics:"
echo "Test1 Global Stats:"
curl -sS -X GET "$BASE_URL/get-user-global-stats?userName=test1"
echo ""

echo "Test2 Global Stats:"
curl -sS -X GET "$BASE_URL/get-user-global-stats?userName=test2"
echo ""

# 18. Final tournament results
echo "18.  Tournament Final Results:"
echo "Tournament Season Info:"
curl -s -X GET $BASE_URL/tournament/season-info
echo ""

echo "Tournament Leaderboard:"
curl -s -X GET $BASE_URL/tournament/leaderboard
echo ""

echo "Tournament #$next_season Info:"
curl -sS -X GET "$BASE_URL/get-tournament-info?tournamentNumber=$next_season"
echo ""

echo "Tournament #$next_season Leaderboard:"
curl -sS -X GET "$BASE_URL/get-tournament-leaderboard?tournamentNumber=$next_season"
echo ""

echo "===  COMPLETE SEASON TEST FINISHED ==="
