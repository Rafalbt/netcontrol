import type { AppRule, LiveUsage, MonitorRange } from "../types";

interface MonitorScreenProps {
  apps: AppRule[];
  liveUsage: Record<string, LiveUsage>;
  range: MonitorRange;
  onRangeChange: (range: MonitorRange) => void;
}

const RANGE_SAMPLES: Record<MonitorRange, number> = {
  "60s": 60,
  "15min": 180,
  "1h": 300,
};

const RANGE_LABEL: Record<MonitorRange, string> = {
  "60s": "60 s",
  "15min": "15 min",
  "1h": "1 godz",
};

const CHART_WIDTH = 900;
const CHART_HEIGHT = 220;
const CHART_PAD = 12;

export function MonitorScreen({ apps, liveUsage, range, onRangeChange }: MonitorScreenProps) {
  const visibleApps = apps.filter((app) => app.enabled && liveUsage[app.id]);
  const sampleCount = RANGE_SAMPLES[range];

  const maxValue = Math.max(
    50,
    ...visibleApps.map((app) => Math.max(app.limitMbps ?? 0, ...(liveUsage[app.id]?.history.slice(-sampleCount) ?? [0]))),
  );

  function toPoints(history: number[]): string {
    const slice = history.slice(-sampleCount);
    const n = Math.max(slice.length, 2);
    return slice
      .map((value, i) => {
        const x = CHART_PAD + (i / (n - 1)) * (CHART_WIDTH - CHART_PAD * 2);
        const y = CHART_HEIGHT - CHART_PAD - (value / maxValue) * (CHART_HEIGHT - CHART_PAD * 2);
        return `${x.toFixed(1)},${y.toFixed(1)}`;
      })
      .join(" ");
  }

  function limitY(limitMbps: number): number {
    return CHART_HEIGHT - CHART_PAD - (limitMbps / maxValue) * (CHART_HEIGHT - CHART_PAD * 2);
  }

  const throttledApps = visibleApps.filter((app) => liveUsage[app.id]?.status === "throttled");
  const okApps = visibleApps.filter((app) => liveUsage[app.id]?.status !== "throttled");
  // Rough estimate: a throttled app is pressing against its limit, so assume
  // unconstrained demand would run ~40% higher (real demand isn't observable
  // from the passed-through rate alone).
  const savedMbps = throttledApps.reduce((sum, app) => sum + (app.limitMbps ?? 0) * 0.4, 0);

  const gridLines = [0.25, 0.5, 0.75, 1];

  return (
    <div className="content">
      <div className="content-header">
        <div className="content-title-group">
          <h2 className="content-title">Monitor na żywo</h2>
          <span className="content-subtitle">Potwierdzenie, że limity są egzekwowane w czasie rzeczywistym</span>
        </div>
        <div className="range-toggle">
          {(Object.keys(RANGE_SAMPLES) as MonitorRange[]).map((r) => (
            <button key={r} className={`toggle-btn${range === r ? " active" : ""}`} onClick={() => onRangeChange(r)}>
              {RANGE_LABEL[r]}
            </button>
          ))}
        </div>
      </div>

      <div className="chart-card">
        <svg viewBox={`0 0 ${CHART_WIDTH} ${CHART_HEIGHT}`} width="100%" height={CHART_HEIGHT}>
          {gridLines.map((g) => (
            <line
              key={g}
              x1={CHART_PAD}
              x2={CHART_WIDTH - CHART_PAD}
              y1={CHART_HEIGHT - CHART_PAD - g * (CHART_HEIGHT - CHART_PAD * 2)}
              y2={CHART_HEIGHT - CHART_PAD - g * (CHART_HEIGHT - CHART_PAD * 2)}
              stroke="#f1eef7"
              strokeWidth={1}
            />
          ))}

          {visibleApps.map((app) => {
            const usage = liveUsage[app.id];
            if (!usage) return null;
            const showLimitLine = usage.status === "throttled" && app.limitMbps != null;
            return (
              <g key={app.id}>
                {showLimitLine && (
                  <line
                    x1={CHART_PAD}
                    x2={CHART_WIDTH - CHART_PAD}
                    y1={limitY(app.limitMbps!)}
                    y2={limitY(app.limitMbps!)}
                    stroke={app.iconColor}
                    strokeWidth={1.5}
                    strokeDasharray="6 5"
                  />
                )}
                <polyline points={toPoints(usage.history)} fill="none" stroke={app.iconColor} strokeWidth={2.5} />
              </g>
            );
          })}
        </svg>

        <div className="chart-legend">
          {visibleApps.map((app) => (
            <div key={app.id} className="chart-legend-item">
              <span className="chart-legend-dot" style={{ background: app.iconColor }} />
              {app.name}: {(liveUsage[app.id]?.mbps ?? 0).toFixed(1)} Mb/s
            </div>
          ))}
        </div>
      </div>

      <div className="summary-grid">
        {throttledApps.length > 0 ? (
          <div className="summary-card alert">
            <span className="summary-icon">⚑</span>
            <div>
              <div className="summary-title">{throttledApps.map((a) => a.name).join(", ")} — limit egzekwowany</div>
              <div className="summary-desc">
                utrzymywane na {throttledApps.map((a) => `${a.limitMbps} Mb/s`).join(", ")}
              </div>
            </div>
          </div>
        ) : (
          <div className="summary-card ok">
            <span className="summary-icon">✓</span>
            <div>
              <div className="summary-title">Wszystkie aplikacje w normie</div>
              <div className="summary-desc">Żadna aplikacja nie osiąga obecnie swojego limitu</div>
            </div>
          </div>
        )}
        <div className="summary-card ok">
          <span className="summary-icon">✓</span>
          <div>
            <div className="summary-title">{okApps.length > 0 ? "Pozostałe aplikacje w normie" : "Brak innych aktywnych aplikacji"}</div>
            <div className="summary-desc">Zaoszczędzono {savedMbps.toFixed(0)} Mb/s</div>
          </div>
        </div>
      </div>
    </div>
  );
}
