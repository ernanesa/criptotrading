# Governança local de experimentos

`ExperimentRegistry` implementa a primeira camada de `US-054` antes de
qualquer OOS com dados reais. Cada experimento precisa ser preregistrado com
hipótese, família, estratégia/versão, hashes do dataset e holdout, search space,
orçamento de trials, benchmark, regra de rejeição, configuração e versão das
métricas.

O registro produz um hash canônico e é idempotente para o mesmo conteúdo;
qualquer reutilização do mesmo `experimentId` com conteúdo diferente falha
fechado. Cada trial, inclusive rejeitado, é numerado e consome o orçamento.

O acesso ao holdout gera `HoldoutAccessAudit`. Acesso não autorizado contamina
o experimento irreversivelmente. Um substituto só pode ser preregistrado na
mesma família com nova identidade, timestamp posterior e hash de holdout
diferente. Assim, a contaminação não pode ser apagada ou mascarada como uma
execução limpa.

## Limite desta fatia

O registry atual é process-local e deliberadamente não lê rede, credenciais ou
dados de mercado. A próxima fatia deve adicionar o store PostgreSQL append-only
e um manifesto de evidência redigido, preservando os mesmos hashes e invariantes
do contrato. A governança não autoriza promoção: RiskPolicy, dataset real,
aprovação humana e decisão Go/No-Go continuam pendentes.
