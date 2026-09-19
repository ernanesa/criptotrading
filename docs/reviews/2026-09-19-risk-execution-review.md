# Revisão independente: Risk, Execution, Paper Outbox e Binance Account

Data: 2026-09-19  
Escopo: `RiskPolicy`/`RiskEvaluator`, `OrderExecutionPipeline`,
`PaperOutboxMessageExecutor`/`OutboxDispatcher` e o adapter autenticado
read-only da Binance.  Esta revisão foi somente de leitura sobre o código de
produção; o único artefato alterado é este relatório.

## Achados

### P1 — O adapter autenticado aceita um endpoint arbitrário

Arquivo: `src/CryptoTrading.Exchange.Binance/BinanceAuthenticatedClientOptions.cs:32-40`

`GetRestBaseAddress()` aceita qualquer URI absoluta `http` ou `https` quando
`RestBaseAddress` é informado. Assim, uma instância criada diretamente (ou uma
composição que não passe pelo `TradingOptionsValidator`) pode usar
`https://api.binance.com/api/`, um proxy, ou até HTTP, apesar de
`Environment` continuar sendo `Testnet`. O adapter atualmente só expõe leituras,
mas ainda envia API key e assinatura para esse destino; isso viola a barreira
de ambiente e pode expor credenciais.

Correção recomendada: restringir o override a uma allow-list derivada de
`BinanceEnvironment` (`testnet.binance.vision` para Testnet e o endpoint Demo
correspondente), exigir HTTPS e rejeitar host/path divergentes. Cobrir tanto o
construtor direto quanto o registro via DI com testes de validação. Manter a
validação em `TradingConfiguration` como segunda barreira, não como única
barreira.

### P1 — Timestamp futuro de mercado pode passar pelo stale-data gate

Arquivo: `src/CryptoTrading.Domain/Risk/RiskPolicy.cs:256-259`

O teste de idade só rejeita quando `evaluatedAt - marketDataAt` é maior que o
limite. Se `marketDataAt` estiver no futuro, a diferença é negativa e qualquer
ordem pode ser aprovada com um snapshot temporalmente inválido. Isso pode
ocorrer por relógio adiantado, erro de serialização ou dado de uma fonte
incorreta. Em um sistema financeiro, a resposta segura é bloquear nova
exposição quando a ordem temporal não é confiável.

Correção recomendada: rejeitar quando `marketDataAt > evaluatedAt` (idealmente
com um pequeno limite explícito de skew, documentado e testado), usando um
reason code de market-data indisponível/stale. Adicionar testes para timestamp
futuro e para o limite exato.

### P2 — Payload inválido do Outbox entra em retry indefinido

Arquivos: `src/CryptoTrading.Worker/OutboxDispatcher.cs:186-218` e
`src/CryptoTrading.Worker/OutboxDispatcher.cs:88-120`

Erros permanentes de desserialização (`JsonException`, enum inválido, decimal
inválido ou envelope incompatível) são tratados como qualquer falha transitória.
O dispatcher mantém a mensagem não processada e calcula um atraso máximo de
300 segundos, sem limite de tentativas nem estado de dead-letter. Uma mensagem
corrompida, incompatível após deploy ou manualmente adulterada permanece
ocupando o fluxo para sempre e gera ruído operacional; também não existe uma
ação explícita para impedir que a mesma mensagem seja reexecutada após cada
reinício.

Correção recomendada: distinguir falhas permanentes de transitórias, registrar
um motivo redigido, e mover a mensagem para dead-letter (ou marcar como
permanently failed) depois de uma política versionada de tentativas. Expor
métrica/alerta para a fila e cobrir o caminho com teste de poison message.

## Recomendações de robustez

1. `BinanceSpotAccountClient.SendSignedAsync` (`src/CryptoTrading.Exchange.Binance/BinanceSpotAccountClient.cs:129-140`)
   descarta o corpo de erro da Binance e não usa a política de retry/rate-limit
   existente. Para reconciliação isso torna diagnósticos de 429/5xx pobres e
   pode transformar uma falha transitória em SafeMode imediato. Adicionar um
   erro estruturado redigido (código/status/endpoint), retry apenas para
   respostas seguras e telemetria de rate-limit; nunca registrar query,
   assinatura ou credenciais.
2. A semântica de `MaxExposureNotional` merece ser explicitada. Em
   `RiskPolicy.cs:289-312`, o valor limita o target solicitado, enquanto
   `CurrentExposureNotional` só participa dos breakers. Se o contrato pretendia
   limitar exposição total após a ordem, falta calcular o headroom
   (`maxExposure - currentExposure`) e testar esse caso; se target for absoluto,
   documentar isso e adicionar testes de compra/aumento e redução.
3. Os DTOs autenticados (`BinanceAccountModels.cs:5-51`) aceitam campos de
   saldo/listas vindos da rede sem uma validação de contrato. Recomenda-se
   validar ausência, símbolos, números decimais e valores não negativos antes
   de alimentar reconciliação, mantendo o comportamento fail-closed.

## Pontos positivos observados

- `OrderExecutionPipeline` é puro e cria command/outbox somente após o
  `RiskEvaluator`; rejeições não produzem efeitos colaterais.
- A política de risco é versionada, reduz targets em vez de aumentá-los e
  preserva reduções de exposição em SafeMode/kill switch.
- O executor PAPER usa idempotência e tenta resolver timeout ambíguo por
  `ClientOrderId` antes de permitir novo processamento.
- O contrato `IBinanceSpotAccountClient` não expõe submit/cancel/transfer/
  withdrawal, e a assinatura HMAC não coloca o secret na URL.
- Configuração local já aplica TESTNET, HTTPS, host permitido e withdrawals
  desabilitados; o problema P1 é a possibilidade de contornar essa camada ao
  compor o adapter diretamente.

## Validação realizada

Foi feita inspeção de implementação, contratos e testes existentes; nenhuma
chave foi carregada e nenhuma chamada externa ou ordem foi executada. Os
achados acima ainda não foram corrigidos neste commit, conforme solicitado para
esta revisão.

