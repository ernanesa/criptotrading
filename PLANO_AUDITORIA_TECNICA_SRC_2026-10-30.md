# Auditoria técnica de `src`

**Base analisada:** `42ce739dd0b0d2c45dd03e990194c00b53c394a6`.
**Modalidade:** leitura estática, sem alterações, testes, builds ou chamadas à Binance. Foram excluídos testes, documentação, `bin`, `obj` e artefatos estáticos/build.

## Conclusão executiva

Os problemas mais importantes são:

1. **Possível supressão das saídas intrabar**, causada por dois controles de tempo usando o mesmo marcador.
2. **Treinamento de modelos no caminho de decisão**, combinado com probabilidades apresentadas como calibradas sem calibração empírica correspondente nesse fluxo.
3. **ADX implementado como DX**, afetando filtros de tendência e rompimento.
4. **Proteção de rate limit fragmentada**, sem orçamento preventivo compartilhado entre os clientes.
5. **Sinais de agressão que ignoram quantidade**, além de um indicador denominado VPIN que usa candles temporais.
6. **Inconsistência possível entre a projeção de ordens e seu histórico de lifecycle** após eventos atrasados.
7. Alocações e ordenações evitáveis no processamento de mensagens e profundidade.

**Não encontrei fundamento para recomendar a remoção de qualquer gate financeiro.** O código mantém `NO_TRADE` por padrão e a fronteira pré-trade bloqueia novas submissões enquanto a evidência necessária não está verificada.

---

## 1. Contexto operacional efetivamente encontrado

| Aspecto | Evidência em `src` |
|---|---|
| Mercado integrado | **Binance Spot**. Existem modelos de futuros no domínio, mas não encontrei chamadas que os integrem ao fluxo operacional auditado. |
| Padrão de execução | `PAPER`, `NoTrade = true`, autonomia desabilitada por padrão. |
| Símbolo/timeframe padrão | `BTCUSDT`, candles de **1 minuto**. |
| Dados contínuos | Assinaturas combinadas de `kline`, `bookTicker`, `trade` e `depth@100ms`. O parser também suporta `aggTrade`. |
| Decisão | Predominantemente baseada em barras fechadas, com tentativa de proteção intrabar. |
| Execução planejada | Táticas passivas e agressivas; não é exclusivamente maker. |
| Submissão atual | `BinancePreTradeValidatedOrderClient.SubmitAsync` termina com `PRETRADE_EVIDENCE_UNVERIFIED`. |

**Localizações:**
`src/CryptoTrading.Worker/WorkerRuntimeOptions.cs:22–24,54–84`
`src/CryptoTrading.Worker/AutonomyCycleHostedService.cs:276–285`
`src/CryptoTrading.Exchange.Binance/BinancePreTradeOrderClient.cs:37–61`

**Caracterização:** o código está mais próximo de um **sistema algorítmico orientado a candles com informações de microestrutura** do que de um motor HFT comprovado. Não há medições nesta auditoria que sustentem latência submilissegundo ou comportamento zero-allocation ponta a ponta.

---

## 2. Matriz de prioridade

O impacto considera o comportamento caso o respectivo caminho esteja habilitado; não representa perda financeira já observada.

| # | Achado | Natureza | Impacto | Implementação |
|---|---|---|---|---|
| 1 | Proteção intrabar pode sofrer starvation | Defeito | Alto | Moderada |
| 2 | Treinamento síncrono e calibração inadequada no fluxo ML | Defeito/modelagem | Alto | Complexa |
| 3 | ADX é DX; suavizadores não correspondem aos nomes | Defeito quantitativo | Alto | Moderada |
| 4 | Regime confunde dispersão de preços com volatilidade | Limitação de modelagem | Médio | Moderada |
| 5 | Agressão ignora quantidade; VPIN usa buckets temporais | Defeito semântico/modelagem | Alto | Moderada |
| 6 | Rate limit reativo e fragmentado | Resiliência | Alto | Complexa |
| 7 | Recepção WS acoplada ao consumidor e parsing alocante | Desempenho/resiliência | Alto | Complexa |
| 8 | Consultas top-N reordenam o livro repetidamente | Otimização | Médio | Moderada |
| 9 | Lifecycle pode registrar atualização rejeitada pela projeção | Consistência/auditoria | Alto | Moderada |
| 10 | Paper não comprova fills maker ou prioridade de fila | Limitação de simulação | Alto para pesquisa maker | Complexa |
| 11 | Arbitragem triangular usa lado incompatível na segunda perna | Defeito em modelo não integrado | Médio | Fácil |
| 12 | Perdas do logger não são contabilizadas corretamente; redação ausente | Observabilidade/segurança | Médio | Fácil |
| 13 | API devolve mensagem interna de exceção | Segurança | Médio | Fácil |

**Sobre os snippets:** são substituições locais ou contratos propostos para os trechos indicados, não um patch aplicado. Não foram compilados nesta auditoria. Onde há mudança de contrato, ela está explicitada.

---

# A. Gestão de risco e execução

## 1. Saída intrabar pode ser continuamente adiada

### Localização

`src/CryptoTrading.Worker/AutonomyCycleHostedService.cs`

- `ConsumeMarketDataStreamAsync`: **384–391**.
- Caminho intrabar de `RunOnceAsync`: **613–668**, especialmente **640–641**.

### Diagnóstico

O produtor de sinais atualiza `lastIntrabarTriggerUtc` a cada **2 segundos** enquanto o stop/take-profit/chandelier estiver acionado.

O consumidor exige que tenham passado **15 segundos desde esse mesmo marcador**.

Com posição conhecida pelo processo, candle já processado e bookTicker frequente, o produtor renova o marcador antes de o consumidor atingir os 15 segundos. Isso pode suprimir a decisão intrabar até surgir um candle novo ou mudar a condição.

Não significa que toda saída está bloqueada: o caminho normal de candle novo continua existindo.

### Antes

```csharp
// Produtor:
lastIntrabarTriggerUtc[tickerEvent.Symbol] = nowUtc;
barCloseSignalChannel.Writer.TryWrite(nowUtc);

// Consumidor:
var lastTrigger = lastIntrabarTriggerUtc.GetValueOrDefault(
    symbol, DateTimeOffset.MinValue);

if ((now - lastTrigger) >= TimeSpan.FromSeconds(15))
{
    // Avaliação intrabar.
}
```

### Solução técnica

Separar:

- **última notificação**, para coalescer wakeups;
- **última decisão intrabar duravelmente confirmada**, para idempotência/cooldown.

O recebimento de uma cotação não deve parecer uma decisão já executada.

### Depois — trecho do consumidor

Usar o checkpoint intrabar já existente como referência da decisão, sem depender do marcador de notificações:

```csharp
var intrabarScope = $"{checkpointScope}:intrabar";
var lastCommitted = await decisionCommitStore
    .GetLastProcessedCloseAsync(intrabarScope, cancellationToken)
    .ConfigureAwait(false);

if (lastCommitted is null ||
    intrabarCloseUtc >= lastCommitted.Value.AddSeconds(15))
{
    checkpointScope = intrabarScope;

    closed.Add(new ClosedBarEnvelope(
        closed.Count,
        new CloseBar(symbolObj, intrabarCloseUtc, intrabarTicker.BidPrice),
        now >= closed[^1].AvailableAtUtc
            ? now
            : closed[^1].AvailableAtUtc));

    isIntrabarProtectiveTrigger = true;
}
```

No produtor, renomear o marcador para algo como `lastIntrabarWakeupUtc`. Preservar os gates de reconciliação, risco, conta e submissão.

**Validação recomendada:** relógio controlado, bookTicker a cada 100 ms, stop persistente e candle sem mudança. A primeira decisão elegível não pode ser adiada pela chegada de novas cotações.

---

## 2. Treinamento no caminho de decisão e probabilidade sem calibração correspondente

### Localização

`src/CryptoTrading.Backtesting/Autonomy/MlNetSystem1DecisionEngine.cs`

- `Evaluate`: **98–158**.
- `EvaluateCalibratedMetaLabeler`: **231–310**.
- `BuildPointInTimeTrainingSet`: **325–405**.

Consumidores relevantes:

`src/CryptoTrading.Backtesting/Autonomy/AdaptiveDecisionCycle.cs:193–198,783,1110–1122`

### Diagnóstico

Em um cache miss, o fluxo:

1. Cria `MLContext`.
2. Executa os detectores temporais.
3. Treina SDCA.
4. Treina GAM.
5. Cria dois `PredictionEngine`.
6. Faz inferência.

A chave do cache inclui OBI e outros valores variáveis. Portanto, mesmo sem mudar o último candle, mudanças de contexto podem provocar novo treinamento.

Há também problemas de interpretação estatística:

- Com menos de 20 barras, `regime.Confidence` vira `MlWinProbability`.
- O treinamento inclui exemplos sintéticos de lucro/prejuízo.
- Os exemplos históricos recebem `OrderBookImbalance = 0`, enquanto a amostra atual pode usar OBI real.
- O fluxo aplica `PlattParameters.Default`, não parâmetros ajustados pelo calibrador a previsões temporais fora da amostra.

Essa saída participa de filtros e de um multiplicador chamado `kellyMultiplier`. **Os limites de risco continuam existindo**, mas a evidência probabilística usada para escolher a exposição é frágil.

### Antes

```csharp
var sdcaModel = featurePipeline
    .Append(mlContext.BinaryClassification.Trainers
        .SdcaLogisticRegression(sdcaOptions))
    .Fit(trainData);

var gamModel = featurePipeline
    .Append(mlContext.BinaryClassification.Trainers.Gam(gamOptions))
    .Fit(trainData);

var plattProb = PlattParameters.Default.Transform(ensembleScore);
```

### Solução técnica

Separar os planos:

- **Slow path:** treinamento, validação temporal, calibração e registro do artefato.
- **Decision path:** inferência sobre artefato imutável e versionado.
- Calibrar usando previsões realmente fora da amostra, com rótulos já maturados.
- Diferenciar `HeuristicConfidence` de `CalibratedWinProbability`.
- Ausência de probabilidade válida deve bloquear **novas entradas dependentes desse modelo**, não eliminar uma saída protetiva independente.

### Depois — gate local de validade da probabilidade

```csharp
private static bool TryCalibratedProbability(
    decimal rawScore,
    PlattParameters fittedCalibration,
    int outOfSampleCount,
    int minimumSamples,
    DateTimeOffset labelsAvailableThroughUtc,
    DateTimeOffset decisionAtUtc,
    bool artifactApproved,
    out decimal probability)
{
    probability = 0m;

    if (!artifactApproved ||
        outOfSampleCount < minimumSamples ||
        labelsAvailableThroughUtc >= decisionAtUtc)
    {
        return false;
    }

    probability = fittedCalibration.Transform(rawScore);
    return probability is >= 0m and <= 1m;
}
```

Esse gate exige metadados novos no artefato; não substitui o processo de calibração. O mínimo de amostras deve vir de política validada, não de um número arbitrário.

**Impacto esperado:** retirar treinamento do ciclo reduz trabalho variável, alocações e risco de atrasar decisões. A magnitude precisa ser medida.

---

# B. Modelagem quantitativa e sinais

## 3. ADX implementado como DX; ATR/RSI não usam a suavização anunciada

### Localização

`src/CryptoTrading.Backtesting/Autonomy/MultiStrategyDecisionOrchestrator.cs`

- `TechnicalIndicatorCalculator.Evaluate`: **569–621**.
- Uso dos limiares de ADX: **183,221,257**.

### Diagnóstico

O código calcula:

```text
100 × |+DI − −DI| / (+DI + −DI)
```

Isso é **DX**. ADX é uma média suavizada dos valores de DX.

ATR, ganhos e perdas de RSI usam médias simples da janela, embora os comentários anunciem Wilder. Média simples não é necessariamente um indicador inválido, mas não é equivalente ao indicador declarado.

Usar limiares como ADX 20/25 sobre DX pode aceitar uma força direcional transitória como tendência sustentada.

### Antes

```csharp
var atr = trSum / effectivePeriod;

var adx = diSum > 0m
    ? Math.Abs(plusDi - minusDi) / diSum * 100m
    : 0m;

var avgGain = gainSum / effectivePeriod;
var avgLoss = lossSum / effectivePeriod;
```

### Solução técnica

Manter estado incremental por símbolo/timeframe, com seed e warmup explícitos:

\[
RMA_t = RMA_{t-1} + \frac{x_t-RMA_{t-1}}{n}
\]

### Depois — atualização após o seed

```csharp
static decimal WilderUpdate(
    decimal previous,
    decimal observation,
    int period)
{
    ArgumentOutOfRangeException.ThrowIfLessThan(period, 2);
    return previous + ((observation - previous) / period);
}

// Após inicializar cada série pela média do período:
atr = WilderUpdate(atr, trueRange, period);
avgGain = WilderUpdate(avgGain, gain, period);
avgLoss = WilderUpdate(avgLoss, loss, period);

var dx = diSum > 0m
    ? 100m * Math.Abs(plusDi - minusDi) / diSum
    : 0m;

adx = WilderUpdate(adx, dx, period);
```

Também é necessário suavizar DM/TR para DI e inicializar o primeiro ADX pela média dos DX iniciais. Não substituir somente a última linha e chamar o resultado de Wilder completo.

---

## 4. Classificador de regime confunde dispersão de preços com volatilidade

### Localização

`src/CryptoTrading.Domain/Regimes/MarketRegimeClassifier.cs`

`Classify`: **75–87,114–123**.

### Diagnóstico

`VolatilityRate` é o coeficiente de variação dos **níveis de preço**:

\[
CV = \frac{\sigma(P)}{\overline P}
\]

Isso cresce com uma tendência suave, mesmo quando a volatilidade dos retornos é baixa.

Contraexemplo calculado nesta auditoria: preços `100,101,...,119`:

- CV dos preços: **5,4028%**, acima do limite padrão de 5%;
- desvio dos retornos por barra: aproximadamente **0,0476%**.

Uma sequência monotonicamente crescente pode ser classificada como `HighVolatilityChop`, que proíbe nova exposição.

### Antes

```csharp
var variance = varianceSum / (n - 1);
var stdDev = (decimal)Math.Sqrt((double)variance);
var volatilityRate = stdDev / mean;
```

### Solução técnica

Separar:

- volatilidade dos retornos;
- direção/força da tendência;
- eficiência do deslocamento;
- liquidez/freshness.

Um complemento simples é o **efficiency ratio**:

\[
ER=\frac{|P_T-P_0|}{\sum_t |P_t-P_{t-1}|}
\]

### Depois — métricas sem confundir as unidades

```csharp
static (decimal ReturnVariance, decimal Efficiency) RegimeMetrics(
    ReadOnlySpan<decimal> prices)
{
    if (prices.Length < 3)
        throw new ArgumentException("At least three prices are required.");

    decimal mean = 0m, m2 = 0m, path = 0m;
    var count = 0;

    for (var i = 1; i < prices.Length; i++)
    {
        if (prices[i - 1] <= 0m || prices[i] <= 0m)
            throw new ArgumentOutOfRangeException(nameof(prices));

        var r = prices[i] / prices[i - 1] - 1m;
        var delta = r - mean;
        mean += delta / ++count;
        m2 += delta * (r - mean);
        path += Math.Abs(prices[i] - prices[i - 1]);
    }

    return (
        m2 / (count - 1),
        path > 0m ? Math.Abs(prices[^1] - prices[0]) / path : 0m);
}
```

Os limiares precisam ser recalibrados por timeframe. **Não reutilizar 5% de CV como limiar de variância de retornos.**

---

## 5. Agressão desconsidera quantidade; “VPIN” não é volume-synchronized

### Localização

- `src/CryptoTrading.Worker/AutonomyCycleHostedService.cs:396–410`
- `src/CryptoTrading.Backtesting/Autonomy/MultiStrategyDecisionOrchestrator.cs:646–669`

### Diagnóstico

No stream, `tradeAggressionDeltaBySymbol` usa somente o sinal de cada negócio.

Assim, uma compra muito pequena e uma compra grande têm o mesmo efeito. Uma sequência de pequenos prints pode dominar um negócio de maior quantidade na direção oposta.

No indicador chamado VPIN, os buckets são candles temporais, não buckets de volume equivalente. O resultado é uma medida de desequilíbrio agregado por candle, **não o VPIN anunciado**.

**Distinção importante:** calcular delta com `2 × TakerBuyBaseVolume − Volume` é válido como delta agregado por candle. O problema não é essa identidade, mas misturar sua interpretação com toxicidade intrabar ou CVD cumulativo ancorado.

### Antes

```csharp
var signedSign = tradeEvent.IsBuyerMaker ? -1m : 1m;

tradeAggressionDeltaBySymbol.AddOrUpdate(
    tradeEvent.Symbol,
    signedSign * 0.25m,
    (_, previous) => previous * 0.85m + signedSign * 0.15m);
```

### Solução técnica

Acumular volume assinado e volume total na mesma janela. Para VPIN, implementar buckets de volume fixo ou renomear o indicador atual.

### Depois — estado incremental ponderado por quantidade

```csharp
readonly record struct AggressionState(decimal Signed, decimal Total)
{
    public decimal Imbalance => Total > 0m ? Signed / Total : 0m;

    public AggressionState Add(
        decimal quantity,
        bool isBuyerMaker,
        decimal decay)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        if (decay is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(decay));

        var signed = isBuyerMaker ? -quantity : quantity;
        return new(
            Signed * decay + signed,
            Total * decay + quantity);
    }
}
```

O decaimento acima é por evento. Para comparar símbolos com intensidades diferentes, preferir janela temporal ou volume fixo explicitamente definido.

### Sinais adicionais úteis, sem promessa de superioridade

O código **já possui OBI, OFI e weighted mid/microprice**. Antes de adicionar indicadores, eu validaria:

- persistência do OBI, não apenas um snapshot;
- resposta do mid-price ao volume agressor;
- divergência entre delta e deslocamento do preço;
- replenishment no nível após agressão;
- VWAP ancorada em evento definido, com bandas;
- markout após fills em horizontes compatíveis com a estratégia.

Exemplo de exaustão: forte volume comprador, mas deslocamento pequeno do mid e reposição persistente no ask. Isso é uma **hipótese de absorção**, não confirmação isolada de reversão.

---

# C. Rede, memória e processamento de mercado

## 6. Rate limit não é coordenado entre todos os clientes

### Localização

- `src/CryptoTrading.Exchange.Binance/BinanceRequestResilience.cs:180–247`
- `src/CryptoTrading.Exchange.Binance/BinanceSpotAccountClient.cs:43–51,306–350`
- `src/CryptoTrading.Exchange.Binance/BinanceSpotMarketClient.cs:102,168,227`
- `src/CryptoTrading.Exchange.Binance/BinanceSpotOrderClient.cs:902–905`
- `src/CryptoTrading.Exchange.Binance/ServiceCollectionExtensions.cs:60,94`

### Diagnóstico

O gate:

- atua depois de 429/418;
- não usa os contadores de consumo para admissão preventiva;
- pode ser criado por instância do cliente de conta;
- não protege uniformemente os clientes públicos e de ordens.

Embora os cabeçalhos sejam parseados, não há um orçamento compartilhado de IP weight, raw requests e ordens cobrindo todos esses caminhos.

O risco existe também em operações somente leitura: múltiplos símbolos e serviços podem consumir a mesma quota do IP.

### Antes

```csharp
this.rateLimitGate = rateLimitGate ?? new BinanceRateLimitGate();

// Cliente público:
using var response = await httpClient.GetAsync(uri, cancellationToken);
```

### Solução técnica

1. Compartilhar o bloqueio reativo no processo.
2. Colocar admissão preventiva em um boundary comum.
3. Reservar custo antes de enviar.
4. Atualizar orçamento com cabeçalhos e metadados da exchange.
5. Coordenar processos que compartilham o IP.
6. Preservar a regra de não repetir submissão ambígua automaticamente.

### Depois — endurecimento mínimo, ainda reativo

```csharp
// Registro único por processo:
services.AddSingleton<BinanceRateLimitGate>();

// Em cada boundary HTTP, com gate injetado:
await gate.WaitAsync(timeProvider, cancellationToken)
    .ConfigureAwait(false);

using var response = await httpClient.SendAsync(
    request,
    HttpCompletionOption.ResponseHeadersRead,
    cancellationToken).ConfigureAwait(false);

gate.ObserveResponse(response, timeProvider.GetUtcNow());
```

Esse trecho **não resolve sozinho o orçamento preventivo nem a coordenação entre processos**.

**Ponto positivo:** o projeto já utiliza `AddHttpClient`; não há base para afirmar que cria uma nova conexão TCP por requisição. Oportunidades de `SocketsHttpHandler` devem ser medidas, não tratadas como correção automática.

---

## 7. Recepção WS depende do ritmo do consumidor; há cópias e strings por mensagem

### Localização

- `src/CryptoTrading.Exchange.Binance/BinanceSpotMarketDataClient.cs:78–131,202–233`
- `src/CryptoTrading.Exchange.Binance/BinanceUserDataStream.cs:407–434`
- `src/CryptoTrading.Exchange.Binance/BinanceMarketDataContracts.cs:158–165,290–318`

### Diagnóstico

Os transportes:

- usam buffer por conexão;
- acumulam mensagens em `MemoryStream`;
- executam `ToArray()` por mensagem;
- suspendem a leitura quando o consumidor está processando o item produzido.

O parser usa DOM, strings para números e objetos por nível de profundidade.

Além disso, não há configuração explícita de `KeepAliveTimeout`. O comportamento padrão não equivale a detecção rápida de peer sem resposta.

**Não é correto dizer “não existe keep-alive”:** o `ClientWebSocket` já tem comportamento padrão. A lacuna é o timeout de responsividade e o desacoplamento da recepção.

### Antes

```csharp
yield return message.ToArray();

decimal.Parse(
    element.GetProperty(propertyName).GetString()!,
    CultureInfo.InvariantCulture);
```

### Solução técnica

- Reader dedicado, mantendo leitura pendente.
- Fila bounded entre recepção e processamento.
- Nunca descartar silenciosamente deltas L2.
- Ao saturar a fila: invalidar o estado, bloquear seu uso e ressincronizar.
- Ownership explícito para buffers pooled.
- Parsing UTF-8 direto no hot path.
- Tipos de valor para níveis/eventos quando o perfil justificar.

### Depois — parsing decimal sem string intermediária

Aplicável a um reader construído sobre mensagem contígua:

```csharp
static decimal ReadDecimalToken(ref Utf8JsonReader reader)
{
    if (reader.TokenType == JsonTokenType.Number)
        return reader.GetDecimal();

    if (reader.TokenType == JsonTokenType.String &&
        !reader.HasValueSequence &&
        !reader.ValueIsEscaped &&
        System.Buffers.Text.Utf8Parser.TryParse(
            reader.ValueSpan,
            out decimal value,
            out var consumed) &&
        consumed == reader.ValueSpan.Length)
    {
        return value;
    }

    throw new JsonException("Invalid decimal token.");
}
```

Configuração de responsividade, antes da conexão:

```csharp
client.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
client.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
```

Os tempos são exemplos de configuração, não valores validados para produção. **Manter um `ReceiveAsync` pendente é essencial para processar PONGs.**

Não recomendo substituir `ToArray()` por memória emprestada sem alterar o contrato de ownership: isso pode introduzir corrupção por reutilização do buffer.

---

## 8. Métricas top-N reordenam o livro repetidamente

### Localização

`src/CryptoTrading.Exchange.Binance/BinanceLocalOrderBook.cs`

- `Bids`/`Asks`: **100–108**.
- `CalculateTopDepthImbalance`: **123–124**.
- `CalculateTopDepthLiquidityNotional`: **141–142**.
- `Prune`: **342–354**.

### Diagnóstico

O livro usa dicionários, mas as consultas ordenam novamente os níveis. Para obter imbalance e liquidez, as mesmas laterais passam por ordenações separadas.

Isso adiciona CPU e alocações enquanto o caller mantém `lock(localBook)`, ampliando a seção crítica.

### Antes

```csharp
var topBidQty = bids
    .OrderByDescending(static p => p.Key)
    .Take(levels)
    .Sum(static p => p.Value);
```

### Solução técnica

Para top-5 fixo, uma seleção limitada em stack evita ordenação e heap. Para maior escala, avaliar estrutura ordenada incremental e snapshot agregado por versão do livro.

### Depois — quantidade e notional top-5 em uma passagem

```csharp
readonly record struct DepthLevel(decimal Price, decimal Quantity);

static (decimal Quantity, decimal Notional) TopFive(
    Dictionary<decimal, decimal> side,
    bool isBid)
{
    Span<DepthLevel> top = stackalloc DepthLevel[5];
    top.Clear();

    foreach (var pair in side)
    {
        var position = 0;
        while (position < top.Length &&
               top[position].Price > 0m &&
               (isBid
                   ? top[position].Price >= pair.Key
                   : top[position].Price <= pair.Key))
            position++;

        if (position == top.Length)
            continue;

        for (var j = top.Length - 1; j > position; j--)
            top[j] = top[j - 1];

        top[position] = new(pair.Key, pair.Value);
    }

    decimal quantity = 0m, notional = 0m;
    foreach (var level in top)
    {
        quantity += level.Quantity;
        notional += level.Price * level.Quantity;
    }

    return (quantity, notional);
}
```

Para `N=5`, o trabalho fica limitado a aproximadamente `5 × níveis`, sem alocações nesse helper. O ganho real precisa ser comparado por benchmark.

---

# D. Consistência, simulação e segurança

## 9. Lifecycle pode incorporar evento rejeitado pela projeção de ordens

### Localização

`src/CryptoTrading.Persistence/PostgresTradingStore.cs`

`ApplyAccountStreamExecutionReportAsync`: **1945–2063**, especialmente **1999–2045**.

### Diagnóstico

O upsert da projeção valida timestamp e quantidade acumulada. Entretanto, o código tenta inserir lifecycle mesmo quando:

```csharp
updated == false
```

O insert consulta o último status, mas não exige que o evento recebido seja temporalmente posterior.

Exemplo:

1. `PartiallyFilled` em `t2` já foi aplicado.
2. Chega um `NEW` antigo, de `t1`.
3. A projeção rejeita a atualização antiga.
4. O lifecycle pode registrar uma transição para `Acknowledged` com timestamp anterior.

O estado materializado pode continuar correto, enquanto o histórico passa a conter uma transição incompatível com ele.

### Antes

```csharp
var updated = await command.ExecuteNonQueryAsync(cancellationToken) > 0;

if (mappedLifecycleStatus is not null)
{
    // INSERT de lifecycle independentemente de updated.
}
```

### Solução técnica

Aplicar projeção, lifecycle e efeitos associados em uma transação, com serialização por ordem. Persistir o evento bruto idempotente separadamente da decisão de aplicá-lo à projeção.

### Depois — estrutura transacional proposta

```csharp
await using var transaction = await connection
    .BeginTransactionAsync(cancellationToken)
    .ConfigureAwait(false);

command.Transaction = transaction;

var updated = await command
    .ExecuteNonQueryAsync(cancellationToken)
    .ConfigureAwait(false) > 0;

if (updated && mappedLifecycleStatus is not null)
{
    lifecycleCommand.Transaction = transaction;
    await lifecycleCommand.ExecuteNonQueryAsync(cancellationToken)
        .ConfigureAwait(false);
}

await transaction.CommitAsync(cancellationToken)
    .ConfigureAwait(false);
```

É um recorte estrutural: o método deve construir os comandos dentro dessa transação e incluir a liberação de reserva correspondente. **Adicionar somente `if (updated)` sem atomicidade pode prejudicar recuperação após falha parcial.**

Empates de timestamp precisam de identidade/ordenação estável; `Guid` aleatório não substitui sequência de origem.

---

## 10. Paper não permite concluir captura maker ou posição na fila

### Localização

`src/CryptoTrading.Exchange.Paper/PaperExchange.cs`

`ApplyCore`/`CanFill`: **313–365,420–426**.

Complementos:

- `src/CryptoTrading.Domain/MarketMaking/OrderBookQueueSimulator.cs:32–41`
- `src/CryptoTrading.Shadow/PaperShadowSession.cs:75–81`

### Diagnóstico

O Paper:

- prioriza ordens por `ClientOrderId` lexical;
- preenche limites quando o preço oposto cruza;
- sem `AvailableQuantity`, pode preencher todo o restante;
- não observa fila anterior, prints por preço ou cancelamentos.

Isso é uma política de simulação explícita, **não uma implementação de matching maker**. Pode tanto deixar de representar fills passivos quanto conceder quantidade não sustentada por evidência.

A sessão Shadow que usa bid=ask=close também não valida spread real.

### Antes

```csharp
var quantity = remainingLiquidity is { } available
    ? new Quantity(Math.Min(state.Remaining.Value, available))
    : state.Remaining;
```

### Solução técnica

Separar dois modos:

- simulação simplificada de candles;
- replay de execução com liquidez obrigatória, latência, fills parciais e fila.

### Depois — endurecimento conservador do replay de execução

```csharp
if (remainingLiquidity is not { } available || available <= 0m)
{
    // Ausência de evidência de liquidez não concede fill.
    continue;
}

var quantity = new Quantity(
    Math.Min(state.Remaining.Value, available));
```

Esse endurecimento não implementa uma fila real. Um modelo maker ainda precisa consumir negócios executados no preço, considerar quantidade à frente e registrar incerteza.

**Não usar o Paper atual como evidência de rentabilidade de market making.**

---

## 11. Arbitragem triangular usa bid onde a perna exige ask

### Localização

`src/CryptoTrading.Domain/Arbitrage/TriangularArbitrageDetector.cs`

`EvaluateCycle`: **25–44**.

### Diagnóstico

O ciclo declarado é:

```text
USDT → BTC → ETH → USDT
```

Na segunda perna, compra-se ETH com BTC. Para `ETHBTC`, é necessário o **ask**.

O parâmetro chama-se `ethToBtcBidPrice`, é descrito como preço de venda de ETH, mas a fórmula divide por ele para comprar ETH.

Se o caller respeitar esse nome e passar bid, a oportunidade será superestimada.

### Antes

```csharp
var grossMultiplier =
    (1m / usdtToBtcAskPrice) *
    (1m / ethToBtcBidPrice) *
    ethToUsdtBidPrice;
```

### Depois

```csharp
static decimal GrossTriangularMultiplier(
    decimal btcUsdtAsk,
    decimal ethBtcAsk,
    decimal ethUsdtBid)
{
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(btcUsdtAsk);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ethBtcAsk);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ethUsdtBid);

    return ethUsdtBid / (btcUsdtAsk * ethBtcAsk);
}
```

Mesmo corrigida, essa fórmula continua sendo de **cotação teórica**: falta simular quantidade, profundidade, filtros, taxas por ativo e risco de execução parcial.

Não encontrei integração desse detector ao Worker; portanto, não atribuo perda operacional atual a esse ponto.

---

## 12. Logger perde registros sem contabilizar corretamente e não aplica redação

### Localização

`src/CryptoTrading.Observability/BoundedJsonLineFileLoggerProvider.cs`

- Canal: **52–57**.
- Contador: **66–74**.
- Serialização de mensagens/exceções: **212–222**.

### Diagnóstico

Com `BoundedChannelFullMode.DropWrite`, `TryWrite` pode retornar sucesso mesmo quando o item é descartado pela política do canal. O contador atual só incrementa quando `TryWrite` retorna `false`.

Logo, `DroppedRecords` pode não refletir perdas por saturação.

Além disso, mensagem formatada e mensagem da exceção são persistidas sem usar o `SecretRedactor` existente.

O próprio arquivo distingue corretamente esse logger da auditoria financeira durável; não estou tratando perda de diagnóstico como perda automática do ledger.

### Antes

```csharp
FullMode = BoundedChannelFullMode.DropWrite;

// ...
if (channel.Writer.TryWrite(record))
    return true;

Interlocked.Increment(ref droppedRecords);
```

### Depois — contabilização de descarte

```csharp
channel = Channel.CreateBounded<LogRecord>(
    new BoundedChannelOptions(DefaultCapacity)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    },
    _ => Interlocked.Increment(ref droppedRecords));
```

O incremento para canal fechado pode permanecer em `TryWrite`: é uma situação diferente do descarte por overflow.

### Depois — redação na fronteira

```csharp
var safeMessage = SecretRedactor.RedactEmbeddedSecrets(
    formatter(state, exception));

var safeExceptionMessage = exception?.Message is { } text
    ? SecretRedactor.RedactEmbeddedSecrets(text)
    : null;
```

Usar esses valores no `LogRecord`. Para erros de exchange/autenticação, preferir tipo, código permitido e correlação; regex não garante remoção de todo segredo arbitrário.

---

## 13. Endpoint devolve mensagem interna de exceção

### Localização

`src/CryptoTrading.Api/Program.cs:605–608`

Endpoint `/portfolio/performance`.

### Diagnóstico

A resposta HTTP inclui `ex.Message` do armazenamento. Dependendo da exceção, isso pode expor nomes de objetos, detalhes SQL ou informações internas do provider.

O controle de acesso local reduz o alcance, mas não justifica devolver detalhes internos.

### Antes

```csharp
catch (Exception ex)
{
    return ApiProblem.ServiceUnavailable(
        "PORTFOLIO_HISTORY_UNAVAILABLE",
        $"Durable portfolio snapshots could not be read: {ex.Message}");
}
```

### Depois

```csharp
catch (Exception ex)
{
    ApiLog.UnhandledRequestFailure(
        app.Logger,
        "GET",
        "/portfolio/performance",
        Activity.Current?.TraceId.ToString() ?? string.Empty,
        ex.GetType().Name);

    return ApiProblem.ServiceUnavailable(
        "PORTFOLIO_HISTORY_UNAVAILABLE",
        "Durable portfolio snapshots could not be read.");
}
```

---

## 3. Avaliação da proteção de capital e da execução

### Controles existentes que devem ser preservados

Nos fluxos auditados encontrei:

- `NO_TRADE` e opt-ins separados para Testnet;
- SafeMode/KillSwitch;
- validação de conta, regras e freshness;
- reconciliação;
- fencing e controle durável de submissão;
- identificação determinística de ordens;
- tratamento de resultados ambíguos;
- reservas de capital e taxas;
- limites de exposição, perda e slippage;
- stops e saídas adaptadas à volatilidade.

**Não há evidência estática suficiente para declarar esses controles integralmente validados em integração.**

### Sizing: melhoria recomendada

Em `AdaptiveDecisionCycle.cs:1110–1142`, o chamado `kellyMultiplier` é uma tabela heurística de multiplicadores, não a fórmula de Kelly estimada com distribuição de resultados.

Antes de sofisticá-lo, recomendo um orçamento explícito de perda por operação:

\[
Q \leq \frac{R_{\text{operação}}}
{|P_{\text{entrada}}-P_{\text{stop}}|+
C_{\text{taxas/unidade}}+
C_{\text{slippage/unidade}}}
\]

Depois aplicar o mínimo entre esse limite, saldo livre, exposição autorizada, liquidez e filtros. Isso deve ser uma política versionada, não um ajuste autônomo de LLM.

### Maker, taker e prioridade de fila

`ExecutionPlanner.cs:331–389` já distingue preço passivo/agressivo e melhoria por tick. Entretanto:

- preço passivo no snapshot não garante execução maker após latência;
- melhorar um tick altera o trade-off entre fila, spread capturado e seleção adversa;
- cancelar/recolocar perde prioridade;
- “iceberg” no nome de uma tática não comprova slicing ou ordem iceberg efetiva.

A escolha deveria comparar, por quantidade:

\[
EV_{\text{maker}} =
P(\text{fill})\times
(\text{edge}-\text{fee}-\text{markout adverso})
-\text{custo de oportunidade}
\]

contra o custo taker, incluindo spread, impacto e taxa. Não basta comparar a diferença nominal entre maker fee e taker fee.

A adoção futura de post-only/`LIMIT_MAKER` exige contrato de execução revisado e autorização própria; **não é recomendação para habilitar mutações agora**.

---

## 4. Bibliotecas e padrões recomendados

A prioridade é aproveitar o runtime já utilizado, sem adicionar dependências por moda.

| Necessidade | Recomendação | Aplicação |
|---|---|---|
| Parsing quente | `System.Text.Json.Utf8JsonReader` + `Utf8Parser` | Reduzir DOM e strings de números |
| DTOs fora do parser manual | `JsonSerializerContext` source-generated | Serialização explícita e preparação para AOT |
| Buffers | `ArrayPool<byte>` / `MemoryPool<byte>` | Ownership explícito; retorno somente após consumo |
| Recepção/processamento | `System.Threading.Channels` bounded | Capacidade, atraso e overflow observáveis |
| REST | Manter `IHttpClientFactory` | Centralizar transporte, orçamento e telemetria |
| WS | Manter inicialmente `ClientWebSocket` | Corrigir leitura, watchdog e ressincronização antes de trocar cliente |
| Indicadores | Estado incremental local | ATR/ADX/RSI/fluxo com unidade, seed e warmup definidos |
| ML | Treino separado da inferência | Artefato imutável, versionado e calibrado temporalmente |

Não recomendo substituir `decimal` por `float`/`double` em execução, taxas, saldo ou P&L. Conversões existentes para ferramentas estatísticas precisam continuar restritas à camada analítica.

Os projetos em `src` declaram `net11.0`; ML.NET usa versão preview. **Compatibilidade Native AOT/trimming não foi verificada.** Como configurações fora de `src` foram excluídas, não concluo que AOT esteja habilitado ou ausente.

Referências oficiais usadas apenas para conferir APIs propostas:

- [WebSockets e keep-alive no .NET](https://learn.microsoft.com/dotnet/fundamentals/networking/websockets#keep-alive-strategies)
- [Utf8JsonReader](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/use-utf8jsonreader)
- [Channels bounded e descarte](https://learn.microsoft.com/dotnet/core/extensions/channels)

---

## 5. Validação e handoff

### Executado

- `git status --short`
- `git log -5 --oneline -- src`
- `git rev-parse HEAD`
- Inventário dos fontes versionados elegíveis.
- Inspeções paralelas com **GPT-6 Luna, variante `none`**, seguidas de confirmação direta dos achados prioritários.
- Contraexemplos matemáticos locais para regime e convenção de arbitragem.
- `git diff --stat -- src`
- `git status --short -- src`

Os dois últimos comandos não apresentaram alterações em `src`.

### Não verificado

- Build Release, testes e publicação Native AOT.
- Traceability, caos, soak e integração PostgreSQL.
- Integração Binance, rate limits efetivos e reconexão real.
- Latências p50/p95/p99, alocações por evento e pausas de GC.
- Rentabilidade, calibração fora da amostra e executabilidade de alpha.

A varredura cobriu os 12 projetos de `src`, com aprofundamento nos fluxos financeiros e de mercado. Isso não equivale a prova formal de ausência de defeitos em cada caminho.

**Arquivos alterados na auditoria:** nenhum.
**Commits criados:** nenhum.

**Ordem recomendada de correção:** proteção intrabar → consistência de lifecycle → ML/indicadores → rate limits → recepção WS → otimizações do livro → realismo de execução. Os gates financeiros devem permanecer fechados durante essa evolução.

---

## Nota de consolidação

A conclusão inicial da primeira inspeção de risco/OMS de que não havia defeito forte foi **superseded** pela verificação específica posterior do caminho intrabar, registrada no achado 1 deste relatório. A confirmação é estática: a duração do atraso não foi medida em execução.

Os opt-ins de Testnet **não bastam para liberar novas submissões**, pois a fronteira pré-trade atual bloqueia o envio com `PRETRADE_EVIDENCE_UNVERIFIED`.

O salvamento deste documento é uma entrega documental solicitada pelo usuário; não implementa as correções, não altera o código-fonte nem autoriza operações na exchange.

## Execução incremental — base 0f6e8ab

Este registro parcial complementa a auditoria histórica, cuja base original `42ce739` e achados originais permanecem preservados. Não representa conclusão total da auditoria nem prontidão para produção.

### Itens concluídos

- **#1 — proteção intrabar:** implementação concluída e auditada por Luna (`low`), sem achados. Checkpoint durável de cooldown de 15 s está separado do wakeup de 2 s; o ciclo usa `TimeProvider`. Teste RED focado falhou como esperado (`expected 2, actual 1`); GREEN dos guards passou (31), incluindo persistência com falha e retry.
- **#3 — indicadores Wilder:** concluído e aprovado na auditoria Terra (`low`). ATR, +DM, -DM e RSI usam seeds Wilder reais; ADX usa média RMA do DX, com `IsWilderReady`/`IsAdxReady` e ADX vazio igual a zero, sem valor inventado. Fixtures de confluência aquecidas preservam os gates. RED: 7 testes de matemática/warmup; GREEN: 8 focados, 9 boundary e 274 Backtesting Release.
- **#4 — dispersão e sigma de retorno:** concluído e aprovado na auditoria Terra (`low`). `ReturnSigmaPerBar` usa variância amostral dos n-1 retornos, dividida por n-2, e raiz quadrada; `VolatilityRateCV` legado mantém alias `PriceDispersionRate` para features/DB/políticas. Novo threshold explícito de retorno é nullable e não reutiliza o limite legado de CV de 5%. Alto CV por tendência não vira `Chop`, mas sinaliza `LegacyDispersionLimitBreached`, preservando bloqueio de entrada e saída protetiva. Métricas incompletas/legadas falham fechadas: reader valida metadados JSON e `HasCompleteMetrics`; writer usa source generation; presença da chave `appliedMaxReturnSigmaPerBar` é verificada via SQL `exists`, distinguindo ausente de JSON null. API current/history expõe unidades e bloqueia ausência. RED: 3 classificação, 1 unidades do challenger, 3 round-trip/legado do PostgreSQL, 2 API e 1 parcial PostgreSQL. GREEN: 258 Domain, 274 Backtesting, 40 API Release, 53 PostgreSQL e 18 local Debug, sem skips.
- **#5 — fluxo de trades/VPIN:** concluído e aprovado na auditoria Terra (`low`). Estado usa quantidade assinada/total com decaimento temporal analítico de 5 s via `TimeProvider`, deduplicação de IDs e freshness de source/received; eventos inválidos/futuros são ignorados, overflow invalida mantendo watermark, desconexão limpa estado e só trades são consumidos para evitar dupla contagem de agregados. `EventTime <= 0` é validado antes de `AddOrUpdate`, evitando envenenamento de watermark; regressão envia ID 9999 com tempo zero e depois ID 1 válido (RED: `expected 1, actual null`). Proxy por candle é `AbsoluteBarVolumeImbalance`, sem alegação de probabilidade de toxicidade; gates e valores de sinal permanecem inalterados. GREEN final: 275 Backtesting Release e 255 Worker Release.
- **#11 — arbitragem triangular:** concluído e auditado por Luna (`none`), sem achados. Contrato usa ask na compra, rejeita fees/hurdle inválidos e deixa explícito o lucro teórico. GREEN: `ArbitrageTests` (7).
- **#12 — logger:** concluído e auditado por Luna (`none`), sem achados. Callback `DropWrite`, redação de mensagem/exceção/stack e console limitado ao tipo do erro. GREEN: `Observability` (48).
- **#13 — API:** concluído e auditado por Luna (`none`), sem achados. HTTP 503 genérico; correlation header registrado no log. GREEN: `API` (37); último run com `Logging__LogLevel__Default=Warning`.

Os testes RED de cada item falharam antes das respectivas correções. A suspeita inicial de double-count do logger foi **superseded** pela revisão final: callback `DropWrite` → `TryWrite=true` e canal fechado → `false` contam uma perda cada; testes de conservação com 20.000 registros e canal fechado (1) cobrem os casos.

### Estado e validação

- **#9:** concluído após auditoria Terra (`medium`), que aprovou a correção do último P1. As iterações identificaram e corrigiram/supersederam riscos de atomicidade em upsert, lifecycle e reservas; rejeição de update sem efeitos colaterais; rejeição de `NEW` após estado parcial; e conflito entre relógios local e da exchange, que agora lança `ACCOUNT_STREAM_LIFECYCLE_CLOCK_CONFLICT_REQUIRES_RECONCILIATION`, faz rollback e preserva `LockedUnknown`. Os três escritores de stream (`Append`/`Resolve`) adquirem o mesmo advisory lock por ordem antes dos row locks; resolução de identidade foi validada.
- **#4 — verificação pós-auditoria:** GREEN: 258 Domain, 274 Backtesting, 40 API e 53 PostgreSQL, sem skips. Reader exige existência da chave do limite; nullable explícito é aceito. `HasCompleteMetrics=false` para payload legado/parcial bloqueia exposure; writer usa `MarketRegimeJsonContext` source-generated.
- Migration 036 adiciona desempate persistente por sequência para eventos novos. Empates históricos bloqueiam o upgrade e exigem reconciliação humana, sem inventar causalidade. `LOCK TABLE ACCESS EXCLUSIVE` antes da guarda mantém serializada a migração até `ALTER`/commit. SHA-256: `1c21492c1299b522c6b88709d5a00060a624d665c5fa63e91367d4f14f68c6a8`.
- REDs confirmados: lifecycle (`expected 2, actual 3`); dois fault triggers tornaram a falha visível; dois testes de conflito de relógio sem exceção; empate de timestamp entre `Ack` e `Partial`; corrida de migration sem exceção. Erros iniciais de fixture/manifest/inventário e lock herdado do harness foram corrigidos antes da validação final.
- GREEN final: build Release de IntegrationTests sem warnings/erros; Persistence.Tests Release (43/43); PostgreSQL 18 real (49/49, Debug, sem skips) pelo harness com porta 55433 e diretório privado de lock. Usou imagem Docker local, instâncias temporárias privadas e credenciais efêmeras não logadas; compose foi parado ao fim e volumes nomeados preservados pelo script. PostgreSQL local preexistente na porta 5432 não foi tocado. Testes de concorrência usam barreiras `pg_locks`; fixtures de upgrade não alteram o journal real.
- A validação #9 não conclui a auditoria global. #3–5 foram concluídos; #2 está em andamento apenas como mapeamento, ainda sem implementação de código. #6–8, #10 e recomendações complementares seguem pendentes. Validação global, traceability, chaos e AOT continuam **not verified**. Core permanece `NO_TRADE`/pretrade block; nenhuma ordem ou commit foi criado.
- **PENDENTES:** #6–8, #10 e recomendações complementares.
- Comandos usados como referência:
  - `dotnet test tests/CryptoTrading.Worker.Tests/CryptoTrading.Worker.Tests.csproj --no-restore --configuration Release --minimum-expected-tests 1 -- --filter-class '*AutonomyCycleExecutionGuardsTests'`
  - `dotnet test tests/CryptoTrading.Domain.Tests/CryptoTrading.Domain.Tests.csproj --no-restore --configuration Release --minimum-expected-tests 1 -- --filter-class '*ArbitrageTests'`
  - Observability e API: respectivos projetos de teste, mesmas flags, sem filtro.
- Build Release global, traceability, chaos, PostgreSQL e AOT: **not verified**.
- Nenhum commit criado e nenhuma operação exchange executada.

### Fechamento do #2 — execução final (Terra, medium)

**#2 concluído e auditado em três incrementos (#2a/#2b/#2c).** Este adendo atualiza o estado da execução sobre a base `0f6e8ab`; não altera nem substitui o diagnóstico, as evidências ou os snippets da auditoria histórica acima.

O treinamento foi separado da decisão por meio de artefato numérico imutável, que exporta os modelos SDCA/GAM com a normalização ajustada apenas em `Train` e o packing de features definido por constantes. A paridade com ML.NET foi verificada com erro máximo `<= 1e-4`. `.Fit` e `PredictionEngine` permanecem somente no treinador offline; `RuntimeCore` e `Adaptive` não treinam, não mantêm cache ou priors e não fabricam replay.

A validação usa três folds cronológicos, embargo `H` e maturidade mínima de rótulos derivada de timeframe × horizon. Platt é ajustado somente na partição de calibração; Brier/ECE são calculados somente na validação. Metadados e parâmetros usam `JsonRequired`; o catálogo valida SHA-256 externo de 64 caracteres, hash interno do arquivo e limite de bundle de 1 MB. A chave é congelada em `Symbol/Frame/Side/H`. Sem modelo, o comportamento é fail-closed; ausência de probabilidade aprovada permanece `null`, sem converter confidence em probabilidade nem produzir `Sell` falso por zero. Compra Spot segue como continuação; saídas não dependem do modelo. A trace preserva model id/hash/target side, e exit confidence continua explicitamente heurística. Stop e `LegacyDispersionRiskExit` não são bloqueados por Noise/HoldWinner.

O loader do Worker lê somente um arquivo `.mlmodel.json` real e local em modo read-only, valida-o antes do ciclo e exige o par `WORKER_ML_ARTIFACT_PATH`/`WORKER_ML_ARTIFACT_SHA256`. Elegibilidade usa `TimeProvider`; Stop impede publicação tardia; falhas de I/O/hash invalidam qualquer artefato anterior; logs seguros não expõem caminhos globais ou segredos. A correção do intervalo trim resolve o caso `5m`/`8`.

A API pública offline `TrainFromBars` reutiliza as mesmas features e lookback 8. A política usa triple barrier preexistente (`TP max 180/CV 3`, `SL max 100/CV 1.8`) e três folds com purge. As barras fechadas são fornecidas por operador: o caminho book-aware não pode fabricar histórico a partir de OHLC. Sem artefato, walk-forward resulta em `no evidence`, nunca `false`; book-aware sem histórico rejeita a entrada em vez de inventar escalar.

**Exemplo documental, não executado.** `closedBars` deve ter sido previamente obtido como barras fechadas reais, sem credenciais ou chamada de exchange neste fluxo. Publicar o hash em configuração separada é condicionado a revisão humana e não aprova execução:

```csharp
var candidate = MlDecisionTrainer.TrainFromBars(
    closedBars,
    new MlArtifactPolicy("BTCUSDT", "1m", OrderSide.Buy, 4),
    timeProvider.GetUtcNow(),
    validUntilUtc,
    ct);
var bundle = MlArtifactCatalog.CreateBundle([candidate]);
await File.WriteAllBytesAsync(
    pathEndingMlmodelJson,
    bundle.Payload.ToArray(),
    ct);
// bundle.Sha256: configurar separadamente somente após revisão humana.
```

**Validação registrada:** REDs: 7 testes de foundation, 5 de causalidade, 116 para impedir calibração com amostras fake e 5 do loader cobrindo `5m`/`8`. GREEN final: 301 Backtesting e 266 Worker Release; build Release da solução com 0 warnings e 0 errors (mensagens informativas de SDK preview). Restore foi offline (`--source /tmp/kilo`); a referência não necessária `ML.TimeSeries` foi removida e os lockfiles regenerados sem upgrade de versão. JSON usa source generation. `Fit` coopera com cancelamento entre fases; a versão do SDK não oferece cancelamento durante `CancelExecution`.

**Limites e estado:** dados de teste são todos sintéticos e não constituem alpha. AOT, integração Binance e rentabilidade não foram verificados. Artefatos reais de produção não foram criados; nenhuma credencial, rede, edição de fonte, commit ou operação de exchange foi usada nesta atualização documental. `NO_TRADE` e a suspensão pré-trade permanecem inalterados. Continuam pendentes #6–8, complementos do #10 e validação global, trace, chaos/soak.
