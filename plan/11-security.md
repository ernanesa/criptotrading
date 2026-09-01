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

## Binance keys

- withdrawals **sempre desabilitados**;
- permitir apenas endpoints necessários;
- IP allowlist quando possível;
- separar keys por ambiente;
- rotação planejada;
- Ed25519 quando requerido/recomendado pelo fluxo utilizado;
- nunca reutilizar key de produção em dev/paper.

## Secrets

### AWS
SSM Parameter Store `SecureString` para baseline de baixo custo; Secrets Manager se rotação/recursos justificarem.

### Oracle/VPS
Secrets injetados no runtime por arquivo protegido/secret manager adequado; nunca em compose versionado. Considerar SOPS/age para configuração cifrada versionável.

## Web

- autenticação forte;
- cookies HttpOnly/Secure/SameSite se sessão web;
- CSRF quando aplicável;
- CSP;
- input validation;
- rate limiting administrativo;
- ações live críticas com step-up/confirmation;
- audit trail imutável.

OWASP ASVS 5.0 é referência de verificação, proporcional a uma aplicação administrativa privada.

## Supply chain

- pin/lock dependencies;
- Dependabot/Renovate;
- CodeQL;
- Trivy;
- secret scanning;
- GitHub branch protection;
- signed/provenance artifacts quando maturidade permitir;
- SBOM em releases.

## Network

- banco não exposto publicamente;
- firewall/security group mínimo;
- SSH restrito, preferir SSM Session Manager na AWS;
- HTTPS;
- outbound necessário para Binance e serviços de observabilidade.

## Logs

Redaction de headers, signatures, cookies, API keys, connection strings e payloads sensíveis. Testes automatizados devem procurar padrões de secrets.

## Regra de segurança operacional

Se autenticidade do estado da conta não puder ser confirmada, o sistema entra em modo seguro e não abre novas posições.
