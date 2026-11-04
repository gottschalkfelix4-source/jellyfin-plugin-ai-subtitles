# AI Subtitle Translator – Jellyfin-Plugin

Übersetzt die **vorhandenen Untertitel** eines Films oder einer Folge **live im Player** in eine beliebige Sprache. Angebunden wird ein KI-Modell entweder über einen **OpenAI-kompatiblen Endpunkt** (OpenAI, Ollama, LM Studio, OpenRouter, vLLM, LocalAI …) oder direkt über die **Anthropic-API (Claude)**.

- Zielversion: **Jellyfin 12.4** (.NET 10)
- Quelle: alle Text-Untertitel, die Jellyfin kennt – eingebettet (MKV/MP4) oder extern (SRT, ASS/SSA, WebVTT …)
- Bild-Untertitel (PGS, VobSub) lassen sich nicht übersetzen

## Funktionen

**Im Web-Player** erscheint neben dem Untertitel-Symbol ein Übersetzen-Button (Symbol 文A). Das Menü bietet:

- Wahl der Quell-Untertitelspur
- Zielsprache aus einer konfigurierbaren Liste **oder frei eingegeben** (z. B. „Schweizerdeutsch“, „Plattdeutsch“, „pt-BR“)
- Live-Übersetzung: übersetzt wird zuerst der Abschnitt an der aktuellen Position, danach bis zu 2½ Minuten im Voraus. Nach einem Sprung im Film geht es an der neuen Stelle weiter.
- Die Anzeige bleibt auch beim Transcoding synchron (der Transcoding-Versatz wird herausgerechnet). Die Original-Untertitel werden währenddessen ausgeblendet.
- Die gewählte Sprache wird gemerkt; bei der nächsten Folge übersetzt das Plugin automatisch weiter, bis „Aus“ gewählt wird.
- „Als Untertitel speichern“: übersetzt komplett und legt das Ergebnis als normale Untertitelspur am Titel ab. Danach steht es in **allen** Clients zur Verfügung, auch in TV-Apps.
- „SRT herunterladen“

**In „Untertitel suchen“** (Dialog in der Titelansicht, in allen Clients mit Untertitelsuche) erscheint für jede vorhandene Text-Spur ein Eintrag „KI-Übersetzung … → Sprache“. Beim Herunterladen wird komplett übersetzt und gespeichert. Der automatische Untertitel-Download des Servers wird bewusst ignoriert, damit nicht unbemerkt ganze Bibliotheken übersetzt werden.

**Cache:** Jeder übersetzte Abschnitt wird im Datenordner des Plugins auf dem Server gespeichert (Unterordner `translations` unter `plugins/`). Wer eine Stelle noch einmal ansieht oder später speichert, verursacht keine neuen Kosten.

## Installation (Unraid / Docker)

1. Paket bauen (siehe unten) oder den fertigen Ordner `artifacts\AI Subtitle Translator_1.0.0.0` nehmen.
2. Den Ordner in das Plugin-Verzeichnis des Jellyfin-Containers kopieren. Beim offiziellen Image ist das `/config/plugins/`, auf Unraid meist
   `/mnt/user/appdata/jellyfin/plugins/` bzw. `/mnt/user/appdata/jellyfin/config/plugins/` – je nachdem, wohin `/config` gemappt ist.
   Ergebnis: `…/plugins/AI Subtitle Translator_1.0.0.0/` mit drei DLLs und `meta.json`.
3. Container neu starten.
4. Dashboard → Plugins → **AI Subtitle Translator** öffnen, Anbieter einrichten, „Speichern & Verbindung testen“.
5. Den Browser-Tab mit Jellyfin einmal neu laden. Oben auf der Einstellungsseite steht, ob das Player-Skript ausgeliefert wird.

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

| Client | Live im Player | gespeicherte Übersetzung |
|---|---|---|
| Browser (jellyfin-web) | ✔ | ✔ |
| Jellyfin Media Player, Android-App (nutzen die Server-Oberfläche) | ✔ | ✔ |
| Android TV, Swiftfin, Roku, Kodi u. a. native Apps | ✘ | ✔ (über „Untertitel suchen“ oder „Als Untertitel speichern“) |

## Funktionsweise

```
Web-Player ── Button/Menü ─► GET /AiSubtitles/Plan     (Abschnitte + Zeiten)
           ── Position ────► GET /AiSubtitles/Batch    (Abschnitt n übersetzen)
                                 │
Plugin ── ISubtitleEncoder (Jellyfin) → Original als SRT → Abschnitte à 40 Zeilen
       ── Cache-Treffer? ─ ja → sofort zurück
                          └ nein → KI (OpenAI-kompatibel | Anthropic-SDK) → Cache
```

- Das Player-Skript wird beim Ausliefern in die `index.html` des Web-Clients eingefügt (Middleware). Die Datei auf der Platte bleibt unverändert, und das funktioniert auch bei schreibgeschützten Docker-Images.
- Die Abspielposition liest das Skript aus den Fortschrittsmeldungen, die jellyfin-web ohnehin an den Server schickt. So stimmt die Zeit auch beim Transcoding.
- Gleiche Abschnitte, die mehrere Zuschauer zugleich anfordern, werden nur einmal übersetzt.

Falls die Einbindung nicht greift (Hinweis auf der Einstellungsseite), lässt sich das Skript alternativ über das Plugin „JavaScript Injector“ laden: `<server>/AiSubtitles/ClientScript`.

## API

| Methode | Pfad | Zweck |
|---|---|---|
| GET | `/AiSubtitles/ClientScript` | Player-Skript |
| GET | `/AiSubtitles/ClientConfig` | Sprachen, Rechte |
| GET | `/AiSubtitles/Streams/{itemId}?mediaSourceId=` | übersetzbare Spuren |
| GET | `/AiSubtitles/Plan/{itemId}?mediaSourceId=&streamIndex=` | Abschnitte |
| GET | `/AiSubtitles/Batch/{itemId}?…&lang=&batch=&batchSize=` | einen Abschnitt übersetzen |
| GET | `/AiSubtitles/File/{itemId}?…&lang=&format=srt\|vtt` | komplette Übersetzung als Datei |
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

`package.ps1` erzeugt `artifacts\AI Subtitle Translator_<Version>\` (direkt kopierbar) und ein ZIP samt MD5.

Projektstruktur:

```
Jellyfin.Plugin.AiSubtitles/
  Plugin.cs, PluginServiceRegistrator.cs
  Configuration/   Einstellungen + Dashboard-Seite
  Providers/       OpenAI-kompatibel (HTTP), Anthropic (offizielles SDK)
  Subtitles/       SRT/VTT-Parser, Quell-Untertitel, Untertitel-Provider
  Translation/     Abschnitte, Prompt, Cache, Sprachauflösung
  Api/             REST-Controller
  Web/             Player-Skript, Skript-Einbindung
Jellyfin.Plugin.AiSubtitles.Tests/   Parser-, Antwort- und Endpunkt-Tests (lokaler Fake-Server)
```

## Grenzen

- Bild-Untertitel (PGS/VobSub) bräuchten OCR und werden nicht unterstützt.
- „Speichern“ und der Download aus „Untertitel suchen“ übersetzen den ganzen Film in einem Request. Liegt ein Reverse-Proxy mit kurzem Timeout davor (z. B. 60 s), kann der Request abbrechen. Die Übersetzung läuft serverseitig trotzdem weiter und landet im Cache, sodass ein zweiter Versuch sofort fertig ist.
- Das Player-Skript hängt sich an die Oberfläche von jellyfin-web (`.videoOsdBottom .btnSubtitles`, `.videoPlayerContainer`). Größere Umbauten des Web-Clients in künftigen Versionen können Anpassungen nötig machen.
