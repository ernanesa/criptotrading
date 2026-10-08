export type OperationalMode = 'Normal' | 'SafeMode' | 'KillSwitch';

export interface OperationalStatusResponse {
  source: string;
  mode: OperationalMode;
  blocksNewEntries: boolean;
  revision: number;
  changedAtUtc: string;
  changedBy: string | null;
  reason: string | null;
  mutatingEndpointsEnabled: boolean;
}

export interface CheckpointResponse {
  scope: string;
  lastProcessedCloseUtc: string | null;
}

export interface RiskPolicy {
  version: string;
  maxOrderNotional: number;
  maxExposureNotional: number;
  maxDailyLoss: number;
  maxDrawdown: number;
  maxMarketDataAge: string;
  withdrawalsEnabled: boolean;
  maxSlippageBps?: number | null;
  maxVolatilityBps?: number | null;
  minLiquidityNotional?: number | null;
}

export interface RiskPolicyDocument {
  policy: RiskPolicy;
  effectiveFromUtc: string;
  expiresAtUtc: string;
  createdBy: string;
  documentHash: string;
  lifecycle: 'Draft' | 'Approved' | 'Active' | 'Superseded' | 'Revoked';
  approvedBy: string | null;
  approvalId: string | null;
  approvedAtUtc: string | null;
}

export interface AutonomyDecisionCommit {
  decisionId: string;
  environment: string;
  account: string;
  scope: string;
  symbol: string;
  strategy: string;
  strategyVersion: string;
  evaluatedCloseUtc: string;
  decisionUtc: string;
  riskPolicyVersion: string;
  configurationFingerprint: string;
  featureSnapshotSha256: string;
  signal: string;
  signalReason: string;
  proposedNotional: number | null;
  outcome: string;
  outcomeReason: string;
  decisionTraceJson: string;
  rejectionReason: string | null;
}

export interface AutonomyDecisionReconstructionReport {
  commit: AutonomyDecisionCommit;
  canonicalIdentityHashMatches: boolean;
  isAuditVerified: boolean;
  intentClientOrderId: string | null;
  expectedFee?: {
    clientOrderId: string;
    expectedCommissionRateBps: number;
    expectedCommissionAsset: string;
    estimatedCommissionNotional: number;
  } | null;
  submissionContext?: {
    clientOrderId: string;
    symbol: string;
    side: string;
    type: string;
    quantity: number;
    price: number;
    notional: number;
  } | null;
  lifecycleEvents?: Array<{
    eventId: string;
    clientOrderId: string;
    fromStatus: string;
    toStatus: string;
    occurredAtUtc: string;
  }> | null;
}

export interface HealthStatusResponse {
  status: 'Healthy' | 'Degraded' | 'Unhealthy';
}

export interface MarketTickerResponse {
  symbol: string;
  bidPrice: number;
  bidQuantity: number;
  askPrice: number;
  askQuantity: number;
  markPrice: number;
  spread: number;
  spreadBps: number;
  observedAtUtc: string;
}

export interface AssetBalance {
  asset: string;
  free: number;
  locked: number;
  total: number;
  valueUsd: number;
}

export interface PortfolioBalancesResponse {
  environment: string;
  canTrade: boolean;
  totalNavUsd: number;
  cashUsdt: number;
  positionsUsd: number;
  balances: AssetBalance[];
  observedAtUtc: string;
}

export interface OpenOrderResponse {
  symbol: string;
  orderId: number;
  clientOrderId: string;
  price: number;
  origQty: number;
  executedQty: number;
  status: string;
  type: string;
  side: 'BUY' | 'SELL';
  timeUtc: string;
}

export interface TradeHistoryResponse {
  symbol: string;
  id: number;
  orderId: number;
  price: number;
  qty: number;
  quoteQty: number;
  commission: number;
  commissionAsset: string;
  timeUtc: string;
  isBuyer: boolean;
  isMaker: boolean;
}

export interface PortfolioPerformanceResponse {
  feesAreComplete: boolean;
  unvaluedFeeAmounts: Array<{ asset: string; amount: number }>;
  scopeSymbol: string;
  quoteAsset: string;
  asOfUtc: string;
  totalNavUsd: number;
  initialCapitalUsd: number | null;
  initialCapitalAvailabilityReason: string;
  cashBalanceUsd: number | null;
  positionsValueUsd: number | null;
  totalProfitUsd: number;
  totalProfitPct: number | null;
  totalProfitPctAvailabilityReason: string;
  totalRealizedPnlUsd: number;
  totalUnrealizedPnlUsd: number;
  totalFeesPaidUsd: number;
  totalTradesCount: number;
  winRatePercentage: number;
  profitFactor: number | null;
  profitFactorAvailabilityReason: string | null;
  periods: Array<{
    periodName: string;
    profitUsd: number | null;
    profitPercentage: number | null;
    realizedProfitUsd: number;
    unrealizedProfitUsd: number;
    feesPaidUsd: number;
    tradesCount: number;
    winRatePercentage: number;
    snapshotAvailability?: string;
  }>;
}

export interface TelemetryMetricsResponse {
  apiRequests: number;
  apiErrors: number;
  workerHeartbeats: number;
  apiDurationMilliseconds: number;
  alertDeliveries: number;
  alertDeliveryFailures: number;
  alertDeduplicated: number;
  reconciliationRuns: number;
  reconciliationMismatches: number;
  outboxDispatches: number;
  outboxDispatchFailures: number;
  outboxDeadLetters: number;
  invalidMarketDataPayloads: number;
}

export interface ApiErrorResponse {
  error: string;
  correlationId?: string;
  status: number;
}

export interface PeriodPerformance {
  periodName: string;
  profitUsd: number;
  profitBrl: number;
  profitPercentage: number;
  realizedProfitUsd: number;
  unrealizedProfitUsd: number;
  feesPaidUsd: number;
  tradesCount: number;
  winRatePercentage: number;
}

export interface PortfolioFinancialSnapshot {
  totalDepositedUsd: number;
  totalDepositedBrl: number;
  totalWithdrawnUsd: number;
  totalWithdrawnBrl: number;
  netInvestedCapitalUsd: number;
  netInvestedCapitalBrl: number;
  currentNavUsd: number;
  currentNavBrl: number;
  cashBalanceUsd: number;
  positionsMarketValueUsd: number;
  totalProfitUsd: number;
  totalProfitBrl: number;
  totalProfitPercentage: number;
  day: PeriodPerformance;
  week: PeriodPerformance;
  month: PeriodPerformance;
  year: PeriodPerformance;
  allTime: PeriodPerformance;
  usdBrlRate: number;
  calculatedAtUtc: string;
}

export interface MarketRegimeSnapshot {
  symbol: string;
  regime: string;
  confidence: number;
  trendSlopeRate: number;
  volatilityRate: number;
  reason: string;
  observedAtUtc: string;
  favorsTrendFollowing?: boolean;
  favorsMeanReversion?: boolean;
  prohibitsNewExposure?: boolean;
}

export interface StrategyModelRecord {
  modelId: string;
  strategyId: string;
  version: string;
  codeCommitSha: string;
  datasetHash: string;
  status: string;
  registeredAtUtc: string;
  registeredBy: string;
  promotionJustification: string | null;
  canonicalHash: string;
}

export interface DriftObservationRecord {
  observationId: string;
  strategyId: string;
  type: string;
  expectedValue: number;
  observedValue: number;
  allowedDeviationThreshold: number;
  absoluteDeviation: number;
  relativeRate: number;
  severity: string;
  requiresQuarantine: boolean;
  message: string;
  observedAtUtc: string;
}

export interface StrategyRegimePerformanceRecord {
  strategyId: string;
  regime: string;
  observationCount: number;
  cumulativeReward: number;
  averageReward: number;
}
