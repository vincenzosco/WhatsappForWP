# Traduci l'app - senza programmare

Non devi scrivere codice, installare niente, nemmeno avere un computer. Se
sai usare GitHub nel browser, puoi tradurre questa app nella tua lingua.

L'app ha **222 stringhe brevi** - pulsanti, menu, notifiche. Oggi esistono
in inglese e in italiano. La lingua successiva può essere la tua.

## Cosa devi fare

C'è un file di testo da riempire. Ogni riga è fatta così:

```
Nav_Chats = Chats
```

La parte a sinistra è un nome che non si tocca mai. La parte a destra è
l'originale inglese: **ci scrivi sopra la tua lingua**, sulla stessa riga.

Puoi tradurre tutto il file o solo dieci righe: ciò che salti resta in
inglese dentro l'app finché qualcuno lo completa.

## I cinque passi

1. **Apri il modello.** È [`MODELLO.txt`](MODELLO.txt) in questa cartella.
   Premi il pulsante *Raw*, poi seleziona tutto e copia.

2. **Traduci dove ti è comodo.** Un'app di note, un editor di testo, una
   risposta su GitHub - ovunque tu possa scrivere. Lascia intatta la parte
   `Key = ` e scrivi la tua traduzione dopo.

3. **Dai al file il nome della tua lingua.** Il francese è `fr.txt`, lo
   spagnolo `es.txt`, il portoghese del Brasile `pt.txt`. Se non sai quali
   due lettere usare, non preoccuparti: scrivi il nome della lingua al
   passo 4 e qualcuno sceglierà il codice al posto tuo.

4. **Mandalo su GitHub.** Vanno bene entrambi i modi:
   - in questa cartella premi *Add file* poi *Create new file* (o carica il
     tuo), nominalo come al passo 3, premi **Propose changes** e poi
     **Create pull request**; oppure
   - apri [una nuova issue di tipo "traduzione"](../../issues/new/choose),
     incolla le tue righe nel riquadro e invia.

5. **Fine.** Uno dei maintainer esegue un comando che trasforma il tuo file
   nella risorsa dell'app e dice quanto hai tradotto.

## Le cose che chiedono tutti

**Cosa sono `{0}` in alcune righe?**
Sono spazi che l'app riempie dopo: `{0} unread messages` diventa "5
messaggi non letti". Tieni ogni `{0}` e `{1}` esattamente come nell'originale;
il resto è tuo.

**E se sbaglio qualcosa?**
Non succede niente di male. L'app rifiuta un file con una riga rotta o con
un `{0}` mancante, e lo strumento dice al maintainer quale riga guardare.
Una traduzione parziale è sempre benvenuta.

**Le parole in MAIUSCOLO le traduco?**
Sì, sono etichette di menu. Se la tua lingua non usa le maiuscole, scrivi
alla maniera naturale.

**Quale file apro per vedere l'app nella mia lingua?**
Nessuno. L'app segue la lingua del telefono in automatico.

**Posso aiutare in altro modo?**
Sì - il [README](../../README.it.md) dice di cosa ha bisogno il progetto:
maintainer, revisioni e documentazione.

---

*Questo file è una guida, non fa parte dell'app. Le stringhe dell'app
stanno in `WhatsappApp.Core/Strings/`.*