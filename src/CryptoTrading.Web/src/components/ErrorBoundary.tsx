import { AlertTriangle, RotateCcw } from 'lucide-react';
import { Component, type ReactNode } from 'react';
import { generateCorrelationId } from '../api/client';

interface Props {
  children: ReactNode;
  fallback?: ReactNode;
  resetKey?: string;
}

interface State {
  hasError: boolean;
  correlationId: string;
}

export class ErrorBoundary extends Component<Props, State> {
  public state: State = {
    hasError: false,
    correlationId: generateCorrelationId(),
  };

  public static getDerivedStateFromError(): Partial<State> {
    return {
      hasError: true,
      correlationId: generateCorrelationId(),
    };
  }

  public componentDidCatch() {
    console.error('ui.render_failure', { correlationId: this.state.correlationId, source: 'ErrorBoundary' });
  }

  public componentDidUpdate(previous: Props) {
    if (previous.resetKey !== this.props.resetKey && this.state.hasError) this.handleRetry();
  }

  private handleRetry = () => {
    this.setState({ hasError: false });
  };

  public render() {
    if (this.state.hasError) {
      if (this.props.fallback) {
        return this.props.fallback;
      }

      return (
        <div className="min-h-[400px] flex items-center justify-center p-6" data-testid="error-boundary-card">
          <div className="max-w-lg w-full bg-slate-900 border border-rose-900/60 rounded-xl p-6 shadow-2xl">
            <div className="flex items-center gap-3 text-rose-400 mb-4">
              <AlertTriangle className="w-8 h-8 flex-shrink-0" />
              <div>
                <h2 className="text-lg font-bold text-slate-100">Falha inesperada no console</h2>
                <p className="text-xs text-rose-300/80">Ocorreu um erro durante a renderização.</p>
              </div>
            </div>

            <div className="bg-slate-950 p-3 rounded-lg border border-slate-800 text-xs font-mono mb-4 text-slate-300 break-all">
              <span className="text-slate-500 block mb-1">Correlation ID:</span>
              <span className="text-cyan-400" data-testid="error-correlation-id">
                {this.state.correlationId}
              </span>
            </div>

            <p className="text-xs text-slate-400 mb-6">
              Referência local de diagnóstico. Esta interface não executou nenhuma ação de negociação.
            </p>

            <div className="flex gap-3">
              <button
                type="button"
                onClick={this.handleRetry}
                className="flex-1 flex items-center justify-center gap-2 px-4 py-2.5 bg-slate-800 hover:bg-slate-700 text-slate-200 font-medium rounded-lg text-sm border border-slate-700 transition-colors cursor-pointer"
              >
                <RotateCcw className="w-4 h-4" />
                <span>Tentar Novamente</span>
              </button>
            </div>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}
