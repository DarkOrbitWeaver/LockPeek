# Microsoft Store listing draft

Not published. Review only after the release gates pass.

**Name:** LockPeek

**Short description:** Find apps using your files, close the ones you choose, and retry in Explorer.

## Description

When Windows says a file is in use, LockPeek helps you find the apps Windows identifies as using it.

LockPeek is an Explorer context-menu tool. After installation, right-click a file or folder to use it; it does not need to stay open in the background.

Select files or folders in Explorer and choose Find apps using this file, this folder, or these items. LockPeek opens one native window for the whole selection. Review the detected processes, expand a row for details, and request a normal close for selected eligible apps. If needed, choose Force end and confirm the possible loss of unsaved work.

LockPeek rescans after app actions and reports what it actually observed. A separate Check and delete workflow scans first, then lets you review eligible local selections before deletion. Windows uses the Recycle Bin when possible and can warn before permanent deletion. LockPeek does not delete automatically after closing apps or change a normal close into a forced termination.

Features:
- Explorer multi-selection and incremental folder scans
- One row per process, with PID, executable, account and session details
- Graceful close and explicit Force end for eligible ordinary user apps
- Optional, explicitly confirmed Delete workflow for eligible local items
- Light and dark themes, drag/drop and multi-file picker
- No account, telemetry, startup task or resident service

Detection uses a high-performance direct NT handle scanner. A zero-result scan does not guarantee that an item can be deleted. Direct folder handles, some handle types and unsupported or inaccessible resources may not be identified; incomplete coverage is reported.

Optional Ko-fi support does not unlock features. All features remain free.

## Submission details still needed

Partner Center Product identity values: Package/Identity/Name, Package/Identity/Publisher, Package/Properties/PublisherDisplayName. Also the approved public display name, privacy-policy URL and license. Do not paste passwords, private keys, PFX files, recovery codes or API secrets into a chat.

Microsoft signs accepted Store-distributed MSIX packages. A CA-trusted personal certificate is not required for Store submission. Public sideloading is a separate distribution/signing decision; the existing self-signed development certificate is only locally trusted.

Sources: https://learn.microsoft.com/en-us/windows/apps/publish/get-started and https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview . Checked 2026-11-04.
