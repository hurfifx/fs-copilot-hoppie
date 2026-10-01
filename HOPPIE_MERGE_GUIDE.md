# Hoppie Integration Guide

This repository contains the Hoppie/CPDLC shared-cockpit source handoff. Merge it into a matching full FS Copilot source tree; this repository alone is not a standalone application build.

## Core Hoppie files

- `FsCopilot/Hoppie/HoppieDump.cs`
- `FsCopilot/Hoppie/HoppieCaptureAutomation.cs`
- `FsCopilot/Hoppie/HoppieProxyService.cs`
- `FsCopilot/Hoppie/HoppieSettingsStore.cs`
- `FsCopilot/Hoppie/HoppieTransparentCaptureManager.cs`
- `FsCopilot/Hoppie/SessionRole.cs`

## Application integration points

- `FsCopilot/Program.cs`: service registration and startup configuration
- `FsCopilot/App.axaml.cs`: service disposal during application shutdown
- `FsCopilot/ViewModels/MainViewModel.cs`: host/joiner state and settings commands
- `FsCopilot/Views/MainWindow.axaml`: OB settings tab
- `FsCopilot/Views/MainWindow.axaml.cs`: UI event wiring
- `FsCopilot/Diagnostics/SessionLogSnapshotService.cs`: Hoppie dump collection
- `FsCopilot/FsCopilot.csproj`: framework/runtime references

## Receive fan-out behavior

1. The host polls Hoppie for the active aircraft callsign.
2. The host stores newly received messages and sends them to the joiner over the existing peer session.
3. The joiner stores the messages locally and acknowledges receipt.
4. Each aircraft receives pending messages through its own local Hoppie-compatible endpoint when it polls.

The source includes message deduplication in the host cache and tracks fan-out acknowledgements so the host can retire messages after both clients have seen them.

## Merge checklist

1. Merge the Hoppie folder and the listed application integration points into the target full source tree.
2. Preserve the target application's existing tabs, settings, session lifecycle and networking behavior while integrating the OB tab and Hoppie services.
3. Ensure the target project includes the ASP.NET Core framework reference required by the local listeners.
4. Confirm shutdown disposes the Hoppie service and capture automation.
5. Build and test on two PCs: send one CPDLC message, verify one upstream send, then verify inbound messages appear in both aircraft.
6. Check both clients' Hoppie dump files for host fan-out, joiner receipt, local delivery and acknowledgement events.

For pilot-facing setup, see [HOPPIE_SETUP.md](HOPPIE_SETUP.md).
