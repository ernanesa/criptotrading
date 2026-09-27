#!/usr/bin/env bash
set -euo pipefail

echo "================================================================="
echo "   AUTONOMOUS 30-MINUTE AUDIT RUNNER - $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
echo "================================================================="

# 1. API Health
echo -n "[1/6] Checking API Health... "
API_HEALTH=$(curl -s --max-time 5 http://localhost:5000/health/live || echo '{"status":"Unreachable"}')
echo "$API_HEALTH"

# 2. Database Connectivity & Recent Decisions (last 30 minutes)
echo "[2/6] Checking PostgreSQL Autonomy Decisions (Last 30m)..."
docker compose exec -T postgres psql -U cryptotrading -d cryptotrading -P pager=off -c "
SELECT 
    COUNT(*) as total_last_30m,
    COUNT(*) FILTER (WHERE outcome = 'DISPATCHABLE') as dispatchable_orders,
    COUNT(*) FILTER (WHERE outcome = 'NO_TRADE') as no_trade_cycles,
    COUNT(*) FILTER (WHERE no_trade_reason LIKE '%SafeMode%') as safe_mode_events
FROM trading.autonomy_decisions 
WHERE committed_at >= NOW() - INTERVAL '30 minutes';
"

# 3. Last 5 Autonomous Decisions Details
echo "[3/6] Latest 5 Decisions Summary..."
docker compose exec -T postgres psql -U cryptotrading -d cryptotrading -P pager=off -c "
SELECT close_time, strategy_id, signal, outcome, SUBSTRING(no_trade_reason, 1, 45) as reason, committed_at 
FROM trading.autonomy_decisions 
ORDER BY close_time DESC LIMIT 5;
"

# 4. Outbox Messages & Dead-letter Inspection
echo "[4/6] Inspecting Transactional Outbox..."
docker compose exec -T postgres psql -U cryptotrading -d cryptotrading -P pager=off -c "
SELECT message_id, event_type, attempts, processed_at, dead_lettered_at, SUBSTRING(last_error, 1, 60) as last_error, created_at
FROM trading.outbox_messages 
ORDER BY created_at DESC LIMIT 5;
"

# 5. Risk Policy Status
echo "[5/6] Checking Active Risk Policy..."
docker compose exec -T postgres psql -U cryptotrading -d cryptotrading -P pager=off -c "
SELECT policy_version, max_order_notional, max_daily_loss, withdrawals_enabled, effective_from, expires_at 
FROM trading.risk_policies 
ORDER BY created_at DESC LIMIT 1;
"

# 6. Process Daemons Status
echo "[6/6] Checking System Processes..."
ps aux | grep -E "CryptoTrading.Worker|CryptoTrading.Api|vite" | grep -v grep || true

echo "================================================================="
echo "   AUDIT COMPLETE - $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
echo "================================================================="
