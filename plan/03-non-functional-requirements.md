# 03 - Requisitos Não Funcionais (RNF)

## Confiabilidade
- **RNF-001** Nenhuma ordem reconhecida pela exchange pode desaparecer do ledger interno.
- **RNF-002** Toda ação de ordem deve ser idempotente ou reconciliável.
- **RNF-003** Startup live deve reconciliar conta antes de liberar novas entradas.
- **RNF-004** Falha de conexão deve resultar em estado seguro; não em repetição cega.
- **RNF-005** Processos devem reiniciar sem corromper estado.
- **RNF-006** Eventos financeiros críticos não podem ser descartados por overflow, sampling ou shutdown.
- **RNF-007** Entrega distribuída assume at-least-once com idempotência e reconciliação; nenhuma alegação de exactly-once sem prova formal do boundary.

## Performance
- **RNF-010** Hot path market-event → signal → risk deve evitar I/O bloqueante.
- **RNF-011** Meta inicial interna: p95 ≤ 25 ms e p99 ≤ 75 ms para decisão padrão, excluindo rede da exchange; benchmark definirá baseline real.
- **RNF-012** API de leitura do dashboard: p95 ≤ 300 ms sob carga nominal.
- **RNF-013** Alocações de memória no hot path devem ser medidas e reduzidas quando relevantes.
- **RNF-014** Backtests devem paralelizar de modo configurável sem esgotar RAM.

## Segurança
- **RNF-020** Chaves de trading nunca terão permissão de saque.
- **RNF-021** Segredos não podem existir em Git, imagens Docker ou logs.
- **RNF-022** TLS obrigatório externamente.
- **RNF-023** Privilégio mínimo em cloud/exchange/database.
- **RNF-024** IP allowlist na exchange quando operacionalmente viável.
- **RNF-025** Dependências e imagens devem passar por scanning automatizado.
- **RNF-026** Controles web seguirão OWASP ASVS 5.0 como referência proporcional ao risco.

## Integridade temporal e financeira
- **RNF-030** UTC é padrão de persistência.
- **RNF-031** Duração/latência usa relógio monotônico.
- **RNF-032** Clock drift deve ser monitorado; warning e stop thresholds configuráveis.
- **RNF-033** Valores de preço/quantidade destinados à exchange usam representação decimal exata e filtros da exchange.
- **RNF-034** Indicadores podem usar floating point quando erro numérico estiver compreendido e testado.

## Auditabilidade
- **RNF-040** Toda decisão live deve possuir correlation/causation IDs.
- **RNF-041** Deve ser possível reconstruir sinal → target → decisão de risco → ordem → fill.
- **RNF-042** Configuração e strategy version usadas em cada trade devem ser recuperáveis.
- **RNF-043** Ledger financeiro é append-only; correções usam lançamentos compensatórios.
- **RNF-044** Fee preserva ativo, quantidade, valuation source/time e vínculo ao fill.
- **RNF-045** Cada decisão de execução deve preservar hash/versão, fonte, timestamp, validade e regra da tabela de comissão usada no sizing e no custo esperado.

## Observabilidade
- **RNF-050** Traces, metrics e logs estruturados via OpenTelemetry.
- **RNF-051** Alertas críticos devem conter contexto acionável, não apenas mensagem de erro.
- **RNF-052** Logs não podem conter secrets.

## Portabilidade
- **RNF-060** Imagens Linux `amd64` e `arm64`.
- **RNF-061** Runtime não deve depender de serviço proprietário de cloud para lógica de negócio.
- **RNF-062** Adapters de secrets/object storage/telemetry isolam provider.
- **RNF-063** Deploy padrão deve funcionar com Docker Compose em uma única VM.

## Manutenibilidade
- **RNF-070** Modular monolith com boundaries verificáveis.
- **RNF-071** Strategy, Risk e Execution não podem criar dependências circulares.
- **RNF-072** API pública interna deve ser pequena e documentada.
- **RNF-073** ADR obrigatório para decisão arquitetural duradoura.

## Testabilidade
- **RNF-080** Clock, exchange, random e market data devem ser substituíveis por doubles/simulators.
- **RNF-081** Backtest deve ser determinístico quando seed e dataset forem iguais.
- **RNF-082** Hot-path changes exigem benchmark antes/depois.
- **RNF-083** Código crítico de risco/ordens exige testes de invariantes e cenários de falha.

## Disponibilidade e recuperação
- **RNF-090** Meta inicial live: 99,5% de disponibilidade mensal do engine; evoluir para 99,9% após maturidade.
- **RNF-091** RTO inicial ≤ 15 min para falha de aplicação em single-node.
- **RNF-092** Order intent, risk decision, order/fill e ledger possuem RPO efetivo zero no processo normal; backup/PITR de desastre tem RPO alvo ≤ 15 min e recuperação é seguida por reconciliação.
- **RNF-093** RTO/RPO devem ser medidos em restore drill e distinguir falha de processo, host, banco e região.

## Custo
- **RNF-100** O ambiente inicial deve executar integralmente nesta máquina local, sem criar ou exigir recursos AWS, Oracle Cloud, VPS ou outro serviço de compute gerenciado.
- **RNF-101** Qualquer destino cloud/hosting é opção futura e exige decisão explícita, ADR, estimativa de custo, guardrails, threat model e plano de migração antes de entrar no roadmap ativo.
- **RNF-102** Escala só será adicionada com evidência de gargalo.
- **RNF-103** O perfil local deve declarar e medir budgets de CPU, memória, disco, I/O e retenção para evitar que research, telemetria ou backtest prejudiquem o trading runtime.
