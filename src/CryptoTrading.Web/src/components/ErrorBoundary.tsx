import { Component, type ErrorInfo, type ReactNode } from 'react';
import { AlertTriangle, RefreshCw } from 'lucide-react';
import { generateCorrelationId } from '../api/client';

interface Props {
  children: ReactNode;
  fallback?: ReactNode;
}

interface State {
  hasError: boolean;
  error: Error | null;
  correlationId: string;
}

export class ErrorBoundary extends Component<Props, State> {
  public state: State = {
    hasError: false,
    error: null,
    correlationId: generateCorrelationId(),
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
        <div
          className="min-h-[400px] flex items-center justify-center p-6"
          data-testid="error-boundary-card"
        >
          <div className="max-w-md w-full bg-slate-900 border border-rose-900/60 rounded-xl p-6 shadow-2xl">
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
              Nenhum dado sensível ou chave de API foi exposto. O incidente foi registrado com o Correlation ID acima.
            </p>

            <button
              onClick={this.handleReset}
              className="w-full flex items-center justify-center gap-2 px-4 py-2.5 bg-rose-600 hover:bg-rose-500 text-white font-medium rounded-lg text-sm transition-colors cursor-pointer"
            >
              <RefreshCw className="w-4 h-4" />
              <span>Recarregar página</span>
            </button>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}
