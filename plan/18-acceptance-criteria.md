# 18 - Critérios de Aceite

## Critérios globais

Todo PBI deve atender DoD e seus critérios específicos. Para código live, acceptance inclui comportamento de falha.

## AC-MARKET-01 Market stream
**Given** conexão saudável, **when** mensagens chegam, **then** são ordenadas/processadas e `market_data_age` permanece dentro do threshold.  
**Given** gap/sequence inválida, **when** detectada, **then** feed fica inválido e estratégias dependentes não podem abrir posição até resync.

## AC-BOOK-01 Local order book
Após snapshot+diff, best bid/ask e níveis devem corresponder ao algoritmo oficial; um update perdido deve causar resync, nunca correção silenciosa.

## AC-OMS-01 Idempotência
**Given** timeout após submit, **when** o resultado é desconhecido, **then** o OMS consulta por clientOrderId antes de qualquer novo submit.

## AC-OMS-02 Partial fill
Fills parciais atualizam filled quantity e exposição exatamente uma vez; cancelamento preserva fills já ocorridos.

## AC-RECON-01
No startup live, novas entradas permanecem bloqueadas até conta/ordens/estado local serem reconciliados.

## AC-RISK-01
Target acima de limite retorna Adjusted/Rejected com razão estruturada e não gera ordem acima do autorizado.

## AC-RISK-02
Data stale, clock drift crítico ou mismatch não explicado bloqueiam novas entradas.

## AC-KILL-01
Kill switch bloqueia novas exposições independentemente do status das strategies, gera audit event e executa política de cancelamento configurada.

## AC-BACKTEST-01
Mesma strategy version + dataset hash + config + seed produz o mesmo resultado dentro da tolerância definida.

## AC-COST-01
Todo relatório exibe PnL bruto, fees, spread/slippage estimado e PnL líquido separadamente.

## AC-VALIDATION-01
Nenhuma estratégia muda para status `CanaryEligible` sem evidências OOS, walk-forward/sensitivity e stress exigidas pela Strategy Spec.

## AC-STRATEGY-01
Strategy package compila sem dependência do ExchangeTrading adapter e só publica AlphaSignal/Intent.

## AC-REGIME-01
Mudança de regime possui timestamp, confidence/features e é reproduzível no replay para mesmo dataset/model version.

## AC-UI-01
A UI mostra persistentemente `DEV/PAPER/SHADOW/CANARY/LIVE`; CANARY/LIVE não podem ser confundidos visualmente com simulação.

## AC-SEC-01
Nenhum secret aparece no repo, image history, logs de teste ou telemetry; chave Binance live não possui withdrawal permission.

## AC-PORT-01
A release do Worker/API possui imagens `linux/amd64` e `linux/arm64`; o target nativo da máquina local passa por smoke test e o target alternativo passa por runner/emulação antes de qualquer migração futura que o exija.

## AC-LOCAL-01 Bootstrap local reproduzível

**Given** uma máquina local compatível com as dependências documentadas, **when** o bootstrap e o perfil DEV/PAPER são executados, **then** Worker, API, Web, PostgreSQL e telemetria necessária ficam saudáveis via Docker Compose, com persistência em volumes, migrations aplicadas, smoke tests verdes e nenhuma credencial ou API de cloud exigida.

## AC-LOCAL-02 Isolamento e capacidade local

**Given** trading runtime e workload de research/telemetria na mesma máquina, **when** budgets de CPU, memória, disco, I/O ou retenção são atingidos, **then** profiles/limits impedem degradação silenciosa do hot path, a condição fica observável e workloads não críticos podem ser pausados sem comprometer ledger, OMS ou reconciliação.

## AC-OPS-01
Alert de reconciliação/disconnect crítico contém ambiente, exchange, symbol/account scope, timestamp, correlation ID e ação sugerida.

## AC-ENV-01 Ambiente separado

**Given** um perfil DEV/PAPER/TESTNET/DEMO/PRODUCTION, **when** o worker inicia, **then** endpoints, key reference, namespace e modo de ordem pertencem ao mesmo ambiente; combinação inconsistente falha antes de conectar.

## AC-ENV-02 Testnet sem mock

**Given** uma integração marcada como Testnet, **when** o teste envia/consulta estado, **then** usa Binance Spot Testnet real; mocks/fakes são permitidos somente em unit/contract/replay e nunca contam como evidência de integração.

## AC-OMS-03 Erro ambíguo

**Given** timeout `-1007`, resposta 5XX ou resultado parcial de cancel-replace, **when** o comando retorna estado desconhecido, **then** o OMS persiste o estado ambíguo, consulta a exchange/user-data e não reenvia cegamente.

## AC-EXCHANGE-01 Filtros e status

**Given** um snapshot de `exchangeInfo`/filtros de conta, **when** um target é convertido em ordem, **then** status, `PRICE_FILTER`, lotes, notional, percent-price, limites, STP e `CANCEL_ONLY` são avaliados com arredondamento decimal exato.

## AC-RISK-03 RiskPolicy

**Given** uma RiskPolicy versionada, **when** uma ordem é autorizada, **then** exposição/notional, daily loss, drawdown, stale data, clock drift, slippage, volatilidade, liquidez e churn são avaliados e a versão da policy acompanha a decisão.

## AC-UNIVERSE-01 Universo point-in-time

**Given** um timestamp de seleção, **when** a UniversePolicy calcula candidatos, **then** usa somente dados disponíveis naquele instante, aplica status/delist/liquidez/capacidade, respeita o limite configurado e persiste snapshot, hash, ranking, hysteresis e churn budget.

## AC-AUDIT-01 Journal financeiro

**Given** uma decisão, ordem, fill, reconciliação ou intervenção, **when** o evento é persistido, **then** existe audit event append-only com schema/version, correlation/causation, policy/config version, hash e retenção/ACL; sampling de trace não pode removê-lo.

## AC-LEDGER-01 Ledger por ativo

**Given** fill com comissão em qualquer ativo, **when** ele é aplicado, **then** postings imutáveis registram principal, proceeds e fee com quantidade, ativo, valuation source/time e vínculo a order/trade IDs; position e PnL podem ser reconstruídos.

## AC-LEDGER-02 Ajuste reconciliável

**Given** depósito, retirada, conversão, trade externo ou mismatch confirmado, **when** o estado é reconciliado, **then** um evento/lançamento compensatório explica a diferença sem apagar ou reescrever o histórico.

## AC-DURABILITY-01 Persist-before-submit

**Given** uma ordem autorizada, **when** o adapter pode enviá-la, **then** OrderIntent, RiskDecision, policy/config version, idempotency key e outbox record já estão duráveis; crash em qualquer boundary é recuperável sem submit cego.

## AC-DURABILITY-02 Backpressure

**Given** saturação de fila, **when** a capacidade é excedida, **then** eventos financeiros nunca são descartados; a policy por classe é aplicada, a condição é observável e novas entradas são bloqueadas quando a integridade estiver incerta.

## AC-RAG-01 Research Plane

**Given** uma consulta de planejamento ou implementação, **when** o RAG retorna contexto, **then** aplica autoridade/versão/status/frescor, cita path/linha/hash, mostra conflitos/supersession e confirma contra o commit atual antes de recomendar mudança.

## AC-TRACE-01 Rastreabilidade

**Given** uma história crítica, **when** ela é marcada Done, **then** há links Goal/Epic/US/AC/TASK/SUBTASK/Code/Test/Evidence, comando executado, saída observada, review, risco residual e rollback.

## AC-RESEARCH-01 Preregistration e holdout

**Given** um experimento que pode influenciar promoção, **when** ele começa, **then** hypothesis, experiment family, search space, trial budget, métricas, benchmarks e rejeição estão registrados antes do resultado; acessos/contaminação do holdout são auditados.

## AC-PARITY-01 Research/production parity

**Given** mesmo dataset, config e seed, **when** o replay executa em backtest, paper e shadow, **then** Strategy, Portfolio, Risk, filtros e arredondamento geram resultados equivalentes dentro da tolerância versionada ou registram divergência explicada por ADR.

## AC-SEC-02 Administração crítica

**Given** ação CANARY/LIVE, mudança de RiskPolicy/credencial ou reset de kill switch, **when** solicitada, **then** MFA/step-up, autorização, anti-replay e audit event são exigidos; sessão revogada não é aceita.

## AC-KILL-02 Caminho independente

**Given** UI/API ou Strategy indisponível e uma ordem em voo, **when** o kill switch é ativado pelo caminho operacional aprovado, **then** novas exposições são bloqueadas, a ordem é tratada conforme policy e o resultado é reconciliado/auditado.

## AC-DATA-01 Temporalidade e correção

**Given** dado de mercado/reference data, **when** ingerido ou corrigido, **then** event/exchange/receive/ingestion times, schema/source version e checksum são preservados; correção cria versão `supersedes` e não altera dataset já usado.

## AC-AWS-01 Guardrail de custo

**Given** uma iniciativa AWS futura explicitamente aprovada, **when** qualquer recurso pago puder ser criado, **then** eligibility/account plan/region/pricing, budget baixo, TTL, owner, estimativa e teardown estão aprovados; sem essa aprovação, nenhum recurso ou credencial AWS faz parte do ambiente.

## Critério para release canary

- todos os ACs críticos vinculados explicitamente na `PromotionReadinessMatrix`;
- restore drill concluído;
- kill switch exercitado;
- estratégia possui report assinado/versionado;
- risk policy canary aprovada;
- chave sem saques;
- monitoramento e canal de alerta ativos;
- nenhuma vulnerabilidade crítica aberta.
- `RiskPolicy`, `PromotionReadinessMatrix`, `CanaryPlan`, `ThreatModel`, `ReconciliationReport` e `ReleaseEvidence` completos e aprovados;
- período, volume, regimes observados e tolerâncias possuem valores/unidades, não placeholders;
- ordem em voo, abort, rollback e fallback `NO_TRADE` foram ensaiados.
