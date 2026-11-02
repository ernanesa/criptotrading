import { useEffect, useState } from 'react';

interface QueryEvidence {
  data?: unknown;
  isPending: boolean;
  isError: boolean;
  isFetching: boolean;
  dataUpdatedAt: number;
}

export function QueryStatus({
  label,
  query,
  observedAt,
}: {
  label: string;
  query: QueryEvidence;
  observedAt?: string;
}) {
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 5000);
    return () => clearInterval(timer);
  }, []);
  const hasData = query.data !== undefined;
  const age = Math.max(0, Math.floor((now - query.dataUpdatedAt) / 1000));
  const observedTime = observedAt ? Date.parse(observedAt) : Number.NaN;
  const observedAge = Math.max(0, Math.floor((now - observedTime) / 1000));
  return (
    <div className="text-xs text-slate-400" role="status">
      {!hasData && query.isPending && <span>{label}: carregando...</span>}
      {query.isError && (
        <span className="text-amber-300">
          {label}: {hasData ? 'falha ao atualizar; dados em cache desatualizados.' : 'consulta indisponível.'}{' '}
        </span>
      )}
      {hasData && query.dataUpdatedAt > 0 && (
        <span>
          {label}: Última atualização da consulta {new Date(query.dataUpdatedAt).toISOString()} · idade {age}s.
          {age > 30 && ' Dados antigos; não representam evidência atual.'}
        </span>
      )}
      {hasData && Number.isFinite(observedTime) && (
        <span className="block">
          Snapshot de origem: {new Date(observedTime).toISOString()} · idade {observedAge}s.
          {observedAge > 30 && ' Snapshot antigo; recebê-lo novamente não o torna atual.'}
        </span>
      )}
      {hasData && query.isFetching && <span> Atualizando...</span>}
    </div>
  );
}
