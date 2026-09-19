# OOS/walk-forward sensitivity local

`./scripts/test-oos-sensitivity.sh` executa uma matriz pequena e explícita de
sensibilidade sobre o dataset sintético versionado `synthetic-btcusdt-oos/1.0.0`.
Cada caso percorre as mesmas seis janelas cronológicas (60 barras de treino,
20 barras OOS e passo de 20), preserva seu fingerprint de configuração e é
avaliado novamente para verificar reprodutibilidade.

O harness não escolhe um vencedor, não acessa rede, não carrega credenciais e
não envia ordens. O relatório redigido é gravado em `artifacts/oos/`, diretório
ignorado pelo Git. O runner falha fechado quando credenciais Binance estão
presentes ou quando o filtro não executa um teste aprovado.

```bash
./scripts/test-oos-sensitivity.sh
```

Este é um gate de pipeline local, não uma conclusão estatística. O dataset é
intencionalmente sintético; ainda são obrigatórios dataset de mercado real com
hash/versionamento, OOS/walk-forward, sensitivity, custos de execução medidos
e aprovação independente antes de qualquer promoção.
