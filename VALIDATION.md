# Validation record

Executed locally on 2026-10-27 with .NET SDK 10.0.400 on Windows:

- Release build: passed, zero warnings and errors.
- xUnit integration suite: **6/6 passed**, independently rerun by the portfolio maintainer.
- Format verification: passed.
- NuGet vulnerability audit: no vulnerable packages reported by the audit at validation time.
- Live HTTP smoke: create 201, unchanged idempotent replay 201, other-tenant read 404, ETag update from version 1 to 2, local outbox dispatch published one event without failure.
- Development CORS tests allow only the documented localhost origins and reject an unrelated origin.

The Azure Service Bus adapter compiles against the official SDK. No Azure namespace, cloud delivery, cloud deployment, production identity system, or distributed multi-instance behavior was exercised. Local keys and data are synthetic demonstration fixtures.
