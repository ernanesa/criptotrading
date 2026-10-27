# Auditoria inicial e baseline — 2026-10-27 UTC

## Estado e limites da evidência

- Commit local: `882c7a1ffee25a1aec1fb0a582c3250fac7d181b`; árvore com alterações preexistentes em `AdaptiveDecisionCycle.cs` e `AdaptiveDecisionCycleTests.cs`. A versão efetivamente executada não é estabelecida pelo commit local.
- Esta é uma auditoria parcial, não uma certificação de produção. Não foram emitidas ordens nem efetuadas alterações em corretora, high-water mark, parâmetros de risco ou banco operacional.
- Rentabilidade desde o início e em 24h/7d/30d: **inconclusiva**. Resultado realizado/não realizado, número de trades e posições, bloqueios, custos, exposição e drawdown: **não verificados com dados reconciliados**. Não interpretar valores ausentes como zero.
- Latência p95/p99, CPU, memória e alocações operacionais: **não medidos**. Build e testes não são benchmarks.
- Inspeção local de nomes/status encontrou `cryptotrading-postgres-1` saudável e dois processos `dotnet`, mas não identifica o executável gerenciado nem comprova conexão, dados, conta ou versão; não se consultou o banco nesta etapa. Nenhum contêiner de worker CryptoTrading apareceu na listagem local de contêineres em execução.

## Inventário de defaults — não confundir com configuração ativa

- `src/CryptoTrading.Worker/WorkerRuntimeOptions.cs:16-60`: modo default `PAPER`, `NoTrade=true`; persistência, migrations, reconciliação, política obrigatória, stream e autonomia são opt-in. Se habilitada, a autonomia tem engine `RegimeAdaptive` e símbolo default `BTCUSDT`. Esses valores podem ser sobrescritos.
- `src/CryptoTrading.Domain/Risk/BudgetPolicy.cs:154-166`: orçamento nominal default R$ 100, teto 20 USDT, uma posição, perda diária R$ 2, drawdown R$ 5, risco por trade R$ 1, fee 10 bps e buffer 25 bps. São constantes de código, **não evidência da política vigente ou aprovação operacional**.
- `src/CryptoTrading.Backtesting/Autonomy/MultiStrategyDecisionOrchestrator.cs:102-128` constrói candidatos de tendência, breakout, reversão e squeeze; para pelo menos 20 barras usa tendência de períodos 5/15. Existência dessas classes não demonstra quais decisões estão ativas em execução.
- O projeto `tests/CryptoTrading.Persistence.IntegrationTests` existe fora da solução `CryptoTrading.slnx`; a suíte de 971 testes acima não comprova execução desse projeto.

## Achados estáticos prioritários

| Prioridade | Evidência no código | Impacto condicional e ação |
|---|---|---|
| P0 | No HEAD, `src/CryptoTrading.Persistence/PostgresTradingStore.cs` completava inventário insuficiente de venda com custo igual ao preço da venda. | Corrigido localmente em R-06; transação em PostgreSQL, entrega fora de ordem, replay e recuperação continuam não verificados. |
| P0 | `src/CryptoTrading.Worker/AccountStreamExecutionAccounting.cs:41-47`; `src/CryptoTrading.Persistence/Migrations/013_execution_accounting.up.sql:3-7`; migration 030 usa `(symbol, trade_id)` para outra tabela. | Identidade global de trade pode colidir entre símbolos. Definir identidade por ambiente/conta/símbolo e migração compatível com fatos e journal históricos; validar colisões e idempotência, não reescrever histórico às cegas. |
| P0 | `src/CryptoTrading.Worker/AutonomyCycleHostedService.cs:733-760` subtrai valores fixos de faucet Testnet quando a conta tem mais de 20 ativos. | Heurística não comprova origem dos saldos; pode omitir posição. Investigar baseline, resets e fluxos, sem transferir parâmetros à conta real. |
| P0 | `src/CryptoTrading.Worker/AutonomyCycleHostedService.cs:766-785` considera BNB suportado, mas itera o universo para valorar outros ativos. | BNB fora do universo pode não ser sinalizado como não suportado nem incluído no NAV. Validar toda a expressão de equity e criar teste com BNB e preço ausente. |
| P1 | `src/CryptoTrading.Persistence/PostgresTradingStore.Performance.cs:150-160` contabiliza vendas por fill e deduz adicional fixo de 18 bps. | Quantidade de fills não é quantidade de trades fechados; custo adicional é cenário estimado e pode duplicar custos contabilizados. Separar ambos do resultado observado. |
| P1 | `src/CryptoTrading.Persistence/PostgresTradingStore.Performance.cs:207-218` invalida delta quando fluxo é desconhecido. | Comportamento conservador; fornecer histórico de fluxos completo e identificado antes de apurar retorno de carteira. |
| P0 | `src/CryptoTrading.Persistence/PostgresTradingStore.cs:1513,1606` calcula perdas realizadas negativas por símbolo e quote; `ExecutionSubmissionGuard.cs:554` também fornece perda diária ao campo de perda acumulada. | O plano descreve risco de portfólio, mas conta, moeda, perda líquida/bruta e janela precisam de contrato demonstrável antes de uma soma global. Não afrouxar limites nem misturar contas ou moedas; sem contexto confiável, novas entradas devem permanecer bloqueadas. |

Achados são derivados de leitura estática; impacto operacional e reprodução dos cenários específicos ainda são **not verified**. Não há evidência, nesta revisão, de que esses casos tenham efetivamente ocorrido.

## Matriz inicial da documentação Binance Spot

Consulta em 2026-10-27 UTC ao [índice oficial](https://github.com/binance/binance-spot-api-docs) e ao [changelog](https://github.com/binance/binance-spot-api-docs/blob/master/CHANGELOG.md), cuja última atualização indicada era 2026-09-18. As demais páginas são `master` sem SHA fixado: revalidar antes de implementação. Esta matriz é parcial; páginas apenas listadas abaixo não foram integralmente auditadas.

| Documento/seção | Recurso / uso local observado | Lacuna ou próxima ação |
|---|---|---|
| [Testnet general info](https://github.com/binance/binance-spot-api-docs/blob/master/testnet/general-info.md#faq-periodic-reset) | Saldos virtuais; reset aproximadamente mensal inclui ordens e reposição. Heurística local em `AutonomyCycleHostedService.cs:733-760`. | Estabelecer baseline/epoch por evidência; não inferir valores de faucet pelo número de ativos. `/sapi` não é suportado em Testnet. |
| [Filtros](https://github.com/binance/binance-spot-api-docs/blob/master/filters.md) | `PRICE_FILTER`, `LOT_SIZE`, `MARKET_LOT_SIZE`, notional e outros; `BinanceSpotMarketClient.cs:279-303` lê `exchangeInfo`, `BinanceTradingRulesCache.cs` guarda regras. | Comparar filtros efetivamente implementados, referência de preço e filtros de conta; dados e validade reais não verificados. |
| [Comissões](https://github.com/binance/binance-spot-api-docs/blob/master/faqs/commission_faq.md) | Standard, tax e special; `BinanceSpotAccountClient.cs:143` consulta comissões. | Verificar configuração real de conta, maker/taker, desconto e conversão temporal da moeda cobrada, sem usar taxas fictícias. |
| [User Data Stream](https://github.com/binance/binance-spot-api-docs/blob/master/user-data-stream.md#order-update) | `executionReport` inclui fill incremental/acumulado e comissão; `BinanceUserDataStream.cs:325` faz parse; `BinanceUserDataStreamApi.cs:86` implementa assinatura moderna. | Testar duplicatas, gaps e recuperação em ordem parcial; modo efetivamente ativo não verificado. A implementação legada em `BinanceUserDataStream.cs:153-154` deve ser confrontada com retirada anunciada em changelog. |
| [Changelog 2026-01-21](https://github.com/binance/binance-spot-api-docs/blob/master/CHANGELOG.md#2026-01-21) | Anuncia indisponibilidade dos endpoints legados `userDataStream` em 2026-02-20. | Confirmar seleção e endpoints do processo efetivo; existência de código legado não prova uso atual. |
| [REST: limites por IP](https://github.com/binance/binance-spot-api-docs/blob/master/rest-api.md#ip-limits) | `BinanceRequestResilience.cs` calcula atraso para clientes que usam `BinanceRequestRetryPolicy`; prazo anteriormente era reduzido pelo teto local. | Corrigido localmente para respeitar o mínimo informado; bloqueio compartilhado entre requisições concorrentes/instâncias permanece **não verificado**. |
| [Market streams: livro local](https://github.com/binance/binance-spot-api-docs/blob/master/web-socket-streams.md#how-to-manage-a-local-order-book-correctly) | Binance exige buffer de deltas antes de snapshot e ponte de sequência; `AutonomyCycleHostedService.cs` aplica deltas apenas ao livro saudável. | Bootstrap com buffer e recuperação completa **não verificados**; não assumir livro confiável logo após snapshot isolado. |
| [Trailing Stop FAQ](https://github.com/binance/binance-spot-api-docs/blob/master/faqs/trailing-stop-faq.md) | `trailingDelta` em BIPS e filtro `TRAILING_DELTA`; pode ser perna contingente de OCO. | Uso efetivo no adaptador **não verificado**. Gatilho não garante fill; testes e autorização humana são necessários antes de ordem mutante. |
| [Order Amend Keep Priority](https://github.com/binance/binance-spot-api-docs/blob/master/faqs/order_amend_keep_priority.md) | Só reduz quantidade e mantém prioridade; depende de `amendAllowed`. | Uso no projeto e benefício não verificados; não habilitar automaticamente. |
| REST, WebSocket API, market streams, livro, timeout, limites, OCO/OTOCO/trailing, SBE e FIX | Referências disponíveis no índice oficial. | Cobertura detalhada e correspondência com código **pendentes**; timeout de ordem desconhecido exige reconciliação antes de qualquer reenvio. SBE/FIX dependem de elegibilidade e benefício medido. |

## Plano de reconciliação e baseline

1. Identificar processo, build/image digest, configuração efetiva sanitizada, ambiente, conta pseudonimizada, banco e cobertura temporal. Não inspecionar segredos.
2. Obter snapshots read-only de `free`/`locked`, ordens, fills e comissões por símbolo, com paginação completa e timestamps; comparar com fatos persistidos por identidade composta. Considerar captura sequencial não atômica e lacunas de streams.
3. Reconstruir inventário e custo com fonte verificável; identificar fluxos externos, faucet e reset por conta; valores não conhecidos permanecem desconhecidos. Valorar comissões em moeda distinta usando cotação temporal e informar fonte.
4. Congelar baseline por versão/configuração e intervalo UTC: realizado, não realizado, fees, exposição, drawdown, ordens, fills e trades fechados. Medir spread/slippage como decomposição de execução, sem abatê-los novamente se já contidos nos preços de fill.
5. Só comparar candidata com baseline em dados e execução equivalentes. Sem amostra ou conciliação suficiente, decisão: **evidência insuficiente**.

## Pesquisa e experimentos — pré-registro

Nenhuma candidata foi aprovada ou comparada. Antes de cada ensaio registrar ID, commit e diff/hash, dados/hash, universo ex ante, custos, janelas, parâmetros e todas as tentativas. Reservar teste final intocado; ajustar após consultá-lo invalida sua independência. Usar walk-forward, purga/embargo quando houver rótulos sobrepostos, ablações, sensibilidade, reamostragem por blocos e controle de múltiplos testes justificável.

Achados adicionais de leitura estática, não reproduzidos:

- `TrendFollowingBacktest.cs:108-129` usa o fechamento da própria barra que origina a decisão como referência de execução; essa hipótese pode ser otimista. Comparar com execução posterior e publicação atrasada; não generalizar automaticamente a todos os avaliadores.
- `StrategyComparisonEvaluator.cs:179-186` gera todos os sinais antes da simulação; verificar se rejeições e fills parciais são retroalimentados às estratégias com estado. Um teste de rejeição seguida de novos sinais deve observar inventário e decisões subsequentes.
- `ConservativeResearchExecutionModel.cs:218-245` pode simular compra acima ou venda abaixo do limite após spread/slippage. Um toque OHLC tampouco prova posição na fila ou preenchimento. Testar o invariante de preço limitado, separadamente da hipótese de probabilidade de fill.
- `PlattCalibrator.cs:8-10` contém parâmetros default fixos. Validar onde são usados e a calibração em dados cronológicos independentes com Brier/log loss; uma sigmoide isolada não demonstra calibração.

| ID | Hipótese verificável | Aceitação prévia / decisão |
|---|---|---|
| EXP-01 | Uma regra simples de tendência tem expectativa líquida superior a caixa e buy-and-hold ajustados ao mesmo universo e custos. | Pré-definir horizonte e amostra mínima, IC por blocos e limite de drawdown antes da execução; rejeitar se vantagem não for robusta fora da amostra. |
| EXP-02 | Regime/ATR/ADX ou outro filtro agrega valor incremental ao modelo simples. | Ablação pareada em janelas e dados idênticos, depois de custos; nenhum indicador entra apenas por popularidade. |
| EXP-03 | Reduzir o gargalo medido de processamento melhora execução líquida, não só benchmark. | Medir p50/p95/p99 e overhead, stress e resultado pareado com mesma execução/custos; sem regressão de auditoria ou proteção. |

Famílias a avaliar conforme dados: momentum, breakout, reversão, força relativa, seleção por regime e microestrutura. Market making/arbitragem ficam exploratórios até comprovar profundidade, custos, infraestrutura e capital; Margin e Futures estão fora do escopo Spot. ML requer rótulos sem vazamento, calibração, drift, custo de inferência e comparação simples; probabilidade de alta não é edge líquido.

## Performance: hipóteses, não gargalos medidos

- `BinanceMarketDataContracts.cs:157-160` copia o payload para parse; medir bytes/evento e GC antes de otimizar.
- `OutboxDispatcher.cs:320-330` processa sequencialmente mensagens de um lote. A métrica é capturada antes de cada dispatch e inclui espera após claim para mensagens posteriores; não equivale isoladamente ao tempo até o claim. Medir backlog, idade e percentis antes de mudar concorrência.
- `AutonomyCycleHostedService.cs:57-62` coalesce sinais de despertar via `DropOldest`; isso não demonstra perda de comandos da outbox. Já `:404-413` aplica deltas de depth somente quando o livro está saudável: testar bootstrap com snapshot, buffer, gap e retomada antes de confiar no estado do livro.
- Definir timestamps monotônicos por estágio, medir separadamente rede e fill, p50/p95/p99, filas, GC, CPU e overhead da instrumentação. Valores operacionais e benefício econômico: **not verified**. Auditoria e proteção não serão removidas para reduzir latência.

Referências consultadas em 2026-10-27: [Freqtrade lookahead](https://github.com/freqtrade/freqtrade/blob/develop/docs/lookahead-analysis.md), com limitações e possíveis falsos positivos/negativos; [Freqtrade aquecimento recursivo](https://github.com/freqtrade/freqtrade/blob/develop/docs/recursive-analysis.md); [Hummingbot position executor](https://github.com/hummingbot/hummingbot/blob/master/hummingbot/strategy_v2/executors/position_executor/position_executor.py), referência de ciclo de ordens e barriers, não evidência de lucratividade. A consulta ao artigo SSRN 2460551 retornou HTTP 403; não foi analisado.

## Validações locais

- `dotnet build -c Release`: passou, 0 avisos/0 erros, SDK 11.0.100-rc.1; inclui alterações preexistentes da árvore.
- `dotnet test -c Release --no-build`: passou, 971 testes, 0 falhas/ignorados; não demonstra integração real.
- `bash scripts/validate-traceability.sh`: passou, 126 IDs; 0 itens Done com cadeia completa de evidências.
- `bash scripts/test-paper-soak-chaos.sh`: passou, 3 repetições, fixture determinística em memória, sem rede/banco; `artifacts/chaos/paper-soak-chaos-20261027T092907Z-60374.md`. Não é soak operacional nem prova de reconciliação Binance.
- Testes de integração PostgreSQL, Native AOT publish, medições operacionais, contrato real Binance e reconciliação histórica: **not verified**.

### Correção local R-01 — Retry-After

- **Problema/evidência:** a política limitava `Retry-After: 30` ao teto local de 5 segundos; [Binance REST — IP Limits](https://github.com/binance/binance-spot-api-docs/blob/master/rest-api.md#ip-limits) exige aguardar o prazo informado para 418/429.
- **Hipótese:** usar o prazo do servidor como mínimo evita que o retry individual ocorra antes do prazo; não prova coordenação de outras requisições da mesma origem IP.
- **Teste RED:** `dotnet test tests/CryptoTrading.Exchange.Binance.Tests/CryptoTrading.Exchange.Binance.Tests.csproj -c Release -- --filter-class '*BinanceRequestResilienceTests*'` falhou nos dois casos 418/429 e no teste direto, com 5s em vez de 30s.
- **Implementação/GREEN:** `BinanceRequestResilience.cs` mantém limite para backoff local, mas nunca reduz o prazo positivo informado, inclusive no wrapper de política; os mesmos testes passaram: 10/10.
- **Regressão após R-01:** `dotnet build -c Release --verbosity quiet` passou com 0 avisos/erros; `dotnet test -c Release --no-build --verbosity quiet` passou com 973 testes, 0 falhas/ignorados, na árvore de trabalho atual.
- **Decisão:** correção local aceita apenas para esse contrato individual; sem medição de impacto em latência nem evidência de integração real. Não alterar proteção para reduzir tempo. Próximo passo: avaliar limitação compartilhada de IP, cabeçalhos malformados e cancelamento.
- **Rollback:** reverter somente o diff de `BinanceRequestResilience.cs` e `BinanceRequestResilienceTests.cs`; isso restaura o comportamento inseguro anterior, portanto manter NO_TRADE até outra mitigação ser validada.

### Correções locais R-02 e R-03 — reconciliação e backtest

- **R-02:** `BinanceReadOnlySnapshotProvider` agrupava ordens abertas por ID numérico; teste com o mesmo ID para BTC e ETH falhou (1 ordem em vez de 2). A chave agora inclui símbolo e ID, e duplicatas divergentes falham explicitamente. Suíte de reconciliação: 34/34. Isso não comprova integração Binance nem isolamento de contas. Rollback: reverter o diff da reconciliação e seus testes, sabendo que o risco de omissão voltará.
- **R-03:** o simulador aceitava compra acima ou venda abaixo do limite. Dois testes falharam antes da alteração; agora preços candidatos que violam o limite após quantização resultam em `NoTrade`, sem inventar um fill ao limite. Suíte de backtesting: 212/212, incluindo o caso de tick para venda. Um toque OHLC continua não provando liquidez ou posição na fila. Rollback: reverter o diff do simulador e seus testes, invalidando resultados posteriores até nova revisão.
- **Regressão após R-02/R-03:** `dotnet build -c Release --verbosity quiet` passou com 0 avisos/erros; `dotnet test -c Release --no-build --verbosity quiet` passou com 978 testes, 0 falhas/ignorados, incluindo alterações preexistentes na árvore.
- **P0 contábil ainda pendente:** a mudança de identidade de fills exige política explícita para dados legados, conta e referências existentes; não alterar unicidade ou reescrever journal antes de testar upgrade isolado e preservar aliases. Nenhum dado foi migrado ou corrigido.

### Contenção R-04 — impedir autorrecuperação insegura

- **Evidência:** o snapshot de SafeMode não atualiza sua causa ao receber um segundo incidente; no bootstrap, a causa durável é restaurada como genérica. Isso podia permitir reset automático apesar de um incidente contábil não resolvido.
- **RED/GREEN:** testes com falha contábil após SafeMode transitório e com stop restaurado falharam antes da alteração e passaram depois. O autorizador do Worker agora nega todos os resets automáticos; reconciliação saudável isolada não comprova resolução de todos os incidentes. Teste assíncrono preserva KillSwitch. `dotnet test tests/CryptoTrading.Worker.Tests/CryptoTrading.Worker.Tests.csproj -c Release`: 200/200.
- **Impacto operacional:** SafeMode também permanecerá bloqueado após falhas transitórias. O autorizador do Worker não oferece nesta implementação um caminho de reset humano: não contornar a restrição nem assumir que reinício libera a operação. É necessária uma solução futura de incidentes duráveis, restauração e fluxo humano com auditoria. Persistência indisponível/crash antes do registro durável e restart real continuam **not verified**.
- **Rollback:** reverter apenas os diffs em `ReconciliationOperationalControlResetAuthorizer.cs` e `ReconciliationOperationalControlResetAuthorizerTests.cs`; o comportamento anterior pode liberar SafeMode inseguramente e, portanto, exige NO_TRADE e mitigação alternativa antes de uso.

### Contenção R-05 — patrimônio incompleto e saldo Testnet

- **Evidência/RED:** em testes locais, BNB fora do universo gerou patrimônio 5005,025 sem seu valor; uma carteira Testnet grande gerou 10000 após suprimir saldos presumidos de faucet. O pré-envio também aceitou BNB não valorado em PAPER e TESTNET. Os testes específicos falharam antes da mudança.
- **Implementação/GREEN:** ciclo e guarda pré-envio agora usam os saldos `free + locked` integrais, sem deduzir faucet fixo, e identificam ativos positivos fora do universo como não valorados, inclusive BNB. Patrimônio incompleto é enviado como `null`; o contexto contábil deve impedir novas entradas. A guarda não exclui mais posições pequenas do agregado. A suíte Worker passou 202/202.
- **Limitações:** esta é contenção conservadora, não valoração automática de BNB nem reconciliação de faucet. Testnet com ativos fora do universo pode permanecer sem novas entradas. Preço, timestamp e base dos ativos no universo, saídas protetivas, HWM já persistido, feeds reais e banco seguem sujeitos a validação independente. Não corrigimos automaticamente histórico nem HWM.
- **Rollback:** reverter somente os diffs do ciclo, guarda e respectivos testes; isso restaura as heurísticas que omitem saldo, portanto não usar para liberar risco sem mitigação alternativa comprovada.

### Correção R-06 — proibir custo de aquisição fictício

- **RED:** a projeção que a persistência usava para venda inventava quantidade e custo ao preço da venda. Três testes locais reproduziram a aceitação indevida para inventário ausente, parcial e insuficiente após comissão na base.
- **GREEN:** `RecordExecutionAsync` passa a projeção as quantidades e custos efetivamente carregados; o cálculo de domínio rejeita venda sem base suficiente. Testes unitários de persistência: 34/34. Nenhum preço de venda é usado como custo de aquisição ausente.
- **Evidência preservada / limites:** o worker grava o evento bruto antes da contabilidade, em outra transação. A falha pode deixar projeção de ordem já atualizada; não se afirma atomicidade entre elas. Criado teste de integração de não-commit de fill/journal, compilado mas **não executado**: requer PostgreSQL de teste isolado. R-04 evita autorreset no Worker, mas persistência de SafeMode após crash e resolução/replay de evento não estão demonstradas.
- **Regressão geral após R-06:** build Release passou com 0 avisos/erros; a suíte falhou em `ModernApiClientRecoversWhenInitialSubscriptionHandshakeStalls` com `NotSupportedException` ao descartar enumerador assíncrono. A mesma suíte havia passado com 984 testes antes de R-06; a causa e relação com concorrência são investigadas, sem presumir que seja apenas intermitência. O encadeamento parou antes de rastreabilidade e chaos dessa rodada.
- **Rollback:** reverter o diff do store, o `InternalsVisibleTo` e os testes associados; o código anterior fabrica custo e não deve ser usado para produzir PnL ou liberar risco. Não alterar os fatos históricos.

### Correção R-07 — reconexão do user-data stream

- **Evidência/RED:** a suíte geral falhou no descarte do enumerador ao expirar handshake. Teste com leitura artificialmente mantida após cancelamento reproduziu determinísticamente `NotSupportedException` antes da correção.
- **GREEN:** no timeout/cancelamento de handshake, cancela-se a sessão e aguarda-se a leitura pendente antes de descartar o enumerador; a limpeza também ocorre ao interromper o consumidor. Timeout e cancelamento externo foram testados sem rede; 12/12 testes da classe passaram.
- **Limites:** transporte que ignora cancelamento pode manter a limpeza pendente indefinidamente; não há reconexão prematura nesse caso, mas falta política de abort com prazo e teste real de transporte. Reconexão real e recuperação durável continuam **not verified**. Não se presume melhora mensurada de latência.
- **Rollback:** reverter os diffs de `BinanceUserDataStreamApi.cs` e `BinanceUserDataStreamTests.cs`; a corrida anterior volta a ser possível e requer mitigação antes de operar.

### Correção R-08 — contagem de divergências

- **RED:** resolução saudável de ordem antes incrementava `cryptotrading.reconciliation.mismatches`; teste com `MeterListener` observou 1 onde o correto era 0.
- **GREEN:** a telemetria usa a mesma definição tipada de `ReconciliationRunResult.MismatchCount`, sem classificar `OpenOrderReconciled` como divergência. Suíte de reconciliação passou 34/34. Nenhuma alteração na decisão de risco ou no estado de ordens.
- **Rollback:** reverter o cálculo de métrica e seu teste; alertas podem voltar a contabilizar reconciliações saudáveis como divergências.
- **Regressão geral após R-08:** build Release 0 avisos/erros; suíte da solução 990/990; rastreabilidade passou com 126 IDs e 0 itens Done com cadeia completa; chaos Paper local passou em três repetições (`artifacts/chaos/paper-soak-chaos-20261027T100141Z-108092.md`). PostgreSQL de integração e Binance real continuam não verificados.

### Contenção R-09 — journal em replay

- **RED/GREEN:** um replay com a mesma identidade de journal e postings financeiros diferentes era aceito pelo caminho existente. A persistência agora compara o journal carregado com o candidato usando o mesmo verificador de equivalência do append de journal, incluindo postings e precisão temporal do PostgreSQL. Testes unitários de persistência: 35/35.
- **Extensão:** o replay compara também `trade_id`, `order_id`, símbolo, ativos, lado, quantidade, preço, comissão, timestamp à precisão de microssegundos e ID do journal com os fatos persistidos; diferenças falham antes do commit. Testes unitários passaram 36/36.
- **Precisão de replay:** revisão detectou falso conflito para replay idêntico quando o PostgreSQL arredonda `numeric(38,18)`. Dois testes reproduziram o problema e passaram após a comparação na escala 18 com desempate para longe de zero, conforme [tipos numéricos do PostgreSQL 18](https://www.postgresql.org/docs/18/datatype-numeric.html#DATATYPE-NUMERIC-DECIMAL). Suíte de persistência: 38/38. Teste SQL correspondente compilou, mas não foi executado; campos diferentes que persistem no mesmo valor de 18 casas são indistinguíveis sem evidência adicional.
- Após o ajuste de precisão: build Release sem avisos/erros, 1003 testes da solução passaram e rastreabilidade passou. O chaos Paper anterior passou antes deste ajuste; não foi repetido nesta rodada.
- **Limites:** isso não corrige a identidade global de `trade_id` nem prova igualdade de metadados que não foram persistidos. Resultados de taxa/qualidade no replay ainda são recalculados; não se deve interpretá-los como valores históricos verificados. Testes transacionais para replay divergente foram adicionados, mas sua execução SQL e a compatibilidade de legado permanecem **not verified** até teste em PostgreSQL isolado.
- **Rollback:** reverter as validações de journal/fatos, a leitura SQL correspondente, `ExecutionReplayFacts.cs` e os testes específicos; o replay anterior poderá aceitar fatos ou postings divergentes.

### Contenção R-10 — preços de posições secundárias

- **RED:** a guarda pré-envio aceitou em testes cotações de outra posição antigas, futuras, cruzadas e de símbolo incorreto. No ciclo, casos de preço antigo, futuro e símbolo incorreto produziram patrimônio aparentemente completo.
- **GREEN:** ciclo e guarda compartilham validação de símbolo, bid positivo, ask não cruzado, timestamp não futuro e idade máxima da política. O ciclo tenta fallback REST e mantém equity `null` se não houver cotação válida; cancelamento explícito não é engolido. A guarda rejeita a submissão ao receber cotação secundária inválida. Teste adicional reproduziu uma cotação secundária que expirava durante a releitura final da política; o guard agora revalida os timestamps mais antigo e mais recente de todas as cotações antes do envio. Testes do Worker: 212/212.
- **Limites:** os testes são offline; não medem sincronização de relógios da corretora. A validação final do guard considera todos os preços usados, mas o ciclo autônomo ainda não revalida todas as cotações após seus múltiplos I/O. Precificação do ativo principal, prova de base, HWM histórico e recuperação operacional continuam independentes. Não há demonstração de melhoria de latência ou retorno.
- **Rollback:** reverter as alterações nos dois consumidores, os testes correspondentes e `PortfolioTickerValidation.cs`; o comportamento anterior pode incorporar cotações inválidas no patrimônio.
- **Validação geral 2026-10-27 23:10 UTC:** build Release 0 avisos/erros; solução 1000 testes aprovados; projeto de integração PostgreSQL compilou, mas testes não executados; rastreabilidade passou (126 IDs, 0 Done com cadeia completa); chaos Paper em memória passou em três repetições (`artifacts/chaos/paper-soak-chaos-20261027T231021Z-676510.md`). Esses resultados incluem alterações preexistentes na árvore e não demonstram corretora, banco ou resultado financeiro reais.
- `dotnet format whitespace ... --verify-no-changes` nos arquivos de produção dos incrementos R-09/R-10: passou.
- **Regressão após a revalidação final:** 2026-10-27 23:14 UTC: build Release 0 avisos/erros; 1001 testes da solução passaram; projeto de integração compilou sem executar testes; rastreabilidade passou; chaos Paper em memória passou (`artifacts/chaos/paper-soak-chaos-20261027T231424Z-685099.md`).
- **Native AOT — not verified:** `dotnet publish src/CryptoTrading.Worker/CryptoTrading.Worker.csproj -c Release -r linux-x64 --self-contained -p:PublishAot=true --no-restore` falhou com `NETSDK1047`, pois o assets local não contém `net11.0/linux-x64`. Esse erro impede avaliar AOT; não prova nem incompatibilidade nem compatibilidade do código. Não foi executado restore de runtime/pacotes nessa tentativa.

## Rollback e próximos passos

As alterações funcionais R-01 a R-10 são delimitadas acima. Este diagnóstico pode ser revertido isoladamente removendo este arquivo; preservar a evidência de auditoria em qualquer processo formal de rollback. Para correções futuras, preparar migration aditiva e reversão testada em cópia isolada; não destruir fatos, nem ajustar HWM para liberar operações. Prioridade: comprovar processo e fontes, reproduzir P0 em fixtures e banco isolado, e só depois construir baseline e candidatos.
