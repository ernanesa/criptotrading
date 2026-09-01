# 05 - Stack e ferramentas

## Backend

| Área | Escolha |
|---|---|
| Runtime | .NET 11 |
| Linguagem | C# 15 |
| Web/API | ASP.NET Core 11 |
| ORM/control plane | EF Core 11 |
| Driver PostgreSQL | Npgsql |
| Hot-path DB/bulk | Npgsql direto/Binary COPY quando medido |
| Concorrência | Channels, async/await, IAsyncEnumerable |
| Serialização | System.Text.Json source generation; SBE futuramente |
| Realtime UI | ASP.NET Core SignalR |
| Resiliência HTTP | Microsoft.Extensions.Http.Resilience/Polly |
| Observabilidade | OpenTelemetry |
| Benchmarks | BenchmarkDotNet |
| Testes | xUnit v3 + Microsoft Testing Platform |

### Política .NET 11 pré-GA

O alvo definitivo é `net11.0` e C# 15. Antes do GA de novembro/2026, usar o SDK preview/RC mais recente pinado por `global.json`; atualizar em PR próprio. Produção com capital real exige GA + patches estáveis.

## Frontend

| Área | Escolha |
|---|---|
| UI | React 19.2+ |
| Linguagem | TypeScript stable |
| Build | Vite 8.1+ / Rolldown |
| Package manager | pnpm |
| Routing | TanStack Router v1 |
| Server state | TanStack Query v5 |
| Tables | TanStack Table |
| Validation | Zod |
| Forms | React Hook Form quando necessário |
| CSS | Tailwind CSS 4 + componentes acessíveis/shadcn conforme necessidade |
| Charts de mercado | TradingView Lightweight Charts |
| Testes unit/component | Vitest + Testing Library |
| E2E | Playwright |

Global state adicional (ex. Zustand) só entra com caso concreto; Redux não é dependência padrão.

## Node

Usar Node 24 LTS enquanto for LTS e compatível. Atualizações de runtime devem ser feitas por PR automatizado + CI.

## Dados

- PostgreSQL 18.x como baseline atual, atualizando patches.
- Parquet para histórico grande.
- DuckDB para analytics/research local sobre Parquet.
- S3/OCI Object Storage/arquivo local via abstraction.
- Redis opcional, não obrigatório no MVP.
- TimescaleDB/ClickHouse apenas se medições mostrarem necessidade.

## Infraestrutura

- Docker + Docker Compose.
- Multi-arch BuildKit: `linux/amd64,linux/arm64`.
- GitHub Actions.
- OpenTofu/Terraform para IaC quando o laboratório AWS começar.
- AWS: EC2, IAM, VPC/Security Groups, S3, CloudWatch, SSM Parameter Store, Budgets; RDS opcional conforme crédito/custo.
- Oracle/VPS: Docker Compose + PostgreSQL + volumes + reverse proxy.

## Qualidade e supply chain

- EditorConfig.
- dotnet format/analyzers.
- nullable enabled.
- TreatWarningsAsErrors nas configurações apropriadas.
- Dependabot ou Renovate.
- CodeQL.
- Trivy para imagens/IaC.
- secret scanning.
- SBOM em release.

## Ferramentas de referência/benchmark competitivo

Estudar arquitetura e comportamento, sem copiar código sem licença compatível:
- QuantConnect LEAN;
- Hummingbot;
- Freqtrade;
- Jesse;
- 3Commas;
- Bitsgap;
- Cryptohopper;
- TradingView;
- CCXT como referência de abstração multi-exchange.
