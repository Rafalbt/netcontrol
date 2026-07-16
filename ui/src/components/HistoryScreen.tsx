import type { HistoryData } from "../backend";
import type { AppRule, HistoryPeriod } from "../types";

interface HistoryScreenProps {
  apps: AppRule[];
  period: HistoryPeriod;
  realHistory: HistoryData | null;
  onPeriodChange: (period: HistoryPeriod) => void;
}

const DAY_LABELS = ["Pn", "Wt", "Śr", "Cz", "Pt", "So", "Nd"];

const PERIOD_LABEL: Record<HistoryPeriod, string> = {
  day: "Dzień",
  week: "Tydzień",
  month: "Miesiąc",
};

const PERIOD_MULTIPLIER: Record<HistoryPeriod, number> = {
  day: 1 / 7,
  week: 1,
  month: 4.3,
};

// Deterministic pseudo-random in [0, 1) so mock charts stay stable across
// re-renders instead of jittering on every tick.
function seeded(seed: string): number {
  let hash = 0;
  for (let i = 0; i < seed.length; i++) {
    hash = (hash * 31 + seed.charCodeAt(i)) >>> 0;
  }
  return (hash % 1000) / 1000;
}

export function HistoryScreen({ apps, period, realHistory, onPeriodChange }: HistoryScreenProps) {
  const chartApps = realHistory ? apps : apps.filter((app) => app.limitMbps != null);

  // With a service connection the chart shows real per-bucket usage from
  // SQLite; otherwise it falls back to the stable demo data.
  const dayData = realHistory
    ? realHistory.labels.map((label, bucketIndex) => {
        const segments = chartApps
          .map((app) => ({ app, gb: realHistory.seriesGb[app.id]?.[bucketIndex] ?? 0 }))
          .filter((s) => s.gb > 0);
        const total = segments.reduce((sum, s) => sum + s.gb, 0);
        return { label, segments, total };
      })
    : DAY_LABELS.map((label, dayIndex) => {
        const segments = chartApps.map((app) => {
          const gb = 4 + seeded(`${app.id}-${dayIndex}`) * 16;
          return { app, gb };
        });
        const total = segments.reduce((sum, s) => sum + s.gb, 0);
        return { label, segments, total };
      });

  const maxTotal = Math.max(realHistory ? 0.001 : 1, ...dayData.map((d) => d.total));
  const multiplier = realHistory ? 1 : PERIOD_MULTIPLIER[period];

  const totalGb = dayData.reduce((sum, d) => sum + d.total, 0) * multiplier;
  // Real data: "saved" = bytes held back by the pacer + dropped outside
  // schedule windows (the plan's demand-vs-passed estimate).
  const savedGb = realHistory ? realHistory.savedGb : totalGb * 0.28;
  const throttleEvents = realHistory
    ? realHistory.throttleEvents
    : Math.round(dayData.length * chartApps.length * 4.6 * multiplier);

  return (
    <div className="content">
      <div className="content-header">
        <div className="content-title-group">
          <h2 className="content-title">Historia</h2>
          <span className="content-subtitle">Statystyki transferu i egzekwowania limitów</span>
        </div>
        <div className="period-toggle">
          {(Object.keys(PERIOD_LABEL) as HistoryPeriod[]).map((p) => (
            <button key={p} className={`toggle-btn${period === p ? " active" : ""}`} onClick={() => onPeriodChange(p)}>
              {PERIOD_LABEL[p]}
            </button>
          ))}
        </div>
      </div>

      <div className="kpi-grid kpi-grid-3">
        <div className="kpi-card">
          <div className="kpi-label">Pobrano łącznie</div>
          <div className="kpi-value">
            {totalGb.toFixed(1)}
            <span className="kpi-unit"> GB</span>
          </div>
        </div>
        <div className="kpi-card">
          <div className="kpi-label">Zaoszczędzono limitami</div>
          <div className="kpi-value ok">
            {savedGb < 10 ? savedGb.toFixed(2) : savedGb.toFixed(1)}
            <span className="kpi-unit"> GB</span>
          </div>
        </div>
        <div className="kpi-card">
          <div className="kpi-label">Zdarzeń ograniczenia</div>
          <div className="kpi-value accent">{throttleEvents}</div>
        </div>
      </div>

      <div className="chart-card">
        <div className="bar-chart">
          {dayData.map((day) => (
            <div key={day.label} className="bar-col">
              <div className="bar-stack" style={{ height: `${(day.total / maxTotal) * 100}%` }}>
                {day.segments.map(({ app, gb }) => (
                  <div
                    key={app.id}
                    style={{ height: `${(gb / day.total) * 100}%`, background: app.iconColor }}
                  />
                ))}
              </div>
              <span className="bar-day-label">{day.label}</span>
            </div>
          ))}
        </div>
        <div className="chart-legend">
          {chartApps.map((app) => (
            <div key={app.id} className="chart-legend-item">
              <span className="chart-legend-dot" style={{ background: app.iconColor }} />
              {app.name}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
