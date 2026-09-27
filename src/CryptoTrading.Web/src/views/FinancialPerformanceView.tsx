import { useQuery } from '@tanstack/react-query';
import { Calendar, CheckCircle2, Clock, DollarSign, Layers, Percent, TrendingUp, Wallet } from 'lucide-react';
import type React from 'react';
import { useState } from 'react';
import { apiClient } from '../api/client';
import type { PeriodPerformance } from '../types/api';

export const FinancialPerformanceView: React.FC = () => {
  const [usdBrl, setUsdBrl] = useState<number>(5.65);

  const performanceQuery = useQuery({
    queryKey: ['portfolioPerformance'],
    queryFn: apiClient.getPortfolioPerformance,
    refetchInterval: 5000,
  });

  const balancesQuery = useQuery({
    queryKey: ['portfolioBalances'],
    queryFn: apiClient.getPortfolioBalances,
    refetchInterval: 5000,
  });

  const perf = performanceQuery.data;
  const balances = balancesQuery.data;

  // Real financial metrics from exchange and accounting engine
  const currentNavUsd = perf?.totalNavUsd ?? balances?.totalNavUsd ?? 0;
  const initialCapitalUsd = perf?.initialCapitalUsd ?? currentNavUsd;
  const cashBalanceUsd = perf?.cashBalanceUsd ?? balances?.cashUsdt ?? 0;
  const positionsValueUsd = perf?.positionsValueUsd ?? balances?.positionsUsd ?? 0;

  const totalProfitUsd = perf?.totalProfitUsd ?? 0;
  const totalProfitBrl = totalProfitUsd * usdBrl;
  const totalProfitPct = perf?.totalProfitPct ?? 0;
  const totalFeesPaidUsd = perf?.totalFeesPaidUsd ?? 0;
  const totalTradesCount = perf?.totalTradesCount ?? 0;
  const winRatePercentage = perf?.winRatePercentage ?? 0;
  const profitFactor = perf?.profitFactor ?? 0;

  const currentNavBrl = currentNavUsd * usdBrl;
  const initialCapitalBrl = initialCapitalUsd * usdBrl;

  // Real accounting periods
  const defaultPeriods: PeriodPerformance[] = [
    {
      periodName: 'Hoje (Dia / 24 Horas)',
      profitUsd: 0,
      profitBrl: 0,
      profitPercentage: 0,
      realizedProfitUsd: 0,
      unrealizedProfitUsd: 0,
      feesPaidUsd: 0,
      tradesCount: 0,
      winRatePercentage: 0,
    },
    {
      periodName: 'Semana (Últimos 7 Dias)',
      profitUsd: 0,
      profitBrl: 0,
      profitPercentage: 0,
      realizedProfitUsd: 0,
      unrealizedProfitUsd: 0,
      feesPaidUsd: 0,
      tradesCount: 0,
      winRatePercentage: 0,
    },
    {
      periodName: 'Mês (Últimos 30 Dias)',
      profitUsd: 0,
      profitBrl: 0,
      profitPercentage: 0,
      realizedProfitUsd: 0,
      unrealizedProfitUsd: 0,
      feesPaidUsd: 0,
      tradesCount: 0,
      winRatePercentage: 0,
    },
    {
      periodName: 'Ano (Acumulado / YTD)',
      profitUsd: 0,
      profitBrl: 0,
      profitPercentage: 0,
      realizedProfitUsd: 0,
      unrealizedProfitUsd: 0,
      feesPaidUsd: 0,
      tradesCount: 0,
      winRatePercentage: 0,
    },
  ];

  const periods: PeriodPerformance[] =
    perf?.periods && perf.periods.length > 0
      ? perf.periods.map((p) => ({
          ...p,
          profitBrl: p.profitUsd * usdBrl,
        }))
      : defaultPeriods;

  return (
    <div className="space-y-6" data-testid="financial-performance-view">
      {/* Header */}
      <div className="flex flex-col md:flex-row md:items-center justify-between gap-4 border-b border-slate-800 pb-5">
        <div>
          <div className="flex items-center gap-2">
            <Wallet className="w-5 h-5 text-emerald-400" />
            <h1 className="text-xl font-bold text-slate-100">Patrimônio, Capital Investido & Lucro (P&L)</h1>
          </div>
          <p className="text-xs text-slate-400 mt-1">
            Acompanhamento transparente do total aportado, patrimônio líquido em tempo real (NAV) e lucro no Dia, na
            Semana, no Mês e no Ano sem valores fictícios ou simulados.
          </p>
        </div>

        {/* Simulador de Câmbio PTAX */}
        <div className="flex flex-wrap items-center gap-3">
          <div className="flex items-center gap-2 bg-slate-900/80 px-3 py-1.5 rounded-lg border border-slate-800 text-xs">
            <span className="text-slate-400">Dólar PTAX (R$):</span>
            <input
              type="number"
              step="0.05"
              aria-label="Cotação do Dólar PTAX"
              value={usdBrl}
              onChange={(e) => setUsdBrl(Number(e.target.value) || 1)}
              className="w-20 bg-slate-950 border border-slate-700 rounded px-2 py-0.5 text-right text-slate-100 font-mono focus:outline-none focus:border-emerald-500"
            />
          </div>
        </div>
      </div>

      {/* Painel Principal de Capital e Patrimônio */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        {/* Card 1: Quanto de Dinheiro Investimos */}
        <div className="bg-slate-900/80 border border-slate-800 rounded-xl p-5 relative overflow-hidden shadow-sm">
          <div className="flex items-center justify-between text-xs text-slate-400 mb-2">
            <span className="font-semibold uppercase tracking-wider text-[11px]">Capital Investido (Aporte)</span>
            <DollarSign className="w-4 h-4 text-cyan-400" />
          </div>
          <div className="text-2xl font-bold text-slate-100 font-mono">
            R${' '}
            {initialCapitalBrl.toLocaleString('pt-BR', {
              minimumFractionDigits: 2,
              maximumFractionDigits: 2,
            })}
          </div>
          <div className="text-xs text-cyan-400 font-mono mt-1">
            $ {initialCapitalUsd.toLocaleString('en-US', { minimumFractionDigits: 2 })} USD
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
            R$ {currentNavBrl.toLocaleString('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
          </div>
          <div className="text-xs text-emerald-400/90 font-mono mt-1">
            $ {currentNavUsd.toLocaleString('en-US', { minimumFractionDigits: 2 })} USD
          </div>
          <p className="text-[11px] text-slate-400 mt-2">
            Caixa USDT: ${cashBalanceUsd.toFixed(2)} • Cripto: ${positionsValueUsd.toFixed(2)}
          </p>
        </div>

        {/* Card 3: Lucro Total Acumulado */}
        <div className="bg-slate-900/80 border border-slate-800 rounded-xl p-5 relative overflow-hidden shadow-sm">
          <div className="flex items-center justify-between text-xs text-slate-400 mb-2">
            <span className="font-semibold uppercase tracking-wider text-[11px]">Lucro Total Acumulado</span>
            <span
              className={`px-1.5 py-0.5 rounded text-[10px] font-bold ${
                totalProfitUsd >= 0
                  ? 'bg-emerald-500/20 text-emerald-400 border border-emerald-500/30'
                  : 'bg-rose-500/20 text-rose-400 border border-rose-500/30'
              }`}
            >
              {totalProfitPct >= 0 ? `+${totalProfitPct.toFixed(2)}%` : `${totalProfitPct.toFixed(2)}%`}
            </span>
          </div>
          <div className={`text-2xl font-bold font-mono ${totalProfitBrl >= 0 ? 'text-emerald-400' : 'text-rose-400'}`}>
            {totalProfitBrl >= 0 ? '+R$ ' : '-R$ '}
            {Math.abs(totalProfitBrl).toLocaleString('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
          </div>
          <div className={`text-xs font-mono mt-1 ${totalProfitUsd >= 0 ? 'text-emerald-400/90' : 'text-rose-400/90'}`}>
            {totalProfitUsd >= 0 ? '+$' : '-$'}
            {Math.abs(totalProfitUsd).toLocaleString('en-US', { minimumFractionDigits: 2 })} USD
          </div>
          <p className="text-[11px] text-slate-400 mt-2">Lucro líquido contábil auditado pós taxas de corretagem.</p>
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
            Profit Factor: <strong className="text-purple-300">{profitFactor.toFixed(2)}</strong>
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
            const isPositive = p.profitUsd >= 0;
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
                  {isPositive ? '+R$ ' : '-R$ '}
                  {Math.abs(p.profitBrl).toLocaleString('pt-BR', {
                    minimumFractionDigits: 2,
                    maximumFractionDigits: 2,
                  })}
                </div>

                {/* Lucro em Dólares e Percentual */}
                <div className="flex items-center justify-between mt-1 text-xs font-mono">
                  <span className="text-slate-300">
                    {isPositive ? '+$' : '-$'}
                    {Math.abs(p.profitUsd).toFixed(2)} USD
                  </span>
                  <span
                    className={`font-bold px-1.5 py-0.5 rounded ${
                      isPositive ? 'text-emerald-400 bg-emerald-500/10' : 'text-rose-400 bg-rose-500/10'
                    }`}
                  >
                    {isPositive ? `+${p.profitPercentage.toFixed(2)}%` : `${p.profitPercentage.toFixed(2)}%`}
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
                      {p.unrealizedProfitUsd >= 0
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

      {/* Tabela Analítica de P&L Consolidado */}
      <div className="bg-slate-900/40 border border-slate-800 rounded-xl overflow-hidden">
        <div className="p-4 border-b border-slate-800 flex items-center justify-between">
          <div className="flex items-center gap-2">
            <TrendingUp className="w-4 h-4 text-emerald-400" />
            <h2 className="text-sm font-semibold text-slate-200">Demonstrativo de Resultado de Negociação (DRE)</h2>
          </div>
          <span className="text-xs text-slate-400 font-mono">Binance Spot • PTAX R$ {usdBrl.toFixed(2)}</span>
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
                const isPositive = p.profitUsd >= 0;
                return (
                  <tr key={p.periodName} className="hover:bg-slate-800/30 text-slate-300 transition-colors">
                    <td className="py-3 px-4 font-sans font-medium text-slate-200 flex items-center gap-2">
                      <CheckCircle2 className="w-3.5 h-3.5 text-emerald-400" />
                      <span>{p.periodName}</span>
                    </td>
                    <td
                      className={`py-3 px-3 text-right font-bold ${isPositive ? 'text-emerald-400' : 'text-rose-400'}`}
                    >
                      {isPositive ? '+R$ ' : '-R$ '}
                      {Math.abs(p.profitBrl).toLocaleString('pt-BR', {
                        minimumFractionDigits: 2,
                        maximumFractionDigits: 2,
                      })}
                    </td>
                    <td
                      className={`py-3 px-3 text-right font-bold ${isPositive ? 'text-emerald-400' : 'text-rose-400'}`}
                    >
                      {isPositive ? '+$' : '-$'}
                      {Math.abs(p.profitUsd).toFixed(2)}
                    </td>
                    <td
                      className={`py-3 px-3 text-right font-bold ${isPositive ? 'text-emerald-400' : 'text-rose-400'}`}
                    >
                      {isPositive ? `+${p.profitPercentage.toFixed(2)}%` : `${p.profitPercentage.toFixed(2)}%`}
                    </td>
                    <td className="py-3 px-3 text-right text-slate-200">
                      {p.realizedProfitUsd >= 0
                        ? `+$${p.realizedProfitUsd.toFixed(2)}`
                        : `-$${Math.abs(p.realizedProfitUsd).toFixed(2)}`}
                    </td>
                    <td className="py-3 px-3 text-right text-cyan-400">
                      {p.unrealizedProfitUsd >= 0
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
