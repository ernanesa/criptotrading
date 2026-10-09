# CanaryPlan proposto — ensaio local → Spot Testnet

**Status:** Draft bloqueado / **NO-GO para ordem mutável**. Este plano registra um candidato a revisão; não concede aprovação, não habilita o Worker e não autoriza capital real.

**As of:** 2026-10-08. **Base de auditoria:** `62687c9285967a89ae99c74b7e36abf52605bdfe`; a árvore está alterada e ainda não há commit candidato congelado. **Owner / aprovador:** pendentes. **RiskPolicy:** [RISK-20261008-local-canary-draft.md](RISK-20261008-local-canary-draft.md), ainda não canônica nem aprovada. **Validade proposta:** 7 dias após aprovação. **Válida até:** PENDENTE. **Decisão:** No-Go até os critérios abaixo estarem satisfeitos e haver autorização humana específica.

A PRM disponível é snapshot histórico expirado, com No-Go para Testnet/Live. Este CanaryPlan não o substitui e não demonstra gates aprovados.

## Envelopes separados

| Fase | Capital / notional | Ações propostas | Estado |
|---|---:|---|---|
| Preparação local | R$ 0 real; 0 USDT de ordem | Validar modo PAPER/NO_TRADE, executar apenas checks locais autorizados e montar evidência sem credenciais | Permitida como trabalho local; nenhum teste foi executado por este artefato |
| Testnet somente leitura | 0 ordem; leitura de conta, saldos, filtros, ordens abertas, trades e comissão | Probe autenticado read-only, após credenciais protegidas estarem disponíveis | Requer credenciais Testnet e escolha explícita para consulta externa somente leitura |
| Candidato a C1 mutável | Até **20 USDT de notional virtual**, sem orçamento real; no máximo **1 submit LIMIT + 1 cancel** | Uma ordem passiva BTCUSDT, consulta por `clientOrderId`, cancelamento somente se ainda aberta, consulta terminal e reconciliação | **Bloqueado:** adaptador atual não oferece post-only/passive-only; sem limite de one-shot comprovado no Worker; autorização específica ausente |
| Mercado/round-trip | Fora deste envelope | Qualquer `MARKET`, fill de ida/volta ou segunda ordem | Exige aprovação separada e novo envelope; não está incluído por padrão |

O saldo de Testnet é virtual e independente dos R$ 100 informados para planejamento. Não transferir fundos nem interpretar o teto de 20 USDT como conversão dos R$ 100. Sem saldo de Testnet livre verificado, filtros atuais, comissão e conversão BRL/USDT fresca quando necessária para avaliar o risco, permanecer em `NO_TRADE`.

## Bloqueio técnico para ordem passiva

O contrato do domínio e o adaptador Binance encontrados aceitam somente `Limit` e `Market`; a ordem Limit permite GTC/IOC/FOK, sem parâmetro post-only ou `LIMIT_MAKER`. Uma Limit GTC comum pode cruzar o book e preencher imediatamente; posicionar preço longe do toque não prova passividade por causa de mudanças e corrida de mercado. Portanto o passo passivo solicitado não está implementável com garantia no contrato atual. Não substituir silenciosamente por LIMIT GTC comum. Primeiro adicionar capacidade post-only comprovável ou revisar formalmente o escopo e risco; em ambos os casos, exigir teste de contrato que prove zero submit em configuração incompatível.

O executor Testnet pode ser registrado pelo Worker quando `WORKER_EXECUTION_MODE=TESTNET` e exige, entre outros gates, `WORKER_NO_TRADE=false`, persistência e migrations, acknowledgement de execução, `WORKER_TESTNET_ORDER_EXECUTION_ENABLED=true`, RiskPolicy obrigatória, reconciliação e Account Stream. Esses switches habilitam capacidade mutável, mas não implementam por si só o limite de uma ordem. Não configurar nem iniciar esse modo para este draft. Não existe comando dedicado confirmado para executar somente o ciclo de uma ordem e um cancelamento.

## Sequência de aceitação proposta, após desbloqueios e autorização

1. Congelar commit e artefatos de código; validar RiskPolicy concreta, hash, validade, aprovação segregada e configuração do Worker.
2. Fazer preflight autenticado somente leitura no endpoint Spot Testnet exato: `exchangeInfo` para status/filtros `BTCUSDT`, book/ticker fresco, conta/permissões, saldo `free/locked`, open orders, trades e comissão. Confirmar withdrawals desabilitados e nenhum estado desconhecido. Redigir identificadores privados.
3. Reconciliar estado local com exchange e validar persistência, fee/quote valuation, regra de risco, KillSwitch/SafeMode, TTL, limite de exposição, saldo após fee/reserva e preço/quantidade arredondados a tick/step. Rejeitar sem mutação qualquer dado stale ou discordante.
4. Somente com autorização específica, enviar no máximo uma ordem passiva de `BTCUSDT`, teto 20 USDT virtual; sem retry cego. Em outcome ambíguo, consultar `clientOrderId` e reconciliar antes de qualquer ação. Cancelar no máximo uma vez, apenas se ainda estiver aberta.
5. Consultar estado terminal, ordens, trades/fills e saldos; conferir `execution_fills`, journal/ledger, projeções e relatório de reconciliação. Executar recuperação/replay local isolado e comparar evidência antes de fechar o gate.

Qualquer execução que preencha imediatamente, resulte em fill parcial, timeout, rejeição, divergência, perda de freshness ou unknown state termina o ensaio; manter novas entradas bloqueadas e reconciliar. Não repetir submit como recuperação. Um round-trip `MARKET` precisa de outro documento e autorização específica.

## Critérios bloqueantes e decisão

- [ ] Owner operador, aprovador de risco independente e autorização escrita para exato commit, conta Testnet, símbolo, 1 submit LIMIT passivo, até 1 cancel, limite 20 USDT virtual e janela UTC.
- [ ] RiskPolicy aprovada, atual e coerente com saldo/conversão BRL/USDT, fee total, risco por trade, loss diário/acumulado e posição única.
- [ ] Capacidade post-only/passive-only implementada e verificada; no máximo um submit e um cancel demonstrados sem caminho alternativo autônomo.
- [ ] Conta Testnet segregada, `canTrade` verdadeiro, withdrawals desligados, credenciais protegidas e conta reconciliada sem ordens desconhecidas.
- [ ] Market data, FX, symbol filters, saldo e commission frescos e vinculados ao mesmo contexto de decisão.
- [ ] Testes e integração PostgreSQL no commit candidato, fail-closed em precondições inválidas, evidência durável e revisão independente.
- [ ] Critérios de stop, report sanitizado, recovery e operador/on-call definidos para a janela.

**Decisão atual:** NO-GO para C1 mutável. A fase somente leitura é o próximo gate possível após credenciais protegidas e decisão explícita sobre consulta autenticada. Nada neste draft autoriza ordens, cancelamentos, transferências, deploy OCI ou capital real.
