# Envelope de preparação Spot Testnet — 2026-10-08

**Status:** documentação somente leitura / NO-GO para mutações. Base de trabalho observada: `62687c9285967a89ae99c74b7e36abf52605bdfe`; árvore alterada, sem commit de entrega congelado. Nenhuma ordem, cancelamento, transferência, withdrawal ou deploy foi executado por esta leitura.

## Estado de credenciais observado

As quatro variáveis do processo `BINANCE_API_KEY`, `BINANCE_API_SECRET`, `BINANCE_API_KEY_FILE` e `BINANCE_API_SECRET_FILE` estavam **ausentes** na sessão desta leitura. Isso é somente uma verificação booleana do ambiente corrente; não inspecionei `.env`, conteúdo de arquivos, secret store ou permissões, então não afirma que credenciais locais não existam. Nenhum valor ou caminho foi exibido.

## Comandos e capacidades que existem

**Preflight offline sem credenciais:**

```bash
WORKER_NO_TRADE=true WORKER_EXECUTION_MODE=PAPER bash scripts/test-binance-readonly-preflight.sh
```

O script roda o probe em diretório temporário sem `.env`, remove as variáveis de credencial e espera recusa antes de criar cliente de rede autenticado. Ele grava um relatório local privado em `artifacts/binance`; não prova credenciais válidas, conectividade, regras do símbolo ou reconciliação. Este comando foi apenas lido, não executado nesta tarefa.

**Probe autenticado read-only existente:** `bash scripts/test-binance-testnet-auth-readonly.sh`. Ele exige credenciais Testnet em valores locais ou arquivos protegidos, valida endpoint Spot Testnet e `BINANCE_WITHDRAWALS_ENABLED=false`, e consulta conta, ordens abertas e trades. Não envia nem cancela ordens; é consulta externa somente leitura, não foi executado e não há confirmação de que credenciais protegidas estejam configuradas. O relatório não deve incluir chaves ou payloads brutos.

**Capacidade mutável encontrada:** o adaptador de ordens possui `POST /api/v3/order` para submit e `DELETE /api/v3/order` para cancel, além das consultas GET. O registro de execução Testnet do Worker exige flags explícitas incluindo `WORKER_EXECUTION_MODE=TESTNET`, `WORKER_NO_TRADE=false`, persistência/migrations, `WORKER_TESTNET_EXECUTION_ACKNOWLEDGED=true`, `WORKER_TESTNET_ORDER_EXECUTION_ENABLED=true`, `WORKER_RISK_POLICY_REQUIRED=true`, reconciliação e Account Stream. Não habilitar essas flags neste passo. Não foi encontrado comando CLI de one-shot que limite o Worker a uma única ordem.

`tools/CryptoTrading.TestnetStateInitializer` não é um preflight read-only: pode aplicar migrations e criar, aprovar ou ativar RiskPolicy; quando o ledger está vazio, consulta saldos Testnet e os persiste, além de semear dados de cost basis. Não usar para obter apenas leitura ou para preparar o ensaio sem aprovação e revisão do estado local.

## Escopo mutável proposto, ainda não autorizado

- Ambiente: Binance Spot Testnet segregada; nenhum capital real, margem, empréstimo, Futures, transferência ou saque.
- Símbolo candidato: BTCUSDT, sujeito a `exchangeInfo`, filtros, book, saldo e comissão correntes.
- Teto virtual: 20 USDT de notional; apenas limite do ensaio, não conversão de R$ 100 e não orçamento real.
- Limite proposto: no máximo 1 submit de ordem passiva + 1 cancel, somente após autorização específica para commit, conta, símbolo, tamanho e janela UTC. Sem retry cego; timeout/ambiguidade exige consulta por `clientOrderId` e reconciliação.
- O adaptador suporta `Limit` GTC/IOC/FOK comum, sem post-only/`LIMIT_MAKER`; portanto não consegue garantir que a ordem permaneça passiva. C1 fica bloqueado até capacidade e testes passive-only ou revisão formal do requisito. Não substituir por `LIMIT GTC` comum.
- Uma ordem `MARKET`, round-trip ou segunda ordem fica fora deste envelope e requer aprovação separada.

Antes de converter o limite de risco informado em BRL para quote USDT, exigir saldo livre real da Testnet, fonte e timestamp BRL/USDT aprovados, limite de frescor e conversão conservadora. Validar min-notional, `LOT_SIZE`, `PRICE_FILTER`, fee/reserva, liquidez e idade de preço. Qualquer insuficiência ou incerteza resulta em `NO_TRADE`.

Após eventual autorização do C1, evidência mínima inclui snapshot redigido pré-trade, commit/hash da RiskPolicy, contexto de FX/mercado/filtros, ids de ordem mascarados, consultas terminal, balances/orders/trades reconciliados com `execution_fills`/journal/ledger/projeções, hashes, recovery/replay e relatório sanitizado. Esse bundle não é aprovação automática para canary real ou Live.

## Gates antes de qualquer mutação

- [ ] Credenciais Testnet segregadas, protegidas e validadas sem imprimir valores; conta e permissões confirmadas por probe read-only autorizado.
- [ ] Withdrawals desabilitados e nenhuma margem/Futures; conta/reconciliação sem estado desconhecido.
- [ ] RiskPolicy aprovada, vigente, hash canônico, conversão BRL/USDT fresca e limites aplicáveis testados.
- [ ] Post-only/passive-only disponível e comprovado, ou escopo revisado formalmente; ordem única/cancel único demonstrados.
- [ ] Testes, integração PostgreSQL, recovery, auditoria e revisão independente do commit exato aprovados.
- [ ] Autorização humana imediata e específica para mutação, registrando exatamente conta, commit, símbolo BTCUSDT, até 20 USDT virtuais, 1 submit + 1 cancel e janela UTC.

**Próximo passo seguro:** decidir se autoriza somente o probe autenticado read-only, após provisionamento seguro de credenciais Testnet. Isso não autoriza submit/cancel. Até fechar os gates acima, manter PAPER + NO_TRADE e não iniciar OCI.
