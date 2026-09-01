# 04 - Arquitetura

## Estilo

**Modular Monolith + Event-Driven internamente + Ports & Adapters.**

Microservices não serão usados inicialmente. Processos podem ser separados operacionalmente quando houver benefício claro.

## Fluxo principal

```text
Binance
  ↓
Market Data → Feature/State → Regime → Strategies
                                      ↓
                                  Alpha/Intent
                                      ↓
                              Portfolio Construction
                                      ↓
                                  Risk Engine
                                      ↓
                               Execution Engine
                                      ↓
                                     OMS
                                      ↓
                                   Binance
```

## Regra arquitetural principal

`Strategy -> Intent -> Portfolio -> Risk -> Execution -> OMS -> Exchange`

Nenhum módulo de Strategy recebe credencial da exchange ou acesso a envio de ordem.

## Bounded modules

- `Domain`: value objects, enums, invariantes e eventos neutros.
- `MarketData`: streams, books, candles, quality/gaps.
- `Exchange.Abstractions`: contratos de exchange.
- `Exchange.Binance`: adapter Binance.
- `Strategies`: estratégias e SDK.
- `Regimes`: classificação de mercado.
- `Portfolio`: combinação/netting e targets.
- `Risk`: limites e protections.
- `Execution`: algoritmos de execução.
- `Orders`: OMS/state machine/reconciliation.
- `Research`: datasets, experiments, validation.
- `Backtesting`: event simulation.
- `Analytics`: métricas e attribution.
- `Persistence`: PostgreSQL/Parquet.
- `Operations`: health, alerts, kill switch.
- `Api`: control/read API.
- `Worker`: live trading process.
- `Web`: React frontend.

## Processos previstos

### Trading Worker
Hot path, conexões Binance, strategies, portfolio, risk, execution e OMS.

### API
Control plane e queries para UI. Comandos live passam por autorização e audit.

### Web
SPA React servida estaticamente.

### Research Runner
Backtests/optimization/replay isolados para não competir com live por CPU/RAM.

## Mensageria interna

Começar com `System.Threading.Channels`, tipos imutáveis/readonly e backpressure explícito. Kafka/RabbitMQ só entram se processos realmente precisarem de broker distribuído.

## Persistência

Não adotar Event Sourcing integral no MVP. Manter:
- tabelas transacionais para estado materializado;
- journal imutável de eventos críticos de trading/auditoria;
- snapshots/reconciliação.

Isso fornece replay e auditabilidade sem transformar cada CRUD em evento.

## Consistência

A exchange é autoridade externa para ordens/fills/saldos. Estado local é otimizado para decisão, mas divergências levam à reconciliação e, quando necessário, ao bloqueio de novas entradas.

## Deployment portability

Toda lógica de negócio roda em containers Linux multi-arch. AWS/Oracle/Hostinger são destinos de infraestrutura, não dependências do domínio.
