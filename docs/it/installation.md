# Installazione

[🇬🇧 English](../en/installation.md) · 🇮🇹 Italiano

mcp-dotNET è un server MCP in .NET. "Installare" significa compilarlo e registrarlo con il tuo client AI. Non serve altro oltre all'SDK .NET.

## Requisiti

- Windows 10/11, Linux o macOS.
- [SDK .NET 10](https://dotnet.microsoft.com/download) (`dotnet --version` ≥ 10.0).
- Un client MCP: Claude Desktop, Claude Code, l'app desktop di ChatGPT, Codex o altri.

## Opzione A — script di installazione (consigliata)

Lo script compila il server in Release e scrive/unisce la configurazione per Claude Code, Claude Desktop e Codex. Prima di ogni modifica viene creato un backup con data e ora; le voci esistenti vengono preservate.

```powershell
# Windows
git clone https://github.com/jumpifequal/mcp-dotnet C:\Tools\mcp-dotnet
cd C:\Tools\mcp-dotnet
powershell -ExecutionPolicy Bypass -File .\Scripts\install.ps1
```

```bash
# Linux / macOS (serve python3 per unire il JSON)
git clone https://github.com/jumpifequal/mcp-dotnet ~/mcp-dotnet
cd ~/mcp-dotnet
./Scripts/install.sh
```

Opzioni utili (PowerShell):

| Opzione | Effetto |
|---|---|
| `-Target claude-code \| claude-desktop \| codex \| all` | quali client configurare (predefinito `all`) |
| `-ReadOnly` | registra con `--read-only` (niente strumenti di patch/rename/save) |
| `-Transport http -Port 5005` | registra il trasporto HTTP invece di stdio (Claude Code) |
| `-NoBuild` | salta la compilazione, usa un binario Release esistente |
| `-NoRegister` | compila soltanto, non tocca alcuna configurazione |

`Scripts\setup.bat` esegue la stessa cosa con un doppio clic.

## Opzione B — configurazione manuale

Compila una volta:

```bash
dotnet build -c Release
```

Il DLL del server è in `bin/mcp-dotnet.dll` (una build Release appiattisce anche `bin/` a questo singolo livello tramite `Scripts/flatten-bin.ps1`, eseguito automaticamente da `Scripts/install.ps1`). Avvialo tramite l'host condiviso `dotnet`.

### Collega il tuo client AI

**Claude Desktop / qualsiasi JSON `mcpServers`** — aggiungi a `claude_desktop_config.json` (Windows: `%APPDATA%\Claude\`, macOS: `~/Library/Application Support/Claude/`):

```json
{
  "mcpServers": {
    "dotnet": {
      "command": "dotnet",
      "args": ["C:\\Tools\\mcp-dotnet\\src\\MCPPOC\\bin\\Release\\net10.0\\mcp-dotnet.dll", "--read-only"]
    }
  }
}
```

Togli `--read-only` per abilitare la patch. Riavvia completamente Claude Desktop.

**Claude Code**

```bash
claude mcp add dotnet -- dotnet /percorso/bin/mcp-dotnet.dll
```

**Codex** — aggiungi a `~/.codex/config.toml`:

```toml
[mcp_servers.dotnet]
command = "dotnet"
args = ["/percorso/bin/mcp-dotnet.dll"]
```

### Client OpenAI

L'app desktop di ChatGPT e Codex (CLI ed estensione IDE) parlano MCP via stdio; usa la configurazione Codex qui sopra, o le impostazioni "Developer / MCP servers" dell'app con gli stessi `command`/`args`. ChatGPT sul web non può usare un server locale.

## Installazione come strumento .NET

```bash
dotnet pack src/MCPPOC/MCPPOC.csproj -c Release -o artifacts
dotnet tool install -g mcp-dotnet --add-source ./artifacts
```

Il comando diventa semplicemente `mcp-dotnet`, e le configurazioni possono usare `"command": "mcp-dotnet"` senza percorso del DLL.

## Pacchetto pronto all'uso

Per consegnare a qualcuno una build eseguibile senza SDK, crea una distribuzione:

```powershell
powershell -ExecutionPolicy Bypass -File .\Scripts\pack-dist.ps1
```

Crea `Distribution/mcp-dotnet/` (binari pubblicati + Scripts\ + documentazione) e uno zip. Il destinatario lo scompatta ed esegue `Scripts\install.ps1 -NoBuild`. Aggiungi `-SelfContained -Runtime win-x64` per includere il runtime .NET così da non richiedere installazioni. Vedi [Personalizzazione → Distribuzione](tweaking.md#distribuzione).

## Trasporto HTTP

Per Claude Code o un server condiviso, avvia il trasporto HTTP invece di stdio:

```bash
# genera un token una volta
dotnet bin/mcp-dotnet.dll --gen-token
# avvia il server (loopback, autenticazione bearer)
set MCP_DOTNET_HTTP_TOKEN=<token>        # export su Linux/macOS
dotnet .../mcp-dotnet.dll --transport http --port 5005
```

Registralo in Claude Code:

```bash
claude mcp add dotnet --transport http http://127.0.0.1:5005/mcp --header "Authorization: Bearer <token>"
```

Oppure lascia fare a installer e console: `Scripts\install.ps1 -Transport http -Target claude-code`, poi `Scripts\console.bat` per avviare e sorvegliare il server. Dettagli e TLS in [Personalizzazione → Trasporti](tweaking.md#trasporti-stdio-e-http).

## Verifica

Avvia una conversazione e chiedi al client di *caricare un assembly .NET ed elencarne i namespace*. Da terminale puoi fare uno smoke test del server stdio:

```bash
dotnet .../mcp-dotnet.dll --help
```

## Aggiornamento

```bash
git pull
dotnet build -c Release
```

La configurazione del client non cambia (punta allo stesso DLL).

## Disinstallazione

Rimuovi la voce del server dalla configurazione del client (o `claude mcp remove dotnet`), elimina il clone e — se hai installato lo strumento — `dotnet tool uninstall -g mcp-dotnet`.
