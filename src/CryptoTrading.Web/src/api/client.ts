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
import { validateResponse } from './contracts';

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly correlationId?: string | null,
    public readonly kind: 'http' | 'transport' | 'timeout' | 'invalid_response' | 'invalid_request' = 'http',
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
  timeoutMs?: number;
}

export async function apiRequest<T>(endpoint: string, options: RequestOptions = {}): Promise<T> {
  const { correlationId: requestedCorrelationId, timeoutMs = 15_000, ...requestOptions } = options;
  const correlationId = requestedCorrelationId || generateCorrelationId();
  if (
    !Number.isFinite(timeoutMs) ||
    timeoutMs <= 0 ||
    (options.method && options.method.toUpperCase() !== 'GET') ||
    options.body != null
  ) {
    throw new ApiError(0, 'Configuração inválida para uma consulta somente leitura.', correlationId, 'invalid_request');
  }
  const headers = new Headers(options.headers || {});
  headers.set('X-Correlation-Id', correlationId);
  headers.set('Accept', 'application/json');

  const controller = new AbortController();
  const external = options.signal;
  const abort = () => controller.abort(external?.reason);
  if (external?.aborted) abort();
  else external?.addEventListener('abort', abort, { once: true });
  let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    controller.abort();
  }, timeoutMs);
  let response: Response | undefined;
  let responseCorrelationId = correlationId;
  try {
    response = await fetch(endpoint, { ...requestOptions, headers, method: 'GET', signal: controller.signal });
    responseCorrelationId = response.headers.get('X-Correlation-Id') || correlationId;
    if (!response.ok) {
      let message = `Consulta recusada ou indisponível (HTTP ${response.status}).`;
      try {
        const problem: unknown = await response.json();
        if (
          problem &&
          typeof problem === 'object' &&
          'title' in problem &&
          typeof problem.title === 'string' &&
          problem.title.length <= 240
        ) {
          message = problem.title;
        }
      } catch {
        /* Do not expose raw response bodies or exception details. */
      }
      throw new ApiError(response.status, message, responseCorrelationId);
    }
    const contentType = response.headers.get('Content-Type')?.toLowerCase() || '';
    if (contentType.includes('application/json') || contentType.includes('application/problem+json')) {
      const value: unknown = await response.json();
      if (!validateResponse(endpoint, value)) {
        throw new ApiError(
          response.status,
          'A resposta da API não corresponde ao contrato esperado.',
          responseCorrelationId,
          'invalid_response',
        );
      }
      return value as T;
    }
    if (endpoint === '/health/live' || endpoint === '/health/ready') {
      const text = (await response.text()).trim();
      if (['Healthy', 'Degraded', 'Unhealthy'].includes(text)) return { status: text } as T;
    }
    throw new ApiError(
      response.status,
      'A API retornou um formato de resposta inesperado.',
      responseCorrelationId,
      'invalid_response',
    );
  } catch (error) {
    if (external?.aborted) throw external.reason ?? new DOMException('Consulta cancelada.', 'AbortError');
    if (timedOut)
      throw new ApiError(response?.status ?? 0, 'Tempo limite da consulta excedido.', responseCorrelationId, 'timeout');
    if (error instanceof ApiError) throw error;
    throw new ApiError(
      response?.status ?? 0,
      response ? 'Resposta JSON inválida da API.' : 'Não foi possível consultar a API.',
      responseCorrelationId,
      response ? 'invalid_response' : 'transport',
    );
  } finally {
    clearTimeout(timer);
    external?.removeEventListener('abort', abort);
  }
}

export const apiClient = {
  getHealthLive: (options: RequestOptions = {}) => apiRequest<HealthStatusResponse>('/health/live', options),
  getHealthReady: (options: RequestOptions = {}) => apiRequest<HealthStatusResponse>('/health/ready', options),
  getOperationalStatus: (options: RequestOptions = {}) =>
    apiRequest<OperationalStatusResponse>('/operational/status', options),
  getOperationalDecision: (decisionId: string, options: RequestOptions = {}) =>
    apiRequest<AutonomyDecisionReconstructionReport>(
      `/operational/decisions/${encodeURIComponent(decisionId.trim())}`,
      options,
    ),
  getAutonomyCheckpoint: (scope: string, options: RequestOptions = {}) =>
    apiRequest<CheckpointResponse>(`/operational/checkpoints/${encodeURIComponent(scope.trim())}`, options),
  getActiveRiskPolicy: (options: RequestOptions = {}) =>
    apiRequest<RiskPolicyDocument>('/operational/risk-policy', options),
  getMarketTicker: (symbol = 'BTCUSDT', options: RequestOptions = {}) =>
    apiRequest<MarketTickerResponse>(`/market/ticker/${encodeURIComponent(symbol.trim())}`, options),
  getPortfolioBalances: (options: RequestOptions = {}) =>
    apiRequest<PortfolioBalancesResponse>('/portfolio/balances', options),
  getOpenOrders: (symbol?: string, options: RequestOptions = {}) =>
    apiRequest<OpenOrderResponse[]>(
      symbol ? `/trading/open-orders?symbol=${encodeURIComponent(symbol.trim())}` : '/trading/open-orders',
      options,
    ),
  getTrades: (symbol = 'BTCUSDT', options: RequestOptions = {}) =>
    apiRequest<TradeHistoryResponse[]>(`/trading/trades?symbol=${encodeURIComponent(symbol.trim())}`, options),
  getPortfolioPerformance: (options: RequestOptions = {}) =>
    apiRequest<PortfolioPerformanceResponse>('/portfolio/performance', options),
  getTelemetryMetrics: (options: RequestOptions = {}) => apiRequest<TelemetryMetricsResponse>('/metrics', options),
  getCurrentMarketRegime: (symbol = 'BTCUSDT', options: RequestOptions = {}) =>
    apiRequest<MarketRegimeSnapshot>(
      `/operational/regimes/current?symbol=${encodeURIComponent(symbol.trim())}`,
      options,
    ),
  getMarketRegimeHistory: (symbol = 'BTCUSDT', limit = 20, options: RequestOptions = {}) =>
    apiRequest<MarketRegimeSnapshot[]>(
      `/operational/regimes/history?symbol=${encodeURIComponent(symbol.trim())}&limit=${limit}`,
      options,
    ),
  getRegisteredModels: (strategyId = 'trend-following', options: RequestOptions = {}) =>
    apiRequest<StrategyModelRecord[]>(
      `/operational/models?strategyId=${encodeURIComponent(strategyId.trim())}`,
      options,
    ),
  getRecentDrifts: (strategyId?: string, limit = 30, options: RequestOptions = {}) =>
    apiRequest<DriftObservationRecord[]>(
      strategyId
        ? `/operational/drift?strategyId=${encodeURIComponent(strategyId.trim())}&limit=${limit}`
        : `/operational/drift?limit=${limit}`,
      options,
    ),
  getStrategyAllocations: (options: RequestOptions = {}) =>
    apiRequest<StrategyRegimePerformanceRecord[]>('/operational/allocations', options),
};
