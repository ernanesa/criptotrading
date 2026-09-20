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

## Secrets e primeiro bootstrap

O repositório não contém credenciais. O contrato local para a Binance Spot
Testnet está em [`secrets/README.md`](secrets/README.md), o exemplo sem valores
está em [`.env.example`](.env.example) e o helper de entrada mascarada está em
[`scripts/init-testnet-secrets.sh`](scripts/init-testnet-secrets.sh). Execute o
helper somente depois de revogar qualquer chave que tenha sido exposta e gerar
uma nova chave Testnet. A integração pública pode ser verificada com
[`scripts/test-binance-testnet-public.sh`](scripts/test-binance-testnet-public.sh);
nenhum teste autenticado deve ocorrer com segredo enviado pelo chat.

O procedimento do bootstrap local está em [`docs/local-development.md`](docs/local-development.md).
O acompanhamento das tarefas está em [`docs/implementation-checklist.md`](docs/implementation-checklist.md).
Antes de abrir uma mudança, os gates locais independentes podem ser executados
em paralelo com [`scripts/test-local-gates.sh`](scripts/test-local-gates.sh);
o relatório redigido e as limitações do comando estão documentados em
[`docs/local-quality-gates.md`](docs/local-quality-gates.md).

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

## Contexto dos agentes

As regras de colaboração estão em [`AGENTS.md`](AGENTS.md). Cada tarefa começa
com um context pack mínimo e termina com um handoff redigido; decisões
substituídas ficam marcadas como `SUPERSEDES` e não retornam ao contexto ativo.
O skill global `context-state-manager` aplica a mesma política, e o RAG é usado
somente para localizar fontes relevantes por ID/hash antes da confirmação no
checkout atual.

## Adapter Binance Spot read-only

O projeto `CryptoTrading.Exchange.Binance` possui duas fronteiras separadas:

- o cliente público de market data (`ping`, `time`, `exchangeInfo`, klines e
  streams), que não lê credenciais;
- o cliente autenticado estritamente read-only, usado apenas pelo probe e pela
  reconciliação opt-in, limitado a account, balances, open orders e trades.

Não existem endpoints de submit/cancel de ordens, saques, transferências,
margin ou Futures no adapter autenticado. O registro autenticado não ocorre por
default; exige Testnet, `NO_TRADE`, persistência quando aplicável e credenciais
locais protegidas. Em runtime, a validação rejeita qualquer conta que reporte
`canWithdraw=true`. Exclusivamente o probe manual de Testnet registra essa
capacidade para permitir a verificação completa das leituras autenticadas: a
Testnet não expõe `/sapi` e o cliente continua sem qualquer endpoint mutável.

O registro no host de composição usa `AddBinanceSpotMarketClient`, com
`BinanceEnvironment.Testnet` como padrão. O endereço da Testnet é
`https://testnet.binance.vision/api/`; Demo pode ser selecionado explicitamente.
O cliente valida timeout, endereço absoluto HTTP(S), símbolo ASCII e respostas
HTTP sem copiar o corpo de erro para exceções ou logs.

Os testes usam `HttpMessageHandler` falso e não fazem chamadas à exchange. A
única execução externa autorizada é o probe manual
`scripts/test-binance-testnet-auth-readonly.sh`, que grava apenas relatório
redigido e mantém o gate em No-Go quando qualquer guarda falha.
