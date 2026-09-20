# 18 - Critérios de Aceite

## Critérios globais

Todo PBI deve atender DoD e seus critérios específicos. Para código live, acceptance inclui comportamento de falha.

## AC-MARKET-01 Market stream
**Given** conexão saudável, **when** mensagens chegam, **then** são ordenadas/processadas e `market_data_age` permanece dentro do threshold.
**Given** gap/sequence inválida, **when** detectada, **then** feed fica inválido e estratégias dependentes não podem abrir posição até resync.

## AC-BOOK-01 Local order book
**Given** snapshot e sequência contínua de diffs, **when** o livro é materializado, **then** best bid/ask e níveis correspondem ao algoritmo oficial.
**Given** update perdido, duplicado ou fora de ordem, **when** a sequência é validada, **then** o livro fica inválido, consumidores são bloqueados e ocorre resync completo, nunca correção silenciosa.

## AC-OMS-01 Idempotência
**Given** timeout após submit, **when** o resultado é desconhecido, **then** o OMS consulta por clientOrderId antes de qualquer novo submit.

## AC-OMS-02 Partial fill
**Given** fill parcial duplicado ou fora de ordem, **when** o OMS o processa, **then** filled quantity, exposição e ledger são atualizados exatamente uma vez.
**Given** cancelamento posterior, **when** a ordem chega a estado terminal, **then** fills já ocorridos permanecem preservados e reconciliáveis.

## AC-RECON-01
**Given** startup, reconnect, timeout ambíguo ou restore, **when** o estado autoritativo ainda não foi reconciliado, **then** novas entradas permanecem bloqueadas.
**Given** divergência explicada e tratada, **when** os invariantes financeiros voltam a ser confiáveis, **then** a liberação segue a policy e gera audit event.

## AC-RISK-01
**Given** target acima de qualquer limite aplicável, **when** o Risk Engine avalia a intenção, **then** retorna `Adjusted` ou `Rejected` com razão estruturada e nenhuma ordem excede o autorizado.

## AC-RISK-02
**Given** data stale, clock drift crítico ou mismatch não explicado, **when** uma nova exposição é solicitada, **then** a entrada é bloqueada e a condição gera estado operacional, métrica e alerta acionáveis.

## AC-KILL-01
**Given** qualquer estado de Strategy, **when** o kill switch é ativado, **then** novas exposições são bloqueadas, um audit event é persistido e a política configurada para ordens abertas/em voo é executada e reconciliada.

## AC-BACKTEST-01
**Given** mesma strategy version, dataset hash, config e seed, **when** o backtest é repetido no ambiente suportado, **then** produz o mesmo resultado dentro da tolerância numérica versionada ou falha com divergência diagnosticável.

## AC-COST-01
**Given** backtest, paper, shadow, canary ou live report, **when** o resultado é calculado, **then** PnL bruto, fees, spread, slippage, demais custos e PnL líquido aparecem separadamente, com moeda, unidade, período e fonte.

## AC-VALIDATION-01
**Given** uma estratégia candidata, **when** a transição para `CanaryEligible` é solicitada, **then** ela é recusada sem evidências OOS, walk-forward, sensitivity e stress exigidas pela Strategy Spec e pela PromotionReadinessMatrix.

## AC-STRATEGY-01
**Given** um Strategy package, **when** build e architecture tests executam, **then** ele não depende de ExchangeTrading, credenciais ou gateway de ordens e publica somente AlphaSignal/Intent conforme contrato.

## AC-REGIME-01
**Given** mesmo dataset, feature pipeline, model version e config, **when** o detector é reproduzido, **then** a mudança de regime mantém timestamp, confidence/features e resultado dentro da tolerância versionada.

## AC-UI-01
**Given** qualquer rota operacional, **when** a UI é exibida, **then** mostra persistentemente `DEV/PAPER/SHADOW/CANARY/LIVE` e CANARY/LIVE não podem ser confundidos visualmente ou por tecnologia assistiva com simulação.

## AC-SEC-01
**Given** repo, image history, logs, traces, backups e evidence artifacts, **when** os scanners e testes de redaction executam, **then** nenhum secret é encontrado; qualquer chave Binance live verificada não possui permissão de saque.

## AC-PORT-01
**Given** uma release do Worker/API, **when** o pipeline de containers executa, **then** produz imagens `linux/amd64` e `linux/arm64`, o target nativo passa por smoke test e o alternativo passa por runner/emulação antes de qualquer migração que o exija.

## AC-LOCAL-01 Bootstrap local reproduzível

**Given** uma máquina local compatível com as dependências documentadas, **when** o bootstrap e o perfil DEV/PAPER são executados, **then** Worker, API, Web, PostgreSQL e telemetria necessária ficam saudáveis via Docker Compose, com persistência em volumes, migrations aplicadas, smoke tests verdes e nenhuma credencial ou API de cloud exigida.

## AC-LOCAL-02 Isolamento e capacidade local

**Given** trading runtime e workload de research/telemetria na mesma máquina, **when** budgets de CPU, memória, disco, I/O ou retenção são atingidos, **then** profiles/limits impedem degradação silenciosa do hot path, a condição fica observável e workloads não críticos podem ser pausados sem comprometer ledger, OMS ou reconciliação.

## AC-OPS-01
**Given** reconciliação, disconnect ou falha crítica, **when** um alerta é emitido, **then** contém ambiente, exchange, symbol/account scope, timestamp, correlation ID, severidade, owner e ação/runbook sugeridos; o teste de entrega comprova o recebimento no canal definido.

## AC-USERDATA-01 User Data Stream

**Given** subscription autenticada, **when** eventos duplicados, atrasados, desconhecidos ou fora de ordem chegam, **then** o adapter preserva o payload/version, aplica idempotência/ordering e aciona reconciliação quando a continuidade não puder ser provada.
**Given** encerramento ou reconnect, **when** o stream volta, **then** nenhuma nova entrada é liberada antes da reconciliação.

## AC-ANALYTICS-01 Definições de métricas

**Given** uma métrica usada em análise ou gate, **when** calculada e reportada, **then** fórmula, frequência, annualization, risk-free rate, moeda, unidade, versão e tratamento de dados ausentes estão registrados e cobertos por exemplos determinísticos.

## AC-RELEASE-REPORT-01 Relatório pós-release

**Given** uma janela de canary/live encerrada, **when** o relatório pós-release é emitido, **then** agrega confiabilidade, risco, execução, estratégia, incidentes, intervenções e divergências usando fontes/versionamentos declarados e registra decisão, aprovador e follow-ups.

## AC-AUTONOMY-01 Operação baseline autônoma

**Given** a baseline Trend em shadow/canary dentro do envelope aprovado, **when** completa o período, volume e regimes definidos na PromotionReadinessMatrix, **then** intervention rate, recovery, `NO_TRADE`, SafeMode, violações de risco e divergências permanecem dentro dos limites explícitos; qualquer violação impede promoção.

## AC-RAG-02 Recuperação do índice

**Given** embeddings indisponíveis ou índice RAG ausente/corrompido, **when** uma consulta é executada, **then** o sistema degrada para busca lexical ou falha explicitamente, não retorna contexto não verificável e permite rebuild determinístico a partir das fontes registradas.

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

## AC-AUTONOMY-02 Lifecycle e readiness
**Given** processo iniciado, **when** startup, reconnect, falha transitória ou KillSwitch ocorrer, **then** lifecycle persistido bloqueia entradas fora de `Ready` e não reseta KillSwitch automaticamente.

## AC-AUTONOMY-03 Market-data acionável
**Given** snapshot/deltas e candles, **when** gap, duplicata, atraso ou clock drift ocorrer, **then** o pipeline deduplica/ordena, reaquece e bloqueia contexto stale.

## AC-AUTONOMY-04 Ciclo baseline idempotente
**Given** candle BTCUSDT fechado, **when** o trigger for repetido, **then** estratégia, target, risco e outbox usam o mesmo `DecisionId` sem ordem duplicada.

## AC-AUTONOMY-05 Risk obrigatório
**Given** Testnet com execução habilitada, **when** RiskPolicy ou guard estiver inválido, **then** startup falha seguro ou o ciclo rejeita/SafeMode sem submit.

## AC-AUTONOMY-06 OMS e projeções
**Given** eventos REST/user-data duplicados ou fora de ordem, **when** a projeção atualizar, **then** OMS/ledger são idempotentes e não regridem nem geram overfill.

## AC-AUTONOMY-07 User stream e reconciliação
**Given** `NO_TRADE=false`, **when** startup, reconnect, gap ou submit ambíguo ocorrer, **then** stream e reconciliação coexistem e mismatch bloqueia novas entradas.

## AC-AUTONOMY-08 Planner determinístico
**Given** target autorizado e preço/depth recentes, **when** houver partial fill/timeout/custo excessivo, **then** somente residual é planejado com attempt determinístico ou `NO_TRADE`.

## AC-AUTONOMY-09 Self-healing fail-safe
**Given** dependência falhando, **when** o supervisor classificar o incidente, **then** retry/breaker é bounded e o runtime entra em SafeMode quando a integridade for incerta.

## AC-AUTONOMY-10 Prova de autonomia
**Given** qualquer ciclo/falha, **when** consultado por `DecisionId`, **then** versões, motivos, métricas, transições e evidência são reconstruíveis sem segredo.

## AC-AUTONOMY-11 Promotion ladder A2
**Given** uma fase A2 concluída, **when** a próxima fase for avaliada, **then** promoção exige critérios, evidence bundle, aprovação, capability correta e rollback testado.

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
