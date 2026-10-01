# FS Copilot Hoppie Shared-Cockpit Sync

Source handoff for the Hoppie/CPDLC synchronization feature integrated into FS Copilot.

## What it does

In shared cockpit, two aircraft clients can send duplicate Hoppie requests, while an inbound response may only reach one cockpit. This module routes both clients through the existing FS Copilot peer session, lets the host own the single upstream Hoppie connection, suppresses duplicate sends, and distributes received messages to both aircraft clients.

The handoff includes the latest receive fan-out work: the host pushes newly fetched messages to the joiner, and the joiner can serve its next aircraft poll from its local cache.

## User setup

See [HOPPIE_SETUP.md](HOPPIE_SETUP.md) for host/joiner configuration and operation.

## Integrating into a full FS Copilot source tree

See [HOPPIE_MERGE_GUIDE.md](HOPPIE_MERGE_GUIDE.md) for source files, integration points, and validation steps.

This is a source handoff, not a standalone build of FS Copilot. The integration files depend on the rest of the FS Copilot application and networking layer. Merge these changes into the matching full source tree before building.

## Main implementation

- `FsCopilot/Hoppie/HoppieProxyService.cs`: local endpoints, upstream polling/sending, dedupe, session tunnel and inbound fan-out
- `FsCopilot/Hoppie/HoppieCaptureAutomation.cs`: session-driven capture lifecycle
- `FsCopilot/Hoppie/HoppieTransparentCaptureManager.cs`: local hostname capture and certificate management
- `FsCopilot/Hoppie/HoppieSettingsStore.cs`: saved settings
- `FsCopilot/Hoppie/SessionRole.cs`: host/joiner role state
- `FsCopilot/Program.cs`, `App.axaml.cs`, `ViewModels/MainViewModel.cs`, `Views/MainWindow.axaml`: app integration

## Current limitation

Aircraft that hardcode Hoppie's hostname still require transparent local capture. Aircraft also need to initialize their Hoppie/ATC client and poll for messages.

## License

No license is included in this handoff. Public visibility allows people to view the repository; it does not grant reuse rights. Add the project owner's approved license before accepting outside contributions or redistribution.
