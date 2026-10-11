# 26 - Referências pesquisadas

Data de revisão: 2026-09-19.

## Processo
- Scrum Guide 2020: https://scrumguides.org/

## .NET / C#
- .NET 11 overview: https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/overview
- C# 15: https://learn.microsoft.com/dotnet/csharp/whats-new/csharp-15
- .NET support policy e releases go-live: https://dotnet.microsoft.com/platform/support/policy/dotnet-core

Baseline verificada em 2026-09-18: .NET 11 RC1 é go-live, com GA esperada para novembro/2026; C# 15 continua preview. A implementação usa versões exatas pinadas e revalida essas páginas em cada upgrade.

## Frontend
- React versions: https://react.dev/versions
- Vite 8: https://vite.dev/blog/announcing-vite8
- Vite 8.1: https://vite.dev/blog/announcing-vite8-1
- TanStack Query: https://tanstack.com/query/latest/docs/framework/react
- TanStack Router: https://tanstack.com/router/latest/docs/overview
- Node releases: https://nodejs.org/en/about/previous-releases

## Binance
- Spot REST API: https://developers.binance.com/en/docs/products/spot/rest-api
- Spot Query Commission Rates (`GET /api/v3/account/commission`): https://github.com/binance/binance-spot-api-docs/blob/master/rest-api.md#query-commission-rates-user_data
- Spot WebSocket API: https://developers.binance.com/en/docs/products/spot/web-socket-api
- Spot WebSocket Streams: https://developers.binance.com/en/docs/products/spot/web-socket-streams
- Spot User Data Stream: https://developers.binance.com/en/docs/products/spot/user-data-stream
- Spot Filters: https://developers.binance.com/en/docs/products/spot/filters
- Spot Changelog: https://developers.binance.com/en/docs/products/spot/CHANGELOG
- Spot Testnet: https://developers.binance.com/en/docs/products/spot/testnet/general-info
- Spot Demo Mode: https://developers.binance.com/en/docs/products/spot/demo-mode/general-info
- Usar sempre documentação oficial atual antes de implementar autenticação, user data, SBE, FIX, filters e rate limits; registrar data, endpoint e versão/schema consultados.

## Trading frameworks
- QuantConnect Algorithm Framework: https://www.quantconnect.com/docs/v2/writing-algorithms/algorithm-framework/overview
- Hummingbot V2 Controllers: https://hummingbot.org/strategies/v2-strategies/controllers/
- Freqtrade Protections: https://docs.freqtrade.io/en/stable/plugins/
- Freqtrade Backtesting: https://docs.freqtrade.io/en/stable/backtesting/

## Quant research
- Bailey et al., Probability of Backtest Overfitting: https://papers.ssrn.com/sol3/papers.cfm?abstract_id=2326253
- Bailey & López de Prado, Deflated Sharpe Ratio: https://papers.ssrn.com/sol3/papers.cfm?abstract_id=2460551
- Bailey et al., effects of backtest overfitting: https://papers.ssrn.com/sol3/papers.cfm?abstract_id=2308659
- Novy-Marx, Backtesting Strategies Based on Multiple Signals: https://www.nber.org/papers/w21329

## Observability / Security / Data
- OpenTelemetry .NET: https://opentelemetry.io/docs/languages/dotnet/
- OpenTelemetry Semantic Conventions: https://opentelemetry.io/docs/concepts/semantic-conventions/
- OWASP ASVS 5.0: https://owasp.org/projects/asvs
- SLSA specification: https://slsa.dev/spec/
- NIST AI Risk Management Framework: https://www.nist.gov/itl/ai-risk-management-framework
- PostgreSQL releases: https://www.postgresql.org/docs/release/

## Cloud futura, fora do escopo ativo
- AWS Free Tier: https://docs.aws.amazon.com/awsaccountbilling/latest/aboutv2/free-tier.html
- AWS Free Tier plans: https://docs.aws.amazon.com/awsaccountbilling/latest/aboutv2/free-tier-plans.html
- AWS VPC/Public IPv4 pricing: https://aws.amazon.com/vpc/pricing/
- Systems Manager Parameter Store: https://docs.aws.amazon.com/systems-manager/latest/userguide/systems-manager-parameter-store.html

## Política de atualização

Antes de implementar integração externa, confirmar documentação oficial novamente. Binance, .NET previews e bibliotecas frontend mudam rapidamente; cloud pricing só deve ser pesquisado se uma iniciativa futura de cloud for explicitamente aprovada. Este arquivo registra baseline, não congela versões eternamente.
