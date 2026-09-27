import { useQuery } from '@tanstack/react-query';
import {
  Activity,
  CheckCircle2,
  ChevronDown,
  ChevronUp,
  Clock,
  Database,
  Layers,
  RefreshCw,
  ShieldCheck,
  TrendingUp,
  Wallet,
} from 'lucide-react';
import type React from 'react';
import { useState } from 'react';
import { apiClient, formatIsoDate } from '../api/client';

export const RealtimeTradingView: React.FC = () => {
  const [showAllBalances, setShowAllBalances] = useState(false);
  // 1. Live Market Ticker from Binance Spot
  const tickerQuery = useQuery({
    queryKey: ['marketTicker', 'BTCUSDT'],
    queryFn: () => apiClient.getMarketTicker('BTCUSDT'),
    refetchInterval: 3000,
  });

  // 2. Real Portfolio Balances from Binance Spot Testnet
  const balancesQuery = useQuery({
    queryKey: ['portfolioBalances'],
    queryFn: apiClient.getPortfolioBalances,
    refetchInterval: 5000,
  });

  // 3. Real Open Orders from OMS / Exchange
  const openOrdersQuery = useQuery({
    queryKey: ['openOrders', 'BTCUSDT'],
    queryFn: () => apiClient.getOpenOrders('BTCUSDT'),
    refetchInterval: 5000,
  });

  // 4. Real Trade History from OMS / Exchange
  const tradesQuery = useQuery({
    queryKey: ['trades', 'BTCUSDT'],
    queryFn: () => apiClient.getTrades('BTCUSDT'),
    refetchInterval: 5000,
  });

  // 5. Autonomous Market Regime (US-074 / US-171)
  const regimeQuery = useQuery({
    queryKey: ['marketRegime', 'BTCUSDT'],
    queryFn: () => apiClient.getCurrentMarketRegime('BTCUSDT'),
    refetchInterval: 5000,
  });

  const ticker = tickerQuery.data;
  const balances = balancesQuery.data;
  const openOrders = openOrdersQuery.data || [];
  const trades = tradesQuery.data || [];

  const currentPrice = ticker?.markPrice ?? 0;
  const spread = ticker?.spread ?? 0;
  const spreadBps = ticker?.spreadBps ?? 0;
  const bidPrice = ticker?.bidPrice ?? 0;
  const bidQty = ticker?.bidQuantity ?? 0;
  const askPrice = ticker?.askPrice ?? 0;
  const askQty = ticker?.askQuantity ?? 0;

  // Separate assets with real market value (or BTC/USDT/BNB) from zero-value testnet tokens
  const nonZeroBalances = (balances?.balances || []).filter((b) => b.total > 0);
  const primaryBalances = nonZeroBalances
    .filter((b) => b.valueUsd > 0 || ['BTC', 'USDT', 'BNB'].includes(b.asset.toUpperCase()))
    .sort((a, b) => b.valueUsd - a.valueUsd);
  const zeroValueBalances = nonZeroBalances.filter(
    (b) => b.valueUsd <= 0 && !['BTC', 'USDT', 'BNB'].includes(b.asset.toUpperCase()),
  );
  const displayedBalances = showAllBalances ? [...primaryBalances, ...zeroValueBalances] : primaryBalances;

  return (
    <div className="space-y-6" data-testid="realtime-trading-view">
      {/* Header Bar */}
      <div className="flex flex-col md:flex-row md:items-center justify-between bg-slate-900 border border-slate-800 rounded-lg p-5 gap-4">
        <div className="flex items-center gap-4">
          <div className="w-12 h-12 rounded-lg bg-cyan-500/10 border border-cyan-500/30 flex items-center justify-center text-cyan-400">
            <Activity className="w-6 h-6 animate-pulse" />
          </div>
          <div>
            <div className="flex items-center gap-2">
              <h2 className="text-xl font-bold tracking-tight text-slate-100">BTC / USDT</h2>
              <span className="px-2 py-0.5 text-xs font-semibold rounded bg-emerald-500/20 text-emerald-400 border border-emerald-500/30">
                SPOT TESTNET
              </span>
              {regimeQuery.data?.regime && regimeQuery.data.regime !== 'Unknown' && (
                <span
                  className={`px-2 py-0.5 text-xs font-semibold rounded border ${
                    regimeQuery.data.prohibitsNewExposure
                      ? 'bg-rose-500/20 text-rose-400 border-rose-500/30'
                      : 'bg-indigo-500/20 text-indigo-300 border-indigo-500/30'
                  }`}
                >
                  REGIME: {String(regimeQuery.data.regime || '').toUpperCase()} (
                  {Math.round((regimeQuery.data.confidence ?? 0) * 100)}%)
                </span>
              )}
              {tickerQuery.isFetching && <RefreshCw className="w-3.5 h-3.5 text-cyan-400 animate-spin" />}
            </div>
            <p className="text-xs text-slate-400 mt-0.5">
              Dados de mercado e custódia 100% reais conectados à Binance Spot Testnet API
            </p>
          </div>
        </div>

        {/* Real Ticker Badges */}
        <div className="flex flex-wrap items-center gap-6 text-sm">
          <div>
            <div className="text-xs text-slate-400">Mark Price Real</div>
            <div className="text-2xl font-mono font-bold text-slate-100">
              {currentPrice > 0
                ? `$${currentPrice.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
                : 'Aguardando...'}
            </div>
          </div>
          <div>
            <div className="text-xs text-slate-400">Melhor Compra (Bid)</div>
            <div className="font-mono text-emerald-400 font-semibold">
              ${bidPrice.toLocaleString('en-US', { minimumFractionDigits: 2 })}
              <span className="text-[10px] text-slate-400 ml-1">({bidQty.toFixed(4)} BTC)</span>
            </div>
          </div>
          <div>
            <div className="text-xs text-slate-400">Melhor Venda (Ask)</div>
            <div className="font-mono text-rose-400 font-semibold">
              ${askPrice.toLocaleString('en-US', { minimumFractionDigits: 2 })}
              <span className="text-[10px] text-slate-400 ml-1">({askQty.toFixed(4)} BTC)</span>
            </div>
          </div>
          <div>
            <div className="text-xs text-slate-400">Spread Real</div>
            <div className="font-mono text-cyan-400 font-bold">
              ${spread.toFixed(2)} ({spreadBps.toFixed(2)} bps)
            </div>
          </div>
        </div>
      </div>

      {/* Grid: Positions and Market Depth */}
      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Main Section: Balances, Open Orders and Trades */}
        <div className="lg:col-span-2 space-y-6">
          {/* Custody & Balances Section */}
          <div className="bg-slate-900 border border-slate-800 rounded-lg p-5">
            <div className="flex flex-col sm:flex-row sm:items-center justify-between mb-4 gap-2">
              <div className="flex items-center gap-2">
                <Layers className="w-4 h-4 text-cyan-400" />
                <h3 className="text-base font-semibold text-slate-200">Posições Abertas & Custódia de Ativos</h3>
                <span className="px-2 py-0.5 rounded text-[11px] font-mono bg-cyan-500/10 text-cyan-400 border border-cyan-500/20">
                  {primaryBalances.length} Ativos Principais
                </span>
              </div>
              <span className="text-xs text-slate-400 font-mono">
                NAV Total: ${balances?.totalNavUsd?.toLocaleString('en-US', { minimumFractionDigits: 2 }) ?? '...'} USD
              </span>
            </div>

            <div className="overflow-x-auto">
              <table className="w-full text-left text-xs">
                <thead className="bg-slate-950/60 text-slate-400 uppercase tracking-wider border-b border-slate-800">
                  <tr>
                    <th className="py-2.5 px-3">Ativo</th>
                    <th className="py-2.5 px-3">Saldo Disponível</th>
                    <th className="py-2.5 px-3">Bloqueado em Ordens</th>
                    <th className="py-2.5 px-3">Saldo Total</th>
                    <th className="py-2.5 px-3 text-right">Valor Estimado (USD)</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-800/60 font-mono">
                  {displayedBalances.length > 0 ? (
                    displayedBalances.map((b) => (
                      <tr key={b.asset} className="hover:bg-slate-800/30 transition-colors">
                        <td className="py-3 px-3 font-semibold text-slate-200 flex items-center gap-1.5">
                          <span
                            className={`px-1.5 py-0.5 rounded text-[10px] font-bold ${
                              b.valueUsd > 0
                                ? 'bg-cyan-500/10 text-cyan-400 border border-cyan-500/20'
                                : 'bg-slate-800 text-slate-400 border border-slate-700'
                            }`}
                          >
                            {b.asset}
                          </span>
                        </td>
                        <td className="py-3 px-3 text-slate-300">
                          {b.free.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 8 })}
                        </td>
                        <td className="py-3 px-3 text-slate-400">
                          {b.locked.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 8 })}
                        </td>
                        <td className="py-3 px-3 text-slate-200 font-medium">
                          {b.total.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 8 })}
                        </td>
                        <td
                          className={`py-3 px-3 text-right font-bold ${
                            b.valueUsd > 0 ? 'text-slate-100' : 'text-slate-500'
                          }`}
                        >
                          ${b.valueUsd.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
                        </td>
                      </tr>
                    ))
                  ) : (
                    <tr>
                      <td colSpan={5} className="py-6 text-center text-slate-500">
                        Nenhum saldo encontrado na conta Testnet.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            {/* Collapse / Expand Toggle for Zero-Value Testnet Tokens */}
            {zeroValueBalances.length > 0 && (
              <div className="mt-4 pt-3 border-t border-slate-800 flex flex-col sm:flex-row items-center justify-between gap-3 text-xs">
                <span className="text-slate-400 text-[11px]">
                  {showAllBalances
                    ? `Exibindo todos os ${displayedBalances.length} ativos (incluindo ${zeroValueBalances.length} tokens de teste com cotação $0.00)`
                    : `${zeroValueBalances.length} tokens airdrop da Testnet (sem cotação de mercado) foram ocultados.`}
                </span>
                <button
                  type="button"
                  onClick={() => setShowAllBalances(!showAllBalances)}
                  className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg bg-slate-800 hover:bg-slate-700 text-slate-300 hover:text-white text-xs font-medium transition-colors cursor-pointer shrink-0"
                >
                  {showAllBalances ? (
                    <>
                      <ChevronUp className="w-3.5 h-3.5 text-cyan-400" />
                      <span>Colapsar e Ocultar Tokens Zerados</span>
                    </>
                  ) : (
                    <>
                      <ChevronDown className="w-3.5 h-3.5 text-cyan-400" />
                      <span>Expandir Outros {zeroValueBalances.length} Tokens Testnet</span>
                    </>
                  )}
                </button>
              </div>
            )}
          </div>

          {/* Real Open Orders Section */}
          <div className="bg-slate-900 border border-slate-800 rounded-lg p-5">
            <div className="flex items-center justify-between mb-4">
              <h3 className="text-base font-semibold text-slate-200 flex items-center gap-2">
                <Clock className="w-4 h-4 text-cyan-400" />
                Ordens Abertas no Livro (OMS)
              </h3>
              <span className="text-xs text-slate-400 font-mono">
                {openOrders.length} {openOrders.length === 1 ? 'Ordem' : 'Ordens'} no Livro
              </span>
            </div>

            {openOrders.length > 0 ? (
              <div className="overflow-x-auto">
                <table className="w-full text-left text-xs">
                  <thead className="bg-slate-950/60 text-slate-400 uppercase tracking-wider border-b border-slate-800">
                    <tr>
                      <th className="py-2.5 px-3">ClientOrderId</th>
                      <th className="py-2.5 px-3">Lado</th>
                      <th className="py-2.5 px-3">Preço</th>
                      <th className="py-2.5 px-3">Preenchido / Total</th>
                      <th className="py-2.5 px-3">Status</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-800/60 font-mono">
                    {openOrders.map((ord) => (
                      <tr key={ord.clientOrderId} className="hover:bg-slate-800/30 transition-colors">
                        <td className="py-3 px-3 font-mono text-cyan-400 font-medium">{ord.clientOrderId}</td>
                        <td className="py-3 px-3">
                          <span
                            className={`px-1.5 py-0.5 rounded text-[10px] font-bold ${
                              ord.side === 'BUY'
                                ? 'bg-emerald-500/20 text-emerald-400 border border-emerald-500/30'
                                : 'bg-rose-500/20 text-rose-400 border border-rose-500/30'
                            }`}
                          >
                            {ord.side}
                          </span>
                        </td>
                        <td className="py-3 px-3 text-slate-300">${ord.price.toFixed(2)}</td>
                        <td className="py-3 px-3 text-slate-300">
                          {ord.executedQty} / {ord.origQty} BTC
                        </td>
                        <td className="py-3 px-3">
                          <span className="px-2 py-0.5 rounded text-[10px] font-semibold bg-slate-800 text-slate-300 border border-slate-700">
                            {ord.status}
                          </span>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : (
              <div className="py-8 text-center text-xs text-slate-400 border border-dashed border-slate-800 rounded-lg space-y-1">
                <CheckCircle2 className="w-6 h-6 text-emerald-400 mx-auto mb-1 opacity-70" />
                <div className="font-semibold text-slate-300">Nenhuma ordem pendente no livro no momento</div>
                <p className="text-[11px] text-slate-500 max-w-md mx-auto">
                  O motor de risco e o OMS estão com todas as regras de segurança ativas. Ordens submetidas pelo ciclo
                  autônomo aparecerão aqui em tempo real.
                </p>
              </div>
            )}
          </div>

          {/* Real Trades History Section */}
          <div className="bg-slate-900 border border-slate-800 rounded-lg p-5">
            <div className="flex items-center justify-between mb-4">
              <h3 className="text-base font-semibold text-slate-200 flex items-center gap-2">
                <Wallet className="w-4 h-4 text-emerald-400" />
                Histórico de Execuções Reais (Trades)
              </h3>
              <span className="text-xs text-slate-400 font-mono">
                {trades.length} {trades.length === 1 ? 'Execução' : 'Execuções'}
              </span>
            </div>

            {trades.length > 0 ? (
              <div className="overflow-x-auto">
                <table className="w-full text-left text-xs">
                  <thead className="bg-slate-950/60 text-slate-400 uppercase tracking-wider border-b border-slate-800">
                    <tr>
                      <th className="py-2.5 px-3">ID</th>
                      <th className="py-2.5 px-3">Lado</th>
                      <th className="py-2.5 px-3">Preço</th>
                      <th className="py-2.5 px-3">Quantidade</th>
                      <th className="py-2.5 px-3">Total (USDT)</th>
                      <th className="py-2.5 px-3">Taxa Paga</th>
                      <th className="py-2.5 px-3">Horário UTC</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-800/60 font-mono">
                    {trades.map((tr) => (
                      <tr key={tr.id} className="hover:bg-slate-800/30 transition-colors">
                        <td className="py-3 px-3 text-cyan-400 font-medium">#{tr.id}</td>
                        <td className="py-3 px-3">
                          <span
                            className={`px-1.5 py-0.5 rounded text-[10px] font-bold ${
                              tr.isBuyer
                                ? 'bg-emerald-500/20 text-emerald-400 border border-emerald-500/30'
                                : 'bg-rose-500/20 text-rose-400 border border-rose-500/30'
                            }`}
                          >
                            {tr.isBuyer ? 'BUY' : 'SELL'}
                          </span>
                        </td>
                        <td className="py-3 px-3 text-slate-300">${(tr.price ?? 0).toFixed(2)}</td>
                        <td className="py-3 px-3 text-slate-300">{tr.qty} BTC</td>
                        <td className="py-3 px-3 text-slate-300">${(tr.quoteQty ?? 0).toFixed(2)}</td>
                        <td className="py-3 px-3 text-amber-400">
                          {tr.commission} {tr.commissionAsset}
                        </td>
                        <td className="py-3 px-3 text-slate-400">{formatIsoDate(tr.timeUtc)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : (
              <div className="py-8 text-center text-xs text-slate-400 border border-dashed border-slate-800 rounded-lg space-y-1">
                <Database className="w-6 h-6 text-cyan-400 mx-auto mb-1 opacity-70" />
                <div className="font-semibold text-slate-300">Nenhum trade registrado ainda nesta conta</div>
                <p className="text-[11px] text-slate-500 max-w-md mx-auto">
                  A conta Testnet está configurada com saldos reais (USDT $10.000,00 e BTC 1.0). Nenhuma execução
                  fictícia é inventada pelo sistema.
                </p>
              </div>
            )}
          </div>
        </div>

        {/* Side Panel: Order Book Top & Execution Safety */}
        <div className="space-y-6">
          <div className="bg-slate-900 border border-slate-800 rounded-lg p-5">
            <h3 className="text-base font-semibold text-slate-200 mb-4 flex items-center gap-2">
              <TrendingUp className="w-4 h-4 text-cyan-400" />
              Livro de Ofertas L1 (Topo Real)
            </h3>

            {/* Asks (Red) */}
            <div className="space-y-1 font-mono text-xs mb-2">
              <div className="flex justify-between text-rose-400/90 py-1.5 px-3 bg-rose-500/10 rounded border border-rose-500/20">
                <span className="font-bold">Venda (Ask): ${askPrice.toFixed(2)}</span>
                <span className="text-slate-300">{askQty.toFixed(4)} BTC</span>
              </div>
            </div>

            {/* Spread Bar */}
            <div className="py-2 px-3 bg-slate-950 border-y border-slate-800 flex justify-between items-center text-xs font-mono my-3">
              <span className="text-slate-400 text-[10px]">SPREAD PASSIVO</span>
              <span className="text-cyan-400 font-bold">
                ${spread.toFixed(2)} ({spreadBps.toFixed(2)} bps)
              </span>
            </div>

            {/* Bids (Green) */}
            <div className="space-y-1 font-mono text-xs">
              <div className="flex justify-between text-emerald-400/90 py-1.5 px-3 bg-emerald-500/10 rounded border border-emerald-500/20">
                <span className="font-bold">Compra (Bid): ${bidPrice.toFixed(2)}</span>
                <span className="text-slate-300">{bidQty.toFixed(4)} BTC</span>
              </div>
            </div>

            <div className="mt-4 pt-3 border-t border-slate-800 text-[11px] text-slate-400 flex items-center justify-between">
              <span>Obs: {ticker?.observedAtUtc ? new Date(ticker.observedAtUtc).toLocaleTimeString() : '...'}</span>
              <span className="text-emerald-400 font-mono">Conexão WebSocket/REST ativa</span>
            </div>
          </div>

          {/* Execution Safety Guard */}
          <div className="bg-slate-900 border border-slate-800 rounded-lg p-5">
            <h3 className="text-base font-semibold text-slate-200 mb-3 flex items-center gap-2">
              <ShieldCheck className="w-4 h-4 text-emerald-400" />
              Garantia de Execução Segura
            </h3>
            <ul className="space-y-2 text-xs text-slate-400">
              <li className="flex items-start gap-2">
                <span className="w-1.5 h-1.5 rounded-full bg-emerald-400 mt-1.5 shrink-0" />
                <span>Modo de execução com travas rígidas de spread e notional.</span>
              </li>
              <li className="flex items-start gap-2">
                <span className="w-1.5 h-1.5 rounded-full bg-emerald-400 mt-1.5 shrink-0" />
                <span>Zero risco de liquidação em conta Spot sem margem.</span>
              </li>
              <li className="flex items-start gap-2">
                <span className="w-1.5 h-1.5 rounded-full bg-cyan-400 mt-1.5 shrink-0" />
                <span>Reserva determinística de taxas (25 bps) aplicada antes de cada BUY.</span>
              </li>
              <li className="flex items-start gap-2">
                <span className="w-1.5 h-1.5 rounded-full bg-amber-400 mt-1.5 shrink-0" />
                <span>Retiradas desativadas (canWithdraw == false) verificado por contrato.</span>
              </li>
            </ul>
          </div>
        </div>
      </div>
    </div>
  );
};
