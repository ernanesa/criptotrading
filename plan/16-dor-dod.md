# 16 - Definition of Ready (DoR) e Definition of Done (DoD)

## Definition of Ready

Um PBI pode entrar no Sprint quando:

- [ ] objetivo/valor está claro;
- [ ] dependências conhecidas;
- [ ] critérios de aceite testáveis;
- [ ] risco principal identificado;
- [ ] dados/fixtures necessários disponíveis ou tarefa explícita para obtê-los;
- [ ] impacto em segurança/risk engine avaliado;
- [ ] impacto em observabilidade definido;
- [ ] tamanho cabe no Sprint ou foi fatiado;
- [ ] dúvidas capazes de bloquear implementação foram resolvidas;
- [ ] não depende de API presumida sem checar documentação oficial.
- [ ] para trabalho .NET, SDK 11/C# 15 e compatibilidade das dependências foram confirmados contra as versões pinadas; a solução não propõe downgrade de runtime;
- [ ] parent, prioridade, fonte/frescor e IDs de rastreabilidade definidos;
- [ ] comando de validação e evidência esperada definidos;
- [ ] estados de conflito, timeout e falha cobertos.
- [ ] thresholds, duração e volume apontam para policy/matrix versionada com valor e unidade;
- [ ] manifesto de rastreabilidade atualizado sem IDs órfãos ou intervalos implícitos.

Spikes podem entrar com hipótese, timebox e saída esperada mesmo sem solução conhecida.

## Definition of Done geral

- [ ] acceptance criteria atendidos;
- [ ] código builda sem erros;
- [ ] testes adequados passam;
- [ ] analyzers/typecheck/lint passam;
- [ ] nenhuma secret adicionada;
- [ ] dependências sem vulnerabilidade crítica/alta não aceita;
- [ ] logs/metrics/traces adicionados quando operacionalmente relevantes;
- [ ] documentação/ADR atualizada se necessário;
- [ ] migrations possuem rollback/estratégia segura;
- [ ] branch/PR revisado, mesmo em projeto solo quando mudança é relevante;
- [ ] componente de runtime passa no target nativo local e sua imagem multi-arch é construída/testada proporcionalmente ao risco;
- [ ] container/CI atualizado quando necessário;
- [ ] comandos de build/test/deploy relevantes funcionam localmente sem credencial cloud;
- [ ] incremento demonstrável.
- [ ] Goal/Epic/Story/AC/Task/Test/Evidence vinculados;
- [ ] fonte superseded não é usada como autoridade ativa;
- [ ] risco residual, rollback e limitações registrados.
- [ ] manifest/validator não reporta link órfão, duplicado ou evidência obrigatória ausente.

## DoD adicional para hot path

- [ ] benchmark antes/depois;
- [ ] alocações e latência verificadas;
- [ ] não introduz blocking I/O;
- [ ] backpressure/failure mode documentado.

## DoD adicional para Risk/OMS

- [ ] invariantes testadas;
- [ ] testes de falha/timeout/retry;
- [ ] audit event presente;
- [ ] replay/reconciliation scenario testado;
- [ ] nunca eleva exposição além do autorizado.

## DoD adicional para estratégia

- [ ] Strategy Spec completa;
- [ ] custos no backtest;
- [ ] OOS/walk-forward conforme gate;
- [ ] sensitivity report;
- [ ] stress/Monte Carlo aplicável;
- [ ] resultado registrado no Experiment Registry;
- [ ] nenhuma promoção live implícita.
- [ ] preregistration, experiment family/trial budget e holdout access registrados;
- [ ] parity suite e ablations/benchmarks conforme Strategy Spec.

## DoD adicional para release live

- [ ] paper/shadow executados;
- [ ] canary plan aprovado;
- [ ] backup/restore testado;
- [ ] kill switch testado;
- [ ] runbook e rollback;
- [ ] alerts testados;
- [ ] API key sem withdrawal;
- [ ] clock sync e reconciliation validados.
- [ ] RiskPolicy, PromotionReadinessMatrix, CanaryPlan, ThreatModel, ReconciliationReport e ReleaseEvidence aprovados;
- [ ] ledger/fee/valuation e persist-before-submit comprovados;
- [ ] kill switch com ordem em voo e sem UI/API testado.
