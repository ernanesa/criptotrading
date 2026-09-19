# 23 - Engenharia de Performance

## Regra

Otimizar após medir, exceto práticas óbvias que evitam alocação/I/O desnecessários no hot path.

## Hot paths

1. WebSocket parse.
2. Local order book update.
3. Feature calculation incremental.
4. Signal generation.
5. Portfolio/Risk decision.
6. Order serialization/send.

## .NET guidelines

- async end-to-end para I/O;
- Channels com bounded capacity onde backpressure é necessária;
- `Span<T>`, `ReadOnlySpan<T>`, `Memory<T>` somente onde benchmark justificar;
- ArrayPool/object pooling com cuidado e testes;
- source-generated JSON contexts;
- structs/readonly records para valores pequenos quando apropriado;
- evitar LINQ/alocações em loops de alta frequência quando medido;
- no blocking `.Result/.Wait()` em hot path;
- usar `Stopwatch`/TimeProvider apropriado para timings;
- avaliar Server GC vs Workstation GC por processo/carga;
- profile com dotnet-counters/dotnet-trace/perf tools.

## SBE

Após baseline JSON, benchmark de SBE. Adotar quando redução de CPU/latência justificar complexidade de schema lifecycle. Manter decoder isolado da lógica de trading.

## Backtesting

Paralelização por experimentos é preferível a paralelizar cada evento. Definir max degree of parallelism a partir de CPU/RAM. Não rodar pesquisa pesada no mesmo host do live engine.

## Frontend

- Vite 8/Rolldown;
- code splitting por rota;
- virtualização para tabelas grandes;
- throttling/batching de updates SignalR para não renderizar cada tick;
- React Profiler antes de memoização indiscriminada;
- market charts recebem amostragem/aggregation apropriada.

## Performance budgets

Toda release mede:
- market-event decision p50/p95/p99;
- allocations/event;
- WS parse throughput;
- book update throughput;
- order command latency interna;
- backtest events/sec;
- dashboard render/update rate.

Baseline é versionado. Regressão relevante exige justificativa ou correção.

O budget de decisão é decomposto por ingestão/parse, fila, feature, strategy, portfolio/risk, persistência durável, serialização e network/exchange. Medir queueing e clock skew separadamente; média não substitui p50/p95/p99 e cauda sob overload. Otimização não pode remover persistência, auditabilidade ou risk checks para cumprir latência.
