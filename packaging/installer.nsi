; SPDX-License-Identifier: GPL-3.0-or-later
; -*- coding: utf-8 -*-
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "WordFunc.nsh"
!include "TextFunc.nsh"
!include "x64.nsh"
!define PRODUCT "AbsSystemBox.JohnsonTang2019"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT}"
Name "AbsSystemBox ${VERSION}"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\AbsSystemBox"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor zlib
BrandingText "AbsSystemBox · 粉色原生工具箱"
VIProductVersion "${WINDOWS_VERSION}"
VIAddVersionKey /LANG=2052 "ProductName" "AbsSystemBox"
VIAddVersionKey /LANG=2052 "FileDescription" "AbsSystemBox 用户安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "GPL-3.0-or-later; third-party terms included"
!define MUI_ABORTWARNING
!define MUI_INSTFILESPAGE_COLORS "C54679 FFF5F9"
!insertmacro MUI_PAGE_WELCOME
!define MUI_LICENSEPAGE_CHECKBOX
!insertmacro MUI_PAGE_LICENSE "${LICENSEFILE}"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\app\SysBox.App.exe"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"
Var PayloadRoot
Var Manifest
Var Line
Var FullPath
Var Parent
Var Prefix
Var PrefixLength
Var Counter
Var PriorVersion
Var InstallLock
Var ExistingRoot

!macro CANONICAL OUTPUT INPUT
  StrCpy $6 "${INPUT}"
  System::Call 'kernel32::GetFullPathNameW(w r6,i 1024,w .r8,p 0)i.r7'
  StrCpy ${OUTPUT} $8
  ${If} $7 == 0
  ${OrIf} $7 >= 1024
    StrCpy ${OUTPUT} ""
  ${EndIf}
!macroend

!macro CLEAN_PAYLOAD FN
Function ${FN}CleanPayload
  ; Delete only manifest-owned files within this payload, never recurse over user data.
  IfFileExists "$PayloadRoot\.sysbox-payload" 0 cleanup_done
  FileOpen $0 "$PayloadRoot\.sysbox-payload" r
  FileRead $0 $1
  FileClose $0
  ${TrimNewLines} $1 $1
  StrCmp $1 "${PRODUCT}" 0 cleanup_done
  StrCpy $6 $PayloadRoot
  System::Call 'kernel32::GetFileAttributesW(w r6)i.r0'
  IntOp $0 $0 & 0x400
  IntCmp $0 0 0 cleanup_done cleanup_done
  FileOpen $Manifest "$PayloadRoot\installed-files.txt" r
  IfErrors cleanup_done
  !insertmacro CANONICAL $PayloadRoot "$PayloadRoot"
  StrCmp $PayloadRoot "" cleanup_abort
  StrCpy $Prefix "$PayloadRoot\"
  StrLen $PrefixLength $Prefix
  StrCpy $Counter 0
cleanup_read:
  ClearErrors
  FileRead $Manifest $Line
  IfErrors cleanup_end
  IntOp $Counter $Counter + 1
  IntCmp $Counter 5000 0 0 cleanup_end
  ${TrimNewLines} $Line $Line
  StrCmp $Line "" cleanup_read
  !insertmacro CANONICAL $FullPath "$PayloadRoot\$Line"
  StrCpy $0 $FullPath $PrefixLength
  StrCmp $0 $Prefix 0 cleanup_read
  ${GetParent} $FullPath $Parent
cleanup_check_parent:
  StrCpy $6 $Parent
  System::Call 'kernel32::GetFileAttributesW(w r6)i.r0'
  IntOp $0 $0 & 0x400
  IntCmp $0 0 0 cleanup_read cleanup_read
  StrCmp $Parent $PayloadRoot cleanup_delete
  ${GetParent} $Parent $Parent
  Goto cleanup_check_parent
cleanup_delete:
  Delete "$FullPath"
  ${GetParent} $FullPath $Parent
cleanup_dirs:
  StrCmp $Parent $PayloadRoot cleanup_read
  RMDir "$Parent"
  ${GetParent} $Parent $Parent
  Goto cleanup_dirs
cleanup_end:
  FileClose $Manifest
  Delete "$PayloadRoot\installed-files.txt"
  Delete "$PayloadRoot\.sysbox-payload"
  RMDir "$PayloadRoot"
cleanup_done:
  Return
cleanup_abort:
  FileClose $Manifest
FunctionEnd
!macroend
!insertmacro CLEAN_PAYLOAD ""
!insertmacro CLEAN_PAYLOAD "un."

Function CheckBusy
  System::Call 'kernel32::OpenMutexW(i 0x100000,i 0,w "Local\AbsSystemBox.Desktop")p.r0'
  IntCmp $0 0 check_file
  System::Call 'kernel32::CloseHandle(p r0)'
  Goto busy
check_file:
  IfFileExists "$INSTDIR\app\SysBox.App.exe" 0 not_busy
  ClearErrors
  FileOpen $0 "$INSTDIR\app\SysBox.App.exe" a
  IfErrors busy
  FileClose $0
  Goto not_busy
busy:
  MessageBox MB_OK|MB_ICONEXCLAMATION "请先关闭 AbsSystemBox，再安装或更新。" /SD IDOK
  SetErrorLevel 4
  Abort
not_busy:
FunctionEnd

Function ValidateRoot
  StrCmp $INSTDIR "" invalid_root
  !insertmacro CANONICAL $INSTDIR "$INSTDIR"
  StrCmp $INSTDIR "" invalid_root
  ReadRegStr $ExistingRoot HKCU "${UNINSTALL_KEY}" "InstallLocation"
  StrCmp $ExistingRoot "" no_prior_root
  !insertmacro CANONICAL $ExistingRoot "$ExistingRoot"
  StrCmp $INSTDIR $ExistingRoot 0 invalid_root
no_prior_root:
  IfFileExists "$INSTDIR\.sysbox-install" existing_root
  ClearErrors
  FindFirst $0 $1 "$INSTDIR\*"
  IfErrors valid_root
empty_loop:
  StrCmp $1 "" empty_valid
  StrCmp $1 "." empty_next
  StrCmp $1 ".." empty_next invalid_root_close
empty_next:
  FindNext $0 $1
  IfErrors empty_valid
  Goto empty_loop
empty_valid:
  FindClose $0
  Goto valid_root
invalid_root_close:
  FindClose $0
  Goto invalid_root
existing_root:
  FileOpen $0 "$INSTDIR\.sysbox-install" r
  FileRead $0 $1
  FileClose $0
  ${TrimNewLines} $1 $1
  StrCmp $1 "${PRODUCT}" 0 invalid_root
  StrCpy $6 $INSTDIR
  System::Call 'kernel32::GetFileAttributesW(w r6)i.r0'
  IntOp $0 $0 & 0x400
  IntCmp $0 0 valid_root invalid_root invalid_root
invalid_root:
  MessageBox MB_OK|MB_ICONSTOP "请选择空目录或本应用已有的安装目录；不会覆盖其他文件。" /SD IDOK
  SetErrorLevel 5
  Abort
valid_root:
FunctionEnd

Function .onInit
  SetShellVarContext current
  SetRegView 64
  IfSilent silent_license interactive_license
silent_license:
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/ACCEPTLICENSE" $1
  IfErrors missing_license interactive_license
missing_license:
  SetErrorLevel 9
  Abort
interactive_license:
  ${IfNot} ${RunningX64}
    MessageBox MB_OK "需要 Windows 11 x64。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  ReadRegStr $0 HKLM "SOFTWARE\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
  IntCmp $0 22000 os_ok unsupported os_ok
unsupported:
  MessageBox MB_OK "需要 Windows 11（版本 22000 或更新）。" /SD IDOK
  SetErrorLevel 2
  Abort
os_ok:
  System::Call 'kernel32::CreateMutexW(p 0,i 0,w "Local\AbsSystemBox.Setup")p.r0 ?e'
  Pop $1
  StrCpy $InstallLock $0
  IntCmp $0 0 setup_busy
  IntCmp $1 183 0 setup_ready setup_ready
setup_busy:
  MessageBox MB_OK "已有安装程序正在运行。" /SD IDOK
  SetErrorLevel 6
  Abort
setup_ready:
  ReadRegStr $PriorVersion HKCU "${UNINSTALL_KEY}" "WindowsVersion"
  StrCmp $PriorVersion "" version_ok
  ${VersionCompare} "${WINDOWS_VERSION}" "$PriorVersion" $0
  StrCmp $0 "2" 0 version_ok
  MessageBox MB_OK|MB_ICONSTOP "已安装更高版本，已阻止降级。规则版本回滚请在应用内完成。" /SD IDOK
  SetErrorLevel 3
  Abort
version_ok:
  Call CheckBusy
FunctionEnd

Section "安装/更新"
  Call ValidateRoot
  Call CheckBusy
  CreateDirectory "$INSTDIR"
  FileOpen $0 "$INSTDIR\.sysbox-install" w
  FileWrite $0 "${PRODUCT}$\r$\n"
  FileClose $0
  ; Recover an interrupted directory switch, without changing user configuration.
  IfFileExists "$INSTDIR\app\SysBox.App.exe" recovered
  IfFileExists "$INSTDIR\app.previous\SysBox.App.exe" 0 recovered
  Rename "$INSTDIR\app.previous" "$INSTDIR\app"
recovered:
  StrCpy $PayloadRoot "$INSTDIR\app.previous"
  Call CleanPayload
  IfFileExists "$INSTDIR\app.previous\*" 0 previous_clear
  MessageBox MB_OK "旧版本备份中有未归属文件，请先检查 app.previous。已保留文件并停止更新。" /SD IDOK
  SetErrorLevel 7
  Abort
previous_clear:
  StrCpy $PayloadRoot "$INSTDIR\app.incoming"
  Call CleanPayload
  IfFileExists "$INSTDIR\app.incoming\*" 0 incoming_clear
  MessageBox MB_OK "暂存目录有未归属文件，已停止更新。" /SD IDOK
  SetErrorLevel 7
  Abort
incoming_clear:
  ClearErrors
  SetOutPath "$INSTDIR\app.incoming"
  File /oname=installed-files.txt "${MANIFEST}"
  FileOpen $0 "$INSTDIR\app.incoming\.sysbox-payload" w
  FileWrite $0 "${PRODUCT}$\r$\n"
  FileClose $0
  File /r "${APPDIR}\*"
  IfErrors stage_failed
  SetOutPath "$INSTDIR"
  ClearErrors
  IfFileExists "$INSTDIR\app\*" 0 switch_new
  Rename "$INSTDIR\app" "$INSTDIR\app.previous"
  IfErrors stage_failed
switch_new:
  Rename "$INSTDIR\app.incoming" "$INSTDIR\app"
  IfErrors switch_failed
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\AbsSystemBox"
  CreateShortcut "$SMPROGRAMS\AbsSystemBox\AbsSystemBox.lnk" "$INSTDIR\app\SysBox.App.exe"
  CreateShortcut "$SMPROGRAMS\AbsSystemBox\卸载.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "AbsSystemBox"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "WindowsVersion" "${WINDOWS_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "Johnson-Tang2019"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\app\SysBox.App.exe"
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  StrCpy $PayloadRoot "$INSTDIR\app.previous"
  Call CleanPayload
  SetErrorLevel 0
  Goto install_done
switch_failed:
  Rename "$INSTDIR\app.previous" "$INSTDIR\app"
stage_failed:
  MessageBox MB_OK|MB_ICONSTOP "安装失败，原版本和用户规则保留。请检查空间/文件占用后重试。" /SD IDOK
  SetErrorLevel 8
  Abort
install_done:
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  System::Call 'kernel32::CreateMutexW(p 0,i 0,w "Local\AbsSystemBox.Setup")p.r0 ?e'
  Pop $1
  StrCpy $InstallLock $0
  IntCmp $0 0 un_setup_busy
  IntCmp $1 183 un_setup_busy un_setup_ready un_setup_ready
un_setup_busy:
  SetErrorLevel 6
  Abort
un_setup_ready:
  System::Call 'kernel32::OpenMutexW(i 0x100000,i 0,w "Local\AbsSystemBox.Desktop")p.r0'
  IntCmp $0 0 un_ready
  System::Call 'kernel32::CloseHandle(p r0)'
  MessageBox MB_OK "请先关闭 AbsSystemBox。" /SD IDOK
  SetErrorLevel 4
  Abort
un_ready:
FunctionEnd

Section "Uninstall"
  FileOpen $0 "$INSTDIR\.sysbox-install" r
  IfErrors un_done
  FileRead $0 $1
  FileClose $0
  ${TrimNewLines} $1 $1
  StrCmp $1 "${PRODUCT}" 0 un_done
  StrCpy $PayloadRoot "$INSTDIR\app"
  Call un.CleanPayload
  StrCpy $PayloadRoot "$INSTDIR\app.previous"
  Call un.CleanPayload
  StrCpy $PayloadRoot "$INSTDIR\app.incoming"
  Call un.CleanPayload
  Delete "$SMPROGRAMS\AbsSystemBox\AbsSystemBox.lnk"
  Delete "$SMPROGRAMS\AbsSystemBox\卸载.lnk"
  RMDir "$SMPROGRAMS\AbsSystemBox"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  Delete "$INSTDIR\.sysbox-install"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  ; %LOCALAPPDATA%\SysBox and unknown files are intentionally preserved.
un_done:
SectionEnd
