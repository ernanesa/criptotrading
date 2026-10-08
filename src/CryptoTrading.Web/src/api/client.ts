import type {
  AutonomyDecisionReconstructionReport,
  CheckpointResponse,
  DriftObservationRecord,
  HealthStatusResponse,
  MarketRegimeSnapshot,
  MarketTickerResponse,
  OpenOrderResponse,
  OperationalStatusResponse,
  PortfolioBalancesResponse,
  PortfolioPerformanceResponse,
  RiskPolicyDocument,
  StrategyModelRecord,
  StrategyRegimePerformanceRecord,
  TelemetryMetricsResponse,
  TradeHistoryResponse,
} from '../types/api';

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly correlationId?: string | null,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

export function generateCorrelationId(): string {
  if (typeof crypto !== 'undefined' && crypto.randomUUID) {
    return crypto.randomUUID();
  }
  return `corr-${Math.random().toString(36).substring(2, 15)}`;
}

export function formatIsoDate(dateVal: string | number | Date | null | undefined): string {
  if (!dateVal) return 'N/A';
  try {
    const d = new Date(dateVal);
    if (Number.isNaN(d.getTime())) return String(dateVal);
    return d.toISOString();
  } catch {
    return String(dateVal);
  }
}

export function formatUtcTimestamp(dateVal: string | number | Date | null | undefined): string {
  if (!dateVal) return 'N/A';
  try {
    const d = new Date(dateVal);
    if (Number.isNaN(d.getTime())) return String(dateVal);
    return `${d.toISOString().replace('T', ' ').substring(0, 19)} UTC`;
  } catch {
    return String(dateVal);
  }
}

export interface RequestOptions extends RequestInit {
  correlationId?: string;
}

export async function apiRequest<T>(endpoint: string, options: RequestOptions = {}): Promise<T> {
  const correlationId = options.correlationId || generateCorrelationId();
  const headers = new Headers(options.headers || {});
  headers.set('X-Correlation-Id', correlationId);
  headers.set('Accept', 'application/json');

  const response = await fetch(endpoint, {
    ...options,
    headers,
  });

  const responseCorrelationId = response.headers.get('X-Correlation-Id') || correlationId;

  if (!response.ok) {
    let message = `API request failed with status ${response.status}`;
    try {
      const data = await response.json();
      if (data && typeof data.title === 'string') {
        message = data.title;
      } else if (data && typeof data.error === 'string') {
        message = data.error;
      }
    } catch {
      // Body is not JSON
    }

    if (response.status === 403) {
      message = 'Acesso proibido: o endpoint requer conexão local ou proxy privado autenticado.';
    } else if (response.status === 503) {
      message = 'Serviço indisponível: o armazenamento persistente (PostgreSQL) não está conectado.';
    } else if (response.status === 404) {
      message = message || 'Recurso não encontrado.';
    }

    throw new ApiError(response.status, message, responseCorrelationId);
  }

  const contentType = response.headers.get('Content-Type') || '';
  if (contentType.includes('application/json')) {
    return response.json() as Promise<T>;
  }

  const text = (await response.text()).trim();
  if (text === 'Healthy' || text === 'Degraded' || text === 'Unhealthy') {
    return { status: text } as T;
  }

  try {
    return JSON.parse(text) as T;
  } catch {
    return text as unknown as T;
  }
}

export const apiClient = {
  getHealthLive: () => apiRequest<HealthStatusResponse>('/health/live'),
  getHealthReady: () => apiRequest<HealthStatusResponse>('/health/ready'),
  getOperationalStatus: () => apiRequest<OperationalStatusResponse>('/operational/status'),
  getOperationalDecision: (decisionId: string) =>
    apiRequest<AutonomyDecisionReconstructionReport>(`/operational/decisions/${encodeURIComponent(decisionId.trim())}`),
  getAutonomyCheckpoint: (scope: string) =>
    apiRequest<CheckpointResponse>(`/operational/checkpoints/${encodeURIComponent(scope.trim())}`),
  getActiveRiskPolicy: () => apiRequest<RiskPolicyDocument>('/operational/risk-policy'),
  getMarketTicker: (symbol = 'BTCUSDT') =>
    apiRequest<MarketTickerResponse>(`/market/ticker/${encodeURIComponent(symbol.trim())}`),
  getPortfolioBalances: () => apiRequest<PortfolioBalancesResponse>('/portfolio/balances'),
  getOpenOrders: (symbol?: string) =>
    apiRequest<OpenOrderResponse[]>(
      symbol ? `/trading/open-orders?symbol=${encodeURIComponent(symbol.trim())}` : '/trading/open-orders',
    ),
  getTrades: (symbol = 'BTCUSDT') =>
    apiRequest<TradeHistoryResponse[]>(`/trading/trades?symbol=${encodeURIComponent(symbol.trim())}`),
  getPortfolioPerformance: () => apiRequest<PortfolioPerformanceResponse>('/portfolio/performance'),
  getTelemetryMetrics: () => apiRequest<TelemetryMetricsResponse>('/metrics'),
  getCurrentMarketRegime: (symbol = 'BTCUSDT') =>
    apiRequest<MarketRegimeSnapshot>(`/operational/regimes/current?symbol=${encodeURIComponent(symbol.trim())}`),
  getMarketRegimeHistory: (symbol = 'BTCUSDT', limit = 20) =>
    apiRequest<MarketRegimeSnapshot[]>(
      `/operational/regimes/history?symbol=${encodeURIComponent(symbol.trim())}&limit=${limit}`,
    ),
  getRegisteredModels: (strategyId = 'trend-following') =>
    apiRequest<StrategyModelRecord[]>(`/operational/models?strategyId=${encodeURIComponent(strategyId.trim())}`),
  getRecentDrifts: (strategyId?: string, limit = 30) =>
    apiRequest<DriftObservationRecord[]>(
      strategyId
        ? `/operational/drift?strategyId=${encodeURIComponent(strategyId.trim())}&limit=${limit}`
        : `/operational/drift?limit=${limit}`,
    ),
  getStrategyAllocations: () => apiRequest<StrategyRegimePerformanceRecord[]>('/operational/allocations'),
};
