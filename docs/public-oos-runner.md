# Runner OOS público local

`CryptoTrading.PublicOosRunner` consome um journal criado pelo
`PublicKlinesRecorder`, executa `PublicKlineDatasetLoader` + quality gate e
avalia os quatro baselines fixos (trend following, breakout, mean reversion e
adaptive grid) sobre o mesmo ativo e candles. Usa janelas de treino `60`, score
`20`, passo `20`; não ajusta parâmetros nem seleciona vencedores. A comparação
usa o fill proxy no fechamento da próxima barra, fee fixa de `10 bps` e slippage
de `5 bps`, com um cenário de stress adicional de half-spread `5 bps`. O baseline
de spread zero significa “não observado”, não “spread inexistente”.
Buy-and-Hold usa o mesmo close anterior à janela e as mesmas fees, half-spread
e slippage da estratégia, para comparar intervalos e custos simétricos.

Cada estratégia e variante é simulada separadamente com o capital inicial
completo; os resultados não são alocações simultâneas nem devem ser somados como
uma carteira. O capital de `10.000 USDT` no relatório serve apenas para
normalizar sizing e não representa BRL, nem o teto real de R$100. O relatório compacto contém
métricas, fingerprints, resultados por janela e `promotion_authorized=false`;
não contém curvas/trades completos. O backtest detalhado continua reproduzível
a partir do hash do dataset, fingerprint da estratégia e opções registrados.

O multi-ativo exploratório atualmente compara a shortlist fixa (`BTCUSDT`,
`ETHUSDT`, `BNBUSDT`, `SOLUSDT`, `XRPUSDT`) com candles diários públicos desde
2022-10-10. Não é o universo inteiro nem uma seleção point-in-time por liquidez;
há viés de seleção/sobrevivência. Cada janela inclui datas e retorno/drawdown de
Buy-and-Hold; a atribuição agrupa janelas como Up/Down/Flat pelo sinal do retorno
realizado no mesmo período. É análise ex-post, não label ou previsão disponível
antes da janela.

Com `--include-sensitivity true`, também emite uma grade fixa no código de 12
variantes por cenário de custo (12 baseline + as mesmas 12 no stress; 24
resultados por estudo): trend `(20,50)`, `(10,50)`, `(20,100)`; breakout `(20,10)`,
`(10,5)`, `(40,10)`; mean-reversion lookback/entry-z `(20,-1.8)`, `(10,-1.5)`,
`(30,-2.5)`; grid spacing/levels `(1.5%,5)`, `(1%,3)`, `(2%,5)`. Todos os
resultados são mantidos; não há seleção automática de vencedor nem promoção.
Essa grade pequena é sensibilidade exploratória, não otimização exaustiva nem
preregistro formal. Usá-la para escolher estratégia ainda requer holdout forward
independente não observado nesta campanha.
Quando o micro-cap está habilitado, cada uma das 12 variantes recebe o teto
integral em uma simulação separada; não é um plano de alocação simultânea dos
R$100.
O JSON apresenta cada variante de forma compacta, com fingerprint, métricas,
retornos por janela e `state_sync_warning`; curvas completas e trades individuais
não são serializados nesse relatório.

Para normalização, trend/breakout/mean-reversion usam todo o caixa quote
disponível na entrada; grid usa 10% do capital quote normalizado por nível e
limita inventário a 50%, calculado no primeiro fechamento. São premissas fixas
de pesquisa, não limites aprovados nem um modelo de sizing para os R$100.
O relatório expõe sinais rejeitados, não preenchidos ou parcialmente executados.
O estado interno da estratégia não recebe feedback do fill efetivo; qualquer
divergência marca `state_sync_warning`, e esse resultado não deve ser promovido
nem tratado como inventário exato.

O runner aceita uma simulação opcional do teto micro-cap somente com os cinco
inputs explícitos `--micro-cap-brl`, `--usdtbrl-price`,
`--minimum-order-notional-quote`, `--fx-snapshot-utc` e
`--exchange-rules-snapshot-utc`; os dois timestamps exigem sufixo UTC explícito
`Z` ou `+00:00`, devem estar a no máximo 15 minutos do relógio local e entre si
não podem diferir mais de cinco minutos. Isso é apenas freshness de research;
o CLI recusa um valor acima de R$100 nesta campanha, mas essa validação pertence
somente ao runner offline e não configura um limite no bot de execução.
qualquer ordem exigiria atualizar esses dados imediatamente antes. Exemplo dos snapshots públicos consultados em
2026-10-11: `100 BRL / 5.0205 BRL/USDT`, filtro
mínimo `5 USDT`. Isso equivale a cerca de `19.92 USDT` antes de conversão,
fees/deposit e spread; no máximo três posições simultâneas no mínimo teórico,
menos custos. Com a premissa de grid de 10% por nível, cada ordem seria cerca de
`1.99 USDT`, abaixo do filtro, e o simulador a rejeita. O parâmetro
`maximum_position_notional_at_buy_quote` limita o notional marcado ao comprar,
mas não limita valorização posterior do ativo e não é uma política ativa. A seção
do relatório é explicitamente `simulation_only` e `live_orders_authorized=false`;
os valores de FX e filtros são uma captura pontual e devem ser renovados para
uma futura janela de teste.
Os valores de FX/filtros são entradas fornecidas pelo operador; o runner valida
formato e freshness, mas não os busca nem prova sua origem. O campo
`snapshot_sources_verified_by_runner=false` impede interpretar o relatório como
atestação de que o endpoint retornou aqueles valores.

```bash
unset BINANCE_API_KEY BINANCE_API_SECRET
dotnet run --project tools/CryptoTrading.PublicOosRunner -c Release -- \
  --input /caminho/do/journal \
  --output /caminho/do/oos-report.json \
  --dataset-id btcusdt-public-1d \
  --version 2026-09-19 \
  --source binance-public-data \
  --symbol BTCUSDT
```

O runner não preregistra experimento, não acessa holdout e não autoriza
promoção. O relatório só pode entrar no `ExperimentEvidenceWriter` depois de
preregistration, budget, holdout e referências de rastreabilidade válidos.

Uma execução anterior (`public-oos-report/v1`, 2026-09-19) processou 179 barras
e validou somente o baseline Trend Following com a aproximação de fill então
vigente. Ela permanece como evidência histórica, mas foi superseded pelo schema
`public-oos-report/v3` (`strategy-comparison/v2`) para comparação entre
estratégias e avaliação opcional do teto micro-cap, e não deve ser misturada com
os novos resultados.

Para repetir a coleta pública e o walk-forward em um único gate descartável,
execute `scripts/test-public-oos-production.sh`. O harness usa um diretório
temporário, recusa credenciais presentes no ambiente, coleta no máximo 180
barras, valida o schema/fingerprint do relatório e exige
`promotion_authorized=false`; nenhum dataset ou relatório temporário é
versionado. A coleta pública é read-only, mas não é prova de fills reais,
regras da conta, custos da sua fee tier ou rentabilidade futura.
