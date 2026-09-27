import { AlertTriangle, ChevronDown, ChevronUp, RefreshCw, RotateCcw } from 'lucide-react';
import { Component, type ErrorInfo, type ReactNode } from 'react';
import { generateCorrelationId } from '../api/client';

interface Props {
  children: ReactNode;
  fallback?: ReactNode;
}

interface State {
  hasError: boolean;
  error: Error | null;
  correlationId: string;
  showDetails: boolean;
}

export class ErrorBoundary extends Component<Props, State> {
  public state: State = {
    hasError: false,
    error: null,
    correlationId: generateCorrelationId(),
    showDetails: false,
  };

  public static getDerivedStateFromError(error: Error): Partial<State> {
    return {
      hasError: true,
      error,
      correlationId: generateCorrelationId(),
    };
  }

  public componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    console.error('ErrorBoundary caught exception:', error, errorInfo);
  }

  private handleRetry = () => {
    this.setState({ hasError: false, error: null, showDetails: false });
  };

  private handleReset = () => {
    this.setState({ hasError: false, error: null });
    window.location.reload();
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

            {this.state.error?.message && (
              <div className="mb-4">
                <button
                  type="button"
                  onClick={() => this.setState((prev) => ({ showDetails: !prev.showDetails }))}
                  className="flex items-center gap-1.5 text-xs text-slate-400 hover:text-slate-200 transition-colors cursor-pointer"
                >
                  {this.state.showDetails ? (
                    <ChevronUp className="w-3.5 h-3.5" />
                  ) : (
                    <ChevronDown className="w-3.5 h-3.5" />
                  )}
                  <span>{this.state.showDetails ? 'Ocultar detalhes técnicos' : 'Exibir detalhes técnicos'}</span>
                </button>

                {this.state.showDetails && (
                  <div className="mt-2 p-3 bg-slate-950 rounded-lg border border-rose-900/40 text-[11px] font-mono text-rose-300 max-h-48 overflow-y-auto break-all">
                    <strong className="block text-rose-400 mb-1">Erro:</strong>
                    {this.state.error.message}
                    {this.state.error.stack && (
                      <div className="mt-2 text-slate-400 text-[10px] whitespace-pre-wrap">
                        {this.state.error.stack}
                      </div>
                    )}
                  </div>
                )}
              </div>
            )}

            <p className="text-xs text-slate-400 mb-6">
              Nenhum dado sensível ou chave de API foi exposto. O incidente foi registrado com o Correlation ID acima.
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
              <button
                type="button"
                onClick={this.handleReset}
                className="flex-1 flex items-center justify-center gap-2 px-4 py-2.5 bg-rose-600 hover:bg-rose-500 text-white font-medium rounded-lg text-sm transition-colors cursor-pointer"
              >
                <RefreshCw className="w-4 h-4" />
                <span>Recarregar página</span>
              </button>
            </div>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}
