# 31 — Complemento de implementação: runtime autônomo fechado (A2)

**Status:** planned / implementação incremental
**Data-base:** 2026-09-20
**Épico:** E-19 — Closed-loop Autonomous Runtime Integration
**Escopo:** completar o caminho executável entre market data, decisão, risco, execução, projeções, reconciliação e operação segura.

## 1. Definição operacional

Para este produto, “100% autônomo” significa operação rotineira sem intervenção humana dentro do envelope aprovado: coletar dados, decidir `NO_TRADE` ou exposição, dimensionar, validar risco, enviar, acompanhar, reconciliar, registrar resultados e degradar/recuperar-se automaticamente. Não significa lucro garantido, risco ilimitado ou ausência de governança. Hard limits, credenciais, promoção para capital real, alteração de políticas e reset do `KillSwitch` continuam ações humanas autorizadas.

LLM, RAG, MCP e pesquisa ficam fora do hot path financeiro. Podem gerar hipóteses, documentação e candidatos versionados, mas nunca autorizam ou enviam uma ordem.

## 2. Estado global do runtime

```text
Stopped → Bootstrapping → Reconciling → WarmingUp → Ready ↔ Degraded
                                      ↓             ↓
                                   SafeMode ←───────┘
                                      ↓
                                  KillSwitch → Stopping
```

- Somente `Ready` cria nova exposição.
- `Degraded` só executa ações permitidas pela policy; `SafeMode` bloqueia entradas.
- Startup, reconnect, estado desconhecido e retomada passam por reconciliação.
- `KillSwitch` não possui reset automático.
- Toda transição tem revisão monotônica, motivo, causation/correlation ID, versão de configuração e evento de auditoria.

## 3. Loop A2 decision-complete

```text
Binance public stream → normalize/dedupe → data-quality gate
→ candle fechado/features → baseline Trend Following
→ StrategySignal → target/sizing → RiskEngine independente
→ OrderIntent + RiskDecision + outbox (uma transação)
→ ExecutionPlanner → OMS → Binance Testnet
→ REST ack/user-data/fills → projections/ledger/PnL
→ reconciliation → StrategyHealth → próximo ciclo
```

Invariantes:

1. A2 começa em BTCUSDT Spot, long-only e candle fechado, conforme a Strategy Spec.
2. Um ciclo é único por `environment + account + symbol + strategyVersion + candleClose`.
3. Repetição reutiliza `DecisionId`; nunca duplica uma ordem ainda não terminal.
4. Toda etapa pode retornar `NoTrade(reasonCodes)`; incerteza nunca vira sinal implícito.
5. Redução/saída tem prioridade. Em Spot não existe `reduce-only` nativo: venda não excede saldo BTC livre comprovado.

## 4. Contratos e envelope

As interfaces devem ser introduzidas nos projetos de domínio/aplicação, com adapters separados para Binance e PostgreSQL:

```csharp
IMarketDataPipeline.PublishAsync(MarketDataEnvelope)
IDataQualityGate.Evaluate(MarketDataSnapshot)
IFeatureEngine.Build(ClosedMarketSnapshot)
IStrategy.Evaluate(StrategyContext)
IPortfolioTargetBuilder.Build(StrategySignal, AccountSnapshot)
IRiskEngine.Evaluate(PortfolioTarget, RiskContext, RiskPolicy)
IExecutionPlanner.Plan(AuthorizedTarget, ExecutionContext)
IOrderManagementService.ApplyAsync(OrderLifecycleEvent)
IAccountStateReader.GetConsistentSnapshotAsync()
IReconciliationCoordinator.ReconcileAsync(ReconciliationTrigger)
IAutonomyCycle.RunAsync(DecisionTrigger)
IRuntimeStateCoordinator.TransitionAsync(...)
```

Cada entrada e saída carrega schema version, `DecisionId`, correlation/causation, timestamps UTC (event/available/received), environment/account/symbol, strategy/policy/config/feature/code versions, qualidade/freshness/confidence e payload hash. `ClientOrderId` deriva de `DecisionId + leg + executionAttempt`; nova tentativa exige estado terminal ou ausência comprovada.

## 5. Persistência e OMS

Antes do primeiro I/O mutável da exchange, uma transação PostgreSQL grava decision cycle, signal, target, `RiskDecision`, `OrderIntent`, estado inicial, audit event e outbox. O desenho usa journal crítico append-only e projeções reconstruíveis, não Event Sourcing integral.

Projeções/checkpoints necessários: runtime, market-data sequence, decision cycles, signals/targets, risk decisions, orders/transitions/fills, balances/positions, ledger/valuation, StrategyHealth, reconciliation e inbox de eventos.

OMS obrigatório:

```text
Created → Validated → Authorized → Queued → Submitting
→ Acknowledged/New → PartiallyFilled → Filled
                         ↘ CancelPending → Cancelled
→ Rejected | Expired | Unknown
```

`Unknown` bloqueia novas entradas no símbolo; timeout consulta por `ClientOrderId`; não há retry cego. Duplicatas são idempotentes, eventos atrasados não regressam estados terminais, e o outbox só é concluído após resultado externo estar projetado de forma durável.

## 6. Market data, execução e risco

Market data precisa de reconnect/backoff, sequence/gap/dedup, snapshot + delta do book, candle fechado, bid/ask, spread, depth, VWAP, staleness, clock drift, checkpoint e replay.

ExecutionPlanner usa passive limit por padrão, TTL/cancel/replan e residual de partial fill. Market só é permitida com snapshot de preço recente, depth suficiente e bound conservador de notional/slippage. `exchangeInfo`, saldo e fee reserve são revalidados imediatamente antes do submit.

Execução Testnet exige persistence/migrations, RiskPolicy ativa/aprovada/não expirada, reconciliação e account stream, quality gate saudável, operational control, ausência de ordens desconhecidas, withdrawals desabilitados e UniversePolicy compatível.

Cada entrada verifica notional, exposição pós-ordem, saldo/fee reserve, risco por trade, daily loss/drawdown, turnover/fees, spread/slippage/volatilidade/liquidez, idade do dado, clock, rate limit, saúde de DB/fila/reconciliação/estratégia e edge líquido. Risk pode reduzir ou rejeitar, nunca aumentar target.

## 7. Falhas e self-healing

| Falha | Ação automática |
|---|---|
| Dados stale/gap | bloquear entradas, reconstruir stream/book e aquecer |
| Account stream indisponível | SafeMode, reconnect e REST reconciliation |
| DB/outbox indisponível | nenhum submit; SafeMode |
| Submit/cancel ambíguo | consulta por client ID, reconcilia, sem retry cego |
| Clock drift | bloquear submit e ressincronizar |
| 429/418 | respeitar Retry-After, breaker e reduzir carga |
| Evento duplicado/fora de ordem | inbox dedupe sem regressão de OMS |
| Strategy exception/timeout | NoTrade, degradar health |
| RiskPolicy inválida/expirada | SafeMode |
| Divergência de reconciliação | SafeMode e ajuste compensatório evidenciado |
| Fila saturada/recursos críticos | bloquear entradas, preservar OMS/reconciliação |
| KillSwitch | bloquear primeiro; tratar ordens em voo conforme policy |

## 8. Histórias e critérios de aceite

O backlog executável está em [`ClickUp/07-autonomous-runtime-completion.md`](../ClickUp/07-autonomous-runtime-completion.md). As dez histórias são US-190…US-199. Cada uma possui cinco tasks, subtasks explícitas, arquivos permitidos, invariantes, comandos e condição de não concluído.

| Story | Resultado verificável | AC |
|---|---|---|
| US-190 | lifecycle persistido e readiness derivada | AC-AUTONOMY-02 |
| US-191 | market-data acionável, fresco e reproduzível | AC-AUTONOMY-03 |
| US-192 | baseline fecha decisão→target→risk→outbox | AC-AUTONOMY-04 |
| US-193 | risk obrigatório e independente | AC-AUTONOMY-05 |
| US-194 | OMS, fills, ledger e projeções idempotentes | AC-AUTONOMY-06 |
| US-195 | user stream e reconciliação coexistem em execução | AC-AUTONOMY-07 |
| US-196 | planner determinístico sem overfill | AC-AUTONOMY-08 |
| US-197 | falhas recuperam ou entram em fail-safe | AC-AUTONOMY-09 |
| US-198 | autonomia pode ser reconstruída por evidência | AC-AUTONOMY-10 |
| US-199 | Paper→Shadow→Testnet tem gates e rollback | AC-AUTONOMY-11 |

## 9. Milestones e ordem de implementação

1. **M0/P1:** permitir Testnet + account stream + reconciliation simultaneamente; tornar RiskPolicy obrigatória; projetar ack/fill/cancel.
2. **M1/A2 Paper:** market data → decisão → risco → Paper OMS/ledger.
3. **M2/A2 Shadow:** pipeline completo sem capability de submit; soak e chaos.
4. **M3/A2 Testnet:** execução real Testnet com OMS, user stream e reconciliação.
5. **M4/A2 estável:** recuperação sem intervenção rotineira e evidence bundle.
6. **M5/A3:** regime + duas estratégias isoladamente aprovadas.
7. **M6/A4:** allocation adaptativa bounded.
8. **M7/A5:** Research/Learning Runner separado, champion/challenger e promoção controlada.

Nenhum milestone superior começa sem testes, rastreabilidade, evidência, rollback e go/no-go do anterior.

## 10. A3–A5 fora do hot path

A3 usa `MarketContext` determinístico, `Unknown`, eligibility, netting e hysteresis. A4 aloca somente estratégias aprovadas e conserva budgets/caps; jamais altera hard limits. A5 executa em `Research/Learning Runner`: outcomes → drift → candidate → OOS/walk-forward/stress → challenger → paper → shadow → canary → champion/rejected. Hashes de dataset/features/config/seed/commit são obrigatórios; falha parcial não promove. LLM só pode gerar hipótese sem privilégio.

## 11. Observabilidade e test ladder

Auditar runtime transitions, data rejection, decision cycle, signal, no-trade, target, risk outcomes, order lifecycle, ledger, reconciliation, health, SafeMode, KillSwitch e promotion/rollback. Medir idade/gaps/reconnect, duração/no-trade, outbox lag, submit/ack/fill/cancel latency, fills/shortfall, unknown orders, drift, PnL/DD, health e intervenções.

Test ladder: architecture → unit/property → state machine → PostgreSQL → protocol → replay/backtest → OOS/stress → Paper → Shadow → Testnet → chaos/soak → Canary/live. Evidência financeira é 100% retida; traces podem ser amostradas.

## 12. Handoff para implementação

Cada cartão deve ser executado na ordem `contrato → migration/store → implementação pura → adapter/host → unit → integration → observability → docs/evidence`. O modelo executor deve parar se qualquer invariant, arquivo permitido, comando de validação ou condição de segurança não for atendido; não deve compensar lacunas com mocks silenciosos.
