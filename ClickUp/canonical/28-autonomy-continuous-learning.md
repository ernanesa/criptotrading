# 28 - Autonomia, Autoaprendizado e Resiliência

## Objetivo

O CryptoTrading deve operar 24x7 sem intervenção humana em decisões rotineiras de mercado: perceber o estado do mercado, classificar contexto/regime, escolher entre estratégias previamente validadas, dimensionar capital, aplicar risco, decidir como executar, acompanhar resultados, degradar/desativar estratégias ruins, recuperar-se de falhas e aprender com novos dados.

`100% automatizado` significa operação normal autônoma, não ausência de controle. Kill switch, limites rígidos e capacidade de intervenção humana permanecem obrigatórios.

O sistema não pode interpretar `melhor estratégia` como uma verdade conhecida. Deve selecionar a alternativa com maior utilidade esperada líquida ajustada a risco e incerteza entre estratégias elegíveis e validadas para o contexto atual.

## Níveis de autonomia

- **A0 Manual:** apenas análise.
- **A1 Signal Automation:** sinais automáticos, execução manual.
- **A2 Trading Automation:** sinais + risk + execução automáticos.
- **A3 Regime-Adaptive:** seleção/desseleção automática de estratégias por estado de mercado.
- **A4 Adaptive Allocation:** ranking e orçamento de risco mudam continuamente segundo contexto e performance live.
- **A5 Bounded Continuous Learning:** modelos/parâmetros aprendem com novos dados, challengers são avaliados e promovidos automaticamente dentro de envelopes e gates predefinidos.

**Alvo arquitetural de longo prazo:** A5. O R1.0 entrega A2 para uma baseline; A3/A4 e depois A5 são releases posteriores. Nenhum nível superior ignora os gates de segurança dos níveis anteriores.

## Loop de decisão autônomo

```text
Market Data
   ↓
Data Quality Gate
   ↓
Feature / Market State Engine
   ↓
Regime Detector
   ↓
Candidate Strategy Eligibility
   ↓
Strategy Selector / Meta Allocator
   ↓
Portfolio Construction + Netting
   ↓
Independent Risk Engine
   ↓
Adaptive Execution
   ↓
OMS / Exchange
   ↓
Fills + Outcome Attribution
   ↓
Performance / Drift / Strategy Health
   └──────────── feedback ────────────→ Selector / Learning Plane
```

## Autonomy Orchestrator

Novo módulo responsável por coordenar decisões, sem possuir permissão para burlar Risk ou OMS.

Responsabilidades:
- construir contexto atual;
- obter estratégias candidatas compatíveis;
- excluir estratégias unhealthy, incompatíveis ou sem edge líquido mínimo;
- ranquear alternativas;
- distribuir risk budget;
- decidir `NO_TRADE` quando a utilidade esperada não compensar risco/custos;
- explicar e registrar a decisão;
- reagir a mudança de regime e performance;
- manter hysteresis/cooldown para evitar troca excessiva de estratégia.

O Orchestrator não envia ordens. Fluxo obrigatório permanece `Strategy/Selector -> Portfolio -> Risk -> Execution -> OMS`.

## Strategy Selector

### Fase inicial: determinística e explicável

Usar matriz regime x estratégia + score por:
- compatibilidade com regime;
- expectativa líquida recente e histórica;
- drawdown;
- Strategy Health;
- custos e slippage previstos;
- liquidez/spread;
- correlação com exposições atuais;
- incerteza da estimativa;
- capacidade disponível da estratégia.

Exemplo conceitual:

`Utility = ExpectedNetEdge - RiskPenalty - CostPenalty - UncertaintyPenalty - CorrelationPenalty`

Nenhum peso inicial é considerado universal; parâmetros são versionados, pesquisados e validados.

### Evolução: contextual bandit / Bayesian allocation

Cada estratégia validada pode ser tratada como um candidato/arm, com contexto de mercado. O allocator aprende qual estratégia tende a entregar melhor utilidade líquida em cada contexto.

Exploração deve ser restrita:
- challengers exploram livremente em backtest/paper/shadow;
- live usa apenas estratégias aprovadas;
- qualquer exploração com capital real usa orçamento canary específico e limitado;
- Risk Engine possui autoridade final em todos os casos.

Cada candidato declara exploration budget por estratégia, ativo, regime e janela. Antes de capital real, executar off-policy evaluation e shadow comparison pareado, registrar delayed feedback e medir o efeito das próprias ordens sobre features, fills e labels. O learner não aumenta seu próprio budget.

Contextual bandits são preferidos antes de reinforcement learning amplo por terem problema de decisão mais restrito, auditável e controlável.

## Market State e regime

Contexto deve combinar, quando disponível:
- tendência e força de tendência;
- volatilidade realizada/implícita quando aplicável;
- liquidez, spread e depth;
- order-flow/imbalance;
- correlação e beta sistêmico;
- volume e participação relativa;
- horário/sessão;
- qualidade/staleness dos dados;
- funding/basis/open interest após Futures;
- indicadores de saúde da exchange;
- performance/health das próprias estratégias.

Classificação deve retornar regime + probabilidades/confiança, evitando falso determinismo.

## Continuous Learning Plane

O hot path de trading não deve reescrever código nem treinar modelos pesados.

```text
Live telemetry / outcomes
        ↓
Training dataset versionado
        ↓
Drift detection
        ↓
Train / tune candidate
        ↓
Historical + OOS + Walk-forward + Stress
        ↓
Challenger
        ↓
Paper / Shadow
        ↓
Canary automático dentro de envelope
        ↓
Champion ou Rejected
```

### O que pode aprender automaticamente
- score/alocação entre estratégias validadas;
- parâmetros adaptativos dentro de intervalos aprovados;
- modelos de regime;
- modelos de volatilidade;
- previsão de slippage/market impact;
- Strategy Health thresholds quando metodologia for validada;
- filtros de oportunidade;
- execution tactic selection.

### O que não deve se autoalterar livremente
- código-fonte live;
- limites máximos de risco;
- permissão de saque;
- regras que removem circuit breakers;
- novos tipos de ordem/derivativo não aprovados;
- mudança de credenciais/segurança.

## Concept drift e strategy decay

Separar:
- **Data drift:** distribuição dos inputs mudou.
- **Concept drift:** relação entre contexto e resultado mudou.
- **Execution drift:** slippage/fill/latência pioraram.
- **Strategy decay:** edge líquido observado deteriorou.

Monitorar por janelas múltiplas. Uma mudança relevante pode:
1. reduzir risk budget;
2. colocar estratégia em `DEGRADED`;
3. mover para `SUSPICIOUS`/quarantine;
4. desabilitar novas entradas;
5. disparar retreinamento/retuning;
6. lançar challenger em shadow.

## Champion-Challenger

Para cada modelo adaptativo:
- `Champion`: versão ativa aprovada.
- `Challenger`: nova versão avaliada paralelamente.

Promoção automática só ocorre se critérios mínimos e margem de melhoria forem cumpridos e não houver regressão material de risco/execução.

Rollback automático para o champion anterior quando:
- performance live degrada além do envelope;
- drift não explicado;
- divergência shadow/live excessiva;
- erro técnico ou de dados;
- Risk Engine aciona proteção.

O champion anterior não é automaticamente seguro em um novo regime. Quando sua elegibilidade ou integridade não puder ser confirmada, o rollback correto é `NO_TRADE`/SafeMode até nova avaliação.

Model Registry deve persistir versão, dataset, features, parâmetros, métricas, seed, commit, motivo de promoção/rejeição e lineage.

O pipeline monitora training-serving skew, versão de transformação/feature e compatibilidade do schema. Modelo, dataset, execution policy e RiskPolicy não mudam simultaneamente sem experimento identificável; caso contrário, attribution e rollback ficam inconclusivos.

## Estratégia de autoaprimoramento segura

### Loop rápido: segundos/minutos
- atualização de contexto/regime;
- Strategy Health operacional;
- ranking e alocação;
- execution tactic;
- no-trade/derisk.

### Loop médio: horas/dias
- atualizar estatísticas rolling/Bayesian;
- recalibrar estimadores dentro de bounds;
- drift analysis;
- gerar challengers parametrizados.

### Loop lento: dias/semanas
- retraining/reoptimization;
- walk-forward;
- Monte Carlo/stress;
- PBO/Deflated Sharpe e sensibilidade;
- paper/shadow/canary promotion.

Separar loops reduz risco de um ruído de minutos reconfigurar completamente o sistema.

## Resiliência e self-healing

### Aplicação
- supervised process/restart policy;
- health/readiness/liveness;
- reconnect com exponential backoff + jitter;
- circuit breaker de exchange;
- backpressure e bounded queues;
- timeouts explícitos;
- isolamento do Research Runner;
- degradação controlada de módulos não críticos.

### Estado
- journal durável de decisões/ordens/fills;
- idempotência;
- snapshots;
- reconciliação no startup/reconnect/periodicamente;
- troca para `SAFE_MODE` quando estado financeiro não for confiável.

### Falhas e fallback

```text
Normal
 ↓ falha de strategy/model
Fallback Strategy/NO_TRADE
 ↓ falha de market data
NO_NEW_ENTRIES
 ↓ estado da conta incerto
SAFE_MODE + reconcile
 ↓ risco crítico
KILL SWITCH / reduce-only conforme política
```

### Infraestrutura
Single-node pode ser self-healing em nível de processo, mas não oferece alta disponibilidade contra falha total da VM/região. Evolução posterior:
- primary/standby;
- durable external backup;
- leader election/fencing antes de permitir duas instâncias com permissão de ordem;
- failover testado;
- nunca executar dois leaders inadvertidamente.

## Adaptive Execution

O sistema deve escolher técnica de execução automaticamente segundo:
- urgência;
- spread;
- depth;
- volatilidade;
- order size relativo à liquidez;
- probabilidade de fill;
- adverse selection;
- edge decay;
- fees maker/taker.

Começar com `market vs passive limit`; evoluir somente após dados para cancel-replace, TWAP/iceberg e modelos de execução aprendidos.

## Autonomous NO_TRADE

`NO_TRADE` é uma estratégia de preservação de capital. Deve ser selecionada automaticamente quando:
- nenhum candidato possui edge líquido suficiente;
- incerteza é excessiva;
- regime é desconhecido/transitório;
- spread/slippage/volatilidade excedem limites;
- dados estão stale;
- exchange está degradada;
- risco/correlação do portfólio já está alto;
- Strategy Health global está ruim.

## LLM/IA generativa

LLM não deve estar no hot path de decisão de capital. Pode auxiliar pesquisa, documentação, explicação e geração de hipóteses/challengers em ambiente de research, mas qualquer artefato deve passar pelo mesmo pipeline quantitativo antes de ser elegível para live.

## Critérios de aceite de autonomia

- operação paper/shadow pelo período e volume definidos na `PromotionReadinessMatrix`, sem necessidade de intervenção rotineira;
- mudança de regime altera elegibilidade/alocação automaticamente e de forma auditável;
- estratégia degradada perde alocação/risk budget ou é desabilitada automaticamente;
- `NO_TRADE` é escolhido quando nenhuma oportunidade passa o threshold;
- reinício do processo reconcilia e retoma em estado seguro;
- challenger nunca recebe capital live fora do envelope de promoção;
- rollback automático funciona em teste de falha;
- toda decisão contém contexto, candidatos avaliados, score, risk decision e execution result;
- nenhum mecanismo adaptativo consegue elevar hard risk limits;
- processo completo é reproduzível a partir de versões de dados/modelo/config/código.

## Definição prática de sucesso

O sistema não precisa prever o futuro perfeitamente. Deve conseguir, autonomamente, escolher entre alternativas validadas, atualizar crenças com novos resultados, abandonar comportamentos degradados, preservar capital quando a incerteza aumenta e melhorar decisões mensuráveis sem violar envelopes de risco.
