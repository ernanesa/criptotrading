# 01 - Product Requirements

## Capacidades obrigatórias

### Research Plane
- ingestão e versionamento de dados históricos;
- criação de estratégias por contrato estável;
- backtesting event-driven;
- custos, spread, slippage e latência simulados;
- out-of-sample, walk-forward e Monte Carlo;
- registro de experimentos e parâmetros;
- comparação com benchmarks;
- detecção de lookahead/data leakage;
- métricas de overfitting e estabilidade.

### Trading Plane
- market data realtime;
- estado de conta e posições;
- Order Management System central;
- Portfolio Construction;
- Risk Engine independente;
- Execution Engine;
- reconciliação com exchange;
- circuit breakers e kill switch;
- modos paper, shadow, canary e live.

### Control Plane
- configuração versionada;
- gestão de estratégias;
- alteração de limites com auditoria;
- dashboard realtime;
- alerts e incidentes;
- autenticação da interface administrativa.

### Data Plane
- PostgreSQL para dados transacionais e configuração;
- Parquet para grandes datasets históricos;
- DuckDB para consulta analítica local sobre Parquet;
- armazenamento de objetos compatível com S3 quando aplicável;
- Redis apenas quando houver caso medido para cache/coordenação.

## Personas funcionais

### Quant Researcher
Cria hipóteses, executa testes, compara estratégias e decide se há evidência suficiente para promover uma estratégia.

### Trader/Operator
Ativa modos, acompanha posições, limites, alertas e pode acionar kill switch.

### Developer/SRE
Mantém software, performance, segurança, deployment e incidentes.

No início, as três personas representam a mesma pessoa, mas as responsabilidades permanecem separadas no desenho.

## Fluxo de promoção de estratégia

Idea → Spec → Unit Tests → Historical Backtest → Out-of-Sample → Walk-Forward → Monte Carlo/Stress → Paper → Shadow → Canary → Live.

Cada transição gera evidência persistida e pode ser bloqueada por critérios objetivos.

## Trading modes

- `Backtest`: tempo histórico simulado.
- `Paper`: mercado atual, execução simulada.
- `Shadow`: sinais e decisões live, nenhuma ordem enviada.
- `Canary`: capital real mínimo e limites reduzidos.
- `Live`: limites aprovados de produção.

## Modelo de intenção

Estratégias não produzem ordens diretamente. Produzem `TradeIntent`/`AlphaSignal` com direção, confiança, horizonte, invalidação e opcionalmente retorno esperado. Portfolio combina intenções e produz targets. Risk ajusta ou rejeita targets. Execution converte targets autorizados em ordens.

## Gates de promoção

Uma estratégia não avança se:
- depende de lookahead;
- possui amostra insuficiente;
- resultado é consumido por custos conservadores;
- performance depende de uma pequena região de parâmetros;
- falha em regimes críticos;
- excede drawdown/tail-risk aceitos;
- diverge excessivamente entre backtest e paper/shadow;
- não possui explicação e versionamento completos.
