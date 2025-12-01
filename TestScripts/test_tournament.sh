#!/bin/bash

BASE_URL="http://localhost:5290/linera"

echo "=== 🏆 TOURNAMENT COMPLETE TEST ==="
echo ""

# 1. Check season info
echo "1. 📊 Checking current season info..."
response=$(curl -s -X GET $BASE_URL/tournament/season-info)
current_season=$(echo "$response" | grep -o '"number":[0-9]*' | cut -d':' -f2)
next_season=$((current_season + 1))

echo "Tournament Current season: $current_season"
echo "Tournament Next season: $next_season"
echo ""

# 2. Start tournament season
echo "2. Starting Tournament Season $next_season..."
curl -s -X POST "$BASE_URL/tournament/start?name=Season_$next_season_Tournament"
echo ""

# 3. Set participants for testing, in product it already take from leaderboard top 8
echo "3. Setting participants..."
curl -s -X POST $BASE_URL/tournament/set-participants \
  -H "Content-Type: application/json" \
  -d '{
    "participants": ["test1", "test2", "test3", "test4", "test5", "test6", "test7", "test8"]
  }'
echo ""

# 4. Initialize tournament
echo "4. Initializing tournament 4 brackets and 8 players..."
curl -s -X POST $BASE_URL/tournament/init
echo ""

# 5. Check season info và match list
echo "5. 📋 Tournament status:"
curl -s -X GET $BASE_URL/tournament/season-info
echo ""

echo "Match list:"
curl -s -X GET $BASE_URL/tournament/match-list
echo ""

# 6. Quarter Finals
echo "6. Quarter Finals..."
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

# 7. Advance to Semi Finals
echo "7. Advancing to Semi Finals..."
curl -s -X POST $BASE_URL/tournament/advance-bracket
echo ""

# 8. Semi Finals
echo "8. Semi Finals..."
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

# 9. Advance to Finals
echo "9. Advancing to Finals..."
curl -s -X POST $BASE_URL/tournament/advance-bracket
echo ""

# 10. Finals
echo "10.=== Finals Round ==="
echo "F1: test1 vs test5"
curl -s -X POST $BASE_URL/tournament/submit-match \
  -H "Content-Type: application/json" \
  -d '{"matchId": "F1", "winnerUsername": "test1", "loserUsername": "test5"}'
echo ""

# 11. End tournament
echo "11. Ending tournament..."
curl -s -X POST $BASE_URL/tournament/end
echo ""

# 12. Final results
echo "12. Final Results:"
echo "Season info:"
curl -s -X GET $BASE_URL/tournament/season-info
echo ""

echo "Tournament leaderboard:"
curl -s -X GET $BASE_URL/tournament/leaderboard
echo ""

sleep 2
echo "=== TOURNAMENT TEST COMPLETED ==="