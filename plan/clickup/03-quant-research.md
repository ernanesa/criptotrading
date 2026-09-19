# ClickUp Manifest — Quant, Risk, Research, Execution e Autonomia

## Uso e governança

Este documento é o manifesto de publicação para os épicos `E-04`, `E-05`, `E-06`, `E-07`, `E-08`, `E-09` e `E-17`. Ele não comprova implementação e não autoriza capital real. Os IDs existentes são preservados.

Decisões de classificação:

- `US-055` pertence a `E-05` e cobre o RAG local do Research Plane;
- `US-073` pertence a `E-06` e cobre Strategy Health da única baseline do R1.0;
- `US-077` pertence a `E-07`, pós-R1.0, e cobre Strategy Health comparativo/multi-strategy;
- `US-062`, `US-063`, `US-064`, todo `E-07` e todo `E-17` ficam `Deferred` até os gates declarados;
- `E-08` começa determinístico; adaptação aprendida permanece fora do R1.0;
- `E-09` não autoriza canary/live sem aprovação explícita dos artefatos obrigatórios.

Campos comuns no ClickUp: tipo, ID, parent, release, status, prioridade, plane, bounded context, risco, source/hash, RF/RNF, AC, dependências, evidência, residual-risk owner e approval authority.

Itens da seção ACTIVE publicam inicialmente com status `Proposed`; itens pós-R1.0 publicam como `Deferred/Option`. `Source Commit/Hash` é injetado do `HEAD` limpo no momento da publicação, e qualquer RF/RNF, AC, threshold ou owner ainda não aprovado permanece `Not defined`, bloqueando `Ready`.

DoD comum: ACs demonstrados; testes e comandos com saída anexada; hashes de código/dados/configuração registrados; telemetria e falhas cobertas; manifesto atualizado; revisão independente em itens de Risk/Execution/Autonomy; rollback e risco residual documentados.

---

# ACTIVE — MVP/R1.0

## E-04 — Risk Engine

**Outcome:** nenhuma exposição é criada sem decisão de risco independente, versionada, reproduzível e auditável.

### US-040 — Avaliar RiskPolicy versionada antes de qualquer ordem

**Objetivo/valor:** aplicar limites consistentes e impedir que Strategy, Selector ou operador não autorizado ampliem risco.
**Escopo:** lifecycle da policy; exposição por ativo/estratégia/portfólio; daily loss; drawdown; valuation/freshness; decisão estruturada.
**Não escopo:** otimização autônoma de limites; derivativos; aprovação de canary.
**Regras/contratos/falhas:** `Approved|Adjusted|Rejected|EmergencyBlocked`; Risk nunca aumenta target; decisão carrega policy/config version e reason codes; valuation incerta resulta em `NO_NEW_ENTRIES`; policy inválida/expirada falha fechada.

**ACs**

- Given target acima do limite, When Risk o avalia, Then retorna Adjusted/Rejected e nenhuma ordem excede o autorizado.
- Given valuation stale ou policy expirada, When a exposição não pode ser comprovada, Then bloqueia novas entradas e emite audit/alert.

**Dependências:** US-011, US-012, US-013, US-014; RF-070, RF-071, RF-072, RF-073, RF-074, RF-075; AC-RISK-01, AC-RISK-02, AC-RISK-03.
**Testes/evidência:** properties `risk-never-increases`; stale valuation; lifecycle/supersession; `EVIDENCE-TASK-US-040-*`.

- `TASK-US-040-01` — Modelar e persistir RiskPolicy.
  - `SUBTASK-TASK-US-040-01-01` — Definir schema, unidades, janelas, escopos, hash e lifecycle.
  - `SUBTASK-TASK-US-040-01-02` — Impedir activation com campo obrigatório, validade ou aprovação ausente.
- `TASK-US-040-02` — Implementar pipeline de decisão auditável.
  - `SUBTASK-TASK-US-040-02-01` — Avaliar exposure, loss, drawdown e valuation em decimal exato.
  - `SUBTASK-TASK-US-040-02-02` — Persistir decisão, razões, versões e métricas de outcome.

### US-041 — Dimensionar posição por risco e invalidação

**Objetivo/valor:** derivar quantidade do risco aceito, não de quantidade fixa.
**Escopo:** fixed-fractional, ajuste por volatilidade, invalidação, saldo, fee reserve e filtros.
**Não escopo:** full Kelly, ML sizing, leverage.
**Regras/falhas:** cálculo decimal determinístico; arredondamento nunca amplia risco; abaixo de mínimo gera rejeição; stop inválido, saldo/valuation incerto ou volatilidade ausente falham fechados.

**ACs**

- Given mesmo estado, policy e relógio, When sizing executa, Then produz resultado idêntico.
- Given rounding, fee reserve ou saldo insuficiente, When a quantidade é normalizada, Then nunca supera risco/notional autorizado.

**Dependências:** US-020, US-040; RF-070; AC-EXCHANGE-01.
**Testes/evidência:** boundary/property tests para zero, máximos, step size e min notional.

- `TASK-US-041-01` — Implementar sizing determinístico.
  - `SUBTASK-TASK-US-041-01-01` — Calcular capital em risco e distância de invalidação.
  - `SUBTASK-TASK-US-041-01-02` — Aplicar ajuste de volatilidade e caps da policy.
- `TASK-US-041-02` — Normalizar quantidade para a exchange.
  - `SUBTASK-TASK-US-041-02-01` — Aplicar saldo, fee reserve, lot e notional filters.
  - `SUBTASK-TASK-US-041-02-02` — Cobrir residual pós-partial-fill e rejeições estruturadas.

### US-042 — Interromper exposição por protections e circuit breakers

**Objetivo/valor:** conter perdas e incerteza antes que virem ordens.
**Escopo:** breakers strategy/symbol/exchange/global; guards financeiros, mercado e operacionais; reset/ack.
**Não escopo:** kill switch manual; autoalteração de threshold.
**Regras/falhas:** cada abertura registra causa, observado, threshold e ação; normalização não reseta automaticamente quando approval é exigido; ordem em voo segue policy; falha de telemetria não reabre breaker.

**ACs**

- Given guard acima do stop, When chega target, Then nenhuma nova exposição é criada no escopo afetado.
- Given breaker aberto, When a condição normaliza sem reset autorizado, Then permanece aberto e auditável.

**Dependências:** US-040, US-111, US-114; RF-074, RF-075, RF-076, RF-077, RF-078, RF-079, RF-080.
**Testes/evidência:** transitions, restart, concorrência, propagation e alert-delivery.

- `TASK-US-042-01` — Implementar protections financeiras e de mercado.
  - `SUBTASK-TASK-US-042-01-01` — Daily loss, drawdown, streak, spread, volatility e liquidity.
  - `SUBTASK-TASK-US-042-01-02` — Testar thresholds, escopos e reason codes.
- `TASK-US-042-02` — Implementar lifecycle operacional dos breakers.
  - `SUBTASK-TASK-US-042-02-01` — Clock, exchange, stale data, reconciliation e queue pressure.
  - `SUBTASK-TASK-US-042-02-02` — Persistir reset/ack, alertas e comportamento de ordens em voo.

### US-043 — Acionar kill switch por caminho independente

**Objetivo/valor:** bloquear imediatamente novas exposições mesmo com Strategy ou UI principal indisponível.
**Escopo:** comando idempotente, caminho independente, cancel policy, in-flight orders, step-up/reset e drills.
**Não escopo:** liquidação automática não configurada.
**Regras/falhas:** bloqueio precede cancelamento; cancelamento ambíguo mantém bloqueio e reconcilia; reset exige autorização; a ação sempre gera incidente/audit.

**ACs**

- Given UI/API ou Strategy indisponível, When kill switch é acionado, Then novas exposições são bloqueadas pelo caminho aprovado.
- Given ordem em voo com cancelamento ambíguo, When o switch atua, Then o sistema mantém bloqueio, reconcilia e não presume cancelamento.

**Dependências:** US-033, US-040, US-113; AC-KILL-01, AC-KILL-02.
**Testes/evidência:** integration drill, concurrent submit, unavailable UI, reset replay.

- `TASK-US-043-01` — Implementar comando e caminho independente.
  - `SUBTASK-TASK-US-043-01-01` — Persistir ativação idempotente antes dos efeitos.
  - `SUBTASK-TASK-US-043-01-02` — Expor caminho operacional autenticado e auditado.
- `TASK-US-043-02` — Executar política e drill.
  - `SUBTASK-TASK-US-043-02-01` — Tratar open/in-flight/ambiguous orders e positions.
  - `SUBTASK-TASK-US-043-02-02` — Provar bloqueio, alerta, reconciliation e reset controlado.

### US-044 — Governar RiskPolicy e PromotionReadinessMatrix

**Objetivo/valor:** impedir aprovações vagas ou expiradas.
**Escopo:** schema, thresholds, unidades, janelas, validade, approvals, supersession e evidence links.
**Não escopo:** escolher valores sem dados ou aprovar capital.
**Regras/falhas:** `TBD`, “adequado” ou campo vazio impedem approval; artefato expirado falha fechado; alteração cria versão; learner não aprova policy.

**ACs**

- Given artefato com threshold sem valor/unidade/janela, When approval é solicitado, Then a transição é recusada.
- Given artefato superseded/expired, When usado por decisão, Then a decisão é bloqueada e aponta a versão ativa.

**Dependências:** US-040, US-053, US-054; AC-RISK-03, AC-VALIDATION-01.
**Testes/evidência:** schema validation, expiry clock, authorization, immutable history.

- `TASK-US-044-01` — Validar e versionar artefatos.
  - `SUBTASK-TASK-US-044-01-01` — Validar campos, unidades, janelas, owner e validade.
  - `SUBTASK-TASK-US-044-01-02` — Implementar supersession, hash e links de evidência.
- `TASK-US-044-02` — Controlar approval e expiração.
  - `SUBTASK-TASK-US-044-02-01` — Aplicar autorização/step-up e segregation of duties.
  - `SUBTASK-TASK-US-044-02-02` — Testar expiry, revoke e fallback seguro.

## E-05 — Research Data, Backtesting e Knowledge Retrieval

**Outcome:** decisões de pesquisa são reproduzíveis, temporalmente corretas e resistentes a overfitting.

### US-050 — Produzir datasets reproduzíveis

**Objetivo/valor:** permitir reconstrução exata e impedir survivorship/leakage.
**Escopo:** import, QA, Parquet, temporalidade, Dataset Card, hashes, supersession e lineage.
**Não escopo:** data lake cloud; coleta L3 indiscriminada.
**Regras/falhas:** raw append-only; `available_at`; quarantine; correção cria nova versão; licença/proveniência ausente bloqueia eligibility.

**ACs**

- Given mesma fonte/config, When dataset é reconstruído, Then hash e conteúdo são idênticos.
- Given correção tardia, When publicada, Then a versão anterior permanece imutável e consumidores afetados são listados.

**Dependências:** US-020, US-023, US-024; AC-DATA-01, AC-UNIVERSE-01.
**Testes/evidência:** Dataset Card, data QA report, checksums, point-in-time replay.

- `TASK-US-050-01` — Importar, normalizar e particionar.
  - `SUBTASK-TASK-US-050-01-01` — Preservar timestamps, source/schema version e checksum.
  - `SUBTASK-TASK-US-050-01-02` — Particionar Parquet e registrar geração determinística.
- `TASK-US-050-02` — Governar qualidade e lineage.
  - `SUBTASK-TASK-US-050-02-01` — Detectar gaps, duplicates, reorder, outliers e quarantine.
  - `SUBTASK-TASK-US-050-02-02` — Publicar Dataset Card, supersession e consumer impact.

### US-051 — Executar backtest event-driven com custos e fills

**Objetivo/valor:** rejeitar estratégias cujo edge desaparece sob execução realista.
**Escopo:** clock/replay, simulated exchange L1, fees, spread, slippage, latency, partial fills, rejects e filtros.
**Não escopo:** L3/queue model; garantia de performance live.
**Regras/falhas:** core compartilhado com produção; ambiguidade intrabar usa pior sequência plausível ou invalida conclusão; PnL líquido obrigatório; seed/config/version determinísticos.

**ACs**

- Given mesmo code/dataset/config/seed, When backtest repete, Then resultado respeita tolerância versionada.
- Given stop e target possíveis no mesmo candle, When não há sequência observável, Then aplica regra conservadora declarada ou rejeita a conclusão.

**Dependências:** US-050, US-060, US-040; AC-BACKTEST-01, AC-COST-01, AC-PARITY-01.
**Testes/evidência:** synthetic datasets, fee assets, partial fills, no-fill, invalid filter.

- `TASK-US-051-01` — Implementar event loop e simulated exchange.
  - `SUBTASK-TASK-US-051-01-01` — Integrar replay clock, order lifecycle e deterministic queue.
  - `SUBTASK-TASK-US-051-01-02` — Implementar regra intrabar e fixtures sintéticas.
- `TASK-US-051-02` — Modelar execução e custos.
  - `SUBTASK-TASK-US-051-02-01` — Fee, spread, slippage, latency e commission valuation.
  - `SUBTASK-TASK-US-051-02-02` — Partial fill, reject, rounding, balance e min-notional residual.

### US-052 — Produzir analytics versionados e completos

**Objetivo/valor:** comparar resultados sem ambiguidade de fórmula ou unidade.
**Escopo:** returns, risk, trade, exposure, execution/cost attribution, uncertainty e ruin.
**Não escopo:** recomendação financeira; seleção automática por uma métrica isolada.
**Regras/falhas:** cada métrica declara fórmula/frequência/annualization/risk-free/unit; NaN/amostra insuficiente é explícita; bruto e líquido separados.

**ACs**

- Given relatório, When uma métrica é exibida, Then sua definição/version e unidade são recuperáveis.
- Given amostra insuficiente, When intervalo/ratio não é defensável, Then o resultado é `InsufficientEvidence`, não zero.

**Dependências:** US-051; RF-120, RF-121, RF-122, RF-123, RF-124.
**Testes/evidência:** golden calculations, dimensional/unit tests, attribution reconciliation.

- `TASK-US-052-01` — Implementar catálogo e métricas.
  - `SUBTASK-TASK-US-052-01-01` — Versionar formulas de retorno, DD e risk-adjusted ratios.
  - `SUBTASK-TASK-US-052-01-02` — Calcular expectancy, MAE/MFE, turnover, exposure e time-under-water.
- `TASK-US-052-02` — Implementar attribution e incerteza.
  - `SUBTASK-TASK-US-052-02-01` — Separar fees, spread, slippage, latency e benchmark delta.
  - `SUBTASK-TASK-US-052-02-02` — Calcular CI/lower bound, losing streak, tail e risk of ruin.

### US-053 — Validar robustez temporal e estatística

**Objetivo/valor:** detectar leakage, pico paramétrico e fragilidade de regime.
**Escopo:** temporal split, purging/embargo, walk-forward, sensitivity, stress/Monte Carlo, PBO/DSR.
**Não escopo:** converter heurísticas estatísticas em garantia.
**Regras/falhas:** sem random shuffle; treino usa apenas passado; pico estreito não promove; resultado in-sample isolado é inelegível.

**ACs**

- Given janela de avaliação, When modelo/parameters são escolhidos, Then somente dados anteriores são usados.
- Given custos/stress adversos, When gates falham, Then candidate é Rejected/Iterate e não tem status promocional.

**Dependências:** US-051, US-052, US-054; AC-VALIDATION-01.
**Testes/evidência:** leakage sentinels, walk-forward folds, surfaces e stress report.

- `TASK-US-053-01` — Implementar validação temporal.
  - `SUBTASK-TASK-US-053-01-01` — Splits, warmup, purging/embargo e availability checks.
  - `SUBTASK-TASK-US-053-01-02` — Walk-forward com selection somente na janela passada.
- `TASK-US-053-02` — Implementar robustez e stress.
  - `SUBTASK-TASK-US-053-02-01` — Sensitivity surface, trial-aware PBO/DSR e sample validity.
  - `SUBTASK-TASK-US-053-02-02` — Monte Carlo/bootstrap, fee/slippage/liquidity shocks.

### US-054 — Governar preregistration, experiment family e holdout

**Objetivo/valor:** tornar seleção bias e múltiplas tentativas visíveis.
**Escopo:** hypothesis, search space, trial budget, benchmark, rejection rule, locked holdout, ablation e Experiment Card.
**Não escopo:** impedir pesquisa exploratória; exploratório apenas não promove.
**Regras/falhas:** todos os trials contam; acesso auditado; contaminação cria nova versão; benchmark pré-holdout.

**ACs**

- Given experimento promocional, When começa, Then preregistration existe antes de qualquer resultado.
- Given acesso indevido ao holdout, When detectado, Then ele é marcado contaminado e não serve ao gate.

**Dependências:** US-050, US-051, US-052; AC-RESEARCH-01.

**Sequência:** a governança desta história precede a execução promocional de US-053.
**Testes/evidência:** immutable timestamp, access audit, budget exhaustion e contamination flow.

- `TASK-US-054-01` — Registrar família e plano experimental.
  - `SUBTASK-TASK-US-054-01-01` — Persistir hypothesis, search space, metrics, benchmark e rejection.
  - `SUBTASK-TASK-US-054-01-02` — Contabilizar trials, variantes descartadas e deviations.
- `TASK-US-054-02` — Proteger holdout e decisão.
  - `SUBTASK-TASK-US-054-02-01` — Auditar access, contaminar e versionar replacement.
  - `SUBTASK-TASK-US-054-02-02` — Persistir Experiment Card, ablations, command, evidence e decision.

### US-055 — Recuperar conhecimento local com RAG citado e seguro

**Objetivo/valor:** reduzir contexto e inconsistência sem transformar o índice em autoridade ou permissão.
**Escopo:** SQLite/FTS5; embeddings locais opcionais; ingestão incremental; graph relations; authority/freshness; citations; evals.
**Não escopo:** hot path, ordem, RiskPolicy, credenciais, publicação externa ou vector DB hospedado.
**Regras/falhas:** fallback lexical; source/hash/status obrigatórios; superseded fora do retrieval ativo; conflito exibido; índice corrompido pode ser reconstruído; claim sem fonte é `não confirmado`.

**ACs**

- Given consulta, When contexto é recuperado, Then cita path/linha/hash e prioriza fonte canônica vigente.
- Given embeddings indisponíveis ou índice stale, When consultado, Then usa fallback FTS5 ou recusa recomendação sem inventar evidência.

**Dependências:** US-015; AC-RAG-01, AC-TRACE-01.
**Testes/evidência:** canonical recall, stale-source rate, unsupported claims, adversarial prompt e rebuild.

- `TASK-US-055-01` — Implementar ingestão e índice local.
  - `SUBTASK-TASK-US-055-01-01` — Hash/chunk/metadados, FTS5 e invalidation incremental.
  - `SUBTASK-TASK-US-055-01-02` — Extrair IDs/edges explícitos, supersession e rebuild determinístico.
- `TASK-US-055-02` — Implementar retrieval e avaliação.
  - `SUBTASK-TASK-US-055-02-01` — Authority/status/freshness filters, citations e conflict report.
  - `SUBTASK-TASK-US-055-02-02` — Executar eval set, token/cache metrics e prompt-injection cases.

## E-06 — Strategy SDK & Baseline

**Outcome:** uma única baseline Trend BTC/USDT atravessa o pipeline completo sem acesso direto à exchange.

### US-060 — Publicar contrato de Strategy independente da exchange

**Objetivo/valor:** separar hipótese de execução e tornar replay/parity possíveis.
**Escopo:** interface, lifecycle, registry, `AlphaSignal/TradeIntent`, rationale e deterministic clock.
**Não escopo:** order gateway, sizing final, risk approval.
**Regras/falhas:** strategy só emite intent; stale/insufficient data produz ausência de sinal; identity/version obrigatórias.

**ACs**

- Given package Strategy, When architecture test executa, Then não há dependência de ExchangeTrading.
- Given replay igual, When Strategy roda, Then sinais e razões são determinísticos.

**Dependências:** US-010, US-011, US-050; AC-STRATEGY-01.
**Testes/evidência:** architecture/contract/replay tests.

- `TASK-US-060-01` — Definir contrato e registry.
  - `SUBTASK-TASK-US-060-01-01` — Modelar signal, horizon, invalidation, timestamps e identity.
  - `SUBTASK-TASK-US-060-01-02` — Versionar lifecycle, parameters e status.
- `TASK-US-060-02` — Provar isolamento e determinismo.
  - `SUBTASK-TASK-US-060-02-01` — Criar architecture tests contra gateway/adapter.
  - `SUBTASK-TASK-US-060-02-02` — Testar warmup, stale input, replay e rationale.

### US-061 — Validar Trend Following BTC/USDT como baseline

**Objetivo/valor:** provar a cadeia end-to-end com hipótese simples e falsificável.
**Escopo:** uma variante explicável; Strategy Spec; backtest/OOS/walk-forward/stress; paper/shadow handoff.
**Não escopo:** multi-asset, ML, grid, futures ou promessa de retorno.
**Regras/falhas:** custos conservadores; trials registrados; failure modes explícitos; falha de gate rejeita/itera, nunca ajusta retrospectivamente em silêncio.

**ACs**

- Given candidate, When pede promoção, Then hashes, custos, holdout, robustez e thresholds estão completos.
- Given OOS/cost/stress abaixo do gate, When decisão é registrada, Then não recebe elegibilidade promocional.

**Dependências:** US-040, US-050, US-051, US-052, US-053, US-054, US-060; AC-VALIDATION-01.
**Testes/evidência:** Strategy Spec, Experiment Cards, reports, parity tests.

- `TASK-US-061-01` — Especificar e implementar baseline.
  - `SUBTASK-TASK-US-061-01-01` — Preencher hipótese, signal, exit, invalidation, costs e failure modes.
  - `SUBTASK-TASK-US-061-01-02` — Implementar indicator/signal e unit/property tests.
- `TASK-US-061-02` — Validar e decidir.
  - `SUBTASK-TASK-US-061-02-01` — Executar backtest, OOS, walk-forward, sensitivity e stress.
  - `SUBTASK-TASK-US-061-02-02` — Comparar cash/buy-and-hold, ablations e persistir decision/evidence.

### US-073 — Degradar a baseline por Strategy Health

**Objetivo/valor:** reduzir/desabilitar automaticamente a única baseline quando seu comportamento se deteriorar.
**Escopo:** rolling expectancy, drawdown, slippage, parity divergence; states e reabilitação.
**Não escopo:** comparação/alocação entre estratégias, pertencente à US-077.
**Regras/falhas:** `HEALTHY→DEGRADED→SUSPICIOUS→DISABLED`; passagem do tempo não reabilita; input stale reduz confiança; fallback é `NO_TRADE`.

**ACs**

- Given métrica viola envelope, When health é atualizado, Then risk budget reduz ou novas entradas são desabilitadas conforme policy.
- Given strategy Disabled, When apenas tempo passa, Then não volta a Healthy sem evidência/reaprovação.

**Dependências:** US-061, US-040, US-052; R0.4/R1.0.
**Testes/evidência:** transition table, replay, stale metrics, quarantine/re-enable report.

- `TASK-US-073-01` — Calcular health e transições.
  - `SUBTASK-TASK-US-073-01-01` — Implementar janelas, métricas, confidence e threshold versions.
  - `SUBTASK-TASK-US-073-01-02` — Persistir state changes, reasons e decision trace.
- `TASK-US-073-02` — Aplicar degradação e reabilitação.
  - `SUBTASK-TASK-US-073-02-01` — Integrar risk reduction, disable e `NO_TRADE`.
  - `SUBTASK-TASK-US-073-02-02` — Implementar quarantine/re-enable policy e testes de restart.

### US-062 — Momentum/Breakout challengers — DEFERRED pós-R1.0

**Objetivo/valor:** avaliar hipóteses alternativas somente após baseline comprovada.
**Escopo:** specs independentes, sem promoção conjunta. **Não escopo:** multi-strategy imediato.
**Regras/falhas:** cada challenger usa família/holdout próprios e pipeline completo.

**ACs:** Given R1.0 não concluído, When refinamento é solicitado, Then card permanece Deferred. Given gate liberado, When cada challenger é testado, Then decisões/evidências são independentes.
**Dependências:** US-061 concluída e pós-release review.

- `TASK-US-062-01` — Especificar challengers.
  - `SUBTASK-TASK-US-062-01-01` — Criar Strategy Spec Momentum.
  - `SUBTASK-TASK-US-062-01-02` — Criar Strategy Spec Breakout.
- `TASK-US-062-02` — Validar isoladamente.
  - `SUBTASK-TASK-US-062-02-01` — Executar pipeline completo de Momentum.
  - `SUBTASK-TASK-US-062-02-02` — Executar pipeline completo de Breakout.

### US-063 — Mean Reversion challenger — DEFERRED pós-R1.0

**Objetivo/valor:** testar reversão somente em regimes compatíveis.
**Escopo:** z-score/VWAP variant, regime guard e custos. **Não escopo:** ativação sem detector validado.
**Regras/falhas:** low liquidity/trend incompatível gera no-trade; tail/gap stress obrigatório.

**ACs:** Given regime incompatível, When sinal aparece, Then é inelegível. Given custos/tail stress falham, Then candidate é rejeitado.
**Dependências:** US-070 e baseline R1.0.

- `TASK-US-063-01` — Especificar e implementar candidate.
  - `SUBTASK-TASK-US-063-01-01` — Definir regime, signal, invalidation e failure modes.
  - `SUBTASK-TASK-US-063-01-02` — Implementar candidate e deterministic tests.
- `TASK-US-063-02` — Validar robustez.
  - `SUBTASK-TASK-US-063-02-01` — OOS/walk-forward/sensitivity por regime.
  - `SUBTASK-TASK-US-063-02-02` — Stress de gaps, trend e liquidity/costs.

### US-064 — Adaptive Grid challenger — DEFERRED pós-R1.0

**Objetivo/valor:** pesquisar grid bounded sem exposição infinita.
**Escopo:** planner, spacing, inventory/capital caps e simulator. **Não escopo:** DCA infinito ou live sem regime guard.
**Regras/falhas:** hard inventory cap; trending/panic regime desabilita; partial fills e dust modelados.

**ACs:** Given cap atingido, When novo nível é proposto, Then não amplia inventário. Given regime proibido, Then retorna no-trade/cancel policy.
**Dependências:** US-070, US-071 e simulator adequado.

- `TASK-US-064-01` — Implementar planner bounded.
  - `SUBTASK-TASK-US-064-01-01` — Definir arithmetic/geometric spacing e caps.
  - `SUBTASK-TASK-US-064-01-02` — Tratar partial fills, dust e cancel lifecycle.
- `TASK-US-064-02` — Validar failure modes.
  - `SUBTASK-TASK-US-064-02-01` — Simular trend, gap, inventory saturation e depeg.
  - `SUBTASK-TASK-US-064-02-02` — Executar cost/capacity/sensitivity report.

## E-08 — Execution Quality

**Outcome:** converter target autorizado em execução mensurável sem exceder exposição.

### US-080 — Bloquear ordens sem edge líquido mínimo

**Objetivo/valor:** evitar trades cujo custo esperado consome a oportunidade.
**Escopo:** fee/spread/slippage/latency estimate e threshold policy. **Não escopo:** model learned no R1.0.
**Regras/falhas:** estimativa stale/incerta bloqueia ou usa bound conservador; unidade e horizon compatíveis.

**ACs:** Given expected edge abaixo do custo+margin, When avaliado, Then ordem não é criada. Given estimate stale, Then falha fechado conforme policy.
**Dependências:** US-040, US-052.

- `TASK-US-080-01` — Calcular edge/cost bound.
  - `SUBTASK-TASK-US-080-01-01` — Agregar fees, spread, slippage e latency.
  - `SUBTASK-TASK-US-080-01-02` — Versionar threshold, confidence e units.
- `TASK-US-080-02` — Integrar gate e telemetria.
  - `SUBTASK-TASK-US-080-02-01` — Bloquear abaixo do threshold com reason code.
  - `SUBTASK-TASK-US-080-02-02` — Registrar expected/realized e false-positive analysis.

### US-081 — Escolher execução market ou passive limit

**Objetivo/valor:** balancear urgência, fill probability e custo com policy determinística.
**Escopo:** market/passive, stale cancel, fill tracking. **Não escopo:** TWAP/iceberg/adaptive ML.
**Regras/falhas:** nunca excede target após partial fill/retry; cancel ambíguo reconcilia; passive stale não permanece indefinidamente.

**ACs:** Given partial fill e novo target, When replanning ocorre, Then cumulative fill nunca excede autorizado. Given cancel timeout, Then não envia replacement antes de reconciliar.
**Dependências:** US-031, US-032, US-033, US-040, US-080.

- `TASK-US-081-01` — Implementar policy determinística.
  - `SUBTASK-TASK-US-081-01-01` — Definir urgency/spread/liquidity rules.
  - `SUBTASK-TASK-US-081-01-02` — Implementar passive timeout e stale cancel.
- `TASK-US-081-02` — Tratar lifecycle e limites.
  - `SUBTASK-TASK-US-081-02-01` — Partial fill/retry/replan sem overfill.
  - `SUBTASK-TASK-US-081-02-02` — Ambiguous cancel/replacement reconciliation tests.

### US-082 — Atribuir qualidade de execução

**Objetivo/valor:** explicar diferença entre decisão e resultado realizado.
**Escopo:** arrival price, implementation shortfall, fill ratio, latency e expected-vs-realized.
**Não escopo:** atribuir todo market movement à execução.
**Regras/falhas:** timestamps monotônicos/UTC; comissão preserva ativo; missing benchmark é explícito.

**ACs:** Given fill, When attribution roda, Then reconcilia quantity/fees e produz métricas com source/time. Given benchmark ausente, Then não fabrica zero.
**Dependências:** US-013, US-052, US-081.

- `TASK-US-082-01` — Capturar baselines e outcomes.
  - `SUBTASK-TASK-US-082-01-01` — Persistir arrival/decision/submit/ack/fill timestamps e prices.
  - `SUBTASK-TASK-US-082-01-02` — Preservar fill, fee asset e valuation lineage.
- `TASK-US-082-02` — Calcular e reconciliar attribution.
  - `SUBTASK-TASK-US-082-02-01` — Shortfall, slippage, fill ratio e latency decomposition.
  - `SUBTASK-TASK-US-082-02-02` — Comparar estimate/realized e emitir degradation metrics.

### US-083 — Selecionar execução adaptativa bounded — DEFERRED pós-R1.0

**Objetivo/valor:** escolher tática por contexto somente após dados da baseline.
**Escopo:** tactic scorer bounded; market/passive primeiro. **Não escopo:** novas order types sem aprovação.
**Regras/falhas:** Risk/OMS continuam soberanos; exploration budget; fallback determinístico.

**ACs:** Given scorer incerto/indisponível, Then fallback determinístico é usado. Given tactic viola target/policy, Then Risk/OMS bloqueiam.
**Dependências:** US-082, US-172; pós-R1.0.

- `TASK-US-083-01` — Especificar contexto e bounds.
  - `SUBTASK-TASK-US-083-01-01` — Definir features, actions, fallback e exploration budget.
  - `SUBTASK-TASK-US-083-01-02` — Versionar scorer, policy e decision trace.
- `TASK-US-083-02` — Avaliar em replay/shadow.
  - `SUBTASK-TASK-US-083-02-01` — Off-policy/paired comparison contra baseline.
  - `SUBTASK-TASK-US-083-02-02` — Testar drift, uncertainty e rollback.

## E-09 — Paper, Shadow & Canary

**Outcome:** acumular evidência forward sem confundir simulação, Testnet e capital real.

### US-090 — Operar paper trading com mercado real

**Objetivo/valor:** validar pipeline forward com execution simulada.
**Escopo:** virtual balances, shared Strategy/Risk/Execution contracts e evidence.
**Não escopo:** mock contado como Testnet; capital real.
**Regras/falhas:** namespace/banner separado; determinismo possível por replay; simulator explicitamente identificado.

**ACs:** Given PAPER, When worker inicia, Then endpoints, namespace e order mode são coerentes. Given ordem, Then nenhum exchange trading endpoint real é usado.
**Dependências:** US-051, US-061, US-040; AC-ENV-01, AC-PARITY-01.

- `TASK-US-090-01` — Implementar paper adapter e contas.
  - `SUBTASK-TASK-US-090-01-01` — Virtual balances, fills, fees e rejects.
  - `SUBTASK-TASK-US-090-01-02` — Isolar config/namespace e bloquear endpoint de ordem real.
- `TASK-US-090-02` — Produzir parity/evidence.
  - `SUBTASK-TASK-US-090-02-01` — Comparar replay backtest/paper com tolerância versionada.
  - `SUBTASK-TASK-US-090-02-02` — Persistir report, telemetry e known limitations.

### US-091 — Operar shadow sem enviar ordens

**Objetivo/valor:** observar decisões contra mercado live sem efeito financeiro.
**Escopo:** pipeline completo até intent de ordem, comparison report e decision trace.
**Não escopo:** submit/cancel na exchange.
**Regras/falhas:** shadow adapter não possui capability de trading; toda decisão registra counterfactual.

**ACs:** Given SHADOW, When target é autorizado, Then decisão/execução simulada são registradas e nenhum submit ocorre. Given capability incorreta, Then startup falha.
**Dependências:** US-090, US-081, US-073.

- `TASK-US-091-01` — Implementar shadow boundary.
  - `SUBTASK-TASK-US-091-01-01` — Remover capability de exchange order e validar startup.
  - `SUBTASK-TASK-US-091-01-02` — Persistir intent, risk, hypothetical execution e outcome.
- `TASK-US-091-02` — Produzir comparação pareada.
  - `SUBTASK-TASK-US-091-02-01` — Comparar forecast/fill/cost contra observável live.
  - `SUBTASK-TASK-US-091-02-02` — Reportar divergência, coverage e limitations.

### US-092 — Restringir canary por envelope reduzido

**Objetivo/valor:** limitar blast radius quando capital real for explicitamente autorizado.
**Escopo:** reduced symbols/notional/loss/time; abort e rollback.
**Não escopo:** autorização implícita ou promoção por data.
**Regras/falhas:** exige PRM/RiskPolicy/CanaryPlan aprovados; valores numéricos; expiration; qualquer gate crítico falha fechado.

**ACs:** Given artefato ausente/expirado, When CANARY inicia, Then startup/entry é bloqueado. Given abort criterion, Then novas entradas param e plano de in-flight/rollback executa.
**Dependências:** US-043, US-044.

**Sequência:** os controles desta história são entrada para US-134 e, posteriormente, para o gate US-130.

- `TASK-US-092-01` — Aplicar envelope canary.
  - `SUBTASK-TASK-US-092-01-01` — Validar artefatos, validade, symbols, capital e limits.
  - `SUBTASK-TASK-US-092-01-02` — Impedir config acima de live/canary approved envelope.
- `TASK-US-092-02` — Ensaiar abort/rollback.
  - `SUBTASK-TASK-US-092-02-01` — Tratar in-flight/open orders e no-trade fallback.
  - `SUBTASK-TASK-US-092-02-02` — Executar drill e anexar Release/Reconciliation evidence.

### US-093 — Provar operação autônoma prolongada em shadow

**Objetivo/valor:** medir intervenção, recovery e estabilidade antes de canary.
**Escopo:** soak com período/volume/regimes; forced failures; baseline/no-trade/health.
**Não escopo:** exigir multi-strategy no R1.0.
**Regras/falhas:** duração e volume definidos na PRM; restart/reconcile; incident/intervention contabilizados, inclusive zero explícito.

**ACs:** Given soak aprovado, When completa, Then período, volume, regimes, interventions e incidents possuem evidência. Given forced failure, Then recovery não libera entries antes de reconcile.
**Dependências:** US-073, US-091, US-180, US-181.

- `TASK-US-093-01` — Definir/executar soak.
  - `SUBTASK-TASK-US-093-01-01` — Fixar duration, event/trade volume, regimes e pass/fail.
  - `SUBTASK-TASK-US-093-01-02` — Coletar intervention, availability e strategy-health metrics.
- `TASK-US-093-02` — Injetar falhas e revisar.
  - `SUBTASK-TASK-US-093-02-01` — Disconnect, restart, stale data, DB pressure e mismatch.
  - `SUBTASK-TASK-US-093-02-02` — Produzir incident/recovery report e go/no-go.

### US-094 — Validar integração real na Binance Spot Testnet

**Objetivo/valor:** comprovar o adapter de trading, autenticação, lifecycle de ordens e reconciliação contra a Spot Testnet real antes de qualquer capital. **Escopo:** credenciais segregadas, submit/query/cancel, User Data Stream, filtros, rate limits e timeout ambíguo. **Não escopo:** mocks como evidência de integração, Demo Mode ou live. **Falhas:** endpoint/key/mode incompatível falha no startup; resultado desconhecido nunca causa reenvio cego.

**ACs:** Given perfil Testnet válido, When submit/query/cancel executam, Then usam a Binance Spot Testnet real e preservam IDs/estado para reconciliação. Given timeout, 5xx ou lacuna no User Data Stream, When o resultado não puder ser provado, Then novas entradas são bloqueadas, o OMS consulta/reconcilia e nenhuma segunda ordem é enviada cegamente.

**Dependências:** US-003, US-020, US-030, US-031, US-032, US-033; AC-ENV-02, AC-USERDATA-01, AC-OMS-01, AC-OMS-03, AC-RECON-01.

- `TASK-US-094-01` — Preparar e validar o boundary Testnet.
  - `SUBTASK-TASK-US-094-01-01` — Configurar endpoint, key reference, namespace e permissions sem saque.
  - `SUBTASK-TASK-US-094-01-02` — Executar preflight de `exchangeInfo`, server time, filters e rate limits.
- `TASK-US-094-02` — Provar o lifecycle real e as falhas ambíguas.
  - `SUBTASK-TASK-US-094-02-01` — Exercitar submit/query/partial fill/cancel e User Data Stream com IDs correlacionados.
  - `SUBTASK-TASK-US-094-02-02` — Injetar timeout/disconnect, reconciliar e anexar comandos, respostas redigidas e evidence sem mock.

---

# DEFERRED — Pós-R1.0

## E-07 — Regime, Portfolio & Autonomous Multi-Strategy

**Gate de ativação:** R1.0 concluído; duas estratégias aprovadas isoladamente; attribution/parity estáveis; nova PRM. Nenhum card abaixo deve receber Sprint/data antes do gate.

### US-070 — Classificar regime explicável

**Objetivo/valor:** representar contexto com confidence em vez de classe absoluta. **Escopo:** trend/volatility/liquidity/correlation, transitions e replay. **Não escopo:** ML obrigatório. **Falhas:** dado stale/insuficiente produz Unknown.

**ACs:** Given replay/model version iguais, When classifica, Then regime/confidence são reproduzíveis. Given baixa confiança, Then eligibility usa Unknown/no-trade policy.
**Dependências:** US-050, US-052.

- `TASK-US-070-01` — Construir MarketContext.
  - `SUBTASK-TASK-US-070-01-01` — Versionar features, availability e quality flags.
  - `SUBTASK-TASK-US-070-01-02` — Calcular classes/confidence com baseline explicável.
- `TASK-US-070-02` — Controlar transições.
  - `SUBTASK-TASK-US-070-02-01` — Hysteresis/cooldown e Unknown state.
  - `SUBTASK-TASK-US-070-02-02` — Replay, drift e transition audit tests.

### US-071 — Combinar sinais e fazer netting

**Objetivo/valor:** impedir estratégias de se auto-conflitarem. **Escopo:** aggregation, conflicts, target builder e STP hints. **Não escopo:** risk approval. **Falhas:** conflito sem regra resulta no-trade.

**ACs:** Given intents opostos, When netting ocorre, Then não gera self-cross desnecessário. Given regra ausente, Then bloqueia target e registra conflito.
**Dependências:** US-060, duas strategies aprovadas.

- `TASK-US-071-01` — Definir aggregation/netting contract.
  - `SUBTASK-TASK-US-071-01-01` — Normalizar horizon/confidence/exposure units.
  - `SUBTASK-TASK-US-071-01-02` — Implementar conflict rules e deterministic ordering.
- `TASK-US-071-02` — Produzir target auditável.
  - `SUBTASK-TASK-US-071-02-01` — Persistir inputs, netting e rationale.
  - `SUBTASK-TASK-US-071-02-02` — Testar opposing intents, duplicates e concurrency.

### US-072 — Alocar risk budget por estratégia

**Objetivo/valor:** limitar contribuição individual e correlacionada. **Escopo:** caps, correlation/capacity e health integration. **Não escopo:** learned allocator. **Falhas:** missing correlation/capacity usa conservative cap.

**ACs:** Given soma de requests acima do budget, When aloca, Then total e caps não são excedidos. Given strategy unhealthy, Then budget reduz conforme policy.
**Dependências:** US-040, US-071, US-077.

- `TASK-US-072-01` — Implementar allocator bounded.
  - `SUBTASK-TASK-US-072-01-01` — Strategy/asset/portfolio caps e correlation penalty.
  - `SUBTASK-TASK-US-072-01-02` — Capacity e conservative missing-data behavior.
- `TASK-US-072-02` — Integrar health/risk.
  - `SUBTASK-TASK-US-072-02-01` — Aplicar health state e eligibility.
  - `SUBTASK-TASK-US-072-02-02` — Property tests de budget conservation.

### US-074 — Orquestrar decisões autônomas

**Objetivo/valor:** coordenar contexto, candidatos e no-trade sem poder enviar ordem. **Escopo:** orchestration API, trace e fallback. **Não escopo:** burlar Portfolio/Risk/OMS. **Falhas:** módulo indisponível resulta no-trade.

**ACs:** Given decisão, Then trace contém contexto/candidatos/selection/risk/execution link. Given nenhum candidato seguro, Then escolhe no-trade.
**Dependências:** US-070, US-071, US-072, US-075, US-076, US-077.

- `TASK-US-074-01` — Definir orchestration contract.
  - `SUBTASK-TASK-US-074-01-01` — Inputs/outputs, identity e deterministic decision ID.
  - `SUBTASK-TASK-US-074-01-02` — Enforce no ExchangeOrderGateway capability.
- `TASK-US-074-02` — Implementar decision trace/fallback.
  - `SUBTASK-TASK-US-074-02-01` — Persistir candidates, scores, exclusions e allocation.
  - `SUBTASK-TASK-US-074-02-02` — Testar unavailable component, no-candidate e replay.

### US-075 — Selecionar estratégia por utilidade líquida

**Objetivo/valor:** ranquear candidatos após risco, custos e incerteza. **Escopo:** deterministic utility, eligibility e hysteresis. **Não escopo:** bandit. **Falhas:** score incomparável/stale exclui candidate.

**ACs:** Given nenhum score acima do threshold, Then no-trade. Given noisy transition, Then hysteresis evita churn dentro da policy.
**Dependências:** US-070, US-073, US-077, US-080.

- `TASK-US-075-01` — Implementar eligibility/scorer.
  - `SUBTASK-TASK-US-075-01-01` — Edge, risk, cost, uncertainty e correlation penalties.
  - `SUBTASK-TASK-US-075-01-02` — Units, normalization e missing-data rules.
- `TASK-US-075-02` — Implementar ranking estável.
  - `SUBTASK-TASK-US-075-02-01` — Tie-break, hysteresis, cooldown e churn budget.
  - `SUBTASK-TASK-US-075-02-02` — Replay, sensitivity e audit tests.

### US-076 — Rebalancear budgets entre estratégias

**Objetivo/valor:** adaptar allocation sem exceder caps ou gerar turnover excessivo. **Escopo:** deterministic allocator e rebalance. **Não escopo:** bandit/Bayesian. **Falhas:** infeasible allocation reduz exposição/no-trade.

**ACs:** Given capacity/correlation caps, When rebalance executa, Then conserva budget e respeita limits. Given target inviável, Then não força allocation.
**Dependências:** US-072, US-075.

- `TASK-US-076-01` — Implementar allocation/rebalance.
  - `SUBTASK-TASK-US-076-01-01` — Constraints, objective e deterministic solution.
  - `SUBTASK-TASK-US-076-01-02` — Turnover, minimum allocation e no-trade residual.
- `TASK-US-076-02` — Validar portfolio behavior.
  - `SUBTASK-TASK-US-076-02-01` — Stress correlation/capacity/regime shifts.
  - `SUBTASK-TASK-US-076-02-02` — Audit attribution e rollback to prior config.

### US-077 — Gerir Strategy Health comparativo multi-strategy

**Objetivo/valor:** comparar degradação relativa sem contaminar health da baseline. **Escopo:** per-strategy/regime metrics, quarantine e re-entry. **Não escopo:** substituir US-073 no R1.0. **Falhas:** insufficient evidence não beneficia candidate.

**ACs:** Given strategy degrada em regime específico, Then eligibility/budget reduzem apenas no escopo validado. Given re-entry, Then nova evidência satisfaz policy.
**Dependências:** US-073, US-070, duas strategies aprovadas.

- `TASK-US-077-01` — Modelar health contextual.
  - `SUBTASK-TASK-US-077-01-01` — Métricas por strategy/regime/window e confidence.
  - `SUBTASK-TASK-US-077-01-02` — Comparative baseline e multiple-testing controls.
- `TASK-US-077-02` — Integrar quarantine/re-entry.
  - `SUBTASK-TASK-US-077-02-01` — Eligibility/budget transitions e no-trade fallback.
  - `SUBTASK-TASK-US-077-02-02` — Revalidation evidence, expiry e audit.

## E-17 — Continuous Learning & Adaptive Allocation

**Gate de ativação:** R1.1 estável, registry/lineage completo, shadow evidence, exploration policy e approval authority. Todo item é R1.2/Deferred e não pode alterar hard risk limits.

### US-170 — Registrar modelos e estratégias com lineage completo

**Objetivo/valor:** reproduzir qualquer candidate/champion. **Escopo:** registry lifecycle e query. **Não escopo:** promotion por cadastro. **Falhas:** lineage incompleto torna item inelegível.

**ACs:** Given model version, Then data/code/features/config/metrics/decision são recuperáveis. Given hash ausente, Then não vira Challenger.
**Dependências:** US-050, US-054.

- `TASK-US-170-01` — Implementar registry/lifecycle.
  - `SUBTASK-TASK-US-170-01-01` — Schema Candidate/Challenger/Champion/Retired.
  - `SUBTASK-TASK-US-170-01-02` — Hashes, approvals, expiry e supersession.
- `TASK-US-170-02` — Implementar reproducibility query.
  - `SUBTASK-TASK-US-170-02-01` — Resolver Dataset/Experiment/Model Cards.
  - `SUBTASK-TASK-US-170-02-02` — Detectar links ausentes e immutable history.

### US-171 — Avaliar regime ML como challenger

**Objetivo/valor:** superar baseline explicável sem perder controle. **Escopo:** feature pipeline, temporal validation e shadow. **Não escopo:** substituir automaticamente baseline. **Falhas:** leakage/skew/regression rejeitam.

**ACs:** Given challenger, When comparado, Then supera baseline nos gates predefinidos sem regressão material. Given skew, Then não promove.
**Dependências:** US-070, US-170.

- `TASK-US-171-01` — Treinar/validar challenger.
  - `SUBTASK-TASK-US-171-01-01` — Versionar feature pipeline e temporal splits.
  - `SUBTASK-TASK-US-171-01-02` — OOS/walk-forward/ablation/calibration.
- `TASK-US-171-02` — Executar shadow comparison.
  - `SUBTASK-TASK-US-171-02-01` — Paired predictions e regime transition metrics.
  - `SUBTASK-TASK-US-171-02-02` — Skew/drift/fallback and decision report.

### US-172 — Modelar slippage/impact adaptativamente

**Objetivo/valor:** melhorar estimates sem permitir execução insegura. **Escopo:** labels, calibration, monitoring e shadow integration. **Não escopo:** liberar tática/order type. **Falhas:** uncertainty/drift usa estimator conservador.

**ACs:** Given learned estimate incerto, Then fallback bound é usado. Given realized degradation, Then model health reduz eligibility.
**Dependências:** US-082, US-170.

- `TASK-US-172-01` — Construir dataset/model.
  - `SUBTASK-TASK-US-172-01-01` — Labels com own-order/selection bias e lineage.
  - `SUBTASK-TASK-US-172-01-02` — Train/calibrate/OOS contra heuristic baseline.
- `TASK-US-172-02` — Monitorar e integrar bounded.
  - `SUBTASK-TASK-US-172-02-01` — Calibration/drift/uncertainty metrics.
  - `SUBTASK-TASK-US-172-02-02` — Shadow integration, fallback e rollback tests.

### US-173 — Avaliar meta-allocation contextual

**Objetivo/valor:** aprender allocation entre estratégias já aprovadas. **Escopo:** bandit/Bayesian research, OPE e shadow. **Não escopo:** arms não aprovados ou self-increased budget. **Falhas:** unsupported actions/poor overlap impedem OPE claim.

**ACs:** Given exploration budget, Then learner não consegue ampliá-lo. Given OPE não confiável, Then nenhum capital é autorizado.
**Dependências:** US-076, US-170, US-174.

- `TASK-US-173-01` — Especificar learner e budgets.
  - `SUBTASK-TASK-US-173-01-01` — Context/actions/reward/delayed feedback.
  - `SUBTASK-TASK-US-173-01-02` — Hard exploration caps por strategy/asset/regime/window.
- `TASK-US-173-02` — Avaliar offline/shadow.
  - `SUBTASK-TASK-US-173-02-01` — OPE overlap/variance/sensitivity e baseline comparison.
  - `SUBTASK-TASK-US-173-02-02` — Paired shadow, own-order feedback e no-trade behavior.

### US-174 — Detectar data, concept, execution drift e strategy decay

**Objetivo/valor:** separar causas de degradação e acionar resposta proporcional. **Escopo:** metrics, windows, thresholds, alerts/retraining triggers. **Não escopo:** promoção automática. **Falhas:** false positives monitorados; missing baseline é Unknown.

**ACs:** Given drift acima do threshold/janela, Then ação versionada é disparada. Given dados insuficientes, Then não declara estabilidade.
**Dependências:** US-170, US-052, US-082.

- `TASK-US-174-01` — Implementar detectors.
  - `SUBTASK-TASK-US-174-01-01` — Data/concept metrics e reference windows.
  - `SUBTASK-TASK-US-174-01-02` — Execution/strategy decay e attribution signals.
- `TASK-US-174-02` — Governar respostas.
  - `SUBTASK-TASK-US-174-02-01` — Alert/reduce/quarantine/retrain action matrix.
  - `SUBTASK-TASK-US-174-02-02` — Backtest false-positive/delay e threshold versioning.

### US-175 — Automatizar champion-challenger bounded

**Objetivo/valor:** avaliar candidate sem promoção por backtest isolado. **Escopo:** creation, paired evaluation, gates, promotion/rollback. **Não escopo:** remover approval onde exigido. **Falhas:** champion anterior inseguro resulta no-trade, não rollback cego.

**ACs:** Given challenger, Then não recebe capital fora do envelope. Given champion/challenger viola gate, Then rollback/no-trade é executado e auditado.
**Dependências:** US-170, US-174, US-092.

- `TASK-US-175-01` — Implementar evaluation lifecycle.
  - `SUBTASK-TASK-US-175-01-01` — Create/expire challenger e paired shadow evidence.
  - `SUBTASK-TASK-US-175-01-02` — Gate engine com uncertainty/non-regression checks.
- `TASK-US-175-02` — Implementar promotion/rollback.
  - `SUBTASK-TASK-US-175-02-01` — Atomic active-version switch e audit.
  - `SUBTASK-TASK-US-175-02-02` — Rollback/no-trade sob regime/drift/technical failure.

### US-176 — Adaptar parâmetros dentro de envelopes aprovados

**Objetivo/valor:** permitir retuning limitado sem deriva irrestrita. **Escopo:** parameter bounds, optimizer/retrainer e full validation. **Não escopo:** código/hard limit changes. **Falhas:** out-of-envelope output é rejeitado.

**ACs:** Given proposal fora do envelope, Then é rejeitada antes de evaluation. Given proposal válida, Then percorre validation/promotion completo.
**Dependências:** US-054, US-170, US-175.

- `TASK-US-176-01` — Definir e enforçar envelopes.
  - `SUBTASK-TASK-US-176-01-01` — Types/ranges/joint constraints e versioning.
  - `SUBTASK-TASK-US-176-01-02` — Reject/clamp policy sem silent mutation.
- `TASK-US-176-02` — Automatizar candidate validation.
  - `SUBTASK-TASK-US-176-02-01` — Generate preregistered candidate/trial accounting.
  - `SUBTASK-TASK-US-176-02-02` — Execute OOS/stress/shadow e promotion/rollback tests.

### US-177 — Orquestrar slow learning loop

**Objetivo/valor:** executar training/validation fora do hot path com evidência e recovery. **Escopo:** scheduled/triggered DAG, artifacts, retries e shadow/canary handoff. **Não escopo:** training no trading loop. **Falhas:** partial pipeline não promove; retry idempotente.

**ACs:** Given stage falha, Then downstream promotion não executa e estado é resumível. Given success, Then cada input/output/hash/evidence está ligado.
**Dependências:** US-170, US-171, US-172, US-173, US-174, US-175, US-176, US-014.

- `TASK-US-177-01` — Implementar validation DAG durável.
  - `SUBTASK-TASK-US-177-01-01` — Stage contracts, idempotency, checkpoints e retries.
  - `SUBTASK-TASK-US-177-01-02` — Resource isolation, schedule/trigger e concurrency policy.
- `TASK-US-177-02` — Integrar gates e observabilidade.
  - `SUBTASK-TASK-US-177-02-01` — Persistir artifacts/evidence e block partial results.
  - `SUBTASK-TASK-US-177-02-02` — Shadow/canary handoff, alert, cancel e recovery tests.

## Lacunas e blockers para Ready

- Os valores numéricos de RiskPolicy/PRM/CanaryPlan devem permanecer fora dos cards até serem aprovados com unidade, janela e validade.
- `traceability.yaml` precisa receber todas as relações Story → Task → AC → Test/Evidence antes de qualquer item virar Ready.
- RF-084, RF-110, RF-111, RF-112, RF-113, RF-114, RF-115, RF-116, RF-117, RF-118, RF-119, RF-120, RF-121, RF-122, RF-123 e RF-124 devem ser ligados explicitamente às histórias acima.
- ACs declarativos do plano devem ser normalizados para Given/When/Then no ClickUp.
- Dataset/Experiment/Model Cards precisam de owner, retention/license, review/expiry e supersession.
- Nenhum Deferred recebe Sprint, estimate de entrega ou autorização live enquanto seu gate não for observado.
