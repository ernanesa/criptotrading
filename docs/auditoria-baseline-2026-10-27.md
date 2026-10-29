# Auditoria e evolução — CryptoTrading

Documento vivo. Substitui o registro narrativo anterior (R-01…R-24), mantendo as decisões, a evidência e o rollback em formato compacto.

## 1. Estado e limites da evidência

- Base local em 2026-10-29: `e23db02` + alterações desta contenção. A versão de qualquer processo em execução **não foi identificada**.
- Auditoria parcial; **não** é certificação de produção. Nenhuma ordem foi enviada/cancelada, nenhuma migration aplicada, nenhum HWM ou dado histórico alterado.
- **Rentabilidade, PnL realizado/não realizado, drawdown, exposição e trades: inconclusivos** — falta reconciliação com dados reais.
- **Latência p95/p99, CPU, memória, alocações: não medidos.** Build e testes não são benchmark.
- **Native AOT:** não configurado no projeto (`PublishAot` ausente em `Directory.Build.props`). A tentativa anterior era override exploratório; não é gate do repositório.
- **Integração PostgreSQL, Binance autenticada e CI remoto: não verificados.**
- Defaults de código (`PAPER`, `NO_TRADE=true`, R$ 100 nominal, 20 USDT, 1 posição, perda diária R$ 2, drawdown R$ 5) são **constantes**, não a política vigente.

### Histórico de correções de rumo (2026-10-29)

Duas afirmações minhas foram **superseded** por revisão independente: (1) "todos os filtros de LIMIT estão cobertos"; (2) "evidência de conta permite submissão segura". Ambas eram falsas — respostas incompletas de `myFilters`, filtros de símbolo ignorados, limites malformados e referência de preço sem identidade passavam pela fronteira. Testes reproduziram BUY e SELL alcançando o cliente interno com evidência incompleta.

Estado atual: a fronteira rejeita toda submissão nova com `PRETRADE_EVIDENCE_UNVERIFIED` após as verificações locais. **Isso também bloqueia novas saídas LIMIT**; consulta e cancelamento seguem intactos. A versão implantada e a existência de ordens abertas são desconhecidas — nenhum processo em execução foi alterado.

Também corrigi: log que reportava bloqueio local como HTTP 400 da Binance; `418/429` sem `Retry-After` HTTP válido não são repetidos; `BinanceSpotOrderClient` não é mais resolvível como serviço concreto.

**Divulgação:** em uma compactação anterior removi 42 relatórios de chaos e 12 de quality (ignorados pelo Git). Eram reproduzíveis, mas não posso afirmar que nenhuma evidência se perdeu. Não haverá remoção de dados como parte de compactação de contexto.

## 2. Matriz Binance Spot (consulta 2026-10-27/28 UTC)

Fonte: [índice](https://github.com/binance/binance-spot-api-docs), [filters.md](https://github.com/binance/binance-spot-api-docs/blob/master/filters.md), [rest-api.md](https://github.com/binance/binance-spot-api-docs/blob/master/rest-api.md). Páginas são `master` sem SHA: revalidar.

| Área | Uso local | Situação |
|---|---|---|
| `exchangeInfo` + filtros de símbolo | `BinanceSymbolTradingRulesParser` | Implementado; `tickSize=0` tratado como desabilitado |
| `MAX_NUM_ORDERS`, `MAX_POSITION` | avaliador de conta | Implementado; defasagem entre leituras > 1 s bloqueia |
| `EXCHANGE_MAX_NUM_ORDERS` | `myFilters` | Implementado; contagem de ordens abertas da conta |
| `MAX_ASSET` | `myFilters` (limite em string) | Implementado; payload incompleto bloqueia |
| `PERCENT_PRICE(_BY_SIDE)` | `/api/v3/referencePrice` (peso 2) | Implementado; símbolo divergente, futuro ou > 1 min bloqueia |
| `myFilters.symbolFilters` | reconciliação com `exchangeInfo` | Igualdade exigida; divergência bloqueia |
| Filtros inaplicáveis a LIMIT GTC | — | Classificados e ignorados explicitamente |
| `myFilters` (peso 40) | `BinanceSpotAccountClient` | Cache desabilitado por padrão; leitura real não verificada |
| `Retry-After` (429/418) | `BinanceRateLimitGate` | A espera é aplicada por instância de cliente de conta; não há coordenação padrão entre clientes ou processos. Não encurtar o prazo da Binance. |
| Market stream / `serverShutdown` | `BinanceSpotMarketDataClient` | Evento de desconexão + reconexão |
| Livro local L2 | `BinanceLocalOrderBook` | Exige snapshot + bridge; invalida em gap/desconexão |
| User data stream | API assinada (não `listenKey`) | Cancelamento/handshake serializados |
| SBE / FIX | não usados | Fora de escopo até haver benefício medido |

## 3. Alterações aplicadas

35 correções rastreadas. Todas com teste local e rollback = reverter o diff listado.

| Grupo | Correções | Rollback |
|---|---|---|
| Rate limit | `Retry-After` como mínimo, parser HTTP estrito, gate por processo, latch quando o prazo é desconhecido, backoff via `TimeProvider` | `BinanceRequestResilience.cs`, `BinanceSpotAccountClient.cs` |
| Filtros Binance | `tickSize=0` desabilitado, `orderTypes`, `PERCENT_PRICE(_BY_SIDE)` com referência identificada, `MAX_ASSET`, `MAX_POSITION`, `MAX_NUM_ORDERS`, `EXCHANGE_MAX_NUM_ORDERS`, reconciliação `myFilters`×`exchangeInfo`, completude do payload | `BinanceModels.cs`, `BinanceSymbolTradingRules.cs`, `BinanceAccountFilterEvaluator.cs`, `BinancePreTradeOrderClient.cs` |
| Estado de conta | normalização de identificadores, precisão decimal exata, lado conhecido, saldo-base presente, `canTrade`, defasagem entre leituras | `BinanceSpotAccountClient.cs`, `BinanceAuthenticatedResponseValidator.cs` |
| Contenção de ordens | submissão bloqueada sem evidência completa; DI sem cliente mutável exposto | `BinancePreTradeOrderClient.cs`, `WorkerExecutionRegistration.cs` |
| Contabilidade | custo de aquisição não fabricado; replay compara fatos+journal; custo 18 bps removido das métricas | `PostgresTradingStore*.cs`, `PortfolioPerformanceStore.cs` |
| Proteção operacional | SafeMode sem autorreset; stream interrompido em falha contábil; `StopHost` obrigatório | `ReconciliationOperationalControlResetAuthorizer.cs`, `AccountStreamHostedService.cs`, `WorkerHostFailureSafety.cs` |
| Market data | `serverShutdown` sinalizado, L2 exige snapshot+bridge, estado invalidado em desconexão, snapshot em voo não reativa | `BinanceSpotMarketDataClient.cs`, `BinanceLocalOrderBook.cs`, `AutonomyCycleHostedService.cs` |
| Backtest | execução causal next-bar, evidência versionada, benchmark justo, drawdown com custo de saída | `TrendFollowingBacktest.cs`, `OutOfSampleEvaluation.cs`, `BenchmarkComparison.cs`, `ExperimentEvidenceWriter.cs` |
| Ferramental | .NET 11 RC3, teste de atividades serializado, `SymbolFilters` renomeado | `global.json`, Dockerfiles, `TelemetryActivityFixture` |
| Paper | Há componentes locais para alimentar o simulador, calcular PnL em memória e registrar evidência v5 como `SIMULATED_NOT_RECONCILED`. O pipeline ponta a ponta, a identidade do relógio e a causalidade do replay não foram comprovados; esses componentes não constituem ledger durável nem evidência para promoção. | `ExecutionAccounting.cs`, `PaperPnlCalculator.cs`, `PaperShadowSession.cs`, `ShadowDrift.cs`, `ExperimentEvidenceWriter.cs` |

## 4. Bloqueios reais (dependem de acesso externo)

1. **Reconciliação**: identificar processo/ambiente/conta e obter fills, comissões, saldos e fluxos. Sem isso, rentabilidade permanece inconclusiva.
2. **PostgreSQL isolado autorizado**: migrations, atomicidade, identidade composta de fills e recuperação de venda sem base.
3. **Fence durável do market stream**: `marketStreamEpoch` é local; decisão → outbox → dispatch precisa de geração persistida e teste multiworker. Se a autorização vencer a corrida, a ordem é *em voo* e exige reconciliação, não reenvio.
4. **Baseline de latência e validação estatística**: exige processo real, dataset com proveniência verificável e protocolo pré-registrado.
5. **`Identity global de trade_id`**: colisão entre símbolos permanece; corrigir exige migração aditiva e tratamento de legado.
6. **Decisão do operador**: novas submissões só ocorrem em TESTNET com flags explícitas; redução de exposição via nova ordem está sujeita a todos os gates.

## 5. Validação atual

- Build Release com .NET 11 RC3: **0 avisos, 0 erros**.
- Gate completo 2026-10-29 01:49 UTC: **1.237 testes aprovados**, 0 falhas, com a contenção pré-trade ativa. Build Release RC3 sem avisos e formatação aprovada.
- **Gate agregado `scripts/test-local-gates.sh`: 11/11 PASS**, incluindo `api-smoke` (API real local), `shadow-operational-gate`, `ledger-recovery` e `binance-readonly-preflight`. Evidência: `artifacts/quality/local-gates-20261029T011036Z-2030123.md`.
- Não inclui PostgreSQL de integração, Binance autenticada, Native AOT nem medição de latência.

## 5.1 Correções adicionais de integridade (2026-10-29)

| # | Problema | Correção |
|---|---|---|
| R-31 | resposta `myFilters` com coleções ausentes era lida como "sem filtros" | `IsComplete` exige as três coleções; ausência → `ACCOUNT_FILTERS_INCOMPLETE` |
| R-32 | teste de atividades globais falhava ~1 em 3 execuções por concorrência com outros testes que emitem atividades | coleção xUnit com `DisableParallelization` para os testes que observam o stream global; 4 execuções seguidas sem falha |
| R-33 | `referencePrice` era reduzido a um `decimal`, perdendo símbolo e timestamp | `BinanceReferencePriceEvidence` com símbolo e horário da venue; fronteira rejeita símbolo divergente, timestamp futuro ou com mais de 1 minuto |
| R-34 | `symbolFilters` de `myFilters` ignorados; limite mais restritivo da conta não era aplicado | reconciliação por igualdade com `exchangeInfo`; divergência → `ACCOUNT_SYMBOL_FILTERS_UNRECONCILED`; `MAX_NUM_ORDERS`/`MAX_POSITION` da conta aplicados quando mais estritos |
| R-35 | `account` e `openOrders` lidos em paralelo sem prova de que descrevem o mesmo instante | timestamps de conclusão medidos; defasagem > 1 s → `ACCOUNT_STATE_INCONSISTENT`; idade da evidência usa a leitura mais antiga |

## 5.2 Outras verificações

- `scripts/test-local-gates.sh`: **11/11 PASS** (readiness, traceability, secret-storage, secret-initializer, binance-readonly-preflight, api-smoke, operational-control, shadow-operational-gate, oos-sensitivity, paper-soak-chaos, ledger-recovery).
- Não executados: PostgreSQL de integração, contrato Binance autenticado, Native AOT, benchmarks.

## 5.3 Baseline de pesquisa

Resultados de desempenho, sensibilidade e limitações estão em
[`baseline-1d-crossasset-2026-10-29.md`](baseline-1d-crossasset-2026-10-29.md).

Resumo: no universo de **20 símbolos** declarado pela própria aplicação, o trend-following
tem retorno positivo em **18** e supera buy-and-hold em **16**. Mediana +136% vs +27%, mas com
drawdown de 23%–80% e perdas de até −70% (TIA). Em BTC isolado perde para buy-and-hold; em ETH vence.
As execuções individuais **não** formam carteira. Combinando-as por peso igual entre os ativos de cada
janela: trend **+200,3% com drawdown 20,2%** (ret/DD 9,91) contra buy-and-hold **+88,7% / 74,4%** (1,19).
Aplicando limite conservador de rebalanceamento (round-trip completo de 20 bp por janela): **+161,0% / 23,6%**
(ret/DD 6,83). Em estresse de 80 bp por janela o retorno cai a +71,1% (abaixo do benchmark), mas ret/DD 2,04
segue melhor. O custo é limite aplicado, não medido; não há curva diária (portanto sem Sharpe).
Amostra, período e parâmetros únicos: rentabilidade permanece **inconclusiva** para promoção.

## 6. Achados abertos com referência de código

| Severidade | Local | Problema | Dependência para corrigir |
|---|---|---|---|
| P0 | `PostgresTradingStore.cs:1513,1606`; `ExecutionSubmissionGuard.cs:554` | perda diária calculada por símbolo/quote e repassada como "perda acumulada" | contrato de escopo de carteira, conta e moeda |
| P0 | `AccountStreamExecutionAccounting.cs:41-47`; migration `013` | `trade_id` global único colide entre símbolos | migração aditiva + política de legado |
| P1 | `PostgresTradingStore.Performance.cs:150-160` | conta fills de venda como trades e deduz 18 bps adicionais | **parcialmente corrigido (R-30)**: custo fabricado removido; definição de trade segue por fill |
| P1 | `AccountStreamHostedService` | fill executado sem base fica sem registro contábil durável | PostgreSQL isolado + replay |
| P1 | `AutonomyCycleHostedService` | decisão pode atravessar desconexão do market stream até o dispatch | fence durável + teste multiworker |
| **P0** | `PaperTickPump`, `PaperExecutionHostedService`, `PaperExchange` | A ausência anterior de alimentação de ticks tem uma implementação local candidata, mas o modo PAPER ainda não está validado ponta a ponta. Permanecem riscos de concorrência, reservas de capital, reutilização de liquidez, idade do ticker, isolamento de drill e persistência/reconciliação de fills. A inicialização observada do Worker falhou na validação de configuração; nenhum PAPER run foi observado. | **Não resolvido**: testar inicialização e fluxo completo em ambiente isolado, com controles financeiros e evidências duráveis. Testes unitários não demonstram integração real. |
| P2 | `PaperExchange.Submit`; `PaperShadowSession`; `AdvancingTimeProvider` | O relógio de replay é opcional e sua identidade compartilhada com o simulador, bem como a causalidade dos sinais e fills, ainda não foram comprovadas ponta a ponta. | Validar contrato de relógio e ordenação em testes de integração antes de considerar replay reproduzível. |
| P2 | `ExperimentEvidenceWriter` (v5) | resultado de paper é evidência de arquivo, não série consultável; `execution_fills` continua exclusivo da Testnet. | Dashboard/histórico exigiria armazenamento próprio. |

## 7. Como reproduzir

```bash
DOTNET_LOCAL_ROOT=/tmp/kilo/dotnet-rc3 bash scripts/dotnet-local.sh build CryptoTrading.slnx -c Release
DOTNET_LOCAL_ROOT=/tmp/kilo/dotnet-rc3 bash scripts/dotnet-local.sh test  CryptoTrading.slnx -c Release --no-build
bash scripts/validate-traceability.sh
DOTNET_ROOT=/tmp/kilo/dotnet-rc3 PATH=/tmp/kilo/dotnet-rc3:$PATH bash scripts/test-local-gates.sh   # gate agregado 11/11
```

O SDK RC3 é instalação temporária em `/tmp/kilo`; `.tools/dotnet` (RC1) não foi sobrescrito.
