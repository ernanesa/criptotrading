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
- [ ] comportamento funciona em ARM64 quando componente de runtime;
- [ ] container/CI atualizado quando necessário;
- [ ] incremento demonstrável.

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

## DoD adicional para release live

- [ ] paper/shadow executados;
- [ ] canary plan aprovado;
- [ ] backup/restore testado;
- [ ] kill switch testado;
- [ ] runbook e rollback;
- [ ] alerts testados;
- [ ] API key sem withdrawal;
- [ ] clock sync e reconciliation validados.
