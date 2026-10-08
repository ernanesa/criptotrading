# Evidência local de validação — entrega 2026-10-07

Base: `7419aa7099c18dd84c22065b5c956400dabd42a8`. Árvore de trabalho sem commit de entrega. Evidências executadas em 2026-10-07/08 UTC, isoladas de contas Binance. **NO-GO Live/produção**, conforme [envelope](production-delivery-2026-10-07.md).

Hashes abaixo identificam os logs temporários em `/tmp/cryptotrading-delivery-<nome>.log`; este registro conserva o resultado e escopo, sem payload financeiro privado. Nenhum teste simulado constitui integração Binance real.

| Check | Comando/escopo | Resultado inspecionado | SHA-256 do log |
|---|---|---|---|
| Solução .NET Release | `dotnet test CryptoTrading.slnx -c Release --no-restore` | 645/645, 11 assemblies | `994fbf11bb8095485768934562d069b8caf63c56ac9bf83338574612ac45cd83` |
| API final | `dotnet test tests/CryptoTrading.Api.Tests/CryptoTrading.Api.Tests.csproj -c Release --no-restore` | 29/29 | `b0d3051006246d50c196626f5d46b755eec53b276921c2f8beb4d112de0b99b6` |
| Worker final | `dotnet test tests/CryptoTrading.Worker.Tests/CryptoTrading.Worker.Tests.csproj -c Release --no-restore` | 95/95 | `a0f509bf84c5463f516abdb87e31326891ed6022fcae392a5a2086d7870585ac` |
| PostgreSQL | `bash scripts/test-postgres-integration.sh` | 27/27, PostgreSQL real local | `88cfcd26e044fd49a20f04c548427df8f57e05636c41e7851ea1cad51ca07206` |
| Web | `npm run lint; npm test -- --run; npm run build` | 26/26; lint e build aprovados | `ea4cb615d9131f548c131aee2657411ec0461eee7e33c6d5b333bd4849233176` |
| Imagens/telemetria | `bash scripts/test-container-smoke.sh` | API + Worker + Web; proxy autenticado; log consultado no Loki | `0cb7dcd5d52d2757276770c2c026f9a8a1504dfe1a9f1bc653ff1a55068c16c2` |
| Startup/restart | `bash scripts/test-worker-persistence-startup.sh` | 2x12s PAPER/NO_TRADE, sem ordens | `e5f64d63a6eb32db733cb575f718608e977d30fc6dff60d07723354ef16a836e` |
| DR | `bash scripts/test-postgres-backup-restore.sh` | 35 linhas/36 tabelas; hashes iguais; RTO local 657ms | `15fd8a89d79052ffb60b9c79cd50bf83c28bfe9a3be5c3e6d36f28b17fa3843f` |
| Caos PAPER | `cenário local Category=PaperChaos` | 1 aprovado; simulador, sem prova de venue | `8f9797e9ccbe308a5874bf976222b976df73ad03c32f9763c8d698ec123f2b4b` |
| Format | `dotnet format CryptoTrading.slnx --verify-no-changes --no-restore` | aprovado | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |
| Segredos | `bash scripts/scan-secrets.sh` | working tree versionada e não ignorada, nomes apenas | `122502ac5aed18d209eb22f684f92c34c216f2142c1a65e01c98be81443fda2a` |
| Rastreabilidade | `bash scripts/test-traceability-validator.sh` | 126 IDs + testes positivos/negativos | `3d468ff2fa6528c78486feec771fef93323801127ca708c2c027c35174df156d` |
| Readiness validator | `bash scripts/test-readiness-validator.sh` | contratos positivos/negativos; não é aprovação humana | `2842bfb00b498bca9b6a70379ed8a5ac4b52e13b2c15ba013e5a730f4402ff03` |

Restore locked aprovado; relatório NuGet local sem High/Critical, incluindo dependências transitivas. O checker foi exercitado com zero findings, finding High, relatório incompleto e relatório com erro. `npm audit --omit=dev` local sem vulnerabilidades. Os scans de imagens/attestations definidos no CI remoto e os scans das imagens de observabilidade permanecem **not verified**.

[Relatório DR corrigido](evidence/postgres-recovery-2026-10-07.md): backup 193ms, restore 657ms, verificação 3719ms, 35 linhas em 36 tabelas e teste de corrupção rejeitado. O relatório anterior 011413 está **superseded**: Docker exec consumia o stdin do loop, resultando na comparação de somente uma tabela. O harness corrigido fecha stdin nessa chamada e exige fixture e quantidade mínima de tabelas.

Revisão independente encontrou fees em terceiro ativo ausentes no custo/P&L. Correção exige marca válida com até 2min, correspondente e não futura, cobra BNB em quote e rejeita contabilização sem valuation; testes de compra, venda, rebate, erro de marca e rollback passaram. Base fees são debitadas uma vez pelo inventário total disposto. Sem reescrita de ledger imutável histórico: qualquer reconstrução histórica exige fonte e validação próprias.

Pendências: adapter Live, promoção humana, Testnet mutável, OOS representativo, soak prolongado, cashflows auditáveis, ambiente/acesso remoto, CI/imagens aprovadas, backup agendado e alertas externos/on-call. O [runbook](production-operations-runbook.md) cobre apenas a operação local PAPER/NO_TRADE.

Bases de Node, nginx, PostgreSQL e observabilidade fixadas por digests de manifests oficiais consultados via `docker buildx imagetools inspect`; SDK/runtime .NET já estavam fixados. O CI registra em JSON todos os achados High/Critical das imagens e bloqueia os que têm correção disponível. Revisão estática independente aprovada; a execução remota permanece `not verified`. Os contextos Docker raiz e Web excluem arquivos privados e segredos.

O teste PostgreSQL consulta explicitamente `execution_fills` e `journal_entries` após rejeitar a fee BNB sem valuation: nenhum dos dois contém a identidade rejeitada. Exceções terminais de account stream/reconciliação são substituídas por mensagens genéricas antes de chegar ao Host; tipo e stack originais permanecem no evento estruturado privado.

Build Release da solução completa aprovado, incluindo ferramentas; log `/tmp/cryptotrading-delivery-build.log`, SHA-256 `4f0cff2d89124ef412e4a419545e819ea0c84fd38281b315ffdff14dadc22d4e`.
