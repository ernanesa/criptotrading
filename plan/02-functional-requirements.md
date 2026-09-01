# 02 - Requisitos Funcionais (RF)

## Configuração e identidade
- **RF-001** Autenticar o usuário administrativo.
- **RF-002** Autorizar operações administrativas por papel, mesmo que inicialmente exista um único usuário.
- **RF-003** Manter configurações versionadas por ambiente.
- **RF-004** Auditar quem/o quê/quando para mudanças de configuração e risco.
- **RF-005** Permitir feature flags para capacidades experimentais.
- **RF-006** Separar segredos de configurações normais.

## Market data
- **RF-010** Consultar `exchangeInfo` e filtros da Binance.
- **RF-011** Receber trades realtime.
- **RF-012** Receber book ticker.
- **RF-013** Receber depth updates e manter livro local sincronizado.
- **RF-014** Receber/produzir candles.
- **RF-015** Detectar gaps, mensagens fora de ordem e staleness.
- **RF-016** Resincronizar order book automaticamente.
- **RF-017** Reconectar WebSockets com backoff/jitter.
- **RF-018** Suportar múltiplos símbolos configuráveis.
- **RF-019** Persistir datasets históricos selecionados.
- **RF-020** Suportar replay determinístico de market data gravado.

## Conta e OMS
- **RF-030** Receber User Data Stream via WebSocket API.
- **RF-031** Consultar saldos, posições e ordens para reconciliação.
- **RF-032** Criar MARKET, LIMIT e LIMIT_MAKER quando suportadas.
- **RF-033** Suportar IOC/FOK quando aplicável.
- **RF-034** Cancelar ordens.
- **RF-035** Acompanhar NEW, PARTIALLY_FILLED, FILLED, CANCELED, REJECTED e EXPIRED.
- **RF-036** Utilizar `clientOrderId` único/idempotente.
- **RF-037** Tratar timeout ambíguo consultando estado antes de repetir ação.
- **RF-038** Respeitar tick size, step size, min/max quantity, notional e demais filtros dinâmicos.
- **RF-039** Tratar status de símbolo, inclusive HALT/BREAK/CANCEL_ONLY quando disponibilizado.
- **RF-040** Centralizar netting de intenções para evitar estratégias comprando e vendendo entre si.
- **RF-041** Configurar Self-Trade Prevention quando aplicável.
- **RF-042** Reconciliar estado interno x Binance no startup e periodicamente.

## Strategy Engine
- **RF-050** Registrar estratégias por interface/plugin estável.
- **RF-051** Habilitar/desabilitar estratégia sem alterar código.
- **RF-052** Versionar parâmetros e código associados a cada experimento.
- **RF-053** Emitir AlphaSignal/TradeIntent, nunca ordens diretas.
- **RF-054** Suportar múltiplos timeframes.
- **RF-055** Suportar múltiplas estratégias simultaneamente.
- **RF-056** Manter Strategy Health: HEALTHY/DEGRADED/SUSPICIOUS/DISABLED.
- **RF-057** Registrar racional, features e contexto de cada sinal.

## Regime e alocação
- **RF-060** Classificar regime de tendência.
- **RF-061** Classificar volatilidade.
- **RF-062** Classificar liquidez/spread.
- **RF-063** Classificar correlação sistêmica.
- **RF-064** Permitir selecionar/desselecionar estratégias conforme regime.
- **RF-065** Combinar múltiplos sinais com regra explícita.
- **RF-066** Versionar modelo de regime e thresholds.

## Portfolio e Risk
- **RF-070** Calcular position sizing por risco, não quantidade fixa.
- **RF-071** Aplicar exposição máxima por ativo.
- **RF-072** Aplicar exposição máxima por estratégia.
- **RF-073** Aplicar exposição total e correlacionada.
- **RF-074** Aplicar limite de perda diária.
- **RF-075** Aplicar limite de drawdown.
- **RF-076** Aplicar limite de número/frequência de operações.
- **RF-077** Bloquear baixa liquidez/spread excessivo.
- **RF-078** Bloquear dados stale ou inconsistentes.
- **RF-079** Bloquear clock drift excessivo.
- **RF-080** Implementar circuit breakers hierárquicos.
- **RF-081** Implementar kill switch manual.
- **RF-082** Permitir reduzir risco automaticamente conforme Strategy Health.
- **RF-083** Calcular risk budget por estratégia.
- **RF-084** Calcular probabilidade/estimativa de ruin quando houver dados suficientes.

## Execution
- **RF-090** Converter targets autorizados em ordens.
- **RF-091** Estimar spread e slippage antes da execução.
- **RF-092** Registrar slippage realizado.
- **RF-093** Suportar execução immediate/market e passive limit.
- **RF-094** Evoluir para TWAP/iceberg/amend/cancel-replace conforme necessidade.
- **RF-095** Cancelar ordens stale ou incompatíveis com novo target.
- **RF-096** Nunca exceder target autorizado após partial fills/retries.

## Research e Backtesting
- **RF-100** Importar histórico com proveniência.
- **RF-101** Executar backtest reproduzível por seed/config/version.
- **RF-102** Modelar fees maker/taker.
- **RF-103** Modelar spread e slippage.
- **RF-104** Modelar latência e partial fills em níveis de fidelidade configuráveis.
- **RF-105** Executar train/validation/test temporal.
- **RF-106** Executar walk-forward.
- **RF-107** Executar Monte Carlo/stress.
- **RF-108** Executar análise de sensibilidade de parâmetros.
- **RF-109** Calcular PBO/Deflated Sharpe quando aplicável.
- **RF-110** Detectar lookahead e leakage por testes automáticos.
- **RF-111** Comparar com buy-and-hold e benchmarks relevantes.
- **RF-112** Persistir experimentos e resultados.

## Analytics
- **RF-120** Calcular PnL bruto e líquido.
- **RF-121** Calcular fees, spread e slippage attribution.
- **RF-122** Calcular CAGR/retorno, max drawdown, Sharpe, Sortino, Calmar, profit factor e expectancy.
- **RF-123** Calcular MAE/MFE, turnover, exposição e time-under-water.
- **RF-124** Produzir PnL attribution por estratégia, ativo, regime e execução.

## Interface e operação
- **RF-130** Dashboard de saúde do sistema.
- **RF-131** Dashboard de mercado, ordens, fills, posições e PnL.
- **RF-132** Dashboard de estratégias e regimes.
- **RF-133** Dashboard de risco e circuit breakers.
- **RF-134** Atualização realtime via SignalR.
- **RF-135** Exibir claramente ambiente e modo de trading.
- **RF-136** Exigir confirmação reforçada para ações destrutivas/live.
- **RF-137** Permitir kill switch pelo painel com auditoria.
- **RF-138** Exportar relatórios/CSV para análise e obrigações externas.

## Operações
- **RF-140** Health/readiness/liveness checks.
- **RF-141** Alertar erros críticos, drawdown, reconciliação e disconnects.
- **RF-142** Backup e restauração testados.
- **RF-143** Suportar deployment ARM64 e AMD64.
- **RF-144** Suportar configuração AWS, Oracle Cloud e VPS sem alteração de domínio.
