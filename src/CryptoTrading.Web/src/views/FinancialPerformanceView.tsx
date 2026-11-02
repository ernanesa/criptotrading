import { useQuery } from '@tanstack/react-query';
import { Calendar, CheckCircle2, Clock, DollarSign, Layers, Percent, TrendingUp, Wallet } from 'lucide-react';
import type React from 'react';
import { useState } from 'react';
import { apiClient } from '../api/client';
import { QueryStatus } from '../components/QueryStatus';

export const FinancialPerformanceView: React.FC = () => {
  const [usdInput, setUsdInput] = useState('5.65');
  const usdBrl = Number(usdInput);
  const validRate = usdInput.trim() !== '' && Number.isFinite(usdBrl) && usdBrl > 0;
  const toBrl = (value: number | null): number | null => {
    const result = value === null || !validRate ? null : value * usdBrl;
    return result !== null && Number.isFinite(result) ? result : null;
  };

  const performanceQuery = useQuery({
    queryKey: ['portfolioPerformance'],
    queryFn: ({ signal }) => apiClient.getPortfolioPerformance({ signal }),
    refetchInterval: 5000,
  });

  const perf = performanceQuery.data;

  if (!perf) {
    const availabilityMessage = performanceQuery.isError
      ? 'Dados de P&L indisponíveis. Não foi possível concluir a consulta à API.'
      : 'Carregando snapshots de P&L auditáveis...';

    return (
      <div className="space-y-6" data-testid="financial-performance-view">
        <div className="border-b border-slate-800 pb-5">
          <div className="flex items-center gap-2">
            <Wallet className="w-5 h-5 text-emerald-400" />
            <h1 className="text-xl font-bold text-slate-100">Patrimônio, Capital Investido & Lucro (P&L)</h1>
          </div>
          <p className="text-xs text-slate-400 mt-1">
            Valores são exibidos somente quando há snapshots e contexto contábil suficientes.
          </p>
        </div>
        <div
          className="rounded-xl border border-amber-700/60 bg-amber-950/30 p-5 text-sm text-amber-200"
          data-testid="performance-unavailable"
        >
          {availabilityMessage}
        </div>
      </div>
    );
  }

  const currentNavUsd = perf.totalNavUsd;
  const initialCapitalUsd = perf.initialCapitalUsd;
  const cashBalanceUsd = perf.cashBalanceUsd;
  const positionsValueUsd = perf.positionsValueUsd;

  const totalProfitUsd = perf.totalProfitUsd;
  const totalProfitBrl = toBrl(totalProfitUsd);
  const totalProfitPct = perf.totalProfitPct;
  const totalFeesPaidUsd = perf.totalFeesPaidUsd;
  const totalTradesCount = perf.totalTradesCount;
  const winRatePercentage = perf.winRatePercentage;
  const profitFactor = perf.profitFactor;

  const currentNavBrl = toBrl(currentNavUsd);
  const initialCapitalBrl = toBrl(initialCapitalUsd);
  const cashAndPositions =
    cashBalanceUsd !== null && positionsValueUsd !== null
      ? `Caixa USDT: $${cashBalanceUsd.toFixed(2)} • Cripto: $${positionsValueUsd.toFixed(2)}`
      : 'Composição de caixa e posições indisponível para este snapshot.';

  const periods = perf.periods
    .filter((period) => period.snapshotAvailability === undefined || period.snapshotAvailability === 'Available')
    .map((period) => ({
      ...period,
      profitBrl: toBrl(period.profitUsd),
    }));
  const hasUnavailableHistory = periods.length !== perf.periods.length;
  const hasPartialPerformance = !perf.feesAreComplete || initialCapitalUsd === null || totalProfitPct === null;

  return (
    <div className="space-y-6" data-testid="financial-performance-view">
      <QueryStatus label="Performance" query={performanceQuery} observedAt={perf.asOfUtc} />
      {/* Header */}
      <div className="flex flex-col md:flex-row md:items-center justify-between gap-4 border-b border-slate-800 pb-5">
        <div>
          <div className="flex items-center gap-2">
            <Wallet className="w-5 h-5 text-emerald-400" />
            <h1 className="text-xl font-bold text-slate-100">Patrimônio, Capital Investido & Lucro (P&L)</h1>
          </div>
          <p className="text-xs text-slate-400 mt-1">
            Valores dos snapshots contábeis fornecidos pela API. Conversões em BRL são simulações com cotação manual,
            não resultados cambiais realizados.
          </p>
        </div>

        {/* Manual exchange-rate assumption, not an official quote. */}
        <div className="flex flex-wrap items-center gap-3">
          <div className="flex items-center gap-2 bg-slate-900/80 px-3 py-1.5 rounded-lg border border-slate-800 text-xs">
            <span className="text-slate-400">USD/BRL simulado (R$):</span>
            <input
              type="number"
              step="0.05"
              min="0.01"
              aria-label="Cotação manual USD/BRL"
              aria-invalid={!validRate}
              value={usdInput}
              onChange={(e) => setUsdInput(e.target.value)}
              className="w-20 bg-slate-950 border border-slate-700 rounded px-2 py-0.5 text-right text-slate-100 font-mono focus:outline-none focus:border-emerald-500"
            />
          </div>
        </div>
      </div>

      {!validRate && (
        <p role="alert">Cotação inválida: informe um número finito maior que zero. Conversões em BRL indisponíveis.</p>
      )}

      {/* Painel Principal de Capital e Patrimônio */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        {/* Card 1: Quanto de Dinheiro Investimos */}
        <div className="bg-slate-900/80 border border-slate-800 rounded-xl p-5 relative overflow-hidden shadow-sm">
          <div className="flex items-center justify-between text-xs text-slate-400 mb-2">
            <span className="font-semibold uppercase tracking-wider text-[11px]">Capital Investido (Aporte)</span>
            <DollarSign className="w-4 h-4 text-cyan-400" />
          </div>
          <div className="text-2xl font-bold text-slate-100 font-mono">
            {initialCapitalBrl === null
              ? 'Indisponível'
              : `R$ ${initialCapitalBrl.toLocaleString('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`}
          </div>
          <div className="text-xs text-cyan-400 font-mono mt-1">
            {initialCapitalUsd === null
              ? 'Baseline e fluxos externos ainda não auditáveis'
              : `$ ${initialCapitalUsd.toLocaleString('en-US', { minimumFractionDigits: 2 })} USD`}
          </div>
          <p className="text-[11px] text-slate-400 mt-2">Base de custo líquido aportada no fundo operacional.</p>
        </div>

        {/* Card 2: Patrimônio Atual (NAV) */}
        <div className="bg-slate-900/80 border border-slate-800 rounded-xl p-5 relative overflow-hidden shadow-sm">
          <div className="flex items-center justify-between text-xs text-slate-400 mb-2">
            <span className="font-semibold uppercase tracking-wider text-[11px]">Patrimônio Atual (NAV)</span>
            <Layers className="w-4 h-4 text-emerald-400" />
          </div>
          <div className="text-2xl font-bold text-emerald-400 font-mono">
            {currentNavBrl === null
              ? 'Indisponível'
              : `R$ ${currentNavBrl.toLocaleString('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`}
          </div>
          <div className="text-xs text-emerald-400/90 font-mono mt-1">
            $ {currentNavUsd.toLocaleString('en-US', { minimumFractionDigits: 2 })} USD
          </div>
          <p className="text-[11px] text-slate-400 mt-2">{cashAndPositions}</p>
        </div>

        {/* Card 3: Lucro Total Acumulado */}
        <div className="bg-slate-900/80 border border-slate-800 rounded-xl p-5 relative overflow-hidden shadow-sm">
          <div className="flex items-center justify-between text-xs text-slate-400 mb-2">
            <span className="font-semibold uppercase tracking-wider text-[11px]">P&L conhecido</span>
            <span
              className={`px-1.5 py-0.5 rounded text-[10px] font-bold ${
                totalProfitUsd >= 0
                  ? 'bg-emerald-500/20 text-emerald-400 border border-emerald-500/30'
                  : 'bg-rose-500/20 text-rose-400 border border-rose-500/30'
              }`}
            >
              {totalProfitPct === null
                ? 'Rentabilidade indisponível'
                : totalProfitPct >= 0
                  ? `+${totalProfitPct.toFixed(2)}%`
                  : `${totalProfitPct.toFixed(2)}%`}
            </span>
          </div>
          <div
            className={`text-2xl font-bold font-mono ${totalProfitBrl !== null && totalProfitBrl >= 0 ? 'text-emerald-400' : 'text-slate-300'}`}
          >
            {totalProfitBrl === null
              ? 'Indisponível'
              : `${totalProfitBrl >= 0 ? '+R$ ' : '-R$ '}${Math.abs(totalProfitBrl).toLocaleString('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`}
          </div>
          <div className={`text-xs font-mono mt-1 ${totalProfitUsd >= 0 ? 'text-emerald-400/90' : 'text-rose-400/90'}`}>
            {totalProfitUsd >= 0 ? '+$' : '-$'}
            {Math.abs(totalProfitUsd).toLocaleString('en-US', { minimumFractionDigits: 2 })} USD
          </div>
          <p className="text-[11px] text-slate-400 mt-2">
            Valores conhecidos de P&L; consulte o aviso de completude abaixo.
          </p>
        </div>

        {/* Card 4: Taxa de Câmbio & Eficiência */}
        <div className="bg-slate-900/80 border border-slate-800 rounded-xl p-5 relative overflow-hidden shadow-sm">
          <div className="flex items-center justify-between text-xs text-slate-400 mb-2">
            <span className="font-semibold uppercase tracking-wider text-[11px]">Win Rate Geral</span>
            <Percent className="w-4 h-4 text-purple-400" />
          </div>
          <div className="text-2xl font-bold text-purple-400 font-mono">
            {winRatePercentage.toFixed(1)}%{' '}
            <span className="text-xs font-normal text-slate-400">({totalTradesCount} trades)</span>
          </div>
          <div className="text-xs text-slate-400 font-mono mt-1">
            Profit Factor:{' '}
            <strong className="text-purple-300">
              {profitFactor === null ? 'Indisponível' : profitFactor.toFixed(2)}
            </strong>
          </div>
          <p className="text-[11px] text-slate-400 mt-2">
            Taxas de corretagem pagas: ${totalFeesPaidUsd.toFixed(2)} USD.
          </p>
        </div>
      </div>

      {/* Os 4 Períodos: Dia, Semana, Mês e Ano */}
      <div>
        <div className="flex items-center justify-between mb-3">
          <h2 className="text-sm font-bold text-slate-200 uppercase tracking-wider flex items-center gap-2">
            <Clock className="w-4 h-4 text-emerald-400" />
            <span>Lucro nos Períodos: Dia, Semana, Mês e Ano</span>
          </h2>
          <span className="text-xs text-slate-400">Valores líquidos auditados em Reais (BRL) e Dólares (USD)</span>
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
          {periods.map((p, idx) => {
            const icons = [Clock, Calendar, Calendar, TrendingUp];
            const Icon = icons[idx] || TrendingUp;
            const isPositive = p.profitUsd !== null && p.profitUsd >= 0;
            return (
              <div
                key={p.periodName}
                className="bg-slate-900/70 border border-slate-800 hover:border-slate-700 rounded-xl p-5 transition-all shadow-sm"
              >
                <div className="flex items-center justify-between text-xs text-slate-400 mb-2">
                  <span className="font-medium text-slate-300">{p.periodName}</span>
                  <Icon className="w-3.5 h-3.5 text-emerald-400" />
                </div>

                {/* Lucro em Reais */}
                <div className={`text-xl font-bold font-mono ${isPositive ? 'text-emerald-400' : 'text-rose-400'}`}>
                  {p.profitBrl === null
                    ? 'Indisponível'
                    : `${isPositive ? '+R$ ' : '-R$ '}${Math.abs(p.profitBrl).toLocaleString('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`}
                </div>

                {/* Lucro em Dólares e Percentual */}
                <div className="flex items-center justify-between mt-1 text-xs font-mono">
                  <span className="text-slate-300">
                    {p.profitUsd === null
                      ? 'Indisponível'
                      : `${isPositive ? '+$' : '-$'}${Math.abs(p.profitUsd).toFixed(2)} USD`}
                  </span>
                  <span
                    className={`font-bold px-1.5 py-0.5 rounded ${
                      isPositive ? 'text-emerald-400 bg-emerald-500/10' : 'text-rose-400 bg-rose-500/10'
                    }`}
                  >
                    {p.profitPercentage === null
                      ? 'Indisponível'
                      : isPositive
                        ? `+${p.profitPercentage.toFixed(2)}%`
                        : `${p.profitPercentage.toFixed(2)}%`}
                  </span>
                </div>

                {/* Decomposição do Período */}
                <div className="mt-3 pt-3 border-t border-slate-800/80 space-y-1.5 text-[11px] text-slate-400">
                  <div className="flex justify-between">
                    <span>Lucro Realizado:</span>
                    <span className="text-slate-200 font-mono">
                      {p.realizedProfitUsd >= 0
                        ? `+$${p.realizedProfitUsd.toFixed(2)}`
                        : `-$${Math.abs(p.realizedProfitUsd).toFixed(2)}`}
                    </span>
                  </div>
                  <div className="flex justify-between">
                    <span>Lucro Não-Realizado:</span>
                    <span className="text-cyan-400 font-mono">
                      {p.unrealizedProfitUsd === null
                        ? 'Indisponível'
                        : p.unrealizedProfitUsd >= 0
                          ? `+$${p.unrealizedProfitUsd.toFixed(2)}`
                          : `-$${Math.abs(p.unrealizedProfitUsd).toFixed(2)}`}
                    </span>
                  </div>
                  <div className="flex justify-between">
                    <span>Taxas Deduzidas:</span>
                    <span className="text-rose-400 font-mono">-${p.feesPaidUsd.toFixed(2)}</span>
                  </div>
                  <div className="flex justify-between font-semibold pt-1 border-t border-slate-800/50">
                    <span>Trades / Win Rate:</span>
                    <span className="text-emerald-400 font-mono">
                      {p.tradesCount} trades ({p.winRatePercentage.toFixed(1)}%)
                    </span>
                  </div>
                </div>
              </div>
            );
          })}
        </div>
      </div>

      {(hasUnavailableHistory || hasPartialPerformance) && (
        <div
          className="rounded-xl border border-amber-700/60 bg-amber-950/30 p-4 text-xs text-amber-200"
          data-testid="performance-history-partial"
        >
          P&L parcial: somente valores com fonte conhecida são exibidos. Capital inicial e rentabilidade acumulada
          dependem de baseline e fluxos externos duráveis; profit factor depende de perdas realizadas. Taxas:{' '}
          {perf.feesAreComplete ? 'completas.' : 'incompletas.'}
        </div>
      )}

      {/* Tabela Analítica de P&L Consolidado */}
      <div className="bg-slate-900/40 border border-slate-800 rounded-xl overflow-hidden">
        <div className="p-4 border-b border-slate-800 flex items-center justify-between">
          <div className="flex items-center gap-2">
            <TrendingUp className="w-4 h-4 text-emerald-400" />
            <h2 className="text-sm font-semibold text-slate-200">Demonstrativo de Resultado de Negociação (DRE)</h2>
          </div>
          <span className="text-xs text-slate-400 font-mono">
            Binance Spot • Câmbio manual simulado {validRate ? `R$ ${usdBrl.toFixed(2)}` : 'Indisponível'}
          </span>
        </div>

        <div className="overflow-x-auto">
          <table className="w-full text-left text-xs">
            <thead className="bg-slate-900/80 text-slate-400 border-b border-slate-800">
              <tr>
                <th className="py-3 px-4 font-semibold">Janela de Tempo</th>
                <th className="py-3 px-3 font-semibold text-right">Lucro em Reais (BRL)</th>
                <th className="py-3 px-3 font-semibold text-right">Lucro em Dólares (USD)</th>
                <th className="py-3 px-3 font-semibold text-right">Rentabilidade (%)</th>
                <th className="py-3 px-3 font-semibold text-right">Realizado ($)</th>
                <th className="py-3 px-3 font-semibold text-right">Não-Realizado ($)</th>
                <th className="py-3 px-3 font-semibold text-right">Taxas Pagas ($)</th>
                <th className="py-3 px-3 font-semibold text-right">Qtd Trades</th>
                <th className="py-3 px-4 font-semibold text-right">Taxa de Acerto</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-800/60 font-mono">
              {periods.map((p) => {
                const isPositive = p.profitUsd !== null && p.profitUsd >= 0;
                return (
                  <tr key={p.periodName} className="hover:bg-slate-800/30 text-slate-300 transition-colors">
                    <td className="py-3 px-4 font-sans font-medium text-slate-200 flex items-center gap-2">
                      <CheckCircle2 className="w-3.5 h-3.5 text-emerald-400" />
                      <span>{p.periodName}</span>
                    </td>
                    <td
                      className={`py-3 px-3 text-right font-bold ${isPositive ? 'text-emerald-400' : 'text-rose-400'}`}
                    >
                      {p.profitBrl === null
                        ? 'Indisponível'
                        : `${isPositive ? '+R$ ' : '-R$ '}${Math.abs(p.profitBrl).toLocaleString('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`}
                    </td>
                    <td
                      className={`py-3 px-3 text-right font-bold ${isPositive ? 'text-emerald-400' : 'text-rose-400'}`}
                    >
                      {p.profitUsd === null
                        ? 'Indisponível'
                        : `${isPositive ? '+$' : '-$'}${Math.abs(p.profitUsd).toFixed(2)}`}
                    </td>
                    <td
                      className={`py-3 px-3 text-right font-bold ${isPositive ? 'text-emerald-400' : 'text-rose-400'}`}
                    >
                      {p.profitPercentage === null
                        ? 'Indisponível'
                        : isPositive
                          ? `+${p.profitPercentage.toFixed(2)}%`
                          : `${p.profitPercentage.toFixed(2)}%`}
                    </td>
                    <td className="py-3 px-3 text-right text-slate-200">
                      {p.realizedProfitUsd >= 0
                        ? `+$${p.realizedProfitUsd.toFixed(2)}`
                        : `-$${Math.abs(p.realizedProfitUsd).toFixed(2)}`}
                    </td>
                    <td className="py-3 px-3 text-right text-cyan-400">
                      {p.unrealizedProfitUsd === null
                        ? 'Indisponível'
                        : p.unrealizedProfitUsd >= 0
                          ? `+$${p.unrealizedProfitUsd.toFixed(2)}`
                          : `-$${Math.abs(p.unrealizedProfitUsd).toFixed(2)}`}
                    </td>
                    <td className="py-3 px-3 text-right text-rose-400">-${p.feesPaidUsd.toFixed(2)}</td>
                    <td className="py-3 px-3 text-right text-slate-300">{p.tradesCount}</td>
                    <td className="py-3 px-4 text-right text-purple-400 font-bold">
                      {p.winRatePercentage.toFixed(1)}%
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
};
