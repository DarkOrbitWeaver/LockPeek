# LockPeek privacy

LockPeek processes selected file paths and local process metadata on this PC. It has no telemetry, cloud service, account, advertising or background service. A user-clicked support link opens Ko-fi in the default browser; LockPeek does not send file paths or process metadata to Ko-fi. Ordinary launches do not persist diagnostic reports. Debug builds can produce local test reports containing file paths and process metadata.

Closing the window exits the app. The Delete action can delete selected files and folders after confirmation, using the Windows shell. Windows may offer permanent deletion if an item cannot be recycled. This policy is a draft for review before publication; the publisher must provide a public privacy-policy URL.

After a successful action, a brief thank-you label may appear beside the support button. It does not register Windows notifications or contact Ko-fi. Startup/launch failures may leave local error diagnostics.

Failure diagnostics are stored locally under `%LOCALAPPDATA%\LockPeek` (Windows may redirect this inside the app's package data). `startup-error.txt` replaces the previous exception and can contain local paths; `shell-launch.log` contains error stages/codes and resets after approximately 64 KiB. These files are not uploaded. They persist until replaced or removed; users can delete them with the app closed.
