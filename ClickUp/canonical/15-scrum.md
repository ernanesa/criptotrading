# 15 - Modelo Scrum

## Scrum Team

O Scrum Guide 2020 define três accountabilities: Product Owner, Scrum Master e Developers. Neste projeto pessoal, uma mesma pessoa pode acumular accountabilities; durante planejamento e revisão, elas devem ser tratadas separadamente para reduzir pontos cegos.

### Product Owner
- maximiza valor;
- define Product Goal;
- ordena Product Backlog;
- aceita/rejeita resultado por valor e critérios.

### Scrum Master
- protege empirismo e foco;
- remove impedimentos;
- evita scope creep;
- facilita retrospectivas;
- questiona complexidade sem valor.

### Developers
- dimensionam trabalho;
- criam Sprint Backlog;
- entregam Increment Done;
- preservam qualidade/DoD.

## Sprint cadence

**2 semanas** por padrão, adequado a sessões diárias curtas. Se a capacidade real mostrar que 1 semana produz incrementos pequenos porém úteis, a cadência pode mudar via retrospectiva.

## Eventos

### Sprint Planning
Definir Sprint Goal, selecionar PBIs e decompor o suficiente para iniciar.

### Daily Scrum pessoal
5 minutos: o que mudou, próximo passo concreto, impedimento/risco. Não é relatório de status.

### Refinement
Contínuo; reservar aproximadamente 5–10% da capacidade para deixar 1–2 Sprints prontos.

### Sprint Review
Demonstrar incremento funcionando e atualizar backlog com aprendizado técnico/mercado.

### Retrospective
Escolher no máximo 1–2 melhorias de processo por Sprint.

## Artefatos e compromissos

- Product Backlog ↔ Product Goal.
- Sprint Backlog ↔ Sprint Goal.
- Increment ↔ Definition of Done.

## Definition of Ready

DoR não é parte oficial do Scrum Guide; será usada apenas como política auxiliar para não puxar itens vagos. Não pode virar barreira burocrática.

## Estimativa

Preferir tamanhos relativos (XS/S/M/L/XL) ou story points apenas para capacidade, nunca para medir produtividade. Tasks podem usar timebox se ajudarem a planejar sessões diárias.

## Política de WIP

- máximo 1 história principal em desenvolvimento;
- até 1 item pequeno de documentação/bug em paralelo;
- terminar antes de iniciar nova frente sempre que possível.

## Product Backlog ordering

Prioridade = valor + redução de risco + aprendizado + dependências + custo de atraso.

IDs e dependências vêm do `traceability.yaml`; intervalos numéricos implícitos não substituem seleção explícita de PBIs. Itens de opção exploratória não competem com o Sprint Goal do R1.0.

## Sprint Goal over task completion

Se capacidade mudar, retirar escopo secundário mantendo o Sprint Goal. Não sacrificar DoD para “fechar pontos”.
