# Reconciliation Report RECON-20260919-local

**Escopo:** persistência local, ledger/outbox e serviço de reconciliação read-only
**Trigger:** testes de integração e inspeção de código; não é uma reconciliação de conta Binance executada
**Data (UTC):** 2026-09-19
**Commit de referência:** `29291c1`

## Estado comparado

| Entidade | Estado local/evidência | Estado exchange | Diferença/ação | Status |
|---|---|---|---|---|
| Journal/ledger | integração PostgreSQL verifica transação e idempotência | não consultado | sem comparação externa | PASS local / PENDENTE externo |
| Outbox | lease, retry e dedupe cobertos pela integração | não aplicável | manter durable antes de side effect | PASS local |
| Balances free/locked | contrato de conta read-only disponível | endpoint não executado com credencial | bloquear novas entradas até snapshot | PENDENTE |
| Orders/open orders | reconciliação fail-closed disponível | endpoint não executado | estado desconhecido exige SafeMode/NO_TRADE | PENDENTE |
| Fills/trades/fees | modelo e drift detection disponíveis | endpoint não executado | rebuild do ledger ainda não evidenciado | PENDENTE |
| PnL/positions | projeção local testável | sem valuation externa | não promover | PENDENTE |

## Estado ambíguo e decisão

Não houve ordem real, cancelamento, credencial privada ou consulta autenticada neste snapshot. Portanto não há autorização para inferir paridade com a exchange. A decisão é `No-New-Entries` para qualquer modo que possa emitir ordem; Paper continua permitido. Snapshots read-only antigos ou com timestamp futuro agora falham fechado e ativam `SafeMode` (`STALE_SNAPSHOT`).

## Próxima evidência necessária

Executar, com credenciais novas de Testnet fora do repositório e permissões mínimas, um relatório redigido contendo hashes/timestamps de balances, open orders e fills; simular timeout/reconnect; comparar ledger e registrar divergências. Falha ou ausência de qualquer comparação deve ativar SafeMode e manter `No-Go`.
