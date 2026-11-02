import type * as Api from '../types/api';

type Check = (value: unknown) => boolean;
const text: Check = (value) => typeof value === 'string';
const number: Check = (value) => typeof value === 'number' && Number.isFinite(value);
const boolean: Check = (value) => typeof value === 'boolean';
const timestamp: Check = (value) =>
  typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T/.test(value) && Number.isFinite(Date.parse(value));
const nullable =
  (check: Check): Check =>
  (value) =>
    value === null || check(value);
const optional =
  (check: Check): Check =>
  (value) =>
    value === undefined || check(value);
const array =
  (check: Check): Check =>
  (value) =>
    Array.isArray(value) && value.every(check);
const enumeration =
  (...values: string[]): Check =>
  (value) =>
    typeof value === 'string' && values.includes(value);
const object =
  <T>(fields: { [K in keyof T]-?: Check }): Check =>
  (value) =>
    value !== null &&
    typeof value === 'object' &&
    !Array.isArray(value) &&
    Object.entries<Check>(fields).every(([key, check]) => check((value as Record<string, unknown>)[key]));

const health = object<Api.HealthStatusResponse>({ status: enumeration('Healthy', 'Degraded', 'Unhealthy') });
const operational = object<Api.OperationalStatusResponse>({
  source: text,
  mode: enumeration('Normal', 'SafeMode', 'KillSwitch'),
  blocksNewEntries: boolean,
  revision: number,
  changedAtUtc: timestamp,
  changedBy: nullable(text),
  reason: nullable(text),
  mutatingEndpointsEnabled: boolean,
});
const checkpoint = object<Api.CheckpointResponse>({ scope: text, lastProcessedCloseUtc: nullable(timestamp) });
const policy = object<Api.RiskPolicyDocument>({
  policy: object<Api.RiskPolicy>({
    version: text,
    maxOrderNotional: number,
    maxExposureNotional: number,
    maxDailyLoss: number,
    maxDrawdown: number,
    maxMarketDataAge: text,
    withdrawalsEnabled: boolean,
    maxSlippageBps: optional(nullable(number)),
    maxVolatilityBps: optional(nullable(number)),
    minLiquidityNotional: optional(nullable(number)),
  }),
  effectiveFromUtc: timestamp,
  expiresAtUtc: timestamp,
  createdBy: text,
  documentHash: text,
  lifecycle: enumeration('Draft', 'Approved', 'Active', 'Superseded', 'Revoked'),
  approvedBy: nullable(text),
  approvalId: nullable(text),
  approvedAtUtc: nullable(timestamp),
});
const decision = object<Api.AutonomyDecisionReconstructionReport>({
  commit: object<Api.AutonomyDecisionCommit>({
    decisionId: text,
    environment: text,
    accountScope: text,
    checkpointScope: text,
    symbol: object<{ value: string }>({ value: text }),
    strategyId: text,
    strategyVersion: text,
    closeTimeUtc: timestamp,
    availableAtUtc: timestamp,
    featureSchemaVersion: text,
    featureConfigurationFingerprint: text,
    featureFingerprint: text,
    signal: text,
    strategyReason: text,
    riskDecision: nullable(
      object<NonNullable<Api.AutonomyDecisionCommit['riskDecision']>>({
        status: text,
        requestedTargetNotional: number,
        authorizedTargetNotional: number,
        policyVersion: text,
        reasonCodes: array(text),
      }),
    ),
    outcome: text,
    noTradeReason: nullable(text),
    traceJson: text,
  }),
  canonicalIdentityHashMatches: boolean,
  expectedCanonicalIdentity: text,
  isAuditVerified: boolean,
  hasCompleteDispatchAudit: boolean,
  intentClientOrderId: nullable(text),
  expectedFee: nullable(
    object<NonNullable<Api.AutonomyDecisionReconstructionReport['expectedFee']>>({
      rate: number,
      snapshotHash: text,
      source: text,
      observedAtUtc: timestamp,
      validUntilUtc: timestamp,
      referencePrice: nullable(number),
      authorizedQuantity: nullable(number),
      decisionAtUtc: nullable(timestamp),
    }),
  ),
  submissionContext: nullable(
    object<NonNullable<Api.AutonomyDecisionReconstructionReport['submissionContext']>>({
      clientOrderId: text,
      applicationOrderId: object<{ value: string }>({ value: text }),
      correlationId: object<{ value: string }>({ value: text }),
      outboxMessageId: text,
      symbol: object<{ value: string }>({ value: text }),
      side: text,
      type: text,
      authorizedQuantity: object<{ value: number }>({ value: number }),
      referencePrice: object<{ value: number }>({ value: number }),
      authorizedNotional: number,
      executionAttempt: number,
      plannedAtUtc: timestamp,
      expiresAtUtc: nullable(timestamp),
    }),
  ),
  lifecycleEvents: nullable(
    array(
      object<NonNullable<Api.AutonomyDecisionReconstructionReport['lifecycleEvents']>[number]>({
        eventId: text,
        clientOrderId: text,
        transition: object<
          NonNullable<Api.AutonomyDecisionReconstructionReport['lifecycleEvents']>[number]['transition']
        >({
          from: text,
          to: text,
          idempotencyKey: object<{ value: string }>({ value: text }),
          occurredAtUtc: timestamp,
          exchangeOrderId: nullable(text),
        }),
      }),
    ),
  ),
});
const ticker = object<Api.MarketTickerResponse>({
  symbol: text,
  bidPrice: number,
  bidQuantity: number,
  askPrice: number,
  askQuantity: number,
  markPrice: number,
  spread: number,
  spreadBps: number,
  observedAtUtc: timestamp,
});
const balances = object<Api.PortfolioBalancesResponse>({
  environment: text,
  canTrade: boolean,
  totalNavUsd: number,
  cashUsdt: number,
  positionsUsd: number,
  observedAtUtc: timestamp,
  balances: array(
    object<Api.AssetBalance>({ asset: text, free: number, locked: number, total: number, valueUsd: number }),
  ),
});
const orders = array(
  object<Api.OpenOrderResponse>({
    symbol: text,
    orderId: number,
    clientOrderId: text,
    price: number,
    origQty: number,
    executedQty: number,
    status: text,
    type: text,
    side: enumeration('BUY', 'SELL'),
    timeUtc: timestamp,
  }),
);
const trades = array(
  object<Api.TradeHistoryResponse>({
    symbol: text,
    id: number,
    orderId: number,
    price: number,
    qty: number,
    quoteQty: number,
    commission: number,
    commissionAsset: text,
    timeUtc: timestamp,
    isBuyer: boolean,
    isMaker: boolean,
  }),
);
const performance = object<Api.PortfolioPerformanceResponse>({
  feesAreComplete: boolean,
  unvaluedFeeAmounts: array(object<{ asset: string; amount: number }>({ asset: text, amount: number })),
  scopeSymbol: text,
  quoteAsset: text,
  asOfUtc: timestamp,
  totalNavUsd: number,
  initialCapitalUsd: nullable(number),
  initialCapitalAvailabilityReason: text,
  cashBalanceUsd: nullable(number),
  positionsValueUsd: nullable(number),
  totalProfitUsd: number,
  totalProfitPct: nullable(number),
  totalProfitPctAvailabilityReason: text,
  totalRealizedPnlUsd: number,
  totalUnrealizedPnlUsd: number,
  totalFeesPaidUsd: number,
  totalTradesCount: number,
  winRatePercentage: number,
  profitFactor: nullable(number),
  profitFactorAvailabilityReason: nullable(text),
  periods: array(
    object<Api.PortfolioPerformanceResponse['periods'][number]>({
      periodName: text,
      profitUsd: nullable(number),
      profitPercentage: nullable(number),
      realizedProfitUsd: number,
      unrealizedProfitUsd: nullable(number),
      feesPaidUsd: number,
      tradesCount: number,
      winRatePercentage: number,
      snapshotAvailability: optional(text),
    }),
  ),
});
const telemetry = object<Api.TelemetryMetricsResponse>({
  apiRequests: number,
  apiErrors: number,
  workerHeartbeats: number,
  apiDurationMilliseconds: number,
  alertDeliveries: number,
  alertDeliveryFailures: number,
  alertDeduplicated: number,
  reconciliationRuns: number,
  reconciliationMismatches: number,
  outboxDispatches: number,
  outboxDispatchFailures: number,
  outboxDeadLetters: number,
  invalidMarketDataPayloads: number,
});
const regime = object<Api.MarketRegimeSnapshot>({
  symbol: text,
  regime: text,
  confidence: number,
  trendSlopeRate: number,
  volatilityRate: number,
  reason: text,
  observedAtUtc: timestamp,
  favorsTrendFollowing: optional(boolean),
  favorsMeanReversion: optional(boolean),
  prohibitsNewExposure: optional(boolean),
});
const models = array(
  object<Api.StrategyModelRecord>({
    modelId: text,
    strategyId: text,
    version: text,
    codeCommitSha: text,
    datasetHash: text,
    status: text,
    registeredAtUtc: timestamp,
    registeredBy: text,
    promotionJustification: nullable(text),
    canonicalHash: text,
  }),
);
const drifts = array(
  object<Api.DriftObservationRecord>({
    observationId: text,
    strategyId: text,
    type: text,
    expectedValue: number,
    observedValue: number,
    allowedDeviationThreshold: number,
    absoluteDeviation: number,
    relativeRate: number,
    severity: text,
    requiresQuarantine: boolean,
    message: text,
    observedAtUtc: timestamp,
  }),
);
const allocations = array(
  object<Api.StrategyRegimePerformanceRecord>({
    strategyId: text,
    regime: text,
    observationCount: number,
    cumulativeReward: number,
    averageReward: number,
  }),
);

const contracts: Record<string, Check> = {
  '/health/live': health,
  '/health/ready': health,
  '/operational/status': operational,
  '/operational/risk-policy': policy,
  '/portfolio/balances': balances,
  '/portfolio/performance': performance,
  '/trading/open-orders': orders,
  '/trading/trades': trades,
  '/metrics': telemetry,
  '/operational/regimes/current': regime,
  '/operational/regimes/history': array(regime),
  '/operational/models': models,
  '/operational/drift': drifts,
  '/operational/allocations': allocations,
};

export function validateResponse(endpoint: string, value: unknown): boolean {
  const path = endpoint.split('?')[0];
  const check = path.startsWith('/operational/decisions/')
    ? decision
    : path.startsWith('/operational/checkpoints/')
      ? checkpoint
      : path.startsWith('/market/ticker/')
        ? ticker
        : contracts[path];
  return check?.(value) ?? false;
}
