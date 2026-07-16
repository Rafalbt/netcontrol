import { useEffect, useState } from "react";
import type { AppRule, LiveUsage } from "../types";

interface AppsScreenProps {
  apps: AppRule[];
  liveUsage: Record<string, LiveUsage>;
  todayGb: number;
  onAddApp: () => void;
  onEditApp: (id: string) => void;
  onToggleApp: (id: string) => void;
  onDeleteApp: (id: string) => void;
}

function formatHours(app: AppRule): string {
  if (app.schedule.mode === "always") return "cała doba";
  return `${app.schedule.from.slice(0, 5)}–${app.schedule.to.slice(0, 5)}`;
}

function statusLabel(status: LiveUsage["status"]): string {
  if (status === "active") return "● Aktywna";
  if (status === "throttled") return "● Ograniczana";
  return "● Uśpiona";
}

export function AppsScreen({ apps, liveUsage, todayGb, onAddApp, onEditApp, onToggleApp, onDeleteApp }: AppsScreenProps) {
  const [openMenuId, setOpenMenuId] = useState<string | null>(null);

  // Close the ⋯ menu on any click outside it (hover-based closing made the
  // menu unreachable in some layouts).
  useEffect(() => {
    if (!openMenuId) return;
    const close = () => setOpenMenuId(null);
    document.addEventListener("click", close);
    return () => document.removeEventListener("click", close);
  }, [openMenuId]);

  const totalNow = apps.reduce((sum, app) => sum + (liveUsage[app.id]?.mbps ?? 0), 0);
  const throttledCount = apps.filter((app) => liveUsage[app.id]?.status === "throttled").length;

  return (
    <div className="content">
      <div className="content-header">
        <div className="content-title-group">
          <h2 className="content-title">Aplikacje</h2>
          <span className="content-subtitle">Zarządzaj limitami prędkości i godzinami działania</span>
        </div>
        <button className="btn-primary" onClick={onAddApp}>
          <span style={{ fontSize: 17, lineHeight: 1 }}>+</span> Dodaj aplikację
        </button>
      </div>

      <div className="kpi-grid">
        <div className="kpi-card">
          <div className="kpi-label">Aplikacje</div>
          <div className="kpi-value">{apps.length}</div>
        </div>
        <div className="kpi-card">
          <div className="kpi-label">Teraz</div>
          <div className="kpi-value">
            {totalNow.toFixed(1)}
            <span className="kpi-unit"> Mb/s</span>
          </div>
        </div>
        <div className="kpi-card">
          <div className="kpi-label">Ograniczane</div>
          <div className="kpi-value accent">{throttledCount}</div>
        </div>
        <div className="kpi-card">
          <div className="kpi-label">Dziś</div>
          <div className="kpi-value">
            {todayGb.toFixed(1)}
            <span className="kpi-unit"> GB</span>
          </div>
        </div>
      </div>

      <div className="apps-table">
        <div className="apps-table-row apps-table-header">
          <span>Aplikacja</span>
          <span>Limit</span>
          <span>Godziny</span>
          <span>Teraz</span>
          <span>Status</span>
          <span />
        </div>
        {apps.map((app) => {
          const usage = liveUsage[app.id];
          const status = usage?.status ?? "sleeping";
          const percent = app.limitMbps ? Math.min(100, ((usage?.mbps ?? 0) / app.limitMbps) * 100) : 0;

          return (
            <div key={app.id} className={`apps-table-row${status === "throttled" ? " throttled" : ""}${status === "sleeping" ? " sleeping" : ""}`}>
              <span className="app-name-cell">
                <span className="app-icon" style={{ background: app.iconColor }}>
                  {app.initials}
                </span>
                <span className="app-name">{app.name}</span>
              </span>
              <span className="app-limit">{app.limitMbps ? `${app.limitMbps} Mb/s` : "bez limitu"}</span>
              <span className="app-hours">{formatHours(app)}</span>
              <span className="app-usage-cell">
                <span className={`app-usage-value${status === "throttled" ? " throttled" : ""}${status === "sleeping" ? " sleeping" : ""}`}>
                  {(usage?.mbps ?? 0).toFixed(1)} Mb/s
                </span>
                <span className="app-usage-track">
                  <span
                    className={`app-usage-fill${status === "throttled" ? " throttled" : ""}`}
                    style={{ width: `${percent}%`, background: app.iconColor }}
                  />
                </span>
              </span>
              <span className={`app-status ${status}`}>{statusLabel(status)}</span>
              <span className="app-row-menu">
                <button
                  style={{ background: "none", border: "none", cursor: "pointer", boxShadow: "none", fontSize: 16, color: "#c3bed6" }}
                  onClick={(e) => {
                    e.stopPropagation();
                    setOpenMenuId(openMenuId === app.id ? null : app.id);
                  }}
                >
                  ⋯
                </button>
                {openMenuId === app.id && (
                  <div className="row-menu-popover" onClick={(e) => e.stopPropagation()}>
                    <button
                      onClick={() => {
                        onEditApp(app.id);
                        setOpenMenuId(null);
                      }}
                    >
                      Edytuj
                    </button>
                    <button
                      onClick={() => {
                        onToggleApp(app.id);
                        setOpenMenuId(null);
                      }}
                    >
                      {app.enabled ? "Wyłącz" : "Włącz"}
                    </button>
                    <button
                      className="danger"
                      onClick={() => {
                        onDeleteApp(app.id);
                        setOpenMenuId(null);
                      }}
                    >
                      Usuń limit
                    </button>
                  </div>
                )}
              </span>
            </div>
          );
        })}
        {apps.length === 0 && (
          <div className="apps-table-row">
            <span style={{ color: "var(--color-text-muted)", gridColumn: "1 / -1" }}>
              Brak dodanych aplikacji — kliknij „+ Dodaj aplikację", żeby ustawić pierwszy limit.
            </span>
          </div>
        )}
      </div>
    </div>
  );
}
