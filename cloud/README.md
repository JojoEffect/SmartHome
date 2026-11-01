# SmartHome in der Claude-Code-Cloud

Diese Anleitung richtet die **Cloud-Umgebung** (Linux-Container, in dem Claude-Code-Cloud-Sessions
laufen) so ein, dass jede neue Session sofort arbeitsfähig ist. Alles darin wurde am
2026-11-01 in einer echten Cloud-Session gemessen, nicht angenommen.

| Datei | Wofür |
|---|---|
| [`setup.sh`](setup.sh) | Der Inhalt für das Feld **Setup script** der Umgebung. Installiert PowerShell 7, .NET SDK 8, Mosquitto, `nanoclr`. Idempotent. |
| [`check.sh`](check.sh) | Prüft in einem Durchgang, ob die Umgebung stimmt (`--tests` führt zusätzlich die Skript-Tests aus). Ändert nichts. |

## Kurzfassung

1. Umgebung öffnen: Menü der Cloud-Umgebung in der Titelleiste der Session → **Edit**.
2. **Setup script**: den kompletten Inhalt von [`setup.sh`](setup.sh) einfügen.
3. **Network access**: siehe [Netzwerk](#netzwerk). Mit der Standardeinstellung läuft alles.
4. **Environment variables / Secrets**: nichts eintragen (siehe [unten](#variablen-und-secrets)).
5. Neue Session starten und `bash cloud/check.sh --tests` ausführen. Erwartet: `Result: ready`
   und `345 passed, 0 failed, 17 skipped`.

## Was in der Cloud geht und was nicht

Die Skripte in `scripts/` sind für einen Windows-Schreibtisch mit ESP32 gebaut. Die Cloud ist
ein Linux-Container ohne COM-Port. Daraus folgt:

| Arbeit | Cloud? | Beleg / Grund |
|---|---|---|
| Code lesen, ändern, committen, PR anlegen und beobachten | **Ja** | Git und die GitHub-MCP-Tools funktionieren. |
| `scripts\Run-ScriptTests.ps1` (Host-seitige Skript-Tests) | **Ja** | Unter `pwsh` 7.6 auf Linux: 345 bestanden, 17 übersprungen (brauchen `cmd.exe`). Seit PR #165 laufen sie auf 5.1 und 7. |
| `scripts\Sync-NanoFrameworkRepos.ps1` (14 Schwesterrepos) | **Ja** | Gemessen: 1,6 GB, rund 5 Minuten. Klont `main`; die Tag-Pins aus `CLAUDE.md` setzt der Subagent `nanoframework-sync`. |
| `tools\DeviceDebugMonitor` bauen (`dotnet build`) | **Ja** | Baut mit dem .NET SDK 8 aus dem Ubuntu-Archiv (9 s). Nützt ohne Gerät aber nur als Kompilierprüfung. |
| Mosquitto-Werkzeuge (`mosquitto`, `mosquitto_sub`, `mosquitto_pub`) | Ja, von Hand | Für Experimente. Die Skripte rufen `mosquitto.exe` auf, `Start-DevEnv.ps1` ist Windows-only (`Start-Process -WindowStyle` gibt es unter `pwsh` auf Linux nicht). |
| `nanoclr` (virtuelles Gerät, CLI) | Startet | Nur Versionen ab 1.1.3xx laufen unter Linux; 1.1.122, das die CI auf Windows pinnt, stürzt beim Start ab. Ohne gebaute Test-Assembly nützt es nichts. |
| `.nfproj` bauen, `Run-Tests.ps1`, Unit-Tests | **Nein** | Siehe [Warum C# nicht in der Cloud gebaut wird](#warum-c-nicht-in-der-cloud-gebaut-wird). **Die CI auf `windows-latest` ist hier die Prüfinstanz.** |
| Deploy, Flashen, Integrationstests, Seriell-Mitschnitt | **Nein** | Kein ESP32, kein COM-Port. Ohnehin nur nach Rückfrage (Hardware-Regel in `CLAUDE.md`). |
| `Test-Setup.ps1`, `Start-DevEnv.ps1`, `Deploy-*.ps1` | **Nein** | Windows-Pfade (`C:\…\mosquitto.exe`, `vswhere`, `.exe`). `Test-Setup.ps1` bricht unter Linux bei `Join-Path 'C:\…'` ab. Statt dessen: `cloud/check.sh`. |
| `scripts\Get-BacklogPriorities.ps1`, `Clean-GitBranches.ps1` | **Nein, vorerst** | Sie nutzen `gh issue list` / `gh pr list` (GraphQL), und das blockiert die Cloud mit HTTP 403. REST geht. Issue #166. |

### Warum C# nicht in der Cloud gebaut wird

Das wurde ernsthaft versucht, damit sich nicht jemand noch einmal darauf einlässt:

- Jedes `.nfproj` importiert seine Build-Targets aus `$(MSBuildExtensionsPath)\nanoFramework\v1.0\`.
  Dort liegen sie nur, wo die Visual-Studio-Erweiterung installiert ist. CI kopiert sie dafür aus
  dem VSIX (`.github/actions/setup-nanoframework`).
- Das VSIX ist ein **GitHub-Release-Asset**, und die Cloud blockiert den Download (HTTP 403).
- Die Targets selbst liegen im öffentlichen Repo `nf-Visual-Studio-extension` und sind klonbar. Aber
  `nanoFramework.Tools.BuildTasks` (löst Laufzeit-Abhängigkeiten auf, erzeugt Ressourcen und die
  Binärausgabe) gibt es **nur als Quellcode für .NET Framework 4.7.2**; das NuGet-Paket gleichen
  Namens enthält keine DLL.
- Der Metadata-Prozessor hat auf NuGet eine `net8.0`-Variante (`4.0.0-preview.108`). Sie ist eine
  Vorschau und gehört zu einem anderen Projektsystem als das, was CI und Visual Studio benutzen.
- Ubuntu 24.04 hat kein `msbuild`-Paket für Mono.

Ein Linux-Build wäre also ein Port der nanoFramework-Build-Tasks nach .NET 8 plus ein
Ergebnis, das man gegen die CI nachprüfen müsste. Das ist ein Projekt, keine Konfiguration, und
gehört nicht in diese Umgebung.

## Einrichtung im Detail

### Setup script

Inhalt von [`setup.sh`](setup.sh) in das Feld **Setup script** einfügen. Es läuft als root, beginnt
ein Verzeichnis **über** dem Repository und liest weder Variablen noch Secrets der Umgebung. Deshalb
stehen seine Schalter als einfache Zuweisungen am Anfang der Datei:

| Schalter | Standard | Bedeutung |
|---|---|---|
| `INSTALL_POWERSHELL` | `1` | PowerShell 7 aus dem Microsoft-Repo. Ohne sie läuft kein Skript. Fehlt `pwsh` am Ende, endet das Setup mit Exit 1. |
| `INSTALL_DOTNET_SDK` | `1` | `dotnet-sdk-8.0` aus dem Ubuntu-Archiv. |
| `INSTALL_MOSQUITTO` | `1` | Broker und Clients. Der Dienst wird **nicht** gestartet. |
| `INSTALL_NANOCLR` | `1` | `nanoclr` (aktuelle Version) als globales Tool, verlinkt nach `/usr/local/bin`. |
| `INSTALL_NANOFF` / `NANOFF_VERSION` | `0` / `2.5.131` | Der Flasher. Ohne Gerät nutzlos. |
| `SEED_LOCAL_ENV` | `1` | Legt `scripts/local.env.ps1` und `scripts/nanoFramework.local.env.ps1` aus den Vorlagen an, wenn sie fehlen (beide sind git-ignoriert). |
| `SYNC_SIBLINGS` | `0` | Schwesterrepos gleich mitklonen. Aus, weil es 1,6 GB und rund 5 Minuten kostet. |

Gemessen auf einer Umgebung ohne jede dieser Pakete: **22 Sekunden**; ein zweiter Lauf ändert nichts
und dauert 0,3 s. Das Skript funktioniert auch eingefügt ohne Repo daneben (dann entfallen die
Repo-Schritte) und aus einem Verzeichnis über dem Repo (es findet `./SmartHome` selbst).

Schlägt `apt` fehl, bricht das Skript mit Exit 1 ab und gibt die letzten 40 Zeilen des Logs aus. Der
häufigste Grund ist ein Host, den die Netzwerkrichtlinie nicht freigibt.

### Netzwerk

Gemessen funktionieren mit der Standardrichtlinie dieser Umgebung:

| Host | Wofür | Status |
|---|---|---|
| `archive.ubuntu.com` | `dotnet-sdk-8.0`, Mosquitto | erreichbar |
| `packages.microsoft.com` | PowerShell | erreichbar |
| `api.nuget.org` | `nanoclr`, `nanoff`, NuGet für `DeviceDebugMonitor` | erreichbar |
| `github.com` (Git, nur klonen) | Repo und Schwesterrepos | erreichbar |

Die Webseite `github.com/…` antwortet mit 403, obwohl `git clone` funktioniert. `check.sh` prüft
deshalb die Git-Schnittstelle und nicht die Seite.

Blockiert, und für diese Einrichtung nicht nötig: `builds.dotnet.microsoft.com`, `dotnetcli.azureedge.net`,
`aka.ms` (Microsofts `dotnet-install`-Weg, deshalb kommt .NET aus dem Ubuntu-Archiv),
GitHub-Release-Assets und `api.github.com/…/releases` (deshalb kein VSIX, siehe oben).

Stellst du die Umgebung auf **Limited**, trage die vier Hosts unter *Allowed domains* ein und lass
**Allow package managers** angehakt. Ohne Freigabe schlägt das Setup an der Stelle fehl, die
`apt failed` meldet.

### Variablen und Secrets

Es ist nichts einzutragen.

- **`GH_TOKEN` nicht setzen.** Die Session bringt ein eigenes mit. `gh auth status` meldet es als
  ungültig, aber `gh api …` (REST) funktioniert, und die GitHub-MCP-Tools brauchen es nicht. Ein
  eigener Wert würde diese Verdrahtung ersetzen; ob `gh` und Git danach noch funktionieren, wurde
  nicht geprüft.
- Das Setup-Skript kann Secrets ohnehin nicht lesen.
- `SMARTHOME_COM_PORT` & Co. gelten nur für Hardware-Skripte und fehlen hier mit Absicht.

### Warum kein SessionStart-Hook

Ein Hook (`.claude/settings.json`) würde dieselbe Arbeit bei jedem Sitzungsstart erneut anstoßen. Das
Setup-Skript der Umgebung ist dafür der richtige Ort, und der bestehende Windows-Hook (`powershell.exe`,
seedet Worktrees) bleibt unverändert. Ob er in der Cloud eine Fehlermeldung erzeugt, weil es dort
kein `powershell.exe` gibt, ließ sich nicht prüfen; dann wäre sie harmlos, denn er hat dort nichts zu tun.

## Prüfen

```bash
bash cloud/check.sh            # Werkzeuge, Repo, Netzwerk, GitHub
bash cloud/check.sh --tests    # zusätzlich scripts/Run-ScriptTests.ps1
```

Ausgabe in drei Stufen: `OK`, `WARN` (optional und fehlt) und `FAIL` (nötig und fehlt, Exit 1).
`info`-Zeilen sind Dinge, die in der Cloud absichtlich blockiert sind, damit niemand sie versucht.

## Im Alltag

```bash
pwsh -NoProfile -File scripts/Run-ScriptTests.ps1          # nach jeder Änderung unter scripts/
pwsh -NoProfile -File scripts/Sync-NanoFrameworkRepos.ps1  # vor Framework-/Firmware-Debugging
```

- Pull Requests öffnen und beobachten geht über die GitHub-MCP-Tools. `gh api …` geht für REST,
  `gh issue list` und `gh pr list` nicht (#166).
- **Grün in der CI ist die einzige Prüfung für C#.** Die PR-Beschreibung muss sagen, dass nichts auf
  Hardware lief.
- Skript-Änderungen sollten unter `pwsh` auf Linux **und** der Windows-CI bestehen. Fälle, die
  `cmd.exe` brauchen, werden mit `Describe`/`It -Skip (Get-WindowsOnlyReason '…')` übersprungen und
  in der Zusammenfassung ausgewiesen.

## Wenn etwas nicht stimmt

| Symptom | Ursache | Abhilfe |
|---|---|---|
| `pwsh: command not found` | Setup lief nicht oder scheiterte | Setup script prüfen, `bash cloud/setup.sh` von Hand ausführen |
| `apt failed`, `Unable to locate package` | Host nicht freigegeben | Netzwerk-Tabelle oben; Hosts unter *Allowed domains* eintragen |
| `nanoclr` stürzt mit `UriFormatException` ab | Version 1.1.122 ist installiert | `dotnet tool update -g nanoclr` (aktuelle Version läuft) |
| `nanoclr: command not found` | `~/.dotnet/tools` ist in der Cloud nicht im PATH | `setup.sh` verlinkt nach `/usr/local/bin`; erneut ausführen |
| `HTTP 403: GitHub GraphQL is not available` | `gh issue list` / `gh pr list` | `gh api repos/…` oder MCP-Tools; Issue #166 |
| `Test-Setup.ps1` bricht bei `Join-Path` auf `C:` ab | Windows-Skript | `bash cloud/check.sh` |
| `Missing: …\scripts\local.env.ps1` | Konfigurationsdateien fehlen | `SEED_LOCAL_ENV=1` im Setup, oder die Vorlagen von Hand kopieren |

## Pflege

- `cloud/*.sh` wird in der CI mit `shellcheck` geprüft. Zeilenenden sind per `.gitattributes` auf LF
  festgelegt, damit ein Windows-Checkout die Skripte nicht unbrauchbar macht.
- Ändert sich das Setup, gilt die Änderung erst, wenn der neue Inhalt von `setup.sh` wieder in die
  Umgebung eingefügt wurde. Das Feld ist eine Kopie, kein Verweis.
- `nanoclr` bleibt absichtlich unbepinnt: Der Test-Adapter ersetzt es ohnehin vor jedem Lauf durch die
  neueste Version (siehe `CLAUDE.md`, „The virtual device's CLR is pinned“), und nur neuere
  Versionen laufen unter Linux.
