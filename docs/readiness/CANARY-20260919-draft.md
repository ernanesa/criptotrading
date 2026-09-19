# Canary Plan CANARY-20260919-draft

**Status:** Draft bloqueado; não autoriza ordens nem capital real.
**Release/estratégia/config/RiskPolicy:** ainda não aprovadas; baseline Trend Following BTC/USDT.
**Ambiente/conta/símbolo:** local / Binance Spot Testnet segregada / BTCUSDT (a confirmar)
**Owner/aprovador:** pendente / pendente
**Capital máximo e perda máxima:** não definidos; nenhum capital permitido neste snapshot.

## Pré-condições bloqueantes

- [ ] PRM válida, com thresholds, unidades, janela, validade e aprovador.
- [ ] RiskPolicy versionada e hash registrada.
- [ ] Credenciais novas de Testnet configuradas fora do Git, permissões mínimas e withdrawals desabilitados comprovados.
- [ ] Reconciliação startup/reconnect/timeout executada contra conta Testnet.
- [ ] Backup/restore em volume limpo, kill switch e SafeMode drillados.
- [ ] Soak shadow e paper com intervenção, divergência, stale data e recovery medidos.
- [ ] Operador, canal de alerta e janela de observação definidos.

## Envelope planejado (a preencher somente após aprovação)

| Estágio | Capital/notional | Duração/volume | Ordens permitidas | Promoção |
|---|---:|---|---|---|
| C0 — Testnet read-only | 0 | período a definir | nenhuma | reconciliação e readiness verdes |
| C1 — Paper local | 0 real | duração/volume a definir | simuladas | todos os gates quantitativos pass |
| C2 — Testnet submit controlado | **não autorizado** | a definir | somente após autorização explícita | revisão humana e aborts verdes |
| C3 — Canary real | **não autorizado** | a definir | somente envelope aprovado | decisão Go independente |

## Abort e ações seguras

Sem thresholds aprovados, qualquer evento de risco aborta: novas entradas ficam em `NO_TRADE`, SafeMode é ativado quando aplicável, ordens em estado desconhecido são reconciliadas antes de retry e o ledger nunca é apagado. Triggers obrigatórios: drawdown, daily loss, stale data, drift de saldo/ordem/fill, falha de auditoria, clock drift, degradação da exchange, slippage/fill divergente e intervenção inesperada.

## Ordens em voo, rollback e encerramento

- estado desconhecido: consultar por `clientOrderId` e reconciliar antes de qualquer repetição;
- rollback: configuração/imagem somente após preservar journal, ledger, outbox e evidências;
- fallback: `NO_TRADE`/SafeMode, com reentrada somente por aprovação explícita;
- fechamento: relatório de reconciliação, incident review, métricas e decisão final assinada.
