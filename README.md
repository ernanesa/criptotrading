# CryptoTrading

Plataforma própria de trading algorítmico de criptomoedas, planejada para .NET 11 + C# 15 no backend e React + TypeScript no frontend.

O projeto será desenvolvido de forma incremental, com prioridade para segurança, pesquisa reproduzível, gestão de risco independente, execução confiável e portabilidade entre provedores de infraestrutura.

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
7. Arquitetura modular e containerizada, sem dependência desnecessária do provedor de nuvem.
