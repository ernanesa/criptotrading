# Threat model local — CryptoTrading

**Escopo:** API, Worker, PostgreSQL local, arquivos `.env`, recorder/replay,
adapter Binance read-only e operador local. **Fora do escopo:** produção,
AWS, custódia real e endpoints de envio/cancelamento.

## Ativos e objetivos

- credenciais Testnet: nunca aparecer em Git, logs, métricas ou exceções;
- ledger/journal/outbox: integridade, append-only e idempotência;
- RiskPolicy, SafeMode e KillSwitch: mudanças auditáveis e fail-closed;
- datasets e resultados de backtest: proveniência, checksum e reprodutibilidade;
- host, banco e backups locais: acesso restrito e recuperação testável.

## Ameaças e controles

| ID | Ameaça | Controle atual | Evidência pendente |
|---|---|---|---|
| TM-01 | vazamento de chave | `.env` ignorado, redaction, secret scan, permissões mínimas | rotação após integração |
| TM-02 | replay/duplicação de ordem | `clientOrderId`, idempotência, outbox lease/dedup | recovery após crash |
| TM-03 | drift de saldo/fill | reconciliação read-only e SafeMode | execução periódica automatizada |
| TM-04 | alteração indevida de risco | RiskPolicy versionada, auditoria e reset autorizado | revisão independente |
| TM-05 | dataset adulterado | checksum por registro e fingerprint do replay | dataset OOS versionado |
| TM-06 | indisponibilidade do banco | readiness, outbox persistente e retry | drill backup/restore |
| TM-07 | abuso de endpoint local | bind loopback, sem endpoints de ordem, redaction | threat test HTTP |
| TM-08 | supply-chain | lockfile, build/test/format/secret scan no CI | SBOM e pin de imagens |

## Regras de aceite

Qualquer dúvida sobre identidade, estado de ordem, saldo, integridade do
dataset ou permissão deve resultar em bloqueio. Não existe fallback para
"tentar novamente" uma operação financeira ambígua.
