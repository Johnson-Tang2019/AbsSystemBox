# SysBox Agent Development Guide

Build a Windows-native utility with C#, .NET 10 and WinUI 3.

## Before modifying code
1. Read docs/SYSBOX_MASTER_PLAN.md, docs/STATUS.md and docs/ARCHITECTURE.md.
2. Inspect the affected source and tests.
3. State scope, risks and affected files before meaningful changes.
4. Implement a bounded milestone task; do not generate unrelated roadmap features.

## Constraints
- No Electron, WebView2, Chromium or Node.js application runtime.
- No runtime dependency on Everything, WizTree, Mem Reduct or Fluent Reduct.
- Keep UI separate from filesystem, NTFS and privileged business logic.
- Search and storage share index metadata contracts.
- No dangerous cleanup enabled by default, file deletion, generic privileged commands or admin-only UI.
- Preserve user data; do not start full-volume scans during development.
- Long-running operations must be cancellable or bounded.
- Use explicit error states; never return fake hardware data for unimplemented services.
- Use SafeHandle, checked native layouts and validated buffer bounds when native APIs are added.
- Future named pipes need explicit ACLs and peer identity verification.
- Agent-controlled Boolean parameters cannot authorize privileged operations.
- Source comments and identifiers use English; user-facing text uses Chinese.
- Original code is GPL-3.0-or-later; track third-party licenses before adding dependencies.

## Verification and delivery
Run relevant build/tests. Do not invent results or equate a build with UI/runtime acceptance.
Update docs/STATUS.md and affected API/architecture documents.
Report changes, verification, remaining limitations and the next bounded task.
