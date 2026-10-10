# Evidência de validação de produção — snapshot 2026-10-09

> **Snapshot histórico superseded para o estado atual em 2026-10-10.** A indicação abaixo de árvore limpa descreve somente a observação feita em 09/10; hoje a árvore fc85321 tem alterações locais. Consulte [production-validation-2026-10-10.md](production-validation-2026-10-10.md) para o estado corrente. Os registros datados permanecem como evidência histórica, sem vínculo automático com a árvore atual.

**Estado: NO-GO para Live, Testnet mutável, OCI e promoção.** Este registro separa verificações feitas na árvore atual das evidências intermediárias e de resultados reportados pelo orquestrador. Nenhum comando de build, teste ou scan foi executado durante a preparação deste documento.

## Escopo e estado do repositório

- HEAD observado: `fc85321a7e6f4afe3d7b73468c316487ef00df2d` (`ad 5`). O comando `git status --short` não listou alterações; o daemon fsmonitor retornou erro de IPC, então o estado foi confirmado com `git -c core.fsmonitor=false status --short`.
- A revisão não inicia serviços, envia ordens, acessa credenciais, altera imagens ou contata canais externos.
- O plano em [production-readiness-plan-2026-10-08.md](production-readiness-plan-2026-10-08.md) é um plano anterior, com base `62687c9`; não é evidência de validação da HEAD atual. O relatório [production-validation-2026-10-07.md](production-validation-2026-10-07.md) também é de outra base (`7419aa7`).

## Evidências locais e limites

| Evidência | Resultado | Limite / vínculo |
|---|---|---|
| Estado Git | HEAD acima; árvore sem alterações listadas | Confirmado localmente em 2026-10-09; fsmonitor falhou, status foi repetido sem fsmonitor. |
| Launcher .NET | `bash scripts/dotnet-local.sh --version` → `11.0.100-rc.1.26425.128` | Smoke reportado pelo orquestrador; prova resolução do SDK local, não build nem testes da HEAD atual. A exceção RC está documentada em [production-toolchain-exception-2026-10-08.md](production-toolchain-exception-2026-10-08.md). |
| Diagnósticos offline | `bash scripts/test-binance-readonly-preflight-diagnostics.sh` e `bash scripts/test-full-recovery-diagnostics.sh` passaram | Resultados reportados pelo orquestrador em 2026-10-09 após correções atuais; usam fixtures sintéticas, sem Docker, venue, rede ou credenciais. Não provam restore real nem preflight autenticado. |
| Secret scanner | `bash scripts/scan-secrets.sh` passou no freeze final local em 2026-10-09 | Resultado reportado pelo orquestrador; o scanner de segredos não valida segurança financeira nem CI remoto. Os testes `bash scripts/test-secret-scan-tooling.sh` e `bash scripts/test-binance-readonly-preflight-diagnostics.sh` passaram em execução anterior, não repetida neste registro. |
| Compose observabilidade | `env -u BINANCE_API_KEY -u BINANCE_API_SECRET -u POSTGRES_CONNECTION_STRING POSTGRES_PASSWORD=validation-only API_INTERNAL_ACCESS_TOKEN_SOURCE=/dev/null LOCAL_UID=1000 LOCAL_GID=1000 docker compose --env-file /dev/null --project-directory "$PWD" --file "$PWD/compose.yaml" --file "$PWD/compose.observability.yaml" config --quiet` passou em 2026-10-09 | Resultado reportado pelo orquestrador após os novos pins. Os valores são placeholders sem credenciais; valida somente o render Compose e não inicia serviços, redes ou volumes. |
| Alertmanager | `docker run --rm --network none --read-only --cap-drop ALL --security-opt no-new-privileges --entrypoint /bin/amtool -v /home/ernane/Projetos/criptotrading/ops/observability/alertmanager.yml:/tmp/alertmanager.yml:ro prom/alertmanager:v0.34.1@sha256:e9733bafb1bdef9b00e25a21f8f99dc26a22224bf16641ad754d1649f4c3357a check-config /tmp/alertmanager.yml` passou | Reportado pelo orquestrador: uma rota/receiver `local-only`, zero templates. Não verifica envio, recepção, UI, on-call ou entrega externa. |
| PostgreSQL integration | `COMPOSE_PROJECT_NAME=cryptotrading-readiness-20261008 POSTGRES_TEST_PORT=55441 POSTGRES_VOLUME_NAME=cryptotrading-readiness-20261008 bash scripts/test-postgres-integration.sh` passou: 31 testes, 3,3 s | Reportado pelo orquestrador em árvore intermediária; containers removidos, volume nomeado preservado. Não é evidência de HEAD atual. |
| Full recovery agregado | `RECOVERY_REPORT_DIR=/tmp/cryptotrading-final-recovery-20261009 COMPOSE_PROJECT_NAME=cryptotrading-final-recovery-it-20261009 POSTGRES_TEST_PORT=55463 POSTGRES_VOLUME_NAME=cryptotrading-final-recovery-it-20261009 env -u POSTGRES_TEST_PASSWORD -u POSTGRES_CONNECTION_STRING -u BINANCE_API_KEY -u BINANCE_API_SECRET bash scripts/test-full-recovery-drill.sh` passou em 2026-10-09 09:05 UTC | Relatório local `/tmp/cryptotrading-final-recovery-20261009/full-recovery-20261009T090531Z-3Iu9mj.md`: ledger 1.593 ms, full-state manifest 1.642 ms, integração Postgres 13.433 ms e backup/restore 16.709 ms. O agregado não retém stdout dos filhos, portanto não registra contagem unitária de testes da integração. Volume `cryptotrading-final-recovery-it-20261009` permanece pelo contrato do integration harness; volume de backup/restore foi removido pelo filho. Nenhum Binance credential/call. |
| Backup/restore do agregado | `scripts/test-postgres-backup-restore.sh` como etapa do full recovery passou: 117.338 bytes; SHA-256 validado e cópia corrompida rejeitada; backup 189 ms, restore probe 492 ms, verificação 3.576 ms; 36 rows em 36 tabelas com hashes iguais | Evidência local em `artifacts/recovery/postgres-rpo-rto-20261009T090548Z-3122500.md`; fixture determinística, não RPO/RTO de serviço. |
| Worker persistence startup/restart | `COMPOSE_PROJECT_NAME=cryptotrading-final-worker-20261009 POSTGRES_TEST_PORT=55462 POSTGRES_VOLUME_NAME=cryptotrading-final-worker-20261009 env -u POSTGRES_TEST_PASSWORD -u POSTGRES_CONNECTION_STRING -u BINANCE_API_KEY -u BINANCE_API_SECRET bash scripts/test-worker-persistence-startup.sh` passou: PAPER/NO_TRADE, migrations, checkpoint fail-closed após restart e audit sink; exit codes `124/124` | Reportado pelo orquestrador em 2026-10-09. Cada host foi encerrado pelo timeout do harness após 12 s; nenhum adapter autenticado ou ordem foi habilitado. Volume isolado é removido no cleanup do próprio harness. |
| Solution tests | `DOTNET_ROOT=/home/ernane/Projetos/criptotrading/.tools/dotnet PATH=/home/ernane/Projetos/criptotrading/.tools/dotnet:$PATH env -u POSTGRES_CONNECTION_STRING ./.tools/dotnet/dotnet test CryptoTrading.slnx --no-build --configuration Release --verbosity minimal` passou: 646 testes, 2,611 s | Reportado pelo orquestrador antes das últimas alterações integradas; não vinculado à HEAD atual. |
| Build intermediário | `DOTNET_CLI_USE_MSBUILD_SERVER=0 bash scripts/dotnet-local.sh build CryptoTrading.slnx --no-restore --configuration Release --disable-build-servers -m:1 -p:UseSharedCompilation=false` passou: zero warnings e erros, 15,64 s | Resultado anterior às últimas alterações na guarda financeira; superseded pelo build final abaixo. |
| Build final | `DOTNET_CLI_USE_MSBUILD_SERVER=0 bash scripts/dotnet-local.sh build CryptoTrading.slnx --no-restore --configuration Release --disable-build-servers -m:1 -p:UseSharedCompilation=false` passou: zero warnings e erros, 8,23 s | Resultado reportado pelo orquestrador após freeze final dos writers, árvore dirty derivada do HEAD acima. |
| Suite .NET final | `env -u POSTGRES_CONNECTION_STRING bash scripts/dotnet-local.sh test CryptoTrading.slnx --no-build --configuration Release --verbosity minimal` passou: 673/673, 0 falhas, 0 skips, 11 assemblies, 4,545 ms; exit 0 | Resumo local foi lido pelo orquestrador após freeze final. A cobertura de expiração de ticker ainda espera aproximadamente 3 s de relógio real; não foi comprovada como determinística por fake clock. |
| Format verification final | Passou, exit 0 | Resultado reportado pelo orquestrador depois da correção de whitespace; execução ligada ao freeze final. |
| Readiness validator | Os documentos de readiness citados no plano retornaram exit 1 esperado por decisões, validade, limites, aprovações e placeholders ausentes | Resultado reportado pelo orquestrador em 2026-10-08 para artefatos antigos. Isso mantém NO-GO; não foi repetido contra esta HEAD. |

## Scan da imagem PostgreSQL

O scan isolado de `postgres:18.6-alpine3.24@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873` terminou com exit 1, como configurado para bloquear findings. O relatório local `/tmp/cryptotrading-trivy-reports/postgres.json` contém **22 CVEs únicos: 1 Critical e 21 High**, classificados como corrigíveis pelo Trivy. Todos apontam para `stdlib` Go `v1.24.6`, sem caminho de pacote reportado; o achado anteriormente descrito como “gosu” está associado ao binário `/usr/local/bin/gosu`, não a uma vulnerabilidade do pacote Alpine `gosu`.

O conjunto inclui `CVE-2025-68121` (Critical) e os seguintes High: `CVE-2025-61726`, `CVE-2025-61729`, `CVE-2026-25679`, `CVE-2026-27145`, `CVE-2026-32280`, `CVE-2026-32281`, `CVE-2026-32283`, `CVE-2026-33811`, `CVE-2026-33814`, `CVE-2026-33818`, `CVE-2026-39820`, `CVE-2026-39821`, `CVE-2026-39822`, `CVE-2026-39836`, `CVE-2026-42499`, `CVE-2026-42504`, `CVE-2026-56853`, `CVE-2026-56858`, `CVE-2026-56859`, `CVE-2026-56860` e `CVE-2026-56862`. As versões corrigidas variam por advisory; algumas exigem Go `1.26.6` e outras têm correção estável anterior. Isso não identifica por si só uma tag de imagem corrigida.

Comando reportado pelo orquestrador:

```bash
docker run --rm --user "$(id -u):$(id -g)" --cap-drop ALL --security-opt no-new-privileges \
  -v /tmp/cryptotrading-trivy-cache:/cache \
  -v /tmp/cryptotrading-trivy-reports:/reports \
  aquasec/trivy:0.70.0@sha256:be1190afcb28352bfddc4ddeb71470835d16462af68d310f9f4bca710961a41e \
  image --cache-dir /cache --cache-backend memory --skip-db-update --image-src remote \
  --scanners vuln --severity HIGH,CRITICAL --format json \
  --output /reports/postgres.json --exit-code 1 --timeout 15m --no-progress \
  postgres:18.6-alpine3.24@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873
```

O scan usou o cache e a base Trivy já disponível (`--skip-db-update`); a idade dessa base precisa ser registrada antes de tratar o resultado como atual. O digest PostgreSQL é o mesmo da imagem fixada atualmente, então essa execução não demonstra correção. Não se deve relaxar o gate nem alterar o pin sem novo scan e validação de compatibilidade.

As primeiras tentativas para seis candidatos foram **rejeitadas pela revisão automática por limite de uso**. Isso não foi uma decisão de segurança nem um resultado de scan. Depois, com a retomada da autorização, os seis scans foram executados em 2026-10-09 às 05:44 UTC. Resultado extraído dos JSONs locais em `/tmp/cryptotrading-trivy-reports/`:

| Imagem candidata | High/Critical | Findings corrigíveis | Findings sem correção | Decisão local |
|---|---:|---:|---:|---|
| Loki 3.7.8 | 0 | 0 | 0 | Pin atualizado em Compose e matrix do CI; validação de configuração e execução continuam pendentes. |
| Tempo 3.1.0 | 0 | 0 | 0 | Pin atualizado em Compose e matrix do CI; configuração usa filesystem local, mas o novo formato padrão e compatibilidade precisam ser validados. |
| Prometheus 3.15.0 | 0 | 0 | 0 | Pin atualizado em Compose e matrix do CI; validação de configuração continua pendente. |
| OpenTelemetry Collector Contrib 0.162.0 | 0 | 0 | 0 | Pin atualizado em Compose e matrix do CI; validação da configuração continua pendente. |
| Grafana 13.2.3 | 8 entradas / 4 CVEs únicos | 8 | 0 | Pin mantido; não passa o gate. Os findings estão em plugins Go bundled: grpc `v1.83.0/v1.83.1` (`CVE-2026-84304`, `CVE-2026-84445`) e plugin Tempo `v1.5.1-0.20260910130453-bcfe9f230c1d` (`CVE-2026-21728`, `CVE-2026-28377`). |
| Alertmanager 0.34.1 | 2 entradas / 1 CVE único | 2 | 0 | Pin mantido; `CVE-2026-84445` afeta grpc `v1.83.1` nos binários `alertmanager` e `amtool`. O [projeto publica 0.34.1 como latest](https://github.com/prometheus/alertmanager/releases); não foi encontrada release posterior oficial para substituir o pin. |
| PostgreSQL 18.6 Alpine | 22 CVEs únicos | 22 | 0 | Pin mantido; Go stdlib `v1.24.6` associado ao binário `/usr/local/bin/gosu`. |
| PostgreSQL 18.6 Bookworm (candidato) | 88 entradas / 45 CVEs únicos | 22 | 66 | Não pinado. Além dos mesmos 22 CVEs corrigíveis de Go stdlib v1.24.6 no `/usr/local/bin/gosu`, o relatório marca 66 entradas High/Critical de pacotes Debian como sem correção. |

O digest multi-platform reportado como verificado pelo orquestrador para cada candidato é:

| Componente | Tag candidata | Digest |
|---|---|---|
| Grafana | `13.2.3` | `sha256:b28bae15e219c998fb0e0424ed724930cc61b1f61fb404d47c862f9a23f9e572` |
| Loki | `3.7.8` | `sha256:1107dd5274e0ada47e42472b7a7e71f3b2a2fe878878108f3e2f9e51528f0193` |
| Tempo | `3.1.0` | `sha256:3076b8dcdfb32fd6bc5ccef85e7b7313e6199b9cb84366257fc17ecb696db5fd` |
| Prometheus | `3.15.0` | `sha256:efd719c99d83b060d9daefdcf00360461adf279f45ef5391f8d111892118753e` |
| OpenTelemetry Collector Contrib | `0.162.0` | `sha256:39923a8e431bd1f57be82411999d389fcfe40857492e4365456d97a4c1f74be6` |
| PostgreSQL | `18.6-alpine3.24` | `sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873` (digest atual; sem correção demonstrada) |
| PostgreSQL Debian | `18.6-bookworm` | `sha256:afc7e2d441324c0388fa80c3d24f733b4194a4eb7f47dd8ee2b08eb1a24a647c` (candidato rejeitado pelo gate atual) |

Os digests foram reportados como verificados em fonte oficial pelo orquestrador. Os quatro resultados limpos permitiram atualizar os pins de Loki, Tempo, Prometheus e OTel; isto não significa que os novos serviços iniciaram nem que as configurações foram aprovadas. Os validadores em containers isolados passaram para OTel, Loki, Prometheus e Tempo. O primeiro comando de Tempo sem valor booleano explícito (`--config.verify`) falhou ao analisar a flag; a repetição com `--config.verify=true` passou. A configuração local do Loki já usa TSDB/schema v13, exigido para structured metadata no Loki 3; a configuração local do Tempo usa storage local e não configura Redis. As notas oficiais alertam que Tempo 3.1 escreve vParquet5 por padrão, remove suporte a vParquet3 e muda chaves do Redis ([release notes 3.1](https://grafana.com/docs/tempo/latest/release-notes/v3-1/)); a compatibilidade com dados já persistidos segue não verificada. Loki 3.x exige TSDB/schema v13 para structured metadata ([guia de upgrade](https://grafana.com/docs/loki/latest/setup/upgrade/)). Grafana e Alertmanager continuam bloqueados por findings High corrigíveis. A imagem PostgreSQL Bookworm candidata também falhou o gate: 66 entradas/23 CVEs únicos de pacotes Debian foram marcadas sem correção pelo Trivy, além das 22 entradas de stdlib Go corrigíveis. Nenhuma variante PostgreSQL foi atualizada.

Validadores executados pelo orquestrador sem iniciar os serviços, usando os arquivos montados nos mesmos caminhos de runtime: OTel Collector `validate --config=/etc/otelcol-contrib/config.yaml`; Loki `-config.file=/etc/loki/config.yaml -config.expand-env=true -verify-config=true` com `LOKI_RETENTION_PERIOD=336h`; Tempo `--config.file=/etc/tempo.yaml --config.verify=true`; Prometheus `promtool check config /etc/prometheus/prometheus.yml` e `promtool check rules /etc/prometheus/prometheus-alerts.yml`, montando ambos os arquivos sob `/etc/prometheus/`. Todos passaram após corrigir o argumento booleano de Tempo. Fontes oficiais: [OTel Collector config validation](https://opentelemetry.io/docs/collector/configuration/), [Loki upgrade/config validation](https://grafana.com/docs/loki/latest/setup/upgrade/), [Tempo flags](https://grafana.com/docs/tempo/latest/set-up-for-tracing/setup-tempo/command-line-flags/) e [Prometheus promtool](https://prometheus.io/docs/prometheus/3.9/command-line/promtool/). Os comandos rodaram em containers isolados, sem rede, read-only e com somente os arquivos de configuração montados read-only.

## CI remoto e readiness

O último run remoto conhecido é `37862230794`, sobre `62687c9`, e não representa a HEAD `fc85321`. O plano registra que Web e dependency scan passaram; o job .NET falhou ao iniciar Worker/PostgreSQL e deixou API smoke e recovery como skipped; o secret-scan job falhou no preflight read-only e havia um scanner que tratava `rg` ausente como ausência de findings; seis scans de imagens encontraram CVEs High/Critical; build/scan/SBOM de container foi skipped por dependência. Essas informações são histórico reportado, não status de execução do CI atual. **Workflow remoto da HEAD atual: not verified.**

Os drafts [CANARY](readiness/CANARY-20261008-local-draft.md), [RISK](readiness/RISK-20261008-local-canary-draft.md) e [TESTNET](readiness/TESTNET-20261008-execution-envelope.md) continuam como artefatos locais pendentes, derivados de uma base anterior. O plano registra Live ausente, decisões de política/validade e aprovações pendentes. Nenhum draft autoriza ordem, Testnet mutável ou OCI. A execução do validator na HEAD atual não foi feita.

## Pendências que mantêm NO-GO

- Manter o build, suite, format, integração PostgreSQL, startup/restart e recovery agregados vinculados ao mesmo SHA congelado; render Compose passou com `--env-file /dev/null` e placeholders inofensivos. Executar/inspecionar o workflow remoto ligado ao SHA candidato. Os validadores isolados atuais de OTel, Loki, Tempo e Prometheus passaram, mas não iniciaram os serviços.
- Corrigir e rescans das imagens, preservando bloqueio para High/Critical, inclusive findings sem fix; registrar DB version, digest, SBOM, findings e eventual waiver com owner/expiração antes de qualquer exceção.
- Revisão financeira compartilhada reportou riscos ainda pendentes na revalidação de política/saldo/comissão antes do envio e na reserva de taxas. Exige correção, testes focados e nova revisão independente antes de qualquer promoção.
- Decidir retenção/capacidade/auditoria de logs, backup local agendado e destino criptografado; validar restore isolado; escolher RPO/RTO por decisão documentada. Backup offsite, destino OCI, agenda, on-call e entrega externa continuam pendentes.
- Live continua indisponível; Testnet mutável ainda depende de autorização específica. OOS representativo, soak prolongado, caos/restart e evidência de venue ainda não foram executados neste ciclo e não são substituídos por simuladores ou configurações locais.
- Revalidar os artefatos de risco/canary com owner, aprovações, validade, limites, commit e evidências exatos. O plano atual propõe R$2/dia e R$5 absoluto como discussão, não política aprovada; BTCUSDT permanece candidato condicionado às regras, conversão e saldo atuais.
- Manter `NO_TRADE` e sem withdrawals; não há autorização para Live, Testnet mutável ou deploy.

## Próxima evidência exigida

Depois de fechar os riscos de código e congelar um SHA, registrar novamente status Git, build/testes PostgreSQL, readiness validators e execução remota por SHA. Para as imagens, obter scan completo com DB Trivy atualizado, correções confirmadas em fonte oficial e testes de compatibilidade. Só então atualizar o status dos gates; até lá, esta validação é **NO-GO**.
