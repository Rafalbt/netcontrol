export interface DetectedProcess {
  name: string;
  exeMatch: string;
  iconColor: string;
  initials: string;
}

const APP_COLORS = [
  "oklch(0.7 0.13 250)",
  "oklch(0.68 0.16 30)",
  "oklch(0.62 0.16 300)",
  "oklch(0.62 0.02 260)",
  "oklch(0.65 0.15 90)",
  "oklch(0.6 0.14 200)",
];

export function pickIconColor(index: number): string {
  return APP_COLORS[index % APP_COLORS.length];
}

export function initialsFromName(name: string): string {
  const letters = name.replace(/[^\p{L}\p{N}]/gu, "");
  if (letters.length >= 2) return letters[0].toUpperCase() + letters[1];
  return (letters + "??").slice(0, 2);
}
