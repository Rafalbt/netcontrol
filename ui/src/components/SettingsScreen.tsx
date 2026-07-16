import { useEffect, useState } from "react";
import type { BackendSettings } from "../backend";

interface SettingsScreenProps {
  connected: boolean;
  settings: BackendSettings;
  onSave: (settings: BackendSettings) => void;
}

export function SettingsScreen({ connected, settings, onSave }: SettingsScreenProps) {
  const [capacityText, setCapacityText] = useState(String(settings.linkCapacityMbps));

  useEffect(() => {
    setCapacityText(String(settings.linkCapacityMbps));
  }, [settings.linkCapacityMbps]);

  function commitCapacity() {
    const parsed = Number(capacityText.replace(",", "."));
    if (!Number.isNaN(parsed) && parsed > 0 && parsed !== settings.linkCapacityMbps) {
      onSave({ ...settings, linkCapacityMbps: parsed });
    } else {
      setCapacityText(String(settings.linkCapacityMbps));
    }
  }

  return (
    <div className="content">
      <div className="content-header">
        <div className="content-title-group">
          <h2 className="content-title">Ustawienia</h2>
          <span className="content-subtitle">Globalny wyłącznik i parametry łącza</span>
        </div>
      </div>

      {!connected && (
        <div className="settings-placeholder">
          Tryb demo — ustawienia są dostępne po połączeniu z usługą.
        </div>
      )}

      {connected && (
        <div className="settings-list">
          <div className="settings-row">
            <div>
              <div className="settings-row-title">Egzekwowanie limitów</div>
              <div className="settings-row-hint">
                Po wyłączeniu reguły pozostają zapisane, ale ruch nie jest ograniczany ani blokowany.
              </div>
            </div>
            <button
              className={`switch${settings.enforcementEnabled ? " on" : ""}`}
              role="switch"
              aria-checked={settings.enforcementEnabled}
              onClick={() => onSave({ ...settings, enforcementEnabled: !settings.enforcementEnabled })}
            >
              <span className="switch-knob" />
            </button>
          </div>

          <div className="settings-row">
            <div>
              <div className="settings-row-title">Pojemność łącza</div>
              <div className="settings-row-hint">
                Używana do skali paska „Wykorzystanie łącza" w panelu bocznym (Mb/s).
              </div>
            </div>
            <input
              className="slider-number"
              value={capacityText}
              onChange={(e) => setCapacityText(e.target.value)}
              onBlur={commitCapacity}
              onKeyDown={(e) => {
                if (e.key === "Enter") commitCapacity();
              }}
            />
          </div>
        </div>
      )}
    </div>
  );
}
