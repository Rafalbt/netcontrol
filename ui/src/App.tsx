import { useEffect, useState } from "react";
import "./tokens.css";
import "./App.css";
import { Sidebar } from "./components/Sidebar";
import { AppsScreen } from "./components/AppsScreen";
import { AddAppModal } from "./components/AddAppModal";
import { MonitorScreen } from "./components/MonitorScreen";
import { HistoryScreen } from "./components/HistoryScreen";
import { SettingsScreen } from "./components/SettingsScreen";
import { pickIconColor, initialsFromName } from "./mockData";
import type { DetectedProcess } from "./mockData";
import { isWithinSchedule } from "./schedule";
import { sendCommand, subscribeBackend } from "./backend";
import type { BackendMessage, BackendSettings, HistoryData } from "./backend";
import type { AppRule, LiveUsage, NavKey, UiState } from "./types";

const LINK_CAPACITY_MBPS = 100;
const HISTORY_MAX_SAMPLES = 300;

function initialLiveUsage(apps: AppRule[]): Record<string, LiveUsage> {
  const map: Record<string, LiveUsage> = {};
  for (const app of apps) {
    map[app.id] = { mbps: 0, throttled: false, status: "sleeping", history: [] };
  }
  return map;
}

// Demo mode (no service connected): simulates the 1 Hz "usage" telemetry
// stream the Windows service pushes over IPC — same shape, generated locally.
function simulateTick(app: AppRule, previous: LiveUsage): LiveUsage {
  const now = new Date();
  const withinWindow = isWithinSchedule(app.schedule, now);

  if (!app.enabled || !withinWindow) {
    const history = [...previous.history, 0].slice(-HISTORY_MAX_SAMPLES);
    return { mbps: 0, throttled: false, status: "sleeping", history };
  }

  // Demand presses ~40% above the limit so enforcement is visible in demo.
  const peak = (app.limitMbps ?? 20) * 1.4;
  const demand = peak * (0.85 + Math.random() * 0.3);

  let mbps = demand;
  let status: LiveUsage["status"] = "active";
  if (app.limitMbps != null && demand > app.limitMbps) {
    mbps = app.limitMbps * (0.97 + Math.random() * 0.03);
    status = "throttled";
  }

  const history = [...previous.history, mbps].slice(-HISTORY_MAX_SAMPLES);
  return { mbps, throttled: status === "throttled", status, history };
}

function App() {
  const [connected, setConnected] = useState(false);
  const [apps, setApps] = useState<AppRule[]>([]);
  const [liveUsage, setLiveUsage] = useState<Record<string, LiveUsage>>(() => initialLiveUsage([]));
  const [todayGb, setTodayGb] = useState(0);
  const [detectedProcesses, setDetectedProcesses] = useState<DetectedProcess[]>([]);
  const [historyData, setHistoryData] = useState<HistoryData | null>(null);
  const [settings, setSettings] = useState<BackendSettings>({
    enforcementEnabled: true,
    linkCapacityMbps: LINK_CAPACITY_MBPS,
  });
  const [editingAppId, setEditingAppId] = useState<string | null>(null);
  const [ui, setUi] = useState<UiState>({
    activeNav: "apps",
    addModalOpen: false,
    monitorRange: "60s",
    historyPeriod: "week",
  });

  // Real telemetry from the service (via the Rust named-pipe bridge).
  useEffect(() => {
    let unsubscribe: (() => void) | undefined;
    let cancelled = false;

    function handleMessage(message: BackendMessage) {
      switch (message.type) {
        case "rules":
          setApps(message.rules);
          setLiveUsage((current) => {
            const next: Record<string, LiveUsage> = {};
            for (const rule of message.rules) {
              next[rule.id] = current[rule.id] ?? { mbps: 0, throttled: false, status: "sleeping", history: [] };
            }
            return next;
          });
          break;
        case "usage":
          setLiveUsage((current) => {
            const next: Record<string, LiveUsage> = {};
            for (const [id, usage] of Object.entries(message.apps)) {
              const prev = current[id] ?? { mbps: 0, throttled: false, status: "sleeping", history: [] };
              next[id] = {
                mbps: usage.mbps,
                throttled: usage.throttled,
                status: usage.status,
                history: [...prev.history, usage.mbps].slice(-HISTORY_MAX_SAMPLES),
              };
            }
            return next;
          });
          setTodayGb(message.todayGb);
          break;
        case "processes":
          setDetectedProcesses(
            message.processes.map((proc, index) => ({
              name: proc.name,
              exeMatch: proc.exeMatch,
              iconColor: pickIconColor(index),
              initials: initialsFromName(proc.name),
            })),
          );
          break;
        case "history":
          setHistoryData(message);
          break;
        case "settings":
          setSettings(message.settings);
          break;
        case "error":
          console.warn("Service error:", message.command, message.message);
          break;
      }
    }

    function handleStatus(isConnected: boolean) {
      setConnected(isConnected);
      if (!isConnected) {
        setHistoryData(null);
        setDetectedProcesses([]);
        return;
      }
      // The service pushes `rules` right after the pipe handshake, but that
      // can happen before this webview subscribed — re-request state so a
      // late-discovered connection is never missing data.
      sendCommand({ type: "getRules" });
      sendCommand({ type: "getSettings" });
    }

    subscribeBackend(handleMessage, handleStatus).then((fn) => {
      if (cancelled) fn();
      else unsubscribe = fn;
    });

    return () => {
      cancelled = true;
      unsubscribe?.();
    };
  }, []);

  // Demo-mode simulator — paused while the real service streams usage.
  useEffect(() => {
    if (connected) return;
    const interval = setInterval(() => {
      setApps((currentApps) => {
        setLiveUsage((currentUsage) => {
          const next: Record<string, LiveUsage> = {};
          let bytesThisSecond = 0;
          for (const app of currentApps) {
            const prev = currentUsage[app.id] ?? { mbps: 0, throttled: false, status: "sleeping", history: [] };
            const sample = simulateTick(app, prev);
            next[app.id] = sample;
            bytesThisSecond += (sample.mbps * 1_000_000) / 8;
          }
          setTodayGb((gb) => gb + bytesThisSecond / 1_000_000_000);
          return next;
        });
        return currentApps;
      });
    }, 1000);
    return () => clearInterval(interval);
  }, [connected]);

  // History data comes from SQLite on the service side.
  useEffect(() => {
    if (connected && ui.activeNav === "history") {
      sendCommand({ type: "getHistory", period: ui.historyPeriod });
    }
  }, [connected, ui.activeNav, ui.historyPeriod]);

  const totalNowMbps = apps.reduce((sum, app) => sum + (liveUsage[app.id]?.mbps ?? 0), 0);

  function openAddModal() {
    setEditingAppId(null);
    if (connected) sendCommand({ type: "listProcesses" });
    setUi((s) => ({ ...s, addModalOpen: true }));
  }

  function openEditModal(id: string) {
    setEditingAppId(id);
    setUi((s) => ({ ...s, addModalOpen: true }));
  }

  function closeModal() {
    setUi((s) => ({ ...s, addModalOpen: false }));
    setEditingAppId(null);
  }

  function saveApp(app: AppRule) {
    if (connected) {
      // The service persists the rule and broadcasts the updated rule set,
      // which comes back through handleMessage("rules").
      sendCommand({ type: "setRule", rule: app });
      closeModal();
      return;
    }
    setApps((current) => {
      const exists = current.some((a) => a.id === app.id);
      return exists ? current.map((a) => (a.id === app.id ? app : a)) : [...current, app];
    });
    setLiveUsage((current) => ({
      ...current,
      [app.id]: current[app.id] ?? { mbps: 0, throttled: false, status: "sleeping", history: [] },
    }));
    closeModal();
  }

  function toggleApp(id: string) {
    if (connected) {
      sendCommand({ type: "toggle", id });
      return;
    }
    setApps((current) => current.map((a) => (a.id === id ? { ...a, enabled: !a.enabled } : a)));
  }

  function deleteApp(id: string) {
    if (connected) {
      sendCommand({ type: "deleteRule", id });
      return;
    }
    setApps((current) => current.filter((a) => a.id !== id));
    setLiveUsage((current) => {
      const next = { ...current };
      delete next[id];
      return next;
    });
  }

  const editingApp = editingAppId ? apps.find((a) => a.id === editingAppId) ?? null : null;

  function renderScreen(nav: NavKey) {
    switch (nav) {
      case "apps":
        return (
          <AppsScreen
            apps={apps}
            liveUsage={liveUsage}
            todayGb={todayGb}
            onAddApp={openAddModal}
            onEditApp={openEditModal}
            onToggleApp={toggleApp}
            onDeleteApp={deleteApp}
          />
        );
      case "monitor":
        return (
          <MonitorScreen
            apps={apps}
            liveUsage={liveUsage}
            range={ui.monitorRange}
            onRangeChange={(monitorRange) => setUi((s) => ({ ...s, monitorRange }))}
          />
        );
      case "history":
        return (
          <HistoryScreen
            apps={apps}
            period={ui.historyPeriod}
            realHistory={historyData}
            onPeriodChange={(historyPeriod) => setUi((s) => ({ ...s, historyPeriod }))}
          />
        );
      case "settings":
        return (
          <SettingsScreen
            connected={connected}
            settings={settings}
            onSave={(next) => sendCommand({ type: "setSettings", settings: next })}
          />
        );
    }
  }

  return (
    <div className="app-shell">
      <Sidebar
        activeNav={ui.activeNav}
        onNavigate={(activeNav) => setUi((s) => ({ ...s, activeNav }))}
        totalNowMbps={totalNowMbps}
        linkCapacityMbps={connected ? settings.linkCapacityMbps : LINK_CAPACITY_MBPS}
        connected={connected}
      />
      {renderScreen(ui.activeNav)}
      {ui.addModalOpen && (
        <AddAppModal
          editingApp={editingApp}
          detectedProcesses={detectedProcesses}
          onClose={closeModal}
          onSave={saveApp}
        />
      )}
    </div>
  );
}

export default App;
