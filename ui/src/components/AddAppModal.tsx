import { useEffect, useState } from "react";
import { open } from "@tauri-apps/plugin-dialog";
import { pickIconColor, initialsFromName } from "../mockData";
import type { DetectedProcess } from "../mockData";
import { tauriAvailable } from "../backend";
import type { AppRule, Schedule, ScheduleMode } from "../types";

interface AddAppModalProps {
  editingApp: AppRule | null;
  detectedProcesses: DetectedProcess[];
  onClose: () => void;
  onSave: (app: AppRule) => void;
}

const SLIDER_MAX = 100;

function colorIndexFor(name: string): number {
  let hash = 0;
  for (let i = 0; i < name.length; i++) hash = (hash * 31 + name.charCodeAt(i)) >>> 0;
  return hash;
}

function processFromExePath(path: string): DetectedProcess {
  const fileName = path.split(/[\\/]/).pop() ?? path;
  const baseName = fileName.replace(/\.exe$/i, "");
  const name = baseName.charAt(0).toUpperCase() + baseName.slice(1);
  return {
    name,
    exeMatch: fileName.toLowerCase(),
    iconColor: pickIconColor(colorIndexFor(fileName)),
    initials: initialsFromName(name),
  };
}

export function AddAppModal({ editingApp, detectedProcesses, onClose, onSave }: AddAppModalProps) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const [selectedProcess, setSelectedProcess] = useState<DetectedProcess | null>(() => {
    if (editingApp) {
      return { name: editingApp.name, exeMatch: editingApp.exeMatch, iconColor: editingApp.iconColor, initials: editingApp.initials };
    }
    return null;
  });
  const [limitMbps, setLimitMbps] = useState<number | null>(editingApp ? editingApp.limitMbps : 10);
  const [scheduleMode, setScheduleMode] = useState<ScheduleMode>(editingApp?.schedule.mode ?? "always");
  const [from, setFrom] = useState(editingApp?.schedule.from ?? "16:00");
  const [to, setTo] = useState(editingApp?.schedule.to ?? "22:00");

  // The process list arrives asynchronously from the service after the modal
  // opens — preselect the first entry once it shows up.
  useEffect(() => {
    if (!editingApp && !selectedProcess && detectedProcesses.length > 0) {
      setSelectedProcess(detectedProcesses[0]);
    }
  }, [detectedProcesses, editingApp, selectedProcess]);

  const sliderValue = limitMbps === null ? SLIDER_MAX : limitMbps;

  function handleSliderChange(value: number) {
    setLimitMbps(value >= SLIDER_MAX ? null : value);
  }

  async function browseForExe() {
    const picked = await open({
      title: "Wskaż plik programu",
      filters: [{ name: "Programy", extensions: ["exe"] }],
      multiple: false,
    });
    if (typeof picked === "string" && picked) {
      setSelectedProcess(processFromExePath(picked));
      setPickerOpen(false);
    }
  }

  function handleSave() {
    if (!selectedProcess) return;
    const schedule: Schedule = scheduleMode === "always" ? { mode: "always", from: "00:00", to: "00:00" } : { mode: "hours", from, to };

    const app: AppRule = editingApp
      ? { ...editingApp, limitMbps, schedule }
      : {
          id: crypto.randomUUID(),
          name: selectedProcess.name,
          exeMatch: selectedProcess.exeMatch,
          iconColor: selectedProcess.iconColor,
          initials: selectedProcess.initials,
          limitMbps,
          schedule,
          enabled: true,
        };

    onSave(app);
  }

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <div className="modal-header">
          <h3 className="modal-title">{editingApp ? "Edytuj limit aplikacji" : "Nowy limit aplikacji"}</h3>
          <button className="modal-close" onClick={onClose}>
            ✕
          </button>
        </div>

        <div className="modal-body">
          <div>
            <span className="field-label">Aplikacja</span>
            {!editingApp ? (
              <>
                {selectedProcess ? (
                  <button className="app-picker" onClick={() => setPickerOpen((v) => !v)}>
                    <span
                      className="app-icon"
                      style={{ background: selectedProcess.iconColor, width: 28, height: 28, fontSize: 11 }}
                    >
                      {selectedProcess.initials}
                    </span>
                    <span className="app-picker-name">{selectedProcess.name}</span>
                    <span className="app-picker-change">zmień ▾</span>
                  </button>
                ) : (
                  <button className="app-picker" onClick={() => setPickerOpen((v) => !v)}>
                    <span className="app-picker-name" style={{ color: "var(--color-text-muted)" }}>
                      Wybierz aplikację…
                    </span>
                    <span className="app-picker-change">▾</span>
                  </button>
                )}
                {(pickerOpen || (!selectedProcess && detectedProcesses.length > 0)) && (
                  <div className="picker-list" style={{ marginTop: 8, maxHeight: 260, overflowY: "auto" }}>
                    {detectedProcesses.map((proc, index) => (
                      <button
                        key={proc.exeMatch}
                        className="picker-list-item"
                        onClick={() => {
                          setSelectedProcess(proc);
                          setPickerOpen(false);
                        }}
                      >
                        <span
                          className="app-icon"
                          style={{ background: proc.iconColor || pickIconColor(index), width: 26, height: 26, fontSize: 11 }}
                        >
                          {proc.initials}
                        </span>
                        {proc.name}
                        <span style={{ marginLeft: "auto", fontSize: 12, color: "var(--color-text-muted)" }}>{proc.exeMatch}</span>
                      </button>
                    ))}
                  </div>
                )}
                <div className="field-hint" style={{ display: "flex", alignItems: "center", gap: 10 }}>
                  {detectedProcesses.length > 0
                    ? `${detectedProcesses.length} uruchomionych procesów — najpierw te z ruchem sieciowym`
                    : "Brak połączenia z usługą — wskaż plik programu ręcznie"}
                  {tauriAvailable && (
                    <button
                      className="btn-secondary"
                      style={{ padding: "5px 10px", fontSize: 12 }}
                      onClick={browseForExe}
                    >
                      Wskaż plik .exe…
                    </button>
                  )}
                </div>
              </>
            ) : (
              <div className="app-picker" style={{ cursor: "default" }}>
                <span
                  className="app-icon"
                  style={{ background: selectedProcess!.iconColor, width: 28, height: 28, fontSize: 11 }}
                >
                  {selectedProcess!.initials}
                </span>
                <span className="app-picker-name">{selectedProcess!.name}</span>
              </div>
            )}
          </div>

          <div>
            <span className="field-label">Limit prędkości pobierania</span>
            <div className="slider-row">
              <input
                type="range"
                min={0.5}
                max={SLIDER_MAX}
                step={0.5}
                value={sliderValue}
                onChange={(e) => handleSliderChange(Number(e.target.value))}
              />
              <input
                className="slider-number"
                value={limitMbps === null ? "bez limitu" : limitMbps}
                onChange={(e) => {
                  const parsed = Number(e.target.value.replace(",", "."));
                  if (!Number.isNaN(parsed) && parsed > 0) {
                    handleSliderChange(Math.min(parsed, SLIDER_MAX));
                  }
                }}
              />
            </div>
          </div>

          <div>
            <span className="field-label">Godziny działania</span>
            <div className="schedule-tiles">
              <button
                className={`schedule-tile${scheduleMode === "always" ? " active" : ""}`}
                onClick={() => setScheduleMode("always")}
              >
                Cała doba
              </button>
              <button
                className={`schedule-tile${scheduleMode === "hours" ? " active" : ""}`}
                onClick={() => setScheduleMode("hours")}
              >
                Wybrane godziny
              </button>
            </div>
            {scheduleMode === "hours" && (
              <>
                <div className="time-fields">
                  <input type="time" value={from} onChange={(e) => setFrom(e.target.value)} />
                  <span>→</span>
                  <input type="time" value={to} onChange={(e) => setTo(e.target.value)} />
                </div>
                <div className="field-hint">poza tymi godzinami aplikacja jest wstrzymywana</div>
              </>
            )}
          </div>
        </div>

        <div className="modal-footer">
          <button className="btn-secondary" onClick={onClose}>
            Anuluj
          </button>
          <button
            className="btn-primary"
            onClick={handleSave}
            disabled={!selectedProcess}
            style={!selectedProcess ? { opacity: 0.5, cursor: "not-allowed" } : undefined}
          >
            Zapisz limit
          </button>
        </div>
      </div>
    </div>
  );
}
