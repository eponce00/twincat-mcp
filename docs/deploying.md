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

The platform mapping was taken from a user-mode runtime (`PC-WIN-U-OS`) and a
CP67xx IPC with Windows 10 and the real-time kernel (`CB3273-WIN 10.0`). The
refusal was exercised against the user-mode runtime only. TwinCAT/BSD and x86
targets follow the documented descriptions and were not available for testing.
