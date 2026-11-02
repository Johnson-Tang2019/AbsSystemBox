# NTFS / service validation

2026-11-02 Windows x64. New functionality only; previously accepted cases were not rerun.

- Debug and Release full solution build: 0 warnings, 0 errors.
- 16 new targeted cases: snapshot revisions; journal replacement; hardlink allocation; 1000 coalesced mixed events; transactional failure/cancel/recovery; subtree deletion and parent/child creation; partial metadata retry; schema-2 backup and drive-letter case; ordinary/NTFS mode replacement; ACL/authorization/path checks; forged service; actual client token/image; agent Boolean rejection and missing-service fallback; interrupted tracking marks search/storage historical partial data.
- Restricted real WinUI: missing-service state, cancelled whole-volume consent without database creation, empty setup-volume rejection. See ui-flow.json.
- PowerShell parser and review-only installer plan succeeded. NSIS template compiled against existing fixture payload for syntax only; this is not service installation acceptance.
- No volume enumeration, VHD creation/format, service registration, reboot or other-OS acceptance performed. SCM install/update/removal and native-volume consistency remain unverified.
- JSON summaries omit personal SID, executable paths and raw metadata. Original local logs/TRX remain in artifacts/ntfs-* and artifacts/test-results/ntfs-*.
