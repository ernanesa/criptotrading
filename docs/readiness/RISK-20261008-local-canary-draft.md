# RiskPolicy proposta — canary local / Spot Testnet

**Status:** DRAFT / NO-GO. Este documento propõe limites para revisão; não é uma `RiskPolicy` executável, aprovação, autorização de ordem nem autorização de capital real.

**As of:** 2026-10-11. **Base observada:** `c891d08131c318e35f8a688105434518c8e6e282`. **Owner / criador humano:** Ernane (Risk & Operations Owner). **Aprovador independente:** pendente (Gate M3). **Validade proposta:** 7 dias corridos após aprovação M3; a data UTC exata fica vinculada à assinatura humana final. **Válida até:** PENDENTE_M3. **Version:** 2026.10.11-m1. **SHA-256:** PENDENTE_M3 (Live permanece NO-GO).

## Parâmetros aprovados pelo operador (Ernane) para o motor de risco local (F03–F06)

Capital informado para planejamento: **R$ 100,00**, sem transferência ou uso de capital real nesta etapa (`NO-GO Live`).

| Limite aprovado pelo operador | Valor e unidade | Janela / efeito | Estado |
|---|---|---|---|
| Teto de capital comprometido + perdas | R$ 100,00 (convertido via `USDTBRL <= 60s` com haircut de 2% e hard-cap de 20,00 USDT) | `posições + pending + UNKNOWN + reserva de taxas + perda realizada líquida <= teto`; reuso permitido após encerramento de venda | Implementado em `BudgetPolicy` e `ICapitalReservationStore` |
| Perda realizada diária (`MaxDailyLoss`) | R$ 2,00 (2% dos R$ 100 iniciais) | Dia UTC; bloquear novas entradas e acionar `SafeMode` até revisão humana | Aprovado pelo operador; convertido conservadoramente via `USDTBRL` |
| Perda absoluta acumulada (`MaxDrawdown`) | R$ 5,00 (5% dos R$ 100 iniciais) | Acumulada desde o capital inicial; parada permanente (`SAFE_MODE_NO_TRADE`), sem reset automático | Aprovado pelo operador; convertido conservadoramente via `USDTBRL` |
| Risco máximo por trade | R$ 1,00 (1% dos R$ 100 iniciais) | Por trade (distância de stop + taxas de entrada/saída 10 bps/lado + reserva 25 bps + slippage) | Implementado em `BudgetPolicy.MaxRiskPerTradeBrl` |
| Posições Spot (`MaxOpenPositions`) | 1 (`BTCUSDT`) | Uma posição aberta no máximo; sem margem, empréstimo ou Futures | Implementado; bloqueia enquanto posição ou ordem `LockedUnknown` existir |
| Retiradas | Desabilitadas | Todos os ambientes cobertos pela política | Obrigatório |
| Taxas e reserva (`Fee Tier`) | 10 bps (0,10%) por ordem + 25 bps de reserva técnica | Sem depender de desconto em BNB | Implementado |
| Backup operacional e retenção | AES-256 (keyfile mode 0600, dir 0700), RPO 15m (900s), RTO <= 10m (600s), retenção 7 dias (`prune-after-verified-7d`) | Fail-closed se espaço livre em disco < 2 GB ou < 10% | Implementado em `backup-postgres.sh` e validado via drills |

Os limites de R$ 2 e R$ 5 são limites de orçamento em BRL. O domínio `RiskPolicy` armazena perdas, exposição e notional na moeda quote do instrumento; portanto esses valores **não podem ser copiados como números em USDT**. Antes de qualquer ativação, registrar fonte verificável de BRL/USDT, par e direção usados, instante observado, idade máxima aprovada, tratamento de timestamps futuros, arredondamento conservador e hash da amostra. Usar saldo livre real da conta Spot Testnet e o snapshot de mercado correspondente. Se qualquer entrada estiver ausente, vencida, inconsistente ou insuficiente após fees e reserva, o resultado obrigatório é `NO_TRADE`.

O mínimo de ordem de `BTCUSDT`, `PRICE_FILTER`, `LOT_SIZE`/`MARKET_LOT_SIZE`, `MIN_NOTIONAL`/`NOTIONAL`, status do símbolo, spread, liquidez e comissão devem vir de consultas atuais e ser preservados como evidência redigida. Se o menor notional permitido mais taxa/reserva exceder o saldo livre convertido com margem conservadora aprovada, não há ordem. A conversão não autoriza depósito, transferência ou uso dos R$ 100 em uma conta real.

## Mapeamento para o contrato do repositório

O `RiskPolicy` atual aceita `MaxOrderNotional`, `MaxExposureNotional`, `MaxDailyLoss`, `MaxDrawdown`, `MaxMarketDataAge`, retiradas, slippage, volatilidade e liquidez mínima, com valores monetários em moeda quote. Não há campo explícito para perda máxima por trade, parada cumulativa em BRL ou regra de uma única posição; os três requisitos precisam de decisão de arquitetura e validação antes de alegar que a política os aplica. `MaxDrawdown` só poderá representar o limite absoluto de R$ 5 após uma definição testável de capital inicial, conversão e janela.

| Campo executável | Proposta de valor | Bloqueio para preenchimento |
|---|---|---|
| `MaxOrderNotional` | Menor entre saldo livre elegível e o envelope Testnet de até 20 USDT | Validar saldo, símbolo/filtros, fees e conversão; limite de 20 USDT é apenas notional virtual do teste e não equivale a R$ 100 |
| `MaxExposureNotional` | Máximo para uma única posição Spot, limitado pelo saldo elegível | Formalizar a regra de uma posição e conversão quote |
| `MaxDailyLoss` | Equivalente conservador de R$ 2 | Definir FX fresco e contabilização de fees/realized loss |
| `MaxDrawdown` | Equivalente conservador de R$ 5 | Definir capital inicial auditável, cash flows, reset proibido e regra de parada |
| `MaxMarketDataAge`, slippage, volatilidade e liquidez | Sem valor aprovado neste rascunho | Definir unidades, janela, origem, frescor e comportamento fail-closed com dados observados |
| Risk por trade de R$ 1 | Ainda sem campo implementado | Criar requisito e código que limitem a perda estimada de entrada/saída, fees e slippage, com teste de rejeição |

Nenhuma perda limite garante o resultado efetivo de uma ordem: gaps, liquidez, latência, execução parcial, indisponibilidade da exchange e slippage podem exceder o orçamento nominal. Excesso ou incerteza bloqueia entradas e exige reconciliação; não permite aumentar o limite nem reiniciar automaticamente.

## Aprovações e evidências ainda necessárias

- [ ] Owner humano, aprovador independente, aprovação segregada e ID de aprovação.
- [ ] Política concreta em formato compatível com o domínio, hash SHA-256 calculado sobre conteúdo canônico e validade UTC exata após aprovação.
- [ ] Conversão BRL/USDT definida e demonstrada com fonte, instante, frescor, arredondamento e comportamento de falha.
- [ ] Saldo Spot Testnet livre e suficiente, filtros atuais do símbolo, snapshot de mercado, comissão integral e reserva de taxas preservados como evidência redigida.
- [ ] Tratamento de perda máxima por trade e posição única implementado e testado, ou formalmente removido do escopo por decisão humana registrada.
- [ ] Confirmação de conta Testnet segregada, `canTrade`, retiradas desabilitadas e ausência de ordens desconhecidas.
- [ ] Testes no commit candidato, revisão independente e aprovação humana específica de Testnet antes de qualquer mutação.

**Decisão atual:** No-Go. Manter `PAPER` + `NO_TRADE=true`. Não preencher hash, aprovador ou validade executável com dados fictícios para satisfazer o validator.
