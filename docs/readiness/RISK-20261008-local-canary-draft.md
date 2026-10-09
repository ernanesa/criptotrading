# RiskPolicy proposta — canary local / Spot Testnet

**Status:** DRAFT / NO-GO. Este documento propõe limites para revisão; não é uma `RiskPolicy` executável, aprovação, autorização de ordem nem autorização de capital real.

**As of:** 2026-10-08. **Base observada:** `62687c9285967a89ae99c74b7e36abf52605bdfe`; árvore de trabalho alterada e sem commit candidato congelado. **Owner / criador humano:** pendente. **Aprovador independente:** pendente. **Validade proposta:** 7 dias corridos após aprovação; a data UTC exata fica pendente até a aprovação. **Válida até:** PENDENTE. **Version:** PENDENTE. **SHA-256:** PENDENTE; hash canônico não calculado e nenhum valor é inventado para este rascunho.

## Proposta de limites humanos

Capital informado para planejamento: **R$ 100**, sem transferência ou uso de capital real nesta etapa.

| Limite proposto | Valor e unidade | Janela / efeito | Estado |
|---|---|---|---|
| Perda realizada diária | R$ 2 (2% dos R$ 100 iniciais) | Dia UTC; bloquear novas entradas e acionar `SafeMode`/parada até revisão humana | Proposto; sem conversão executável para USDT |
| Perda absoluta acumulada | R$ 5 (5% dos R$ 100 iniciais) | Acumulada desde o capital inicial; parada permanente até revisão humana, sem reset automático | Proposto; sem conversão executável para USDT |
| Risco máximo por trade | R$ 1 (1% dos R$ 100 iniciais) | Por trade, incluindo taxas e slippage; perda deve ser limitada antes da entrada | Proposta opcional, **não suportada como campo explícito pelo `RiskPolicy` atual**; requer requisito, implementação e teste antes de servir como gate |
| Posições Spot | 1 | Uma posição aberta no máximo; sem margem, empréstimo ou Futures | Proposto; bloqueia enquanto posição ou ordem desconhecida não estiver reconciliada |
| Retiradas | Desabilitadas | Todos os ambientes cobertos pela política | Obrigatório |
| Símbolo candidato | `BTCUSDT` | Somente após validar filtros, liquidez, comissão e dados correntes | Candidato de estudo; ainda não aprovado |

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
