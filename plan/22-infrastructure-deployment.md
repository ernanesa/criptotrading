# 22 - Infraestrutura e Deployment

## Estratégia de provedores

### Fase 1 - AWS para aprendizado
Objetivo: aprender serviços, IaC, IAM, rede, observabilidade e deployment. O novo Free Tier oferece créditos e plano gratuito temporário para contas elegíveis; custos devem ser monitorados por Budgets.

Serviços prioritários:
- EC2, preferencialmente Graviton/ARM quando compatível;
- VPC/Security Groups;
- IAM;
- S3;
- CloudWatch;
- SSM Parameter Store;
- AWS Budgets;
- RDS opcional, apenas se custo/crédito justificar.

### Fase 2 - Oracle Cloud existente
Target econômico: 4 ARM vCPU + 24 GB RAM. Excelente para Worker + API + Web + PostgreSQL + observabilidade moderada. Exige imagens ARM64 e backup externo.

### Fase 3 - Hostinger/VPS
KVM 4 (4 vCPU/16 GB/200 GB NVMe do plano analisado) é baseline adequado. KVM 2 serve para início leve; KVM 8 só com necessidade medida.

## Topologia single-node inicial

```text
Internet
  ↓
Reverse Proxy/TLS
  ├── Web static
  └── ASP.NET API/SignalR

Trading Worker ─── Binance
      │
PostgreSQL
      │
Parquet/volumes

OTel Collector → CloudWatch ou Prometheus/Grafana
```

Trading Worker não depende do frontend para continuar operando.

## Containers

- `cryptotrading-worker`
- `cryptotrading-api`
- `cryptotrading-web`
- `postgres`
- telemetry components conforme ambiente.

Research runner pode ser profile separado e desligado em live para evitar competição de recursos.

## Hardware de produção inicial

Recomendado: 4–8 vCPU, 16 GB RAM, 100–250+ GB NVMe. A Oracle 4 vCPU/24 GB já atende confortavelmente ao escopo inicial. Armazenamento histórico grande deve migrar para object storage/volume dedicado.

## CI/CD

1. PR gates.
2. build/test/security.
3. build multi-arch immutable image.
4. tag por semver + commit SHA.
5. deploy DEV/PAPER.
6. smoke test.
7. promoção explícita para SHADOW/CANARY/LIVE.

Não usar `latest` como referência de produção.

## IaC

OpenTofu/Terraform para AWS e, quando útil, Oracle. Docker Compose é contrato de runtime portátil.

## Backup/DR

- backup PostgreSQL automatizado;
- cópia off-host;
- restore drill;
- config/infra em Git;
- secrets fora do Git;
- runbook de rebuild da VM do zero.

## Latência

Antes de escolher provedor live definitivo, medir RTT, jitter, packet loss e estabilidade WebSocket para endpoints Binance a partir de AWS, Oracle e Hostinger. Marca do provedor não substitui benchmark.
