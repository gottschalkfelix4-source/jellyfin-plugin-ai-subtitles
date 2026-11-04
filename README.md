# AI Subtitle Translator – Jellyfin-Plugin

Ein Plugin für Jellyfin, das die **vorhandenen Untertitel** eines Films oder einer Folge **live im Player** in eine beliebige Sprache übersetzt. Die Übersetzung erscheint in **jeder Jellyfin-App** – Browser, Fernseher, Apple TV, Handy – als zusätzliche Spur im ganz normalen Untertitelmenü. Angebunden wird ein KI-Modell entweder über einen **OpenAI-kompatiblen Endpunkt** (OpenAI, Ollama, LM Studio, OpenRouter, vLLM, LocalAI …) oder direkt über die **Anthropic-API (Claude)**.

Es ist ein reines Server-Plugin: Es wird über den Plugin-Katalog von Jellyfin installiert und läuft im Jellyfin-Server selbst. Zusätzliche Dienste oder Container braucht es nicht.

## Installation

1. In Jellyfin **Dashboard → Plugins → Repositories** (je nach Version auch *Katalog → Zahnrad*) öffnen und ein neues Repository hinzufügen:
   - **Name:** `AI Subtitle Translator`
   - **URL:**
     ```
     https://raw.githubusercontent.com/gottschalkfelix4-source/jellyfin-plugin-ai-subtitles/main/manifest.json
     ```
2. Im **Katalog** unter *Subtitles* **AI Subtitle Translator** auswählen und installieren.
3. Jellyfin neu starten.
4. Dashboard → Plugins → **AI Subtitle Translator**: Anbieter einrichten und „Speichern & Verbindung testen“ klicken.
5. Einen Titel mit Text-Untertiteln abspielen und im Untertitelmenü **„Deutsch (KI)“** wählen.

Updates erscheinen über dasselbe Repository automatisch im Katalog.

Voraussetzung: **Jellyfin 12.4** oder neuer.

## Überblick

- Zielversion: **Jellyfin 12.4** (.NET 10)
- Quelle: alle Text-Untertitel, die Jellyfin kennt – eingebettet (MKV/MP4) oder extern (SRT, ASS/SSA, WebVTT …)
- Bild-Untertitel (PGS, VobSub) lassen sich nicht übersetzen

## Funktionen

**In allen Apps** (Browser, Samsung- und LG-Fernseher, Android TV, Apple TV, iPhone/iPad, Android, Roku, Kodi …) erscheint die Übersetzung als ganz normale Untertitelspur, z. B. **„Deutsch (KI) – aus English“**. An den Apps muss dafür nichts installiert oder eingestellt werden:

- Die Spur wird für jeden Titel angeboten, der Text-Untertitel hat, aber noch keine Spur in der Zielsprache. Welche Sprachen als Spur erscheinen, steht in den Einstellungen unter *Untertitelspuren in allen Apps* (Standard: Deutsch).
- Übersetzt wird erst, wenn jemand die Spur auswählt. Fertige Übersetzungen kommen danach sofort aus dem Cache – auch für andere Benutzer und Geräte.
- **Automatische Auswahl:** Würde Jellyfin Untertitel in einer anderen Sprache einschalten als der bevorzugten Untertitelsprache des Benutzers, wird stattdessen die Übersetzung gewählt.
- **Serien:** Ist eine Folge fertig, wird die nächste Folge im Hintergrund übersetzt, damit ihre Untertitel beim Weiterschauen sofort da sind.
- **Erstes Abspielen:** Apps laden eine Untertitelspur immer als ganze Datei. Beim ersten Mal erscheinen die Untertitel deshalb erst, wenn der ganze Titel übersetzt ist – bei einem Spielfilm je nach Modell etwa 1–5 Minuten (schneller mit `claude-haiku-5-5` oder mehr *parallelen KI-Anfragen*). Danach ist die Spur sofort da.

**In „Untertitel suchen“** (Dialog in der Titelansicht, in allen Clients mit Untertitelsuche) erscheint für jede vorhandene Text-Spur ein Eintrag „KI-Übersetzung … → Sprache“. Beim Herunterladen wird komplett übersetzt und gespeichert. Der automatische Untertitel-Download des Servers wird bewusst ignoriert, damit nicht unbemerkt ganze Bibliotheken übersetzt werden.

**Cache:** Jeder übersetzte Abschnitt wird im Datenordner des Plugins auf dem Server gespeichert (Unterordner `translations` unter `plugins/`). Wer eine Stelle noch einmal ansieht oder später speichert, verursacht keine neuen Kosten.

### Manuelle Installation (ohne Repository)

Das ZIP der gewünschten Version unter [Releases](https://github.com/gottschalkfelix4-source/jellyfin-plugin-ai-subtitles/releases) herunterladen und in einen Ordner `AI Subtitle Translator_<Version>` im Plugin-Verzeichnis von Jellyfin entpacken (`<Jellyfin-Konfiguration>/plugins/`). Danach Jellyfin neu starten.

## Einrichtung

### Anthropic (Claude)

| Feld | Empfehlung |
|---|---|
| API-Schlüssel | aus console.anthropic.com |
| Modell | `claude-opus-5-5` (Standard, beste Qualität) · `claude-haiku-5-5` (deutlich günstiger und schneller) · `claude-sonnet-5-5` |
| Effort | `low` – für Untertitel ausreichend und am schnellsten |
| Endpunkt | `https://api.anthropic.com` (nur für Proxy/Gateway ändern) |
| Fallback bei Ablehnung | an. Lehnt das Modell einen Abschnitt aus Richtlinien-Gründen ab, versucht die API ein Ersatzmodell. Bei Proxys, die das Beta-Feld nicht kennen, ausschalten. |

Die Antworten werden per strukturierter Ausgabe (JSON-Schema) angefordert. Lehnt das Modell dennoch ab, bleibt an der Stelle der Originaltext stehen und im Player erscheint ein Hinweis.

Grobe Kosten für einen Spielfilm mit ca. 1.500 Untertitelzeilen: mit `claude-opus-5-5` etwa 1–2 US-Dollar, mit `claude-haiku-5-5` wenige Cent. Die Werte sind geschätzt und hängen von Textmenge und Effort ab.

### OpenAI-kompatibel

| Server | Basis-URL | Modell (Beispiel) |
|---|---|---|
| OpenAI | `https://api.openai.com/v1` | `gpt-4o-mini` |
| Ollama | `http://<host>:11434/v1` | `qwen2.5:14b` |
| LM Studio | `http://<host>:1234/v1` | Name aus LM Studio |
| OpenRouter | `https://openrouter.ai/api/v1` | `anthropic/claude-haiku-5-5` u. a. |

- **JSON-Modus** sendet `response_format: json_object`. Ausschalten, falls der Server das ablehnt.
- **Temperatur** leer lassen, wenn das Modell den Parameter nicht akzeptiert.
- `<think>…</think>`-Blöcke lokaler Reasoning-Modelle werden automatisch entfernt.

### Übersetzung

- **Zeilen pro KI-Anfrage** (Standard 40): kleiner = schnellerer Start, größer = weniger Anfragen.
- **Kontextzeilen** (Standard 6): vorherige Zeilen, die als Kontext mitgehen, damit Anrede und Begriffe konsistent bleiben.
- **Parallele KI-Anfragen** (Standard 3): Grenze für den ganzen Server.
- **Zusätzliche Anweisungen**: z. B. „Duzen statt siezen“.

## Welche Clients können was?

| Client | Spur „Sprache (KI)“ im Untertitelmenü |
|---|---|
| Browser (jellyfin-web), Jellyfin Media Player, Android-App, LG webOS | ✔ |
| Samsung Tizen, Android TV / Fire TV, Swiftfin (iPhone, iPad, Apple TV), Roku, Kodi u. a. | ✔ |

Apps, die keine externen Text-Untertitel laden können (sie melden das in ihrem Geräteprofil), bekommen die Spur nicht angezeigt.

## Funktionsweise

```
App ── PlaybackInfo ──────────► Server-Antwort + Spur „Deutsch (KI)“ (Index ≥ 100000)
    ── Spur gewählt ──────────► GET /Videos/{id}/{quelle}/Subtitles/{index}/0/Stream.srt
                                      │ (vom Plugin beantwortet)
Plugin ── ISubtitleEncoder (Jellyfin) → Original als SRT → Abschnitte à 40 Zeilen
       ── Cache-Treffer? ─ ja → sofort zurück
                          └ nein → KI (OpenAI-kompatibel | Anthropic-SDK) → Cache
```

- Gleiche Abschnitte, die mehrere Zuschauer zugleich anfordern, werden nur einmal übersetzt.
- Eine Middleware ergänzt die Antworten von `/Items/{id}/PlaybackInfo` und `/Items/{id}` um zusätzliche Untertitelspuren mit eigenen Indizes ab 100000 (externe SRT- oder WebVTT-Datei, passend zum Geräteprofil der App). Fragt eine App `/Videos/{id}/{quelle}/Subtitles/{index}/…/Stream.srt` für einen solchen Index an, liefert das Plugin die Übersetzung: abschnittsweise in Reihenfolge, mit Leerzeilen als Keep-alive, damit Player mit kurzem Lese-Timeout nicht abbrechen. Die Übersetzung läuft weiter, auch wenn die App die Verbindung trennt. Jellyfin selbst bekommt den virtuellen Index nie zu sehen (er wird in Anfragen durch „keine Untertitel“ ersetzt), damit der Server nicht versucht, eine nicht vorhandene Spur einzubrennen.

## API

| Methode | Pfad | Zweck |
|---|---|---|
| GET | `/AiSubtitles/Streams/{itemId}?mediaSourceId=` | übersetzbare Spuren |
| GET | `/AiSubtitles/File/{itemId}?…&lang=&format=srt\|vtt` | komplette Übersetzung als Datei |
| GET | `/AiSubtitles/AppTrack/{itemId}/{mediaSourceId}/{index}/Stream.srt\|vtt` | Untertitelspur (wird über `/Videos/…/Subtitles/{index}/…` erreicht) |
| POST | `/AiSubtitles/Save/{itemId}?…&lang=` | komplett übersetzen und als Untertitel speichern |
| POST | `/AiSubtitles/Test?lang=` | Verbindungstest (Admin) |
| GET/POST | `/AiSubtitles/CacheInfo`, `/AiSubtitles/ClearCache` | Cache (Admin) |

## Entwicklung

Voraussetzung: .NET 10 SDK.

```powershell
dotnet build
dotnet test
powershell -ExecutionPolicy Bypass -File .\package.ps1 -Version 1.0.0.0
```

`package.ps1` erzeugt `artifacts/AI Subtitle Translator_<Version>/` und das ZIP samt MD5.

### Neue Version veröffentlichen

```powershell
git tag v1.0.1.0
git push origin v1.0.1.0
```

Der Workflow `.github/workflows/release.yml` testet und baut das Plugin, legt ein GitHub-Release mit dem ZIP an und trägt die Version mit Prüfsumme in `manifest.json` ein. Jellyfin-Server, die das Repository eingetragen haben, sehen das Update danach im Katalog. Bei einer neuen Jellyfin-Hauptversion `TargetAbi` in `package.ps1`/`scripts/update-manifest.ps1` und die Paketversionen in den `.csproj`-Dateien anheben.

Projektstruktur:

```
Jellyfin.Plugin.AiSubtitles/
  Plugin.cs, PluginServiceRegistrator.cs
  Configuration/   Einstellungen + Dashboard-Seite
  Providers/       OpenAI-kompatibel (HTTP), Anthropic (offizielles SDK)
  Subtitles/       SRT/VTT-Parser, Quell-Untertitel, Untertitel-Provider
  Translation/     Abschnitte, Prompt, Cache, Sprachauflösung
  Api/             REST-Controller
  AppTracks/       Untertitelspuren (Middleware, Auswahl, Hintergrund-Übersetzung)
Jellyfin.Plugin.AiSubtitles.Tests/   Parser-, Antwort- und Endpunkt-Tests (lokaler Fake-Server)
scripts/update-manifest.ps1          trägt eine Version in manifest.json ein
manifest.json                        Jellyfin-Plugin-Repository (wird vom Release-Workflow gepflegt)
```

## Grenzen

- Bild-Untertitel (PGS/VobSub) bräuchten OCR und werden nicht unterstützt.
- „Speichern“ und der Download aus „Untertitel suchen“ übersetzen den ganzen Film in einem Request. Liegt ein Reverse-Proxy mit kurzem Timeout davor (z. B. 60 s), kann der Request abbrechen. Die Übersetzung läuft serverseitig trotzdem weiter und landet im Cache, sodass ein zweiter Versuch sofort fertig ist.
- Die Spuren für alle Apps beruhen darauf, dass die Apps ihre Untertitelliste aus den Antworten des Servers aufbauen und externe Untertitel über die Server-URL laden. Das tun die offiziellen Apps; Drittanbieter-Apps, die Untertitel auf eigenem Weg laden, zeigen die Spur unter Umständen nicht.
- Ändert man die Reihenfolge der Sprachen unter *Untertitelspuren in allen Apps*, ändern sich die Spurnummern. Laufende Wiedergaben müssen dann neu gestartet werden.
