# Recovery drill local

Procedimento para provar recuperação sem capital e sem chaves privadas.

## Escopo

Validar migrations, journal/ledger append-only, outbox lease/retry,
deduplicação e reconstrução após reinício do PostgreSQL/Worker.

## Passos

1. Executar `./scripts/test-postgres-integration.sh` e guardar saída redigida.
2. Interromper o processo após criar uma mensagem outbox, antes do despacho.
3. Reiniciar o processo com o mesmo banco e verificar que a mensagem reaparece
   após expiração do lease, sem duplicar o evento de ledger.
4. Simular uma falha permanente e confirmar incremento de retry/dead-letter
   conforme a política, sem apagar o registro original.
5. Comparar snapshot reconstruído com o ledger esperado e executar a
   reconciliação read-only.

## Critérios de aprovação

- nenhum evento financeiro duplicado;
- nenhum registro imutável alterado;
- mensagens pendentes recuperáveis após reinício;
- divergência entra em SafeMode;
- logs contêm correlation id e não contêm segredos;
- resultado associado ao commit, migration version e dataset hash.

O drill permanece pendente até haver uma execução capturada em ambiente local.
