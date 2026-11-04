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

## Cosa servirebbe per portarlo a termine

Non è una questione di tempo, è una scelta di strada:

- **Runtime bionic.** Un binario compilato contro bionic, eseguito con il
  `linker64` dell'NDK e le `libc.so`/`libdl.so`/`liblog.so` di Android. Da lì si
  può dare al `.so` un `JNIEnv` vero, oppure finto: `RegisterNatives` consegna una
  tabella di puntatori, quindi un `JNIEnv` minimo che implementa solo
  `FindClass`/`RegisterNatives`/`GetByteArrayElements` basta a **catturare** i
  puntatori e chiamare `decompress` direttamente, senza classi Java.
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

Resta in `.tools/apk/wsl/` (cartella ignorata da git): `shim.c`,
`com/facebook/superpack/AssetDecompressor.java`, `Probe.java`, e
`libsuperpack.fixed.so` prodotto dallo script di de-versioning. Il punto 4 è dove
si riparte, e il passo successivo concreto è il runtime bionic, non un altro giro
di patch sull'ELF.
