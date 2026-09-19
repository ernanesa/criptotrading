# Casos de uso, atores e contratos do backlog

Este arquivo transforma os requisitos canônicos em um mapa de casos de uso
para leitura rápida dos cartões. O detalhamento executável permanece nos
manifestos de domínio e nos critérios de aceite.

## Atores

| Ator | Responsabilidade | Limites |
|---|---|---|
| Operador | observa saúde, reconcilia, ativa kill switch e aprova gates | não altera código/threshold sem segregação |
| Pesquisador | cria Dataset/Experiment/Strategy Cards e analisa evidência | não envia ordens |
| Serviço de estratégia | transforma contexto em sinal/intent determinístico | não acessa exchange nem credencial |
| Risk Engine | avalia policy, exposição, custo e circuit breakers | pode bloquear, nunca ampliar envelope |
| OMS/Execution | persiste, envia, reconcilia e atribui ordens/fills | não contorna RiskDecision |
| Exchange adapter | traduz fatos da Binance por ambiente | não decide estratégia |
| Dashboard | expõe estado, evidência e comandos autorizados | não é fonte financeira canônica |
| Agente de entrega | implementa somente escopo aprovado e devolve evidência | não promove sem gate |

## Casos de uso principais

### UC-01 — Inicializar ambiente local reproduzível

- **Pré-condições:** checkout limpo, SDK/runtime pinados, Docker local disponível.
- **Fluxo principal:** validar toolchain → restaurar → migrar → subir perfis →
  verificar liveness/readiness → registrar commit e evidência.
- **Alternativas:** SDK incompatível, lock divergente, porta ocupada ou secret
  ausente falham cedo sem iniciar capacidade financeira.
- **Pós-condição:** ambiente `DEV`/`PAPER` explícito e sem acesso live.
- **Cartões:** `US-001`, `US-003`, `US-124`.

### UC-02 — Ingerir e reconstruir market data

- **Pré-condições:** endpoint e símbolo autorizados, relógio válido.
- **Fluxo principal:** conectar → autenticar/subscrever → validar sequência →
  persistir raw/normalizado → publicar somente estado fresco/Healthy.
- **Alternativas:** gap, payload inválido, staleness ou reconnect disparam
  quarantine/resync e bloqueiam consumidores dependentes.
- **Cartões:** `US-020`–`US-024`, `US-030`.

### UC-03 — Avaliar sinal e RiskDecision

- **Pré-condições:** contexto com timestamps/lineage, Strategy elegível,
  RiskPolicy aprovada e dados frescos.
- **Fluxo principal:** gerar signal → construir intent → calcular sizing →
  avaliar exposição/custo/breakers → emitir decisão auditável ou `NO_TRADE`.
- **Alternativas:** policy expirada, incerteza, drawdown, preço stale ou limite
  excedido produzem bloqueio com reason code.
- **Cartões:** `US-040`–`US-044`, `US-060`, `US-061`, `US-073`.

### UC-04 — Enviar ordem com durabilidade e idempotência

- **Pré-condições:** RiskDecision aprovada, outbox persistida e capability do
  ambiente coerente.
- **Fluxo principal:** criar clientOrderId → persistir intent → enviar → mapear
  ack/fill → atualizar OMS/ledger → emitir audit event.
- **Alternativas:** timeout/5xx/ambiguous exige lookup/reconcile antes de retry;
  nunca executar retry cego ou duplicar fill.
- **Cartões:** `US-031`, `US-032`, `US-033`, `US-080`–`US-082`.

### UC-05 — Reconciliar estado local e exchange

- **Pré-condições:** startup, reconnect, timeout, período ou comando manual.
- **Fluxo principal:** capturar snapshot → comparar balances/orders/fills/fees →
  classificar → aplicar compensating entry/projection rebuild → liberar somente
  após estabilização.
- **Alternativas:** mismatch inexplicado mantém `Blocked/SafeMode` e gera alerta.
- **Cartões:** `US-013`, `US-033`, `US-180`–`US-182`.

### UC-06 — Pesquisar, validar e promover estratégia

- **Pré-condições:** Dataset Card, Experiment Card, hipótese e holdout protegidos.
- **Fluxo principal:** preregistrar → backtest event-driven → custos/fills →
  OOS/walk-forward/stress → paper/shadow → Testnet → readiness → canary.
- **Alternativas:** leakage, edge líquido negativo, degradação ou evidência
  incompleta rejeitam/no-trade e preservam a baseline.
- **Cartões:** `US-050`–`US-055`, `US-061`, `US-090`–`US-094`, `US-130`–`US-134`.

### UC-07 — Operar canary e abortar com segurança

- **Pré-condições:** PromotionReadinessMatrix aprovada, ReleaseEvidence válida,
  kill switch testado, envelope reduzido e aprovação explícita.
- **Fluxo principal:** preflight → habilitar capability → monitorar limites,
  PnL, latência e intervenção → promover/encerrar conforme critérios.
- **Alternativas:** qualquer abort criterion bloqueia novas entradas, trata
  in-flight/open orders, reconcilia e registra rollback.
- **Cartões:** `US-043`, `US-092`, `US-130`, `US-131`, `US-132`, `US-134`.

### UC-08 — Recuperar de falha operacional

- **Pré-condições:** incidente classificado, backup/checksum e runbook aplicável.
- **Fluxo principal:** SafeMode → drain/shutdown → restore/restart → reconcile →
  estabilização → autorização de reset → evidência RPO/RTO.
- **Alternativas:** backup corrompido, estado ambíguo ou falha repetida mantém
  no-entry e escala ao operador.
- **Cartões:** `US-114`, `US-124`, `US-180`, `US-181`, `US-182`.

## Regras transversais

1. `Strategy → Intent → Portfolio → Risk → Execution → OMS → Exchange` é o
   único fluxo autorizado.
2. `NO_TRADE`, `Blocked` e `SafeMode` são resultados válidos e auditáveis.
3. Mocks/fakes comprovam somente unit/contract/paper; Testnet usa boundary real.
4. Dados, decisões, fills e ledger são reconstruíveis; correção é compensating,
   nunca apagamento destrutivo.
5. Credenciais são referências externas ao Git, redacted em logs/evidências e
   segregadas por ambiente; saque permanece desabilitado.
