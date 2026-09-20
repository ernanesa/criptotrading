# Local quality gates paralelos

Para validar rapidamente a fundação local antes de uma alteração ou revisão,
execute:

```bash
./scripts/test-local-gates.sh
```

O runner inicia gates independentes em paralelo: readiness, traceability,
segredos, preflight Binance sem credenciais, smoke da API, controle
operacional, Shadow, OOS e reconstrução do ledger. O Paper chaos também faz
parte do agregado, mas é executado isoladamente porque recompila o projeto e
isso evita contenção de arquivos intermediários do MSBuild. A API recebe
uma porta local derivada do processo para evitar colisão com outra execução.

O relatório agregado contém somente resultado e duração em
`artifacts/quality/local-gates-<timestamp>-<pid>.md`, com modo `0600`. Saída
dos filhos permanece em um diretório temporário privado e é descartada; uma
falha deve ser reproduzida executando o gate individualmente para diagnóstico.

Este comando não prova reconciliação autenticada, permissões reais da conta,
ordens Testnet, Shadow com mercado real ou aprovação de promoção.
