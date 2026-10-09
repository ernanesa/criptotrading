# Exceção temporária de toolchain — .NET 11 RC — 2026-10-08

**Escopo:** manter a toolchain atual durante a fase local-first. Esta exceção não autoriza recursos OCI, deploy, Testnet mutável, canary ou ordens reais.

## Decisão registrada

O usuário decidiu manter, por enquanto, o SDK .NET 11 RC pinado. A decisão cobre somente a continuidade do desenvolvimento e validação local enquanto o restante dos gates permanece `NO-GO`. Ela não representa aprovação de produção nem amplia a autorização de execução financeira.

## Toolchain e dependências afetadas

- `global.json` fixa SDK `11.0.100-rc.1.26425.128`, com `rollForward: latestPatch` e `allowPrerelease: true`.
- `Directory.Build.props` define `TargetFramework=net11.0` e `LangVersion=preview` (C# 15 preview).
- Pacotes .NET Microsoft incluem versões RC alinhadas ao SDK, como `Microsoft.Extensions.Hosting` e `Microsoft.Extensions.Diagnostics.HealthChecks` `11.0.0-rc.1.26425.128` e `Microsoft.AspNetCore.OpenApi` `11.0.0-rc.1.26425.128`.
- A solução e `packages.lock.json` devem continuar em modo locked; não atualizar versões sob esta exceção sem revisão separada.

## Risco e prazo

Pré-lançamentos podem mudar comportamento, compatibilidade, suporte e disponibilidade de correções. Trate isso como risco de desenvolvimento; não assuma suporte GA/SLA. A exceção expira no próximo gate de decisão de toolchain e deve ser revisada **antes da escolha ou implantação OCI (P3-1)**, mesmo que ainda não exista uma data de calendário aprovada. Owner e data de revisão precisam ser registrados nesse gate. Até lá, mudanças Live/produção seguem bloqueadas.

## Contingência e aceite futuro

Não há rollback validado para OCI nesta revisão. Antes de qualquer promoção, fixe o SHA do último artefato aceito e a versão SDK/dependências correspondente; demonstre restore/build/teste e recuperação de banco nesse artefato em ambiente isolado. Se esse artefato imutável ou seu procedimento de retorno não puder ser reproduzido, mantenha `NO-GO`.

Antes de OCI, reavalie se existe versão estável suportada compatível, migre em mudança isolada e valide lockfiles, restore, build, testes, format, imagens, scans e restore/rollback. Se a decisão for renovar a exceção, registre justificativa, owner, evidência, mitigação e nova janela de expiração; nunca prorrogue automaticamente.

## Evidência atual

O SDK e as versões acima foram conferidos em `global.json`, `Directory.Build.props` e projetos locais nesta base. Isso confirma a configuração versionada, não suporte externo nem prontidão. A suíte e build locais registrados no [plano de prontidão](production-readiness-plan-2026-10-08.md) foram executados numa árvore intermediária; devem ser repetidos depois dos patches finais antes de servir como evidência do estado atual.
