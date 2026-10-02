# Skills

Istruzioni riutilizzabili per chi (umano o agente) mette mano a questo progetto.
Ogni cartella e' una skill secondo la specifica Agent Skills: un `SKILL.md` con
frontmatter YAML (`name` uguale al nome della cartella, `description` che dice
cosa fa e quando usarla).

| Skill | Quando usarla |
| --- | --- |
| [`maintain-the-app`](maintain-the-app/SKILL.md) | Prima di modificare il codice: vincoli del toolchain, mappa dei file, workflow con i guard. |
| [`update-the-app`](update-the-app/SKILL.md) | Ricette per aggiungere pagina, sezione, icona, stringa, lingua, endpoint dell'adapter, e per alzare la versione. |
| [`test-the-app`](test-the-app/SKILL.md) | Cosa eseguire e cosa guardare prima di dire che una modifica e' a posto. |
| [`release-the-app`](release-the-app/SKILL.md) | Asset di marca, manifest, versione, deploy su dispositivo ed emulatore. |
| [`run-the-login-server`](run-the-login-server/SKILL.md) | Avviare in locale GOWA + adattatore e collegare l'account WhatsApp dal terminale (QR, codice di abbinamento, stop, diagnosi). |

Oltre a queste, due skill di **comunita'** installate con `npx skills add` servono
per le revisioni. Non sono skill del progetto, non hanno un guard e non vanno
modificate qui; la versione installata e' fissata in `skills-lock.json`:

| Skill | Quando usarla |
| --- | --- |
| [`code-review`](code-review/SKILL.md) | Rivedere un diff rispetto a un punto fisso su due assi: conformita' agli standard del repo e aderenza alla specifica. |
| [`improve-codebase-architecture`](improve-codebase-architecture/SKILL.md) | Cercare occasioni di *deepening* (moduli profondi dietro un'interfaccia stretta) e presentarle in un report HTML. |

Regola numero uno, valida per tutte: **i guard di `tools/` sono il gate**. Il
toolchain di Windows Phone 8.1 usa un compilatore vecchio e non e' su questa
macchina, quindi un errore di sintassi o una risorsa mancante si scoprono con
gli script, non con la build:

```bash
node tools/check-csharp5.js        # sintassi C# 5 + API assenti su WP8.1
node tools/check-icons.js          # icone: forma delle geometrie, coerenza, font vietati
node tools/check-resw.js --strict  # stringhe: x:Uid/Loc.Get <-> entrambi i .resw
node tools/check-docs.js           # documenti: inglese e italiano allineati, disclosure in fondo, niente emoji
```

## Come mantenere, aggiornare e testare l'app, in breve

- **Mantenere**: leggere `maintain-the-app` prima di toccare il codice. I vincoli
  non negoziabili sono tre — C# 5, niente font di icone, niente stringhe
  hardcoded — e ognuno ha un guard che lo verifica.
- **Aggiornare**: seguire la ricetta corrispondente in `update-the-app`. Ogni
  ricetta finisce con i guard, un commit e un `git push`: il lavoro non e' finito
  finche' non e' su `origin/master`. E un commit che tocca `WhatsappBridge/` non
  e' finito nemmeno li': lo stesso commit va portato nel repository Docker
  `docker-whatsappforwp` con il suo `tools/sync.js`, perche' l'immagine e' quello
  che gira sulla maggior parte delle installazioni (vedi *The Docker repository*
  in `maintain-the-app`, che spiega anche come farlo passare in CI).
- **Pianificare**: un piano in `docs/superpowers/plans/` si scrive con la skill
  **`writing-plans`** per eseguirlo subito, in questa stessa sessione e task per
  task, fino all'ultimo commit pushato. Il documento e' il resoconto del lavoro,
  non il risultato: fermarsi al piano vuol dire lasciare il lavoro non fatto e
  spedire la descrizione di un codice che non esiste. La skill si installa con
  `npx skills add obra/superpowers --skill writing-plans --skill executing-plans -g -y`
  (finisce in `~/.agents/skills/`). Il suo *Execution Handoff* non e' un punto di
  arresto: qui si esegue sempre **inline**, nella stessa sessione, e non si chiude
  il turno sulla domanda "quale approccio?".
- **Spingere e' la regola, non una domanda**: ogni lavoro finisce con
  `git push origin master`, e un commit che tocca `WhatsappBridge/` finisce anche
  con il mirror pushato (`docker-whatsappforwp`, `git push origin main`). Chi
  esegue non si ferma a chiedere il permesso di pushare: e' stato chiesto
  esplicitamente che si pushi tutto.
- **Testare**: eseguire la matrice in `test-the-app`. Qui si ferma tutto cio' che
  si puo' verificare senza dispositivo; il gate autorevole e' `msbuild` sulla
  macchina Windows, e in fondo c'e' la checklist da fare sul telefono.
- **Documentare**: i documenti che spiegano il progetto (`README.md`/
  `README.it.md`, `WhatsappBridge/README.md`/`README.it.md`) esistono in inglese e
  in italiano e si aggiornano **insieme**, nello stesso commit: una sezione aggiunta
  da una parte sola divide le due versioni e il guard lo segnala. La sezione
  `## Disclosure` (open source, si cercano maintainer, scritto da un agente AI,
  nessuna responsabilita' sull'account usato) chiude i README del progetto e resta
  l'ultima. Niente emoji: l'unica eccezione e' il segno di pericolo, per un rischio
  reale.
- **Rilasciare**: `release-the-app` per asset, versione e deploy.
- **Collegare un account / far girare il server**: `run-the-login-server`. Un
  comando (`node tools/start-login.js --download`) avvia GOWA e l'adattatore,
  stampa IP e porte per l'app e disegna il QR di login nel terminale.
