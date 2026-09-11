# MCP v2 architecture

The server targets the released [MCP 2026-07-28 specification](https://modelcontextprotocol.io/specification/2026-07-28).
The official [Python v2 migration guide](https://py.sdk.modelcontextprotocol.io/migration/)
describes constructor callbacks, explicit results and removal of automatic low-level
schema validation. This implementation uses those callbacks, validates arguments
with JSON Schema 2020-12, supplies cacheable tool lists and returns structured
results with proper error flags. The SDK owns discovery, per-request metadata,
version errors and wire framing.

## Progressive disclosure

Three public tools are stable: search, describe, execute. Search returns short
ranked summaries; describe returns selected schemas, examples and prerequisites.
Execute validates the selected catalog schema before dispatch. Schemas and handlers
are not dynamically attached to the protocol session. This is an application
design, not a claim to implement a standardized progressive-discovery extension.

The [August 2026 MCP roadmap](https://blog.modelcontextprotocol.io/posts/mcp-roadmap/)
identifies progressive discovery as ongoing work. Cloudflare's
[February 2026 Code Mode design](https://blog.cloudflare.com/code-mode-mcp/) demonstrates
a fixed search/execute surface with code running in sandboxed isolates. Anthropic's
[tool-design guidance](https://www.anthropic.com/engineering/writing-tools-for-agents)
encourages operations that match agent tasks rather than mechanically exposing
every API call.

The server uses a catalog and bounded workflows. It does not run generated
code: this server has native engineering and PLC access and no isolate runtime.
The tradeoff is a discovery round trip and a generic execute tool whose permissions
must be conservative. Descriptions disclose each operation's finer-grained effects.

## Module boundaries

| Module | Responsibility |
| --- | --- |
| server.py | Only MCP types, public schemas, request validation and lifecycle. |
| catalog.py / operations.json | Search, operation contracts, prerequisites, validated examples. |
| engine.py | Explicit contexts, grants, workflow orchestration and preflight checks. |
| jobs.py | SQLite receipts, deduplication, serialized queue, cancellation, bounded result pages. |
| safety.py | Scoped grants with fixed monotonic expiration. |
| backend.py | Structured adapters between operation arguments and native commands. |
| dispatch.py / host.py | One native dispatch; COM serialization and process ownership. |
| cli.py / scope.py | Bounded recording/export processes and owned Scope sessions. |
| TcAutomation | Existing automation primitives and composite deployment/test workflows. |

Native dictionaries become structured result data with explicit success and
error fields.

## Explicit state and execution

MCP's [stateless core](https://modelcontextprotocol.io/specification/2026-07-28/basic)
requires cross-request state to be identified explicitly. Contexts, grants,
recordings and jobs therefore have opaque handles. The persistent shell is an
implementation resource behind an exclusive engineering context; it is not
identified by transport state. Contexts fix solution/target/PLC/version.

The worker queue serializes native operations; discovery and receipt queries run
independently. Preflight validates an entire sequence before effects and rechecks
authorization at every step. Source hashes, matching-login prerequisites and
online-change counters remain enforced by the C# implementation.

Every submitted operation, including context/grant creation, has a durable
request-key receipt. Repeating a key with identical canonical arguments returns
that receipt; different arguments conflict. No retry occurs after loss of a native
reply. Queued work is cancelled before dispatch; in-flight native work may finish.
Partial workflow receipts record what completed.

Jobs are an application API exposed through execute. They are **not** advertised
as the optional MCP Tasks extension. That distinction avoids depending on Tasks
support in every client.

## Compatibility and operational limits

Application v1 tool names and global arming/default-target APIs are removed.
No custom legacy adapter exists. The official SDK may still accept earlier wire
versions as its own built-in behavior; this does not restore v1 application tools.

The local receipt database is shared by server instances under the same OS user.
Receipt handles are unguessable, but this service is not an authenticated HTTP
multi-tenant endpoint. Use unique request keys and protect the directory with
normal OS-user permissions. Native context/recording/grant handles expire at
process restart; persisted jobs remain inspectable.

A slow native call cannot be safely force-cancelled as if it were a pure function.
The queue bounds pending work; native command timeouts bound waiting, not physical
rollback. A lost owner heartbeat produces an uncertain receipt without replay.
The optional Scope path requires a TE13xx-enabled build and separate qualification.

## Validation record

The automated suite checks modern stdio discovery/calls, protocol metadata,
cache hints, stable public tool lists, input errors and failure flags. Mocked
backend tests cover target/grant isolation, source/session invariants, duplicate
submissions, persistence, cancellation, sequence preflight and partial output.
No PLC activation or deployment is performed by these tests.

The protocol and orchestration suite contains 50 tests. Hardware qualification
is separate: live PLC deployment, online change and optional TE13xx Scope
require testing against the intended installation.
