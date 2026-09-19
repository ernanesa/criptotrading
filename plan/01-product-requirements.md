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
- ledger financeiro append-only por ativo e PnL reconstruível;
- persist-before-submit e entrega at-least-once/idempotente para eventos críticos;
- circuit breakers e kill switch;
- modos paper, shadow, canary e live.

### Autonomy Plane

O R1.0 implementa somente o subconjunto A2 para uma baseline. Seleção multi-strategy, Regime Detection adaptativo, alocação dinâmica e A5 são requisitos evolutivos pós-R1.0, não condição para o primeiro live estável.
- operação 24x7 sem intervenção humana em decisões rotineiras;
- classificação automática de contexto/regime com confiança;
- seleção e deselegibilidade automática de estratégias previamente validadas;
- ranking de estratégias por utilidade líquida esperada ajustada a risco, custos e incerteza;
- alocação dinâmica de risk budget entre estratégias;
- `NO_TRADE` automático quando nenhuma alternativa supera os thresholds;
- Strategy Health com redução, quarantine e desativação automáticas;
- feedback contínuo de resultados para o seletor/alocador;
- detecção de data drift, concept drift, execution drift e strategy decay;
- champion-challenger para modelos adaptativos;
- retreinamento/retuning e promoção automáticos somente dentro de envelopes e gates predefinidos;
- rollback automático de modelos/estratégias degradados;
- auto-recuperação operacional, reconciliação e safe mode;
- nenhum mecanismo adaptativo pode elevar hard risk limits ou remover circuit breakers.

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
- filesystem/volumes locais como armazenamento inicial; adapter compatível com object storage somente em iniciativa futura aprovada;
- Redis apenas quando houver caso medido para cache/coordenação.
- raw/normalized/research layers com temporalidade, lineage, quarantine e supersession;
- snapshots históricos de exchangeInfo, filtros, listings/delistings e universo.

## Personas funcionais

### Quant Researcher
Cria hipóteses, executa testes, compara estratégias e define envelopes/gates dentro dos quais o sistema poderá adaptar-se automaticamente.

### Trader/Operator
Acompanha posições, limites e alertas e mantém capacidade de intervenção/kill switch. Não deve ser necessário para decisões rotineiras de mercado.

### Developer/SRE
Mantém software, performance, segurança, deployment, resiliência e incidentes.

No início, as três personas representam a mesma pessoa, mas as responsabilidades permanecem separadas no desenho.

## Fluxo de promoção de estratégia/modelo

Idea → Spec → Unit Tests → Historical Backtest → Out-of-Sample → Walk-Forward → Monte Carlo/Stress → Paper → Shadow/Challenger → Canary → Champion/Live.

Cada transição gera evidência persistida e pode ser bloqueada por critérios objetivos. `RiskPolicy`, `PromotionReadinessMatrix` e, para capital real, `CanaryPlan` definem threshold, unidade, janela, aprovação, validade, abort e rollback. Promoção automática é permitida apenas para candidatos previamente autorizados e dentro de envelopes imutáveis pelo learner.

## Trading modes

- `Backtest`: tempo histórico simulado.
- `Paper`: mercado atual, execução simulada.
- `Shadow`: sinais e decisões live, nenhuma ordem enviada.
- `Canary`: capital real mínimo e limites reduzidos.
- `Live`: limites aprovados de produção.
- `SafeMode`: nenhuma nova exposição; reconciliação/recuperação conforme política.

## Modelo de intenção

Estratégias não produzem ordens diretamente. Produzem `TradeIntent`/`AlphaSignal` com direção, confiança, horizonte, invalidação e opcionalmente retorno esperado. O Autonomy Orchestrator seleciona/pondera candidatos elegíveis; Portfolio combina intenções e produz targets; Risk ajusta ou rejeita targets; Execution converte targets autorizados em ordens.

## Gates de promoção

Uma estratégia/modelo não avança se:
- depende de lookahead;
- possui amostra insuficiente;
- resultado é consumido por custos conservadores;
- performance depende de uma pequena região de parâmetros;
- falha em regimes críticos;
- excede drawdown/tail-risk aceitos;
- diverge excessivamente entre backtest e paper/shadow;
- não possui explicação, lineage e versionamento completos;
- degrada risco, execução ou robustez além de tolerâncias;
- requer aumento automático de hard risk limits para parecer vantajoso.

## Princípio de autonomia segura

`100% automatizado` significa que o sistema executa sozinho a operação normal, inclusive seleção de estratégia, alocação, risk, execução, monitoramento e recuperação. Não significa autonomia irrestrita: hard risk limits, segurança, kill switch e boundaries de promoção ficam fora do controle de modelos adaptativos.
