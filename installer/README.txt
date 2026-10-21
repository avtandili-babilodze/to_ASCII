ASCII Vision - real-time ASCII video engine for Windows
=======================================================

INSTALL
  1. Extract the whole zip to any folder (right-click > Extract All).
  2. Double-click Install.cmd.
     If Windows shows "Windows protected your PC", click "More info" > "Run anyway"
     (the files are not code-signed).
  3. When it finishes, start ASCII Vision from the desktop or Start menu shortcut.

  The installer checks what is already on your PC and only installs what is missing:
    - Windows 10 version 1903 or newer / Windows 11 (64-bit)       (checked only)
    - .NET 9 Desktop Runtime (x64)                                  (from Microsoft)
    - Visual C++ 2015-2022 Redistributable (x64)                    (from Microsoft)
    - FFmpeg with H.264 support, for MP4 playback/recording/export  (from gyan.dev)
  Installing the .NET or Visual C++ runtime shows a Windows permission (UAC) prompt.
  The app itself is installed for your user only, in:
    %LOCALAPPDATA%\Programs\ASCII Vision

  Options (run from PowerShell in this folder):
    .\install.ps1 -NoDesktopShortcut   no desktop shortcut
    .\install.ps1 -NoLaunch            do not start the app at the end
    .\install.ps1 -SkipPrerequisites   only copy the app
    .\install.ps1 -InstallDir "D:\Apps\ASCII Vision"

PORTABLE USE
  If the prerequisites are already installed you can also run app\AsciiVision.exe directly.

UNINSTALL
  Settings > Apps > Installed apps > ASCII Vision > Uninstall, or run Uninstall.cmd.
  The .NET and Visual C++ runtimes stay installed because other programs may use them.

Settings: %APPDATA%\AsciiVision      Logs: %LOCALAPPDATA%\AsciiVision\logs
