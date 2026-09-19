# CryptoTrading

Plataforma própria de trading algorítmico de criptomoedas, a ser implementada em .NET 11 + C# 15 no backend e React + TypeScript no frontend.

O projeto será desenvolvido de forma incremental, com prioridade para segurança, pesquisa reproduzível, gestão de risco independente, execução confiável e portabilidade entre provedores de infraestrutura.

## Decisões vigentes de implementação

- .NET 11 e C# 15 são requisitos obrigatórios, não apenas alvos futuros; não há fallback planejado para .NET 10.
- Enquanto o .NET 11/C# 15 estiver pré-GA, o repositório fixa por `global.json` o SDK RC/go-live oficial validado e atualiza versões somente em mudança isolada com CI verde.
- O ambiente inicial é esta máquina local, com Linux containers e Docker Compose. Desenvolvimento, PostgreSQL, dados, telemetria, paper trading e Testnet não dependem de AWS.
- AWS, Oracle Cloud ou VPS são opções futuras e fora do escopo ativo; qualquer ativação exigirá nova decisão explícita, ADR, estimativa de custo, threat model e plano de migração.

> O sistema não promete lucro nem elimina perdas. O objetivo de engenharia é buscar estratégias com expectativa líquida positiva e limitar rigorosamente risco operacional e financeiro.

## Planejamento

Toda a documentação de produto, arquitetura, Scrum, requisitos, estratégias, riscos, releases e critérios de qualidade está em [`plan/`](plan/README.md).

## Princípios

1. Strategy propõe; Portfolio dimensiona; Risk autoriza; Execution executa.
2. Nenhuma estratégia chama a exchange diretamente.
3. Paper → shadow → canary → live.
4. Custos, spread, slippage e latência fazem parte do resultado.
5. Backtest sem validação fora da amostra não é evidência suficiente.
6. Sem permissão de saque nas chaves de trading.
7. Arquitetura modular e containerizada, local-first e sem dependência de provedor de nuvem.

## Escopo congelado do MVP

O primeiro incremento é Binance Spot em BTC/USDT, com uma única baseline Trend Following, paper e Testnet real. Multi-strategy só começa após o R1.0. Não há arbitragem, Futures ou market making no produto comprometido. A expansão para até 20 ativos depende de uma `UniversePolicy` point-in-time, capacidade medida, churn budget e gate explícito.

O RAG é uma ferramenta local do Research Plane, fora do hot path de capital. Ele deve confirmar fontes contra o código/commit atual e nunca autoriza risco, ordens, credenciais ou mudanças externas.
