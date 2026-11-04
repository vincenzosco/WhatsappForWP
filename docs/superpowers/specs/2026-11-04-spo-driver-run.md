# Eseguire il driver stringpack: cosa è stato provato e dove si ferma

Il 4 novembre 2026, dopo aver determinato che gli `.spo` sono archivi Facebook
Superpack di tipo string pack (`2026-11-04-calls-apk-inventory.md`), ho provato a
eseguire il driver invece di reimplementarlo: il driver è codice di Meta, non una
specifica, e l'unico modo onesto di leggerlo è farlo girare.

Questo file registra l'esito. L'esito è: **non ci sono le etichette**, e il
perché è misurato, non supposto.

## Cosa il driver è, letto dal DEX dell'APK

Le classi che pilotano il nativo, con le firme native come stanno in
`classes.dex` (lette dai `class_data_item`, non da codice decompilato):

```
Lcom/facebook/superpack/AssetDecompressor;
  native ([B[B[BLX/022;)[Ljava/lang/String;       decompress
  native ([B[B[B)[Ljava/lang/String;              decompress_from_so
  native ([B[B[B)[Ljava/lang/String;              decompress_legacy
  native ([B[B[BLX/022;LX/01J;LX/00I;)[Ljava/lang/String;  decompress_range
  native ([B[B[BLX/00I;LX/01S;)[Ljava/lang/String; decompress_range_from_so
  native ([B[B[BLX/00I;)[Ljava/lang/String;       decompress_with_ref
  native ()I                                     get_architecture
  native ([B)I                                   sync_directory_to_disk
  native ([B)I                                   sync_file_to_disk
  native ([B)V                                   testDecompressorLibraryUsable

Lcom/whatsapp/superpack/WhatsAppObiInputStream; extends java/io/InputStream
  native ([B[B)V      closeNative
  native ([B[B[B)J    openBytesNative
  native ([B[B)J      openInputStreamNative
  native ([B[B[B)J    openObArchiveBytesNative
  native ([B[B)J      openRawBytesNative
  native ([B[B[BJ)I   readNative
```

`libsuperpack.so` esporta solo `JNI_OnLoad` e
`Java_com_facebook_superpack_AssetDecompressor_testDecompressorLibraryUsable`: gli
altri punti d'ingresso vengono registrati a runtime, quindi non sono raggiungibili
per nome.

## L'archivio, letto dall'intestazione

`assets/compressed/strings/strings_it.spo`, 195.086 byte:

| campo | valore |
|---|---|
| magic | `0x0001c050` |
| dimensione totale dichiarata | `0x0002fa0e` = 195.086 (esatta) |
| membro | `strings_it.pack` |
| tipo del membro | `pack` |
| dimensione decompressa | `0x00100de6` = 1.052.134 |
| xxh64 del membro | `0xacff11057a4c69e2` (combacia con quello già noto) |

Quindi il file è un archivio con un membro, e il membro è di tipo `pack`: è il
driver `stringpack_driver`, che `libsuperpack.so` nomina insieme a
`xz_file_handler`, `zstd_file_handler`, `zstd_patch_file_handler`, `anon_driver` e
`native_driver`.

I sei stream xz (offset 175.420 … 195.002) rendono 83.454 byte di corpo. Quel
corpo **non contiene testo**: 2.153 run stampabili di almeno 4 caratteri, e le
uniche sequenze di sole lettere lunghe 6 o più sono rumore (`Ithuwc`, `cKdYmm`,
`qICGBZ`, `bvbEQrX`). Nessuna parola italiana. È il motivo per cui il problema non
è il contenitore ma il codec: senza il driver, quei byte non dicono niente.

## Il tentativo di esecuzione, e dove si ferma

Su WSL Ubuntu 24.04 (x86_64, quindi la stessa ABI di `lib/x86_64/libsuperpack.so`)
ho costruito un harness minimo: la libreria estratta dall'APK, uno stub Java delle
classi qui sopra, e uno shim C per i simboli che il `.so` importa e che glibc non
ha (`__android_log_print`, `AAssetManager_*`, `__sF`, `funopen`, `__errno`,
`__strncpy_chk2`, `android_dlopen_ext`).

I passi, con l'errore esatto di ciascuno:

1. `System.load` diretto: `libandroid.so: cannot open shared object file`. Il `.so`
   ha `DT_NEEDED` = `libandroid.so`, `liblog.so`, `libdl.so`, `libc.so`.
2. Stub `libandroid.so`/`liblog.so` e symlink ai nomi bionic: `libdl.so: version
   'LIBC' not found`. La libreria è linkata con **versioni di simbolo bionic**
   (`LIBC`), che glibc non esporta: `malloc` di glibc è `GLIBC_2.2.5`.
3. Neutralizzare il versioning (versym a `*global*`, tag `DT_VERNEED`/`DT_VERNEEDNUM`
   azzerati): `unsupported version 17791 of Verneed record` — il loader legge
   l'indirizzo zero come intestazione ELF e la interpreta.
4. Rendere inerti i due tag invece di azzerarli: `SIGSEGV` in
   `ld-linux-x86-64.so.2+0xfab8`, cioè nel loader, prima che la libreria sia
   mappata.

Il confine è questo: **un `.so` bionic non si carica in un processo glibc** senza
il suo runtime. Non è un dettaglio da aggiustare con due shim; è un'altra ABI.

## Il runtime bionic, provato

Il primo punto qui sotto non è più un'ipotesi: l'ho fatto, e questo è quello che
ha detto.

Il runtime:

- compilatore e header: NDK r27c, `--target=x86_64-linux-android24`;
- loader e libc: da una system image Android API 28 x86_64
  (`sys-img/android/x86_64-28_r04.zip`). Serve perché l'NDK non porta né
  `linker64` né una `libc.so` vera — il suo sysroot ha solo stub di link — e il
  bootstrap di Termux non li contiene: su un telefono arrivano da Android;
- `system.img` di API 28 è già un ext4, quindi `simg2img` lo rifiuta e `7z` lo
  legge direttamente. API 29 e successive lo nascondono dentro `super.img`, un
  passaggio in più;
- nel container: `linker64` in `/system/bin/linker64`, librerie in
  `/system/lib64`, e l'harness si avvia come un eseguibile qualsiasi con
  `--dynamic-linker=/system/bin/linker64`.

L'harness è un `JNIEnv` finto (`.tools/apk/wsl/jni_harness.c`): la tabella ha 233
slot, quelli che servono sono implementati su oggetti C normali (array di byte,
stringhe, array di oggetti) e tutti gli altri sono stub che stampano il proprio
numero invece di saltare a zero. `JNI_OnLoad` risponde `0x10006`, cioè
`JNI_VERSION_1_6`.

`RegisterNatives` ha consegnato **19 metodi in quattro classi**, con firme che il
DEX non mostra (lì i byte sono `[B`, qui sono path, `InputStream` e
`AssetManager`):

| classe | metodo | firma |
|---|---|---|
| AssetDecompressor | `decompress_range` | `(Landroid/content/res/AssetManager;SSSII)[Ljava/lang/String;` |
| | `decompress_range_from_so` | `(SSSII)[Ljava/lang/String;` |
| | `decompress` | `(Landroid/content/res/AssetManager;SSS)[Ljava/lang/String;` |
| | `decompress_from_so` | `(SSS)[Ljava/lang/String;` |
| | `decompress_legacy` | `(Ljava/io/InputStream;SS)[Ljava/lang/String;` |
| | `decompress_with_ref` | `(SSSS)[Ljava/lang/String;` |
| | `sync_file_to_disk` | `(S)I` |
| | `get_architecture` | `()I` |
| WhatsAppObiInputStream | `openInputStreamNative` | `(Ljava/io/InputStream;I)J` |
| | `openBytesNative` | `([BII)J` |
| | `openRawBytesNative` | `(JI)J` |
| | `openObArchiveBytesNative` | `(JJI)J` |
| | `readNative` | `(J[BII)I` |
| | `closeNative` | `(J[B)V` |
| WhatsAppOpenboxArchive | `openNative` | `(IJJ)J` |
| | `getFileCountNative` | `(J)I` |
| | `getFilePtrNative` | `(JI)J` |
| | `getFileSizeNative` | `(JI)J` |
| | `closeNative` | `(J)V` |

(`S` sta per `Ljava/lang/String;`, abbreviato per stare nella tabella.)

### I quattro tipi offuscati

Il DEX li definisce tutti e tre in `classes.dex`, quindi non sono nomi inventati:

| tipo | DEX | cosa è |
|---|---|---|
| `X/022` | `access 0x601`, super `LX/020;` | interfaccia, un solo `<clinit>` |
| `X/01J` | `access 0x1` | classe pubblica, `A00(Object)List` è un bridge |
| `X/00I` | `access 0x401` | classe pubblica finale, `A00()Z` statico |
| `X/01S` | `access 0x1` | classe pubblica, `A00()LX/01R;`, `A01(LX/01V;)V`, `A02(LX/01o;)V` |

`javac` non può scrivere quei nomi, quindi i class file sono costruiti a mano
(`mkclass.py`): `this_class` giusto, nessun campo, metodi dichiarati senza codice
(la classe è astratta, o interfaccia dove il DEX lo dice), più stub vuoti per
`X/01R`, `X/01V` e `X/01o`, altrimenti `getDeclaredMethods` lancia
`NoClassDefFoundError`. Si caricano tutti e quattro con `Class.forName`.

Per la registrazione servono a poco — `RegisterNatives` accetta qualsiasi
`jclass`, e i 19 metodi erano già registrati con il `FindClass` finto — ma sono
ciò che serve a una dichiarazione Java che usi quei parametri.

### Cosa cambia rispetto alla prima lettura

1. `decompress_from_so` non prende l'archivio: prende un **`.so` che contiene il
   driver**. Dandogli la `.spo` risponde, onestamente,
   `Failed to load archive so: ... has bad ELF magic`. Il vecchio `Probe.java`
   sbagliava proprio questo.
2. `decompress(AssetManager, String, String, String)` è la via dell'APK. Con un
   `AssetManager` finto su file — `AAssetManager_open`/`read`/`seek`/
   `openFileDescriptor`, in `.tools/apk/wsl/assethook.c` — e un `liblog` in
   preload che stampa su stderr, la libreria **apre** l'asset e legge
   l'intestazione (`archive start: 0 length: 195086`), poi si ferma su
   `Could not open archive: 1/7/0`.
3. Gli stessi byte del corpo, passati come `payload.bin` (83.454), danno lo stesso
   codice `1/7/0`: il rifiuto è nell'intestazione, non nei dati.
4. `decompress_with_ref` va in `SIGABRT` appena la si chiama: quella variante
   vuole un riferimento che qui non esiste, e senza `logd` il motivo non si legge
   nemmeno con lo stub.

Quindi il confine non è più "bionic non si carica": è che questo ingresso
(`decompress`, tipo `stringpack_driver`) si aspetta un archivio di un tipo che il
lato Java dichiara, e la nostra `.spo` dichiara magic `0x0001c050` e membro di
tipo `pack`. Il codice `1/7/0` è la tupla che il parser usa per dire di no. La
strada che resta dentro la libreria è l'altra famiglia, `WhatsAppObiInputStream` e
`WhatsAppOpenboxArchive` (`openNative(IJJ)J`), che legge lo stesso contenuto come
archivio Obi invece che come superpack.

## Cosa servirebbe per portarlo a termine

Non è una questione di tempo, è una scelta di strada:

- **Runtime bionic.** Provato, e funziona: vedi sopra. Ha dato le firme vere e ha
  portato il problema dal caricamento al formato dell'archivio.
- **Le classi offuscate.** Quattro metodi hanno parametri `LX/022;`, `LX/01J;`,
  `LX/00I;`, `LX/01S;`: nomi che javac non sa nemmeno scrivere. Servono class file
  generati a mano (una costante di pool e un `this_class` bastano) perché la
  registrazione di `JNI_OnLoad` non fallisca.
- **Un dispositivo o un emulatore Android.** È la via corta: là il driver gira già,
  e basta chiamare le stesse funzioni con l'APK montato.

Nessuna delle tre è un'estensione di questo repository, ed è per questo che il
lavoro si ferma qui e non nel codice dell'app: le etichette UI delle chiamate
restano quelle dei `.resw`, dichiarate come limite e non come lavoro fatto.

## L'harness, per chi volesse riprenderlo

Resta in `.tools/apk/wsl/` (cartella ignorata da git):

- `shim.c`, `com/facebook/superpack/AssetDecompressor.java`, `Probe.java`: il
  tentativo glibc, che si ferma al punto 4;
- `jni_harness.c`: l'host bionic, con la tabella JNI e la cattura di
  `RegisterNatives`;
- `logstub.c`: il `liblog` da preload che stampa i messaggi della libreria;
- `assethook.c`: l'`AssetManager` su file, guidato da `ASSET_DIR`;
- `setup.sh` (runtime, NDK, build, prime chiamate) e `decompress.sh` (la via
  `decompress` con l'asset finto);
- `dexinfo.py`, `mkclass.py`, `classes.sh` e `classes/`: i quattro tipi offuscati
  letti dai `class_def` del DEX e scritti come class file.

Il passo successivo concreto non è più "serve un runtime bionic": è leggere
`WhatsAppOpenboxArchive.openNative` e vedere se l'archivio Obi è la porta giusta
per una `.spo` di tipo `pack`.
