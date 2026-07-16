// Bridge between the frontend and the PrzepustnicaService named pipe
// (PLAN_WDROZENIA_WINDOWS.md §2). Maintains one auto-reconnecting client
// connection; every JSON line from the service is re-emitted to the webview
// as a `backend-message` event, and `backend-status` signals connectivity.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;

use tauri::{AppHandle, Emitter, State};
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader, WriteHalf};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};
use tokio::sync::Mutex;

const PIPE_PATH: &str = r"\\.\pipe\przepustnica";

struct Backend {
    writer: Mutex<Option<WriteHalf<NamedPipeClient>>>,
    connected: AtomicBool,
}

#[tauri::command]
async fn send_to_service(state: State<'_, Arc<Backend>>, message: String) -> Result<(), String> {
    let mut guard = state.writer.lock().await;
    let writer = guard.as_mut().ok_or("service not connected")?;
    writer
        .write_all(format!("{message}\n").as_bytes())
        .await
        .map_err(|e| e.to_string())?;
    writer.flush().await.map_err(|e| e.to_string())
}

// The pipe usually connects before the webview finishes loading, so the
// initial `backend-status` event can fire into the void. The frontend calls
// this right after subscribing to catch up on the current state.
#[tauri::command]
fn backend_connected(state: State<'_, Arc<Backend>>) -> bool {
    state.connected.load(Ordering::Relaxed)
}

fn read_auth_token() -> Option<String> {
    // Written by the service on startup; both sides being able to read the
    // same %ProgramData% file is the auth proof (plan §2: pipe + token).
    let base = std::env::var("ProgramData").unwrap_or_else(|_| r"C:\ProgramData".into());
    let path = std::path::Path::new(&base)
        .join("Przepustnica")
        .join("ipc.token");
    std::fs::read_to_string(path)
        .ok()
        .map(|token| token.trim().to_string())
        .filter(|token| !token.is_empty())
}

async fn backend_loop(app: AppHandle, backend: Arc<Backend>) {
    loop {
        match ClientOptions::new().open(PIPE_PATH) {
            Ok(client) => {
                let (read_half, mut write_half) = tokio::io::split(client);

                // Authenticate before anything else; the service closes the
                // pipe on a bad token and we retry on the next loop pass.
                let token = read_auth_token().unwrap_or_default();
                let hello = format!(
                    "{}\n",
                    serde_json::json!({ "type": "hello", "token": token })
                );
                if write_half.write_all(hello.as_bytes()).await.is_err() {
                    tokio::time::sleep(Duration::from_secs(2)).await;
                    continue;
                }

                *backend.writer.lock().await = Some(write_half);
                backend.connected.store(true, Ordering::Relaxed);
                let _ = app.emit("backend-status", true);

                let mut lines = BufReader::new(read_half).lines();
                while let Ok(Some(line)) = lines.next_line().await {
                    let _ = app.emit("backend-message", line);
                }

                *backend.writer.lock().await = None;
                backend.connected.store(false, Ordering::Relaxed);
                let _ = app.emit("backend-status", false);
            }
            Err(_) => {
                // Service not running (or pipe busy) — keep retrying quietly;
                // the UI stays in demo mode until we connect.
            }
        }
        tokio::time::sleep(Duration::from_secs(2)).await;
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let backend = Arc::new(Backend {
        writer: Mutex::new(None),
        connected: AtomicBool::new(false),
    });

    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_dialog::init())
        .manage(backend.clone())
        .invoke_handler(tauri::generate_handler![send_to_service, backend_connected])
        .setup(move |app| {
            let handle = app.handle().clone();
            tauri::async_runtime::spawn(backend_loop(handle, backend));
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
