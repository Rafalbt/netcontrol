// Frontend side of the service IPC bridge. The Rust layer (src-tauri/src/lib.rs)
// holds the named-pipe connection and re-emits each JSON line as a
// `backend-message` event; commands go out through the `send_to_service`
// tauri command. In a plain browser (vite dev without Tauri) everything here
// no-ops and the app stays on the local simulator.

import { invoke, isTauri } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import type { AppRule, AppStatus } from "./types";

export interface UsageApp {
  mbps: number;
  downMbps: number;
  upMbps: number;
  throttled: boolean;
  status: AppStatus;
}

export interface ProcessEntry {
  name: string;
  exeMatch: string;
  path: string | null;
  pidCount: number;
}

export interface HistoryData {
  period: string;
  labels: string[];
  seriesGb: Record<string, number[]>;
  savedGb: number;
  throttleEvents: number;
}

export interface BackendSettings {
  enforcementEnabled: boolean;
  linkCapacityMbps: number;
}

export type BackendMessage =
  | { type: "rules"; rules: AppRule[] }
  | { type: "usage"; ts: number; apps: Record<string, UsageApp>; totalMbps: number; todayGb: number }
  | { type: "processes"; processes: ProcessEntry[] }
  | ({ type: "history" } & HistoryData)
  | { type: "settings"; settings: BackendSettings }
  | { type: "error"; command?: string; message: string };

export const tauriAvailable = isTauri();

export async function subscribeBackend(
  onMessage: (message: BackendMessage) => void,
  onStatus: (connected: boolean) => void,
): Promise<() => void> {
  if (!tauriAvailable) return () => {};

  const unlistenMessage = await listen<string>("backend-message", (event) => {
    try {
      onMessage(JSON.parse(event.payload) as BackendMessage);
    } catch (err) {
      console.warn("Unparseable backend message", err, event.payload);
    }
  });
  const unlistenStatus = await listen<boolean>("backend-status", (event) => onStatus(event.payload));

  // The Rust bridge usually connects to the pipe before this webview code
  // runs, so the initial backend-status event (and the service's first
  // `rules` push) can be missed entirely — catch up on the current state.
  try {
    onStatus(await invoke<boolean>("backend_connected"));
  } catch {
    // Old bridge build without the command — events alone have to do.
  }

  return () => {
    unlistenMessage();
    unlistenStatus();
  };
}

export function sendCommand(command: object): void {
  if (!tauriAvailable) return;
  invoke("send_to_service", { message: JSON.stringify(command) }).catch((err) =>
    console.warn("send_to_service failed", err),
  );
}
