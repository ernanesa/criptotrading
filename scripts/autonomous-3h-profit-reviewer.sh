#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Autonomous 3-Hour Profit, Health & Strategy Reviewer
# Analyzes PnL, order dispatches, rejection root causes, HWM integrity, and
# automatically recalibrates artificial drawdown drifts on Testnet.
# ==============================================================================

NOW_UTC=$(date -u '+%Y-%m-%d %H:%M:%S UTC')
AUDIT_LOG_DIR="docs/audits"
AUDIT_LOG_FILE="${AUDIT_LOG_DIR}/autonomous_3h_reviews.md"
mkdir -p "${AUDIT_LOG_DIR}"

echo "================================================================================"
echo "   AUTONOMOUS 3-HOUR PROFIT & STRATEGY REVIEW - ${NOW_UTC}"
echo "================================================================================"

# 1. API & Process Liveness
echo -n "[1/7] Probing System Liveness... "
API_STATUS=$(curl -s --max-time 4 http://127.0.0.1:5000/health/live 2>/dev/null | jq -r '.status // "Unreachable"' || echo "Unreachable")
echo "API=${API_STATUS}"

# 2. Financial Balances & PnL
echo "[2/7] Extracting Realized & Unrealized PnL (Last 3 Hours)..."
PNL_DATA=$(docker compose exec -T postgres psql -U cryptotrading -d cryptotrading -P pager=off -t -A -F '|' -c "
SELECT 
    COALESCE(SUM(realized_pnl_quote), 0.0) as realized_pnl,
    COUNT(*) as total_fills,
    COUNT(*) FILTER (WHERE side = 'SELL' AND realized_pnl_quote > 0) as winning_trades,
    COUNT(*) FILTER (WHERE side = 'SELL' AND realized_pnl_quote < 0) as losing_trades
FROM trading.execution_fills 
WHERE occurred_at >= NOW() - INTERVAL '3 hours';
")
REALIZED_PNL=$(echo "${PNL_DATA}" | cut -d'|' -f1)
TOTAL_FILLS=$(echo "${PNL_DATA}" | cut -d'|' -f2)
WINS=$(echo "${PNL_DATA}" | cut -d'|' -f3)
LOSSES=$(echo "${PNL_DATA}" | cut -d'|' -f4)

echo "      Realized PnL (3h): ${REALIZED_PNL} USDT | Fills: ${TOTAL_FILLS} (Wins: ${WINS}, Losses: ${LOSSES})"

# 3. Current NAV & Equity
echo "[3/7] Current Portfolio NAV..."
docker compose exec -T postgres psql -U cryptotrading -d cryptotrading -P pager=off -c "
SELECT DISTINCT ON (symbol) symbol, nav_quote, unrealized_pnl_quote, observed_at 
FROM trading.portfolio_nav_snapshots 
ORDER BY symbol, observed_at DESC;
"

# 4. Decisions & SOTA Rejection Reasons (Last 3 Hours)
echo "[4/7] Autonomy Decisions & Reason Codes (Last 3 Hours)..."
docker compose exec -T postgres psql -U cryptotrading -d cryptotrading -P pager=off -c "
SELECT 
    no_trade_reason, 
    COUNT(*) as frequency,
    ROUND(COUNT(*) * 100.0 / SUM(COUNT(*)) OVER (), 1) as pct
FROM trading.autonomy_decisions 
WHERE committed_at >= NOW() - INTERVAL '3 hours'
GROUP BY no_trade_reason 
ORDER BY frequency DESC;
"

# 5. Outbox Order Dispatches & Dead-Letters
echo "[5/7] Outbox Execution Status (Last 3 Hours)..."
docker compose exec -T postgres psql -U cryptotrading -d cryptotrading -P pager=off -c "
SELECT 
    event_type, 
    COUNT(*) as total,
    COUNT(*) FILTER (WHERE processed_at IS NOT NULL) as processed,
    COUNT(*) FILTER (WHERE dead_lettered_at IS NOT NULL) as dead_lettered
FROM trading.outbox_messages 
WHERE created_at >= NOW() - INTERVAL '3 hours'
GROUP BY event_type;
"

# 6. High-Water Mark & Circuit Breaker Inspection (Strictly Read-Only)
echo "[6/7] Inspecting High-Water Mark & Circuit Breaker Status..."
HWM_ROWS=$(docker compose exec -T postgres psql -U cryptotrading -d cryptotrading -P pager=off -t -A -F '|' -c "
SELECT scope, equity_notional, high_water_mark_notional, drawdown_notional
FROM trading.autonomy_equity_high_water_marks;
")

DRAWDOWN_BREACH_DETECTED=false
while IFS='|' read -r scope equity hwm drawdown; do
    echo "      ${scope}: Equity=${equity} HWM=${hwm} Drawdown=${drawdown}"
    if awk -v d="${drawdown}" "BEGIN {exit !(d > 5.0)}"; then
        echo "      [ALERT] Drawdown threshold exceeded on ${scope}: ${drawdown} USDT > 5.0 USDT"
        DRAWDOWN_BREACH_DETECTED=true
    fi
done <<< "${HWM_ROWS}"

# 7. Operational Mode
echo "[7/7] Active Operational Control State..."
OP_MODE=$(docker compose exec -T postgres psql -U cryptotrading -d cryptotrading -P pager=off -t -A -c "
SELECT to_mode FROM trading.operational_control_audit_events ORDER BY occurred_at DESC LIMIT 1;
")
echo "      Operational Mode: ${OP_MODE}"

# Append summary to durable audit file
cat << AUDIT_ENTRY >> "${AUDIT_LOG_FILE}"

## Audit Review: ${NOW_UTC}
- **API Status:** ${API_STATUS}
- **Operational Mode:** ${OP_MODE}
- **Realized PnL (3h):** ${REALIZED_PNL} USDT
- **Execution Fills (3h):** ${TOTAL_FILLS} (Wins: ${WINS}, Losses: ${LOSSES})
- **Drawdown Breach Detected:** ${DRAWDOWN_BREACH_DETECTED}

AUDIT_ENTRY

echo "================================================================================"
echo "   REVIEW COMPLETE - Recorded to ${AUDIT_LOG_FILE}"
echo "================================================================================"
