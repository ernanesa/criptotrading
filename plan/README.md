# Plano mestre do CryptoTrading

**Estado:** planejamento inicial  
**Data-base:** 2026-09-01  
**Produto:** plataforma autônoma de trading algorítmico de criptomoedas  
**Exchange inicial:** Binance Spot  
**Backend-alvo:** .NET 11 + C# 15  
**Frontend-alvo:** React + TypeScript + Vite 8+

Este diretório é a fonte de verdade do planejamento. Mudanças relevantes de produto, risco ou arquitetura devem atualizar estes documentos e, quando forem decisões arquiteturais, o registro de ADRs.

## Product Goal

Construir uma plataforma de trading algorítmico segura, auditável, modular, resiliente e orientada a evidências que opere 24x7 de forma autônoma: analise o mercado, classifique contexto/regime, escolha e combine automaticamente estratégias previamente validadas, aloque risco, execute, monitore resultados, degrade/desative estratégias deterioradas, recupere-se de falhas e evolua com novos dados dentro de boundaries de segurança.

## North Star

**Preservação de capital + expectativa líquida positiva + autonomia segura + confiabilidade operacional.**

Lucro bruto, win rate ou quantidade de operações não são objetivos isolados. `NO_TRADE` é uma decisão válida.

## Índice

- [00 - Project Charter](00-project-charter.md)
- [01 - Product Requirements](01-product-requirements.md)
- [02 - Requisitos Funcionais](02-functional-requirements.md)
- [03 - Requisitos Não Funcionais](03-non-functional-requirements.md)
- [04 - Arquitetura](04-architecture.md)
- [05 - Stack e ferramentas](05-tech-stack.md)
- [06 - Domínio e eventos](06-domain-spec.md)
- [07 - Exchange e market data](07-exchange-market-data.md)
- [08 - Catálogo de estratégias](08-strategy-catalog.md)
- [09 - Gestão de risco](09-risk-management.md)
- [10 - Pesquisa, backtest e validação](10-research-validation.md)
- [11 - Segurança](11-security.md)
- [12 - Dados e retenção](12-data.md)
- [13 - Observabilidade e operações](13-observability-operations.md)
- [14 - Testes e qualidade](14-testing-quality.md)
- [15 - Modelo Scrum](15-scrum.md)
- [16 - Definition of Ready e Definition of Done](16-dor-dod.md)
- [17 - Product Backlog](17-backlog.md)
- [18 - Critérios de aceite](18-acceptance-criteria.md)
- [19 - Roadmap e releases](19-roadmap-releases.md)
- [20 - Plano de Sprints](20-sprint-plan.md)
- [21 - Registro de riscos](21-risk-register.md)
- [22 - Infraestrutura e deployment](22-infrastructure-deployment.md)
- [23 - Engenharia de performance](23-performance.md)
- [24 - ADRs](24-adrs.md)
- [25 - Glossário](25-glossary.md)
- [26 - Referências](26-references.md)
- [27 - Runbooks](27-runbooks.md)
- [28 - Autonomia, autoaprendizado e resiliência](28-autonomy-continuous-learning.md)

## Templates

- [User Story](templates/user-story.md)
- [ADR](templates/adr.md)
- [Strategy Spec](templates/strategy-spec.md)
- [Experiment](templates/experiment.md)
- [Incident](templates/incident.md)

## Regras de governança

- O Product Backlog é ordenado por valor, risco e dependências, não por entusiasmo técnico.
- Toda funcionalidade que pode movimentar capital exige critérios de segurança e teste mais fortes.
- Mudanças em limites de risco exigem versionamento, auditoria e aprovação explícita.
- Estratégias só chegam a live após gates objetivos de pesquisa e operação.
- O sistema pode adaptar seleção, pesos e parâmetros dentro de envelopes aprovados, mas nunca remover hard risk limits.
- Releases devem ser reversíveis.
- Segurança, autonomia, resiliência e observabilidade fazem parte da funcionalidade, não são tarefas finais.

## Escopo de lançamento 1.0

1. Binance Spot.
2. Market data realtime e histórico.
3. OMS e reconciliação.
4. Risk Engine independente.
5. Backtest reproduzível com custos.
6. Paper e shadow trading.
7. Estratégias baseline: Trend Following, Momentum, Breakout, Mean Reversion e Adaptive Grid.
8. Regime Detection com confiança.
9. Autonomy Orchestrator com seleção e alocação automática entre estratégias validadas.
10. Strategy Health, `NO_TRADE` e redução/desativação automáticas.
11. Adaptive execution inicial (market vs passive limit conforme contexto).
12. Dashboard React realtime com decision trace.
13. SafeMode, self-healing e recuperação/reconciliação automáticos.
14. Live canary com capital mínimo.
15. Operação Spot sem intervenção humana rotineira.
16. Observabilidade, backups e runbooks.

Continuous learning avançado, contextual bandits live, Futures, funding/basis, market making avançado, arbitragem cross-exchange e FIX/SBE avançado entram após a baseline autônoma estar comprovada. A arquitetura, porém, já nasce preparada para esses recursos.
