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
