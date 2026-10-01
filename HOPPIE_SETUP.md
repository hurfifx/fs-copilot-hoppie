# Hoppie Shared-Cockpit Setup

This source tree contains the built-in FS Copilot Hoppie sync layer for shared cockpit.

It solves two main problems:

- both pilots can send through one shared upstream Hoppie session without duplicate upstream traffic
- inbound Hoppie replies can be delivered back to both aircraft through each PC's local Hoppie capture

## Current behavior

- For aircraft with hardcoded `www.hoppie.nl`, transparent capture is currently required.
- Only the host/master PC needs the real upstream Hoppie logon in FS Copilot.
- The joiner/slave PC should leave the FS Copilot Hoppie logon empty.
- The aircraft Hoppie logon typed inside the aircraft can stay as-is on both PCs.
- FS Copilot installs and removes the capture automatically around an active session.
- While a session is active, normal browsing to the Hoppie website on that same PC will not behave normally.

## First-time install

Do this once on each PC:

1. Launch the FS Copilot build that includes Hoppie sync.
2. Open the `OB` tab.
3. On the host/master PC only:
   - enter the real Hoppie logon
   - click `Save`
4. On the joiner/slave PC:
   - leave the host Hoppie logon field empty
5. Start or join an FS Copilot session.
6. Accept the Windows elevation prompt if FS Copilot asks to enable capture for that session.

Notes:

- FS Copilot removes the capture automatically when you leave the session or close the app.
- If a previous crash leaves capture behind, the next launch should clean it up automatically.

## Normal use

1. Start FS Copilot on both PCs.
2. Connect the shared-cockpit session.
3. Start the aircraft on both sides.
4. Use Hoppie/CPDLC normally.

For Fenix specifically:

- make sure the aircraft is fully initialized on the Hoppie/ATC side
- if the aircraft is not yet polling Hoppie, inbound replies may not appear immediately
- in practice, having the Fenix flight or ATC side opened and initialized is important

## Recommended setup

Host/master PC:

- FS Copilot Hoppie logon: real Hoppie logon
- aircraft Hoppie logon: leave as you normally use it
- capture: automatic while the session is active

Joiner/slave PC:

- FS Copilot Hoppie logon: blank
- aircraft Hoppie logon: leave as you normally use it
- capture: automatic while the session is active

## Troubleshooting

If messages are not showing up:

1. Confirm both PCs allowed the automatic capture prompt for the active session.
2. Confirm only the host has the real Hoppie logon in FS Copilot.
3. Confirm the aircraft is actually polling Hoppie.
4. For Fenix, confirm the relevant flight or ATC page is initialized.
5. Check that no old hidden `FsCopilot.exe` is still running in Task Manager.

If you need the normal Hoppie website back on that PC:

1. Leave the FS Copilot session, or close FS Copilot.
2. If a previous run crashed and the redirect stayed behind, launch FS Copilot once so it can remove it.

## Developer notes

Main implementation files:

- `FsCopilot/Hoppie/HoppieCaptureAutomation.cs`
- `FsCopilot/Hoppie/HoppieProxyService.cs`
- `FsCopilot/Hoppie/HoppieTransparentCaptureManager.cs`
- `FsCopilot/Hoppie/HoppieSettingsStore.cs`
- `FsCopilot/ViewModels/MainViewModel.cs`
- `FsCopilot/Views/MainWindow.axaml`

Important current limitation:

- the no-capture path for hardcoded Hoppie aircraft is not implemented yet
- today, transparent capture is the supported path for aircraft that do not allow a custom Hoppie endpoint
