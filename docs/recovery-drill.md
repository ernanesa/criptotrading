# Recovery drill local

Procedimento para provar recuperação sem capital e sem chaves privadas.

## Escopo

Validar migrations, journal/ledger append-only, outbox lease/retry,
deduplicação e reconstrução após reinício do PostgreSQL/Worker.

## Reconstrução determinística local

`LedgerReconstructor.Rebuild` reconstitui a projeção somente a partir das
entradas do journal, ordenadas por `OccurredAt` e `LedgerEntryId`. O resultado
registra quantidade de entradas, último timestamp e duração do rebuild; essa
última medida é a evidência local de RTO do componente de projeção. O journal
permanece imutável e duplicatas de uma mesma entrada não geram efeito duplicado.

Isso não substitui a medição operacional completa de RPO/RTO do PostgreSQL,
backup, restore e alertas externos.

## Passos

1. Executar `./scripts/test-postgres-integration.sh` e guardar saída redigida.
2. Interromper o processo após criar uma mensagem outbox, antes do despacho.
3. Reiniciar o processo com o mesmo banco e verificar que a mensagem reaparece
   após expiração do lease, sem duplicar o evento de ledger.
4. Simular uma falha permanente e confirmar incremento de retry/dead-letter
   conforme a política, sem apagar o registro original.
5. Comparar snapshot reconstruído com o ledger esperado e executar a
   reconciliação read-only.

Para validar o ciclo físico de backup sem tocar no volume da aplicação, execute:

```bash
./scripts/test-postgres-backup-restore.sh
```

O script usa um projeto, volume, banco de restauração e arquivo temporários
gerados pelo próprio processo; ao finalizar, remove somente esses recursos.
Além da verificação do marcador, ele mede duração de backup, restore (RTO do
drill), verificação e tempo total. O relatório redigido é gravado em
`artifacts/recovery/postgres-rpo-rto-<timestamp>-<pid>.md` com permissão `600`.
Antes do restore, o dump recebe SHA-256 e o checksum é verificado; uma cópia
adulterada é testada e rejeitada sem ser restaurada. O checksum do artefato
válido e o resultado do probe de corrupção ficam registrados no relatório.
O resultado de RPO é limitado ao marcador confirmado antes do dump: zero linhas
perdidas neste cenário determinístico não constitui garantia de RPO de produção.

Para executar o drill determinístico de soak/chaos somente em memória, execute:

```bash
./scripts/test-paper-soak-chaos.sh
```

Esse cenário cria 64 mensagens Paper, tenta novamente entregas duplicadas,
simula uma falha transitória antes da aceitação, uma queda do worker depois da
aceitação e novos owners após a expiração do lease. O `PaperExchange` recebe a
mesma chave de idempotência na recuperação e prova que há 64 side effects para
64 ordens, apesar das falhas e reinícios simulados. O processo remove as
variáveis `BINANCE_API_KEY` e `BINANCE_API_SECRET` do ambiente do teste; não há
rede, PostgreSQL, credencial ou dinheiro real envolvido.

Para executar a reconstrução determinística do ledger e produzir uma evidência
redigida, execute:

```bash
./scripts/test-ledger-recovery.sh
```

O runner seleciona somente `Category=RecoveryDrill`, recusa credenciais
Binance, não acessa rede ou PostgreSQL e grava o relatório em
`artifacts/recovery/`. Qualquer falha do teste faz o comando falhar fechado.

Para executar o gate composto dos componentes locais de recuperação, execute:

```bash
./scripts/test-full-recovery-drill.sh
```

Esse runner executa os drills de ledger, persistência/restart e
backup/restore em sequência, agrega apenas status e duração em
`artifacts/recovery/full-recovery-<timestamp>-<pid>.md` e não copia stdout ou
stderr dos filhos para o relatório. O gate composto continua limitado às
fixtures locais; não prova RPO/RTO de produção, recuperação autenticada da
exchange ou estado ausente dos fixtures.

## Critérios de aprovação

- nenhum evento financeiro duplicado;
- nenhum registro imutável alterado;
- mensagens pendentes recuperáveis após reinício;
- divergência entra em SafeMode;
- logs contêm correlation id e não contêm segredos;
- resultado associado ao commit, migration version e dataset hash.

O drill físico local de PostgreSQL/RPO/RTO agora possui medição reproduzível; a
definição de RPO/RTO de produção, retenção, restauração do volume real e
reconstrução completa de estado continuam fora deste cenário.
