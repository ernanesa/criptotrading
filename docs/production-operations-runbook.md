# Runbook operacional local

Este procedimento cobre apenas a composição local em Docker. A implantação começa nesta máquina; uma etapa posterior em Oracle Cloud Infrastructure (OCI) exige decisões de rede, dados, custos, acesso, backup e recuperação registradas em [Plano de prontidão para produção — 2026-10-08](production-readiness-plan-2026-10-08.md). Não habilite ordens reais ou mutáveis seguindo este documento.

## Invariantes de operação

- Mantenha `WORKER_EXECUTION_MODE=PAPER`, `WORKER_NO_TRADE=true`, `WORKER_AUTONOMY_ENABLED=false`, `WORKER_TESTNET_ORDER_EXECUTION_ENABLED=false` e `BINANCE_WITHDRAWALS_ENABLED=false`.
- BUY e SELL ficam bloqueados quando KillSwitch/SafeMode estão ativos. Runtime não Ready ou policy inválida também impedem envio. Isso não afirma que stops automáticos estejam habilitados; este runbook não habilita execução.
- Não coloque chaves, senhas, token do proxy, payloads privados ou conteúdo de `.env` em comandos registrados, logs, tickets ou este arquivo. Injete segredos no processo de forma segura; nunca os imprima.
- Portas publicadas pela composição são loopback. O Grafana local permite Viewer anônimo; mantenha a porta em loopback e não exponha o painel por proxy ou rede externa.

## Preparar a sessão

Execute os comandos a partir da raiz do repositório. Exporte `LOCAL_UID=$(id -u)` e `LOCAL_GID=$(id -g)` para alinhar os usuários dos containers ao proprietário do arquivo privado. Forneça `POSTGRES_PASSWORD` por um mecanismo local seguro, sem eco ou gravação em histórico. O Compose também aceita os valores não secretos `POSTGRES_DB`, `POSTGRES_USER`, `LOCAL_UID`, `LOCAL_GID` e `APP_COMPOSE_PROJECT_NAME`.

Crie o token interno usado pelo proxy Web/API e exporte apenas o caminho do arquivo:

```bash
bash scripts/init-proxy-secret.sh
export API_INTERNAL_ACCESS_TOKEN_SOURCE="$PWD/.local-secrets/api_internal_access_token"
export LOCAL_UID="$(id -u)"
export LOCAL_GID="$(id -g)"
```

O script cria o diretório com modo `0700`, arquivo com modo `0600`, valida o UID local e recusa substituir um caminho simbólico. O valor do token não é exibido. Se usar outro diretório local, defina `API_INTERNAL_ACCESS_TOKEN_DIRECTORY` ao executar o script e ajuste `API_INTERNAL_ACCESS_TOKEN_SOURCE` para esse caminho.

## Iniciar serviços

1. Suba primeiro a stack de observabilidade para criar a rede externa nomeada `cryptotrading-observability`:

   ```bash
   docker compose --project-directory "$PWD" --file "$PWD/compose.observability.yaml" up -d
   ```

2. Inicie a aplicação com configuração PAPER/NO_TRADE e persistência/migrations deliberadamente configuradas para o ambiente local:

   ```bash
   export WORKER_EXECUTION_MODE=PAPER
   export WORKER_NO_TRADE=true
   export WORKER_AUTONOMY_ENABLED=false
   export WORKER_ACCOUNT_STREAM_ENABLED=false
   export WORKER_RECONCILIATION_ENABLED=false
   export WORKER_RISK_POLICY_REQUIRED=false
   export WORKER_PERSISTENCE_ENABLED=true
   export WORKER_APPLY_MIGRATIONS=true
   export WORKER_TESTNET_ORDER_EXECUTION_ENABLED=false
   export BINANCE_WITHDRAWALS_ENABLED=false
   docker compose --profile all up -d --build
   ```

   O perfil inicia PostgreSQL, API, Worker e Web. Confirme o estado com `docker compose --profile all ps` e consulte readiness/logs sem imprimir variáveis de ambiente. `TRADING_ENVIRONMENT` da API tem default `TESTNET`; isso não habilita envio de ordens. Não defina flags de execução mutável como parte deste runbook.

3. Depois que API, Worker e Web estiverem ativos, configure a coleta de logs por IDs de containers. O script seleciona somente os serviços e o projeto configurado, sem ler variáveis de ambiente ou payloads:

   ```bash
   bash scripts/start-observability.sh
   ```

   Repita o script sempre que recriar os containers da aplicação. O Collector usa os IDs para incluir apenas os logs desses containers. Prometheus, Alertmanager, Grafana, Loki, Tempo e endpoints do Collector permanecem publicados em loopback.

## Coleta, retenção e auditoria

- Logs de container usam driver `json-file`, rotação padrão de **10 MiB por arquivo** e **5 arquivos** por container. Ajustes locais são `CONTAINER_LOG_MAX_SIZE` e `CONTAINER_LOG_MAX_FILES`.
- Loki usa retenção padrão de **336 horas (14 dias)**. Ajuste `LOKI_RETENTION_PERIOD` apenas conforme política local aprovada; os dados residem em volumes Docker nomeados.
- A coleta Docker é somente leitura e não monta o socket do Docker. Labels limitados ao nome do projeto/serviço são coletados; variáveis de ambiente não são incluídas como atributos.
- O Worker mantém eventos de auditoria e alertas JSONL em `/var/lib/cryptotrading/audit`, no volume `cryptotrading-worker-audit`. Preserve esse volume durante `stop`, recriação e atualização. Arquivos são criados com permissões restritas em sistemas Unix.
- Os arquivos JSONL do Worker são evidência de auditoria durável e não têm rotação automática configurada. Não apague nem compacte os originais por conta própria. Até uma política de retenção ser aprovada, monitore o espaço livre e arquive cópias protegidas com manifesto/hash; decidir janela, armazenamento, acesso e procedimento de restauração continua pendente. A retenção local em Loki não governa esses arquivos.
- Investigue por `correlationId`/`X-Correlation-Id`, serviço, timestamp UTC, severidade e tipo do evento. Use IDs de mensagem, ordem e policy para correlacionar incidentes no ambiente privado; redija identificadores de conta e dados pessoais antes de compartilhar evidência. Nunca inclua credenciais, headers de autenticação ou payload assinado em buscas ou labels.
- O capital inicial pode permanecer `NULL` até existir ingestão auditável de cash flows. Não interprete esse estado como zero nem apresente retorno percentual como conhecido.
- A configuração liga Prometheus ao Alertmanager local e define uma rota `local-only`, sem e-mail, webhook, pager ou destino OCI. A UI `http://127.0.0.1:9093` só estará disponível quando os serviços estiverem ativos; recebimento/visualização de alertas não foi testado. O Alertmanager permanece publicado somente em loopback. Nenhum alerta sai desta máquina e nenhum envio externo/on-call foi configurado ou verificado.

## Backup e recuperação

O backup/restore local não tem agenda automática configurada neste repositório. Defina antes do uso persistente local o caminho protegido, criptografia, controle de acesso, frequência e retenção; monitore que a cópia terminou e faça restore periódico em volume isolado. Os valores de RPO/RTO seguem pendentes de decisão e medição. A rotação de logs não é backup de banco.

Para um dump local protegido, defina `BACKUP_FILE` para um caminho fora do Git e execute com `umask 077`. O processo de PostgreSQL acessa suas próprias variáveis; não copie a senha para a linha de comando:

```bash
umask 077
docker compose exec -T postgres pg_dump \
  -U "${POSTGRES_USER:-cryptotrading}" \
  -d "${POSTGRES_DB:-cryptotrading}" \
  --format=custom > "$BACKUP_FILE"
sha256sum "$BACKUP_FILE"
```

Guarde o hash junto ao artefato em armazenamento protegido. O [relatório corrigido de RPO/RTO local](evidence/postgres-recovery-2026-10-07.md) compara 35 linhas em 36 tabelas e informa tempos de backup/restore da fixture. O relatório anterior de 1 tabela e 0 linhas está superseded e não prova cobertura financeira. Mesmo a evidência corrigida é local, sem garantia de RPO/RTO de produção. `scripts/test-postgres-backup-restore.sh` é um drill descartável com dados sintéticos; ele não é rotina de backup do banco operacional.

No caminho para OCI, decida entre PostgreSQL gerenciado e PostgreSQL operado pela aplicação antes de criar recursos. A documentação da Oracle informa que os endpoints do [OCI Database with PostgreSQL](https://docs.oracle.com/en-us/iaas/Content/postgresql/connect-to-db.htm) não são acessíveis diretamente pela Internet e requerem VCN/sub-rede privada. A documentação também descreve backups de volumes de Compute como snapshots crash-consistent, que não substituem um backup lógico consistente do PostgreSQL ([Boot Volume Backups](https://docs.oracle.com/en-us/iaas/Content/Block/Concepts/bootvolumebackups.htm)). Trate esses serviços apenas como opções para avaliação: tenancy, região, arquitetura, orçamento, políticas IAM, destino offsite, agenda e retenção ainda não foram escolhidos nem aprovados.

Para uma recuperação local, primeiro pare as aplicações e mantenha os volumes originais preservados:

```bash
docker compose --profile all stop cryptotrading-worker cryptotrading-api cryptotrading-web
```

Verifique checksum e restaure em banco/volume isolado, nunca sobre o banco ativo como primeiro passo. Compare contagens e hashes do estado financeiro e valide migrations, ledger, fills, ordens, projeções, decisões, checkpoints, policies, modelos e rewards. Registre horário UTC, origem do backup, checksum e tempos de recuperação sem registrar dados privados. Este repositório ainda não fornece procedimento de cutover para destino de produção nem RPO/RTO aprovado para esse destino.

Após validação e decisão operacional fora deste runbook, reinicie o conjunto local:

```bash
docker compose --profile all up -d
bash scripts/start-observability.sh
docker compose --profile all ps
```

Confirme que a prontidão está saudável e que o Worker continua PAPER/NO_TRADE. Um restart que não restaure checkpoint deve permanecer fail-closed. Investigue estado degradado antes de retomar qualquer processamento.

## Parada normal

Pare primeiro o Worker e interfaces, depois PostgreSQL e observabilidade. `stop` preserva os volumes nomeados:

```bash
docker compose --profile all stop cryptotrading-worker cryptotrading-api cryptotrading-web
docker compose stop postgres
docker compose --project-directory "$PWD" --file "$PWD/compose.observability.yaml" stop
```

Não use `docker compose down -v` em um ambiente com estado que precise ser preservado. O destino dos volumes e a restauração externa ainda precisam de procedimento próprio.

No Grafana/Explore, selecione Loki e use `{service_name="cryptotrading-docker"} |= "unknown order outcome"` ou filtre pelo `message_id`/`correlation_id` conhecido. Esse seletor foi consultado no smoke local; o conteúdo é JSON Docker contendo o evento JSON da aplicação. Stack traces de falhas ficam em `error_stack`, sem serialização de `exception.Message`.

## Evidência do gate e integridade de migrations

O workflow `.github/workflows/quality.yml` configura jobs para solução .NET, Web, imagens, smoke, scans, SBOM/proveniência e recuperação. Isso comprova a configuração versionada; os resultados de uma execução remota precisam ser associados ao commit e preservados como artefato antes de serem chamados de aprovados. O audit anterior que dizia que esses jobs não existiam está superseded pelo workflow atual; snapshots históricos permanecem como histórico.

O run `37862230794` de `62687c9`, consultado em 2026-10-08, não aprovou o gate. Web lint/test/build e dependency scan passaram; .NET restore/build/test/format passaram, mas inicialização do Worker com PostgreSQL efêmero falhou, então API smoke e recuperação foram ignorados. O job de secret scan falhou no teste read-only preflight; o scan de segredos em si foi falso verde porque `rg` estava ausente e o script não distinguia “sem match” de erro do scanner. No working tree o script agora falha fechado sem ripgrep, CI instala `ripgrep`, e há teste de contrato da ausência da ferramenta. Cada scanner de infra encontrou CVEs High/Critical fixáveis; os pacotes, versões corretivas e tags upstream a investigar estão em [plano de prontidão](production-readiness-plan-2026-10-08.md). O job de build/scan/SBOM das imagens da aplicação foi ignorado por dependência do job .NET. Esse run precede as correções e não é evidência do estado atual.

O arquivo `trading.schema_migrations` registra versões aplicadas. Um contrato de hash imutável por arquivo SQL e a rejeição de gaps precisam ser comprovados pelo runner/migration checker; enquanto isso não estiver evidenciado, não altere migrations já aplicadas e não trate rollback `.down.sql` como deploy seguro. Consulte [persistência e migrations](persistence-migrations.md).
