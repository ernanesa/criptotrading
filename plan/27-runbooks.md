# 27 - Runbooks

## RB-01 WebSocket market data caiu
1. marcar feed degraded/disconnected;
2. bloquear novas entradas dependentes;
3. reconnect com backoff/jitter;
4. refazer snapshot/order book;
5. validar sequence/data age;
6. liberar somente após healthy;
7. alertar se exceder threshold.

## RB-02 User Data Stream caiu
1. bloquear ações que dependem de estado não confirmado;
2. reconnect;
3. consultar ordens/saldos relevantes via API;
4. reconciliar;
5. liberar apenas sem mismatch.

## RB-03 Timeout ao enviar ordem
1. NÃO reenviar imediatamente;
2. consultar por clientOrderId;
3. se encontrada, adotar estado da exchange;
4. se comprovadamente inexistente e política permitir, reemitir com decisão explícita;
5. registrar incidente/metric se recorrente.

## RB-04 Reconciliation mismatch
1. abrir circuit breaker;
2. preservar evidência;
3. consultar exchange novamente;
4. comparar orders/fills/positions;
5. corrigir materialized state ou tratar posição inesperada;
6. só reabrir trading após causa conhecida.

## RB-05 Kill switch
1. ativar global block;
2. confirmar audit event;
3. cancelar ordens conforme política;
4. verificar posições;
5. não zerar automaticamente se política não determinar;
6. comunicar/registrar motivo;
7. reset exige autenticação e checklist.

## RB-06 Drawdown breaker
1. bloquear novas entradas;
2. capturar strategy/portfolio attribution;
3. verificar se é mercado, execução ou bug;
4. não aumentar risco para recuperar perdas;
5. reativação apenas conforme Risk Policy.

## RB-07 Host restart com posição aberta
1. worker inicia em `Reconciling`;
2. carrega último ledger/config;
3. consulta exchange;
4. reconcilia ordens/fills/posição;
5. reconstitui stops/policies quando aplicável;
6. somente então entra em `Ready`.

## RB-08 Restaurar servidor
1. provisionar VM por IaC/runbook;
2. instalar Docker/runtime;
3. restaurar PostgreSQL;
4. restaurar configs/secrets por canal seguro;
5. subir em modo read-only/reconcile;
6. validar health/telemetry;
7. reabilitar paper/shadow/live conforme ambiente.

## RB-09 API key potencialmente comprometida
1. kill switch;
2. revogar key;
3. verificar histórico de ordens e permissões;
4. criar nova key com least privilege/no withdrawals;
5. atualizar secret store;
6. investigar fonte do vazamento;
7. só retomar após revisão.
