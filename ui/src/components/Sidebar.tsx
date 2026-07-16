import type { NavKey } from "../types";

const NAV_ITEMS: { key: NavKey; label: string }[] = [
  { key: "apps", label: "Aplikacje" },
  { key: "monitor", label: "Monitor na żywo" },
  { key: "history", label: "Historia" },
  { key: "settings", label: "Ustawienia" },
];

interface SidebarProps {
  activeNav: NavKey;
  onNavigate: (nav: NavKey) => void;
  totalNowMbps: number;
  linkCapacityMbps: number;
  connected: boolean;
}

export function Sidebar({ activeNav, onNavigate, totalNowMbps, linkCapacityMbps, connected }: SidebarProps) {
  const usagePercent = Math.min(100, (totalNowMbps / linkCapacityMbps) * 100);

  return (
    <nav className="sidebar">
      <div className="sidebar-logo">
        <span className="sidebar-logo-mark">P</span>
        <span className="sidebar-logo-text">Przepustnica</span>
      </div>

      <div className="sidebar-nav">
        {NAV_ITEMS.map((item) => (
          <button
            key={item.key}
            className={`sidebar-nav-item${activeNav === item.key ? " active" : ""}`}
            onClick={() => onNavigate(item.key)}
          >
            <span className="sidebar-nav-dot" />
            {item.label}
          </button>
        ))}
      </div>

      {activeNav === "monitor" && (
        <div className="sidebar-monitor-indicator">
          <span className="sidebar-monitor-dot" />
          Monitorowanie aktywne
        </div>
      )}

      <div className="sidebar-capacity" style={{ marginTop: activeNav === "monitor" ? 0 : "auto" }}>
        <div className={`sidebar-service-status${connected ? " connected" : ""}`}>
          <span className="sidebar-service-dot" />
          {connected ? "Usługa połączona" : "Tryb demo — brak usługi"}
        </div>
        <div className="sidebar-capacity-label">Wykorzystanie łącza</div>
        <div className="sidebar-capacity-value">
          {totalNowMbps.toFixed(0)} / {linkCapacityMbps} Mb/s
        </div>
        <div className="sidebar-capacity-track">
          <div className="sidebar-capacity-fill" style={{ width: `${usagePercent}%` }} />
        </div>
      </div>
    </nav>
  );
}
