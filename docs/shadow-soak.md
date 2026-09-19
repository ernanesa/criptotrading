# Soak Shadow local

O comando `scripts/test-shadow-soak.sh` executa o cenário determinístico de
Shadow/Paper marcado com `Category=ShadowSoak`. O runner não carrega `.env`, não
aceita credenciais Binance e não possui cliente de exchange nem capacidade de
submeter ou cancelar ordens.

## Execução

```bash
./scripts/test-shadow-soak.sh
```

Por padrão, o runner repete a janela determinística três vezes e só retorna
sucesso quando todas as repetições produzem um resumo aprovado. A quantidade
pode ser ajustada para um lote local de 1 a 100 execuções:

```bash
SHADOW_SOAK_RUNS=10 ./scripts/test-shadow-soak.sh
```

Cada execução é exibida no terminal e os resumos são agregados no relatório
Markdown redigido em `artifacts/shadow/`, incluindo o resultado individual de
cada repetição. Uma falha em qualquer repetição faz o runner falhar fechado,
mesmo que as demais sejam aprovadas.

No Worker, o drill integrado é opt-in com `WORKER_SHADOW_ENABLED=true` e exige
`WORKER_NO_TRADE=true`; ele executa uma única janela local no startup e não
habilita o dispatcher de ordens.

Quando integrado ao Worker, cada execução acrescenta uma linha JSONL em
`WORKER_SHADOW_REPORT_DIRECTORY` (por padrão `artifacts/shadow`) e publica
alertas locais deduplicados para divergência, stale e recovery. Esses alertas
não são entrega externa de on-call.

O teste deve ser executado com o SDK/restore da solução já preparado. O runner
recusa a execução quando `BINANCE_API_KEY` ou `BINANCE_API_SECRET` estão
presentes, e falha se o filtro não produzir um resumo de testes aprovado. Cada
execução cria um relatório redigido em `artifacts/shadow/`, diretório ignorado
pelo Git:

```text
artifacts/shadow/shadow-soak-<UTC>-<pid>.md
```

O relatório registra apenas resultado, janela UTC, filtro, resumo do runner e a
fronteira da evidência. A saída não copia o log completo do teste para evitar
que futuras mudanças de fixture transformem um valor de diagnóstico em um
vazamento de evidência.

## Limite da evidência

Um resultado `PASS` comprova somente o cenário local determinístico coberto
pelo teste: decisões da estratégia comparadas com observações do Paper,
stale/recovery e divergências controladas. O `PaperShadowSession` executa
decisões contra o `PaperExchange` determinístico e converte fills em
observações redigidas. Ele não comprova dataset real,
reconciliação autenticada da Testnet, entrega de alertas externos, RPO/RTO de
produção ou autorização de Canary/Live. Esses gates permanecem separados e
fail-closed.
