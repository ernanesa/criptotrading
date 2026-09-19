# 22 - Infraestrutura e Deployment

## Decisão vigente

O ambiente inicial e o único target do roadmap ativo é esta máquina local. Worker, API, Web, PostgreSQL, armazenamento analítico e observabilidade executam por Docker Compose. Nenhum recurso AWS, Oracle Cloud ou VPS deve ser provisionado, e nenhuma credencial cloud é necessária para desenvolver, testar, executar paper trading ou integrar com Binance Spot Testnet.

Portabilidade continua sendo requisito arquitetural, mas não autoriza deployment externo. Qualquer mudança desse estado exige decisão explícita do usuário, ADR, análise de custo e segurança, benchmark, plano de dados e rollback.

## Topologia local inicial

```text
Browser local
    │
    └── Web/API/SignalR ─────────────┐
                                     │
Trading Worker ─── Binance/Testnet   │
      │                              │
PostgreSQL + volumes locais          │
      │                              │
Parquet/DuckDB                       │
                                     │
OTel Collector → stack local ────────┘
```

O Trading Worker não depende do frontend para continuar operando. PostgreSQL não é publicado na rede externa. Exposição da UI fica limitada ao host/rede local por padrão.

## Compose e profiles

Serviços previstos:

- `cryptotrading-worker`;
- `cryptotrading-api`;
- `cryptotrading-web`;
- `postgres`;
- `otel-collector` e componentes locais mínimos de logs/metrics/traces;
- `research-runner` em profile separado e desligável.

Profiles devem separar ao menos `dev`, `paper`, `testnet`, `research` e `observability`. CANARY/LIVE nunca é inferido pelo nome do Compose profile: exige configuração, credencial e gate próprios.

O profile de research/backtest recebe limites de CPU, memória e I/O e pode ser pausado. Saturação local não pode causar perda de evento financeiro nem degradação silenciosa do hot path.

## Bootstrap local reproduzível

O foundation deve fornecer procedimento idempotente que:

1. valida Docker Engine/Compose, arquitetura, espaço em disco, portas e versão do SDK .NET 11 pinada;
2. cria somente diretórios, redes e volumes locais previstos;
3. sobe dependências e aplica migrations de forma controlada;
4. executa health checks e smoke tests;
5. comprova que DEV/PAPER inicia sem credencial cloud;
6. registra versões, configuração não sensível e resultado como evidence.

Não instalar ou alterar dependências globais da máquina silenciosamente. Pré-requisitos, versões mínimas e impacto devem estar documentados.

## Persistência, backup e restore

- volumes nomeados e paths de dados explicitamente documentados;
- PostgreSQL com backup automatizado local e cópia off-host removível/criptografada antes de canary;
- Parquet e research artifacts com checksums, retenção e cópia conforme valor;
- configuração em Git, sem secrets;
- restore drill periódico para diretório temporário isolado;
- reconciliação obrigatória com a exchange após recuperação.

Backup no mesmo disco não é proteção contra falha do host. A solução off-host não precisa ser cloud: disco externo ou outro destino local controlado é suficiente inicialmente.

## Recursos e capacidade da máquina

Antes de fixar sizing, coletar CPU/arquitetura, RAM, disco livre, filesystem, IOPS aproximado e estabilidade de rede da máquina atual. Os budgets iniciais devem ser derivados desses dados e validados por benchmark/load test.

Definir limites e alertas para:

- CPU/RAM do Worker e PostgreSQL;
- espaço, crescimento e latência de disco;
- retenção da observabilidade;
- concorrência de backtests;
- lag de ingestão e filas;
- temperatura/throttling quando relevante;
- RTT, jitter, packet loss e estabilidade WebSocket até a Binance.

## Rede e secrets locais

- bind de serviços administrativos em loopback/rede privada por padrão;
- PostgreSQL sem porta pública;
- firewall local mínimo;
- secrets em arquivo ignorado pelo Git com permissões restritas ou Docker secrets quando aplicável;
- chaves separadas por ambiente, sem saque e sem reutilização entre Testnet e live;
- TLS/reverse proxy só quando houver acesso fora do host, após threat model.

## Containers e arquiteturas

As imagens de release devem ser construíveis para `linux/amd64` e `linux/arm64`. O ambiente local nativo é o gate imediato; a arquitetura alternativa pode ser validada por CI runner ou emulação. Suporte multi-arch preserva opções futuras, mas não cria obrigação de Oracle/AWS.

## CI/CD local-first

1. PR gates e análise estática.
2. Build/test/security.
3. Build de imagem imutável por commit SHA.
4. Deploy DEV/PAPER local.
5. Migrations, health checks e smoke test.
6. Promoção explícita para TESTNET/SHADOW/CANARY/LIVE conforme os gates do produto.

GitHub Actions pode validar o repositório, mas o produto deve poder ser construído, testado e executado localmente pelos mesmos comandos. Nenhuma credencial live é necessária em CI. Não usar `latest` como referência de release.

Deploy/rollback declara tratamento de ordens em voo, draining, fencing/instance identity, readiness financeira e reconciliação antes de reabilitar entradas. Rollback de imagem não implica rollback destrutivo de banco ou ledger.

## Opções futuras fora do escopo ativo

AWS, Oracle Cloud e VPS são apenas alternativas de hosting. Não há laboratório AWS planejado nem sequência obrigatória entre provedores.

Se uma opção for futuramente ativada, a iniciativa deverá, antes de provisionar qualquer recurso:

- registrar motivação e requisito que a máquina local deixou de atender;
- medir latência/capacidade e comparar alternativas;
- revalidar preços, créditos, região e elegibilidade;
- aprovar budget, alertas, owner, TTL e teardown;
- evitar NAT Gateway, Load Balancer, RDS, IPv4 público dedicado ou outro custo recorrente sem aprovação específica;
- definir IAM/privilégio mínimo, secrets, rede, backup, migração e rollback;
- testar restore, reconciliação e imagens na arquitetura escolhida.

OpenTofu/Terraform só entra junto com uma iniciativa externa aprovada. Docker Compose permanece o contrato portátil do runtime.
