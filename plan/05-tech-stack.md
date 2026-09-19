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

O projeto nasce em `net11.0` e C# 15; essa decisão é obrigatória e não possui fallback para .NET 10. Na data-base desta documentação, .NET 11 está em RC1 go-live e C# 15 ainda é preview. Antes do GA esperado para novembro/2026, usar o SDK RC oficial mais recente pinado por `global.json`, com `TargetFramework=net11.0` e versão de linguagem explícita compatível; atualizar em PR próprio. Nenhuma RC pode permanecer em uso após sua janela oficial de suporte. Produção com capital real exige GA + patches estáveis.

No bootstrap, registrar versão exata e compatibilidade de ASP.NET Core, EF Core, Npgsql, OpenTelemetry, test tooling, containers e arquiteturas suportadas. Se uma dependência crítica ainda não for compatível, escolher alternativa compatível, isolar o adapter ou adiar a feature; não rebaixar o runtime. Recursos preview de linguagem entram apenas quando agregarem valor mensurável e tiverem testes, analyzers e plano de atualização.

### Baseline moderna de engenharia .NET/C#

- `global.json` fixa feature band/roll-forward deliberado; `Directory.Build.props` e `Directory.Packages.props` centralizam linguagem, analyzers, warnings e versões.
- `Nullable=enable`, implicit/global usings avaliados por projeto, analyzers do SDK habilitados e warnings relevantes tratados como erro no CI.
- restore determinístico com lockfiles/locked mode em CI; dependências e SDK atualizados por PR isolado, nunca por range flutuante.
- built-in DI/configuration/options com validação no startup; source generators onde reduzirem reflection/cold start sem sacrificar clareza.
- `TimeProvider`, cancellation tokens, async streams, Channels bounded e primitives modernas do BCL em vez de abstrações caseiras.
- OpenAPI nativo do ASP.NET Core e contrato versionado; cliente TypeScript gerado/verificado para evitar drift.
- performance orientada por medição: pooling, spans, pipelines, NativeAOT ou recursos C# 15 só entram quando benchmark, compatibilidade e manutenção justificarem.
- APIs preview e `unsafe` são opt-in explícito por projeto/ADR; “moderno” significa suportado, observável e testável, não usar toda novidade indiscriminadamente.

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
- filesystem/volumes locais via abstraction; S3/OCI Object Storage somente em migração futura aprovada.
- Redis opcional, não obrigatório no MVP.
- TimescaleDB/ClickHouse apenas se medições mostrarem necessidade.

## Infraestrutura

- Máquina local como único target inicial.
- Docker Engine + Docker Compose como contrato de runtime reproduzível.
- PostgreSQL, OTel Collector e observabilidade executados localmente em containers/profiles separados.
- Volumes locais versionados por schema, backup local e cópia off-host antes de canary.
- Multi-arch BuildKit: `linux/amd64,linux/arm64`.
- GitHub Actions para validação; execução do produto não depende do CI hospedado.
- OpenTofu/Terraform, AWS, Oracle Cloud e VPS ficam fora do escopo ativo. Só entram após decisão futura explícita e nunca como dependência do domínio.

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
