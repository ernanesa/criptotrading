# Envelope de entrega operacional — 2026-10-07

Base de código: `7419aa7099c18dd84c22065b5c956400dabd42a8`.
Este registro descreve a árvore de trabalho resultante e as evidências locais listadas abaixo; não é uma aprovação para operar capital real.

## Estado executivo

**NO-GO para Live e para promoção de produção.** Os gates locais de despacho, persistência, recuperação, API/Web e observabilidade avançaram e têm evidências locais. O runtime ainda não oferece execução Live: existe apenas o contrato de promoção RSA, sem integração de envio Live. Nenhuma ordem foi enviada neste trabalho; os cenários de runtime foram PAPER/NO_TRADE.

Owners registrados no handoff original: **Terra** (implementação) e **Luna** (finalização documental). Nesta consolidação, **root** conduz os checks finais e **Luna** mantém estes documentos; Terra não participa da execução atual. Esses papéis não aprovam promoção nem substituem os gates humanos externos.

## Critérios D01–D16

| ID | Estado | Implementação/evidência e limite |
|---|---|---|
| D01 | Implementado localmente | KillSwitch/SafeMode, runtime e policy são revalidados na fronteira de submit. Testes locais de guardas; não é integração de venue. |
| D02 | Implementado localmente | Fence durável antes do efeito; retry pós-aceite consulta estado e evita repetir submit. Cobertura Worker e PostgreSQL local. |
| D03 | Implementado localmente | TTL, frescor de mercado/comissão e policy/notional são revalidados antes do envio. |
| D04 | Implementado localmente | Lease/owner/fence limitam a intenção a um início de submit; cenários unitários e PostgreSQL exercitados localmente. |
| D05 | Implementado localmente | Avaliação de risco recebe observações de mercado em vez de volatilidade, slippage ou liquidez fixas. Não atesta qualidade de feed real. |
| D06 | Implementação local; validação local aprovada | Fees mantêm unidade e valuation; colisões divergentes falham. Correção recente de valuation BNB aceita somente preço confiável, correspondente e com até 2 minutos, sem timestamp futuro; falha de valuation aborta a transação e registra o evento bruto durável em SafeMode. A revisão independente identificou esse P1 e ele foi implementado; correção validada nos testes de domínio e PostgreSQL. |
| D07 | Parcial | Snapshots e fluxos sustentam janelas financeiras e ausência de histórico é explícita. Ingestão auditável automática de cash flows ainda não existe; capital inicial permanece `NULL` e retornos não podem ser afirmados sem esse dado. |
| D08 | Implementado localmente | API usa token de arquivo no proxy interno autenticado; falhas são explícitas e a Web não usa fallback financeiro fictício. Smoke local de container/API. |
| D09 | Implementado localmente | Readiness/dependências e estado operacional são expostos com separação API/Worker. Painel externo e acesso remoto não configurados. |
| D10 | Parcial | Imagens de API, Worker e Web e smoke local implementados. CI remoto, security scans remotos, bases fixadas por digest; scans e rollback no destino não verificados. |
| D11 | Parcial | Logs JSON/OTel/Loki e consulta local aprovados; rotação Docker 10 MiB × 5 e retenção Loki 14 dias configuradas. Entrega externa de alertas/on-call não verificada. |
| D12 | Parcial | Rewards duráveis e hidratação de modelos implementadas. OOS representativo do candidato ativo e evidência de Testnet/soak longo permanecem pendentes. |
| D13 | Implementado e validado no harness local | O [relatório corrigido](evidence/postgres-recovery-2026-10-07.md) compara 35 linhas em 36 tabelas, com hashes correspondentes; checksum verificado, cópia corrompida rejeitada, backup em 193 ms, restore em 657 ms e validação em 3.719 ms. É evidência de fixture local, sem garantia de RPO/RTO de produção. O relatório anterior [superseded](evidence/postgres-recovery-superseded-2026-10-07.md) é inválido para provar cobertura financeira (1 tabela, 0 linhas) e foi preservado como histórico. |
| D14 | Parcial | Um cenário local de caos PAPER passou e dois ciclos de startup/restart de 12 s permaneceram em PAPER/NO_TRADE sem ordens. Testnet mutável e soak longo representativo não executados. |
| D15 | Parcial | Contrato RSA de promoção Live implementado; execução Live continua não suportada pelo runtime. Contrato não habilita envio nem equivale a aprovação. |
| D16 | Parcial | Artefatos e rastreabilidade local atualizados. Revisão independente apontou um P1 de valuation BNB, já implementado; correção e consolidação local validadas pelo root. Gates externos também permanecem pendentes. |

## Evidência local registrada

- Testes .NET: **645 passaram**, zero falhas, 11 assemblies, conforme `/tmp/cryptotrading-delivery-tests.log`; API (29) e Worker (95) foram revalidados após os últimos ajustes de logging.
- Integração PostgreSQL: **27 passaram**, conforme `/tmp/cryptotrading-delivery-postgres.log`.
- Web: **26 passaram**, e lint/build terminaram sem falhas, conforme atualização do executor. Essa validação não cobre CI remoto, scans de imagem/dependências ou o destino de produção.
- Caos PAPER: **1 cenário passou**, em `/tmp/cryptotrading-delivery-chaos.log`.
- Startup/restart: **2 ciclos de 12 segundos**, PAPER/NO_TRADE, migrations e recuperação fail-closed, sem ordens, em `/tmp/cryptotrading-delivery-startup.log`.
- Backup/restore: drill local corrigido, relatório acima; o relatório antigo com manifesto de 1 tabela é inválido e superseded.
- Collector/Loki: stack local e consultas verificadas pelo executor; alertas não foram entregues a webhook externo.

Os logs em `/tmp` são evidência temporária da execução local, não artefatos permanentes nem prova de ambiente remoto. Resultados locais não constituem aprovação externa.

## Bloqueios para promoção

- Suporte de execução Live não implementado; capital inicial/limites operacionais externos não definidos.
- Testnet real/mutável, OOS representativo e soak longo ainda pendentes.
- CI remoto, scans de segurança, imagens por digest e observabilidade do destino não verificados.
- Entrega de webhook e escalação on-call externas não verificadas.
- Destino de implantação, acesso ao painel, owners e processo de recuperação do ambiente alvo pendentes.

Mantenha PAPER/NO_TRADE. BUY e SELL são bloqueados quando SafeMode/KillSwitch estão ativos. Isso descreve os gates de parada, não significa que toda estratégia ou stop automático já esteja habilitado. Não converter esta tabela em autorização de execução.

## Handoff

- Resultado: gates locais parciais; **NO-GO Live/produção**.
- Commit criado por este trabalho: nenhum.
- Arquivos documentais: este envelope e `production-operations-runbook.md`.
- Validações executadas pelo autor deste documento: nenhuma; resultados acima foram conferidos nos artefatos locais existentes e no relato do executor.
- Próximo passo: definir ambiente/acesso, fechar os gates externos e implementar a execução Live antes de qualquer promoção. Promoção continua dependendo de evidências externas e autorização humana específica.

A evidência consolidada e os hashes dos checks estão em [production-validation-2026-10-07.md](production-validation-2026-10-07.md). Logs estruturados incluem UTC, correlação e stack de falhas sem mensagem/payload bruto da exceção. Não existe medição automática de tokens/custo por agente nesta execução; a economia foi aplicada por fatias curtas, donos distintos e checks direcionados.

A tela de pesquisa agora informa ausência de artefatos OOS/backtest sem retornos, curvas ou benchmarks fictícios. Campos desconhecidos da RiskPolicy não viram limites zero. O log de contabilização inclui unidades, quantidade, preço, comissão, P&L, custo médio, status de fee e timestamp; o sink central foi consultado após subir os serviços isolados.
