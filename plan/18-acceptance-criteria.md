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
A release do Worker/API possui imagens `linux/amd64` e `linux/arm64` e smoke tests no target ARM antes da migração Oracle.

## AC-OPS-01
Alert de reconciliação/disconnect crítico contém ambiente, exchange, symbol/account scope, timestamp, correlation ID e ação sugerida.

## Critério para release canary

- todos os ACs críticos anteriores;
- restore drill concluído;
- kill switch exercitado;
- estratégia possui report assinado/versionado;
- risk policy canary aprovada;
- chave sem saques;
- monitoramento e canal de alerta ativos;
- nenhuma vulnerabilidade crítica aberta.
