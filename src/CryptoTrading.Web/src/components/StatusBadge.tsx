import type React from 'react';
import type { OperationalMode } from '../types/api';

interface StatusBadgeProps {
  status: OperationalMode | 'Healthy' | 'Unhealthy' | 'Degraded' | 'Verified' | 'Tampered' | string;
  size?: 'sm' | 'md' | 'lg';
}

export const StatusBadge: React.FC<StatusBadgeProps> = ({ status, size = 'md' }) => {
  const sizeClasses = {
    sm: 'text-xs px-2 py-0.5',
    md: 'text-xs px-2.5 py-1',
    lg: 'text-sm px-3 py-1.5',
  }[size];

  let colorClasses = 'bg-slate-800 text-slate-300 border-slate-700';

  switch (status) {
    case 'Normal':
    case 'Healthy':
    case 'Verified':
    case 'Active':
      colorClasses = 'bg-emerald-950/80 text-emerald-400 border-emerald-700/60 ring-1 ring-emerald-500/20';
      break;
    case 'SafeMode':
    case 'Degraded':
      colorClasses = 'bg-amber-950/80 text-amber-400 border-amber-700/60 ring-1 ring-amber-500/20';
      break;
    case 'KillSwitch':
    case 'Unhealthy':
    case 'Tampered':
      colorClasses = 'bg-rose-950/80 text-rose-400 border-rose-700/60 ring-1 ring-rose-500/20 animate-pulse';
      break;
    default:
      break;
  }

  return (
    <span
      className={`inline-flex items-center font-medium rounded-full border tracking-wide uppercase font-mono ${sizeClasses} ${colorClasses}`}
      data-testid={`status-badge-${status.toLowerCase()}`}
    >
      <span className="w-1.5 h-1.5 rounded-full mr-1.5 bg-current" />
      {status}
    </span>
  );
};
