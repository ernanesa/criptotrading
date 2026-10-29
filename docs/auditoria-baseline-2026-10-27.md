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

### Correção de afirmações anteriores — 2026-10-29

As afirmações anteriores de que todos os filtros de LIMIT estavam cobertos e que evidência de conta permitia submissão segura estão **superseded**. Revisão estática encontrou respostas incompletas de `myFilters`, filtros de símbolo ignorados, limites malformados e referências de preço sem identidade/frescor suficientemente verificados. Dois testes reproduziram que BUY e SELL chegavam ao cliente interno mesmo com evidência incompleta. A fronteira agora rejeita novas submissões com `PRETRADE_EVIDENCE_UNVERIFIED` após as verificações locais; consulta e cancelamento não foram modificados. **Isso também bloqueia novas saídas LIMIT** por essa fronteira. A versão implantada e a existência de ordens abertas são desconhecidas; não se alterou o processo em execução.

Revisão adicional reproduziu subcontagem de saldo/ordens por identificadores aceitos mas não normalizados, aceitação de lado desconhecido e conversão inexata de `1,000` ou de quantidades fora da precisão decimal. O estado de conta agora normaliza identificadores, exige lado conhecido e saldo-base presente e rejeita números não representáveis exatamente; a bateria Binance passou **196/196**. Isso **não** torna o snapshot atômico nem resolve compras abertas em outros símbolos da mesma base; o resultado não autoriza ordens e a contenção permanece ativa.

O registro DI do Worker deixou de expor `BinanceSpotOrderClient` como serviço concreto; a interface resolve o decorator que bloqueia submissões. Dois testes locais de composição passaram, mas isso não impede construção direta do cliente em outro código nem prova configuração de um processo implantado. Consulta/cancelamento foram exercitados apenas com fake, sem chamada à corretora.

O executor registrava uma reprovação local de pré-trade como se fosse HTTP 400 da Binance. Um teste offline reproduziu a mensagem falsa; o log agora distingue bloqueio local e usa razões limitadas, sem inventar status HTTP. A projeção persistida ainda usa `OrderStatus.Rejected` sem proveniência detalhada; a distinção durável continua pendente.

Para GET autenticado, respostas 418/429 sem `Retry-After` HTTP válido não são repetidas; datas inválidas/negativas são rejeitadas e prazos válidos não são encurtados. Isso não implementa coordenação por IP entre clientes ou processos. Nenhuma medição de rate-limit, latência ou integração real foi realizada.

Em uma limpeza anterior desta sessão, foram removidos 42 relatórios locais de chaos e 12 relatórios de quality, ignorados pelo Git. Sua recuperação não foi verificada; não se deve afirmar que nenhuma evidência foi descartada. Não serão removidos outros dados, relatórios ou históricos como parte de compactação de contexto.

## 2. Matriz Binance Spot (consulta 2026-10-27/28 UTC)

Fonte: [índice](https://github.com/binance/binance-spot-api-docs), [filters.md](https://github.com/binance/binance-spot-api-docs/blob/master/filters.md), [rest-api.md](https://github.com/binance/binance-spot-api-docs/blob/master/rest-api.md). Páginas são `master` sem SHA: revalidar.

| Área | Uso local | Situação |
|---|---|---|
| `exchangeInfo` + filtros de símbolo | `BinanceSymbolTradingRulesParser` | Implementado; `tickSize=0` tratado como desabilitado |
| `MAX_NUM_ORDERS`, `MAX_POSITION` | avaliador de conta | Cobertura e identidade/frescor do estado não comprovados; novas submissões bloqueadas |
| `EXCHANGE_MAX_NUM_ORDERS` | `myFilters` | Cobertura global/estado concorrente não comprovados; novas submissões bloqueadas |
| `MAX_ASSET` | `myFilters` (limite em string) | Parsing parcial; coleções nulas/desconhecidas não demonstram ausência de filtro |
| `PERCENT_PRICE(_BY_SIDE)` | `/api/v3/referencePrice` (peso 2) | Símbolo e frescor da referência não são comprovados pela fronteira atual |
| Filtros inaplicáveis a LIMIT GTC | — | Classificados e ignorados explicitamente |
| `myFilters` (peso 40) | `BinanceSpotAccountClient` | Cache desabilitado por padrão após correção; leitura real não verificada |
| `Retry-After` (429/418) | `BinanceRateLimitGate` | A espera é aplicada por instância de cliente de conta; não há coordenação padrão entre clientes ou processos. Não encurtar o prazo da Binance. |
| Market stream / `serverShutdown` | `BinanceSpotMarketDataClient` | Evento de desconexão + reconexão |
| Livro local L2 | `BinanceLocalOrderBook` | Exige snapshot + bridge; invalida em gap/desconexão |
| User data stream | API assinada (não `listenKey`) | Cancelamento/handshake serializados |
| SBE / FIX | não usados | Fora de escopo até haver benefício medido |

## 3. Alterações aplicadas

Cada linha: problema → correção → rollback. Todas têm teste local.

| # | Problema | Correção | Rollback |
|---|---|---|---|
| R-01 | `Retry-After` truncado pelo teto local | prazo do servidor é mínimo | reverter `BinanceRequestResilience` |
| R-02 | ordens abertas agregadas só por `orderId` | chave `(symbol, orderId)` + conflito explícito | reverter `ReconciliationContracts` |
| R-03 | simulador executava fora do OHLC/limite | `NoTrade` ao violar | reverter `ConservativeResearchExecutionModel` |
| R-04 | autorreset após incidente contábil | reset automático negado | reverter autorizador |
| R-05 | saldo Testnet suprimido; BNB omitido | quantities integrais; ativo não valorado ⇒ equity `null` | reverter ciclo + guard |
| R-06 | custo de aquisição fabricado na venda | domínio rejeita venda sem base | reverter `PostgresTradingStore` |
| R-07 | descarte concorrente no handshake | aguarda leitura pendente | reverter `BinanceUserDataStreamApi` |
| R-08 | divergência saudável contada como mismatch | usa `MismatchCount` tipado | reverter telemetria |
| R-09 | replay aceitava postings/fatos divergentes | compara fatos + journal, escala 18 | reverter validações |
| R-10 | cotações secundárias sem validação | símbolo/idade/spread validados | reverter ciclo + guard |
| R-11 | fill quantizado fora do candle | `NoTrade` | reverter simulador |
| R-12 | `serverShutdown` ignorado | evento de desconexão + reconexão | reverter parser/cliente |
| R-13 | sinais L2 antes do bridge | `IsSynchronized` obrigatório | reverter livro |
| RC3 | .NET 11 RC1 | SDK/digests/referências RC3 | reverter pins em conjunto |
| R-15 | fill no candle do sinal | padrão next-bar; legado explícito | reverter default e doc |
| R-16 | stream continuava após falha contábil | contenção + erro sanitizado | reverter `AccountStreamHostedService` |
| R-17 | `BackgroundServiceExceptionBehavior.Ignore` aceito | validação rejeita no startup | reverter `WorkerHostFailureSafety` |
| R-18 | submissão sem filtros globais/conta | bloqueio `ACCOUNT_EXCHANGE_FILTERS_UNVERIFIED` | reverter fronteira |
| R-19 | rate limit não compartilhado | gate apenas por instância de cliente; coordenação por IP permanece pendente | reverter gate |
| R-20 | `myFilters` não consultado | provider + avaliador; sua liberação de ordens está **superseded** pela contenção de 2026-10-29 | não reativar sem revisão e testes de contrato |
| R-21 | `tickSize=0` rejeitado | tratado como desabilitado | reverter parser |
| R-22 | filtros percentuais bloqueavam tudo | avaliação com `referencePrice` | reverter avaliador |
| R-23 | `MAX_POSITION` bloqueava compra | posição = free+locked+BUY abertos | reverter estado de conta |
| R-24 | `MAX_NUM_ORDERS` bloqueava símbolo | contagem por símbolo | reverter campo |
| R-25 | fronteira pré-trade com 100+ linhas e 3 ramos de erro | extraída em `RefreshRulesAsync`, `ValidateSymbolFiltersAsync`, `ValidateAccountFiltersAsync`, `ReadTradingStateAsync`, `ReadMaxPosition`, `ReadMaxSymbolOrders` | reverter `BinancePreTradeOrderClient` |
| R-26 | `/myFilters` (peso 40) em toda submissão | cache de 5 min anterior **superseded**; default agora é zero | não ativar cache para autorização sem política de frescor aprovada |
| R-27 | `orderTypes` do símbolo não era validado | parser exige o campo; fronteira rejeita tipo não anunciado ou metadado ausente | reverter `BinanceModels`/parser/fronteira |
| R-28 | `canTrade=false` não bloqueava submissão | estado de conta carrega `CanTrade`; fronteira rejeita com `ACCOUNT_CANNOT_TRADE` | reverter record + fronteira |
| R-29 | `BinanceSymbolTradingRules.ExchangeFilters` nomeava filtros **de símbolo**, ambíguo após existir `BinanceExchangeInfo.ExchangeFilters` (globais) | renomeado para `SymbolFilters` | renomear de volta |
| R-30 | métrica "realistic net profit" subtraía 18 bps fixos de um PnL que **já inclui comissões** | campo removido do store e da API; custo estimado não é mais apresentado como resultado | restaurar campo |

## 3.1 Contrato `orderTypes`

`exchangeInfo` anuncia por símbolo os tipos aceitos. O adaptador agora:

- exige `orderTypes` não vazio ao parsear (payload sem o campo é tratado como malformado);
- rejeita com `ORDER_TYPE_NOT_ALLOWED` quando o tipo pedido não consta;
- rejeita com `ORDER_TYPE_METADATA_UNAVAILABLE` quando a lista está ausente.

Fixtures de teste foram atualizadas para refletir o payload real da venue — a mudança quebrou 20 testes até isso ser feito, o que confirmou que o campo antes era simplesmente ignorado.

## 4. Bloqueios reais (dependem de acesso externo)

1. **Reconciliação**: identificar processo/ambiente/conta e obter fills, comissões, saldos e fluxos. Sem isso, rentabilidade permanece inconclusiva.
2. **PostgreSQL isolado autorizado**: migrations, atomicidade, identidade composta de fills e recuperação de venda sem base.
3. **Fence durável do market stream**: `marketStreamEpoch` é local; decisão → outbox → dispatch precisa de geração persistida e teste multiworker. Se a autorização vencer a corrida, a ordem é *em voo* e exige reconciliação, não reenvio.
4. **Baseline de latência e validação estatística**: exige processo real, dataset com proveniência verificável e protocolo pré-registrado.
5. **`Identity global de trade_id`**: colisão entre símbolos permanece; corrigir exige migração aditiva e tratamento de legado.
6. **Decisão do operador**: novas submissões só ocorrem em TESTNET com flags explícitas; redução de exposição via nova ordem está sujeita a todos os gates.

## 5. Validação atual

- Build Release com .NET 11 RC3: **0 avisos, 0 erros**.
- Gate completo 2026-10-29 00:56 UTC: **1.205 testes aprovados**, 0 falhas, com a contenção pré-trade ativa. Build Release RC3 sem avisos, formatação aprovada, rastreabilidade aprovada (126 IDs, 0 Done com cadeia completa) e chaos Paper determinístico aprovado (`artifacts/chaos/paper-soak-chaos-20261029T005601Z-1999720.md`). Esses resultados não incluem PostgreSQL de integração, Binance autenticada, Native AOT ou medição de latência.
- `dotnet format --verify-no-changes`, rastreabilidade (126 IDs, 0 itens Done com cadeia completa) e chaos Paper determinístico: aprovados.
- **Gate agregado `scripts/test-local-gates.sh`: 11/11 PASS** — readiness, traceability, secret-storage, secret-initializer, binance-readonly-preflight, api-smoke, operational-control, shadow-operational-gate, oos-sensitivity, paper-soak-chaos, ledger-recovery. Evidência: `artifacts/quality/local-gates-20261028T203347Z-1775919.md`.
- Não executados: testes PostgreSQL de integração, contrato Binance autenticado, Native AOT, benchmarks.

## 6. Achados abertos com referência de código

| Severidade | Local | Problema | Dependência para corrigir |
|---|---|---|---|
| P0 | `PostgresTradingStore.cs:1513,1606`; `ExecutionSubmissionGuard.cs:554` | perda diária calculada por símbolo/quote e repassada como "perda acumulada" | contrato de escopo de carteira, conta e moeda |
| P0 | `AccountStreamExecutionAccounting.cs:41-47`; migration `013` | `trade_id` global único colide entre símbolos | migração aditiva + política de legado |
| P1 | `PostgresTradingStore.Performance.cs:150-160` | conta fills de venda como trades e deduz 18 bps adicionais | **parcialmente corrigido (R-30)**: custo fabricado removido; definição de trade segue por fill |
| P1 | `AccountStreamHostedService` | fill executado sem base fica sem registro contábil durável | PostgreSQL isolado + replay |
| P1 | `AutonomyCycleHostedService` | decisão pode atravessar desconexão do market stream até o dispatch | fence durável + teste multiworker |

## 7. Como reproduzir

```bash
DOTNET_LOCAL_ROOT=/tmp/kilo/dotnet-rc3 bash scripts/dotnet-local.sh build CryptoTrading.slnx -c Release
DOTNET_LOCAL_ROOT=/tmp/kilo/dotnet-rc3 bash scripts/dotnet-local.sh test  CryptoTrading.slnx -c Release --no-build
bash scripts/validate-traceability.sh
PAPER_CHAOS_DOTNET_COMMAND=/tmp/kilo/dotnet-rc3/dotnet bash scripts/test-paper-soak-chaos.sh
```

O SDK RC3 é instalação temporária em `/tmp/kilo`; `.tools/dotnet` (RC1) não foi sobrescrito.
