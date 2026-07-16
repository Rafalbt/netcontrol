import type { Schedule } from "./types";

function toMinutes(hhmm: string): number {
  const [h, m] = hhmm.split(":").map(Number);
  return h * 60 + m;
}

export function isWithinSchedule(schedule: Schedule, now: Date): boolean {
  if (schedule.mode === "always") return true;

  const nowMinutes = now.getHours() * 60 + now.getMinutes();
  const from = toMinutes(schedule.from);
  const to = toMinutes(schedule.to);

  if (from <= to) {
    return nowMinutes >= from && nowMinutes < to;
  }
  // Overnight window (e.g. 22:00 -> 06:00).
  return nowMinutes >= from || nowMinutes < to;
}
