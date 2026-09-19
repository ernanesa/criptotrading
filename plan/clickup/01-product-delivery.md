# Manifesto ClickUp — Product Delivery

## Controle de publicação

- **Escopo deste manifesto:** estrutura do projeto, política de publicação, E-10, E-13 e opções E-14/E-15/E-16.
- **Fonte:** `plan/README.md`, `plan/01-product-requirements.md`, `plan/17-backlog.md`, `plan/18-acceptance-criteria.md`, `plan/19-roadmap-releases.md`, `plan/20-sprint-plan.md`, `plan/30-traceability-and-agent-delivery.md`.
- **Modo atual:** proposta revisável; este arquivo não autoriza escrita no ClickUp.
- **Regra:** antes da publicação, apresentar diff exato de Space/Folder/List/items/campos/parents e obter aprovação explícita.
- **IDs:** são imutáveis; item alterado usa `supersedes`, nunca reutiliza ID.

## Hierarquia e campos

```text
Space: CryptoTrading
└── Folder: Product Delivery
    ├── List: R0.x-R1.0 — Active Delivery
    ├── List: R1.1-R1.2 — Post-R1.0
    ├── List: Options — Deferred
    └── List: Decisions, Risks & Evidence
```

Hierarquia lógica: `Epic → Story → Task → Subtask`. Se o plano ClickUp não permitir quatro níveis, manter Epic e Story como tasks relacionadas pelos campos `Epic ID` e `Parent Story`, sem achatar Task/Subtask.

Campos obrigatórios: `Stable ID`, `Item Type`, `Release`, `Sprint`, `Status`, `Priority`, `Persona`, `Risk Class`, `Trading Mode`, `Requirements`, `Acceptance Criteria`, `Dependencies`, `Evidence ID`, `Source Path`, `Source Commit/Hash`, `Deferred Reason`, `Review Required`, `Residual Risk`.

Na publicação, itens ativos começam `Proposed`, opções ficam `Deferred/Option` e somente `US-131` começa `Blocked`. O publicador injeta `Source Commit/Hash` a partir do `HEAD` limpo e mantém campos ainda não aprovados explicitamente vazios/`Not defined`, impedindo transição para `Ready`.

Statuses: `Proposed → Refinement → Ready → In Progress → In Review → Validation → Done`; estados laterais `Blocked` e `Deferred/Option`.

## Política de cartão e Done

Toda Story declara objetivo/valor, escopo, non-goals, regras, contratos, falhas, dependências, AC Given/When/Then, testes, evidência e rollback. Toda Task produz resultado verificável; toda Subtask cabe em uma sessão curta e declara evidência.

Um item só fica `Ready` com fontes atuais, dependências resolvidas, AC testável, comando e evidência esperados. Só fica `Done` com AC comprovado, testes executados e saída inspecionada, revisão, documentação, risco residual, rollback e cadeia `GOAL → EPIC → US → AC → TASK → SUBTASK → CODE → TEST → EVIDENCE` completa.

## Dependências de produto

E-10 depende dos contratos de API/SignalR, autenticação, OMS, Risk, Research e observabilidade. E-13 depende de ledger, persist-before-submit, OMS/reconciliação, RiskPolicy, SafeMode, kill switch, restore drill, segurança, alertas, baseline validada, paper, shadow e Testnet. E-14/E-15/E-16 dependem de R1.0 comprovado e nova decisão de produto.

---

# E-10 — Web Dashboard

**Scope/Status:** mixed; active stories = Proposed; US-104 = Deferred/Option. **Outcome:** operação e pesquisa observáveis sem transformar a UI em fonte autoritativa do estado financeiro.

## US-100 — Disponibilizar shell React type-safe

**Scope/Status/Prioridade:** active / Proposed / P1. **Valor:** oferecer uma base web segura e consistente para as jornadas operacionais. **Escopo:** Vite, Router, Query, autenticação, error boundaries e cliente gerado. **Não escopo:** telas de domínio completas ou contratos inventados no frontend.

**Regras/contratos/falhas:** tipos vêm do OpenAPI versionado; rota protegida exige sessão válida; 401 tenta fluxo autorizado e 403 não é mascarado; falha global mostra correlation ID; ambiente/mode permanecem visíveis; sessão revogada não usa cache privilegiado.

**AC:** Given contrato OpenAPI compatível, when o cliente é gerado e o shell compilado, then não há tipos manuais divergentes. Given sessão ausente/revogada, when rota protegida é acessada, then conteúdo não é exibido e o evento é auditável. Given erro não tratado, when o boundary captura a falha, then há fallback acessível e correlation ID sem secret.

**Dependências:** US-001, US-003, US-110, US-113; AC-UI-01, AC-SEC-02.

- `TASK-US-100-01` — Criar fundação React.
  - `SUBTASK-TASK-US-100-01-01` Fixar Node/pnpm/Vite/React/TypeScript.
  - `SUBTASK-TASK-US-100-01-02` Configurar Router, Query e composition root.
  - `SUBTASK-TASK-US-100-01-03` Configurar lint, typecheck, unit test e build.
- `TASK-US-100-02` — Integrar contratos type-safe.
  - `SUBTASK-TASK-US-100-02-01` Gerar cliente a partir do OpenAPI pinado.
  - `SUBTASK-TASK-US-100-02-02` Validar breaking changes no pipeline.
  - `SUBTASK-TASK-US-100-02-03` Testar serialização de erro e correlation ID.
- `TASK-US-100-03` — Implementar shell seguro.
  - `SUBTASK-TASK-US-100-03-01` Criar auth/session boundary e rotas protegidas.
  - `SUBTASK-TASK-US-100-03-02` Criar environment banner persistente.
  - `SUBTASK-TASK-US-100-03-03` Criar loading/empty/error boundaries acessíveis.

**Testes/evidência:** lint, typecheck, build, testes de rota 401/403/revogação, snapshot do banner por modo e `EVIDENCE-US-100-<date>`. Rollback: servir versão anterior do frontend compatível com o mesmo contrato.

## US-101 — Exibir trading em tempo real

**Scope/Status/Prioridade:** active / Proposed / P1. **Valor:** permitir ao operador compreender mercado, ordens, fills, posições e PnL com frescor explícito. **Escopo:** SignalR, painéis e estados stale/reconnect. **Não escopo:** alterar ledger, enviar ordem ou inferir dados ausentes.

**Regras/contratos/falhas:** API/ledger são fonte autoritativa; toda atualização leva timestamp e sequência; gap invalida o painel afetado até snapshot; reconnect usa backoff e resubscription; valores stale nunca parecem atuais; empty legítimo difere de erro.

**AC:** Given snapshot e stream válidos, when eventos chegam, then a projeção converge sem duplicar fills. Given gap ou disconnect, when detectado, then o painel mostra stale/reconnecting e não inventa continuidade. Given nenhuma ordem/posição, when a consulta retorna vazia, then exibe empty state, não erro.

**Dependências:** US-030, US-031, US-033, US-111; AC-UI-01, AC-OPS-01.

- `TASK-US-101-01` — Definir read models.
  - `SUBTASK-TASK-US-101-01-01` Versionar contratos market/order/fill/position/PnL.
  - `SUBTASK-TASK-US-101-01-02` Definir timestamp, sequence e freshness.
  - `SUBTASK-TASK-US-101-01-03` Criar fixtures normal/empty/stale/failure.
- `TASK-US-101-02` — Implementar lifecycle SignalR.
  - `SUBTASK-TASK-US-101-02-01` Snapshot inicial e subscription.
  - `SUBTASK-TASK-US-101-02-02` Reconnect/backoff/resubscription.
  - `SUBTASK-TASK-US-101-02-03` Gap detection e snapshot recovery.
- `TASK-US-101-03` — Construir painéis operacionais.
  - `SUBTASK-TASK-US-101-03-01` Mercado e freshness.
  - `SUBTASK-TASK-US-101-03-02` Ordens, fills e posições.
  - `SUBTASK-TASK-US-101-03-03` PnL bruto/líquido e custos.

**Testes/evidência:** component tests, stream duplicado/fora de ordem, reconnect, stale, empty e accessibility scan; `EVIDENCE-US-101-<date>`.

## US-102 — Operar risco e kill switch com segurança

**Scope/Status/Prioridade:** active / Proposed / P0. **Valor:** tornar limites, breakers e intervenção crítica compreensíveis e acionáveis. **Escopo:** visualização, confirmação reforçada, step-up, resultado reconciliado e audit trail. **Não escopo:** Risk Engine no browser ou bypass do caminho independente.

**Regras/contratos/falhas:** UI nunca decide autorização; comando crítico exige MFA/step-up, nonce/idempotency key e confirmação com ambiente; duplo clique não duplica comando; timeout fica `unknown/pending reconciliation`; sessão revogada falha fechada.

**AC:** Given breaker ativo, when painel atualiza, then mostra policy version, razão e impacto. Given kill switch solicitado, when step-up e confirmação passam, then novas exposições são bloqueadas e resultado auditado. Given timeout, when resultado é desconhecido, then UI não declara sucesso e consulta estado até resolução.

**Dependências:** US-040, US-042, US-043, US-113; AC-KILL-01, AC-KILL-02, AC-SEC-02.

- `TASK-US-102-01` — Expor estado de risco.
  - `SUBTASK-TASK-US-102-01-01` Modelar limits/utilization/breakers.
  - `SUBTASK-TASK-US-102-01-02` Mostrar policy/config version e reason codes.
  - `SUBTASK-TASK-US-102-01-03` Implementar stale/error states.
- `TASK-US-102-02` — Implementar jornada do kill switch.
  - `SUBTASK-TASK-US-102-02-01` Step-up e confirmação com ambiente.
  - `SUBTASK-TASK-US-102-02-02` Idempotency/anti-replay/pending state.
  - `SUBTASK-TASK-US-102-02-03` Resultado, ordens em voo e reconciliação.
- `TASK-US-102-03` — Tornar intervenção auditável.
  - `SUBTASK-TASK-US-102-03-01` Timeline de request/decision/result.
  - `SUBTASK-TASK-US-102-03-02` Testar revogação, replay e duplo clique.
  - `SUBTASK-TASK-US-102-03-03` Vincular runbook e correlation ID.

**Testes/evidência:** integração com caminho real de comando, timeout, 401/403, replay, UI/API indisponível e ordem em voo; `EVIDENCE-US-102-<date>`. Rollback nunca reabilita trading automaticamente.

## US-103 — Inspecionar experimentos e evidências de pesquisa

**Scope/Status/Prioridade:** active / Proposed / P1. **Valor:** permitir decisão de promoção baseada em evidência reproduzível. **Escopo:** experimentos, lineage, métricas, equity/DD, sensitivity e exports. **Não escopo:** editar resultado, desbloquear holdout ou promover estratégia pela UI.

**Regras/contratos/falhas:** métricas mostram fórmula/unidade/janela; experimento referencia dataset/config/code hash; holdout access é visível; resultado incompleto não recebe aparência de aprovado; export preserva provenance.

**AC:** Given experimento concluído, when detalhe é aberto, then lineage e métricas líquidas são reconstruíveis. Given experimento incompleto/falhou, when exibido, then estado e ausência de métricas são explícitos. Given export, when gerado, then contém hashes, versões, custos e timestamp.

**Dependências:** US-050, US-051, US-052, US-053, US-054, US-055, US-061; AC-COST-01, AC-RESEARCH-01, AC-RAG-01.

- `TASK-US-103-01` — Definir contratos de pesquisa.
  - `SUBTASK-TASK-US-103-01-01` Experiment list/detail schema.
  - `SUBTASK-TASK-US-103-01-02` Metric definition/lineage schema.
  - `SUBTASK-TASK-US-103-01-03` Estados running/failed/contaminated/approved.
- `TASK-US-103-02` — Construir visualizações.
  - `SUBTASK-TASK-US-103-02-01` Equity e drawdown.
  - `SUBTASK-TASK-US-103-02-02` Custos e attribution.
  - `SUBTASK-TASK-US-103-02-03` Sensitivity e confidence intervals.
- `TASK-US-103-03` — Implementar evidência e export.
  - `SUBTASK-TASK-US-103-03-01` Exibir lineage e holdout audit.
  - `SUBTASK-TASK-US-103-03-02` Exportar relatório com provenance.
  - `SUBTASK-TASK-US-103-03-03` Testar acesso, empty/failure e integridade.

**Testes/evidência:** golden experiment, dataset superseded, holdout contaminado, métricas ausentes e export revalidado; `EVIDENCE-US-103-<date>`.

## US-104 — Visualizar autonomia multi-strategy

**Scope/Status/Prioridade:** deferred / Deferred/Option / P2. **Release:** pós-R1.0. **Valor:** explicar regime, candidatos, scores, allocations, health, drift e champion/challenger. **Não escopo:** decisão no frontend ou ativação antes de E-07/E-17.

**AC:** Given decision trace versionado, when detalhe é aberto, then contexto, candidatos, exclusões, score, alocação e resultado são explicáveis. Given dependências ainda não aprovadas, when a story é avaliada, then permanece Deferred.

- `TASK-US-104-01` — Refinar contrato após E-07.
  - `SUBTASK-TASK-US-104-01-01` Confirmar personas e decisões suportadas.
  - `SUBTASK-TASK-US-104-01-02` Versionar decision-trace read model.
  - `SUBTASK-TASK-US-104-01-03` Definir estados de incerteza/drift.
- `TASK-US-104-02` — Preparar validation plan.
  - `SUBTASK-TASK-US-104-02-01` Fixtures multi-strategy.
  - `SUBTASK-TASK-US-104-02-02` Testes de explicabilidade e stale data.
  - `SUBTASK-TASK-US-104-02-03` Gate go/no-go pós-R1.0.

---

# E-13 — Live Spot 1.0

**Scope/Status:** active / Proposed; US-131 = Blocked até readiness e aprovação explícita. **Outcome:** promover a baseline Spot somente com evidência quantitativa, operacional, financeira e de segurança. Nenhum cartão autoriza capital real por si só.

## US-130 — Decidir readiness live por evidência

**Scope/Status/Prioridade:** active / Proposed / P0. **Escopo:** matriz, evidências, drills e decisão assinada. **Não escopo:** executar canary ou aceitar placeholders.

**Regras/falhas:** ausência, expiração ou conflito de evidência resulta em `Not Ready`; thresholds têm valor/unidade/janela; exceção requer owner, validade e risco aceito; decisão é imutável e auditada.

**AC:** Given matriz completa, when gate é avaliado, then cada critério aponta para evidência atual e resultado pass/fail. Given evidência ausente/expirada, when gate roda, then readiness é negada. Given aprovação, when registrada, then inclui versões de Strategy/RiskPolicy/CanaryPlan e aprovador.

**Dependências:** US-013, US-014, US-024, US-044, US-054, US-061, US-073, US-090, US-091, US-092, US-093, US-094, US-113, US-114, US-134, US-180, US-181, US-182; AC-VALIDATION-01, AC-TRACE-01.

- `TASK-US-130-01` — Implementar readiness matrix verificável.
  - `SUBTASK-TASK-US-130-01-01` Enumerar critérios e fontes.
  - `SUBTASK-TASK-US-130-01-02` Validar valores/unidades/janelas/validade.
  - `SUBTASK-TASK-US-130-01-03` Produzir pass/fail e blockers.
- `TASK-US-130-02` — Reunir evidência crítica.
  - `SUBTASK-TASK-US-130-02-01` Strategy/research/parity evidence.
  - `SUBTASK-TASK-US-130-02-02` Ledger/OMS/risk/security evidence.
  - `SUBTASK-TASK-US-130-02-03` Restore/kill-switch/reconcile drills.
- `TASK-US-130-03` — Registrar decisão.
  - `SUBTASK-TASK-US-130-03-01` Revisão independente.
  - `SUBTASK-TASK-US-130-03-02` Aprovação/rejeição com rationale.
  - `SUBTASK-TASK-US-130-03-03` Snapshot/hash e validade da decisão.

**Testes/evidência:** matriz com item ausente, expirado e conflitante; relatório determinístico `EVIDENCE-US-130-<date>`.

## US-131 — Operar canary e promover gradualmente

**Scope/Status/Prioridade:** active / Blocked / P0. **Blocker:** readiness e aprovação humana explícita ainda não concedidas. **Escopo:** preflight, janela de observação, abort, incident review e scale decision. **Não escopo:** autoaprovação ou aumento fora do envelope.

**AC:** Given readiness aprovada e válida, when canary inicia, then capital/símbolos/limites correspondem ao CanaryPlan. Given abort criterion, when disparado, then novas exposições param e ordens em voo seguem policy. Given janela concluída, when revisão ocorre, then promote/hold/rollback é registrada com evidência.

**Dependências:** US-130, US-134 e aprovação humana explícita.

- `TASK-US-131-01` — Executar preflight.
  - `SUBTASK-TASK-US-131-01-01` Validar versões, credencial e withdrawal disabled.
  - `SUBTASK-TASK-US-131-01-02` Validar limits, alerts e kill switch.
  - `SUBTASK-TASK-US-131-01-03` Registrar autorização e janela.
- `TASK-US-131-02` — Observar canary.
  - `SUBTASK-TASK-US-131-02-01` Monitorar risco/reliability/execution.
  - `SUBTASK-TASK-US-131-02-02` Registrar intervenções e incidentes.
  - `SUBTASK-TASK-US-131-02-03` Exercitar abort quando aplicável.
- `TASK-US-131-03` — Decidir promoção.
  - `SUBTASK-TASK-US-131-03-01` Comparar thresholds.
  - `SUBTASK-TASK-US-131-03-02` Revisar incidentes e desvios.
  - `SUBTASK-TASK-US-131-03-03` Registrar promote/hold/rollback.

**Evidência:** `EVIDENCE-US-131-<date>`; rollback segue CanaryPlan e nunca apaga ledger.

## US-132 — Produzir relatório pós-release

**Scope/Status/Prioridade:** active / Proposed / P1. **Valor:** fechar o ciclo empírico e reordenar backlog com dados. **Escopo:** reliability, execution, strategy, risk, autonomia e ações. **Não escopo:** declarar causalidade não sustentada ou alterar gates retroativamente.

**AC:** Given janela encerrada, when relatório é gerado, then período, fontes, versões e dados ausentes são explícitos. Given desvio, when analisado, then ação, owner e prioridade são registrados. Given incidente aberto, when relatório fecha, then residual risk permanece visível.

- `TASK-US-132-01` — Consolidar indicadores.
  - `SUBTASK-TASK-US-132-01-01` Reliability e intervenções.
  - `SUBTASK-TASK-US-132-01-02` Execution quality e custos.
  - `SUBTASK-TASK-US-132-01-03` Strategy/risk performance.
- `TASK-US-132-02` — Produzir review acionável.
  - `SUBTASK-TASK-US-132-02-01` Comparar com gates/benchmarks.
  - `SUBTASK-TASK-US-132-02-02` Registrar incidentes e risco residual.
  - `SUBTASK-TASK-US-132-02-03` Criar ações e reordenar backlog.

**Dependências/evidência:** US-131; relatórios observacionais; `EVIDENCE-US-132-<date>`.

## US-133 — Comprovar operação baseline sem intervenção rotineira

**Scope/Status/Prioridade:** active / Proposed / P0. **Escopo:** baseline, NO_TRADE, SafeMode, recovery, soak e intervention-rate. **Não escopo:** multi-strategy ou ML.

**AC:** Given período/volume/regimes definidos na matrix, when soak termina, then disponibilidade, divergências, recovery e intervenção atendem thresholds. Given contexto inseguro, when nenhum trade é autorizado, then NO_TRADE é registrado como decisão válida. Given falha recuperável, when supervisor atua, then reconciliação precede novas entradas.

- `TASK-US-133-01` — Definir protocolo de soak.
  - `SUBTASK-TASK-US-133-01-01` Fixar período/volume/regimes.
  - `SUBTASK-TASK-US-133-01-02` Fixar KPIs e tolerâncias.
  - `SUBTASK-TASK-US-133-01-03` Definir intervenção e exclusões.
- `TASK-US-133-02` — Executar e medir autonomia.
  - `SUBTASK-TASK-US-133-02-01` Coletar decisions/NO_TRADE/intervenções.
  - `SUBTASK-TASK-US-133-02-02` Medir recovery/reconciliation.
  - `SUBTASK-TASK-US-133-02-03` Classificar falhas e violações.
- `TASK-US-133-03` — Emitir decisão R1.0.
  - `SUBTASK-TASK-US-133-03-01` Validar completude e lineage.
  - `SUBTASK-TASK-US-133-03-02` Revisar residual risk.
  - `SUBTASK-TASK-US-133-03-03` Registrar pass/fail e próximos passos.

**Evidência:** relatório de soak e `EVIDENCE-US-133-<date>`.

## US-134 — Governar canary por plano e release evidence

**Scope/Status/Prioridade:** active / Proposed / P0. **Escopo:** envelope, abort, rollback, ordens em voo, approvals e observation report. **Não escopo:** thresholds vagos ou aprovação implícita.

**AC:** Given CanaryPlan versionado, when validado, then todos os limites, aborts, owners e rollback têm valor/unidade. Given rollback, when executado, then estado financeiro é reconciliado e evidenciado. Given release encerrada, when evidence é assinada, then versões e resultados são imutáveis/auditáveis.

- `TASK-US-134-01` — Completar CanaryPlan.
  - `SUBTASK-TASK-US-134-01-01` Definir envelope e observation window.
  - `SUBTASK-TASK-US-134-01-02` Definir abort/rollback/fallback.
  - `SUBTASK-TASK-US-134-01-03` Definir owner/approval/validade.
- `TASK-US-134-02` — Ensaiar falhas.
  - `SUBTASK-TASK-US-134-02-01` Ordem em voo e timeout ambíguo.
  - `SUBTASK-TASK-US-134-02-02` UI/API indisponível e kill path.
  - `SUBTASK-TASK-US-134-02-03` Restore/reconcile/NO_TRADE fallback.
- `TASK-US-134-03` — Fechar evidence package.
  - `SUBTASK-TASK-US-134-03-01` ReconciliationReport.
  - `SUBTASK-TASK-US-134-03-02` ReleaseEvidence e observation report.
  - `SUBTASK-TASK-US-134-03-03` Revisão/assinatura/hash.

**Dependências/evidência:** US-043, US-044, US-092, US-113, US-114, US-180, US-181, US-182; `EVIDENCE-US-134-<date>`.

US-134 prepara os artefatos antes de US-130 avaliar readiness e US-131 operar o canary; não depende delas.

---

# Opções exploratórias E-14/E-15/E-16

Todos os itens abaixo têm **Status: Deferred/Option**, **Priority: P3**, nenhum Sprint e nenhum compromisso de release. Só podem ir a Refinement após R1.0 comprovado, Product Decision, ADR, threat/risk model, dados, benchmark, custo/capacidade e critérios go/no-go. As tasks são de descoberta; não autorizam implementação nem live.

## E-14 — Futures & Hedging

### US-140 — Avaliar Futures adapter

**Valor/escopo:** medir viabilidade de adapter para contratos, market/account data e ordens Futures. **Não escopo:** capital, leverage ou reuso cego de semântica Spot. **Falhas:** contrato desconhecido permanece explícito; incompatibilidade rejeita a opção. **AC:** Given documentação/versionamento e fixtures, when spike termina, then diferenças Spot/Futures, APIs, limites e gaps são documentados com go/no-go.

**Riscos/dependências:** semântica Futures incorreta, rate limits e mudanças de API; depende de R1.0, US-020, US-030 e ADR futuro. **Plano/go-no-go:** capability matrix, contract tests e benchmark operacional; `go` somente sem perda de semântica ou boundary de risco.

- `TASK-US-140-01` — Mapear contratos.
  - `SUBTASK-TASK-US-140-01-01` Inventariar API e eventos.
  - `SUBTASK-TASK-US-140-01-02` Mapear filtros/status.
  - `SUBTASK-TASK-US-140-01-03` Comparar rate limits com Spot.
- `TASK-US-140-02` — Prototipar contract tests.
  - `SUBTASK-TASK-US-140-02-01` Capturar fixtures versionadas.
  - `SUBTASK-TASK-US-140-02-02` Testar divergências de capacidade.
  - `SUBTASK-TASK-US-140-02-03` Emitir relatório go/no-go.

### US-141 — Modelar leverage, margin e liquidation risk

**Valor/escopo:** definir invariantes de margem e perda antes de qualquer execução. **Não escopo:** otimizar alavancagem. **AC:** Given cenários de mark price/margin, when modelo é simulado, then liquidação, ADL e perda máxima são conservadores e auditáveis.

**Riscos/dependências:** perda não limitada, mark/index divergence e regras de venue; depende de US-140, ledger e Risk Engine estendidos. **Plano/go-no-go:** simulador conservador, property/stress tests e revisão independente; `no-go` se a perda máxima ou liquidação não forem reconstruíveis.

- `TASK-US-141-01` — Especificar risco.
  - `SUBTASK-TASK-US-141-01-01` Modelar margin modes.
  - `SUBTASK-TASK-US-141-01-02` Modelar mark/index price.
  - `SUBTASK-TASK-US-141-01-03` Modelar liquidation e ADL.
- `TASK-US-141-02` — Validar simulador.
  - `SUBTASK-TASK-US-141-02-01` Criar stress fixtures.
  - `SUBTASK-TASK-US-141-02-02` Provar invariantes de exposição.
  - `SUBTASK-TASK-US-141-02-03` Definir rejection thresholds.

### US-142 — Avaliar posições short

**Valor/escopo:** definir lifecycle, borrow/margin exposure e PnL short. **Não escopo:** estratégia short. **AC:** Given posição short simulada, when fills/funding/liquidação ocorrem, then ledger, exposição e PnL reconciliam.

**Riscos/dependências:** borrow indisponível, exposição invertida e PnL incorreto; depende de US-140, US-141 e ledger multi-commodity. **Plano/go-no-go:** cenários de lifecycle/fees/funding/liquidação e reconciliação; `go` somente com invariantes financeiras comprovadas.

- `TASK-US-142-01` — Definir domínio short.
  - `SUBTASK-TASK-US-142-01-01` Modelar posição e collateral.
  - `SUBTASK-TASK-US-142-01-02` Modelar fees/funding.
  - `SUBTASK-TASK-US-142-01-03` Modelar close/reduce-only.
- `TASK-US-142-02` — Criar cenários.
  - `SUBTASK-TASK-US-142-02-01` Simular partial fills e gaps.
  - `SUBTASK-TASK-US-142-02-02` Simular liquidation.
  - `SUBTASK-TASK-US-142-02-03` Validar reconciliation.

### US-143 — Avaliar hedging

**Valor/escopo:** medir hedge effectiveness e basis risk. **Não escopo:** chamar exposição nominal igual de hedge perfeito. **AC:** Given hedge candidato, when stress é executado, then residual delta, basis, funding, liquidity e failure mode são quantificados.

**Riscos/dependências:** hedge failure, basis/liquidity risk e rebalance excessivo; depende de US-141, US-142 e attribution confiável. **Plano/go-no-go:** stress de correlação, funding, gap e venue outage; `go` somente se risco residual e custos atenderem envelope aprovado.

- `TASK-US-143-01` — Especificar objetivo de hedge.
  - `SUBTASK-TASK-US-143-01-01` Definir exposure alvo.
  - `SUBTASK-TASK-US-143-01-02` Selecionar instrumento e rebalance.
  - `SUBTASK-TASK-US-143-01-03` Definir limits.
- `TASK-US-143-02` — Validar eficácia.
  - `SUBTASK-TASK-US-143-02-01` Executar stress e custos.
  - `SUBTASK-TASK-US-143-02-02` Testar failure/rollback.
  - `SUBTASK-TASK-US-143-02-03` Emitir decisão go/no-go.

### US-144 — Pesquisar funding/basis

**Valor/escopo:** dataset e benchmark líquido para funding/basis. **Não escopo:** rentabilidade garantida. **AC:** Given histórico point-in-time, when pesquisa roda, then custos, crowding, liquidation e leakage são modelados.

**Riscos/dependências:** survivorship/leakage, histórico insuficiente e crowding; depende de US-024, US-050 e dados licenciados. **Plano/go-no-go:** Dataset Card, benchmark líquido OOS e stress de capacity; `no-go` se dados point-in-time ou edge líquido não forem comprovados.

- `TASK-US-144-01` — Construir dataset spec.
  - `SUBTASK-TASK-US-144-01-01` Definir funding e basis.
  - `SUBTASK-TASK-US-144-01-02` Definir mark/index alignment.
  - `SUBTASK-TASK-US-144-01-03` Definir provenance e QA.
- `TASK-US-144-02` — Executar research gate.
  - `SUBTASK-TASK-US-144-02-01` Rodar benchmark líquido.
  - `SUBTASK-TASK-US-144-02-02` Medir stress e capacity.
  - `SUBTASK-TASK-US-144-02-03` Registrar decisão.

## E-15 — Market Neutral & Arbitrage

### US-150 — Pesquisar pairs/stat-arb

**Hipótese/valor:** relações relativas estáveis podem produzir edge líquido diversificante. **Não escopo:** selecionar pares após observar holdout ou executar capital. **Riscos/dependências:** cointegration breakdown, borrow, custos e capacity; depende de US-050, US-053 e US-054. **Plano/go-no-go:** preregistration, walk-forward, regime-break stress e benchmark cash; `go` somente com estabilidade OOS e custos conservadores.

**AC:** Given pares sem lookahead, when walk-forward/stress roda, then estabilidade, custos, borrow/capacity e breakdown de cointegration são explícitos.
- `TASK-US-150-01` — Pré-registrar hipótese.
  - `SUBTASK-TASK-US-150-01-01` Fixar universe/selection.
  - `SUBTASK-TASK-US-150-01-02` Fixar testes/trial budget.
  - `SUBTASK-TASK-US-150-01-03` Fixar rejection criteria.
- `TASK-US-150-02` — Validar robustez.
  - `SUBTASK-TASK-US-150-02-01` Executar OOS com custos.
  - `SUBTASK-TASK-US-150-02-02` Testar regime breaks.
  - `SUBTASK-TASK-US-150-02-03` Empacotar evidência.

### US-151 — Pesquisar arbitragem triangular

**Hipótese/valor:** inconsistências transitórias podem superar custos sob execução conservadora. **Não escopo:** assumir atomicidade entre legs ou usar livro stale. **Riscos/dependências:** leg risk, latency, partial fills e inventário residual; depende de market data sincronizado, US-022, US-051 e US-082. **Plano/go-no-go:** replay com freshness, latency e unwind adversos; `go` somente se edge líquido sobreviver ao pior caminho plausível.

**AC:** Given livros sincronizados conservadoramente, when oportunidade é simulada, then atomicity risk, latency, partial fills, fees e inventory residual determinam o resultado líquido.
- `TASK-US-151-01` — Especificar grafo/execução.
  - `SUBTASK-TASK-US-151-01-01` Enumerar cycles e freshness.
  - `SUBTASK-TASK-US-151-01-02` Definir sizing.
  - `SUBTASK-TASK-US-151-01-03` Definir abort/unwind.
- `TASK-US-151-02` — Simular failure modes.
  - `SUBTASK-TASK-US-151-02-01` Testar leg risk/partial fill.
  - `SUBTASK-TASK-US-151-02-02` Testar stale book.
  - `SUBTASK-TASK-US-151-02-03` Validar unwind residual.

### US-152 — Avaliar adapter multi-exchange

**Hipótese/valor:** boundaries por capability podem adicionar venue sem esconder diferenças críticas. **Não escopo:** lowest-common-denominator API ou execução multi-exchange imediata. **Riscos/dependências:** clocks, IDs, precisão, custódia e reconciliação divergentes; depende de R1.0 e ADR específico. **Plano/go-no-go:** capability matrix, contract/reconciliation spike e threat model; `no-go` se a abstração ocultar semântica financeira.

**AC:** Given duas exchanges heterogêneas, when contrato comum é avaliado, then capacidades não são escondidas por abstração e reconciliação permanece por venue.
- `TASK-US-152-01` — Criar capability matrix.
  - `SUBTASK-TASK-US-152-01-01` Comparar orders/streams.
  - `SUBTASK-TASK-US-152-01-02` Comparar IDs/precision.
  - `SUBTASK-TASK-US-152-01-03` Registrar capacidades ausentes.
- `TASK-US-152-02` — Definir boundaries.
  - `SUBTASK-TASK-US-152-02-01` Isolar venue ledger.
  - `SUBTASK-TASK-US-152-02-02` Modelar clocks/errors.
  - `SUBTASK-TASK-US-152-02-03` Produzir ADR go/no-go.

### US-153 — Pesquisar XEMM/cross-exchange

**Hipótese/valor:** maker em um venue e hedge em outro pode gerar edge líquido com inventário limitado. **Não escopo:** transferências instantâneas ou hedge garantido. **Riscos/dependências:** venue outage, stale hedge, transfer delay e capital fragmentado; depende de US-152, US-081 e ledger por venue. **Plano/go-no-go:** stress multi-venue, unwind e capacity/capital analysis; `go` somente com failure envelope e PnL líquido robustos.

**AC:** Given venues com latência e inventário separados, when stress roda, then transfer delay, hedge failure, fees e venue outage são refletidos no PnL/risk.
- `TASK-US-153-01` — Especificar estratégia.
  - `SUBTASK-TASK-US-153-01-01` Definir maker/hedge lifecycle.
  - `SUBTASK-TASK-US-153-01-02` Definir inventory limits.
  - `SUBTASK-TASK-US-153-01-03` Definir capital allocation.
- `TASK-US-153-02` — Validar riscos.
  - `SUBTASK-TASK-US-153-02-01` Simular venue outage/stale hedge.
  - `SUBTASK-TASK-US-153-02-02` Simular transfer delay.
  - `SUBTASK-TASK-US-153-02-03` Validar unwind.

## E-16 — Market Making Advanced

### US-160 — Construir plano de simulador L3

**Hipótese/valor:** dados L3 adequados permitem estimar queue position/fills melhor que L1. **Não escopo:** fabricar sequência ausente ou coletar dados sem licença. **Riscos/dependências:** licença, gaps, timestamp impreciso e modelo não calibrável; depende de Dataset Card, storage capacity e benchmark L1. **Plano/go-no-go:** auditoria de dados, replay prototype e calibration test; `needs-data/no-go` sem coverage suficiente.

**AC:** Given dados L3 adequados, when replay é projetado, then queue position, cancels, latency e fill uncertainty têm contrato verificável; sem dados adequados a opção é rejeitada.
- `TASK-US-160-01` — Auditar dados/licença.
  - `SUBTASK-TASK-US-160-01-01` Medir coverage.
  - `SUBTASK-TASK-US-160-01-02` Auditar timestamps/sequence/gaps.
  - `SUBTASK-TASK-US-160-01-03` Confirmar licença/uso.
- `TASK-US-160-02` — Especificar simulador.
  - `SUBTASK-TASK-US-160-02-01` Definir queue model.
  - `SUBTASK-TASK-US-160-02-02` Definir latency/calibration.
  - `SUBTASK-TASK-US-160-02-03` Definir validation gate.

### US-161 — Pesquisar market making inventory-aware

**Hipótese/valor:** quoting condicionado ao inventário pode capturar spread sem violar caps. **Não escopo:** produção sem simulador L3 validado. **Riscos/dependências:** adverse selection, inventory runaway e cancel limits; depende de US-160, Risk Engine e execution attribution. **Plano/go-no-go:** preregistration, L3 replay, toxicity/capacity stress e baseline; `go` somente com tails e inventário dentro do envelope.

**AC:** Given inventory limits, when simulação adversa roda, then quoting reduz risco, respeita caps e contabiliza adverse selection/custos.
- `TASK-US-161-01` — Pré-registrar policy.
  - `SUBTASK-TASK-US-161-01-01` Definir quotes/skew.
  - `SUBTASK-TASK-US-161-01-02` Definir inventory envelope.
  - `SUBTASK-TASK-US-161-01-03` Definir kill criteria.
- `TASK-US-161-02` — Validar policy.
  - `SUBTASK-TASK-US-161-02-01` Executar L3 replay.
  - `SUBTASK-TASK-US-161-02-02` Medir toxicity/stress.
  - `SUBTASK-TASK-US-161-02-03` Medir capacity.

### US-162 — Pesquisar Avellaneda-Stoikov

**Hipótese/valor:** modelo calibrado pode superar uma policy simples após custos. **Não escopo:** assumir Poisson/volatilidade estacionários sem teste. **Riscos/dependências:** miscalibration, regime shift e sensitivity extrema; depende de US-160, US-161 e dados suficientes. **Plano/go-no-go:** calibration OOS, sensitivity, ablation e regime stress; `no-go` se não superar baseline robustamente.

**AC:** Given baseline simples e parâmetros calibrados OOS, when comparados, then ganho líquido e estabilidade superam baseline sem violar inventory/tail limits.
- `TASK-US-162-01` — Definir modelo/assunções.
  - `SUBTASK-TASK-US-162-01-01` Definir arrival/volatility.
  - `SUBTASK-TASK-US-162-01-02` Definir risk aversion.
  - `SUBTASK-TASK-US-162-01-03` Definir calibration protocol.
- `TASK-US-162-02` — Executar benchmark.
  - `SUBTASK-TASK-US-162-02-01` Rodar OOS/sensitivity.
  - `SUBTASK-TASK-US-162-02-02` Rodar ablation contra baseline.
  - `SUBTASK-TASK-US-162-02-03` Registrar rejection decision.

### US-163 — Controlar adverse selection

**Hipótese/valor:** sinais de toxicity podem reduzir participação antes de perdas assimétricas. **Não escopo:** permitir que modelo aprendido altere hard limits. **Riscos/dependências:** label leakage, false positives e reação tardia; depende de US-160, US-161 e markout point-in-time. **Plano/go-no-go:** labels temporais, replay/shadow, false-positive/latency analysis e fallback; `go` somente com redução líquida de tail loss.

**AC:** Given sinais de toxicity, when threshold dispara, then quote width/size ou participation reduz dentro de envelope e decision trace explica a ação.
- `TASK-US-163-01` — Definir métricas.
  - `SUBTASK-TASK-US-163-01-01` Especificar markout/imbalance.
  - `SUBTASK-TASK-US-163-01-02` Especificar toxicity labels.
  - `SUBTASK-TASK-US-163-01-03` Validar temporalidade dos labels.
- `TASK-US-163-02` — Validar controles.
  - `SUBTASK-TASK-US-163-02-01` Medir false positives/latency.
  - `SUBTASK-TASK-US-163-02-02` Testar fallback.
  - `SUBTASK-TASK-US-163-02-03` Produzir shadow evidence.

Evidência padrão: `EVIDENCE-TASK-US-XXX-NN-<date>`; o resultado permitido é `go`, `no-go` ou `needs-data`, nunca promoção automática.
