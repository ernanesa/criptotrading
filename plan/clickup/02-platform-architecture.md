# Manifesto ClickUp — Plataforma, Arquitetura, Exchange, Segurança e Operações

**Fonte:** `plan/` revisado em 2026-09-19
**Modo:** manifesto exato para publicação; não constitui evidência de implementação
**Escopo:** E-00, E-01, E-02, E-03, E-11, E-12 e E-18
**Product Goal:** GOAL-001

## Convenções de publicação

- Preservar IDs em título e campo customizado `Artifact ID`.
- Criar Epic → Story → Task → Subtask, mantendo as dependências declaradas.
- `active` é scope e publica com status `Proposed`, nunca implica `Ready`; DoR continua obrigatório.
- `deferred` é scope e publica com status `Deferred/Option`; não estimar nem iniciar sem decisão explícita.
- `Source Commit/Hash` é injetado do `HEAD` limpo imediatamente antes da publicação; campos ainda sem decisão permanecem `Not defined` e bloqueiam `Ready`.
- Toda task concluída anexa `TEST-*`, comando, saída observada e `EVIDENCE-*`.
- Comandos são contratos esperados e devem ser ajustados aos paths criados no bootstrap, sem reduzir os gates.
- Segurança financeira prevalece sobre disponibilidade: incerteza de estado bloqueia novas exposições.

## E-00 — Governance & Bootstrap

**Scope/Status:** active / Proposed
**Outcome:** repositório reproduzível, rastreável e protegido contra drift de runtime, dependências e ambientes.
**Não escopo:** lógica de trading, credenciais reais ou hosting cloud.

### US-001 — Estruturar o repositório para evolução segura

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** permitir que qualquer mudança seja restaurada, compilada e validada com versões explícitas de .NET 11, C# 15, Node e pnpm.
**Contexto:** o projeto ainda não possui implementação; o bootstrap é a raiz de todos os gates posteriores.
**Escopo:** solution, projetos, build props, packages, lockfiles, frontend shell técnico, EditorConfig, Git ignore e CI básico.
**Não escopo:** domínio, integração Binance e deploy cloud.
**Contratos/falhas/security:** `net11.0` sem fallback; restore locked; secrets e artefatos locais ignorados; versão incompatível falha cedo e com diagnóstico.
**Dependências:** ADR-001, ADR-002, ADR-003, ADR-019.

**AC:**

- **Given** checkout limpo e pré-requisitos documentados, **When** o bootstrap é executado, **Then** backend e frontend restauram, compilam e testam com versões fixadas.
- **Given** lockfile divergente ou SDK incompatível, **When** CI executa, **Then** falha antes de produzir artefato.
- **Given** arquivo com padrão de secret, **When** scanners executam, **Then** o gate bloqueia e não publica seu conteúdo.

**Tasks:**

- `TASK-US-001-01` — Criar skeleton e baseline de build.
  - `SUBTASK-TASK-US-001-01-01` — Fixar SDK/runtime/linguagem em `global.json` e build props.
  - `SUBTASK-TASK-US-001-01-02` — Criar solution, projetos e referências iniciais.
  - `SUBTASK-TASK-US-001-01-03` — Centralizar packages, analyzers, nullable e warnings.
- `TASK-US-001-02` — Fixar toolchain frontend.
  - `SUBTASK-TASK-US-001-02-01` — Fixar Node/pnpm e lockfile.
  - `SUBTASK-TASK-US-001-02-02` — Configurar lint, typecheck, unit test e build.
- `TASK-US-001-03` — Criar CI e hygiene do repositório.
  - `SUBTASK-TASK-US-001-03-01` — Executar restore/build/test/format frontend e backend.
  - `SUBTASK-TASK-US-001-03-02` — Executar secret/dependency scan e reter evidence.

**Testes/evidência:** `dotnet restore --locked-mode`, `dotnet build --no-restore`, `dotnet test --no-build`, `pnpm install --frozen-lockfile`, `pnpm lint`, `pnpm typecheck`, `pnpm test`, `pnpm build`; `EVIDENCE-TASK-US-001-*-<date>`.

### US-002 — Versionar backlog e decisões arquiteturais

**Scope/Status/Prioridade:** active / Proposed / P1
**Objetivo/valor:** impedir que requisitos e decisões percam autoria, contexto, trade-offs ou histórico.
**Contexto:** documentação e ClickUp devem referenciar a mesma identidade estável.
**Escopo:** templates, política ADR, labels/statuses, supersession e revisão.
**Não escopo:** decidir antecipadamente arquitetura ainda não necessária.
**Contratos/falhas/security:** IDs não são reutilizados; decisão superseded permanece auditável; mudança crítica requer reviewer.
**Dependências:** US-001, ADR-014.

**AC:**

- **Given** decisão arquitetural duradoura, **When** aceita, **Then** ADR registra opções, decisão, consequências, rollout, rollback e revisores.
- **Given** requisito substituído, **When** manifesto é validado, **Then** existe `supersedes` e a fonte antiga não permanece ativa.

**Tasks:**

- `TASK-US-002-01` — Configurar governança de artefatos.
  - `SUBTASK-TASK-US-002-01-01` — Validar templates de ADR/story/task/evidence.
  - `SUBTASK-TASK-US-002-01-02` — Documentar criação, revisão e supersession.
- `TASK-US-002-02` — Configurar taxonomia de backlog.
  - `SUBTASK-TASK-US-002-02-01` — Definir statuses, priorities, releases e custom fields.
  - `SUBTASK-TASK-US-002-02-02` — Definir regras de parent, dependency e deferred.

**Testes/evidência:** revisão amostral de um ADR e uma story; validator sem ID duplicado; `EVIDENCE-TASK-US-002-*-<date>`.

### US-003 — Separar ambientes explicitamente

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** impedir envio de comando ou leitura de credencial no ambiente errado.
**Contexto:** DEV, PAPER, TESTNET, DEMO e PRODUCTION possuem endpoints, dados e risco distintos.
**Escopo:** modelo tipado, configuração, startup validation, namespaces e banner.
**Não escopo:** fornecer credenciais live ou habilitar CANARY/LIVE.
**Contratos/falhas/security:** combinação inconsistente falha antes da conexão; secrets por referência; ambiente aparece em health/log/UI.
**Dependências:** US-001; RF-003, RF-006, RF-135; AC-ENV-01, AC-UI-01.

**AC:**

- **Given** endpoint Testnet com namespace/secret Production, **When** o Worker inicia, **Then** aborta antes de conectar.
- **Given** processo saudável, **When** operador consulta health/UI/log, **Then** ambiente e order mode aparecem sem ambiguidade.

**Tasks:**

- `TASK-US-003-01` — Implementar contrato de ambiente.
  - `SUBTASK-TASK-US-003-01-01` — Modelar endpoints, namespace, key reference e order mode.
  - `SUBTASK-TASK-US-003-01-02` — Validar matriz e rejeitar combinações inválidas.
- `TASK-US-003-02` — Propagar identidade operacional.
  - `SUBTASK-TASK-US-003-02-01` — Expor ambiente em health/log/trace/metrics.
  - `SUBTASK-TASK-US-003-02-02` — Expor banner persistente no contrato da UI.
- `TASK-US-003-03` — Testar isolamento.
  - `SUBTASK-TASK-US-003-03-01` — Cobrir todas as combinações inválidas.
  - `SUBTASK-TASK-US-003-03-02` — Provar separação de namespaces e secret references.

**Testes/evidência:** `TEST-ENV-matrix`, `TEST-ENV-startup-fail-fast`, testes de configuração e screenshot/response do banner; `EVIDENCE-TASK-US-003-*-<date>`.

## E-01 — Platform Foundation

**Scope/Status:** active / Proposed
**Outcome:** núcleo modular, determinístico, durável, reconciliável e reconstruível.
**Não escopo:** full Event Sourcing ou broker distribuído no MVP.

### US-010 — Impedir acoplamento entre Strategy, Risk, Execution e Exchange

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** tornar impossível uma Strategy contornar Risk/OMS ou acessar credenciais.
**Contexto:** arquitetura é modular monolith com Ports & Adapters.
**Escopo:** projetos/módulos, APIs internas, composition roots e architecture tests.
**Não escopo:** microservices ou plugin loading remoto.
**Contratos/falhas/security:** fluxo obrigatório `Strategy→Intent→Portfolio→Risk→Execution→OMS→Exchange`; referência proibida falha no CI.
**Dependências:** US-001; ADR-003, ADR-007.

**AC:**

- **Given** assembly Strategy, **When** architecture tests executam, **Then** nenhuma referência a gateway de ordem, secrets ou adapter Binance é permitida.
- **Given** composição do Worker, **When** inicia, **Then** cada port possui exatamente um adapter válido para o ambiente.

**Tasks:**

- `TASK-US-010-01` — Definir módulos e dependências.
  - `SUBTASK-TASK-US-010-01-01` — Criar mapa de referências permitidas/proibidas.
  - `SUBTASK-TASK-US-010-01-02` — Definir APIs internas mínimas por boundary.
- `TASK-US-010-02` — Implementar composition roots.
  - `SUBTASK-TASK-US-010-02-01` — Compor Worker/API/Research sem service locator.
  - `SUBTASK-TASK-US-010-02-02` — Validar options e adapters no startup.
- `TASK-US-010-03` — Automatizar enforcement.
  - `SUBTASK-TASK-US-010-03-01` — Criar architecture tests de referência/ciclo.
  - `SUBTASK-TASK-US-010-03-02` — Integrar testes ao CI.

**Testes/evidência:** `dotnet test --filter Category=Architecture`; grafo de dependências; `EVIDENCE-TASK-US-010-*-<date>`.

### US-011 — Fornecer tempo e IDs determinísticos

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** permitir replay, correlação e medição de latência sem ambiguidade temporal.
**Contexto:** exchange time, event time, ingestion time e relógio monotônico possuem semânticas diferentes.
**Escopo:** `TimeProvider`, IDs e serialização temporal UTC.
**Não escopo:** sincronizador NTP do sistema operacional.
**Contratos/falhas/security:** IDs não expõem secrets; clientOrderId respeita formato/limite oficial; clock jump não torna duração negativa.
**Dependências:** US-010; RNF-030, RNF-031, RNF-032.

**AC:**

- **Given** mesmo clock/seed/input, **When** replay repete, **Then** decisões e IDs derivados são equivalentes.
- **Given** salto no wall clock, **When** latência é medida, **Then** monotonic clock preserva duração válida.

**Tasks:**

- `TASK-US-011-01` — Implementar abstrações temporais.
  - `SUBTASK-TASK-US-011-01-01` — Fornecer wall/monotonic/test clock.
  - `SUBTASK-TASK-US-011-01-02` — Modelar timestamps e UTC serialization.
- `TASK-US-011-02` — Implementar identidade e causalidade.
  - `SUBTASK-TASK-US-011-02-01` — Gerar correlation/causation/operation IDs.
  - `SUBTASK-TASK-US-011-02-02` — Gerar clientOrderId único e validável.
- `TASK-US-011-03` — Provar invariantes.
  - `SUBTASK-TASK-US-011-03-01` — Testar concorrência/unicidade.
  - `SUBTASK-TASK-US-011-03-02` — Testar replay e clock jumps.

**Testes/evidência:** property tests de unicidade, round-trip UTC e relógio; `EVIDENCE-TASK-US-011-*-<date>`.

### US-012 — Persistir journal crítico append-only

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** reconstruir toda decisão/ordem/fill/intervenção mesmo quando traces forem amostrados.
**Contexto:** audit financeiro não é log operacional.
**Escopo:** envelope, schema, persistência, consulta, hashing, ACL e retenção.
**Não escopo:** Event Sourcing integral.
**Contratos/falhas/security:** append-only; schema versionado; redaction; falha de write impede nova exposição.
**Dependências:** US-011, US-124; RNF-040, RNF-041, RNF-042, RNF-043, RNF-044; AC-AUDIT-01.

**AC:**

- **Given** evento crítico, **When** persistido, **Then** contém IDs, versões, hash, actor, environment e timestamps necessários.
- **Given** storage indisponível, **When** write crítico falha, **Then** sistema bloqueia novas entradas e alerta sem usar telemetry como substituto.

**Tasks:**

- `TASK-US-012-01` — Definir envelope e catálogo.
  - `SUBTASK-TASK-US-012-01-01` — Especificar campos obrigatórios e schemas.
  - `SUBTASK-TASK-US-012-01-02` — Definir compatibility/upcast policy.
- `TASK-US-012-02` — Persistir e consultar journal.
  - `SUBTASK-TASK-US-012-02-01` — Criar migration append-only e índices.
  - `SUBTASK-TASK-US-012-02-02` — Consultar por correlation/causation/aggregate.
- `TASK-US-012-03` — Proteger e testar journal.
  - `SUBTASK-TASK-US-012-03-01` — Implementar redaction/ACL/hash/retention.
  - `SUBTASK-TASK-US-012-03-02` — Testar failure mode e schema evolution.

**Testes/evidência:** integration tests PostgreSQL, tentativa de mutation rejeitada, audit reconstruction; `EVIDENCE-TASK-US-012-*-<date>`.

### US-013 — Manter ledger multi-commodity de dupla entrada

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** reconstruir balances, fees, cost basis, exposição e PnL sem depender de posição canônica inexistente na Binance Spot.
**Contexto:** fills e fees podem usar base, quote, BNB ou outro ativo.
**Escopo:** accounts, postings, fee/rebate, valuation, projeções e compensating entries.
**Não escopo:** aconselhamento ou cálculo fiscal definitivo.
**Contratos/falhas/security:** postings imutáveis e balanceados por commodity; duplicate fill é idempotente; ajustes nunca apagam histórico.
**Dependências:** US-012, US-011; RF-044, RF-045, RF-046, RF-047, RF-048; AC-LEDGER-01, AC-LEDGER-02.

**AC:**

- **Given** fill com fee em qualquer ativo, **When** aplicado, **Then** postings balanceados preservam ativo, quantidade, valuation e IDs externos.
- **Given** depósito/trade externo/mismatch, **When** reconciliado, **Then** compensating entry explica a diferença sem update destrutivo.

**Tasks:**

- `TASK-US-013-01` — Implementar core contábil.
  - `SUBTASK-TASK-US-013-01-01` — Modelar accounts/postings/commodities/invariantes.
  - `SUBTASK-TASK-US-013-01-02` — Persistir atomicamente e deduplicar eventos.
- `TASK-US-013-02` — Registrar trades e fees.
  - `SUBTASK-TASK-US-013-02-01` — Postar principal/proceeds/fee/rebate.
  - `SUBTASK-TASK-US-013-02-02` — Registrar valuation source/time.
- `TASK-US-013-03` — Construir projeções e ajustes.
  - `SUBTASK-TASK-US-013-03-01` — Derivar free/locked/cost basis/PnL.
  - `SUBTASK-TASK-US-013-03-02` — Suportar compensating entries e rebuild.
- `TASK-US-013-04` — Provar propriedades financeiras.
  - `SUBTASK-TASK-US-013-04-01` — Testar fees base/quote/BNB, dust e externos.
  - `SUBTASK-TASK-US-013-04-02` — Testar rebuild e duplicate fill.

**Testes/evidência:** unit/property/integration tests; ledger trial balance e rebuild hash; `EVIDENCE-TASK-US-013-*-<date>`.

### US-014 — Garantir durabilidade por classe de evento

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** impedir perda ou duplicação de side effect financeiro sob crash, overload ou shutdown.
**Contexto:** Channels fornecem backpressure, não durabilidade.
**Escopo:** classification, outbox/inbox, dedupe, poison handling, overflow e recovery.
**Não escopo:** promessa de exactly-once distribuído.
**Contratos/falhas/security:** persist-before-submit; at-least-once; financeiro nunca dropa; saturação incerta bloqueia entradas.
**Dependências:** US-012, US-013; ADR-016; AC-DURABILITY-01, AC-DURABILITY-02.

**AC:**

- **Given** ordem autorizada, **When** adapter pode enviar, **Then** intent/risk/policy/idempotency/outbox já estão duráveis.
- **Given** crash ou overflow, **When** recupera, **Then** não há submit cego nem descarte financeiro e a condição é observável.

**Tasks:**

- `TASK-US-014-01` — Classificar eventos e filas.
  - `SUBTASK-TASK-US-014-01-01` — Declarar capacity/ordering/overflow/owner por canal.
  - `SUBTASK-TASK-US-014-01-02` — Definir shutdown/drain/freshness policy.
- `TASK-US-014-02` — Implementar outbox/inbox.
  - `SUBTASK-TASK-US-014-02-01` — Persistir estado e outbox na mesma transação.
  - `SUBTASK-TASK-US-014-02-02` — Implementar lease/dedupe/retry/idempotent consumer.
- `TASK-US-014-03` — Tratar poison e overload.
  - `SUBTASK-TASK-US-014-03-01` — Quarentenar poison sem perder audit.
  - `SUBTASK-TASK-US-014-03-02` — Acionar block/SafeMode e métricas.
- `TASK-US-014-04` — Injetar falhas nos boundaries.
  - `SUBTASK-TASK-US-014-04-01` — Testar crash antes/depois do commit/submit.
  - `SUBTASK-TASK-US-014-04-02` — Testar duplicate, lag, overflow e shutdown.

**Testes/evidência:** fault-injection matrix e database assertions; `EVIDENCE-TASK-US-014-*-<date>`.

### US-015 — Validar rastreabilidade por máquina

**Scope/Status/Prioridade:** active / Proposed / P1
**Objetivo/valor:** impedir Done sem vínculo a requisito, teste e evidência.
**Contexto:** `traceability.yaml` é o manifesto canônico.
**Escopo:** schema, parser, validators e report.
**Não escopo:** substituir revisão humana.
**Contratos/falhas/security:** intervalos implícitos proibidos; superseded não ativo; evidence não pode conter secret.
**Dependências:** US-001, US-002; AC-TRACE-01.

**AC:**

- **Given** ID duplicado/órfão ou Done sem evidence, **When** validator executa, **Then** falha apontando ID e fonte.
- **Given** baseline válida, **When** report é gerado, **Then** mostra coverage Goal→Evidence e lacunas por severidade.

**Tasks:**

- `TASK-US-015-01` — Implementar schema e parser.
  - `SUBTASK-TASK-US-015-01-01` — Validar convenções e referências.
  - `SUBTASK-TASK-US-015-01-02` — Validar source/version/hash/freshness.
- `TASK-US-015-02` — Implementar regras de consistência.
  - `SUBTASK-TASK-US-015-02-01` — Detectar órfãos, duplicados, ciclos e superseded.
  - `SUBTASK-TASK-US-015-02-02` — Validar artefatos/gates críticos.
- `TASK-US-015-03` — Gerar evidence report.
  - `SUBTASK-TASK-US-015-03-01` — Produzir saída humana e machine-readable.
  - `SUBTASK-TASK-US-015-03-02` — Integrar CI e redaction scan.

**Testes/evidência:** fixtures válidas/inválidas e exit codes; `EVIDENCE-TASK-US-015-*-<date>`.

## E-02 — Binance Reference & Market Data

**Scope/Status:** active / Proposed
**Outcome:** dados resilientes, temporalmente corretos e bloqueados quando inválidos.
**Não escopo:** SBE/FIX no MVP ou hardcode de limites voláteis.

### US-020 — Aplicar símbolos, status e filtros dinâmicos

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** impedir ordens inválidas por precisão, status ou filtros desatualizados.
**Contexto:** `exchangeInfo` e filtros de conta são dinâmicos.
**Escopo:** adapter, snapshot, cache controlado, modelagem extensível e decimal rounding.
**Não escopo:** filtros estáticos por símbolo.
**Contratos/falhas/security:** fonte oficial revisada na implementação; snapshot/hash acompanha validação; unknown enum não é descartado silenciosamente.
**Dependências:** US-003, US-010, US-011; RF-010, RF-038, RF-039; AC-EXCHANGE-01.

**AC:**

- **Given** snapshot de filtros, **When** target vira ordem, **Then** todos os filtros/status presentes são avaliados com decimal exato.
- **Given** schema/enum desconhecido, **When** recebido, **Then** payload é preservado, alerta emitido e ação insegura bloqueada.

**Tasks:**

- `TASK-US-020-01` — Implementar reference-data adapter.
  - `SUBTASK-TASK-US-020-01-01` — Mapear symbols/status/permissions/filters.
  - `SUBTASK-TASK-US-020-01-02` — Mapear filtros de conta quando aplicáveis.
- `TASK-US-020-02` — Versionar e atualizar snapshots.
  - `SUBTASK-TASK-US-020-02-01` — Persistir source time/schema/hash.
  - `SUBTASK-TASK-US-020-02-02` — Implementar refresh/cache/failure policy.
- `TASK-US-020-03` — Validar ordens.
  - `SUBTASK-TASK-US-020-03-01` — Implementar decimal rounding por side/type.
  - `SUBTASK-TASK-US-020-03-02` — Testar filtros/status/enums desconhecidos.

**Testes/evidência:** contract fixtures oficiais versionadas, property tests de rounding e integração read-only; `EVIDENCE-TASK-US-020-*-<date>`.

### US-021 — Consumir trades e candles realtime resilientemente

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** manter feed fresco e impedir decisão sobre conexão degradada.
**Contexto:** WebSocket possui disconnects, lifecycle e limites.
**Escopo:** client, subscription, heartbeat, reconnect, staleness e métricas.
**Não escopo:** order book completo, coberto por US-022.
**Contratos/falhas/security:** backoff/jitter; reconnect storm protection; endpoints por ambiente; payload inválido vai para quarantine.
**Dependências:** US-003, US-020; AC-MARKET-01.

**AC:**

- **Given** stream saudável, **When** eventos chegam, **Then** timestamps/quality/age são atualizados dentro do threshold.
- **Given** disconnect ou stale threshold, **When** detectado, **Then** feed fica inelegível até resubscription e validação.

**Tasks:**

- `TASK-US-021-01` — Implementar lifecycle WebSocket.
  - `SUBTASK-TASK-US-021-01-01` — Conectar/subscrever/ping-pong/shutdown.
  - `SUBTASK-TASK-US-021-01-02` — Reconnect com backoff/jitter/resubscribe.
- `TASK-US-021-02` — Normalizar e qualificar eventos.
  - `SUBTASK-TASK-US-021-02-01` — Mapear trades/candles e tempos.
  - `SUBTASK-TASK-US-021-02-02` — Detectar stale/malformed/duplicate.
- `TASK-US-021-03` — Instrumentar e testar falhas.
  - `SUBTASK-TASK-US-021-03-01` — Emitir age/reconnect/error metrics.
  - `SUBTASK-TASK-US-021-03-02` — Testar disconnect/storm/quarantine.

**Testes/evidência:** replay/contract tests e integração read-only real; `EVIDENCE-TASK-US-021-*-<date>`.

### US-022 — Manter local order book correto

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** produzir best bid/ask e profundidade confiáveis para risco e execução.
**Contexto:** snapshot+delta exige sequência oficial e resync total após gap.
**Escopo:** buffer, snapshot, updates, sequence, invalidation e resync.
**Não escopo:** correção heurística silenciosa.
**Contratos/falhas/security:** update perdido/out-of-order invalida o livro; consumidores só recebem estado `Healthy`.
**Dependências:** US-020, US-021; AC-BOOK-01.

**AC:**

- **Given** snapshot e deltas válidos, **When** aplicados, **Then** níveis correspondem ao algoritmo oficial.
- **Given** gap/ordem inválida, **When** detectado, **Then** livro é descartado e ressincronizado antes de uso.

**Tasks:**

- `TASK-US-022-01` — Implementar state machine do book.
  - `SUBTASK-TASK-US-022-01-01` — Buffer/snapshot/window de sequence.
  - `SUBTASK-TASK-US-022-01-02` — Aplicar update e remover quantidade zero.
- `TASK-US-022-02` — Implementar invalidation/resync.
  - `SUBTASK-TASK-US-022-02-01` — Detectar gap/duplicate/out-of-order.
  - `SUBTASK-TASK-US-022-02-02` — Bloquear consumidor e reconstruir.
- `TASK-US-022-03` — Verificar correção/performance.
  - `SUBTASK-TASK-US-022-03-01` — Replay/property tests contra fixtures.
  - `SUBTASK-TASK-US-022-03-02` — Benchmark update/resync e métricas.

**Testes/evidência:** `TEST-BOOK-snapshot-delta`, gap/resync chaos e benchmark; `EVIDENCE-TASK-US-022-*-<date>`.

### US-023 — Gravar e reproduzir market data

**Scope/Status/Prioridade:** active / Proposed / P1
**Objetivo/valor:** permitir replay determinístico e pesquisa auditável.
**Contexto:** PostgreSQL não deve receber todo tick; histórico usa Parquet.
**Escopo:** recorder, schema, partitioning, checksums, replay clock e retention.
**Não escopo:** coletar order book indiscriminadamente.
**Contratos/falhas/security:** raw imutável; backpressure explícito; disco cheio alerta/bloqueia apenas dependências afetadas sem perder financeiro.
**Dependências:** US-011, US-021, US-124.

**AC:**

- **Given** sessão gravada, **When** replay executa duas vezes, **Then** sequência/hash e tempos lógicos são equivalentes.
- **Given** storage indisponível, **When** write falha, **Then** falha é observável e política de retenção/ingestão é aplicada sem corrupção parcial silenciosa.

**Tasks:**

- `TASK-US-023-01` — Definir storage contract.
  - `SUBTASK-TASK-US-023-01-01` — Especificar schema/partition/compression/metadata.
  - `SUBTASK-TASK-US-023-01-02` — Definir atomic write/checksum/retention.
- `TASK-US-023-02` — Implementar recorder.
  - `SUBTASK-TASK-US-023-02-01` — Batch/write com bounded queue.
  - `SUBTASK-TASK-US-023-02-02` — Tratar disk-full/corruption/retry.
- `TASK-US-023-03` — Implementar replay.
  - `SUBTASK-TASK-US-023-03-01` — Integrar replay clock e ordering.
  - `SUBTASK-TASK-US-023-03-02` — Testar round-trip/hash/determinismo.

**Testes/evidência:** recorder→Parquet→replay integration e fault tests; `EVIDENCE-TASK-US-023-*-<date>`.

### US-024 — Reconstruir datasets e universo point-in-time

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** prevenir lookahead e survivorship bias desde o primeiro dado.
**Contexto:** listing/delisting/filter history pode ser irrecuperável depois.
**Escopo:** temporal semantics, bronze/silver/gold/quarantine, corrections e snapshots de universo.
**Não escopo:** definir estratégia ou ranking quantitativo.
**Contratos/falhas/security:** `available_at`; correction cria nova versão `supersedes`; dataset usado nunca é mutado.
**Dependências:** US-020, US-023; RF-019, RF-020; AC-DATA-01, AC-UNIVERSE-01.

**AC:**

- **Given** timestamp T, **When** dataset/universo é reconstruído, **Then** usa somente informação disponível até T e persiste snapshot/hash.
- **Given** correção tardia, **When** aplicada, **Then** versão anterior permanece reproduzível e a nova registra supersession.

**Tasks:**

- `TASK-US-024-01` — Modelar temporalidade e lineage.
  - `SUBTASK-TASK-US-024-01-01` — Preservar cinco timestamps e `available_at`.
  - `SUBTASK-TASK-US-024-01-02` — Ligar raw→normalized→research.
- `TASK-US-024-02` — Capturar universe/reference history.
  - `SUBTASK-TASK-US-024-02-01` — Persistir exchangeInfo/status/listing snapshots.
  - `SUBTASK-TASK-US-024-02-02` — Registrar migration/delist/exclusion.
- `TASK-US-024-03` — Governar qualidade/correções.
  - `SUBTASK-TASK-US-024-03-01` — Quarentenar gaps/duplicates/ambiguous.
  - `SUBTASK-TASK-US-024-03-02` — Versionar correction/supersedes e Dataset Card.

**Testes/evidência:** time-travel, survivorship e immutable-hash tests; `EVIDENCE-TASK-US-024-*-<date>`.

## E-03 — Account & OMS

**Scope/Status:** active / Proposed
**Outcome:** lifecycle de ordem correto sob duplicação, reorder, timeout, reconnect e partial fill.
**Não escopo:** inferir `Filled` por sucesso HTTP/WS ou retry cego.

### US-030 — Consumir User Data Stream moderno

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** receber mudanças de conta, ordem e execução com baixa latência e recovery seguro.
**Contexto:** usar Spot WebSocket API atual, não listen-key REST legado.
**Escopo:** auth/subscription, mapping, lifecycle, reconnect e reconciliation trigger.
**Não escopo:** polling como fonte exclusiva.
**Contratos/falhas/security:** credencial nunca logada; sessão/endpoint por ambiente; lacuna bloqueia novas ações dependentes.
**Dependências:** US-003, US-011, US-020; RF-030, RF-031; AC-RECON-01.

**AC:**

- **Given** stream autenticado, **When** account/order event chega, **Then** é priorizado, correlacionado e persistido.
- **Given** termination/shutdown/reconnect, **When** stream volta, **Then** reconciliação termina antes de liberar entradas.

**Tasks:**

- `TASK-US-030-01` — Implementar sessão/subscription.
  - `SUBTASK-TASK-US-030-01-01` — Auth, subscribe, subscriptionId e heartbeat.
  - `SUBTASK-TASK-US-030-01-02` — Rotação/termination/shutdown/resubscribe.
- `TASK-US-030-02` — Mapear eventos de conta/ordem.
  - `SUBTASK-TASK-US-030-02-01` — Preservar external IDs/status/fees/STP.
  - `SUBTASK-TASK-US-030-02-02` — Tratar schema/enum desconhecido.
- `TASK-US-030-03` — Integrar recovery.
  - `SUBTASK-TASK-US-030-03-01` — Disparar block/reconcile após gap.
  - `SUBTASK-TASK-US-030-03-02` — Testar lifecycle e redaction.

**Testes/evidência:** contract/reconnect tests e integração Binance Testnet real quando credencial aprovada; `EVIDENCE-TASK-US-030-*-<date>`.

### US-031 — Implementar state machine de ordens robusta

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** representar autoridade externa sem dupla aplicação de fill ou transição ilegal.
**Contexto:** fill pode chegar antes do ack; cancel-replace pode ter resultado parcial.
**Escopo:** estados, transitions, partial fills, cancel/reject/expire/STP e ambiguous.
**Não escopo:** estratégia de execução.
**Contratos/falhas/security:** filled quantity monotônica e ≤ quantity; eventos idempotentes; unknown state força reconciliação.
**Dependências:** US-012, US-013, US-014, US-030; AC-OMS-02, AC-OMS-03.

**AC:**

- **Given** partial fill duplicado, **When** processado duas vezes, **Then** exposure/ledger mudam exatamente uma vez.
- **Given** fill antes do ack, **When** eventos são aplicados, **Then** estado final permanece válido e auditável.

**Tasks:**

- `TASK-US-031-01` — Definir aggregate/state machine.
  - `SUBTASK-TASK-US-031-01-01` — Enumerar estados/transições/invariantes.
  - `SUBTASK-TASK-US-031-01-02` — Modelar leg/result de listas/amend/cancel-replace.
- `TASK-US-031-02` — Aplicar eventos idempotentemente.
  - `SUBTASK-TASK-US-031-02-01` — Dedupe e reorder policy.
  - `SUBTASK-TASK-US-031-02-02` — Atualizar fills/exposure/ledger atomicamente.
- `TASK-US-031-03` — Cobrir falhas de lifecycle.
  - `SUBTASK-TASK-US-031-03-01` — Testar fill-before-ack/partial/cancel/STP.
  - `SUBTASK-TASK-US-031-03-02` — Testar transition inválida/unknown/reconcile.

**Testes/evidência:** model/property tests e persisted transition journal; `EVIDENCE-TASK-US-031-*-<date>`.

### US-032 — Enviar ordens idempotentemente

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** impedir ordem duplicada quando o resultado do submit é desconhecido.
**Contexto:** timeout/5xx não prova que exchange rejeitou o comando.
**Escopo:** intent, idempotency, outbox, submit, lookup-before-retry e ambiguity.
**Não escopo:** retry automático sem reconciliação.
**Contratos/falhas/security:** RiskDecision obrigatório; persist-before-submit; segredo/signature redacted.
**Dependências:** US-014, US-020, US-031; RF-036, RF-037, RF-043; AC-OMS-01, AC-OMS-03, AC-DURABILITY-01.

**AC:**

- **Given** timeout após submit, **When** resultado é desconhecido, **Then** OMS consulta por clientOrderId antes de qualquer novo submit.
- **Given** crash em qualquer boundary, **When** reinicia, **Then** intent é recuperado e reconciliado sem duplicação.

**Tasks:**

- `TASK-US-032-01` — Persistir comando autorizável.
  - `SUBTASK-TASK-US-032-01-01` — Gravar intent/risk/policy/config/idempotency/outbox.
  - `SUBTASK-TASK-US-032-01-02` — Impedir submit sem commit confirmado.
- `TASK-US-032-02` — Implementar submit/ambiguity workflow.
  - `SUBTASK-TASK-US-032-02-01` — Assinar/enviar/mapear resultado sem secret leakage.
  - `SUBTASK-TASK-US-032-02-02` — Persistir Unknown e consultar antes de retry.
- `TASK-US-032-03` — Injetar falhas.
  - `SUBTASK-TASK-US-032-03-01` — Testar timeout/5xx/crash/duplicate.
  - `SUBTASK-TASK-US-032-03-02` — Testar recovery com exchange event concorrente.

**Testes/evidência:** fault-injection e Testnet controlado; `EVIDENCE-TASK-US-032-*-<date>`.

### US-033 — Reconciliar automaticamente

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** restaurar confiança entre ledger/OMS local e fatos autoritativos da exchange.
**Contexto:** ocorre no startup, reconnect, timeout, periodic e sob demanda.
**Escopo:** balances, orders/lists, fills/trades, fees, ledger e projections.
**Não escopo:** sobrescrever histórico local destrutivamente.
**Contratos/falhas/security:** mismatch inexplicado bloqueia entrada; correção por compensating entry; snapshots protegidos.
**Dependências:** US-013, US-030, US-031, US-032; RF-042; AC-RECON-01.

**AC:**

- **Given** startup/reconnect, **When** reconciliação inicia, **Then** novas entradas permanecem bloqueadas até decisão Ready.
- **Given** diferença confirmada, **When** corrigida, **Then** Reconciliation Report e compensating entry preservam explicação.

**Tasks:**

- `TASK-US-033-01` — Coletar snapshots comparáveis.
  - `SUBTASK-TASK-US-033-01-01` — Consultar balances/orders/fills/fees por recorte.
  - `SUBTASK-TASK-US-033-01-02` — Normalizar source time/hash/pagination.
- `TASK-US-033-02` — Classificar e resolver mismatch.
  - `SUBTASK-TASK-US-033-02-01` — Comparar local/exchange por entidade.
  - `SUBTASK-TASK-US-033-02-02` — Aplicar block/compensation/projection rebuild.
- `TASK-US-033-03` — Orquestrar triggers/report.
  - `SUBTASK-TASK-US-033-03-01` — Integrar startup/reconnect/timeout/periodic/manual.
  - `SUBTASK-TASK-US-033-03-02` — Gerar Reconciliation Report e alert.

**Testes/evidência:** mismatch fixtures, Testnet snapshot e report assinado; `EVIDENCE-TASK-US-033-*-<date>`.

## E-11 — Security & Observability

**Scope/Status:** active / Proposed
**Outcome:** capital, credenciais, comandos e estado financeiro protegidos e operacionalmente explicáveis.
**Não escopo:** depender de secret manager ou telemetry cloud.

### US-110 — Gerenciar secrets por ambiente

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** impedir vazamento/reuso de credenciais e permitir rotação/revogação seguras.
**Contexto:** ambiente inicial é local; repositório contém apenas exemplos.
**Escopo:** provider local/Docker secret, permissions, validation, rotation e redaction.
**Não escopo:** AWS Secrets Manager ou credencial live em CI.
**Contratos/falhas/security:** withdrawal desabilitado; keys distintas; secret ausente/inseguro falha cedo; nunca logar valor.
**Dependências:** US-003, US-124; AC-SEC-01.

**AC:**

- **Given** secret/config/log/image/evidence, **When** scanners executam, **Then** nenhum valor sensível aparece.
- **Given** key revogada ou permissionamento inseguro, **When** startup/rotation executa, **Then** trading não é habilitado e evento seguro é auditado.

**Tasks:**

- `TASK-US-110-01` — Implementar provider local.
  - `SUBTASK-TASK-US-110-01-01` — Carregar referência fora do Git com permission check.
  - `SUBTASK-TASK-US-110-01-02` — Validar presença/scope/environment sem revelar valor.
- `TASK-US-110-02` — Implementar hygiene/redaction.
  - `SUBTASK-TASK-US-110-02-01` — Redigir headers/signatures/connections/exceptions.
  - `SUBTASK-TASK-US-110-02-02` — Scan repo/image/log/backup/evidence.
- `TASK-US-110-03` — Definir rotation/revocation.
  - `SUBTASK-TASK-US-110-03-01` — Documentar e testar troca/revogação.
  - `SUBTASK-TASK-US-110-03-02` — Provar separação de keys e no-withdrawal.

**Testes/evidência:** redaction tests, secret scanner e checklist de permissões sem material secreto; `EVIDENCE-TASK-US-110-*-<date>`.

### US-111 — Instrumentar OpenTelemetry end-to-end

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** reconstruir Signal→Risk→Order→Fill e separar latência da aplicação, fila, DB e exchange.
**Contexto:** audit financeiro permanece separado e não amostrado.
**Escopo:** logs JSON, metrics, traces, propagation, dashboards e exporters locais.
**Não escopo:** identificadores de alta cardinalidade em metric labels.
**Contratos/falhas/security:** semantic conventions versionadas; redaction; errors preservados; falha de telemetry não substitui audit.
**Dependências:** US-011, US-012, US-124; RNF-050, RNF-051, RNF-052.

**AC:**

- **Given** lifecycle completo, **When** pesquisado por correlation ID, **Then** traces/logs/audit reconstroem estágios e latências.
- **Given** order ID, **When** metrics são exportadas, **Then** ele não aparece como label de cardinalidade ilimitada.

**Tasks:**

- `TASK-US-111-01` — Definir conventions e propagation.
  - `SUBTASK-TASK-US-111-01-01` — Padronizar fields/names/attributes/resources.
  - `SUBTASK-TASK-US-111-01-02` — Propagar context entre Channels/HTTP/DB.
- `TASK-US-111-02` — Instrumentar sinais.
  - `SUBTASK-TASK-US-111-02-01` — Adicionar spans/logs aos boundaries críticos.
  - `SUBTASK-TASK-US-111-02-02` — Adicionar metrics bounded de sistema/trading.
- `TASK-US-111-03` — Configurar stack local e dashboards.
  - `SUBTASK-TASK-US-111-03-01` — Collector/exporters/retention/profiles.
  - `SUBTASK-TASK-US-111-03-02` — Dashboards System/Trading/Risk/Execution.
- `TASK-US-111-04` — Verificar sampling/redaction.
  - `SUBTASK-TASK-US-111-04-01` — Preservar failed/slow/retried/financial traces.
  - `SUBTASK-TASK-US-111-04-02` — Testar cardinality e secret leakage.

**Testes/evidência:** telemetry integration, dashboard snapshots e label-cardinality report; `EVIDENCE-TASK-US-111-*-<date>`.

### US-112 — Proteger a supply chain

**Scope/Status/Prioridade:** active / Proposed / P1
**Objetivo/valor:** reduzir risco de dependência, workflow ou imagem comprometidos.
**Contexto:** releases exigem locks, scans, SBOM, digest e proveniência.
**Escopo:** scanners, update policy, SBOM, signing/provenance e branch rules.
**Não escopo:** aceitar vulnerabilidade automaticamente por conveniência.
**Contratos/falhas/security:** exceção tem owner/justificativa/expiry; release referencia digest, nunca `latest`; CI sem secret longa duração.
**Dependências:** US-001; R-15.

**Sequência:** scanning/assinatura de imagem integra com os artefatos produzidos posteriormente por US-120.

**AC:**

- **Given** vulnerabilidade crítica/secret/proveniência inválida, **When** pipeline executa, **Then** release é bloqueada com finding acionável.
- **Given** artefato aprovado, **When** verificado, **Then** commit, SBOM, digest e provenance correspondem.

**Tasks:**

- `TASK-US-112-01` — Configurar scans e updates.
  - `SUBTASK-TASK-US-112-01-01` — Dependency/code/secret/image scanning.
  - `SUBTASK-TASK-US-112-01-02` — Updates por PR isolado e policy de exceção.
- `TASK-US-112-02` — Produzir supply-chain evidence.
  - `SUBTASK-TASK-US-112-02-01` — Gerar SBOM e image digest.
  - `SUBTASK-TASK-US-112-02-02` — Assinar/gerar provenance e verificar.
- `TASK-US-112-03` — Proteger fluxo de revisão.
  - `SUBTASK-TASK-US-112-03-01` — Branch/check/reviewer rules.
  - `SUBTASK-TASK-US-112-03-02` — Testar pipeline com artefato adulterado.

**Testes/evidência:** scan reports, SBOM, verification output e exception register; `EVIDENCE-TASK-US-112-*-<date>`.

### US-113 — Modelar ameaças e proteger administração crítica

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** impedir acesso indevido, replay e alteração de risco/credencial/live sem step-up.
**Contexto:** control plane local continua sendo boundary de alto impacto.
**Escopo:** threat model, identity/authz, MFA/WebAuthn, sessions, anti-replay, web/network controls.
**Não escopo:** expor dashboard publicamente.
**Contratos/falhas/security:** default deny; sessão revogada falha; ações críticas geram audit independente; caminho kill switch autenticado.
**Dependências:** US-003, US-010, US-110; RF-001, RF-002, RF-007, RF-008, RF-009; AC-SEC-01, AC-SEC-02.

**AC:**

- **Given** mudança live/risk/credential/reset, **When** solicitada, **Then** autorização, MFA/step-up, anti-replay e audit são exigidos.
- **Given** sessão revogada/expirada ou request repetido, **When** usado, **Then** comando é negado sem side effect.

**Tasks:**

- `TASK-US-113-01` — Elaborar Threat Model.
  - `SUBTASK-TASK-US-113-01-01` — Mapear assets/flows/trust boundaries/misuse.
  - `SUBTASK-TASK-US-113-01-02` — Vincular controles/evidence/residual risks.
- `TASK-US-113-02` — Implementar identidade/autorização.
  - `SUBTASK-TASK-US-113-02-01` — Bootstrap admin, MFA/WebAuthn e recovery.
  - `SUBTASK-TASK-US-113-02-02` — RBAC/step-up/session revoke/timeout.
- `TASK-US-113-03` — Proteger comandos e web.
  - `SUBTASK-TASK-US-113-03-01` — Anti-replay/CSRF/CSP/input/rate-limit.
  - `SUBTASK-TASK-US-113-03-02` — Network bind/allowlist e critical audit.
- `TASK-US-113-04` — Verificar controles.
  - `SUBTASK-TASK-US-113-04-01` — ASVS/security integration tests.
  - `SUBTASK-TASK-US-113-04-02` — Exercitar misuse cases e session revocation.

**Testes/evidência:** ThreatModel, ASVS matrix, authz/anti-replay tests e redacted audit; `EVIDENCE-TASK-US-113-*-<date>`.

### US-114 — Definir SLOs e alertas testáveis

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** detectar perda de confiança financeira e fornecer ação operacional antes de ampliar impacto.
**Contexto:** processo disponível não compensa journal, market data ou reconciliation incorretos.
**Escopo:** SLI/SLO, alert rules, routing, ownership, synthetic probes e delivery tests.
**Não escopo:** alerta sem owner/runbook ou dependência obrigatória de SaaS.
**Contratos/falhas/security:** contexto sem secret; dedup/escalation; alert channel failure é observável.
**Dependências:** US-111, US-033, runbooks; AC-OPS-01.

**AC:**

- **Given** mismatch/disconnect crítico, **When** regra dispara, **Then** alerta contém ambiente, escopo, tempo, correlation e ação/runbook.
- **Given** teste sintético, **When** executado, **Then** entrega e acknowledgement são comprovados dentro do alvo.

**Tasks:**

- `TASK-US-114-01` — Especificar SLIs/SLOs.
  - `SUBTASK-TASK-US-114-01-01` — Fórmula/window/source/exclusions/error budget.
  - `SUBTASK-TASK-US-114-01-02` — Priorizar trust/freshness/reconciliation/durability.
- `TASK-US-114-02` — Implementar alerting.
  - `SUBTASK-TASK-US-114-02-01` — Regras/dedup/routing/owner/escalation.
  - `SUBTASK-TASK-US-114-02-02` — Vincular runbook e ação sugerida.
- `TASK-US-114-03` — Implementar probes e drills.
  - `SUBTASK-TASK-US-114-03-01` — Probes de freshness/journal/readiness/alert.
  - `SUBTASK-TASK-US-114-03-02` — Exercitar delivery failure/escalation.

**Testes/evidência:** alert delivery timestamps, rule tests e dashboard/SLO report; `EVIDENCE-TASK-US-114-*-<date>`.

## E-12 — Runtime local & portabilidade futura

**Scope/Status:** active / Proposed
**Outcome:** produto reproduzível nesta máquina sem cloud; imagens preservam portabilidade futura.
**Não escopo:** provisionar AWS, Oracle Cloud ou VPS.

### US-120 — Produzir containers multi-arch

**Scope/Status/Prioridade:** active / Proposed / P1
**Objetivo/valor:** garantir runtime Linux imutável no host atual sem acoplamento à arquitetura física.
**Contexto:** amd64/arm64 são requisito de portabilidade, não autorização de hosting.
**Escopo:** Dockerfiles, buildx, non-root, health, digest e smoke.
**Não escopo:** Kubernetes ou deploy externo.
**Contratos/falhas/security:** minimal/runtime-only; no secret em layer; digest fixo; target alternativo validado proporcionalmente ao uso.
**Dependências:** US-001, US-112; AC-PORT-01.

**AC:**

- **Given** commit de release, **When** imagens são construídas, **Then** amd64/arm64, SBOM e digests são produzidos.
- **Given** target nativo, **When** smoke executa, **Then** processo roda non-root e health fica saudável sem secret embutido.

**Tasks:**

- `TASK-US-120-01` — Criar Dockerfiles seguros.
  - `SUBTASK-TASK-US-120-01-01` — Multi-stage/minimal/non-root/health.
  - `SUBTASK-TASK-US-120-01-02` — Scan layers e excluir secrets/build residue.
- `TASK-US-120-02` — Construir multi-arch.
  - `SUBTASK-TASK-US-120-02-01` — Configurar buildx amd64/arm64.
  - `SUBTASK-TASK-US-120-02-02` — Gerar digest/SBOM/provenance.
- `TASK-US-120-03` — Validar runtime.
  - `SUBTASK-TASK-US-120-03-01` — Smoke nativo local.
  - `SUBTASK-TASK-US-120-03-02` — Smoke alternativo em runner/emulação.

**Testes/evidência:** `docker buildx build`, image scan, runtime user e smoke outputs; `EVIDENCE-TASK-US-120-*-<date>`.

### US-124 — Executar ambiente completo nesta máquina

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** operar DEV/PAPER/TESTNET de modo reproduzível sem compute ou credencial cloud.
**Contexto:** Worker, API, Web, PostgreSQL, dados e observabilidade compartilham host único.
**Escopo:** inventory, Compose profiles, volumes, health, migrations, budgets, backup/restore e smoke.
**Não escopo:** CANARY/LIVE automático, IaC cloud ou instalar dependências globais silenciosamente.
**Contratos/falhas/security:** DB não público; loopback/private bind; research pausável; disk/resource pressure observável; restore reconciliado.
**Dependências:** US-001, US-003; RF-148; RNF-100, RNF-103; AC-LOCAL-01, AC-LOCAL-02.

**AC:**

- **Given** host compatível, **When** bootstrap DEV/PAPER executa, **Then** serviços, volumes, migrations e smoke ficam saudáveis sem cloud.
- **Given** budgets atingidos, **When** research/telemetry pressiona o host, **Then** workload não crítico é limitado/pausado sem perder ledger/OMS/reconciliation.

**Tasks:**

- `TASK-US-124-01` — Inventariar e validar host.
  - `SUBTASK-TASK-US-124-01-01` — Medir OS/arch/CPU/RAM/disk/filesystem/IOPS.
  - `SUBTASK-TASK-US-124-01-02` — Verificar Docker/Compose/SDK/ports/network.
- `TASK-US-124-02` — Implementar Compose/profiles.
  - `SUBTASK-TASK-US-124-02-01` — Worker/API/Web/Postgres/OTel e networks.
  - `SUBTASK-TASK-US-124-02-02` — Profiles dev/paper/testnet/research/observability.
  - `SUBTASK-TASK-US-124-02-03` — Volumes/migrations/health/readiness.
- `TASK-US-124-03` — Definir resource/retention budgets.
  - `SUBTASK-TASK-US-124-03-01` — CPU/RAM/I/O/disk/telemetry limits e alerts.
  - `SUBTASK-TASK-US-124-03-02` — Pausa/priority do research e load test.
- `TASK-US-124-04` — Automatizar bootstrap/backup/restore.
  - `SUBTASK-TASK-US-124-04-01` — Bootstrap idempotente e smoke sem cloud.
  - `SUBTASK-TASK-US-124-04-02` — Backup + cópia off-host + checksums.
  - `SUBTASK-TASK-US-124-04-03` — Restore isolado, RPO/RTO e reconcile readiness.

**Testes/evidência:** `docker compose config --quiet`, `docker compose --profile dev up -d --wait`, `docker compose ps`, smoke/load/restore reports; `EVIDENCE-TASK-US-124-*-<date>`.

### US-121 — Avaliar laboratório AWS

**Scope/Status/Prioridade:** deferred / Deferred/Option / sem prioridade ativa
**Objetivo/valor:** opção futura somente se o ambiente local deixar de cumprir requisito comprovado.
**Contexto:** não há laboratório AWS no roadmap atual.
**Escopo futuro:** decisão, custo, threat model, IaC e teardown.
**Não escopo atual:** criar conta, credencial ou recurso.
**Contrato:** permanece bloqueada por decisão explícita e ADR.
**Dependências:** decisão futura; US-123; AC-AWS-01.

**AC:**

- **Given** ausência de decisão explícita, **When** item é revisado, **Then** permanece deferred e nenhum recurso/credencial existe.

**Tasks futuras, não autorizadas:**

- `TASK-US-121-01` — Comparar hosting com evidência.
  - `SUBTASK-TASK-US-121-01-01` — Registrar requisito local não atendido.
  - `SUBTASK-TASK-US-121-01-02` — Medir alternativas/custo/latência.
- `TASK-US-121-02` — Projetar laboratório aprovado.
  - `SUBTASK-TASK-US-121-02-01` — Threat model/IAM/network/secrets.
  - `SUBTASK-TASK-US-121-02-02` — IaC/TTL/teardown/rollback.

**Testes/evidência:** somente após ativação; decisão/ADR é pré-condição.

### US-122 — Avaliar deploy Oracle/VPS portátil

**Scope/Status/Prioridade:** deferred / Deferred/Option / sem prioridade ativa
**Objetivo/valor:** preservar opção de hosting sem alterar domínio.
**Contexto:** portabilidade é qualidade; deployment externo não é escopo.
**Escopo futuro:** benchmark, security, migration, backup e rollback.
**Não escopo atual:** contratar/provisionar servidor.
**Contrato:** sem ativação sem decisão/ADR/custo/threat model.
**Dependências:** US-120, US-124 e decisão futura.

**AC:**

- **Given** roadmap atual, **When** planejamento é publicado, **Then** item permanece deferred e sem credencial externa.

**Tasks futuras, não autorizadas:**

- `TASK-US-122-01` — Avaliar capacidade/segurança.
  - `SUBTASK-TASK-US-122-01-01` — Benchmark arquitetura/rede/disco.
  - `SUBTASK-TASK-US-122-01-02` — Threat/cost/support assessment.
- `TASK-US-122-02` — Planejar migração reversível.
  - `SUBTASK-TASK-US-122-02-01` — Data/secret/network/backup plan.
  - `SUBTASK-TASK-US-122-02-02` — Restore/reconcile/rollback drill.

**Testes/evidência:** somente após ativação explícita.

### US-123 — Limitar custo e teardown de eventual AWS

**Scope/Status/Prioridade:** deferred / Deferred/Option / sem prioridade ativa
**Objetivo/valor:** impedir gasto/provisionamento acidental se US-121 for ativada.
**Contexto:** preços, créditos e elegibilidade são voláteis.
**Escopo futuro:** account/region/pricing check, budget, TTL, owner, prohibited services e teardown.
**Não escopo atual:** pesquisa que autorize gasto ou criação.
**Contratos/falhas/security:** validação oficial fresca; approval antes de qualquer paid action.
**Dependências:** decisão futura; AC-AWS-01.

**AC:**

- **Given** iniciativa futura aprovada, **When** recurso pago puder ser criado, **Then** budget/TTL/owner/estimate/teardown estão aprovados previamente.

**Tasks futuras, não autorizadas:**

- `TASK-US-123-01` — Verificar custo/elegibilidade.
  - `SUBTASK-TASK-US-123-01-01` — Confirmar plano/região/preço/créditos.
  - `SUBTASK-TASK-US-123-01-02` — Definir orçamento e serviços proibidos.
- `TASK-US-123-02` — Garantir expiração/teardown.
  - `SUBTASK-TASK-US-123-02-01` — TTL/owner/inventory/alerts.
  - `SUBTASK-TASK-US-123-02-02` — Executar teardown e provar ausência de custo.

**Testes/evidência:** somente após ativação e aprovação específica.

## E-18 — Self-Healing & High Availability

**Scope/Status:** mixed; active stories = Proposed; US-183 = Deferred/Option
**Outcome:** falhas resultam em bloqueio seguro, recovery comprovado e reconciliação antes de operar.
**Não escopo:** HA distribuída antes da necessidade comprovada.

### US-180 — Entrar automaticamente em SafeMode

**Scope/Status/Prioridade:** active / Proposed / P0
**Objetivo/valor:** impedir novas exposições quando market data, account state, journal, ledger ou clock não forem confiáveis.
**Contexto:** `NO_TRADE`/SafeMode é comportamento correto, não indisponibilidade defeituosa.
**Escopo:** trust gates, state machine, entry block, in-flight policy, recovery e audit.
**Não escopo:** liquidar automaticamente sem RiskPolicy.
**Contratos/falhas/security:** Strategy não pode resetar; reset autorizado/auditado; ordem em voo segue policy.
**Dependências:** US-012, US-013, US-014, US-033, US-111, US-114; RF-170.

**AC:**

- **Given** trust gate crítico falho, **When** detectado, **Then** SafeMode bloqueia novas entradas e preserva tratamento de ordens em voo.
- **Given** causa resolvida, **When** re-enable é solicitado, **Then** reconciliação e autorização são exigidas antes de Ready.

**Tasks:**

- `TASK-US-180-01` — Modelar trust gates/state machine.
  - `SUBTASK-TASK-US-180-01-01` — Definir triggers/states/transitions/priorities.
  - `SUBTASK-TASK-US-180-01-02` — Definir no-new-entry/in-flight behavior.
- `TASK-US-180-02` — Implementar entrada/propagação.
  - `SUBTASK-TASK-US-180-02-01` — Agregar health financeiro/market/clock/storage.
  - `SUBTASK-TASK-US-180-02-02` — Propagar block para Risk/Execution/OMS.
- `TASK-US-180-03` — Implementar recovery/re-enable.
  - `SUBTASK-TASK-US-180-03-01` — Reconcile + health stabilization window.
  - `SUBTASK-TASK-US-180-03-02` — Authorization/audit/alert do reset.

**Testes/evidência:** fault matrix de trust gates, in-flight e re-enable; `EVIDENCE-TASK-US-180-*-<date>`.

### US-181 — Recuperar falhas supervisionadamente

**Scope/Status/Prioridade:** active / Proposed / P1
**Objetivo/valor:** recuperar conexões/processos sem retry storm, dupla execução ou intervenção rotineira.
**Contexto:** host é único e dependências podem falhar transitoriamente.
**Escopo:** supervision, restart, backoff/jitter, bounded retries, readiness e soak/fault tests.
**Não escopo:** mascarar falha persistente ou ignorar reconciliation.
**Contratos/falhas/security:** retry budget; readiness financeira distinta de liveness; repeated failure escala SafeMode/alert.
**Dependências:** US-180, US-114; RF-171.

**AC:**

- **Given** falha transitória, **When** supervisor recupera, **Then** serviço reconcilia antes de Ready e não duplica side effect.
- **Given** falha persistente, **When** retry budget acaba, **Then** permanece seguro e alerta com runbook.

**Tasks:**

- `TASK-US-181-01` — Implementar supervision/retry policy.
  - `SUBTASK-TASK-US-181-01-01` — Classificar transient/permanent/ambiguous.
  - `SUBTASK-TASK-US-181-01-02` — Backoff/jitter/budget/circuit breaker.
- `TASK-US-181-02` — Implementar lifecycle/readiness.
  - `SUBTASK-TASK-US-181-02-01` — Restart/drain/shutdown ordenado.
  - `SUBTASK-TASK-US-181-02-02` — Reconcile gate antes de readiness.
- `TASK-US-181-03` — Verificar recovery autônomo.
  - `SUBTASK-TASK-US-181-03-01` — Fault injection DB/WS/outbox/audit.
  - `SUBTASK-TASK-US-181-03-02` — Soak e intervenção-rate report.

**Testes/evidência:** chaos/fault tests e soak report; `EVIDENCE-TASK-US-181-*-<date>`.

### US-182 — Provar disaster recovery

**Scope/Status/Prioridade:** active / Proposed / P0 antes de canary
**Objetivo/valor:** recuperar dados e operação após corrupção/falha do host com RPO/RTO medidos.
**Contexto:** backup no mesmo disco não protege o host; após restore a exchange é reconciliada.
**Escopo:** backup/PITR equivalente, checksum, off-host, restore isolado, measurement e runbook automation.
**Não escopo:** assumir backup válido sem restore drill.
**Contratos/falhas/security:** backup criptografado/restrito; restore nunca reabilita entrada antes de reconcile; ledger não sofre rollback destrutivo.
**Dependências:** US-013, US-014, US-033, US-124, US-180; RF-142; RNF-091, RNF-092, RNF-093.

**AC:**

- **Given** backup selecionado, **When** restore em volume limpo executa, **Then** integridade/checksum/migrations/reconciliation são comprovados e RPO/RTO registrados.
- **Given** backup corrompido, **When** validação executa, **Then** restore é abortado com alerta sem afetar ambiente fonte.

**Tasks:**

- `TASK-US-182-01` — Projetar backup/recovery.
  - `SUBTASK-TASK-US-182-01-01` — Definir mecanismo/frequência/retention/encryption.
  - `SUBTASK-TASK-US-182-01-02` — Definir off-host/checksum/inventory.
- `TASK-US-182-02` — Automatizar restore isolado.
  - `SUBTASK-TASK-US-182-02-01` — Restaurar DB/Parquet/config em target limpo.
  - `SUBTASK-TASK-US-182-02-02` — Validar integrity/migrations/read-only startup.
- `TASK-US-182-03` — Medir e reconciliar.
  - `SUBTASK-TASK-US-182-03-01` — Medir RPO/RTO por failure class.
  - `SUBTASK-TASK-US-182-03-02` — Executar reconciliation antes de Ready.
- `TASK-US-182-04` — Exercitar runbook/falha.
  - `SUBTASK-TASK-US-182-04-01` — Testar backup corrompido/ausente.
  - `SUBTASK-TASK-US-182-04-02` — Atualizar RB-08/RB-10 e ReleaseEvidence.

**Testes/evidência:** restore drill report, checksums, RPO/RTO e ReconciliationReport; `EVIDENCE-TASK-US-182-*-<date>`.

### US-183 — Avaliar primary/standby sem double-trading

**Scope/Status/Prioridade:** deferred / Deferred/Option / pós-R1.0
**Objetivo/valor:** opção futura de reduzir downtime sem risco de dois líderes enviarem ordens.
**Contexto:** complexidade não é justificada no single-node inicial.
**Escopo futuro:** leader election, fencing, state sync, failover e chaos.
**Não escopo atual:** segunda instância ativa ou infraestrutura distribuída.
**Contratos/falhas/security:** nenhuma instância envia sem fencing token válido; failover reconcilia primeiro.
**Dependências:** necessidade medida, ADR futuro, US-182.

**AC:**

- **Given** ausência de requisito medido/ADR, **When** roadmap é executado, **Then** item permanece deferred.
- **Given** implementação futura, **When** partição produz dois candidatos, **Then** somente líder fenced pode submeter ordem.

**Tasks futuras, não autorizadas:**

- `TASK-US-183-01` — Decidir consistency/fencing.
  - `SUBTASK-TASK-US-183-01-01` — Comparar opções e failure assumptions.
  - `SUBTASK-TASK-US-183-01-02` — ADR de leader/fencing/state ownership.
- `TASK-US-183-02` — Prototipar failover seguro.
  - `SUBTASK-TASK-US-183-02-01` — State sync e reconcile-before-ready.
  - `SUBTASK-TASK-US-183-02-02` — Split-brain/partition/chaos tests.

**Testes/evidência:** somente após ativação explícita.

## Dependências críticas de ordem

```text
US-001 -> US-002, US-003, US-010, US-124
US-002 -> US-015
US-010 -> US-011 -> US-012 -> US-013 -> US-014
US-003, US-011, US-014 -> E-02 -> US-030 -> US-031 -> US-032 -> US-033
US-110, US-111, US-112, US-113, US-114 + US-033 -> US-180 -> US-181 -> US-182
US-121, US-122, US-123 e US-183 permanecem deferred
```

## Decisões bloqueadoras antes de `Ready`

1. Identity store local, bootstrap/recovery do administrador e implementação MFA/WebAuthn.
2. Escopo single-account versus multi-account por runtime.
3. Isolation/durability PostgreSQL, outbox lease, ordering e poison policy.
4. Ferramenta/frequência/retenção/criptografia de backup e destino off-host.
5. Retenções quantitativas de ledger, Parquet e telemetry.
6. Criptografia local at rest para banco, secrets, backups e evidence.
7. Inventário real do host e budgets CPU/RAM/disk/I/O/rede.
8. Canal de alerta disponível fora do processo/host, ownership e escalation.
9. Ferramenta e trust model para assinatura/provenance de artefatos.
10. Política de migration expand/contract e rollback compatível.
11. Verificação oficial e datada dos contratos Binance em cada task volátil.
12. Compatibilização de disponibilidade 24x7/RTO com máquina local única antes de canary.
