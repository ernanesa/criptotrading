# Manifesto de reconstrução completa local

`FullStateReconstructor` é uma fronteira read-only para drills de restart e
restore. Ele recebe quatro fontes locais duráveis:

- journal financeiro, reconstruído deterministicamente em uma nova projeção de
  ledger;
- projeção de ordens abertas;
- evidências append-only do User Data Stream;
- metadados de recuperação do outbox (estado, tentativas e leases).

O manifesto ordena cada fonte por uma chave estável, valida IDs duplicados,
timestamps UTC, hashes dos eventos de conta e invariantes de ledger, e gera um
SHA-256 canônico. A mesma entrada produz o mesmo hash independentemente da
ordem em que foi lida. A operação não escreve no banco, não envia ordens e não
acessa a exchange.

O manifesto comprova a consistência das quatro fatias locais fornecidas; não
é, sozinho, evidência de RPO/RTO de produção. A projeção de ordens e as
evidências do stream são capturadas para comparação. Rebuild de estado após
restore ainda deve ser seguido por reconciliação read-only contra a exchange,
e o backup/PITR precisa ser validado separadamente.

`FullStateReconciler.Compare` compara o manifesto pré-restore com o manifesto
pós-restore sem escrever no banco. Ele verifica o hash canônico, a quantidade
de entradas aplicadas, o último timestamp do ledger, as contagens de ordens,
eventos e outbox e a presença das quatro fatias. Qualquer diferença produz
razões explícitas e `IsEquivalent=false`, mantendo o gate fail-closed.
