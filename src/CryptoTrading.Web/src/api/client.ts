import type {
  AutonomyDecisionReconstructionReport,
  CheckpointResponse,
  HealthStatusResponse,
  OperationalStatusResponse,
  RiskPolicyDocument,
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
      if (data && typeof data.error === 'string') {
        message = data.error;
      }
    } catch {
      // Body is not JSON
    }

    if (response.status === 403) {
      message = 'Acesso proibido: o endpoint de diagnóstico requer conexão local de loopback.';
    } else if (response.status === 503) {
      message = 'Serviço indisponível: o armazenamento persistente (PostgreSQL) não está conectado.';
    } else if (response.status === 404) {
      message = message || 'Recurso não encontrado.';
    }

    throw new ApiError(response.status, message, responseCorrelationId);
  }

  return response.json() as Promise<T>;
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
};
