import { Activity, ArrowDownRight, ArrowUpRight, Clock, Layers, ShieldCheck, TrendingUp } from 'lucide-react';
import type React from 'react';
import { useEffect, useState } from 'react';

interface Position {
  symbol: string;
  size: number;
  entryPrice: number;
  markPrice: number;
  unrealizedPnl: number;
  unrealizedPnlPercent: number;
  side: 'LONG' | 'SHORT';
}

interface OpenOrder {
  clientOrderId: string;
  symbol: string;
  side: 'BUY' | 'SELL';
  price: number;
  origQty: number;
  executedQty: number;
  status: 'NEW' | 'PARTIALLY_FILLED';
  createdAtUtc: string;
}

export const RealtimeTradingView: React.FC = () => {
  const [currentPrice, setCurrentPrice] = useState<number>(64250.0);
  const priceChange24h = 2.45;
  const high24h = 65100.0;
  const low24h = 63400.0;
  const volume24h = 1420.5;

  // Simulated live tick update
  useEffect(() => {
    const interval = setInterval(() => {
      const delta = (Math.random() - 0.49) * 15;
      setCurrentPrice((prev) => {
        const next = Math.max(1000, Math.round((prev + delta) * 100) / 100);
        return next;
      });
    }, 1500);
    return () => clearInterval(interval);
  }, []);

  const positions: Position[] = [
    {
      symbol: 'BTCUSDT',
      size: 0.15,
      entryPrice: 63800.0,
      markPrice: currentPrice,
      unrealizedPnl: Math.round((currentPrice - 63800.0) * 0.15 * 100) / 100,
      unrealizedPnlPercent: Math.round(((currentPrice - 63800.0) / 63800.0) * 10000) / 100,
      side: 'LONG',
    },
  ];

  const openOrders: OpenOrder[] = [
    {
      clientOrderId: 'ct-ord-20260921-001',
      symbol: 'BTCUSDT',
      side: 'BUY',
      price: Math.round((currentPrice - 150.0) * 100) / 100,
      origQty: 0.05,
      executedQty: 0.0,
      status: 'NEW',
      createdAtUtc: new Date().toISOString(),
    },
    {
      clientOrderId: 'ct-ord-20260921-002',
      symbol: 'BTCUSDT',
      side: 'SELL',
      price: Math.round((currentPrice + 400.0) * 100) / 100,
      origQty: 0.05,
      executedQty: 0.02,
      status: 'PARTIALLY_FILLED',
      createdAtUtc: new Date(Date.now() - 360000).toISOString(),
    },
  ];

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
            </div>
            <p className="text-xs text-slate-400 mt-0.5">
              Stream de ticks em tempo real com validação determinística de spread
            </p>
          </div>
        </div>

        {/* Ticker Badges */}
        <div className="flex flex-wrap items-center gap-6 text-sm">
          <div>
            <div className="text-xs text-slate-400">Preço Atual</div>
            <div className="text-2xl font-mono font-bold text-slate-100">
              ${currentPrice.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
            </div>
          </div>
          <div>
            <div className="text-xs text-slate-400">Variação 24h</div>
            <div
              className={`font-mono font-semibold flex items-center ${priceChange24h >= 0 ? 'text-emerald-400' : 'text-rose-400'}`}
            >
              {priceChange24h >= 0 ? (
                <ArrowUpRight className="w-4 h-4 mr-0.5" />
              ) : (
                <ArrowDownRight className="w-4 h-4 mr-0.5" />
              )}
              {priceChange24h > 0 ? `+${priceChange24h}%` : `${priceChange24h}%`}
            </div>
          </div>
          <div>
            <div className="text-xs text-slate-400">Máx 24h</div>
            <div className="font-mono text-slate-200">
              ${high24h.toLocaleString('en-US', { minimumFractionDigits: 2 })}
            </div>
          </div>
          <div>
            <div className="text-xs text-slate-400">Mín 24h</div>
            <div className="font-mono text-slate-200">
              ${low24h.toLocaleString('en-US', { minimumFractionDigits: 2 })}
            </div>
          </div>
          <div>
            <div className="text-xs text-slate-400">Volume 24h</div>
            <div className="font-mono text-slate-200">{volume24h.toFixed(1)} BTC</div>
          </div>
        </div>
      </div>

      {/* Grid: Positions and Market Depth */}
      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Positions Section */}
        <div className="lg:col-span-2 space-y-6">
          <div className="bg-slate-900 border border-slate-800 rounded-lg p-5">
            <div className="flex items-center justify-between mb-4">
              <h3 className="text-base font-semibold text-slate-200 flex items-center gap-2">
                <Layers className="w-4 h-4 text-cyan-400" />
                Posições Abertas
              </h3>
              <span className="text-xs text-slate-400 font-mono">1 Ativa</span>
            </div>

            <div className="overflow-x-auto">
              <table className="w-full text-left text-xs">
                <thead className="bg-slate-950/60 text-slate-400 uppercase tracking-wider border-b border-slate-800">
                  <tr>
                    <th className="py-2.5 px-3">Símbolo</th>
                    <th className="py-2.5 px-3">Lado</th>
                    <th className="py-2.5 px-3">Quantidade</th>
                    <th className="py-2.5 px-3">Preço Entrada</th>
                    <th className="py-2.5 px-3">Mark Price</th>
                    <th className="py-2.5 px-3 text-right">PnL Não Realizado</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-800/60 font-mono">
                  {positions.map((pos) => (
                    <tr key={pos.symbol} className="hover:bg-slate-800/30 transition-colors">
                      <td className="py-3 px-3 font-semibold text-slate-200">{pos.symbol}</td>
                      <td className="py-3 px-3">
                        <span className="px-1.5 py-0.5 rounded text-[10px] font-bold bg-emerald-500/20 text-emerald-400 border border-emerald-500/30">
                          {pos.side}
                        </span>
                      </td>
                      <td className="py-3 px-3 text-slate-300">{pos.size} BTC</td>
                      <td className="py-3 px-3 text-slate-300">${pos.entryPrice.toFixed(2)}</td>
                      <td className="py-3 px-3 text-slate-300">${pos.markPrice.toFixed(2)}</td>
                      <td
                        className={`py-3 px-3 text-right font-bold ${pos.unrealizedPnl >= 0 ? 'text-emerald-400' : 'text-rose-400'}`}
                      >
                        {pos.unrealizedPnl >= 0
                          ? `+$${pos.unrealizedPnl.toFixed(2)}`
                          : `-$${Math.abs(pos.unrealizedPnl).toFixed(2)}`}
                        <span className="text-[10px] ml-1 font-normal opacity-80">({pos.unrealizedPnlPercent}%)</span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>

          {/* Open Orders Section */}
          <div className="bg-slate-900 border border-slate-800 rounded-lg p-5">
            <div className="flex items-center justify-between mb-4">
              <h3 className="text-base font-semibold text-slate-200 flex items-center gap-2">
                <Clock className="w-4 h-4 text-cyan-400" />
                Ordens Abertas no Livro (OMS)
              </h3>
              <span className="text-xs text-slate-400 font-mono">{openOrders.length} Pendentes</span>
            </div>

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
                        <span
                          className={`px-2 py-0.5 rounded text-[10px] font-semibold ${
                            ord.status === 'PARTIALLY_FILLED'
                              ? 'bg-amber-500/20 text-amber-300 border border-amber-500/30'
                              : 'bg-slate-800 text-slate-300 border border-slate-700'
                          }`}
                        >
                          {ord.status}
                        </span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        </div>

        {/* Side Panel: Order Book & Execution Quality */}
        <div className="space-y-6">
          <div className="bg-slate-900 border border-slate-800 rounded-lg p-5">
            <h3 className="text-base font-semibold text-slate-200 mb-4 flex items-center gap-2">
              <TrendingUp className="w-4 h-4 text-cyan-400" />
              Livro de Ofertas L2 (Topo)
            </h3>

            {/* Asks (Red) */}
            <div className="space-y-1 font-mono text-xs mb-2">
              <div className="flex justify-between text-rose-400/90 py-0.5 px-2 bg-rose-500/5 rounded">
                <span>${(currentPrice + 4.5).toFixed(2)}</span>
                <span className="text-slate-400">1.450 BTC</span>
              </div>
              <div className="flex justify-between text-rose-400/90 py-0.5 px-2 bg-rose-500/10 rounded">
                <span>${(currentPrice + 2.0).toFixed(2)}</span>
                <span className="text-slate-400">0.820 BTC</span>
              </div>
            </div>

            {/* Spread Bar */}
            <div className="py-2 px-3 bg-slate-950 border-y border-slate-800 flex justify-between items-center text-xs font-mono my-2">
              <span className="text-slate-400 text-[10px]">SPREAD PASSIVO</span>
              <span className="text-cyan-400 font-bold">$2.50 (0.39 bps)</span>
            </div>

            {/* Bids (Green) */}
            <div className="space-y-1 font-mono text-xs">
              <div className="flex justify-between text-emerald-400/90 py-0.5 px-2 bg-emerald-500/10 rounded">
                <span>${(currentPrice - 0.5).toFixed(2)}</span>
                <span className="text-slate-400">0.910 BTC</span>
              </div>
              <div className="flex justify-between text-emerald-400/90 py-0.5 px-2 bg-emerald-500/5 rounded">
                <span>${(currentPrice - 3.0).toFixed(2)}</span>
                <span className="text-slate-400">2.140 BTC</span>
              </div>
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
            </ul>
          </div>
        </div>
      </div>
    </div>
  );
};
