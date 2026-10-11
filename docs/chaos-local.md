# Chaos/soak local em PAPER/NO_TRADE

Os runners abaixo produzem evidência de cenários locais determinísticos. Eles
não carregam `.env`, recusam `BINANCE_API_KEY` e `BINANCE_API_SECRET`, não
iniciam Docker, não acessam rede e não configuram Testnet ou Live.

```bash
./scripts/test-paper-soak-chaos.sh
./scripts/test-shadow-soak.sh
```

Cada runner executa três repetições por padrão. O número pode ser ajustado entre
1 e 100 com `PAPER_CHAOS_RUNS` ou `SHADOW_SOAK_RUNS`. Se alguma repetição
falhar, o runner termina com erro após concluir as repetições configuradas. A saída durável é Markdown redigido em
`artifacts/chaos/` ou `artifacts/shadow/`, ambos ignorados pelo Git, com modo
Unix `0600`. O relatório contém somente metadados fixos do harness, janela UTC
e o resultado de cada processo; ele nunca recebe o log bruto do teste. Inclui
o commit-fonte, SHA-256 do runner e do arquivo do cenário, ID do cenário,
repetições, duração, resultado e motivo de parada. Não registra valores de
ambiente, credenciais ou saída bruta.

## Critérios de aceitação

Os relatórios descrevem o resultado técnico observado no harness. Nenhum
threshold operacional de continuar ou abortar está aceito enquanto L26 do
`PLANO_PRODUCAO_LOCAL.md` estiver pendente. Portanto, `PASS` indica apenas que
as repetições locais selecionadas terminaram sem erro; não é aprovação de
limiares nem gate de promoção.

## Matriz PAPER atual

| Injeção local | Detecção/verificação | Recuperação | Estado final exigido |
|---|---|---|---|
| Entrega duplicada do produtor | A store em memória rejeita a segunda mensagem pela mesma `IdempotencyKey` | Consome uma única entrada | Uma ordem simulada por chave |
| Falha transitória antes do aceite | Exceção `IOException` planejada | Libera lease para retry | Mensagem processada sem duplicar ordem simulada |
| Crash após aceite simulado | Lease fica sem acknowledge | Expiração do lease e replay idempotente | Uma ordem simulada, sem item claimable |
| Reinício do worker simulado | Novo owner por ciclo | Reclama entrada elegível | Todos os 64 itens processados dentro do limite |

O teste cobre somente o `PaperExchange` em memória. O campo “ordem simulada”
não é uma submissão para venue; o runner realiza zero chamadas de submit/cancel
para Binance ou Testnet.

## Fronteira e lacunas de L25

Este slice torna repetível e auditável a evidência existente de restart,
duplicação e recuperação idempotente. Fencing entre processos, timeout de
conexão/banco persistente, atraso/stale data, resultado `Unknown`, eventos
reordenados, kill-switch e pressão de storage ainda exigem injeções vinculadas
às implementações duráveis correspondentes. Um `PASS` local não prova essas
injeções, persistência real, reconciliação, Testnet, venue ou prontidão para
Canary/Live.
