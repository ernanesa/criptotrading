# Baseline de pesquisa — BTCUSDT e ETHUSDT, 1d (2026-10-29)

Resultados de pesquisa separados da auditoria de engenharia. Nada aqui autoriza promoção,
execução real ou aumento de risco.

## 1. Walk-forward sobre dados públicos

Execução offline do `CryptoTrading.PublicOosRunner` sobre dados públicos já presentes no repositório. **Sem credenciais** (o runner recusa rodar com chaves Binance definidas).

- Artefato: `artifacts/evidence/oos-btcusdt-1d-20261029.json` (sha256 `8e51e930…3357e1`)
- Dataset: BTCUSDT `1d`, 1.461 barras, 2022-10-10 → 2026-10-09, hash `83E115C4…79EBF6`
- 70 janelas walk-forward (treino 60 / teste 20 / passo 20)
- Custos: fee 0,1% + slippage 0,5 bp; estresse com +5 bp de meio-spread

| Estratégia | Retorno no período | Drawdown máx. | Sharpe* |
|---|---:|---:|---:|
| trend-following | **+147,6%** | 27,2% | 0,98 |
| breakout-momentum | +114,5% | 27,7% | 0,95 |
| mean-reversion | +52,4% | 27,5% | 0,70 |
| adaptive-grid | +8,1% | 3,9% | 0,75 |
| **buy-and-hold** | **+373,2%** | — | — |
| caixa | 0% | 0% | — |

\* anualização 365; Sharpe calculado pelo avaliador, não verificado independentemente.

**Exposição e risco (dados do relatório):**

| Estratégia | Tempo em mercado | Retorno | Drawdown | Retorno/DD | Turnover |
|---|---:|---:|---:|---:|---:|
| trend-following | 57,4% | +147,6% | 27,2% | **5,42** | 24,4× |
| breakout-momentum | 41,5% | +114,5% | 27,7% | 4,13 | 62,1× |
| mean-reversion | 25,3% | +52,4% | 27,5% | 1,91 | 60,6× |
| adaptive-grid | 6,0% | +8,1% | 3,9% | 2,08 | 2,5× |
| buy-and-hold (mesma janela) | 100% | +382% | 53,0% | **7,22** | — |

O drawdown do buy-and-hold foi calculado sobre a série contínua a partir do primeiro candle OOS (`index 60`). Verifiquei que o benchmark do relatório (+373%) corresponde a +377% nessa janela — está correto; uma comparação anterior minha que partia do índice 0 (+332%) estava errada.

**Interpretação honesta:**
- Nenhuma estratégia venceu buy-and-hold neste ativo e período — nem em retorno absoluto, nem em retorno/drawdown (5,42 vs 7,22).
- O trend-following compensa parte da desvantagem operando só 57% do tempo: concentra o retorno e corta o drawdown de 53% para 27%.
- Em janelas OOS, a estratégia de tendência venceu o buy-and-hold em 35 de 70 (moeda justa), com pior janela em −8,7% e melhor em +35,2%.
- O estresse de spread custou apenas ~1,3 ponto percentual ao trend. As taxas já estão deduzidas de cada estratégia (trend pagou 244 USDT sobre 10.000, 2,4% do capital, com turnover 24×); breakout e mean-reversion pagaram ~6% por turnover ~60×. Custo explica parte da diferença entre estratégias, não toda a diferença para buy-and-hold.
- **Ressalva de medição:** o retorno agregado das estratégias é o produto de 70 janelas, cada uma reiniciando em caixa (paga entrada e saída em cada fronteira); o buy-and-hold paga uma entrada e uma saída. Isso penaliza a estratégia, não a favorece.
- Isso **não** prova que a estratégia seja inútil (drawdown por janela é menor e há regimes distintos), nem que buy-and-hold seja preferível para o orçamento real. Prova que, com este conjunto único de parâmetros e um ativo, não há vantagem sobre a alternativa trivial.
- Limitações: um ativo, um conjunto de parâmetros, barras diárias, execução por fechamento atrasado, sem intrabar/fila/liquidez, sem dados reais de fills.

## 2. Sensibilidade de parâmetros (12 variantes)

Artefato: `artifacts/evidence/oos-btcusdt-1d-sensitivity-20261029.json` (sha256 `557D94FA…`). Grid fixo em código, sem seleção de vencedor; cada variante roda com capital integral e não são posições simultâneas.

| Variante | Retorno | DD | Ret/DD | Tempo em mercado |
|---|---:|---:|---:|---:|
| breakout lookback-40/confirm-10 | +107,1% | 19,3% | **5,55** | 34% |
| trend 20/50 (baseline) | +147,6% | 27,2% | 5,42 | 57% |
| trend 10/50 | +128,0% | 25,2% | 5,08 | 56% |
| trend 20/100 | +121,1% | 24,6% | 4,93 | 51% |
| breakout baseline 20/10 | +114,5% | 27,7% | 4,13 | 41% |
| mean-reversion baseline | +52,4% | 27,5% | 1,91 | 25% |
| mean-reversion lookback-30/z-2,5 | **−8,8%** | 26,7% | −0,33 | 12% |

**Leitura:**
- **Nenhuma das 12 variantes supera buy-and-hold** em ret/DD (7,22).
- A família de tendência é **estável** (4,93–5,42 em três parametrizações) — não há cliff, o que sugere ausência de overfitting grosseiro nessa família.
- **Reversão à média é frágil**: varia de +52% a −8,8% com mudança de janela/z-score. Um resultado positivo ali não sobrevive à perturbação.
- **Controle de múltiplos testes:** 12 variantes foram testadas. A diferença entre a melhor (5,55) e a segunda (5,42) está dentro do ruído esperado; não se deve declarar a breakout-40 como superior sem correção de multiplicidade (DSR ou equivalente) e sem novo holdout.


## 2.1 Cross-check em ETHUSDT (mesmo período)

ETH foi escolhido **a priori** (segundo par mais líquido), não por desempenho. Mesmos 1.461 dias, mesmos custos, mesmos parâmetros — nenhum ajuste.
Artefato: `artifacts/evidence/oos-ethusdt-1d-20261029.json` (sha256 `917AC6BF…`).

| Estratégia | BTC ret | BTC ret/DD | ETH ret | ETH ret/DD |
|---|---:|---:|---:|---:|
| trend-following | +147,6% | 5,42 | **+145,8%** | **4,75** |
| breakout-momentum | +114,5% | 4,13 | +7,5% | 0,16 |
| mean-reversion | +52,4% | 1,91 | +32,9% | 0,83 |
| adaptive-grid | +8,1% | 2,08 | +5,4% | 1,36 |
| **buy-and-hold** | +373,2% | **7,22** | +92,7% | **1,44** |

**O que muda:**
- Em ETH, o trend-following **supera buy-and-hold em retorno absoluto** (+145,8% vs +92,7%) e em retorno/drawdown (4,75 vs 1,44). Em BTC ele perdia em ambos.
- A conclusão anterior ("nenhuma estratégia vence buy-and-hold") era **específica de BTC**, não geral. BTC teve alta excepcional no período; ETH teve drawdown de 67,6%.
- Breakout e mean-reversion degradam muito em ETH — o resultado em BTC não se transfere.
- O sinal do trend é o único que aparece nos dois ativos com magnitude parecida (+147,6% e +145,8%).

**Não concluir:**
- Dois ativos ainda são amostra pequena; ambos são grandes caps correlacionados, no mesmo período.
- O resultado não é walk-forward de seleção: os parâmetros são fixos e não houve escolha de ativo com base em desempenho, mas também não há holdout independente.
- Isso justifica **mais testes**, não promoção. A contenção de submissões permanece.

## 2.2 Universo declarado pela aplicação (20 símbolos)

Não escolhi ativos por desempenho. Usei **o universo que o próprio Worker já declara** em `WorkerRuntimeOptions` (linha 300), baixado pela API pública de klines sem credenciais. Mesmo período, mesmos custos, mesmos parâmetros, sem ajuste.

Artefato: `artifacts/evidence/cross-asset-trend-1d-20261029.json` (sha256 `E3D40D1A…`).

| Símbolo | Trend | B&H | Venceu? | DD | Ret/DD | Tempo |
|---|---:|---:|:---:|---:|---:|---:|
| INJ | +472% | +314% | sim | 41% | 11,55 | 48% |
| SUI | +341% | +39% | sim | 49% | 6,97 | 40% |
| SOL | +276% | +698% | não | 50% | 5,52 | 48% |
| NEAR | +229% | +161% | sim | 61% | 3,74 | 41% |
| BNB | +188% | +153% | sim | 24% | 7,99 | 52% |
| OP | +154% | −89% | sim | 44% | 3,51 | 36% |
| BTC | +148% | +373% | não | 27% | 5,42 | 57% |
| ETH | +146% | +93% | sim | 31% | 4,75 | 51% |
| UNI | +144% | +15% | sim | 70% | 2,05 | 43% |
| AVAX | +141% | −25% | sim | 54% | 2,60 | 37% |
| DOGE | +131% | −15% | sim | 53% | 2,48 | 40% |
| LINK | +108% | +81% | sim | 41% | 2,62 | 46% |
| AAVE | +84% | +164% | não | 55% | 1,53 | 48% |
| SHIB | +65% | −43% | sim | 59% | 1,10 | 34% |
| ADA | +58% | −26% | sim | 58% | 1,00 | 36% |
| ARB | +55% | −79% | sim | 66% | 0,83 | 31% |
| XRP | +27% | +249% | não | 60% | 0,45 | 42% |
| APT | +23% | −79% | sim | 64% | 0,36 | 28% |
| DOT | −19% | −80% | sim | 72% | −0,26 | 29% |
| TIA | −70% | −96% | sim | 80% | −0,88 | 15% |

**Leitura:**
- Retorno **positivo em 18 de 20** ativos; **vence buy-and-hold em 16 de 20**.
- Mediana: trend **+136%** vs buy-and-hold **+27%**. A mediana favorece o trend; a média é puxada por SOL/BTC/XRP.
- Perde justamente nos ativos de alta excepcional (SOL, BTC, XRP) e em AAVE.
- **Dispersão alta**: drawdown de 23% a 80%; ret/DD de −0,88 a 11,55. Não é um edge uniforme.
- TIA (−70%) e DOT (−19%) mostram que o piso é ruim em ativos que caíram o período inteiro.

**Não concluir:**
- As 20 execuções usam capital integral cada; **não são posições simultâneas** e não podem ser somadas como carteira.
- Um único conjunto de parâmetros e um único período. Sem holdout independente.
- Viés de sobrevivência: o universo é o que a aplicação lista hoje; ativos que saíram de listagem não estão aqui.
- Portanto: evidência **a favor** de testar portfólio e timeframe, não de promover execução.

## 2.3 Carteira equiponderada

As 20 execuções da seção 2.2 usam capital integral cada uma; somá-las seria erro. Esta seção combina os retornos **por janela** emitidos pelo próprio motor — nenhuma reimplementação da estratégia.

Regra: peso igual entre os ativos disponíveis em cada janela OOS de 20 barras (universo cresce de 16 para 20 conforme ativos entram em listagem), rebalanceamento na fronteira de cada janela.

Artefato: `artifacts/evidence/equal-weight-portfolio-1d-20261029.json` (sha256 `504101A4…`, inclui cenários de custo).

| Carteira | Retorno | Drawdown | Ret/DD |
|---|---:|---:|---:|
| **trend-following equiponderado** | **+200,3%** | **20,2%** | **9,91** |
| buy-and-hold equiponderado | +88,7% | 74,4% | 1,19 |

- Em 35 das 70 janelas a carteira de trend perde do buy-and-hold naquele intervalo — metade do tempo. A vantagem vem do **controle de drawdown** ao longo do período, não de acertar sempre.
- Incluir TIA (pior ativo, −70% isolado) quase não alterou o resultado: a diversificação absorve o caso ruim.
- O drawdown cai de 74% para 20% porque os sinais não disparam juntos. É o efeito clássico de trend following multi-ativo.

### 2.3.1 Custo de rebalanceamento — limite conservador

Não modelei o custo de realocação; **limitei-o**. Apliquei uma fração do capital cobrada em cada uma das 70 fronteiras, assumindo o pior caso (liquidar tudo e recomprar tudo a cada janela).

| Cenário | Retorno | Drawdown | Ret/DD |
|---|---:|---:|---:|
| sem custo de rebalanceamento | +200,3% | 20,2% | 9,91 |
| **round-trip completo de 20 bp por janela** | **+161,0%** | 23,6% | **6,83** |
| estresse de 80 bp por janela (4×) | +71,1% | 34,9% | 2,04 |
| buy-and-hold equiponderado | +88,7% | 74,4% | 1,19 |

Leitura:
- Mesmo assumindo liquidar e recomprar **tudo** a cada 20 dias (20 bp por fronteira), a carteira segue à frente do buy-and-hold (+161% vs +88,7%) e com ret/DD 5,7× melhor.
- No estresse de 4× (80 bp por fronteira), o retorno absoluto cai para +71,1% — **abaixo** do buy-and-hold — mas o ret/DD (2,04) continua melhor.
- Conclusão robusta até aqui: a vantagem **ajustada ao risco** sobrevive ao limite; a vantagem **absoluta** não sobrevive ao estresse extremo.

**Limitações desta seção (importantes):**
- O custo é um **limite aplicado**, não um custo medido de execução. Um modelo real exigiria ordens, tamanhos e preços observados.
- Os retornos por janela já incluem entrada/saída de cada ativo; o limite cobre apenas a realocação entre eles.
- Viés de sobrevivência: universo é a lista atual.
- Um período, um conjunto de parâmetros, sem holdout independente.
- Sem curva diária de carteira, **não há Sharpe** — só retorno/drawdown.
- Equiponderar 20 ativos com capital integral implícito não é a mesma coisa que o orçamento real de R$ 100 / 20 USDT do bot.

**Conclusão:** há evidência consistente de que a família de tendência, diversificada, tem perfil risco/retorno muito superior a buy-and-hold neste período. Isso justifica validação em paper forward e um teste de custo de rebalanceamento — não promoção.

## 3. Como reproduzir

```bash
DOTNET_ROOT=/tmp/kilo/dotnet-rc3 PATH=/tmp/kilo/dotnet-rc3:$PATH \
dotnet run --project tools/CryptoTrading.PublicOosRunner/CryptoTrading.PublicOosRunner.csproj -c Release --no-build -- \
  --input artifacts/public-klines/btcusdt-1d-audit-20261010-complete \
  --output /tmp/oos.json \
  --dataset-id btcusdt-1d-audit-20261010 --version 2026.10.10 \
  --source "Binance Spot public market-data REST /api/v3/klines" \
  --symbol BTCUSDT --include-sensitivity true
```

O runner recusa execução se `BINANCE_API_KEY`/`BINANCE_API_SECRET` estiverem definidas.

## 4. Próximos experimentos (não executados)

1. **Avaliação em carteira** — as 20 execuções independentes não formam portfólio. Falta alocar entre ativos com risco controlado e medir o resultado combinado, incluindo correlação.
2. **Intraday** — todos os testes são 1d; o gravador público permite obter outras granularidades sem credenciais.
2. **Correção de multiplicidade** — 12 variantes foram testadas; declarar a melhor como superior exige DSR (ou equivalente) e um holdout não consultado.
3. **Sizing por volatilidade** — hipótese de melhorar retorno/drawdown sem mudar o sinal; exige alteração no avaliador e testes próprios.
