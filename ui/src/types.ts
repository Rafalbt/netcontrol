export type ScheduleMode = "always" | "hours";

export interface Schedule {
  mode: ScheduleMode;
  from: string;
  to: string;
}

export interface AppRule {
  id: string;
  name: string;
  exeMatch: string;
  iconColor: string;
  initials: string;
  limitMbps: number | null;
  schedule: Schedule;
  enabled: boolean;
}

export type AppStatus = "active" | "throttled" | "sleeping";

export interface LiveUsage {
  mbps: number;
  throttled: boolean;
  status: AppStatus;
  history: number[];
}

export type NavKey = "apps" | "monitor" | "history" | "settings";

export type MonitorRange = "60s" | "15min" | "1h";

export type HistoryPeriod = "day" | "week" | "month";

export interface UiState {
  activeNav: NavKey;
  addModalOpen: boolean;
  monitorRange: MonitorRange;
  historyPeriod: HistoryPeriod;
}
