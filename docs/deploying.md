# Deploying to a target

`workflow.deploy` builds, activates and restarts the context target. Before it
builds anything, it decides which solution configuration and platform to build
and checks that the target can run that platform. A shell or solution reused
from an earlier session keeps whatever configuration that session left, so the
deploy never builds the active configuration without checking it.

## What is built

| Arguments | Configuration | Platform |
| --- | --- | --- |
| `variant` | The variant's `BuildConfig` from the `.tsproj` | The variant's `BuildConfig` |
| `configuration` / `platform` | As given | As given |
| Neither | The solution's current configuration | The platform the target needs (below) |

Explicit arguments must agree with the variant; a conflict is refused. A
variant whose `TargetNetId` is not the context target is refused: open a
context on that target instead. The variant is made current, and the chosen
configuration and platform are selected and verified, before the clean build.
After the build, the solution must still be on the selection, or nothing is
downloaded.

`skipBuild` is refused when the solution's current configuration is not the
selection, since the last build would be for another platform.

## Target platform check

The deploy reads the target's description from its TwinCAT system service
(ADS port 10000, index group 700, offset 1; the `TcTargetDesc` XAE reads):

| Target | Builds for |
| --- | --- |
| User-mode runtime (`IsUsermodeRuntime`), x64 | `TwinCAT OS (x64)` |
| TwinCAT/BSD, x64 | `TwinCAT OS (x64)` |
| Windows with the real-time kernel, x64 | `TwinCAT RT (x64)` |
| Windows with the real-time kernel, x86 | `TwinCAT RT (x86)` |
| Anything else | Not known |

A build platform that differs from the target's is refused before building or
downloading. A target whose kind is not known is refused too.
`allowUnverifiedPlatform` lets the deploy go ahead only when the description
cannot be read at all. It never overrides a known mismatch, and the receipt
then reports `platformVerified:false` and a `platformStatus` that starts with
"platform not verified".

The receipt, and the dry run, report `variant`, `configuration`, `platform`,
`platformVerified`, `platformStatus` and the `target` description. Step 2 shows
the selection, where it came from (`arguments`, `variant`, `target` or
`active`) and the configuration the solution was on before.

## Boot project

By default each selected PLC's boot project is generated before the
activation. `generateBootProject:false` only sets autostart and lets the
activation write the boot files. Use it for solutions where generating the
boot project re-enables PLC projects that are disabled in the `.tsproj`.

## Ending in Run

After the activation lands, the deploy restarts TwinCAT and waits up to
`runTimeoutSeconds` (default 120) for Run on the system service (port 10000)
and on each deployed PLC's configured ADS port. Run must hold for 2 s. A
system service that comes back in Config and stays there for 30 s has started
in Config. That, a timeout, or a PLC that stays in Stop fails the deployment
instead of reporting success. The receipt's `run` holds the final states,
each distinct observation (`transitions`), and `targetMessages`.

`targetMessages` are the target's event log (TcEventLogger, ADS port 110)
during the restart. The live subscription is completed by reading back the
events logged since just before the restart. They include PLC and
application messages. TwinCAT's system messages that XAE's dialog shows
from the ADS logger, such as a missing licence, are not part of the event log
and are not returned. When the target stays in Config without messages,
check its licences and XAE's output.

`engineering.restart` waits the same way, for the system service and every
PLC whose boot project autostarts.

## Restart into Run or Config over ADS

`runtime.set_state` with `Run` or `Config` on the system service restarts
TwinCAT into that mode: it writes `Reset` (Run) or `Reconfig` (Config) to port
10000, as XAE's restart prompts do; targets reject a direct `Run` there. It
then waits up to 60 s for the requested state, and for `Run` returns the
target's event log when Run is not reached. Other states and the PLC ports
are written as before.

## Deploy, then publish the HMI

`workflow.deploy` takes an optional `hmiPublish` with the arguments of
`hmi.publish` (`projectPath`, `targetUrl`, `profileName`,
`expectedFingerprint`, `timeoutSeconds`, `credentialTarget`). After the target
is verified in Run, the HMI is published once from the same solution, loaded
for HMI work only as `hmi.publish` requires. Nothing is published when the
deployment fails or does not reach Run. Get `expectedFingerprint` from
`hmi.preflight` in an HMI context first. The grant must name both
`workflow.deploy` and `hmi.publish` for the PLC context. A failed publish fails
the workflow and keeps the deployment receipt; the dry run lists the planned
publish.

## Variants and multi-target plans

`engineering.set_variant` applies the variant's build configuration and
target as XAE's variant selector does; the Automation Interface alone changes
only the variant name. It refuses a variant whose target is not the context
target.

`engineering.list_variants` (read-only, no grant) returns each variant's
target and build configuration, the solution's current selection, and a plan
in the requested `order`. Each plan step names the target, the build and the
operations to run, one context per target:
`context.open` on the variant's target, `workflow.deploy` with the variant
(dry run first), and `context.close`. Only the context target is read over
ADS; `probeTargets:true` reads every variant's target.

## Not qualified

- Run verification was exercised on a local user-mode runtime whose licences
  did not allow its PLC to run: the deployment and restart correctly failed in
  Config. Reaching Run with these changes was not exercised.
- `runtime.set_state` Run/Config (`Reset`/`Reconfig` on port 10000) was not
  exercised: on the shared user-mode runtime a system reset has stopped the
  TwinCAT system service before.
- `hmiPublish` was exercised with mocked native steps only.
- No remote RT x64 target was used for any of these.

The platform mapping was taken from a user-mode runtime (`PC-WIN-U-OS`) and a
CP67xx IPC with Windows 10 and the real-time kernel (`CB3273-WIN 10.0`). The
refusal was exercised against the user-mode runtime only. TwinCAT/BSD and x86
targets follow the documented descriptions and were not available for testing.
