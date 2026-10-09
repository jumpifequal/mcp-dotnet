# FAQ e risoluzione problemi

[🇬🇧 English](../en/faq.md) · 🇮🇹 Italiano

### Il client non vede il server / non compaiono strumenti

- Verifica che la build esista: `bin/mcp-dotnet.dll`.
- Controlla che il comando parta: `dotnet .../mcp-dotnet.dll --help` deve stampare l'uso.
- Riavvia completamente il client (Claude Desktop va chiuso dalla traybar, non solo con la X).
- Controlla il percorso di configurazione e che `command`/`args` siano corretti. I log vanno su stderr; alzali con `--log-level Information`.

### "No assembly loaded"

Chiama prima `load_assembly` con un percorso assoluto. Ogni strumento usa poi l'assembly attivo, o un id `assembly` esplicito (vedi `list_assemblies`).

### Uno strumento restituisce un errore invece dei dati

Gli errori degli strumenti sono normali risultati MCP con `isError: true` e un messaggio utile (token sconosciuto, tipo non trovato con suggerimenti, IL non valido, ecc.). È voluto: il modello legge il messaggio e riprova.

### Può eseguire il programma o fare debug?

No. mcp-dotNET fa **solo** analisi statica. Legge e patcha metadati e IL; non esegue mai l'assembly. Per l'analisi dinamica usa un debugger.

### Posso decompilare l'intero assembly in una volta?

Sì — `export_project` scrive un progetto C# in stile SDK compilabile in una cartella vuota. Per assembly grandi è meglio che decompilare tipo per tipo; poi fai grep sull'output.

### La patch funziona davvero? Il file patchato gira?

Sì. Il patcher di IL sistema branch, gestori di eccezioni e MaxStack, e `save_assembly` mantiene stabili i token. La suite di test salva assembly patchati, li carica nel runtime .NET e li esegue. Verifica comunque le tue patch: uno stack di valutazione sbilanciato produce un metodo non valido.

### Assembly offuscati?

`get_assembly_info` riporta indizi di offuscatori (ConfuserEx, Dotfuscator, .NET Reactor e altri) e segnali di rinomina/flattening. Puoi comunque esplorare, decompilare e incrociare riferimenti; usa `rename_member` man mano che capisci i membri così la decompilazione successiva si legge meglio. Stringhe/corpi cifrati e decifrati solo a runtime non si recuperano con la sola analisi statica.

### Assembly mixed-mode / nativi?

Metadati gestiti e IL sono disponibili. `get_assembly_info` segnala il mixed-mode; le parti native non vengono decompilate. `save_assembly` riscrive le immagini mixed-mode con il loro codice nativo.

### HTTP: ricevo 401 Unauthorized

Invia `Authorization: Bearer <token>`, dove il token è il valore di `MCP_DOTNET_HTTP_TOKEN` (o il token effimero stampato all'avvio su bind loopback). Usa `--gen-token` per crearne uno.

### HTTP: ricevo 421 Misdirected Request

L'header `Host` non è nell'allow-list (protezione DNS-rebinding). Su bind loopback sono accettati solo `localhost`/`127.0.0.1`/`::1`; aggiungine altri con `--allowed-host`.

### È sicuro analizzare malware?

Leggere e patchare sono operazioni statiche e sicure. Ma stringhe, risorse e codice decompilati sono contenuti di terze parti: non combinare la sessione con strumenti ad alto impatto (email, shell, browser) e tratta come non fidati percorsi e URL referenziati dal campione. Non eseguire mai il campione.

### Quali versioni di .NET può analizzare?

Qualsiasi assembly ECMA-335: .NET Framework, .NET Core, .NET 5–10, Mono. Il server in sé richiede l'SDK .NET 10 per girare.

### Quali sono i limiti?

Niente analisi dinamica/a runtime, nessun recupero della decifratura solo-runtime, nessuna modifica del codice nativo, e la decompilazione di codice molto offuscato può essere parziale. Segnala i problemi con un campione minimo quando possibile.
