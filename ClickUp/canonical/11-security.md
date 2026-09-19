# 11 - Segurança

## Threat model resumido

Ativos protegidos: API keys, capital, configurações de risco, conta administrativa, histórico de ordens, infraestrutura e supply chain.

Ameaças principais:
- vazamento de chave;
- alteração maliciosa de configuração;
- ordem indevida por bug/replay;
- dependency compromise;
- acesso indevido ao dashboard;
- SSRF/injection/XSS;
- imagem/container comprometido;
- secrets em logs/backups;
- permissões cloud excessivas.

Antes de canary, preencher `templates/threat-model.md` com trust boundaries, data flows, abuse/misuse cases, likelihood/impact, owner, controle preventivo/detectivo, evidência e risco residual. O modelo deve cobrir browser/control plane, Worker, banco, secret store, CI/CD, registry, observabilidade, operador e Binance.

## Binance keys

- withdrawals **sempre desabilitados**;
- permitir apenas endpoints necessários;
- IP allowlist quando possível;
- separar keys por ambiente;
- rotação planejada;
- Ed25519 quando requerido/recomendado pelo fluxo utilizado;
- nunca reutilizar key de produção em dev/paper.

## Secrets

### Ambiente local inicial
Segredos entram por arquivos locais fora do Git, com permissão mínima do sistema operacional, ou por Docker secrets quando aplicável. O repositório contém apenas exemplos sem valores. Backups, logs, shell history e evidence artifacts também passam por redaction/secret scanning.

### Cloud/hosting futuro
O mecanismo será escolhido somente quando um destino for aprovado. SSM/Secrets Manager, vault do provedor ou SOPS/age são opções, nunca requisitos do runtime local.

## Web

- autenticação forte;
- cookies HttpOnly/Secure/SameSite se sessão web;
- CSRF quando aplicável;
- CSP;
- input validation;
- rate limiting administrativo;
- ações live críticas com step-up/confirmation;
- audit trail imutável.
- MFA/WebAuthn para conta administrativa;
- step-up para CANARY/LIVE, RiskPolicy, credenciais, kill switch e reset;
- session timeout, revogação e rotação de tokens;
- proteção anti-replay para comandos críticos;
- control plane restrito por VPN/allowlist/zero-trust quando possível;
- caminho operacional autenticado para kill switch quando UI/API estiver indisponível.

OWASP ASVS 5.0 é referência de verificação, proporcional a uma aplicação administrativa privada. Controles selecionados devem usar IDs versionados no formato `v5.0.0-x.y.z`, com aplicabilidade e evidência.

## Supply chain

- pin/lock dependencies;
- Dependabot/Renovate;
- CodeQL;
- Trivy;
- secret scanning;
- GitHub branch protection;
- OIDC e credenciais efêmeras no CI, sem secrets de longa duração em workflow;
- signed/provenance artifacts e verificação de assinatura/digest no deploy;
- SBOM em releases.

Rotação de chaves, secrets e certificados deve ter procedimento, periodicidade, teste de revogação e evidência. Chaves por ambiente devem ser distintas; nenhuma chave live pode aparecer em Testnet, Demo, paper ou fixtures.

## Network

- banco não exposto publicamente;
- firewall local mínimo; nenhuma porta de banco exposta além do host/rede Compose;
- acesso remoto desabilitado por padrão; se futuramente necessário, exigir VPN/allowlist e autenticação forte;
- HTTPS;
- outbound necessário para Binance e serviços de observabilidade.

## Logs

Redaction de headers, signatures, cookies, API keys, connection strings e payloads sensíveis. Testes automatizados devem procurar padrões de secrets.

## Regra de segurança operacional

Se autenticidade do estado da conta não puder ser confirmada, o sistema entra em modo seguro e não abre novas posições.
