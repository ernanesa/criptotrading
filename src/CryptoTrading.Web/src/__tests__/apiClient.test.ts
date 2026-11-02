import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, apiClient, apiRequest } from '../api/client';

const status = {
  source: 'test',
  mode: 'Normal',
  blocksNewEntries: false,
  revision: 1,
  changedAtUtc: '2026-11-02T00:00:00Z',
  changedBy: null,
  reason: null,
  mutatingEndpointsEnabled: false,
};
const decision = {
  commit: {
    decisionId: 'ad-123',
    environment: 'PAPER',
    accountScope: 'test',
    checkpointScope: 'test',
    symbol: { value: 'BTCUSDT' },
    strategyId: 'test',
    strategyVersion: 'v1',
    closeTimeUtc: '2026-11-02T00:00:00Z',
    availableAtUtc: '2026-11-02T00:00:00Z',
    featureSchemaVersion: 'v1',
    featureConfigurationFingerprint: 'test',
    featureFingerprint: 'test',
    signal: 'Hold',
    strategyReason: 'test',
    riskDecision: null,
    outcome: 'NO_TRADE',
    noTradeReason: 'test',
    traceJson: '{}',
    outboxRecord: null,
  },
  canonicalIdentityHashMatches: true,
  expectedCanonicalIdentity: 'test',
  isAuditVerified: true,
  hasCompleteDispatchAudit: true,
  intentClientOrderId: null,
  expectedFee: null,
  submissionContext: null,
  lifecycleEvents: null,
};

describe('apiClient', () => {
  const originalFetch = globalThis.fetch;

  beforeEach(() => {
    vi.restoreAllMocks();
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.useRealTimers();
  });

  it('attaches X-Correlation-Id header to requests', async () => {
    let capturedHeaders: Headers | undefined;

    globalThis.fetch = vi.fn().mockImplementation(async (_url, init) => {
      capturedHeaders = new Headers(init?.headers);
      return new Response(JSON.stringify(status), {
        status: 200,
        headers: { 'Content-Type': 'application/json', 'X-Correlation-Id': 'corr-test-123' },
      });
    });

    const data = await apiRequest<{ mode: string }>('/operational/status', {
      correlationId: 'corr-test-123',
    });

    expect(data.mode).toBe('Normal');
    expect(capturedHeaders?.get('X-Correlation-Id')).toBe('corr-test-123');
  });

  it('throws ApiError with 403 status on non-loopback access restriction', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ error: 'Forbidden' }), {
        status: 403,
        headers: { 'Content-Type': 'application/json' },
      }),
    );

    await expect(apiClient.getOperationalStatus()).rejects.toThrowError(ApiError);
    await expect(apiClient.getOperationalStatus()).rejects.toMatchObject({
      status: 403,
    });
  });

  it('throws ApiError with 503 status when persistence is disconnected', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response(null, {
        status: 503,
      }),
    );

    await expect(apiClient.getActiveRiskPolicy()).rejects.toThrowError(ApiError);
    await expect(apiClient.getActiveRiskPolicy()).rejects.toMatchObject({
      status: 503,
    });
  });

  it('uses the sanitized problem title instead of an untrusted error detail', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({ title: 'Market data could not be retrieved.', detail: 'credential=should-not-be-shown' }),
        {
          status: 502,
          headers: { 'Content-Type': 'application/problem+json' },
        },
      ),
    );

    await expect(apiClient.getMarketTicker()).rejects.toMatchObject({
      status: 502,
      message: 'Market data could not be retrieved.',
    });
  });

  it('correctly requests decision by ID', async () => {
    let requestedUrl = '';
    globalThis.fetch = vi.fn().mockImplementation(async (url) => {
      requestedUrl = String(url);
      return new Response(JSON.stringify(decision), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    });

    const result = await apiClient.getOperationalDecision('ad-123');
    expect(requestedUrl).toBe('/operational/decisions/ad-123');
    expect(result.commit.decisionId).toBe('ad-123');
  });

  it('rejects successful HTML instead of publishing a typed operational status', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response('<html>fallback</html>', {
        status: 200,
        headers: { 'Content-Type': 'text/html', 'X-Correlation-Id': 'invalid-response-id' },
      }),
    );
    await expect(apiClient.getOperationalStatus()).rejects.toMatchObject({
      name: 'ApiError',
      status: 200,
      correlationId: 'invalid-response-id',
      kind: 'invalid_response',
    });
  });

  it('wraps malformed JSON with HTTP and correlation metadata', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response('{broken', {
        status: 200,
        headers: { 'Content-Type': 'application/json', 'X-Correlation-Id': 'broken-json-id' },
      }),
    );
    await expect(apiClient.getOperationalStatus()).rejects.toMatchObject({
      name: 'ApiError',
      status: 200,
      correlationId: 'broken-json-id',
      kind: 'invalid_response',
    });
  });

  it('preserves the safe problem title without inventing a database outage', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({
          title: 'Market clock is unavailable.',
          detail: 'credential=never-display',
        }),
        { status: 503, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    );
    await expect(apiClient.getOperationalStatus()).rejects.toMatchObject({
      status: 503,
      message: 'Market clock is unavailable.',
    });
  });

  it('forwards query cancellation to fetch and preserves cancellation semantics', async () => {
    let signal: AbortSignal | undefined;
    globalThis.fetch = vi.fn().mockImplementation((_url, init) => {
      signal = init.signal;
      return new Promise((_resolve, reject) =>
        signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true }),
      );
    });
    const controller = new AbortController();
    const pending = apiClient.getOperationalStatus({ signal: controller.signal });
    const failure = expect(pending).rejects.toMatchObject({ name: 'AbortError' });
    controller.abort();
    expect(signal?.aborted).toBe(true);
    await failure;
  });

  it('bounds a stalled request with an explicit application timeout', async () => {
    vi.useFakeTimers();
    globalThis.fetch = vi.fn().mockImplementation(
      (_url, init) =>
        new Promise((_resolve, reject) => {
          init.signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), {
            once: true,
          });
        }),
    );
    const pending = apiRequest('/health/live', { timeoutMs: 100 });
    const failure = expect(pending).rejects.toMatchObject({ name: 'ApiError', kind: 'timeout' });
    await vi.advanceTimersByTimeAsync(100);
    await failure;
  });

  it.each([
    ['/operational/status', 200, 'application/json'],
    ['/health/live', 200, 'text/plain'],
    ['/operational/status', 503, 'application/problem+json'],
  ])('bounds body reads after headers for %s / %s', async (endpoint, status, contentType) => {
    vi.useFakeTimers();
    globalThis.fetch = vi.fn().mockImplementation(
      async (_url, init) =>
        new Response(
          new ReadableStream({
            start(controller) {
              init.signal.addEventListener('abort', () => controller.error(new DOMException('Aborted', 'AbortError')), {
                once: true,
              });
            },
          }),
          { status, headers: { 'Content-Type': contentType, 'X-Correlation-Id': 'body-id' } },
        ),
    );
    const failure = expect(apiRequest(endpoint as string, { timeoutMs: 100 })).rejects.toMatchObject({
      kind: 'timeout',
      correlationId: 'body-id',
      status,
    });
    await vi.advanceTimersByTimeAsync(100);
    await failure;
  });

  it('preserves external cancellation during JSON body reads', async () => {
    globalThis.fetch = vi.fn().mockImplementation(
      async (_url, init) =>
        new Response(
          new ReadableStream({
            start(controller) {
              init.signal.addEventListener('abort', () => controller.error(new DOMException('Aborted', 'AbortError')), {
                once: true,
              });
            },
          }),
          { headers: { 'Content-Type': 'application/json' } },
        ),
    );
    const controller = new AbortController();
    const failure = expect(apiClient.getOperationalStatus({ signal: controller.signal })).rejects.toMatchObject({
      name: 'AbortError',
    });
    await Promise.resolve();
    controller.abort();
    await failure;
  });

  it.each([
    null,
    [],
    {},
    { ...status, mode: 'FutureMode' },
    { ...status, revision: '1' },
    { ...status, changedAtUtc: 'not-a-date' },
    { ...status, blocksNewEntries: null },
  ])('rejects an incompatible operational payload: %j', async (payload) => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(payload), {
        headers: { 'Content-Type': 'application/json', 'X-Correlation-Id': 'schema-id' },
      }),
    );
    await expect(apiClient.getOperationalStatus()).rejects.toMatchObject({
      kind: 'invalid_response',
      correlationId: 'schema-id',
      status: 200,
    });
  });

  it.each([
    () => apiClient.getHealthLive(),
    () => apiClient.getHealthReady(),
    () => apiClient.getOperationalDecision('test'),
    () => apiClient.getAutonomyCheckpoint('test'),
    () => apiClient.getActiveRiskPolicy(),
    () => apiClient.getMarketTicker(),
    () => apiClient.getPortfolioBalances(),
    () => apiClient.getOpenOrders(),
    () => apiClient.getTrades(),
    () => apiClient.getPortfolioPerformance(),
    () => apiClient.getTelemetryMetrics(),
    () => apiClient.getCurrentMarketRegime(),
    () => apiClient.getMarketRegimeHistory(),
    () => apiClient.getRegisteredModels(),
    () => apiClient.getRecentDrifts(),
    () => apiClient.getStrategyAllocations(),
  ])('validates each public response contract', async (request) => {
    globalThis.fetch = vi
      .fn()
      .mockResolvedValue(new Response('{}', { headers: { 'Content-Type': 'application/json' } }));
    await expect(request()).rejects.toMatchObject({ kind: 'invalid_response' });
  });

  it('rejects invalid nested array members and non-finite JSON numbers', async () => {
    globalThis.fetch = vi.fn().mockImplementation(
      async () =>
        new Response(
          '[{"strategyId":"test","regime":"test","observationCount":1,"cumulativeReward":1e999,"averageReward":0}]',
          {
            headers: { 'Content-Type': 'application/json' },
          },
        ),
    );
    await expect(apiClient.getStrategyAllocations()).rejects.toMatchObject({ kind: 'invalid_response' });
  });

  it('accepts valid nullable fields, additional properties and empty collections', async () => {
    globalThis.fetch = vi.fn().mockImplementation(
      async () =>
        new Response(JSON.stringify({ ...status, futureField: true }), {
          headers: { 'Content-Type': 'application/json' },
        }),
    );
    await expect(apiClient.getOperationalStatus()).resolves.toMatchObject(status);
    globalThis.fetch = vi
      .fn()
      .mockImplementation(async () => new Response('[]', { headers: { 'Content-Type': 'application/json' } }));
    await expect(apiClient.getOpenOrders()).resolves.toEqual([]);
  });

  it('accepts the wire decision contract with populated audit evidence', async () => {
    const payload = {
      ...decision,
      commit: {
        ...decision.commit,
        riskDecision: {
          status: 'Approved',
          requestedTargetNotional: 100,
          authorizedTargetNotional: 100,
          policyVersion: 'v1',
          reasonCodes: [],
        },
      },
      expectedFee: {
        rate: 0.001,
        snapshotHash: 'hash',
        source: 'test',
        observedAtUtc: '2026-11-02T00:00:00Z',
        validUntilUtc: '2026-11-02T01:00:00Z',
        referencePrice: null,
        authorizedQuantity: null,
        decisionAtUtc: null,
      },
      submissionContext: {
        clientOrderId: 'test',
        applicationOrderId: { value: 'test' },
        correlationId: { value: 'test' },
        outboxMessageId: 'test',
        symbol: { value: 'BTCUSDT' },
        side: 'Buy',
        type: 'Market',
        authorizedQuantity: { value: 1 },
        referencePrice: { value: 100 },
        authorizedNotional: 100,
        executionAttempt: 1,
        plannedAtUtc: '2026-11-02T00:00:00Z',
        expiresAtUtc: null,
      },
      lifecycleEvents: [
        {
          eventId: 'test',
          clientOrderId: 'test',
          transition: {
            from: 'Created',
            to: 'Submitted',
            idempotencyKey: { value: 'test' },
            occurredAtUtc: '2026-11-02T00:00:00Z',
            exchangeOrderId: null,
          },
        },
      ],
    };
    globalThis.fetch = vi
      .fn()
      .mockImplementation(
        async () => new Response(JSON.stringify(payload), { headers: { 'Content-Type': 'application/json' } }),
      );
    await expect(apiClient.getOperationalDecision('ad-123')).resolves.toEqual(payload);
    payload.submissionContext.authorizedQuantity.value = Number.NaN;
    await expect(apiClient.getOperationalDecision('ad-123')).rejects.toMatchObject({ kind: 'invalid_response' });
  });

  it('preserves legitimately unavailable unrealized period P&L', async () => {
    const payload = {
      feesAreComplete: true,
      unvaluedFeeAmounts: [],
      scopeSymbol: 'BTCUSDT',
      quoteAsset: 'USDT',
      asOfUtc: '2026-11-02T00:00:00Z',
      totalNavUsd: 100,
      initialCapitalUsd: null,
      initialCapitalAvailabilityReason: 'Unknown',
      cashBalanceUsd: null,
      positionsValueUsd: null,
      totalProfitUsd: 0,
      totalProfitPct: null,
      totalProfitPctAvailabilityReason: 'Unknown',
      totalRealizedPnlUsd: 0,
      totalUnrealizedPnlUsd: 0,
      totalFeesPaidUsd: 0,
      totalTradesCount: 0,
      winRatePercentage: 0,
      profitFactor: null,
      profitFactorAvailabilityReason: 'Unknown',
      periods: [
        {
          periodName: 'Day',
          profitUsd: null,
          profitPercentage: null,
          realizedProfitUsd: 0,
          unrealizedProfitUsd: null,
          feesPaidUsd: 0,
          tradesCount: 0,
          winRatePercentage: 0,
        },
      ],
    };
    globalThis.fetch = vi
      .fn()
      .mockImplementation(
        async () => new Response(JSON.stringify(payload), { headers: { 'Content-Type': 'application/json' } }),
      );
    await expect(apiClient.getPortfolioPerformance()).resolves.toMatchObject({
      periods: [{ unrealizedProfitUsd: null }],
    });
    globalThis.fetch = vi
      .fn()
      .mockImplementation(
        async () =>
          new Response(
            JSON.stringify({ ...payload, periods: [{ ...payload.periods[0], unrealizedProfitUsd: 'invalid' }] }),
            { headers: { 'Content-Type': 'application/json' } },
          ),
      );
    await expect(apiClient.getPortfolioPerformance()).rejects.toMatchObject({ kind: 'invalid_response' });
  });

  it.each(['POST', 'PUT', 'DELETE', 'PATCH'])('rejects %s without calling fetch', async (method) => {
    globalThis.fetch = vi.fn();
    await expect(apiRequest('/operational/status', { method })).rejects.toMatchObject({ kind: 'invalid_request' });
    expect(globalThis.fetch).not.toHaveBeenCalled();
  });
});
