#!/bin/bash

BASE_URL="http://localhost:5290/linera"

echo "===  Checking leaderboard season status... ==="

# Lấy thông tin season hiện tại
season_response=$(curl -sS -X GET "$BASE_URL/get-current-season-info")
current_season=$(echo "$season_response" | grep -o '"number":[0-9]*' | cut -d':' -f2)
season_status=$(echo "$season_response" | grep -o '"status":"[^"]*"' | cut -d':' -f2 | tr -d '"')
[ -z "$current_season" ] && current_season=0

echo "Current season: $current_season"
echo ""

# Start leaderboard nếu ended
if [ "$season_status" = "ended" ]; then
    echo "Season ended → Starting new Leaderboard Season $next_season..."
    curl -sS -X POST "$BASE_URL/leaderboard/start?name=Leaderboard_Season_$next_season"
    echo ""
else
    echo " Season is ACTIVE → Skip start, continue creating matches."
fi

echo ""
echo "===  Creating and submitting matches ==="

# Trực tiếp gán UserChainId cho từng test
declare -A userchains
userchains[test1]="6221fdca23da2d6054c20321bc73e74fbb8925d527fc48114061995c9a396bc2"
userchains[test2]="1bc6c28b1765f8e96c6d4e2b781edafb4b805fbb760d4aaad90bcb6d532c9e26"
userchains[test3]="2774e24770a613634ad5fdbafacb0d491ca64461d7992e63ba6f74fcdb85b3d6"
userchains[test4]="8c4cde0815b9314e263dd0ee7efdb7e8a31cd7ed08e58f57312729ff850b27cf"
userchains[test5]="a75621622a0e84b2ebd7531f4187dba4e189b40a225f26d89e715f19c13ae998"
userchains[test6]="7e2483bf6d092e906946e093099585bc3e55b34ea75bb3e7a2843353746538c8"
userchains[test7]="d0f4e4290905933f392178b0d186073f1d57d696ad88d87d3b01168ccb9b08b4"
userchains[test8]="5c5dfdf5ce573ad326bbff7baa364c47123429ee0aa316a2a95f332a1a3cb3ea"

for i in {1..4}; do
  test1="test$((i*2-1))"
  test2="test$((i*2))"

  # Tạo match
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

  echo ""
  echo "Response: $response"
  echo "Extracted - chainId: $chainId, appId: $appId"

  sleep 2

  echo " Submitting match-$i ($test1 vs $test2) on chain=$chainId app=$appId"

   curl -sS -X POST $BASE_URL/submit-match-result \
      -H "Content-Type: application/json" \
      -d "{
        \"chainId\": \"$chainId\",
        \"appId\": \"$appId\",
        \"matchResult\": {
          \"matchId\": \"s${current_season}_match-$i\",
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

echo "===  Current Season Leaderboard ==="
curl -sS -X GET "$BASE_URL/get-leaderboard-data"
echo ""
