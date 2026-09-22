import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { apiRequest, ApiError, apiClient } from '../api/client';

describe('apiClient', () => {
  const originalFetch = globalThis.fetch;

  beforeEach(() => {
    vi.restoreAllMocks();
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it('attaches X-Correlation-Id header to requests', async () => {
    let capturedHeaders: Headers | undefined;

    globalThis.fetch = vi.fn().mockImplementation(async (_url, init) => {
      capturedHeaders = new Headers(init?.headers);
      return new Response(JSON.stringify({ mode: 'Normal' }), {
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
      })
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
      })
    );

    await expect(apiClient.getActiveRiskPolicy()).rejects.toThrowError(ApiError);
    await expect(apiClient.getActiveRiskPolicy()).rejects.toMatchObject({
      status: 503,
    });
  });

  it('correctly requests decision by ID', async () => {
    let requestedUrl = '';
    globalThis.fetch = vi.fn().mockImplementation(async (url) => {
      requestedUrl = String(url);
      return new Response(JSON.stringify({ commit: { decisionId: 'ad-123' } }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    });

    const result = await apiClient.getOperationalDecision('ad-123');
    expect(requestedUrl).toBe('/operational/decisions/ad-123');
    expect(result.commit.decisionId).toBe('ad-123');
  });
});
