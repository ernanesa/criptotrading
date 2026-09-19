# Governança local de experimentos

`InMemoryExperimentRegistry` implementa a primeira camada de `US-054` antes de
qualquer OOS com dados reais. Cada experimento precisa ser preregistrado com
hipótese, família, estratégia/versão, hashes do dataset e holdout, search space,
orçamento de trials, benchmark, regra de rejeição, configuração e versão das
métricas.

O registro produz um hash canônico (`PreregistrationSha256`) e é idempotente para o mesmo conteúdo;
qualquer reutilização do mesmo `experimentId` com conteúdo diferente falha
fechado. Cada trial, inclusive rejeitado, é numerado e consome o orçamento.

O acesso ao holdout gera `HoldoutAccessAudit` com `IdempotencyKey`. Acesso não autorizado contamina
o experimento irreversivelmente. Um substituto só pode ser preregistrado na
mesma família com nova identidade, timestamp posterior e hash de holdout
diferente. Assim, a contaminação não pode ser apagada ou mascarada como uma
execução limpa.

## Limite desta fatia

O registry atual em `CryptoTrading.Domain.Research` é process-local e deliberadamente não lê rede, credenciais ou
dados de mercado. A migration 011 e `IExperimentGovernanceStore` já fornecem o
store PostgreSQL append-only para preregistration, trials e auditorias; o
manifesto de evidência redigido continua como próxima fatia; a leitura do
snapshot já recompõe preregistration, trials, contagem de acessos e estado de
contaminação após restart. A governança não autoriza promoção: RiskPolicy, dataset real,
aprovação humana e decisão Go/No-Go continuam pendentes.
