# 00 - Project Charter

## Problema

Bots simples normalmente misturam sinal, risco e execução, ignoram custos reais e são difíceis de auditar. O CryptoTrading será uma plataforma de engenharia quantitativa, não um script de compra e venda.

## Visão do produto

Permitir que uma pessoa pesquise estratégias, valide robustez, simule com realismo e coloque apenas estratégias aprovadas em produção, mantendo controle centralizado de risco e execução.

## Usuário primário

Operador/desenvolvedor único no início. A arquitetura deve permitir múltiplos usuários futuramente sem obrigar essa complexidade no MVP.

## Resultados desejados

- aprender engenharia moderna em .NET, React, cloud e trading quantitativo;
- criar uma plataforma reproduzível de pesquisa → produção;
- reduzir riscos de bugs, overfitting, execução incorreta e perda catastrófica;
- permitir migração AWS → Oracle Cloud → VPS/Hostinger sem redesenhar o produto;
- construir base extensível para Spot, Futures e estratégias market-neutral.

## Fora de escopo inicial

- HFT competitivo/sub-millisecond;
- custódia e saques automáticos;
- exchange própria;
- gestão de dinheiro de terceiros;
- copy trading público;
- aplicações mobile nativas;
- garantia de rentabilidade;
- execução multi-exchange no 1.0;
- ML como requisito para o primeiro lançamento.

## Escopo congelado do MVP

- Binance Spot, começando por BTC/USDT;
- uma baseline simples antes de multi-estratégia;
- paper trading e integração real com Spot Testnet;
- sem arbitragem, Futures, market making ou multi-exchange;
- expansão para até 20 ativos somente depois de `UniversePolicy` e capacidade serem validadas;
- RAG/GraphRAG somente no Research Plane, sem acesso ao hot path ou a credenciais.

## Restrições

- desenvolvimento incremental, em pequenas sessões diárias;
- infraestrutura inicial de baixo custo;
- suporte obrigatório a ARM64 e AMD64;
- .NET 11/C# 15 como alvo definitivo;
- Binance como primeira exchange;
- interface web React + TypeScript.

## Princípios de produto

1. Capital preservation first.
2. No trade é uma decisão válida.
3. Uma estratégia não controla risco final.
4. Evidência out-of-sample supera backtest bonito.
5. Simulação deve ser conservadora.
6. Estado da exchange é fonte autoritativa para reconciliação.
7. Toda decisão live deve ser explicável e auditável.
8. Complexidade só entra quando resolve uma necessidade medida.

## Métricas do produto

### Engenharia
- disponibilidade do engine;
- reconciliações divergentes;
- latência de processamento;
- perda/gap de market data;
- rejeição de ordens;
- tempo de recuperação;
- incidentes críticos.

### Trading
- PnL líquido;
- maximum drawdown;
- expectancy;
- profit factor;
- Sharpe/Sortino/Calmar;
- slippage esperado x realizado;
- fees e turnover;
- exposição por ativo/estratégia/regime;
- tempo em drawdown;
- strategy health.

## Critério de sucesso 1.0

O produto 1.0 é considerado tecnicamente bem-sucedido quando consegue executar Spot em live canary por período definido sem inconsistências de estado, respeitando todos os limites de risco, com observabilidade suficiente para reconstruir cada decisão e com pelo menos uma estratégia que tenha passado por todo o pipeline de validação. Isso não implica promessa de lucro.
