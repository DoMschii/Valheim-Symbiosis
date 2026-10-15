GitHub Setup Hotfix
===================
Setup-GitHub.ps1 erkennt gh.exe jetzt auch direkt nach einer winget-Installation,
ohne dass PowerShell neu gestartet werden muss.

Falls winget/GitHub CLI nicht verfuegbar ist, laedt das Setup automatisch eine
portable offizielle GitHub-CLI-Version von github.com/cli/cli herunter und legt
sie unter ReleaseTools\tools\gh ab.

Publish-Final.ps1 verwendet danach automatisch genau diese gespeicherte gh.exe.
