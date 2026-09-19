# 02 - Requisitos Funcionais (RF)

## Configuração e identidade
- **RF-001** Autenticar o usuário administrativo.
- **RF-002** Autorizar operações administrativas por papel, mesmo que inicialmente exista um único usuário.
- **RF-003** Manter configurações versionadas por ambiente.
- **RF-004** Auditar quem/o quê/quando para mudanças de configuração e risco.
- **RF-005** Permitir feature flags para capacidades experimentais.
- **RF-006** Separar segredos de configurações normais.
- **RF-007** Exigir MFA/WebAuthn e step-up para ações CANARY/LIVE, RiskPolicy, credenciais e reset de kill switch.
- **RF-008** Revogar sessões e impedir replay de comandos administrativos críticos.
- **RF-009** Restringir o control plane por rede/allowlist/VPN conforme ambiente.

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
- **RF-043** Persistir order intent, RiskDecision, policy/config version e idempotency key antes do submit.
- **RF-044** Manter ledger append-only por ativo para principal, proceeds, fees, rebates e ajustes.
- **RF-045** Registrar balances `free/locked/total`, fills e fees com os timestamps e IDs externos.
- **RF-046** Calcular position, cost basis e PnL como projeções internas reconciliáveis.
- **RF-047** Tratar depósitos, retiradas, conversões, dust e trades externos ao bot.
- **RF-048** Aplicar compensating entry para ajuste financeiro; nunca apagar ou reescrever histórico reconciliado.
- **RF-049** Classificar cada evento quanto a durabilidade, ordering, overflow e recovery.

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
- **RF-113** Pré-registrar hipótese, família de experimento, search space e trial budget.
- **RF-114** Manter holdout final bloqueado e auditar cada acesso.
- **RF-115** Invalidar/versionar holdout contaminado e registrar motivo.
- **RF-116** Reportar intervalos de confiança/incerteza e limite inferior conservador para métricas de promoção.
- **RF-117** Executar ablations e comparar `NO_TRADE/cash`, buy-and-hold e benchmark econômico relevante.
- **RF-118** Definir fórmula, frequência, annualization, risk-free rate e unidade de cada métrica.
- **RF-119** Reutilizar Strategy, Portfolio, Risk, filtros e arredondamento entre backtest, paper, shadow e live.

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
- **RF-144** Se um hosting externo for futuramente aprovado, permitir sua configuração por adapters sem alterar o domínio; este requisito é condicional e não faz parte do deployment inicial.
- **RF-145** Executar synthetic probes e testes periódicos de alert delivery e kill switch.
- **RF-146** Disponibilizar kill switch fora do processo de Strategy e caminho operacional quando UI/API estiver indisponível.
- **RF-147** Tratar ordens em voo e estado ambíguo durante kill switch, rollback e deploy.
- **RF-148** Fornecer bootstrap, health checks, backup/restore e smoke test reproduzíveis do ambiente local sem credenciais cloud.

## Autonomia e aprendizado contínuo
- **RF-150** Executar continuamente o ciclo perceber → classificar → selecionar → alocar → arriscar → executar → aprender sem intervenção humana rotineira.
- **RF-151** Construir um `MarketContext` versionado com regime, volatilidade, liquidez, spread, correlação, qualidade dos dados e demais features habilitadas.
- **RF-152** Determinar automaticamente quais estratégias validadas são elegíveis para o contexto atual.
- **RF-153** Calcular score/utilidade esperada líquida por estratégia considerando retorno, risco, custos, incerteza e correlação.
- **RF-154** Ranqueiar estratégias elegíveis e alocar risk budget automaticamente.
- **RF-155** Escolher `NO_TRADE` quando nenhuma estratégia superar os thresholds de utilidade/segurança.
- **RF-156** Aplicar hysteresis/cooldown para evitar churn de estratégia causado por ruído de regime.
- **RF-157** Atualizar automaticamente Strategy Health e reduzir, colocar em quarantine ou desabilitar estratégias deterioradas.
- **RF-158** Detectar data drift, concept drift, execution drift e strategy decay.
- **RF-159** Disparar pipeline de retreinamento/retuning quando critérios de drift/performance forem atingidos.
- **RF-160** Manter Model/Strategy Registry com lineage de dados, código, configuração, features, parâmetros, métricas e status.
- **RF-161** Executar champion-challenger em paper/shadow antes de promoção.
- **RF-162** Promover automaticamente challengers apenas quando todos os gates quantitativos, operacionais e de risco forem satisfeitos.
- **RF-163** Fazer rollback automático para versão anterior segura quando challenger/champion violar envelope de performance/risco/execução.
- **RF-164** Permitir contextual-bandit/Bayesian allocation entre estratégias já aprovadas, inicialmente em research/shadow e posteriormente live sob budget específico.
- **RF-165** Permitir autoajuste de parâmetros somente dentro de limites previamente validados e versionados.
- **RF-166** Impedir que learner/orchestrator aumente hard risk limits, remova circuit breakers ou altere regras de segurança.
- **RF-167** Selecionar automaticamente tática de execução conforme urgência, spread, liquidez, volatilidade, probabilidade de fill, fees e edge decay.
- **RF-168** Registrar decision trace com contexto, candidatos, scores, seleção, decisão do Risk Engine, execução e resultado.
- **RF-169** Separar loops de adaptação rápida, média e lenta para impedir retreinamento pesado no hot path.
- **RF-170** Entrar automaticamente em `SafeMode` quando estado financeiro, market data ou integridade operacional não puderem ser confiados.
- **RF-171** Recuperar conexões/processos automaticamente e reconciliar estado antes de liberar novas entradas.
- **RF-172** Suportar arquitetura primary/standby futura com fencing/leader election para impedir dupla execução.
- **RF-173** Definir exploration budget por estratégia/ativo/regime/janela e impedir sua ampliação pelo learner.
- **RF-174** Executar off-policy evaluation e shadow comparison pareado antes de exploração com capital.
- **RF-175** Detectar training-serving skew e versionar o pipeline de features usado em treino e inferência.
- **RF-176** Tratar delayed feedback e influência das próprias ordens no dado/resultado.
- **RF-177** Impedir mudança simultânea não identificável de modelo, dados, execution policy e RiskPolicy.
- **RF-178** Permitir fallback para `NO_TRADE` quando champion anterior não for seguro para o contexto atual.
- **RF-179** Fornecer RAG/GraphRAG local no Research Plane com citações, autoridade, versão, frescor, detecção de conflito e confirmação contra o commit atual, sem acesso ao hot path, credenciais ou autorização de risco/ordem.
