# Shadow operational gate local

O componente `ShadowOperationalGate` transforma métricas de uma janela Shadow
em uma decisão de qualidade explicitamente fail-closed. Os critérios podem
limitar observações mínimas, divergência, stale data, intervention-rate e
duração; quando houve stale, o padrão também exige evidência de recovery.

O runner local é:

```bash
./scripts/test-shadow-operational-gate.sh
```

Ele executa apenas os cenários determinísticos marcados
`Category=ShadowOperational`, recusa credenciais Binance, não acessa rede e
gera um relatório redigido em `artifacts/shadow/`. Uma falha retorna código não
zero e nunca autoriza execução; o resultado deve ser interpretado como
`NoTrade`/investigação até revisão operacional.

As métricas usam `ShadowOperationalMetrics.From` para associar o resultado de
divergência do replay a duração e intervenções explicitamente registradas. A
intervention-rate não é inferida de uma ordem nem de um alerta: o operador ou
o harness deve fornecer a contagem, mantendo a semântica auditável.

Este gate prova somente a avaliação local de thresholds e os caminhos de
falha. Não representa um período real de mercado, volume/regimes reais,
intervenções humanas em produção, alert delivery externo ou autorização de
Canary/Live.
