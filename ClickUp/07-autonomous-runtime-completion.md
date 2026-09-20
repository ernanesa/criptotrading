# E-19 — Closed-loop Autonomous Runtime Integration

**Parent:** CryptoTrading / Product Delivery / R1.0 Active Delivery  
**Status:** In Progress — implementação incremental; promoção bloqueada por gates pendentes
**Priority:** P0/P1  
**Release:** R1.0-A2  
**Source:** `plan/31-autonomous-runtime-completion.md`  
**Scope:** fechar o ciclo executável market-data → decisão → risco → outbox → OMS → exchange → projeção → reconciliação → saúde.

## Regras comuns dos cartões

- Produção inicial é local/Testnet; nenhuma credencial deve aparecer no repositório.
- LLM/RAG/MCP não podem autorizar ou enviar ordens.
- Toda ordem exige `DecisionId`, `ClientOrderId` determinístico, RiskDecision persistida e outbox durável.
- `Unknown`, dados stale, política inválida, divergência ou dependência indisponível bloqueiam novas entradas.
- Arquivos permitidos são os listados na task; adicionar um novo projeto exige ADR e atualização da rastreabilidade.
- Cada subtask termina com teste e evidência; “compila” sem teste específico não é Done.

## US-190 — Coordenar lifecycle e readiness do runtime

**Valor:** o robô inicia, aquece, reconcilia, opera e degrada sem depender de intervenção rotineira.  
**Dependências:** US-195, US-197. **Não inclui:** reset automático de KillSwitch ou promoção live.

**AC-AUTONOMY-02 — Given** processo iniciado, dependências configuradas e conta conhecida; **When** ocorre startup, reconnect, falha transitória ou KillSwitch; **Then** a máquina persistida percorre `Bootstrapping → Reconciling → WarmingUp → Ready`, bloqueia entradas fora de `Ready`, registra motivo/versionamento e nunca sai automaticamente de `KillSwitch`.

### Tasks

- **TASK-US-190-01 — Contrato de estados e readiness** (`src/CryptoTrading.Domain/Runtime`, `tests/CryptoTrading.Domain.Tests`)
  - **SUBTASK-TASK-US-190-01-01:** definir enum, transições válidas, invariantes e motivos de transição.
  - **SUBTASK-TASK-US-190-01-02:** definir `ReadinessSnapshot` com dependências, freshness e revisão monotônica.
  - **SUBTASK-TASK-US-190-01-03:** adicionar testes de transição inválida e bloqueio fora de Ready; validar `dotnet test ...Domain.Tests`.
- **TASK-US-190-02 — Persistência e migração do runtime** (`src/CryptoTrading.Persistence`, `db/migrations`)
  - **SUBTASK-TASK-US-190-02-01:** criar tabela/índice de estado, checkpoint e transição append-only.
  - **SUBTASK-TASK-US-190-02-02:** implementar repositório idempotente com concorrência otimista.
  - **SUBTASK-TASK-US-190-02-03:** executar integração PostgreSQL, restart e reconstrução; anexar saída do comando.
- **TASK-US-190-03 — Orquestração no Worker** (`src/CryptoTrading.Worker`)
  - **SUBTASK-TASK-US-190-03-01:** registrar lifecycle coordinator sem criar ordens no bootstrap.
  - **SUBTASK-TASK-US-190-03-02:** conectar readiness a market data, RiskPolicy, account stream e reconciliação.
  - **SUBTASK-TASK-US-190-03-03:** testar startup/reconnect/concurrency e documentar dependências.
- **TASK-US-190-04 — Recuperação segura** (`src/CryptoTrading.Worker`, `src/CryptoTrading.Configuration`)
  - **SUBTASK-TASK-US-190-04-01:** definir cooldown/backoff e condições de retorno de Degraded/SafeMode.
  - **SUBTASK-TASK-US-190-04-02:** impedir reset automático de KillSwitch e bypass de reconciliação.
  - **SUBTASK-TASK-US-190-04-03:** testar restart com ordens `Submitting/Unknown` e condição de não concluído.
- **TASK-US-190-05 — Evidência e operação** (`docs/readiness`, `plan/traceability.yaml`)
  - **SUBTASK-TASK-US-190-05-01:** registrar audit events e métricas de estado/readiness.
  - **SUBTASK-TASK-US-190-05-02:** atualizar runbook de startup, SafeMode e KillSwitch.
  - **SUBTASK-TASK-US-190-05-03:** anexar evidence bundle e atualizar traceability; sem evidência permanece Proposed.

## US-191 — Executar market-data pipeline acionável

**Valor:** decisões usam dados frescos, ordenados, deduplicados e reproduzíveis.  
**Dependências:** US-190. **Não inclui:** sinais de estratégia adaptativa.

**AC-AUTONOMY-03 — Given** stream público com snapshot/deltas e candles; **When** houver gap, duplicata, atraso ou clock drift; **Then** o pipeline deduplica/ordena, rejeita o snapshot inválido, reaquece e não publica contexto acionável até atingir freshness mínima.

### Tasks

- **TASK-US-191-01 — Envelope e normalização** (`src/CryptoTrading.Domain/MarketData`, `src/CryptoTrading.Exchange.Binance`)
  - **SUBTASK-TASK-US-191-01-01:** definir envelope versionado com timestamps, sequence e hash.
  - **SUBTASK-TASK-US-191-01-02:** normalizar trades, book e kline Binance para tipos de domínio.
  - **SUBTASK-TASK-US-191-01-03:** testar payloads válidos/malformados e precisão decimal.
- **TASK-US-191-02 — Stream resiliente e checkpoint** (`src/CryptoTrading.Exchange.Binance`, `src/CryptoTrading.Persistence`)
  - **SUBTASK-TASK-US-191-02-01:** implementar reconnect/backoff/jitter e limites de carga.
  - **SUBTASK-TASK-US-191-02-02:** persistir sequence/checkpoint e detectar gap/duplicata.
  - **SUBTASK-TASK-US-191-02-03:** testar reconnect, gap, duplicata e replay com integração.
- **TASK-US-191-03 — Data-quality gate** (`src/CryptoTrading.Domain/MarketData`, `src/CryptoTrading.Application`)
  - **SUBTASK-TASK-US-191-03-01:** avaliar freshness, spread, profundidade, clock e candle fechado.
  - **SUBTASK-TASK-US-191-03-02:** emitir `NoTrade`/rejection reason sem fallback silencioso.
  - **SUBTASK-TASK-US-191-03-03:** criar property tests de monotonicidade e stale blocking.
- **TASK-US-191-04 — Feature snapshot e replay** (`src/CryptoTrading.Application`, `tests`)
  - **SUBTASK-TASK-US-191-04-01:** construir snapshot determinístico de indicadores baseline.
  - **SUBTASK-TASK-US-191-04-02:** versionar feature set e persistir payload hash.
  - **SUBTASK-TASK-US-191-04-03:** provar paridade replay/live com dataset fixo.
- **TASK-US-191-05 — Observabilidade** (`src/CryptoTrading.Worker`, `docs/observability`)
  - **SUBTASK-TASK-US-191-05-01:** medir idade, gaps, reconnects e rejeições.
  - **SUBTASK-TASK-US-191-05-02:** publicar alertas para stream stale e clock drift.
  - **SUBTASK-TASK-US-191-05-03:** anexar dashboard/query e evidência do comando de teste.

## US-192 — Fechar ciclo autônomo da baseline

**Valor:** cada candle fechado produz uma decisão completa ou `NO_TRADE`, sem duplicação.  
**Dependências:** US-190, US-191, US-193. **Não inclui:** multi-strategy.

**AC-AUTONOMY-04 — Given** snapshot BTCUSDT válido e candle fechado; **When** o trigger é processado uma ou várias vezes; **Then** estratégia, target, risco e outbox são persistidos na mesma decisão determinística, e retries reutilizam `DecisionId`.

### Tasks

- **TASK-US-192-01 — Contratos do ciclo** (`src/CryptoTrading.Domain/Autonomy`)
  - **SUBTASK-TASK-US-192-01-01:** definir DecisionCycle, Signal, PortfolioTarget e DecisionTrigger.
  - **SUBTASK-TASK-US-192-01-02:** definir códigos explícitos de NoTrade e versões.
  - **SUBTASK-TASK-US-192-01-03:** testar serialização, determinismo e ausência de sinal implícito.
- **TASK-US-192-02 — Baseline strategy adapter** (`src/CryptoTrading.Strategies`, `src/CryptoTrading.Application`)
  - **SUBTASK-TASK-US-192-02-01:** adaptar EMA 20/50 long-only a candle fechado.
  - **SUBTASK-TASK-US-192-02-02:** construir target limitado por account snapshot.
  - **SUBTASK-TASK-US-192-02-03:** testar warmup, crossover, saída e `NO_TRADE`.
- **TASK-US-192-03 — AutonomyCycle** (`src/CryptoTrading.Application`, `src/CryptoTrading.Worker`)
  - **SUBTASK-TASK-US-192-03-01:** encadear quality → feature → strategy → target → risk.
  - **SUBTASK-TASK-US-192-03-02:** persistir trace/outbox antes do submit.
  - **SUBTASK-TASK-US-192-03-03:** testar concorrência por symbol/candle e replay idempotente.
- **TASK-US-192-04 — Gate de execução** (`src/CryptoTrading.Worker`)
  - **SUBTASK-TASK-US-192-04-01:** impedir ciclo quando runtime não está Ready.
  - **SUBTASK-TASK-US-192-04-02:** priorizar redução/saída e bloquear venda além do saldo.
  - **SUBTASK-TASK-US-192-04-03:** testar Paper/Shadow/Testnet sem cross-environment.
- **TASK-US-192-05 — Evidence da decisão** (`docs/readiness`, `tests`)
  - **SUBTASK-TASK-US-192-05-01:** registrar DecisionCycleStarted/Completed e NoTradeSelected.
  - **SUBTASK-TASK-US-192-05-02:** criar replay report com hashes e versões.
  - **SUBTASK-TASK-US-192-05-03:** anexar resultado e marcar Done somente com traceability atualizada.

## US-193 — Tornar risk obrigatório e completo

**Valor:** nenhuma exposição nasce sem policy ativa, contexto consistente e limites independentes.  
**Dependências:** US-190, US-192. **Não inclui:** alteração automática de hard limits.

**AC-AUTONOMY-05 — Given** execução Testnet habilitada; **When** RiskPolicy está ausente, expirada, incompatível ou qualquer guard está unhealthy; **Then** startup falha seguro ou o ciclo retorna rejeição/SafeMode, sem aumentar target nem enviar ordem.

### Tasks

- **TASK-US-193-01 — Startup gate de RiskPolicy** (`src/CryptoTrading.Worker/WorkerRuntimeOptions.cs`, `src/CryptoTrading.Configuration`)
  - **SUBTASK-TASK-US-193-01-01:** tornar policy ativa/aprovada/não expirada obrigatória em Testnet.
  - **SUBTASK-TASK-US-193-01-02:** validar environment, UniversePolicy e withdrawals disabled.
  - **SUBTASK-TASK-US-193-01-03:** testar configurações inválidas e preservar Paper/Shadow.
- **TASK-US-193-02 — RiskContext consistente** (`src/CryptoTrading.Domain/Risk`, `src/CryptoTrading.Persistence`)
  - **SUBTASK-TASK-US-193-02-01:** compor saldo, posição, fees, PnL, drawdown e recon age.
  - **SUBTASK-TASK-US-193-02-02:** rejeitar snapshot stale/ambíguo e clock inválido.
  - **SUBTASK-TASK-US-193-02-03:** testar invariantes property-based de não aumento.
- **TASK-US-193-03 — Guards financeiros e de mercado** (`src/CryptoTrading.Domain/Risk`)
  - **SUBTASK-TASK-US-193-03-01:** implementar notional, exposição, fee reserve, daily loss e DD.
  - **SUBTASK-TASK-US-193-03-02:** implementar spread, slippage, volatilidade, depth, turnover e edge.
  - **SUBTASK-TASK-US-193-03-03:** testar limites, arredondamento e rejeições próximas ao bound.
- **TASK-US-193-04 — Guards operacionais** (`src/CryptoTrading.Application`, `src/CryptoTrading.Worker`)
  - **SUBTASK-TASK-US-193-04-01:** bloquear com DB/outbox/queue/rate-limit/reconciliation unhealthy.
  - **SUBTASK-TASK-US-193-04-02:** bloquear Unknown order e StrategyHealth degraded.
  - **SUBTASK-TASK-US-193-04-03:** testar fail-closed e mensagens auditáveis.
- **TASK-US-193-05 — Evidence de risco** (`docs/readiness`, `plan/templates/risk-policy.md`)
  - **SUBTASK-TASK-US-193-05-01:** registrar RiskApproved/Adjusted/Rejected com policy hash.
  - **SUBTASK-TASK-US-193-05-02:** atualizar matriz de promoção e exemplos Testnet.
  - **SUBTASK-TASK-US-193-05-03:** anexar property/integration report e residual risk.

## US-194 — Completar OMS e projeção financeira

**Valor:** ack, fills, cancelamentos e ledger sobrevivem a retries, restart e eventos fora de ordem.  
**Dependências:** US-192, US-195. **Não inclui:** event sourcing integral.

**AC-AUTONOMY-06 — Given** uma ordem autorizada e eventos REST/user-data duplicados ou fora de ordem; **When** a projeção é atualizada; **Then** o OMS não regride, não gera overfill, mantém ledger idempotente e deixa o outbox pendente até resultado externo durável.

### Tasks

- **TASK-US-194-01 — State machine OMS** (`src/CryptoTrading.Domain/Execution`)
  - **SUBTASK-TASK-US-194-01-01:** modelar estados Created até Filled/Cancelled/Rejected/Expired/Unknown.
  - **SUBTASK-TASK-US-194-01-02:** definir transições idempotentes e terminalidade.
  - **SUBTASK-TASK-US-194-01-03:** testar duplicata, atraso, conflito e Unknown.
- **TASK-US-194-02 — Persistência de lifecycle/fills** (`src/CryptoTrading.Persistence`)
  - **SUBTASK-TASK-US-194-02-01:** criar tabelas de order transitions, fills e inbox.
  - **SUBTASK-TASK-US-194-02-02:** implementar unique keys por exchange event/trade ID.
  - **SUBTASK-TASK-US-194-02-03:** executar integração de restart e concorrência.
- **TASK-US-194-03 — Projection e ledger** (`src/CryptoTrading.Persistence`)
  - **SUBTASK-TASK-US-194-03-01:** conectar ack/fill/cancel a open-order e position projection.
  - **SUBTASK-TASK-US-194-03-02:** postar ledger/fees/PnL com ajuste compensatório.
  - **SUBTASK-TASK-US-194-03-03:** testar idempotência, precisão e saldo negativo impossível.
- **TASK-US-194-04 — Outbox completion** (`src/CryptoTrading.Worker`, `src/CryptoTrading.Persistence`)
  - **SUBTASK-TASK-US-194-04-01:** concluir mensagem apenas após projeção externa durável.
  - **SUBTASK-TASK-US-194-04-02:** reter lease em erro e recuperar mensagens em restart.
  - **SUBTASK-TASK-US-194-04-03:** testar retry/dead-letter sem perder lifecycle.
- **TASK-US-194-05 — Operação e auditoria** (`docs/runbooks`, `docs/readiness`)
  - **SUBTASK-TASK-US-194-05-01:** registrar todos os eventos de lifecycle e IDs.
  - **SUBTASK-TASK-US-194-05-02:** documentar Unknown/ambiguous recovery.
  - **SUBTASK-TASK-US-194-05-03:** anexar relatório de replay de uma ordem completa.

## US-195 — Unificar user stream e reconciliação durante execução

**Valor:** o estado local converge para a evidência da exchange também durante operação Testnet.  
**Dependências:** US-190, US-194. **Não inclui:** saque ou transferência.

**AC-AUTONOMY-07 — Given** `NO_TRADE=false` em Testnet; **When** startup, reconnect, intervalo, gap ou submit ambíguo ocorre; **Then** account stream e reconciliation permanecem habilitados, com mismatch em SafeMode e sem exclusões mutuamente incompatíveis.

### Tasks

- **TASK-US-195-01 — Remover gates incompatíveis** (`src/CryptoTrading.Worker/WorkerRuntimeOptions.cs`, `Program.cs`)
  - **SUBTASK-TASK-US-195-01-01:** permitir reconciliação e account stream com execução habilitada.
  - **SUBTASK-TASK-US-195-01-02:** manter validações de segurança e withdrawals disabled.
  - **SUBTASK-TASK-US-195-01-03:** testar matriz Paper/Shadow/Testnet/Production-disabled.
- **TASK-US-195-02 — Consumer do user stream** (`src/CryptoTrading.Worker`, `src/CryptoTrading.Exchange.Binance`)
  - **SUBTASK-TASK-US-195-02-01:** normalizar execution reports e account updates.
  - **SUBTASK-TASK-US-195-02-02:** deduplicar por event/trade ID e encaminhar OMS.
  - **SUBTASK-TASK-US-195-02-03:** testar disconnect, malformed event e reconnect.
- **TASK-US-195-03 — Coordinator de reconciliação** (`src/CryptoTrading.Worker`, `src/CryptoTrading.Persistence`)
  - **SUBTASK-TASK-US-195-03-01:** disparar startup/periodic/reconnect/ambiguous/manual reconciliation.
  - **SUBTASK-TASK-US-195-03-02:** comparar orders, fills, balances, fees e positions.
  - **SUBTASK-TASK-US-195-03-03:** testar mismatch e correção compensatória sem sobrescrever histórico.
- **TASK-US-195-04 — Estado desconhecido e bloqueio** (`src/CryptoTrading.Application`)
  - **SUBTASK-TASK-US-195-04-01:** bloquear símbolo com order Unknown até evidência.
  - **SUBTASK-TASK-US-195-04-02:** integrar resultado à readiness e RiskContext.
  - **SUBTASK-TASK-US-195-04-03:** testar corrida entre stream, REST e novo ciclo.
- **TASK-US-195-05 — Evidência de convergência** (`docs/readiness`, `tests`)
  - **SUBTASK-TASK-US-195-05-01:** registrar run/mismatch/age e alertas.
  - **SUBTASK-TASK-US-195-05-02:** criar reconciliation report versionado.
  - **SUBTASK-TASK-US-195-05-03:** anexar teste de reconnect e atualizar traceability.

## US-196 — Implementar execution planner determinístico

**Valor:** uma decisão autorizada vira ordens economicamente limitadas, sem overfill ou retry cego.  
**Dependências:** US-193, US-194, US-195. **Não inclui:** market making.

**AC-AUTONOMY-08 — Given** target autorizado, preço/depth recentes e filtros exchangeInfo; **When** houver partial fill, timeout ou custo fora do limite; **Then** o planner usa passive limit por padrão, calcula apenas residual, cancela/replaneja com novo attempt determinístico ou retorna `NO_TRADE`.

### Tasks

- **TASK-US-196-01 — Contracto e custo esperado** (`src/CryptoTrading.Domain/Execution`)
  - **SUBTASK-TASK-US-196-01-01:** definir ExecutionPlan, leg, TTL, cost/edge bounds e attempt.
  - **SUBTASK-TASK-US-196-01-02:** definir client ID derivado do DecisionId.
  - **SUBTASK-TASK-US-196-01-03:** testar determinismo, residual e budget conservation.
- **TASK-US-196-02 — Passive-limit lifecycle** (`src/CryptoTrading.Worker`, `src/CryptoTrading.Exchange.Binance`)
  - **SUBTASK-TASK-US-196-02-01:** gerar limit maker quando spread/depth suportarem.
  - **SUBTASK-TASK-US-196-02-02:** aplicar TTL, cancel pending e replan seguro.
  - **SUBTASK-TASK-US-196-02-03:** testar partial fill e cancel ambíguo.
- **TASK-US-196-03 — Market pricing bounded** (`src/CryptoTrading.Exchange.Binance`)
  - **SUBTASK-TASK-US-196-03-01:** exigir snapshot recente, depth e bound conservador.
  - **SUBTASK-TASK-US-196-03-02:** bloquear `MARKET_NOTIONAL_UNVERIFIABLE` sem evidência.
  - **SUBTASK-TASK-US-196-03-03:** testar slippage, stale price e rate limit.
- **TASK-US-196-04 — Filtros e saldo pré-submit** (`src/CryptoTrading.Worker`)
  - **SUBTASK-TASK-US-196-04-01:** revalidar exchangeInfo, lot size, tick size e fee reserve.
  - **SUBTASK-TASK-US-196-04-02:** impedir cumulative quantity acima do target.
  - **SUBTASK-TASK-US-196-04-03:** testar rounding e rejeições da exchange.
- **TASK-US-196-05 — Attribution** (`src/CryptoTrading.Persistence`, `docs/observability`)
  - **SUBTASK-TASK-US-196-05-01:** persistir latency, fill ratio e implementation shortfall.
  - **SUBTASK-TASK-US-196-05-02:** correlacionar plano, attempts e lifecycle.
  - **SUBTASK-TASK-US-196-05-03:** anexar relatório de execução determinística.

## US-197 — Automatizar self-healing e fail-safe

**Valor:** falhas transitórias recuperam-se sem improvisar risco; falhas desconhecidas degradam com segurança.  
**Dependências:** US-190, US-191, US-195.

**AC-AUTONOMY-09 — Given** stream, exchange, DB, fila, relógio ou estratégia falhando; **When** o supervisor classifica o incidente; **Then** aplica retry/backoff/breaker apenas onde permitido, entra em SafeMode quando necessário e preserva OMS/reconciliação.

### Tasks

- **TASK-US-197-01 — Failure classifier** (`src/CryptoTrading.Domain/Operations`)
  - **SUBTASK-TASK-US-197-01-01:** definir classes transient, unsafe, unknown e terminal.
  - **SUBTASK-TASK-US-197-01-02:** mapear cada classe para ação e estado runtime.
  - **SUBTASK-TASK-US-197-01-03:** testar tabela de falhas e default fail-safe.
- **TASK-US-197-02 — Retry/breaker/backpressure** (`src/CryptoTrading.Worker`)
  - **SUBTASK-TASK-US-197-02-01:** respeitar Retry-After e limites 429/418.
  - **SUBTASK-TASK-US-197-02-02:** aplicar circuit breaker, jitter e queue backpressure.
  - **SUBTASK-TASK-US-197-02-03:** testar storms, dead-letter e recuperação.
- **TASK-US-197-03 — Supervised recovery** (`src/CryptoTrading.Worker`)
  - **SUBTASK-TASK-US-197-03-01:** reiniciar adapters sem duplicar decisões.
  - **SUBTASK-TASK-US-197-03-02:** exigir reconciliação e warmup antes de Ready.
  - **SUBTASK-TASK-US-197-03-03:** testar restart process e crash recovery.
- **TASK-US-197-04 — Recursos e isolamento** (`src/CryptoTrading.Worker`, `docker-compose.yml`)
  - **SUBTASK-TASK-US-197-04-01:** definir limites de CPU/memória/disco e fila.
  - **SUBTASK-TASK-US-197-04-02:** pausar research quando recursos críticos.
  - **SUBTASK-TASK-US-197-04-03:** testar pressão e preservar caminho financeiro.
- **TASK-US-197-05 — Chaos/soak** (`tests`, `docs/readiness`)
  - **SUBTASK-TASK-US-197-05-01:** injetar gaps, timeout, DB failure, clock drift e duplicatas.
  - **SUBTASK-TASK-US-197-05-02:** verificar ausência de submit inseguro e recovery bounded.
  - **SUBTASK-TASK-US-197-05-03:** anexar relatório de soak e residual risks.

## US-198 — Provar observabilidade e autonomia

**Valor:** uma decisão, ordem, falha ou intervenção pode ser reconstruída com evidência auditável.  
**Dependências:** US-190…US-197.

**AC-AUTONOMY-10 — Given** qualquer ciclo ou falha; **When** auditoria/operador consulta os dados; **Then** encontra cadeia completa por DecisionId, versões, motivos, métricas, transições, evidência e intervenção, sem expor segredo.

### Tasks

- **TASK-US-198-01 — Audit schemas** (`src/CryptoTrading.Domain/Audit`, `src/CryptoTrading.Persistence`)
  - **SUBTASK-TASK-US-198-01-01:** definir eventos de decisão, risco, OMS, reconciliação e runtime.
  - **SUBTASK-TASK-US-198-01-02:** aplicar retenção append-only e hash de payload.
  - **SUBTASK-TASK-US-198-01-03:** testar integridade e ausência de credenciais.
- **TASK-US-198-02 — Metrics/traces/logs** (`src/CryptoTrading.Worker`, `src/CryptoTrading.Observability`)
  - **SUBTASK-TASK-US-198-02-01:** instrumentar latência, age, gaps, outbox, fills, drift e health.
  - **SUBTASK-TASK-US-198-02-02:** manter IDs de alta cardinalidade fora de labels.
  - **SUBTASK-TASK-US-198-02-03:** testar export e fallback sem bloquear trading seguro.
- **TASK-US-198-03 — Alertas e SLOs** (`docs/observability`, `docker-compose.yml`)
  - **SUBTASK-TASK-US-198-03-01:** definir SLOs de decision, reconciliation e unknown orders.
  - **SUBTASK-TASK-US-198-03-02:** criar alertas SafeMode, mismatch, lag e intervenção.
  - **SUBTASK-TASK-US-198-03-03:** validar alertas com dados sintéticos.
- **TASK-US-198-04 — Decision reconstruction** (`tools`, `src/CryptoTrading.Persistence`)
  - **SUBTASK-TASK-US-198-04-01:** criar comando para reconstruir ciclo por DecisionId.
  - **SUBTASK-TASK-US-198-04-02:** incluir versões, hashes, inputs, outputs e eventos.
  - **SUBTASK-TASK-US-198-04-03:** testar replay sem side effects.
- **TASK-US-198-05 — Evidence bundle** (`docs/readiness`, `plan/templates`)
  - **SUBTASK-TASK-US-198-05-01:** gerar checklist de gate e comandos observados.
  - **SUBTASK-TASK-US-198-05-02:** contabilizar intervenções e exceções humanas.
  - **SUBTASK-TASK-US-198-05-03:** publicar bundle versionado e atualizar traceability.

## US-199 — Promover A2 por ladder controlada

**Valor:** Paper, Shadow e Testnet são promovidos por evidência objetiva, com rollback claro.  
**Dependências:** US-190…US-198. **Não inclui:** promoção live automática.

**AC-AUTONOMY-11 — Given** uma fase A2 concluída; **When** os gates da próxima fase são avaliados; **Then** só há promoção com critérios objetivos, evidence bundle, aprovação requerida, capability correta e rollback testado.

### Tasks

- **TASK-US-199-01 — Paper readiness** (`docs/readiness`, `tests`)
  - **SUBTASK-TASK-US-199-01-01:** definir dados mínimos, warmup, no-trade e PnL ledger.
  - **SUBTASK-TASK-US-199-01-02:** executar replay/paper com custos e slippage.
  - **SUBTASK-TASK-US-199-01-03:** anexar PRM e bloquear se houver lacunas.
- **TASK-US-199-02 — Shadow soak** (`src/CryptoTrading.Worker`, `docs/readiness`)
  - **SUBTASK-TASK-US-199-02-01:** desligar capability de submit mantendo rastreio completo.
  - **SUBTASK-TASK-US-199-02-02:** executar soak/chaos e verificar intervenção zero não planejada.
  - **SUBTASK-TASK-US-199-02-03:** registrar relatório de duração, incidentes e divergências.
- **TASK-US-199-03 — Testnet lifecycle** (`scripts`, `tests`, `docs/runbooks`)
  - **SUBTASK-TASK-US-199-03-01:** exercitar submit/query/cancel/partial fill na Testnet.
  - **SUBTASK-TASK-US-199-03-02:** validar user stream, reconciliation e canWithdraw=false.
  - **SUBTASK-TASK-US-199-03-03:** armazenar evidências sem chaves ou saldos sensíveis.
- **TASK-US-199-04 — Go/no-go e rollback** (`docs/readiness`, `plan/templates`)
  - **SUBTASK-TASK-US-199-04-01:** preencher Promotion Readiness Matrix com observados.
  - **SUBTASK-TASK-US-199-04-02:** validar rollback para SafeMode/versão anterior.
  - **SUBTASK-TASK-US-199-04-03:** exigir aprovação explícita para qualquer capability financeira.
- **TASK-US-199-05 — Release/post-release** (`docs/releases`, `plan/traceability.yaml`)
  - **SUBTASK-TASK-US-199-05-01:** gerar Release Evidence e post-release report.
  - **SUBTASK-TASK-US-199-05-02:** registrar residual risks, incidentes e próximos gates.
  - **SUBTASK-TASK-US-199-05-03:** fechar somente com validação, revisão e rollback demonstrados.

## Dependências e ordem sugerida

```text
US-190 → US-191 → US-192 → US-193 → US-194 → US-195 → US-196
   └──────────────────────→ US-197 → US-198 → US-199
```

Tasks de contratos, schemas, testes de domínio e documentação podem ser paralelizados entre histórias; migrations, adapters e testes de integração devem respeitar a ordem acima para evitar writes concorrentes incompatíveis.
