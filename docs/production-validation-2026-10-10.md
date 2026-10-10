# Evidência de validação de prontidão — 2026-10-10

**Estado: NO-GO para Live, Testnet mutável, OCI, deploy externo e promoção.** Este documento atualiza o snapshot corrente da árvore dirty e supersede o estado atual alegado no relatório 2026-10-09. Não converte resultado local em autorização financeira ou operacional.

## Estado Git e escopo

- HEAD observado: fc85321a7e6f4afe3d7b73468c316487ef00df2d, branch main alinhada a origin/main.
- No início do ciclo havia 13 caminhos modificados e docs/production-validation-2026-10-09.md não rastreado. As mudanças incluem guarda de envio, comissão, persistência, reconciliação, testes, workflow e pins de observabilidade. Recolher status ao congelar a versão final, pois os arquivos podem mudar durante o trabalho.
- Fingerprint do candidato local: manifest readiness/production-source-snapshot-2026-10-10.json, base commit fc85321a7e6f4afe3d7b73468c316487ef00df2d, árvore dirty identificada por 461 arquivos de código/testes/scripts/manifests/CI e digest SHA-256 c58b15bc4bbf6b083ff97e8447e292154dc91534d621a403ef0e767daf190b7c. Verificação reportou 461/461 sem alteração depois dos checks. O manifest exclui docs, artefatos ignorados, credenciais e runtime; CI remoto continua pendente por exigir commit SHA.
- Os resultados abaixo pertencem a esse snapshot local identificado. CI remoto e promoção exigem commit e gates próprios.
- Nenhum resultado autoriza credenciais, ordens, Testnet mutável, deploy, OCI, publicação ou contato externo. NO_TRADE permanece obrigatório.

## Evidência deste ciclo

| # | Gate | Estado e evidência comunicada | Limite |
|---:|---|---|---|
| 1 | Build local | **PASS** — zero warnings/erros, 3,47 s, no snapshot acima. | Comando: DOTNET_CLI_USE_MSBUILD_SERVER=0 bash scripts/dotnet-local.sh build CryptoTrading.slnx --no-restore --configuration Release --disable-build-servers -m:1 -p:UseSharedCompilation=false |
| 2 | Suíte local | **PASS** — 673/673 testes, zero falhas/skips, 2,765 s; resumo em /tmp/cryptotrading-snapshot-tests-20261010.log. | Comando: env -u POSTGRES_CONNECTION_STRING bash scripts/dotnet-local.sh test CryptoTrading.slnx --no-build --configuration Release --verbosity minimal |
| 3 | Relógio determinístico | **PASS** — Worker 110/110 em 0,430 s; revisão determinística sem finding e suite pós-alteração passou. | Critério limitado à regra de freshness/tempo coberta; não afirma que todos os testes do sistema sejam determinísticos. |
| 4 | Rastreabilidade | **PENDENTE**; subcheck de dados públicos passou. | Dataset contentHash, quality datasetContentHash e fingerprint do manifesto coincidem; contagens 1.461/1.461, UTC estrito, 0 gaps. Rastreabilidade completa de requisitos permanece incompleta. |
| 5 | Persistência e recovery local | **PASS local** — relatório /tmp/cryptotrading-final-snapshot-recovery-20261010/full-recovery-20261010T121131Z-p9XWnY.md; ledger 1.777 ms, full-state manifest 1.793 ms, PostgreSQL integration 9.158 ms e fixture backup/restore 12.320 ms. | Passa o drill local identificado; não fecha backup operacional, venue real ou RPO/RTO de produção. |
| 6 | Backup operacional local | **PENDENTE**; ferramenta manual e teste de contrato passaram. | bash scripts/test-backup-postgres.sh valida com Docker falso permissões, diretório privado, checksum, pg_restore --list e limpeza em falhas. A ferramenta manual não agenda, define retenção, criptografa, monitora capacidade nem prova restore operacional periódico. RPO 1 h / RTO 4 h são propostas sem aprovação/medição; offsite não presumido. |
| 7 | OMS/guarda financeira local | **PASS no escopo local** — testes e revisão de fonte confirmados pelo agente financeiro. | Não prova venue real, policy aprovada, máximo de perda ou autorização humana. |
| 8 | Reconciliação real | **PENDENTE**. | Sem evidência autorizada de fills/ordens/estados ambíguos de venue real. |
| 9 | Capital/BRL aprovado | **PENDENTE**. | R$100, R$2/dia e R$5 absoluto são valores de draft, não política aprovada ou teto de perda garantido. |
| 10 | OOS/custos/robustez | **PENDENTE**; existe baseline público exploratório completo. | Há 70 janelas walk-forward e comparação simulada; os limites de fill/custo e demais requisitos de robustez descritos abaixo impedem fechar OOS ou promoção. |
| 11 | Segurança de imagens | **FAIL/bloqueador**. | Snapshot anterior registra High/Critical em Grafana, Alertmanager e PostgreSQL; repetir scan por digest com DB atual. |
| 12 | Segredos/IAM/supply chain | **PENDENTE**. Secret scan e teste fail-closed passaram no snapshot. | IAM e proveniência do build não demonstrados; o scan não fecha o critério completo. |
| 13 | Observabilidade local | **PASS no escopo local** — 38 testes e dois contratos de diagnóstico passaram no snapshot. | Não comprova receiver/on-call externo, que não é requisito desta rubrica. |
| 14 | Logs operacionais consultáveis | **PENDENTE**. | Diagnósticos locais passaram, mas não está demonstrado o protocolo de monitoramento por logs para falhas financeiras, retenção/capacidade e execução do runbook. Receiver e on-call externos não são requisito assumido. |
| 15 | Chaos/soak | **PENDENTE**. | Sem janela completa com limiares e commit candidato evidenciada. |
| 16 | Testnet mutável | **PENDENTE e não autorizada por este registro**. | Não executar ordens sem autorização específica imediatamente antes da janela. |
| 17 | Capability Live | **PENDENTE / indisponível na auditoria atual**. | Adapter Live, contratos, OMS e recuperação de ambiguidade precisam de implementação e revisão crítica. |
| 18 | CI remoto no candidato | **PENDENTE**. | O manifest não é commit SHA; run 37862230794 é de 62687c9, outra base. CI no candidato segue não verificada. |
| 19 | Deploy local/rollback | **PENDENTE**. | Deve ser ensaiado localmente e sem ordens; OCI é etapa posterior, separada. |
| 20 | GO humano | **PENDENTE; NO-GO**. | Sem aprovação independente e vigente ligada a SHA, ambiente e escopo. |

## Replay público e baseline exploratório

O dataset [BTCUSDT 1d completo](../artifacts/public-klines/btcusdt-1d-audit-20261010-complete/dataset-manifest.json) cobre 2022-10-10 00:00:00 UTC até 2026-10-09 23:59:59.999 UTC. Manifesto e [validação de cadência](../artifacts/public-klines/btcusdt-1d-audit-20261010-complete/offline-cadence-validation.json) confirmam 1.461 registros/replays, 0 gaps, sequência estritamente crescente e UTC. Fingerprint de conteúdo 83E115C4B0F3C3B8DCA44234E9DA42FD6AE1899C303CE6CAAD55AA065B79EBF6 coincide entre manifest replay_fingerprint, OOS dataset contentHash e quality datasetContentHash. O quality replayFingerprint 50A16F4D68020AAC6EF662A27F2EF405BA259B0C21C2A690DAD67804644AAF4E é um hash derivado separado do replay gate, não o contentHash do manifesto.

O [relatório OOS completo](../artifacts/oos/public-oos-audit-20261010-complete.json) contém 70 janelas (60 barras de treino, 20 OOS), capital simulado de 10.000 unidades, fee 10 bps, slippage 5 bps e fill assumido no fechamento diário. Não modela spread observado, latência, fills parciais, fila ou impacto de mercado; esse capital não é BRL nem os R$100 de planejamento. A comparação agregada reporta beatsBuyAndHold=false. Não interpretar métricas da simulação como retorno real ou recomendação; promotion_authorized=false.

Os artefatos de captura e OOS iniciais [manifest](../artifacts/public-klines/btcusdt-1d-audit-20261010/dataset-manifest.json) e [relatório](../artifacts/oos/public-oos-audit-20261010.json) estão preservados, mas superseded e excluídos de conclusões: registravam 1.461 versus 1.460 replay records e hashes de dataset inconsistentes entre manifesto e relatório. O conjunto completo acima corrige a proveniência e contagem. O gate 4 ainda aguarda rastreabilidade completa; o gate 10 continua pendente de custos/risco/robustez e paper soak.

## Cobertura dos gates

O plano [production-readiness-plan-2026-10-10.md](production-readiness-plan-2026-10-10.md) define 20 critérios binários com peso igual de 5 pontos percentuais cada. **Cobertura consolidada do snapshot local: 6/20 (30%)**, com 14/20 (70%) pendentes ou bloqueados; os seis PASS são build, suíte, relógio determinístico, persistência/recovery, OMS/guarda e observabilidade local. O fingerprint está no manifest acima; CI remoto e promoção exigem commit SHA e gates próprios. O gate de logs operacionais permanece pendente. Esta porcentagem mede cobertura dos critérios e não prontidão de produção, chance de segurança ou autorização.

NO-GO permanece; todos os gates externos e humanos seguem independentes.

## Próxima evidência local

Os checks locais do fingerprint acima foram concluídos. Repetir os comandos abaixo se mudar qualquer arquivo incluído no manifest; preservar nomes de projeto/portas/volumes isolados e atrelar as novas saídas ao fingerprint atualizado.

    DOTNET_CLI_USE_MSBUILD_SERVER=0 bash scripts/dotnet-local.sh build CryptoTrading.slnx --no-restore --configuration Release --disable-build-servers -m:1 -p:UseSharedCompilation=false
    env -u POSTGRES_CONNECTION_STRING bash scripts/dotnet-local.sh test CryptoTrading.slnx --no-build --configuration Release --verbosity minimal
    bash scripts/test-postgres-integration.sh
    bash scripts/test-full-recovery-drill.sh
    bash scripts/test-backup-postgres.sh
    bash scripts/scan-secrets.sh
    ./scripts/validate-traceability.sh

Também passaram no fingerprint a verificação de formato, o secret scan, o teste fail-closed do scanner e o contrato do backup manual. O secret scan não demonstra IAM/proveniência; o backup contract usa Docker falso e não configura uma agenda nem prova restore operacional periódico.

Isolar nomes de projeto, portas e volumes do PostgreSQL; registrar cleanup e volumes remanescentes. Nunca incluir credenciais em argv, stdout ou relatórios. Resultado local não autoriza serviço ligado a venue nem transmissão de ordem.

## Bloqueadores e decisões abertas

- CVEs High/Critical e scan atualizado de imagens permanecem bloqueadores.
- Policy de risco, saldo, conversão BRL/USDT, taxas e reserva não foram aprovados como limites operacionais. R$2/dia e R$5 absoluto não garantem a perda máxima em gaps, slippage, falhas ou fills parciais.
- Protocolo de logs consultáveis para saúde, falhas financeiras, retenção/capacidade e runbook, além de backup operacional local criptografado, retenção e restore, aguardam implementação e evidência. RPO 1 h e RTO 4 h são propostas, ainda sem aprovação ou medição. Não se presume offsite, receiver ou on-call externo. Logs devem evitar credenciais e payload sensível; a revisão diária local e os limites de retenção/capacidade precisam constar do runbook.
- OOS, robustez, chaos/soak, reconciliação real e Testnet autorizada ainda não têm evidência suficiente.
- Live não está disponível; OCI, canary e promoção são etapas posteriores com decisão, autorização e evidência próprias.
- O usuário manteve a exceção documentada do SDK .NET 11 RC. Ela não é bloqueio obrigatório de migração nesta revisão; reavaliar SDK antes de uma decisão futura de OCI, sem presumir expiração da decisão atual.

## Handoff

- **Resultado:** NO-GO; 6/20 gates locais cobertos (30%), todos os demais pendentes ou bloqueados.
- **Commits:** nenhum criado neste relatório; base observada fc85321.
- **Arquivos:** adicionar o plano e este relatório; atualizar apenas o aviso no topo do snapshot 09/10.
- **Validações executadas por este documento:** nenhuma. Os PASS são reportados pelo responsável/orquestrador e precisam permanecer atribuídos até evidência reproduzível anexada.
- **SDK:** o usuário mantém a exceção .NET 11 RC documentada para trabalho local; migração estável não é bloqueio obrigatório nesta revisão. Reavaliar antes de OCI conforme decisão futura, sem presumir expiração hoje.
- **Próximo passo:** fechar rastreabilidade, backup operacional e demais gates locais; executar CI no commit candidato e manter ações externas bloqueadas sem autorização específica.
