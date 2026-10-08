# Auditoria de prontidão para produção — 2026-10-07

**Decisão: NO-GO para operação com capital real.** Há implementação substancial e checks locais aprovados, mas existem falhas bloqueantes no despacho, execução Live não suportada e evidência de promoção/operacional insuficiente.

## Envelope da auditoria

- Objetivo: avaliar o projeto inteiro como sistema financeiro operável, separando implementação, defeitos e evidência de produção.
- Base: `7419aa7099c18dd84c22065b5c956400dabd42a8`; árvore inicialmente limpa.
- Escopo: Domain, Backtesting, Worker, Binance, Configuration, Persistence, Reconciliation, Shadow, API, Web, Observability, Compose, CI, testes e artefatos de readiness.
- Restrições: sem ordens, mutação Testnet, deploy, publicação externa ou acesso aos valores de segredos. Nenhum código de produto foi alterado.
- Paralelismo: três subagentes `gpt-6-luna`, reasoning `medium`, para financeiro, persistência e operação; consolidação e execução dos checks pelo agente principal.
- Dependências: SDK local fixado, dependências existentes, Node/npm e Docker com acesso autorizado para testes isolados.
- Não objetivos: implementar correções, aprovar risco, provar rentabilidade ou operar a conta do usuário.

## Evidência executada nesta auditoria

| Check | Resultado | Limite da evidência |
|---|---|---|
| `.tools/dotnet/dotnet test CryptoTrading.slnx --no-restore --configuration Release --verbosity minimal -m:1 -p:UseSharedCompilation=false` | PASS: 616 testes, zero falhas | A solução não inclui o projeto de integração PostgreSQL; este foi executado separadamente. Não é Testnet real. |
| `COMPOSE_PROJECT_NAME=cryptotrading-production-audit POSTGRES_TEST_PORT=55439 POSTGRES_VOLUME_NAME=cryptotrading-production-audit-20261007 ./scripts/test-postgres-integration.sh` | PASS: 19 testes | Banco isolado; container removido pelo script e volume exclusivo preservado. Não acessou o banco operacional. |
| Web: `npm run lint`, `npm run build`, `npm run test` | PASS: lint, build e 16 testes | Avisos de depreciação da integração Vite/React; não comprovam funcionamento da imagem Web junto à API. |
| `./scripts/scan-secrets.sh` | PASS | Scanner de padrões suportados no conteúdo versionado. Não audita todo o histórico Git, secret store ou permissões reais na Binance. |
| `./scripts/validate-traceability.sh` | PASS: 126 IDs | Validação estrutural de referências; não comprova requisitos satisfeitos pelo runtime. |
| `./scripts/test-readiness-validator.sh` | PASS | Valida o comportamento do validator com fixtures; não aprova os snapshots do produto. |
| `POSTGRES_CONNECTION_STRING='' API_TEST_PORT=58089 ./scripts/test-api-smoke.sh` | PASS | Host local e configuração sintética. Sem ordens, requisições à Binance ou banco operacional. |
| Validator com PRM 20260921, Canary draft e template RiskPolicy, `READINESS_AS_OF_UTC=2026-10-07` | FAIL esperado, exit 1 | Aprovações, campos estruturais, política concreta e itens obrigatórios não satisfeitos; o template não é uma RiskPolicy aprovada. |
| Duas reproduções offline adicionais em `/tmp/cryptotrading-audit-repro` | Comportamentos inseguros reproduzidos | São testes diagnósticos com doubles; os asserts documentam o defeito atual, não um gate de segurança aprovado. |

Total dos testes existentes executados: **651 = 616 .NET + 19 PostgreSQL + 16 Web**. As duas reproduções diagnósticas são contabilizadas separadamente.

O primeiro `dotnet test` foi bloqueado pelo sandbox ao criar sockets locais do MSBuild. A execução com permissão concedida passou. O primeiro smoke API, sem esvaziar explicitamente a connection string, recebeu `404` onde o script esperava `503`: o script permite herdar configuração local via `AddLocalDotEnv`. A repetição isolada passou. Isso é uma limitação de isolamento do harness; não é evidência de quebra da rota.

Logs locais desta sessão: `/tmp/cryptotrading-production-dotnet-retry.log`, `/tmp/cryptotrading-production-postgres.log`, `/tmp/cryptotrading-production-api-smoke-isolated.log`, `/tmp/cryptotrading-production-repro.log` e `/tmp/cryptotrading-production-readiness.log`. Arquivos em `/tmp` são temporários; os resultados e cenários relevantes estão registrados aqui.

## Bloqueios técnicos confirmados

### P1 — parada operacional não é revalidada no envio

Fontes: `src/CryptoTrading.Worker/TestnetOutboxMessageExecutor.cs:38-92`, `OutboxDispatcher.cs:296-317`, `AutonomyCycleHostedService.cs:292-299`.

O ciclo considera o controle operacional antes de produzir a decisão. Entretanto, dispatcher e executor não consultam o estado atual de `IOperationalControl`/runtime antes de `SubmitAsync`. Verificam flags de configuração, símbolo, comissão e saldo. Se o KillSwitch/SafeMode for ativado entre enfileiramento e despacho, a mensagem já pendente ainda pode criar exposição. A existência de kill switch no domínio não garante interrupção da fila.

Correção necessária: gate operacional fail-closed na fronteira de mutação, incluindo estado atual, política/autorizações vigentes e tratamento distinto para comandos de redução/cancelamento. Teste obrigatório: enfileirar, ativar KillSwitch, despachar e observar zero submits.

### P1 — recuperação depois de aceite pode reenviar e perder a projeção

Fontes: `TestnetOutboxMessageExecutor.cs:78-110`, `OutboxDispatcher.cs:343-366`, `src/CryptoTrading.Persistence/OrderProjectionService.cs:33-36,70-73`, `src/CryptoTrading.Exchange.Binance/BinanceSpotOrderClient.cs:471`.

Se Binance aceita a ordem e a persistência da projeção posterior falha, a exceção comum chega ao retry do dispatcher. A próxima execução reconstrói o agregado e chama `SubmitAsync` novamente; não consulta previamente o resultado durável/exchange. No caso de rejeição da segunda submissão por ID duplicado, projeta `Rejected` e remove o registro aberto, embora a primeira ordem continue no venue.

Reprodução offline executada: primeiro submit retorna `NEW`; o store falha uma vez no upsert que contém o ID externo; replay retorna rejeição de duplicidade. Resultado: **2 submits, 0 queries, projeção aberta vazia**. Fonte temporária: `/tmp/cryptotrading-audit-repro/WorkerRuntimeTests.cs`, teste `Audit_AcceptedSubmitThenProjectionFailureRetriesAndRemovesOpenOrder`.

Correção necessária: persistir e recuperar o resultado externo sem resubmissão cega; consultar pelo ID antes de repetir uma tentativa; classificar falha posterior ao aceite como estado que exige resolução. A cobertura deve incluir falha no ACK, falha no mark processed, crash e replay com ordem já terminal.

### P1 — inputs de risco de mercado têm valores fixos

Fonte: `src/CryptoTrading.Worker/AutonomyCycleHostedService.cs:287-291`; consumidor: `src/CryptoTrading.Domain/Risk/RiskPolicy.cs:274-309`.

O Worker fornece `expectedSlippageBps=5`, `volatilityBps=0` e liquidez igual ao saldo livre da conta. Esses campos alimentam limites de slippage, volatilidade e liquidez do RiskEngine. Saldo disponível não mede profundidade negociável do mercado. O motor adaptativo também recebe esse snapshot; seu fallback baseado em volatilidade do regime não é usado quando o snapshot está presente.

Consequência: esses limites podem aprovar uma ordem com dados que não representam o mercado, apesar de filtros de símbolo, spread e classificador de regime existirem em outras etapas.

Correção necessária: calcular métricas observadas de barras/book, com origem, timestamp, janela e qualidade; ausência ou desatualização deve bloquear. Testar cenários de liquidez baixa, alta volatilidade e slippage acima do limite com saldo alto.

### P2 — TTL de um submit pendente não bloqueia o envio

Fontes: `OutboxDispatcher.cs:117-145`, `TestnetOutboxMessageExecutor.cs:45-92`, `ExpiredOrderCancellationHostedService.cs:23`.

`ExecutionExpiresAtUtc` chega ao executor, mas não é comparado ao relógio antes da submissão. O scanner de cancelamento posterior não substitui a prevenção de envio de um sinal já vencido.

Reprodução offline executada: comando com `ExecutionExpiresAtUtc=UtcNow-1min`, comissão ainda válida e saldo suficiente; **1 submit**. Teste `Audit_ExpiredCommandStillSubmits` no reproducer temporário.

Correção necessária: expiração fail-closed imediatamente antes do envio; conferir também validade da política e frescor do contexto usado para autorizar preço/notional.

### P2 — concorrência de workers sem proteção demonstrada no side effect

Fontes: `OutboxDispatcher.cs:278-296`, `WorkerRuntimeOptions.cs:39-41`; `src/CryptoTrading.Domain/Runtime/FencingTokenCoordinator.cs:22`.

A reivindicação SQL tem `SKIP LOCKED` e owner de lease; os updates conferem o owner. Isso protege alterações no store. O dispatcher, porém, não renova o lease nem valida expiração/fencing antes de executar cada mensagem. O lote padrão de 25 mensagens compartilha leases de 30 segundos e é processado sequencialmente. Pausa prolongada/latência pode permitir que outro worker reivindique mensagens ainda em processamento. O coordenador de fencing do domínio não está integrado ao envio externo.

Este é um risco estático de concorrência, não uma duplicação real observada na auditoria. Correção necessária: contrato de liderança/leases/recuperação que impeça efeitos externos de workers obsoletos; teste de dois workers com expiração durante o envio. Não promover operação multiworker antes dessa evidência.

## Defeitos de contabilidade, segurança e implantação

| Prioridade | Achado e evidência | Correção e verificação necessária |
|---|---|---|
| P2 | `PostgresTradingStore.Performance.cs:54,84` soma `commission` na moeda nativa e API `Program.cs:516` apresenta como USD. Mesmo BTCUSDT pode cobrar fee em BTC/BNB. A soma de P&L também não delimita quote/símbolo. | Usar valuation na data do fill e unidade explícita; não misturar moedas. Testar fees USDT/BTC/BNB e agregação por escopo. |
| P2 | API `Program.cs:443,465,475` silencia falhas de preço, conta e store, devolvendo `200` com zeros ou dados incompletos. Uma falha de preço com posição aberta pode aparentar perda do custo integral. | Retornar estado indisponível/stale e erro correlacionado; não representar ausência de dado como saldo/lucro zero. |
| P2 | API `Program.cs:518-534` aplica o mesmo unrealized P&L acumulado aos períodos dia/semana/mês/ano. Uma posição antiga sem variação hoje aparece como lucro diário. | Snapshots de NAV/capital e variação por janela, com fluxo externo segregado; testar posição carregada entre períodos. |
| P2 | `PostgresTradingStore.Performance.cs:86,119` compara `timestamptz` com limite UTC transformado em timestamp sem timezone; depende da timezone da sessão. | Limites UTC tipados explicitamente; testar sessão PostgreSQL não UTC. |
| P2 | `PostgresTradingStore.AutonomousLearning.cs:145` usa `ON CONFLICT(model_id) DO NOTHING` sem comparar hash/payload. Uma identidade reutilizada com conteúdo divergente retorna sucesso. | Aceitar replay equivalente e rejeitar colisão divergente; teste no store PostgreSQL. |
| P1 se publicado sem proteção adicional | API `Program.cs:564-726` registra novas rotas operacionais diretamente em `app.MapGet`, fora do filtro loopback do grupo em `122-131`; não há autenticação/autorização implementada. | Uniformizar proteção de rotas e incluir auth em exposição remota; testar todas as rotas. O Compose restringe o host a loopback, reduzindo exposição direta atual. |
| P2 | Web `Dockerfile:11-15` serve estáticos sem proxy; `src/api/client.ts:63-72` usa endpoints relativos. Proxy em `vite.config.ts` é apenas desenvolvimento. | Gateway/nginx com encaminhamento API e política de acesso compatível; smoke das imagens Web+API. O filtro loopback de outras rotas também exige desenho de identidade/proxy adequado. |
| P2 | API `Program.cs:731-748` verifica somente configuração no readiness; `/operational/status` usa controle em memória da API, distinto do Worker (`46-47,136-147`). | Probes de banco/migrations e saúde operacional apropriada; expor estado autoritativo do Worker, com timestamp e escopo. Não usar o estado da API como kill switch global. |

## Aprendizado adaptativo: implementação parcial no runtime

`AdaptiveDecisionCycle.cs:236` **chama** o scorer de execução; a hipótese preliminar de scorer desconectado foi descartada como `superseded` após inspeção do módulo Backtesting.

Entretanto, `ContextualMetaAllocator` mantém rewards em memória (`src/CryptoTrading.Domain/Research/ContextualMetaAllocator.cs:38-54`). A busca de chamadas em `src/` não encontrou alimentação de `RecordReward` no runtime. `IStrategyRegimePerformanceStore` é registrado no Worker e lido pela API, mas o ciclo não carrega esse histórico no allocator nem registra reward de fills. Assim, existem classificação, estratégias e seleção adaptativa implementadas, mas aprendizado a partir do resultado durável e sua recuperação após restart não estão demonstrados. Não tratar tabelas e testes isolados como um learning loop integrado aprovado.

Falta provar avaliação OOS/walk-forward do **candidato realmente ativo**, com seleção multi-strategy, regimes, custos, fees, execução, holdout e limites de experimentação. O repositório contém harness de OOS público e registros de execuções antigas; não se conclui que não existe dado real. O que não está comprovado nesta auditoria é a suficiência/atualidade dessas evidências para a estratégia adaptativa do HEAD.

## Gates de produção que faltam

1. **Suporte Live deliberadamente implementado.** `WorkerRuntimeOptions.cs:102-109` aceita só PAPER/TESTNET; `TradingConfiguration.cs:37-40` exige TESTNET; `WorkerExecutionRegistration.cs:70` fixa Testnet. Não basta trocar endpoint/flags. Falta contrato explícito de ambiente, capabilities, permissões, rollout e autorização Live.
2. **Resolver os P1 de despacho e validar TTL/concurrency.** Os gates devem existir no ponto da mutação, com testes observáveis dos cenários acima.
3. **Integração real controlada.** Submit/cancel, partial fills, timeout/UNKNOWN, reconexão, reconciliation, restart e drift devem ter evidência redigida em Testnet com chave/permissões verificadas. Não executado nesta auditoria: `not verified`. Registros antigos de probe read-only não equivalem a esse gate.
4. **Recuperação financeira completa.** `scripts/test-postgres-backup-restore.sh:52-78` restaura um banco de teste com marcador. `FullStateReconstruction.cs:60-64` compara journal, ordens, stream e metadados outbox. Faltam evidências de restore/reconstrução e comparação do estado financeiro/learning completo: fills, cost basis, decisões/checkpoints, policies, regimes, modelos e rewards. O dump pode incluir tabelas presentes; a lacuna é o cenário e a comparação que o drill comprova. Medir RPO/RTO no ambiente alvo, com retenção e recuperação operacional aprovadas.
5. **Shadow/soak/chaos operacional e qualidade quantitativa.** Período, regimes, volume, reconexão, divergências, intervenção e thresholds aprovados; evidências locais sintéticas não bastam.
6. **Alert delivery e operação.** Há sink webhook implementado e opção JSONL; o default pode ser memória. Falta entrega real verificada, escalonamento, runbooks exercitados e telemetria da stack alvo. Compose de observabilidade é separado e o Compose da aplicação não declara wiring OTEL entre serviços.
7. **Artefatos implantáveis reproduzíveis.** CI não possui job Web, build/smoke das imagens ou scan de dependências/imagens/SBOM. Dockerfiles usam tags mutáveis, não copiam `global.json` e restore não é locked. Pinning, build/runtime compatíveis, atualização de dependências e rollback precisam de evidência. `dotnet restore --locked-mode` no CI não substitui lockfiles ausentes.
8. **Promoção vigente e segregada.** RiskPolicy concreta aprovada, PRM, CanaryPlan, limites de capital/símbolos/perdas, critérios de parada, owners, aprovação humana, validade e commit exato. PRM 20260921 refere `efc6826`, expirou em **2026-09-30** e declara No-Go Testnet/Live. O README de readiness mantém No-Go e contém descrições antigas; reconciliar evidências novas sem sobrescrever snapshots históricos.

O checklist ainda registra rotação/verificação de chaves como pendente. Essa anotação é histórica; a auditoria não verificou credenciais atuais e não afirma que uma chave atualmente usada esteja exposta. Rotação, permissões, IP restriction e withdrawals disabled precisam de evidência atual redigida.

## Pontos sólidos confirmados

- Default PAPER/NO_TRADE; Testnet exige múltiplos gates de configuração explícitos.
- `RecordExecutionAsync` grava fill, journal/postings, cost basis e trade conhecido em transação serializável, com replay ligado ao journal.
- Decisão/outbox/checkpoint possuem caminho de commit atômico e teste PostgreSQL executado.
- OMS, fees, reconciliação, Account Stream, filtros, stale data, governança e observabilidade têm código e cobertura automatizada relevantes.
- API/container com usuário sem privilégios e portas host em loopback no Compose local.

O SDK .NET 11 RC1 **não foi classificado como bloqueio isolado**: a [publicação oficial da Microsoft](https://devblogs.microsoft.com/dotnet/dotnet-11-rc-1/) informa licença go-live. O problema observado é a ausência de paridade/reprodutibilidade comprovada das imagens e evidência operacional do produto.

## Sequência recomendada e critérios de encerramento

| Ordem | Entrega | Critério observável |
|---|---|---|
| 1 | Segurança do despacho | Zero submit após parada/TTL; replay pós-aceite consulta e recupera; política/contexto válidos na mutação; concorrência exercitada. |
| 2 | Contabilidade e visibilidade | Fees com unidade correta; P&L por janela; falhas explícitas; API representa estado do Worker; proteção uniforme de rotas. |
| 3 | Artefato operável | Imagens buildadas, Web acessa API, probes/alertas/exporter funcionam, scans e rollback exercitados. |
| 4 | Testnet e recuperação | Evidência real autorizada de ciclo completo e falhas; restore com estado financeiro completo e RPO/RTO. |
| 5 | Candidato quantitativo | OOS/walk-forward/soak do modelo e execução atuais, thresholds aprovados e learning loop rastreável. |
| 6 | Promoção Live | Ambiente suportado, PRM/Canary/RiskPolicy vigentes no commit aprovado e autorização humana específica. |

## Handoff

- Resultado: NO-GO Live; base de desenvolvimento local avançada, sem aprovação de produção/Testnet mutável.
- Commits criados: nenhum.
- Arquivos de produto alterados: nenhum. Artefato adicionado: este relatório.
- Validações: tabela acima; 651 testes existentes aprovados e dois comportamentos inseguros reproduzidos offline.
- Lacunas: integração financeira real, DR/soak de produção, métricas quantitativas representativas, supply chain e aprovações vigentes — `not verified` quando não executadas.
- Próximo passo: uma fatia de correção dos gates de despacho e recuperação pós-aceite, com testes de regressão, antes de habilitar qualquer envio externo.
