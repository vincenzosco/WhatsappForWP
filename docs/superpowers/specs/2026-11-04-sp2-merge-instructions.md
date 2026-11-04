# Le istruzioni di merge di sp2, tipo 0x36

Data: 4 novembre 2026. Artefatto letto: `libsuperpack.so` (arm64) estratto da
`lib/arm64-v8a/libs.so` dell'APK ufficiale, sezione `.text` da `0x614`. Metodo:
solo statico (capstone; su questa macchina nessun provider ELF nativo accetta il
target), indirizzi di `.text`.

Perché questo documento esiste: la ricostruzione delle `.so` dentro l'archivio sp2
si ferma sulle tabelle di sezione. Il tipo `0x36` è l'elemento dell'archivio che
*produce* byte a partire da un'altra base, e finora non era descritto da nessuna
parte. Qui c'è quello che il codice dice, con l'indirizzo di ogni affermazione, e
la parte che **non** si può ancora provare.

## Dove sta il tipo 0x36

Il dispatcher degli elementi è a `0x1ec00`: legge il tipo con `ldr w3, [x26, #0xc]`
e confronta `0x29`, `0x2a`, `0x35`, `0x36` (`0x1eca0`–`0x1ecbc`). Al `0x36` salta
a `0x1ecc4`.

La tabella dei nomi a `0x1ef8c` dà nome e classe di ogni tipo:

| tipo | gestore | classe |
|------|---------|--------|
| `0x01` | `xz_file_handler` | 2 |
| `0x02` | `zstd_file_handler` | 2 |
| `0x03` | `store_file_handler` | 2 |
| `0x04` | `zstd_patch_file_handler` | 0xd |
| `0x35` | `sp2_file_handler` | 2 |
| `0x36` | `sp2_patch_file_handler` | 2 |

Quindi `0x36` = **patch in formato sp2 applicata a un elemento che esiste già**;
`0x35` è lo stesso formato senza base (elemento autonomo), `0x04` la variante zstd.

## I campi del record

Il record è la struttura puntata da `x26`:

| offset | significato | prova |
|--------|-------------|-------|
| `+0x0c` | tipo, u32 | `0x1eca0` |
| `+0x20` | dimensione attesa dell'output | `ldr x27, [x26, #0x20]` a `0x1ece8`, passata come `x3` |
| `+0x28` | puntatore del patch | `ldp x0, x1, [x26, #0x28]` a `0x1ece0` |
| `+0x30` | lunghezza del patch | secondo registro dello `ldp` sopra (`x1`) |
| `+0x38` | stato dell'elemento bersaglio | `ldr x28, [x26, #0x38]` a `0x1ecc4` |
| `+0x48` | secondo campo del locator | `ldr x9, [x26, #0x48]` a `0x1ecec` |
| `+0x64` | «possiedo il patch, liberalo» | `ldr w8, [x26, #0x64]` a `0x1ed24`, poi `0x33a60` |

La **base** non è un campo del record: viene dallo stato del bersaglio.

```
x28 = [x26 + 0x38]
x8  = [x28]        ; inizio
x9  = [x28 + 8]    ; fine
se x8 != x9: 0x2e6d0(x28)        ; riavvio dello stato, poi x8 = [x28]
base_ptr = [[x8] + 8]
locator  = { [x26 + 0x48], base_ptr }
```

Il locator è la coppia passata a `x2` (`stp x9, x8, [sp, #0x20]` a `0x1ecfc`), e
`x8 = [[x8]+8]` è il buffer della base: la patch **non** contiene la base, la
riceve.

## Il contenitore OB: 24 byte prima delle istruzioni

`0x2f840` è il parser, e rifiuta tutto ciò che non comincia con `O` `B`:

```
0x2f840  ldrb w8, [x0]        ; deve essere 0x4f  'O'
0x2f84c  ldrb w8, [x0, #1]    ; deve essere 0x42  'B'
0x2f858  ldur w9, [x0, #2]    ; u32 -> [x1+0x00]
0x2f860  ldur w9, [x0, #6]    ; u32 -> [x1+0x04]
0x2f868  ldur w9, [x0, #0xa]  ; u32 -> [x1+0x08]
0x2f870  ldur w9, [x0, #0xe]  ; u32 -> [x1+0x0c]
0x2f878  ldur w9, [x0, #0x12] ; u32 -> [x1+0x10]
0x2f880  ldrb w9, [x0, #0x16] ; u8  -> [x1+0x14]
0x2f888  ldrb w9, [x0, #0x17] ; u8  -> [x1+0x15]
```

L'intestazione OB è quindi di 24 byte, con i campi **non allineati** (u32 a +2,
+6, +0xa, +0xe, +0x12); l'errore è `Not an OB file` (`0x2f8a8`). La forma — cinque
u32 più due byte — è la stessa dell'intestazione dell'archivio sp2.

## Come si legge una patch

`0x2f0b8(patch, patch_len, locator, out_size, 0)` fa, in ordine:

1. `0x2f840` → intestazione OB nello stack (`sp+0x3b0`); se fallisce, `Bad OB header`
   (`0x2f110`) e ritorno `-1`;
2. due contesti di decompressione: `0x31cdc(&ctx_a, 0x1ed, 1)`, `0x30cc8`, poi
   `0x32100(&ctx_b, 0, 8, 4)` e `0x32268` che li collega;
3. `0x30950` avvia e legge subito tre campi con larghezze **4, 1, 4**;
4. `0x302e4` dà un numero di voci, `0x2f8c0` alloca una tabella (0x20 byte per
   voce: due array di puntatori e uno di byte) e `0x2f9d8` la riempie;
5. `0x2fb18(h.f0, voce, tabella, count)` e `0x30fd4` preparano la sorgente dei
   letterali.

Il lettore dei campi è a **larghezza variabile**: la chiamata indiretta `blr x8`
con `w2` uguale a `1`, `2`, `4`, `8`, `0x20` (`0x2f348`–`0x2f3f0`). Non è un
formato a record fissi: è un flusso di campi in cui la larghezza la decide
l'istruzione stessa.

## Il ciclo delle istruzioni

```
0x2f2c0  w8 = h.f4                 ; quante voci nel ciclo
0x2f330  per w28 = 0 .. w8-1:
           se w28 == [sp+0x60] && w28 != ultimo:
               leggi 1 bit   -> se != 1, all'errore 0x2f7e8
               leggi 2 bit   -> w24 = valore + 1
               leggi 4 bit   -> [sp+0x88]
               leggi 32 bit  -> [sp+0x60]   ; prossimo indice che cambia
               leggi 8 bit   -> x21
               leggi 8 bit   -> x19
               leggi 8 bit   -> [sp+0x80]
           altrimenti:
               riusa i valori precedenti (default 6): la voce eredita
           w20 = w24 ; w8 = x29 % w20 ; w3 = w8 << 2
           w23 = [x26+0x18](x26, ctx, x21, w3, min(w23,3))   ; lunghezza del run
           base[x29] = [x26](x26, ctx, out, flag, w24, x29, primo?)
```

Due callout dentro `x26` fanno il lavoro: `[x26]` produce **un byte** e
`[x26+0x18]` dice **quanti** byte produrne. Il primo byte del run si legge con
l'indice `[sp+0x88]`, i successivi con `w6 = -1` (`0x2f4d4`–`0x2f504`). Da
`0x2f580` c'è la catena di estensione: se il valore letto è `0x1fffd`
(`movz 0xfffd` + `movk 1, lsl 16`) se ne legge un altro e si somma `valore - 3`,
finché il valore non cambia. È il motivo della stringa
`patch extends across multiple elements` (`0x3269`): un run può oltrepassare la
fine dell'elemento e continuare nel successivo.

In pratica: **la patch è un elenco di run di byte, ognuno preso dai letterali
oppure copiato da byte già scritti nella base** — il `w6 = -1` e l'offset
calcolato da `x29 % w24` sono il back-reference.

## Applicare una patch a una base nota

1. **La base**: i byte dell'elemento a cui la patch si riferisce (`base_ptr`),
   della dimensione che l'archivio dichiara per quell'elemento. Nessun byte della
   base sta nella patch.
2. **Il patch**: il blob `OB` dell'elemento `0x36`, preso con `+0x28`/`+0x30` del
   record.
3. **L'uscita**: un buffer di `+0x20` byte. Se `0x2f0b8` restituisce non-zero, il
   client scrive `SP2 decompression failed for a stream with %d error` (`0x1ed18`)
   e marca l'elemento non disponibile (`x27 = -1`, `0x1ed20`).
4. **La verifica**: `xxh64` del buffer deve dare il valore della tabella dei file
   (per `libhiddenapis2.so`: `0x7392b6dc922f1283` su 21.984 byte). La routine è a
   `0x2ec80` (`ror 41/53`, moltiplicatori 11400714785074694791,
   14029467366897019727, 1609587929392839161, 9650029242287828579,
   2870177450012600261, seme 0).
5. **Se il run eccede la fine**: non è un errore, è la catena `0x1fffd` da
   continuare nell'elemento successivo.

Pseudocodice della parte provata (contenitore e verifica):

```python
def ob_header(b):
    if b[0] != 0x4F or b[1] != 0x42:
        raise ValueError("Not an OB file")
    u = lambda o: int.from_bytes(b[o:o + 4], "little")
    return dict(f0=u(2), f4=u(6), f8=u(10), f12=u(14), f16=u(18),
                b20=b[0x16], b21=b[0x17])

def verify(out_bytes, expected_xxh64):
    return xxh64(out_bytes) == expected_xxh64
```

## Cosa questo archivio non contiene

In `libs.so` **non c'è nessun elemento `0x36` e nessun blob `OB`**: su tutti i 140
stream decomprimibili (33.331.370 byte) la ricerca della magic `OB` con cinque u32
plausibili dà **zero** candidati, e la tabella dei file dichiara tipo `so` solo per
il primo membro e vuoto per gli altri 82. Tutte le `.so` sono elementi `0x35`/`0x01`
(xz), quindi la patch non serve a ricostruire questo archivio: la decodifica qui
sopra viene dal codice, non da un esempio reale.

## Cosa resta non provato

- il significato di `f0`, `f8`, `f12`, `f16` e dei due byte `b20`, `b21`;
- la mappatura esatta dei campi del ciclo (quale dei tre `8 bit` è l'indice della
  sorgente e quale la lunghezza) e la forma completa della sentinella `0x1fffd`;
- la semantica dei due callout `[x26]` e `[x26+0x18]`, che è il punto in cui il
  formato diventerebbe eseguibile: senza un blob `OB` reale non c'è modo di
  distinguere un'ipotesi dall'altra.

Per chiudere il punto serve **un archivio con almeno un elemento `0x36`** — per
esempio quello di una build con patch incrementali — e la stessa verifica `xxh64`
sull'uscita: se il checksum torna, la decodifica è giusta; se non torna, l'errore è
in uno dei punti elencati sopra.
