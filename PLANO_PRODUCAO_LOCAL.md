# Plano de prontidão local e caminho para produção

**Estado da decisão:** NO-GO para Live, ordens reais, Testnet mutável, deploy externo e promoção. O objetivo imediato é deixar a stack local segura, recuperável e observável em `PAPER/NO_TRADE`; isso é uma etapa de desenvolvimento e operação local, não substitui o objetivo de construir e aprovar uma capability Live distinta.

**Referência de código deste plano:** `main` no commit `085ea57b165998ddb452a19c8a3e15efe6dfd427`, observado limpo e alinhado a `origin/main` em 2026-10-10. Resultado de teste só se transfere para este candidato quando sua evidência identifica exatamente esse SHA ou um fingerprint de conteúdo equivalente, verificado contra a árvore. O histórico de `docs/production-validation-2026-10-10.md` usa a árvore local anterior baseada em `fc85321` e não é, por si só, prova dos gates no SHA atual.

**Invariantes:** `NO_TRADE` por padrão, falha fechada, saques desabilitados, credenciais fora do Git e nenhuma transmissão de ordem durante este plano. Qualquer ação futura mutável exige aprovação humana específica, registrada imediatamente antes da janela e ligada a ambiente, símbolo, capital, duração, limites e SHA. Ler este documento ou completar uma etapa não concede tal aprovação.

## Estado ativo, objetivo e limites

- **Objetivo agora:** fechar os controles de uma instalação local em Linux/Docker Compose, com PostgreSQL persistente, API, Worker, Web, logs consultáveis, backup/restauração ensaiados, segurança e capacidade de parar/recuperar sem enviar ordens.
- **Objetivo final do produto:** avaliar uma capability Live de Binance Spot como projeto e gate próprios depois das evidências locais, OOS/risco, chaos/soak, CI e decisões humanas. Live não é suportado hoje segundo a auditoria registrada; `PAPER/NO_TRADE` local não satisfaz esse objetivo final.
- **Fora da Definition of Done local:** OCI/AWS/VPS, tenancy/conta Oracle, rede pública, recursos billable, DNS/proxy externo, alertas por e-mail/webhook, on-call externo, envio real de ordens ou execução mutável de Testnet. IAM Oracle é requisito de promoção para OCI, não gate da operação exclusivamente local.
- **Escopo financeiro conhecido:** planejamento com capital informado de R$100; perda diária proposta de R$2 e limite cumulativo proposto de R$5 são drafts sem aprovação, não limites de perda garantidos. Taxa, slippage, conversão BRL/USDT, gaps, execução parcial e falha de stop podem superar os valores.
- **Recuperação:** o usuário aceitou planejar em horas. RPO de 1 hora e RTO de 4 horas são valores propostos para orientar o planejamento, não resultados medidos nem aceitação de limites exatos. Exercitar a rotina para avaliar esses alvos ou ajustá-los antes de promoção.
- **SDK:** a exceção local ao .NET 11 RC foi aceita. Não adicionar migração para SDK estável como gate obrigatório deste plano. Registrar novamente versão/digest e reavaliar antes de qualquer decisão futura de OCI.
- **Próximo passo:** revisar e aceitar este artefato de planejamento; em seguida iniciar o backlog P0 restante. Os gates 1, 2, 3, 5, 7 e 13 já foram revalidados no SHA atual; repetir somente se código/configuração/SDK mudar. Fechar as lacunas dos gates 4, 6, 8–12, 14–20 conforme aplicável e atualizar a tabela quando nova evidência existir.

## Base técnica observada

O README fixa .NET 11/C# 15, desenvolvimento local com containers e independência de AWS; o MVP de pesquisa/execução é Binance Spot BTC/USDT e uma baseline Trend Following. O `compose.yaml` oferece PostgreSQL, API, Worker e Web. O Worker tem defaults `PAPER` e `WORKER_NO_TRADE=true`; as flags de envio mutável permanecem desabilitadas no runbook local. O Compose injeta configuração PostgreSQL e o token interno da API por arquivo Docker secret. `scripts/init-proxy-secret.sh` cria o token interno em diretório `0700` e arquivo `0600`. O runbook mantém endpoints publicados em loopback e instruções de coleta local.

Há separação de configuração para chaves Binance: o `.env.example` mostra campos de ambiente e campos `*_FILE`; scripts de preflight validam armazenamento protegido. A composição, porém, declara valores `BINANCE_API_KEY`/`BINANCE_API_SECRET` por variáveis de ambiente. A próxima revisão de acesso precisa escolher e testar um mecanismo file-scoped para qualquer credencial autorizada, revisar processos de container/host e confirmar que nenhum segredo aparece em logs, argv, dump, crash report ou evidência compartilhada. Não fornecer credenciais durante a fase PAPER/NO_TRADE.

O adaptador autenticado documentado é estritamente read-only e não expõe endpoints de submit/cancel/withdrawal. Isso é evidência de escopo do adapter atual, não prova de OMS Live nem de reconciliação operacional. O runbook informa que a UI Grafana local permite Viewer anônimo; manter a publicação restrita a loopback e não colocar painel atrás de proxy externo.

## Como medir cobertura e prontidão

A rubrica possui 20 gates binários, com peso uniforme de 5 pontos percentuais. A cobertura de critérios é `gates PASS com evidência válida / 20 × 100`. Parcial, `not verified`, histórico de outra árvore, fixture em lugar de integração real, FAIL ou aprovação pendente contam como gate não satisfeito. O denominador não varia quando um gate é difícil ou externo. Um subcheck aprovado pode ser anotado sem promover o gate inteiro.

Esse número mede cobertura documental/operacional dos critérios, não probabilidade de segurança, qualidade de estratégia, retorno financeiro, capacidade de suportar perdas ou autorização. **O resultado de produção Live continua NO-GO enquanto qualquer gate obrigatório, inclusive capability Live, aprovação de risco e GO humano, estiver pendente.** A conclusão local PAPER pode ser medida em conjunto próprio, sem trocar silenciosamente o objetivo Live.

Na atualização desta versão, o root reportou no candidato atual build Release em 49,49 s, sem warnings/erros; suíte com 673 passed, 0 failed, 0 skipped em 2,639 s, distribuída por 11 assemblies (Domain 217, Worker 110, Binance 89, Backtesting 93, API 32, Config 29, Reconciliation 22, Persistence 23, Observability 38, Shadow 12, Paper 8); secret scan PASS, contrato local do backup PASS e dois contratos de diagnóstico PASS. O sumário de testes foi registrado em `/tmp/cryptotrading-local-plan-tests-20261010.log`; esse caminho em `/tmp` é efêmero. O recovery integrado também terminou PASS: relatório `/tmp/cryptotrading-local-plan-recovery-20261010/full-recovery-20261010T123051Z-pNKwj8.md`, igualmente efêmero. O root reportou ainda format verification 2,223 PASS, Web 26/26 em 9 arquivos e 2,90 s, lint 28 PASS e Vite build em 938 ms. Os seis gates locais com evidência comunicada são 1, 2, 3, 5, 7 e 13: cobertura de critério 6/20 (30%), com 14/20 (70%) abertos. Secret scan, contrato manual de backup, contratos de diagnóstico, formatação/lint e build Web são evidência parcial dos gates 12, 6, 14, 18 e 1; não fecham individualmente esses gates. Esta autoria não executou nem inspecionou logs brutos; os resultados foram comunicados pelo root e se limitam ao escopo local, sem provar venue/Live.

O relatório histórico 10/10 informava 6/20 (30%) para snapshot dirty baseado em `fc85321`, com 461 arquivos e fingerprint `c58b15bc…`; seu plano e validação declaram explicitamente essa base. A cobertura atual de 6/20 coincide numericamente, mas agora está atribuída às evidências reportadas no SHA limpo `085ea57b165998ddb452a19c8a3e15efe6dfd427`, após build, suíte e recovery locais. Não transportar para o candidato atual nenhuma outra alegação daquele snapshot anterior. Os 14 gates restantes continuam abertos; esse percentual não é prontidão Live, autorização ou estimativa de retorno.

## Rubrica de 20 gates

`PASS` abaixo representa o status informado para o candidato local atual ou, quando indicado, apenas evidência de subparte. A coluna **Estado para o candidato** separa critérios fechados de subchecks e pendências. Nenhuma linha concede autorização mutável.

| # / peso | Critério de aceite | Estado e evidência disponível | Dependências e próximo artefato verificável |
|---|---|---|---|
| 1 · 5 pp | Build local Release pelo SDK fixado, sem warnings/erros, mesmo SHA do restante do bundle. | Root reportou PASS no SHA `085ea57b…`, 49,49 s, zero warnings/erros. Anexar saída redigida e SDK/digest. | Congelar candidato; Gate L01 guarda saída de build e versão do SDK. |
| 2 · 5 pp | Suíte completa da solução passa, sem falhas/skips, vinculada ao mesmo build/candidato. | Root reportou PASS no SHA atual: 673 passed, 0 failed, 0 skipped, 2,639 s em 11 assemblies; sumário revisado no escopo local. | Gate 1; saída completa/sumário de `dotnet test` e nomes/contagens de projetos, sem secrets. |
| 3 · 5 pp | Regras de TTL/freshness usam relógio controlável; não esperam tempo real para regra financeira. | Root reportou PASS no escopo local; Worker tem 110 testes na suíte atual e a regra usa relógio controlável segundo revisão reportada. | Gate 2; teste com relógio controlado e revisão da regra. |
| 4 · 5 pp | Traceability de requisito→aceite→código→teste→review→evidência; dados de pesquisa ligam captura/replay/manifest por conteúdo, contagem e janela. | Dataset público completo tinha subcheck consistente no snapshot anterior; mapa integral continua pendente. | Gates 1–3; saída do validador e revisão semântica com links por requisito. |
| 5 · 5 pp | Migrations, persistência, replay, ledger e recovery local coerentes após falha/restart no PostgreSQL isolado. | Root reportou PASS local no candidato atual; relatório `/tmp/cryptotrading-local-plan-recovery-20261010/full-recovery-20261010T123051Z-pNKwj8.md`. | Gate 1; relatório de integration/recovery ligado ao SHA, volumes e projeto descartável identificados. |
| 6 · 5 pp | Backup operacional agendado, protegido/criptografado, retenção aprovada, falhas/capacidade consultáveis e restore periódico isolado. | Script manual e teste de contrato existem; contrato passou no root. Não existe ciclo operacional configurado nem restore periódico demonstrado. | Gate 5; decisão de frequência/proteção/retenção/RPO/RTO; run de backup e restore com manifest/hash. |
| 7 · 5 pp | Guarda local mantém NO_TRADE, readiness, policy/saldo/comissão e rejeita pré-condições com zero submits. | **PASS local no SHA atual.** A suíte atual passou; `SubmissionDispatchSafetyTests.FinalGuardFailureRejectsBeforeVenueAndNeverSubmits` verifica rejeição antes do venue e zero submits. `WorkerRuntimeTests.TestnetExecutorQuarantinesAmbiguousSubmitWhenOutcomeCannotBeQueried` mantém resultado `UNKNOWN` após um submit/consulta; `SubmissionDispatchSafetyTests.AcceptedSubmitWithProjectionFailureQueriesAndDoesNotResubmit` não repete o submit após falha de projeção. `ExecutionSubmissionGuardTests` e `ExecutionSubmissionGuard.cs` cobrem saldo livre, policy atual/válida, comissão/frescor, relógio e readiness. Evidência: `src/CryptoTrading.Worker/ExecutionSubmissionGuard.cs`; `tests/CryptoTrading.Worker.Tests/SubmissionDispatchSafetyTests.cs`; `tests/CryptoTrading.Worker.Tests/ExecutionSubmissionGuardTests.cs`; `tests/CryptoTrading.Worker.Tests/WorkerRuntimeTests.cs`. São testes automatizados locais com clients de teste, não execução nem evidência de venue. | Gates 1–3; repetir review/testes se a guarda mudar. Aprovação da policy financeira continua no gate 9. |
| 8 · 5 pp | Reconciliação de venue autorizada cobre fills, posições, replay, resultado Unknown terminal e divergência com evidência redigida. | **PENDENTE.** Testes/integração PostgreSQL locais não equivalem à execução ou reconciliação com venue real. | Gates 1–7 e autorização independente futura; bundle de execução redigido e assinado por janela/SHA. |
| 9 · 5 pp | Política financeira aprovada e versionada: capital, BRL/USDT fresco, fees/reserva/notional, limites, parada, owner, validade. | PENDENTE. R$100, R$2/dia, R$5 cumulativo são drafts. | Gates 3 e 7; decisão humana, fonte/timestamp/hash/validade e testes de fronteira. |
| 10 · 5 pp | OOS sem leakage, walk-forward, custos realistas, stress/sensitivity predefinidos e paper soak reproduzível aprovado. | PENDENTE. Artefato exploratório público tem 1.461 barras completas e 70 janelas; `beatsBuyAndHold=false`; não é evidência de resultado real. | Gates 4, 7, 9; preregistro, métricas/custos, comparação, stress e relatório de soak. |
| 11 · 5 pp | Scan de dependências/imagens por digest e DB atual; sem High/Critical, ou waiver formal, escopado, temporal e rastreável. | Snapshot anterior registrou findings High/Critical; pins foram atualizados no commit, mas novo scan completo é obrigatório. | Gates 1/18; relatório Trivy atual por digest, SBOM e compatibilidade; sem waiver automático. |
| 12 · 5 pp | Acesso local mínimo para operação: dono/grupo e UID/GID dos containers; diretórios `0700`, arquivos `0600`; token API file-scoped e validação fixed-time; PostgreSQL por role restrita e loopback/rede local; Binance ausente em PAPER e, quando futura leitura autorizada, chaves file-scoped segregadas por escopo com withdrawals disabled. | **PENDENTE, parcialmente coberto.** Secret scan passou. Token interno já é fornecido por arquivo; valores Binance ainda podem ser injetados como variáveis pelo `compose.yaml`, então falta migrar/verificar caminho file-scoped, isolamento no processo/telemetria e escopo de acesso operacional. Proveniência/SBOM estão nos gates 11 e 18, não neste gate. | `compose.yaml`, `.env.example`, `scripts/init-proxy-secret.sh`, preflights de armazenamento, permissões locais/roles e revisão de runtime; teste de ausência de credencial em PAPER/redaction. |
| 13 · 5 pp | Observabilidade local detecta falhas relevantes com diagnósticos sanitizados e sem vazar dado sensível. | Root reportou PASS no escopo local: 38 testes do projeto de observabilidade e dois contratos de diagnóstico. | Gate 1; sumário de testes, fixture de redaction e consulta local reproduzível. |
| 14 · 5 pp | Logs consultáveis demonstram saúde, falhas financeiras/operacionais, backup, idade, capacidade/retenção e uso do runbook. | PENDENTE. Diagnósticos e stack local não provam protocolo operacional exercitado. | Gate 13; exercício com eventos de sucesso/falha, consulta Loki/log local e decisões documentadas. |
| 15 · 5 pp | Chaos/soak mede restart, fencing, timeout, falha persistente, ambiguidade e kill-switch, com limiares/relatório vinculados ao commit. | PENDENTE. Harness local existe em escopo paper, mas não há relatório completo vigente ligado ao candidato. | Gates 5, 7, 8 e 14 conforme escopo; janela paper, matriz de falhas e evidência de recuperação/parada. |
| 16 · 5 pp | Testnet mutável executada apenas em janela explicitamente autorizada, com credenciais segregadas, limites e reconciliação. | PENDENTE e não autorizada por este plano. | Gates locais aplicáveis e aprovação humana imediatamente antes; bundle por símbolo/capital/janela/SHA. |
| 17 · 5 pp | Capability Live isolada: adapter, capabilities, secrets, pretrade, OMS, reconciliação ambígua, kill-switch, contrato e revisão independentes. | PENDENTE; Live não suportado hoje. | Arquitetura e gates locais; ADR, implementação isolada, contrato/segurança/chaos e autorização separada. |
| 18 · 5 pp | CI remoto integral no SHA candidato, sem skips/falsos verdes; scans, testes PostgreSQL, SBOM, proveniência e artefatos disponíveis. | PENDENTE no SHA atual até execução e inspeção do run. | Gates 1–14 aplicáveis; URL/run, SHA exato, proveniência do build, saída dos jobs e artefatos retidos. |
| 19 · 5 pp | Deploy e rollback ensaiados em stack local isolada, com health checks, persistência e NO_TRADE. | PENDENTE. Instrução de Compose/runbook não é evidência de exercício. | Gates 5, 6, 13 e 18; registro before/after, health/readiness, volumes preservados e rollback. |
| 20 · 5 pp | GO humano independente vigente e durável, ligado ao SHA, policy, capital, símbolo, janela, ambiente e bundle. | PENDENTE; NO-GO. | Todos os gates aplicáveis; aprovação dos responsáveis. Assinatura, validator ou documento deste plano não substituem aprovação. |

### Progresso posterior ao baseline

Após o baseline `085ea57`, o worktree recebeu implementações ainda não
congeladas em SHA: isolamento de secrets no Compose, wrapper agendável de
backup fail-closed, redaction de valores textuais, runbook logs-only, relatórios
PAPER/Shadow chaos e ADR da boundary Live. Os contratos offline correspondentes
passaram, assim como uma janela de cada soak local. Isso reduz lacunas, mas não
altera os seis gates PASS nem o percentual: a mudança de código/configuração
reabre build, suíte e observabilidade para o próximo candidato congelado.

Na revisão posterior mais recente, o build Release integrado passou sem warnings
ou erros e a suíte .NET passou com **687/687** testes, zero falhas e zero skips.
Ela também cobriu o novo bloqueio de regras de mercado indisponíveis/incompletas
e a remoção de mutações automáticas do inicializador de Testnet. A worktree
continua deliberadamente não congelada; portanto esta evidência confirma o
estado local de desenvolvimento, mas não substitui o bundle de um SHA candidato.

O validador de rastreabilidade agora exige referências locais completas para
itens explicitamente marcados como concluídos, sem transformar itens planejados
em evidência fictícia. A matriz PAPER também cobre falha persistente, timeout
após aceite e quarentena `UNKNOWN` sem redespacho cego; os relatórios PAPER e
Shadow registram estado/fingerprint da worktree, cenário, duração e motivo de
parada em arquivos `0600`. Esses exercícios continuam determinísticos e em
memória: não fecham os gates de persistência, venue, limiares aprovados ou Live.

- **Gate 6:** `scripts/backup-postgres-scheduled.sh` acrescenta lock, timeout,
  retry limitado e eventos JSON sem dados de conexão; continua pendente até
  política humana de criptografia/retenção, agendamento real e restore periódico.
- **Gate 12:** a base `compose.yaml` deixou de injetar credenciais Binance; o
  override `compose.testnet.yaml` recebe somente paths file-scoped e mantém
  PAPER/NO_TRADE. A montagem real, UID/GID, roles e inspeção sanitizada seguem
  pendentes.
- **Gates 13–14:** `SecretRedactor` passa a sanitizar query, userinfo e
  Authorization/Bearer em valores textuais; há runbook e drill de controle
  operacional. Consulta de logs da stack realmente iniciada, retenção e
  capacidade ainda não foram demonstradas.
- **Gate 15:** os runners PAPER/Shadow agora recusam credenciais, repetem
  janelas e escrevem relatório privado redigido. Fencing persistente, banco,
  stale data, `UNKNOWN`, kill-switch e pressão de disco permanecem pendentes.
- **Gate 17:** `docs/adr/ADR-0001-live-capability-boundary.md` fixa a decisão
  de não reutilizar Testnet para Live; não há adapter Live, aprovação ou
  alteração de runtime.

## Arquitetura operacional local desejada

### Serviços mínimos

1. **PostgreSQL local:** volume persistente, acesso de rede limitado à rede Compose, health check, migrações explícitas e backup separado. Não publicar a porta do banco em interfaces externas; se acesso de host for indispensável para desenvolvimento, ligar somente a `127.0.0.1` e registrar a porta aprovada.
2. **API:** endpoints locais previstos pelo projeto; a Web usa o token file-scoped para chamadas Web→API. Acesso direto à API em loopback também é permitido para ferramentas/processos locais autorizados. O token protege a fronteira de serviço entre Web e API; não cria isolamento contra outro processo local executando como o mesmo usuário ou root, nem contra comprometimento desse usuário. Manter o arquivo em modo `0600`, documentar rotação e nunca registrar o valor em logs.
3. **Worker:** `PAPER`, `WORKER_NO_TRADE=true`, autonomia e envio de ordem desabilitados; adapter read-only ou ausência de credenciais nesta fase. Falha de persistência, checkpoint, readiness, relógio, policy ou reconciliação deve manter sistema parado/NO_TRADE.
4. **Web:** acesso local; controlar sessão e token interno. Não assumir que esconder uma rota substitui autenticação. Health status não deve revelar dados financeiros/segredos.
5. **Observabilidade opcional:** Collector, Loki, Prometheus, Grafana, Tempo e Alertmanager podem ser ligados no perfil local quando necessários para operar a fase. Padrão de entrega é logs consultáveis somente na máquina; Alertmanager fica local-only. Nenhum e-mail, webhook, receiver remoto, on-call externo ou proxy público faz parte do DoD.

### Fronteiras e superfícies

- API, Web, PostgreSQL e observabilidade permanecem em rede Docker privada e/ou loopback. Revise `docker compose config` redigido e `docker compose ps` para provar bindings reais antes de cada smoke; não publique porta na LAN por conveniência.
- Abra apenas superfície necessária no host. Regra de firewall local deve restringir por interface/origem; registrar comandos/resultado sem expor configuração sensível. Se a aplicação só precisa ser acessada pela máquina, bind `127.0.0.1` é o padrão.
- TLS é necessário quando tráfego atravessar máquina/interface não confiável. Para tráfego exclusivamente loopback/bridge privada local, documentar a fronteira de confiança e impedir exposição externa; não inventar TLS local que quebre integração sem reduzir risco. Qualquer proxy/TLS externo sai do escopo atual e requer plano próprio.
- Sincronizar relógio do host via serviço local confiável; monitorar desvio e falhar fechado para decisões temporais fora do limite aprovado. Não usar relógio do exchange como relógio do sistema.
- Monitorar espaço livre em volumes de PostgreSQL, auditoria, Loki e diretório de backup; limites, alertas locais e ação de parada precisam ser aprovados. Evitar crescimento sem quota/retention, sobretudo nos arquivos de auditoria financeira.
- Executar containers com usuário não-root quando compatível, UID/GID local explícitos, filesystem read-only onde possível, capabilities mínimas e sem Docker socket montado. Confirmar isso por compose config e inspeção do container.
- Fixar imagens por versão e digest. Atualizações de digest exigem compatibilidade, SBOM, scan atual e CI; tag mutável não é identidade suficiente.

### Acesso e segredos

- Token API/proxy interno: arquivo local protegido, diretório `0700`, arquivo `0600`, dono UID da conta local; nunca imprimir valor ou usar valor em linha de comando. Runbook atual usa `API_INTERNAL_ACCESS_TOKEN_SOURCE` como caminho, não conteúdo.
- PostgreSQL: senha local forte via mecanismo protegido; não embutir em Git, argv, relatório ou command history. `POSTGRES_CONNECTION_STRING` é configuração sensível; remover do ambiente antes de testes que não precisem dela (`env -u POSTGRES_CONNECTION_STRING`).
- Binance: nenhuma chave necessária para PAPER sem market/user data autenticado. Se uma janela futura read-only autorizada for necessária, usar credenciais segregadas e file-scoped (`*_FILE`), escopo mínimo, withdrawals desabilitados e preflight com `NO_TRADE`. A composição precisa ser revisada para garantir que o conteúdo da chave não vire variável de ambiente visível a processos vizinhos.
- Não persistir tokens em `.env` versionado, logs, telemetry attributes, crash dumps, artefatos CI ou evidence bundles. Não carregar segredos no RAG.
- Rotacionar/revogar credencial exposta antes de qualquer uso; teste redigido deve provar que segredo de canário não aparece em stdout/stderr/logs/trace. Nunca copiar segredo real para testar redaction.

## Política operacional por logs

Logs consultáveis locais e runbook são a resposta operacional desta fase. O sinal precisa permitir perceber falha e agir, sem coletar payload financeiro bruto. Campos mínimos quando aplicável:

- timestamp UTC, serviço, versão/SHA, ambiente, nível e tipo de evento;
- `correlation_id`/`operation_id` não secreto, estado (`started`, `succeeded`, `failed`, `degraded`) e código/razão sanitizados;
- estado de readiness/NO_TRADE, health/dependency, idade do heartbeat e duração da operação;
- para backup: `backupId`, `last_success_utc`, `backup_age_seconds`, bytes, verificação/hash e espaço livre do volume;
- para evento de decisão financeira: identificador interno não sensível da policy/dataset/strategy, estado de aprovação, custo/reserva/saldo apenas no nível mínimo aprovado, quantidade/notional quando imprescindível, decisão NO_TRADE/guard e razão codificada;
- para execução futura autorizada: IDs redigidos/estáveis necessários à reconciliação, estado da ordem/fill, latência, erro classificado, ação de stop e posição reconciliada. Nunca imprimir assinatura, chave, header, payload privado ou conta completa.

Toda ausência de sinal deve ser explícita (`unknown`/`null`); log inexistente não é sucesso. Preservar o JSONL de auditoria append-only como evidência. Qualquer rotação/arquivamento deve manter integridade, sequência, hashes/manifests e possibilidade de restaurar; não apagar logs financeiros por idade sem política aprovada. A retenção de Loki (configuração atual documentada em 14 dias) não é retenção do ledger/audit do Worker. Definir capacidade e procedimento local para arquivo de auditoria sem truncar silenciosamente.

O exercício do gate 14 deve injetar ou simular, só em ambiente de teste/paper, heartbeat ausente, falha de rotina de backup, espaço abaixo do limiar, erro de dependência e evento redigido; o operador consulta os logs, identifica estado, segue o runbook e registra resultado. Esses cenários não devem tocar venue nem usar dados reais. Notificações externas permanecem fora do escopo.

## Backup, retenção e recuperação

O repositório contém `scripts/backup-postgres.sh`, ferramenta manual que recebe container, database, user, diretório e mínimo de espaço explicitamente. O script cria staging privado, valida com `pg_restore --list`, calcula SHA-256 e manifesto e publica diretório novo sem sobrescrever. O contrato é testado sem Docker real por `scripts/test-backup-postgres.sh`. A ferramenta não criptografa, não agenda, não faz cópia externa e não restaura. Ela é início útil, não fechamento do gate 6.

Antes de depender do serviço local por mais de uma sessão:

1. Decidir dono, diretório e filesystem privados fora de qualquer worktree; validar proprietário, modo `0700`, quota/espaço, sem symlink e sem incluir credenciais.
2. Planejar a frequência/cadência considerando os valores propostos de RPO 1 hora e RTO 4 horas e a capacidade de storage. São alvos de trabalho para planejamento em horas, não tempos aceitos como exatos nem atendimento medido; validar a rotina contra esses alvos ou ajustá-los antes de promoção. Especificar lock contra duas execuções, timeout, falha/retry/backoff e não sobreposição de publicação.
3. Escolher criptografia em repouso, gestão/chave separada do arquivo e caminho de acesso/rotação. Não afirmar backup protegido antes de exercitar restauração com chave disponível e controle de acesso.
4. Definir retenção e capacidade antes de eliminar cópias. Preservar várias gerações, checksum, manifesto, versão do PostgreSQL e IDs de migration. Sem decisão, não limpar cópias nem audit.
5. Emitir logs consultáveis para sucesso, falha, idade, bytes e espaço; verificar a consulta e procedimento de incidente. Um cron/systemd timer só passa após provar execução no horário, falha visível e lock.
6. Fazer restore periódico em PostgreSQL/volume isolado, comparar integrity/ledger, migrations e contagens/hashes, medir tempo total e limpeza, mantendo original intacto.
7. Exercitar a rotina para avaliar os alvos propostos de RPO 1 hora e RTO 4 horas, ou ajustá-los antes de promoção. Medir desde falha percebida até serviço local coerente e utilizável; reportar distribuição/limites, não só o melhor caso.

Não usar `docker compose down -v` para cleanup de ambiente que contenha dados que devam ser preservados. Em teste, gerar nomes de projeto, portas e volumes novos e registrar os alvos; remover apenas recursos identificados como descartáveis pelo script. Nenhuma etapa deste plano aprova exclusão de dados reais.

## Pesquisa e risco

Artefatos públicos completos referenciados pelo snapshot 10/10 cobrem BTCUSDT 1d de 2022-10-10 a 2026-10-09, 1.461 registros/replays, 0 gaps e sequência UTC crescente. O run OOS exploratório tinha 70 janelas (60 treino / 20 OOS), capital simulado 10.000 unidades, fee 10 bps, slippage 5 bps e fill assumido no fechamento diário. `beatsBuyAndHold=false`; não modela spread observado, latência, fills parciais, fila, impacto ou liquidez. Esse número simulado não é BRL nem o draft de R$100, e o relatório marca `promotion_authorized=false`.

O conjunto inicial tinha divergência 1.461 barras versus 1.460 replay records e hashes não coincidentes; o conjunto completo corrigiu proveniência/contagem. Mesmo assim, dataset íntegro não fecha gate 10. Antes de qualquer conclusão sobre estratégia: preregistrar hipótese, universo, janela, divisão temporal, métricas, parâmetros, custos, baseline e critérios de rejeição; limitar seleção repetida/overfit; incluir regimes/robustez/custos; repetir replay determinístico; fazer paper soak com execução/custos em escopo autorizado. Pesquisa não acessa hot path e nunca autoriza execução.

Risco precisa ser aprovado com saldo/BRL-USDT fresco, fees, reserva, notional mínimo, tamanho de posição, limites de exposição, perdas/pausa, slippage, gaps, timeout/unknown, stops e validade. R$2/dia e R$5 cumulativo não podem ser chamados de perda máxima garantida. `NULL` para capital conhecido deve continuar desconhecido, nunca convertido em zero. Qualquer dado vencido, conversão ausente, saldo ambíguo, limite ultrapassado ou reconciliação divergente força NO_TRADE.

## Backlog executável por fase

P0 bloqueia o uso local persistente com confiança ou a decisão Live; P1 fecha operação local; P2 prepara uma avaliação futura, sem antecipar autorização. Estimativas usam tamanhos XS/S/M/L, sem somar em calendário: dependências humanas e descoberta tornam qualquer prazo agregado especulativo. Dono de cada caminho mutável é único por tarefa. Mudança de contrato ou base invalida tarefas dependentes até revisão.

### Fase A — Congelar candidato e fechar baseline de evidência

**Estado:** gates 1, 2, 3, 5, 7 e 13 já foram revalidados pelo root no SHA atual; manter as evidências existentes e repetir somente se source/config/SDK relevante mudar. A fase agora preserva esse bundle, fecha rastreabilidade (4) e conclui a revisão de acesso local/segredos (12). Não reexecutar checks básicos já PASS sem um invalidador. Não alterar produção/venue.

| ID | Prioridade / tamanho | Trabalho e critério verificável | Dependências / responsável / caminhos principais | Evidência |
|---|---|---|---|---|
| L01 | P0 / S | Baseline atual registrada: SHA/branch/status e SDK do candidato. Recoletar apenas quando source/config/SDK mudar ou a worktree ficar dirty. | Root; sem mutação; `global.json`, manifests, Git. | Header deste plano fixa SHA; capturar novo fingerprint só após mudança. |
| L02 | P0 / S | Build Release já PASS no candidato (49,49 s; 0 warnings/erros). Repetir somente após mudança invalidante. | L01; Luna/Terra; nenhum arquivo de app esperado. | Resultado root ligado ao SHA atual. |
| L03 | P0 / M | Suíte atual PASS (673/673; zero skips/falhas). Investigar apenas novo finding ou mudança invalidante. | L02; Terra; código/testes somente se houver finding concreto. | Summary informado pelo root; após alterações, novo resultado ligado ao novo SHA. |
| L04 | P0 / S | Relógio/freshness validado no SHA atual. Repetir somente se regra temporal/relógio mudar. | L03; Luna; `AdaptiveDecisionCycle*` e freshness tests se houver finding. | Evidência da suíte/revisão root vinculada ao commit. |
| L05 | P0 / M | Recovery PostgreSQL completo PASS no candidato atual. Repetir integration/recovery somente se persistência, migration, configuração ou script do drill mudar. | L02; Persistence/Terra; scripts de drill isolados. | `/tmp/cryptotrading-local-plan-recovery-20261010/full-recovery-20261010T123051Z-pNKwj8.md`; resultado root associado ao SHA. |
| L06 | P0 / S | Scanner de segredos, contrato manual de backup e dois contratos de diagnóstico PASS. Para lacunas restantes, executar traceability (gate 4) e concluir acesso/runtime do gate 12. Os PASS parciais não fecham gates 4, 6, 12 ou 14. | L01; Luna; sem alterar policy por teste. | Saídas atuais do root; validação traceability e checklist file-scoped permanecem. |
| L07 | P0 / M | Guarda local revisada e testada no candidato; manter evidência source/test vinculada. Reabrir só após mudança de execução/guard. | L03/L05; Sol review crítico; `ExecutionSubmissionGuard.cs` e testes associados. | Casos e paths citados no gate 7. |
| L08 | P0 / S | Atualizar matriz deste documento e fechar somente lacunas reais 4/6/8–12/14–20, sem reexecutar gates PASS sem invalidadores. | L01–L07; autor do plano/root; este arquivo e bundle. | Cobertura local 6/20 e `not verified` nos critérios ainda incompletos. |

**Comandos de baseline**, executar em sequência quando necessário; nenhum precisa de credenciais:

```bash
git rev-parse HEAD
git -c core.fsmonitor=false status --short --branch
DOTNET_CLI_USE_MSBUILD_SERVER=0 bash scripts/dotnet-local.sh build CryptoTrading.slnx --no-restore --configuration Release --disable-build-servers -m:1 -p:UseSharedCompilation=false
env -u POSTGRES_CONNECTION_STRING bash scripts/dotnet-local.sh test CryptoTrading.slnx --no-build --configuration Release --verbosity minimal
bash scripts/test-postgres-integration.sh
bash scripts/test-full-recovery-drill.sh
bash scripts/test-backup-postgres.sh
bash scripts/scan-secrets.sh
bash scripts/test-secret-scan-tooling.sh
bash scripts/test-binance-readonly-preflight-diagnostics.sh
bash scripts/test-full-recovery-diagnostics.sh
./scripts/validate-traceability.sh
```

Não duplicar check já executado no mesmo SHA/fingerprint com saída preservada e revisada. Antes de integração Postgres/recovery, inspecionar o script para nomes de projeto, portas, volume, cleanup, credenciais de fixture e ausência de `down -v` sobre volumes persistentes. Não injetar `POSTGRES_CONNECTION_STRING` se o caso não precisar.

### Fase B — Operação local, logs, backup e segurança de host

**Gate:** a stack local opera com serviço reiniciável e dados preservados; falhas de aplicação/backup/disco são consultáveis; backup cifrado e restore isolado são exercitados em relação aos valores propostos para planejamento em horas (RPO 1h/RTO 4h, não números exatos já aceitos ou medidos), ou os alvos são ajustados antes de promoção. Não habilitar ordens.

| ID | Prioridade / tamanho | Trabalho e critério verificável | Dependências / responsável / caminhos principais | Evidência |
|---|---|---|---|---|
| L09 | P0 / M | Especificar owner local, limites de CPU/memória/disco, free-space threshold, política de rotação para stdout/Loki e armazenamento para audit append-only; manter retenção explícita e preservar originals. | L01, decisão do operador; `compose.yaml`, `compose.observability.yaml`, runbooks. | ADR/decisão local sem apagar arquivo financeiro. |
| L10 | P0 / M | **Parcial implementado:** `backup-postgres-scheduled.sh` tem lock, timeout, retry, logs estruturados e não sobrescreve; ainda configurar scheduler, criptografia/chave e retenção sem exclusão automática. | L05/L09, decisões humanas de retenção/proteção; `scripts/backup-postgres-scheduled.sh`, `docs/backup-local.md`. | Contrato offline PASS; falta execução controlada, política e evidência de arquivo privado. |
| L11 | P0 / M | Ensaiar restore criptografado em DB/volume isolado; validar migrations, ledger, fills/ordens/policies/checkpoints e comparar integridade; preservar original. | L10; Persistence/Operação; scripts recovery próprios, nunca restore sobre banco ativo. | Relatório de restore, tempo observado e comparação sem dados privados. |
| L12 | P0 / S | Exercitar a rotina para avaliar os valores propostos RPO 1h/RTO 4h (planejamento em horas, não aceitação dos números exatos); registrar medição, escopo e limites, ou ajustar os alvos antes de promoção. | L10/L11; Operação; documento de readiness. | Drill cronometrado, origem da falha e estado de serviço restaurado. |
| L13 | P0 / M | **Parcial implementado:** redaction cobre nome sensível, query, userinfo e Authorization em diagnósticos textuais; ainda instrumentar idade/status de backup, espaço e retenção. | L09; `src/CryptoTrading.Observability/TradingTelemetry.cs`, `docs/observability-local.md`. | Teste focado e drill local PASS; faltam logs da stack e campos operacionais restantes. |
| L14 | P0 / S | **Parcial implementado:** runbook logs-only e drill SafeMode/KillSwitch existem; executar consulta em stack local iniciada e registrar diagnóstico sem payload/credential leakage. | L13; Operação; `docs/observability-local.md`, scripts diagnostic tests. | Checklist datado, query local, ações e evidência redigida. |
| L15 | P0 / M | Revisar exposição Docker: binds, portas, networks, users, capabilities, filesystem, healthchecks, firewall local e clock; remover acessos sem necessidade. | L01/L09; platform/security; `compose.yaml`, compose observability, runbook. | Compose config redigida, `ps`/inspect sanitizado e checklist do host. |
| L16 | P0 / M | **Parcial implementado:** base Compose não recebe credencial Binance; override isolado fornece paths file-scoped e preserva PAPER/NO_TRADE. Provar montagem/UID, ambiente efetivo, roles e telemetria sanitizada. | L15; `.env.example`, `compose.testnet.yaml`, secrets e teste de isolamento. | contratos estáticos PASS; falta inspect/smoke sanitizado e revisão do host. |
| L17 | P1 / M | Aplicar controles de supply chain também à operação local: atualizar imagens por digest, gerar SBOM e escanear imagens e pacotes com DB atualizado; classificar findings e compatibilidade e corrigir bloqueadores sem waiver genérico. | L01/L15; Security/Terra; Compose/CI e dependências. | Relatórios scan/SBOM vinculados a digests e pacotes, política de waiver escopada. |
| L18 | P1 / M | Rodar smoke container com UIDs locais e API/Web/Worker/PostgreSQL; confirmar `PAPER`, `NO_TRADE`, readiness, persistência após restart e bindings loopback. | L15/L16; Ops/Terra; `scripts/test-container-smoke.sh`, Compose. | saída redigida, health e configuração efetiva sem valores secretos. |

**Critérios de interrupção:** se audit volume encher, clock estiver fora da tolerância, backup estiver atrasado, filesystem ficar read-only, migration falhar, checkpoint estiver inconsistente ou logs indicarem resultado ambíguo, parar Worker em NO_TRADE, manter dados e coletar evidência. Não “resolver” apagando volume/log/ledger.

### Fase C — Risk policy, OOS e avaliação de robustez

**Gate:** requisitos e medidas de estratégia/riscos são reproduzíveis e aprovados; ainda assim só autorizam a próxima avaliação, não ordens.

| ID | Prioridade / tamanho | Trabalho e critério verificável | Dependências / responsável / caminhos principais | Evidência |
|---|---|---|---|---|
| L19 | P0 / M | Decidir policy de capital: saldo, BRL/USDT, taxa, reserva, notional, exposição, limites e validade; especificar diferença entre proposta e limite contratual. | L07 e owner humano; `docs/risk-context-autonomy.md`, risk policy versionada. | assinatura/aprovador, fonte/time/hash/expiry e testes de fronteira. |
| L20 | P0 / M | Fixar protocolo de tempo/preço/custo para replay e paper: spread, slippage, fill parcial, latency, fee tiers, filtros exchange, falhas e conversão. | L19; Research/Terra; replay/OOS code and docs. | parâmetros preregistrados e fixtures públicas; sem dados ou credencial privada. |
| L21 | P1 / L | Preregistrar hipótese, universo, splits temporais, benchmark, métricas, critérios de rejeição, sensitivity/stress e regras anti-leakage/selection bias. | L04/L19/L20; Research/Terra; `docs/public-oos-runner.md`, plan research. | plano versionado antes do run, ID/hash dataset e configuração. |
| L22 | P1 / M | Executar replay determinístico e OOS completo no dataset público validado; comparar baselines; rejeitar se falhar benchmark/robustez. | L21; Research; artefatos OOS/datasets ignorados ou versionados por política. | relatório com hash, janela, métricas/custos e limitação explícita. |
| L23 | P1 / M | Executar paper soak/chaos sem API de ordem; observar ciclo completo, custos simulados, restart, fencing, timeout, stale data e kill-switch. | L05/L13/L20/L22; Research/Ops; harness paper. | timeline, falhas, duração/limiares e motivo de parada ligado ao commit. |
| L24 | P0 / M | Fazer revisão independente de estratégia/risco, inclusive seleção repetida, custos, limites de amostra e stop behavior. | L19–L23; Sol + owner risco; report only. | review com findings fechados ou decisão NO-GO. |

Nenhum retorno percentual simulado deve ser usado como previsão/garantia. Se baseline continua pior que benchmark, custos não observados ou tamanho de amostra inadequado, interromper promoção; manter artifact para pesquisa com `promotion_authorized=false`.

### Fase D — Chaos/soak e autorização Testnet separada

**Gate:** testar estado recuperável em ambiente paper; depois decidir, de forma independente, se haverá uma janela mutável Testnet. O plano não solicita nem contém essa aprovação.

| ID | Prioridade / tamanho | Trabalho e critério verificável | Dependências / responsável / caminhos principais | Evidência |
|---|---|---|---|---|
| L25 | P0 / M | **Parcial implementado:** PAPER/Shadow repetem janelas sem credenciais e guardam relatório `0600` com duplicação, falha transitória, restart e replay simulados. Expandir fencing persistente, banco, atraso, `UNKNOWN`, kill-switch e storage pressure. | L11/L14/L23; `scripts/test-paper-soak-chaos.sh`, `scripts/test-shadow-soak.sh`, `docs/chaos-local.md`. | relatório por janela PASS; falta matriz completa e vínculo a estado durável. |
| L26 | P0 / S | Definir thresholds de abortar/continuar por saúde, drawdown de paper, desvio relógio, stale data, backup age e reconciliation. | L19/L25; risk owner/Ops; runbook. | thresholds com unidade/janela/owner/validade/testes. |
| L27 | P0 / S | Revisar readiness gate de Testnet e autorização antes da janela (ambiente, símbolo, capital, tempo, creds, stop, reconciliação). | L01–L26; aprovadores humanos; checklist somente local até aprovação. | decision record; sem segredos nem ação executada. |
| L28 | P0 / M | Se e somente se autorização posterior existir, executar janela limitada Testnet, coletar resposta e reconciliation, encerrar credenciais e registrar evidência redigida. | L27; operador autorizado + reviewer; scripts opt-in Testnet. | aprovação vigente, logs redigidos, recon, stops, divergências e SHA. |

Gates L27/L28 não são executáveis por automação sem aprovação. Timeout/Unknown, conta/position mismatch, estado não reconciliado, credencial em ambiente errado ou quebra de limite deve interromper janela e forçar NO_TRADE; não repetir submit às cegas.

### Fase E — Capability Live como trabalho de arquitetura separado

**Gate:** estabelecer que Live está suportado com contrato, controles, evidência, identidade e aprovação próprios. Não reutilizar implicitamente credenciais ou flags de Testnet. A execução real continua proibida até GO independente.

| ID | Prioridade / tamanho | Trabalho e critério verificável | Dependências / responsável / caminhos principais | Evidência |
|---|---|---|---|---|
| L29 | P0 / L | ADR de capability e threat model: endpoints, auth, idempotência, estado Unknown, rate limits, filtros, fencing, ownership de orders e fail-safe. | L07/L24–L28; Sol arquitet; docs/ADR e contracts, sem adapter Live ainda. | decisões, abuse cases, rollback/kill-switch e review crítico. |
| L30 | P0 / L | Implementar adaptador/live capability em boundary isolada, disabled por default, com segredo separado, allowlist e nenhuma rota de saque/transferência. | L29; Terra; Exchange/Execution e testes contract. | contract tests contra fonte oficial/mock nomeado para teste; wiring runtime demonstra default off. |
| L31 | P0 / L | End-to-end dry/paper e simulador de respostas ambíguas, partial fills, reconnect/reconciliation; garantir zero real submit nos testes. | L30; OMS/Persistence/Terra; integration/contract suites. | evidência de protocolo/call order, idempotência, reconciliation e kill-switch. |
| L32 | P0 / M | Revisão crítica de segurança, concurrency, persistência, contratos e risco; resolver todos os bloqueadores. | L29–L31; Sol critical reviewer + financeiro; review. | findings e vínculos a testes/commits, approval não substituída pelo agente. |
| L33 | P0 / S | Definir gate humano GO Live como registro separado de readiness local/Testnet, ligado a policy/capital/venue/janela/SHA e expiração. | L19/L27/L32; owner humano independente; documento assinado/versionado. | aprovação durável e vigente; ausência ou ambiguidade mantém NO-GO. |

Até L29–L33 concluídos e humanos aprovarem o escopo, produção Live permanece **não suportada/NO-GO** mesmo se serviço local alcançou 100% de cobertura de gates locais.

### Fase F — CI, deploy local e rollback

| ID | Prioridade / tamanho | Trabalho e critério verificável | Dependências / responsável / caminhos principais | Evidência |
|---|---|---|---|---|
| L34 | P0 / M | Executar workflow integral no commit SHA candidato para a stack local: build/test, PostgreSQL, backup contracts, secret scanner, scan de imagens e pacotes, SBOM e artefatos com retenção. | Fases A–C; Terra/CI; `.github/workflows/quality.yml`. | URL, SHA exato, status de todos os jobs e artefatos. |
| L35 | P0 / M | Corrigir skips, falsos verdes, comandos diferentes de local e discrepância de digest; verificar que bloqueio security falha fechado. | L34; CI/security; workflow e scan tests. | revisão do workflow + teste negativo do gate. |
| L36 | P1 / M | Ensaio de deploy local em project/network/volumes isolados, PAPER/NO_TRADE, health checks e persistência. | L05/L06/L09/L13/L17/L18/L34; Operação/Terra; compose/runbook. | before/after, imagem digest, readiness, volume e log. |
| L37 | P1 / M | Ensaio de rollback em stack local isolada após falha induzida; recuperar versão anterior sem downgrade SQL cego e sem apagar volume. | L11/L36; DBA/Operação/Terra; migrations/rollback docs. | tempo, dados preservados, migrations compatíveis e saúde final. |
| L38 | P0 / S | Revisão final independente de rubrica/evidence chain e decisão local paper service vs Live NO-GO. | L01–L37; Sol + root/owner; este doc e bundle. | tabela 20 gates recalculada; `not verified` explicitado; assinatura local do reviewer. |

Deploy local é exercício reversível após revisão de escopo. Deploy externo, OCI, publicação, criação de recurso billable, Testnet mutável e ordem real são ações separadas e não estão autorizadas aqui.

## Sequência, dependências e priorização

1. **Preservar baseline e fechar traceability/acesso (L01–L08):** build, suite, relógio, recovery, guarda e observabilidade já foram revalidados no SHA atual. Preservar essas saídas; qualquer mudança nos invalidadores reabre apenas os checks afetados. Fechar traceability (gate 4), revisão file-scoped de acesso local (gate 12) e consolidar evidence bundle. Saída: candidato local caracterizado sem repetir checks básicos PASS.
2. **Deixar seguro operar parado (L09–L18):** fechar disco/log/access/secrets/security e smoke container em `PAPER/NO_TRADE`. Backup cifrado/restores (L10–L12) e logs operacionais exercitados (L13–L14) são bloqueadores do uso persistente prolongado, ainda que outros componentes iniciem.
3. **Decidir estratégia, custo e risco (L19–L24):** aprovação humana de policy precede qualquer métrica financeira considerada aceitável; pesquisa preregistrada e OOS depois. Baseline desfavorável não é motivo para diminuir gate.
4. **Provar resiliência local (L25–L26):** chaos/soak com limiares e estado recuperável. Separar falhas sintéticas de evidência venue.
5. **Gate futuro de Testnet (L27–L28):** decisão humana no momento da janela; não pré-aprovar no plano ou por completion automática.
6. **Construir e revisar capability Live (L29–L33):** boundary e autorização independentes, código Live off por default; Testnet não promove Live.
7. **Reproduzir CI e deploy local/rollback (L34–L38):** SHA final, digest, workflow integral, ensaio persistente e revisão final. Só o GO humano ligado a escopo pode permitir uma etapa financeira posterior.

Paralelismo permitido: tarefas de logs/observability (L13–L14) e backup/recovery (L10–L12) podem investigar em paralelo após contratos e métricas L09 estarem estáveis; scan de supply chain (L17) pode ocorrer em paralelo ao desenvolvimento local se digests forem congelados. Todo caminho mutável tem um único implementador. Teste/review lê a saída depois; não edita em paralelo. Se uma premissa compartilhada ou SHA mudar, cancelar evidência dependente, registrar invalidação e replanejar antes de integrar.

Dependências humanas que podem bloquear sem serem resolvidas pelo agente: política de retenção e descarte, local/chave de backup, limite de disco, approver de risco e capital, parâmetros/custos da estratégia, autorização Testnet, aprovação Live. Os valores de planejamento RPO 1h/RTO 4h são alvos propostos em horas, não números exatos aceitos nem desempenho medido; exercitar a rotina contra esses alvos ou ajustá-los antes de promoção. Enquanto decisões operacionais aguardam, executar somente trabalho independente e conservar NO_TRADE.

## Registro de riscos e resposta

| Risco | Sinal / impacto | Controle requerido | Ação de parada e residual |
|---|---|---|---|
| Snapshot antigo tratado como atual | SHA/fingerprint não coincide; gate creditado sem evidência. | Recoletar status/SHA e vincular todos os relatórios; recalcular 20 gates. | Invalidar resultado afetado; percentual atual permanece incompleto. |
| Envio financeiro acidental | Flag/env/adapter permite submit; credential disponível em ambiente paper. | NO_TRADE, default off, sem chaves em PAPER, zero-submit tests, boundaries segregadas. | Stop Worker; revogar credencial se exposure; não repetir ação. |
| Chaves vazam pelo ambiente | Env dump/process listing/log/trace/CI. | File-scoped secret, least privilege, redaction canary e scanner fail-closed. | Revogar/rotacionar antes de qualquer reuso e guardar evidência redigida. |
| Perda/corrupção de banco | Disco cheio, migration/restart inconsistente, restore não exercitado. | Backup agendado cifrado, manifest/hash, restore isolado, retenção e capacidade. | Stop writer, preservar originais, restaurar isolado e comparar; não fazer downgrade cego. |
| Audit sem integridade/crescimento | JSONL append-only sem rotação e volume enche. | Métricas de bytes/idade, archive verificado, capacity limit e retention aprovada. | NO_TRADE; conservar arquivo; nenhuma remoção automática sem política. |
| Falso sucesso no log | Falta sinal interpretada como healthy. | `unknown/null` explícito, heartbeat e exercício fail/degraded. | Tratar ausência como degraded; seguir runbook. |
| CVE de imagem/dependência | High/Critical em digest atual ou DB scan desatualizado. | Scan por digest em DB atual, SBOM e bloqueio CI; fix ou waiver formal e temporário. | Não promover/deploy; registrar exposição e mitigação. |
| OOS/leakage/custo irreal | Dataset divergente, benchmark ruim, fill otimista, seleção repetida. | Content hash, preregistro, custos/stress realistas, revisão independente. | Manter pesquisa exploratory/NO-GO; não transferir métrica a capital real. |
| Clock drift/stale market | Timestamp fora de limite ou dataset/replay incompleto. | NTP monitorado, TTL controlado, UTC estrito e fail-closed. | Pausar decisões, manter NO_TRADE até fonte válida. |
| Estado desconhecido da ordem | timeout/fill parcial/reconnect deixa posição ambígua. | Idempotency, fencing e reconciliation antes de qualquer retry. | Bloquear novos submits; reconciliar ou escalar humanamente. |
| Testes usam infraestrutura ativa | Nome/porta/volume colide ou script remove volume errado. | Revisar scripts, projeto/portas/volume únicos, inventário antes/depois. | Parar cleanup; preservar dados; incident report se alvo indevido tocado. |
| Alertas fora do escopo | Compose/proxy envia dados a terceiro. | Logs/local-only; sem receiver/webhook/email; scan de config. | Desabilitar rota externa; tratar publicação como incidente de autorização. |
| Meta RPO/RTO tratada como garantida | proposta 1h/4h sem evidência de exercise. | Marcar proposta, medir drills repetidos, revisar cauda/limitação. | Não afirmar produção pronta até owner aprovar tolerância. |
| Plano local confundido com Live/OCI | stack local passa mas boundaries externas não verificadas. | Separar gates, arquiteturas, approvals e percentuais por destino. | Status Live NO-GO e OCI fora do DoD. |

## Evidências e atualização deste plano

O bundle por candidato deve conter apenas: SHA e status, SDK/build metadata, hashes de configuração não secreta, logs de comando redigidos, resumos de teste, relatório recovery/backup, digest e SBOM de imagens, scan e findings, mapa de traceability, logs de exercício e review. Caminhos sob `/tmp`, incluindo os logs desta validação, são temporários e podem desaparecer. Os resumos de resultados registrados neste plano preservam o contexto, mas não substituem artifacts duráveis, retidos e ligados ao SHA exigidos para promoção. Guardar esses artifacts em local versionado aprovado ou armazenamento local protegido; nunca incluir `.env`, valores secret, dados de conta, payload autenticado ou tokens.

Para cada PASS registrar comando exato, código de saída, data/hora UTC, SHA/fingerprint, versão de ferramenta, resultado observado e reviewer. Se o comando não foi executado nesta revisão ou o artefato não está acessível, usar `not verified`. Um relatório velho pode orientar o que repetir, jamais substituir o resultado atual. Preservar snapshots antigos como histórico e marcar conteúdo superado em arquivo novo com link de origem, sem reescrever evidência pretérita.

Atualizar a tabela sempre que um gate mudar, incluindo a causa do status, artefato/commit e invalidadores. Alteração em código/config/teste invalida build/suite/CI pertinentes; alteração de imagem invalida digest/scan/SBOM/smoke; mudança de policy/data invalida pesquisa/risco/promocão; qualquer mudança no SHA candidato invalida CI e GO ligado a SHA.

## Definition of Done por resultado

**Serviço local PAPER pronto para exercício supervisionado:** gates 1–7, 11–15 e 18–19 têm evidência vigente no mesmo candidato para os componentes aplicáveis; secrets/access e operação local foram revisados; backup/recovery e logs exercitados; `NO_TRADE` continua demonstrado; nenhum bloqueio crítico aberto. Gate 16 não foi executado por implicação; Testnet segue não autorizada.

**Research/risco avaliável:** gates 4, 9 e 10 foram fechados com policy aprovada, OOS preregistrado/robusto e paper soak, mantendo distinção de simulação e execução.

**Live elegível para uma decisão humana futura:** todos os gates aplicáveis, incluindo 8, 11–12, 15, 17–20, têm evidência revisada e capability Live implementada e isolada; aprovadores independentes registram GO por escopo e validade. Isso apenas torna concreta a decisão; não dispara ordem automaticamente.

**Resultado do artefato:** plano completo como backlog e referência de trabalho; isso não declara a aplicação local pronta nem produção Live autorizada. A cobertura local confirmada para este SHA é 6/20 (30%). Live permanece NO-GO.

## Handoff desta versão

- **Resultado:** plano local concluído como artefato, com 20 gates uniformes e backlog em fases, preservando NO_TRADE e separando serviço local PAPER de capability Live. Rubrica no SHA atual: 6/20 (30%), com 14/20 pendentes/abertos; o percentual não representa prontidão Live.
- **Base:** `085ea57b165998ddb452a19c8a3e15efe6dfd427`, worktree limpa antes da criação deste arquivo.
- **Arquivos alterados:** somente `PLANO_PRODUCAO_LOCAL.md`.
- **Validações reportadas pelo root:** build 49,49 s sem warnings/erros; suíte 673/673 sem falhas/skips; format verification 2,223 PASS; Web 26/26 em 9 arquivos; lint 28 PASS; Vite build 938 ms; recovery integrado PASS no relatório `/tmp/cryptotrading-local-plan-recovery-20261010/full-recovery-20261010T123051Z-pNKwj8.md`; secret scan PASS; contrato manual de backup PASS; dois contratos de diagnóstico PASS. Esta autoria não reexecutou nem inspecionou logs brutos.
- **Lacunas:** gates 4, 6, 8–12 e 14–20 permanecem abertos conforme tabela. Backup operacional, acesso Binance file-scoped, exercício de logs consultáveis, scan completo atual, CI no candidato, paper soak/chaos e gates de risco/venue/Live ainda precisam de evidência.
- **Próximo passo:** revisão do plano e início dos itens P0 restantes. Não repetir gates básicos PASS sem mudança que invalide sua evidência. Preservar NO_TRADE; decisões humanas são necessárias apenas nos pontos de decisão/autorização explicitados.
