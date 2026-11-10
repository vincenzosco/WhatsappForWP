[English](README.md) | [Italiano](README.it.md) | **Português (Brasil)**

<p align="center">
  <img src="WhatsappApp/Assets/WhatsAppLogo.png" width="128" alt="Ícone do app WhatsApp para Windows Phone 8.1">
</p>

<h1 align="center">WhatsApp para Windows Phone 8.1</h1>

> **Uma nota sobre os commits.** Alguns commits deste repositório foram feitos por agentes de IA. Isso
> não significa que o projeto foi "vibecodado": os agentes foram usados para olhar o próprio app do
> WhatsApp e ver o que o protocolo faz na rede. O restante - o cliente, a pilha do protocolo, as
> páginas - é em grande parte trabalho de uma pessoa.

<p align="center">
  <strong>Um cliente do WhatsApp que roda em um telefone de 2014, escrito a partir do protocolo.</strong><br>
  O telefone fala direto com o WhatsApp: handshake Noise, nós binários, o ratchet do Signal, mídia.
  Não há servidor para iniciar e nada para configurar.
</p>

<p align="center">
  <a href="#compilar">Compilar</a> ·
  <a href="#usar">Usar</a> ·
  <a href="#como-funciona">Como funciona</a> ·
  <a href="#solucao-de-problemas">Solução de problemas</a>
</p>

<p align="center">
  <a href="#compilar"><img src="https://img.shields.io/badge/platform-Windows%20Phone%208.1-2ea44f" alt="Windows Phone 8.1"></a>
  <a href="README.md"><img src="https://img.shields.io/badge/languages-English%20%7C%20Italiano%20%7C%20Portugu%C3%AAs%20%28Brasil%29-blue" alt="Inglês, italiano e português"></a>
</p>

O Windows Phone 8.1 perdeu o cliente oficial do WhatsApp há anos. Este é open source, vive em um
único app no telefone e fala o protocolo WebSocket do WhatsApp em vez de conversar com uma ponte no
PC. Ele roda em um dispositivo WP8.1 real ou no emulador x86. É gratuito e não exige nenhuma conta
além do número do WhatsApp que você vincular.

Não existe instalador e nem release: você compila a partir do código-fonte com o Visual Studio e o
SDK do WP8.1.

## O que é

Um app, um processo, nenhum backend. `Protocol/` é a rede (handshake, nós, sessões Signal, chaves de
mídia), `Protocol/Client/` é a sessão (login, sincronização do histórico, envio, recibos, registro de
chamadas, feed de status) e `Services/NativeConnection.cs` é quem possui o socket e os eventos que as
páginas escutam.

As três abas são as do app lançado: chats, status, chamadas, sob uma faixa de abas fixa com a barra de
app do WP8. Uma conversa é uma página separada.

| Página | O que é |
| --- | --- |
| `Pages/HomePage.xaml` | chats, status e chamadas, deslizados sob `Controls/TabHeader.xaml` |
| `Controls/StatusPane.xaml` | o feed de status |
| `Controls/CallsPane.xaml` | o registro de chamadas |
| `Pages/ChatPage.xaml` | a conversa |
| `Pages/SettingsPage.xaml` | tema (papel de parede do chat) e o caminho para a conta |
| `Pages/ConnectionPage.xaml` | o número vinculado e o estado da conexão |
| `Pages/PairPage.xaml` | o QR code que vincula um dispositivo |

O que funciona hoje:

- mensagens de texto, imagens e notas de voz, com marcas de enviado/entregue/lido em cada bolha, e uma linha **Tentar novamente** sob uma bolha cujo envio não chegou
- busca na lista de chats por nome, pelo que a conversa falou por último ou pelo número
- toque longo em uma mensagem copia suas palavras, a apaga neste telefone ou a retira para todos em até dois dias - e uma mensagem que outro dispositivo retira também some neste
- a lista real de chats vinda da sincronização do histórico, avatares, não lidos, grupos e os nomes salvos na agenda de contatos do WhatsApp do telefone principal, que chegam criptografados no app state da conta
- indicadores de digitação em ambos os sentidos
- o feed de status: o que os contatos publicaram, com a foto do autor e o nome que o WhatsApp envia junto com a atualização; tocar abre a foto, o vídeo ou as palavras, e o recibo de visualização volta para `status@broadcast`. O feed do último dia fica no telefone, então continua lá depois de reiniciar; a prévia de uma atualização sai da linha quando o dia foi visto, e uma atualização cuja foto não chegou tem um botão **Tentar novamente** em vez de um beco sem saída
- o registro de chamadas em ambos os lados, com as perdidas escritas por extenso
- um tile ao vivo que gira entre as pessoas esperando - nome e mensagem, e entre uma mensagem e outra a foto apenas da pessoa ainda sem resposta - além do tile de uma conversa fixada, texto na tela de bloqueio e uma verificação em segundo plano a cada 15 minutos aproximadamente
- seletor de tema com os papéis de parede que o pacote oficial entrega, além do rabisco e do branco puro; o escolhido pinta todas as telas - as três abas, a conversa, as configurações, as informações do contato, a conta e a página de vinculação - e as listas mantêm-no atrás de um véu branco de 60% para que as linhas permaneçam legíveis
- uma conversa é desenhada uma página por vez (trinta linhas em um telefone de 512 MB, cem acima disso), e as imagens são decodificadas com o mesmo orçamento, então um histórico longo não carrega todas as bolhas de uma vez
- chamadas em ambos os lados: a oferta carrega a chave da chamada criptografada para cada um dos dispositivos do outro telefone; o telefone toca e vibra, e a chamada vai para o registro; o áudio ainda não viaja
- inglês e italiano, seguindo o idioma do telefone

## Bom para, não é bom para

**Bom para:** manter um WP8.1 útil como segundo dispositivo do WhatsApp; ler o protocolo em C# 5; um telefone que não é o número principal.

**Não é bom para:** substituir o WhatsApp no seu telefone principal. Este é um cliente não oficial, que quebra os Termos de Serviço do WhatsApp, e o número vinculado pode ser banido. Use um número de teste ou secundário. Também não é um cliente completo: o histórico antigo não é buscado sob demanda, e edições feitas em outros dispositivos não são lidas.

## Compilar

Você precisa do **SDK do Windows Phone 8.1**, além de um Visual Studio cuja MSBuild tenha os targets do 8.1: o 2013 (v12.0) e o 2015 (v14.0) servem, e esta árvore compila aqui com o MSBuild 14.0. Compile a partir de um caminho em disco local, não de uma pasta compartilhada ou unidade de rede.

**Linha de comando** (na raiz do repositório):

```bash
msbuild WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86
```

A build deve terminar com `0 Error(s)` e deixar
`WhatsappApp\AppPackages\WhatsApp_<version>_Debug_Test\WhatsApp_<version>_x86_Debug.appxbundle`.
Para o telefone, compile `Release` em `ARM` e use **Build > Deploy**.

**No Visual Studio**: abra `WhatsappApp.sln`, escolha `Debug` / `x86` (emulador) ou `Release` / `ARM` (telefone) e depois F5.

<details>
<summary><strong>O que a build precisa e o que ela recusa</strong> A toolchain foi pensada para ser antiga.</summary>
<br>

- A toolchain do WP8.1 compila com o compilador **C# 5**. Strings interpoladas, `?.`, membros com
  corpo de expressão, inicializadores automáticos de propriedades, pattern matching e `out var` não
  compilam aqui. `node tools/check-csharp5.js` lista cada arquivo, linha e construto que precisa ser
  corrigido, e também detecta membros WinRT que existem no Windows 10, mas não na projeção do WP8.1.
- Se você compilar dentro de `C:\Mac\Home\...` (a pasta compartilhada do Parallels), a segunda
  passagem do XAML falha sempre com `Xaml Internal Error error WMC9999: The given key was not present in the dictionary`.
  Copie a árvore para um disco local (`robocopy <share> C:\wp81 /E`) e compile.
- O emulador do WP8.1 não inicia em um Mac com Apple Silicon: suas imagens são x86 e precisam do Hyper-V.
  Use uma máquina Windows x86/x64 ou um telefone conectado via USB.

</details>

## Usar

1. Abra o app. A primeira tela é o QR code, desenhado pelo próprio app (nenhuma imagem vem de fora).
2. No telefone principal: WhatsApp > dispositivos vinculados > vincular um dispositivo e escaneie.
3. O app faz login, lê a sincronização do histórico e preenche a lista de chats.
4. Toque em uma conversa para escrever. Enter envia. O clipe anexa uma foto; o microfone grava uma nota de voz.
5. Deslize para a aba de status para ver o que seus contatos publicaram. Toque em um para abrir: a foto, o vídeo ou as palavras, com legenda abaixo.
6. A aba de chamadas lista o registro de chamadas que o WhatsApp enviou com a sincronização.

Dicas:

- Uma conversa pode ser fixada na tela Iniciar a partir do menu de toque longo. O tile leva a foto da pessoa com o nome na linha grande e a última mensagem embaixo, além do número de mensagens pendentes no badge. O próprio tile do app funciona do mesmo jeito, e entre uma mensagem e outra ele mostra apenas a face de alguém que ainda está esperando resposta. Um tile fixado é redesenhado a cada inicialização a partir da foto e do total que o telefone tem, para que uma nova versão do app não deixe o cartão da versão anterior na tela Iniciar. A imagem que ele desenha é a foto de perfil em tamanho real que pede ao WhatsApp uma vez que a conversa é fixada, não a miniatura que a lista usa: o tile é o único lugar que mostra um rosto no tamanho do tile. Quando alguém publicou uma atualização que ninguém viu, uma rotação do tile diz isso ("Atualizações" sobre "3 novas atualizações") - mas só depois de cada mensagem e apenas quando nada está pendente: uma conversa que ninguém respondeu leva o tile e o texto detalhado da tela de bloqueio primeiro, porque é isso que a pessoa está sendo informada, e a rotação da atualização recebe o espaço que as mensagens deixam. O badge próprio da aba só fica visível com o app aberto, e o número no ícone é o de mensagens pendentes, que uma atualização não conta. As rotações sem a face de ninguém são pintadas com o fundo escolhido em Configurações - Tema: esse papel de parede, ou a cor de destaque do telefone quando a escolha for branco puro - de modo que o tile tenha o mesmo visual da lista. A marca do WhatsApp é desenhada nesse fundo, com baixa opacidade para que um glifo branco leia sobre ela, e o tile largo é preenchido com o mesmo fundo em vez de enquadrar um quadrado em verde-azulado.
- Uma mensagem dispara um alerta somente enquanto este app não está em primeiro plano: a verificação em segundo plano acorda, uma mensagem chega e a área de notificações diz quem escreveu. Com o app em frente não há alerta nenhum - na conversa aberta a bolha já está lá, e em qualquer outra conversa a linha já está na lista - então a mesma notícia nunca é dita duas vezes. Uma conversa silenciada continua em silêncio de qualquer forma, e o tile e o número no ícone se movem normalmente: isso é o que sobra quando ninguém está olhando.
- Uma atualização publicada por alguém e uma chamada não atendida cada uma dispara uma notificação na mesma área de uma mensagem: o nome do autor e nada mais - a própria atualização é aberta onde está. Cada uma é anunciada uma vez, a partir de uma marca guardada no telefone, porque o WhatsApp reenvia o dia inteiro de atualizações e o registro inteiro de chamadas em cada login. Mensagens, atualizações e chamadas perdidas têm um interruptor em Configurações. O tile e o número no ícone não fazem parte disso.
- O badge de chamadas conta as ligações perdidas que chegaram desde a última vez em que o registro esteve na tela, então ele volta a zero quando a aba é aberta em vez de mostrar o mesmo número para sempre.
- Os emojis funcionam como este telefone pode. Um rosto que a fonte do Windows Phone 8.1 nunca teve - o coração azul-claro, o braço mecânico, o planeta com anel - é desenhado como o rosto mais próximo que ele possui, e um que não tem primo mais antigo é omitido em vez de ser desenhado como uma caixa. O que é enviado é o que foi escrito, então um telefone mais novo do outro lado vê exatamente isso. Digitar um sorriso do jeito antigo ainda envia um rosto: a caixa de texto pede ao teclado o escopo do chat, que é o que coloca a tecla de emoji ao lado da barra de espaço, e `:)` - ou `:(`, `;)`, `:D`, `:P`, `:O`, `:|`, `<3`, `</3` - vira o rostinho em si na saída, mesmo em um teclado que não faz isso.
- A verificação em segundo plano está ativada por padrão. Desative em Configurações se o telefone deve ficar em silêncio.
- Se o app abrir uma lista vazia e ficar ali, abra a página de Diagnóstico no estado vazio: ela mostra as últimas linhas `DIAG`.

## Como funciona

Cada etapa é comparada com as implementações de referência (`@whiskeysockets/baileys` e `whatsmeow`), e os testes que mantêm tudo honesto ficam em `ProtocolTests/`.

- **Transporte**: um WebSocket para `wss://web.whatsapp.com/ws/chat` via TLS. Tudo ali é um nó binário: uma tag, um mapa de atributos, filhos ou bytes (`Protocol/WABinary/`). O comprimento de um nó é validado contra um limite antes de ser lido.
- **Handshake**: o handshake do protocolo Noise com a cadeia de certificados do servidor, verificada no telefone (`Protocol/Noise/NoiseHandshake.cs`).
- **Vinculação**: o QR é codificado pelo app (`Protocol/Qr/QrCode.cs`), e a vinculação salva a conta no próprio armazenamento de credenciais do protocolo na pasta do app.
- **Mensagens**: X3DH para uma nova sessão, o double ratchet para tudo depois, e o sender key para grupos (`Protocol/Signal/`). Uma mensagem com mídia nomeia as chaves e o endereço do arquivo; os bytes vão e voltam para o host de mídia do WhatsApp e são criptografados e descriptografados no telefone (`Protocol/Media/`).
- **Sessão**: o login, o classificador de nós, a sincronização do histórico, o caminho de envio com seus recibos, a consulta de foto de perfil, o registro de chamadas e o feed de status (`Protocol/Client/`).
- **Uma conexão, um dono**: somente a tentativa mais recente publica seu socket e loop de leitura, então um socket que falha não consegue fechar o que foi bem-sucedido. O cliente reconecta sozinho, e uma sessão silenciosa é fechada e reaberta em vez de permanecer como um socket que parece vivo.

Um status não é uma conversa: ele vive em `status@broadcast`, seu autor é o `participant` e assisti-lo é um recibo de `read` para aquela lista com o id do status e o autor. O feed mantém isso fora da lista de chats, por isso um telefone com um dia de status não mostra uma linha chamada "status" no topo.

As ícones da interface são elementos vetoriais `Path` com geometria embutida em cada `Path`: o WP8.1 não tem `Segoe MDL2 Assets`, então uma fonte de glifos renderiza em branco. `node tools/check-icons.js` garante isso, a forma do elemento geométrico e que o mesmo ícone tenha a mesma geometria em todo lugar.

## Solução de problemas

<details>
<summary><strong>A build falha com WMC9999 "The given key was not present in the dictionary"</strong></summary>
<br>

O projeto está na pasta compartilhada do Mac. A segunda passagem do compilador XAML sempre falha ali. Copie a árvore para um disco local e execute a build novamente; nada no código precisa mudar.
</details>

<details>
<summary><strong>O app abre com uma lista de chats vazia</strong></summary>
<br>

Ou o telefone ainda não foi vinculado (a primeira tela é o QR), ou a conexão terminou antes da sincronização do histórico. Abra a página de Diagnóstico a partir do estado vazio: as linhas `DIAG` nomeiam a etapa em que a conexão chegou. As mesmas linhas estão em `diag.log` na pasta do app, que o `ISETool.exe ts` copia do telefone.
</details>

<details>
<summary><strong>As mensagens chegam só a cada 15 minutos enquanto o app está fechado</strong></summary>
<br>

Isso é a verificação em segundo plano, não um erro. O WP8.1 fecha o socket ao suspender o app e não existe serviço em nuvem para empurrar mensagens, então o `BackgroundSync` acorda com um `TimeTrigger`, conecta, lê o que está esperando, atualiza o tile e a tela de bloqueio e fecha. 15 minutos é o menor intervalo aceito por um `TimeTrigger`; o sistema decide o momento exato e pode atrasar. Desative em Configurações se preferir que nada rode em segundo plano.

A permissão é do telefone, não do interruptor: o usuário pode retirar o trabalho em segundo plano do app nas configurações do próprio telefone, e a economia de bateria faz isso sozinha por um tempo. O registro continua e nada acorda, então Configurações lê a permissão novamente a cada visita e informa isso sob o interruptor.
</details>

<details>
<summary><strong>O app faz login e a lista de chats fica vazia</strong></summary>
<br>

A sincronização de histórico pode ter acabado cedo, o app ainda precisa do login e do `ContextInfo`. O diagnóstico indica em qual ponto a conexão parou; se a conta estiver conectada, os chats no `account` são preenchidos por `ChatSync` e pela leitura de `history sync`.
</details>

<details>
<summary><strong>As mensagens não aparecem em uma conversa antigas</strong></summary>
<br>

O app não busca o histórico antigo sob demanda. Ele mantém as últimas mensagens sincronizadas e só carrega o que o WhatsApp entregou no início do login; se o registro não apareceu, o telegrama está no `history sync` e não no cache local.
</details>

<details>
<summary><strong>Uma foto em uma conversa ou status mostra uma palavra até ser tocada</strong></summary>
<br>

Linhas do histórico carregam as chaves e o endereço do arquivo, não o arquivo. O app baixa pela primeira vez que você toca nele e depois o mantém. Uma foto de status que ainda não aparece depois do download geralmente expirou: status vivem por um dia.
</details>

<details>
<summary><strong>Uma chamada toca, mas não há voz</strong></summary>
<br>

A chamada é sinalizada, não transportada. `Protocol/Client/CallSignaling.cs` constrói e lê o nó `call`, `Services/CallSession.cs` é os cinco estados e `Pages/CallPage.xaml` é a tela: uma oferta toca, o telefone atende, recusa e desliga, o registro de chamadas recebe uma linha para a chamada que este telefone fez, e uma chamada que ninguém atende termina sozinha depois de 45 segundos em vez de segurar a sessão. O que falta é a mídia. DTLS 1.2, SRTP, STUN e um responder ICE-lite estão escritos e testados em `Protocol/Media/` e `Protocol/Crypto/` - o cabeçalho RTP, a sessão SRTP, o buffer de jitter, a expansão da chave da chamada, `CallMediaSession`, a mensagem STUN com seus dois hashes (pinados aos vetores do RFC) e o agente que responde a uma verificação de conectividade - mas nada disso está em um socket, e três coisas ainda faltam: o relay TURN para o qual as chamadas do WhatsApp fazem fallback, o papel de servidor DTLS com o cronograma da chave do par e um caminho de áudio ao vivo, que o WP8.1 não expõe (a captura grava em arquivo e a plataforma não tem evento de frame de áudio). `docs/superpowers/specs/2026-11-04-calls-audio-feasibility.md` diz o que cada um deles exigiria. A tela de chamada diz a mesma frase enquanto a chamada está ativa.
</details>

## Comandos

Execute estes na raiz do repositório antes de um pull request. Os guardas são o contrato: cada um pega uma falha que a build não vê.

```
node tools/check-csharp5.js         sintaxe C# 6/7 e membros WinRT que o WP8.1 não oferece
node tools/check-icons.js           ícones vetoriais inline, formato dos elementos e geometria consistente
node tools/check-resw.js --strict   x:Uid, Loc.Get, os dois arquivos .resw e nenhuma chave sem uso
node tools/check-docs.js            grupo de READMEs, mesmos títulos, disclosure no fim, sem emoji
node tools/check-memory.js          largura de decodificação, limites de cache e limite por conversa
node tools/check-tile.js            conteúdo do tile, tamanhos dos arquivos e tiles recusados pela plataforma
node tools/check-waopus.js          DLL do Opus: ARMNT, sem KERNEL32, apenas imports API Set e exports
node tools/check-actions.js         botões da barra do app
node tools/check-fire-and-forget.js nenhuma tarefa cujo erro passe despercebido
node tools/check-project-files.js   todos os arquivos compilados pelos projetos
node tools/check-xaml-names.js      um x:Name por escopo de nomes
node tools/check-client-state.js    etapas passiva/ativa do login e canal de notificações
node tools/check-background-task.js classe ativável da tarefa, limite de 30 s e permissão do telefone
node tools/check-diagnostics.js     registro que permanece após uma falha
node tools/check-launch-route.js    toque no tile: o que ele registra e a inicialização que usa esse registro
node tools/check-notification-alerts.js  atualização e chamada perdida anunciadas uma vez, e badge que diminui
node tools/check-held-group.js       mensagem de grupo que aguarda a chave do remetente em vez de pedir reenvio
node tools/check-avatar-refusal.js   recusa da foto de um contato distinguida de uma sessão não iniciada
node tools/check-no-exception-probes.js  padding lido diretamente, sem adivinhar por meio de exceções
node tools/check-app-state.js       nomes salvos: chaves preservadas, sincronização solicitada e linhas atualizadas
node tools/check-chat-list-refresh.js  uma conversa por pessoa e lista que atualiza suas linhas
node tools/check-one-row-per-chat.js  linha de conversa criada uma vez e com endereço canônico: lista, grupos, status
node tools/check-emoji.js           emojis ausentes na fonte e emoticon digitado enviado como emoji
node tools/check-send-path.js       falhas no envio registradas em diag.log
node tools/check-device-identity.js mensagem que inicia uma sessão leva a assinatura da conta neste dispositivo
node tools/check-chat-list-source.js, check-history-sync.js, check-send-status.js,
     check-attachment-file.js, check-chat-scroll.js, check-no-server.js, check-status-viewer.js
```

A porta inteira, em uma linha:

```bash
for f in tools/check-*.js; do node "$f" || exit 1; done
node --test tools/test/*.test.js
node tools/test-protocol.js
```

`tools/test-protocol.js` compila e executa os 643 testes de protocolo em `ProtocolTests/`. Os testes de protocolo são C# puro com um executor pequeno, porque a toolchain do WP8.1 não consegue hospedar um framework de teste moderno.

## Bom saber

- Mensagens antigas são apenas as que o WhatsApp enviou após o login: este cliente não pede o histórico antigo de uma conversa sob demanda.
- Exclusões e edições feitas em outro dispositivo ainda não são lidas. Uma mensagem revogada em outro lugar permanece na tela até um novo login sincronizar novamente a conversa.
- Fixar, silenciar e excluir funcionam somente neste telefone, em `chat-preferences.json`. Nada sobre isso é enviado ao WhatsApp, então outro dispositivo não o vê e limpar os dados do app os perde.
- O número de não lidos de uma conversa vem do WhatsApp com a sincronização do histórico e é limpo quando você abre a conversa, o que informa o servidor. Mensagens que chegam enquanto o app está fechado são contadas pela próxima sincronização.
- Um vídeo recebido é escrito na pasta local do app e não é limpo quando o app fecha.
- As últimas 60 mensagens de cada conversa são gravadas no telefone quando a sincronização termina, então uma conversa aberta após reinicialização aparece imediatamente.
- Em um telefone de 512 MB, o app fica em torno de 185 MB. Ele decodifica cada imagem do tamanho em que é desenhada, descarta avatares decodificados sob pressão de memória e mantém no máximo 200 mensagens por conversa na memória. `node tools/check-memory.js` protege os três.
- Indicadores de digitação exigem que a conta esteja presente, que é o que os contatos veem como online. Com o app fechado, nada é enviado ou recebido.
- O tile ao vivo precisa de o app ter sido executado desde que a contagem mudou; uma mensagem que chega enquanto o app está suspenso é contada pela verificação em segundo plano.
- Um arquivo compartilhado para o app por outro app é lido no momento em que o compartilhamento é entregue, porque o app pode ser encerrado enquanto o seletor está aberto. Um vídeo muito grande pode não caber na memória em um telefone de 512 MB.

## Contribuindo

Issues e pull requests são bem-vindos. Leia `.agents/skills/maintain-the-app/SKILL.md` antes de mexer no código: C# 5 apenas, ícones vetoriais embutidos, sem strings visíveis ao usuário codificadas e documentação sempre sincronizada. O índice das habilidades do projeto está em `.agents/skills/README.md`.

Você também pode entrar no [nosso servidor do Discord](https://discord.gg/B8jncjBX6G) e contribuir
por lá. O servidor funciona somente em inglês. Se quiser ajudar, abra um ticket em
`#become-contributor` e inclua:

- Com o que você pode ajudar (programação em C#, arte, localização como português brasileiro ou italiano etc.)
- Por que quer contribuir
- Sua experiência
- Qualquer outra informação útil

1. Faça um fork e crie uma branch: `fix/...`, `feat/...`, `docs/...`.
2. Faça a mudança e rode o gate acima. Ele precisa estar verde.
3. Compile o app no Windows e diga no pull request que ele reporta `0 Error(s)`. Não existe host de testes C# para o app, então a build é o gate dele.
4. Escreva o assunto do commit como `tipo: curta ordem imperativa` em inglês e diga por que a mudança é necessária, não apenas o que ela faz. O histórico é o changelog.
5. Texto visível ao usuário vai em **ambos** `.resw`, e os três arquivos README mudam na mesma mudança.

Um bom relatório de bug tem as linhas `DIAG` da janela de saída, a build que você executou e o que o telefone fez.

Os documentos que explicam o projeto existem em três línguas e são mantidos em sincronia. Adicionar, mover ou renomear uma seção significa fazer isso nos três READMEs na mesma mudança, e a seção `## Disclosure` fica por último em todos. `node tools/check-docs.js` recusa um grupo cujos títulos difiram, um documento sem disclosure e qualquer emoji além do sinal de aviso.

## Aviso

- Este é um projeto não oficial, não afiliado ao WhatsApp ou à Meta.
- Ele usa métodos não oficiais para se conectar ao WhatsApp, o que viola os Termos de Serviço do WhatsApp.
- Vincular este app pode resultar em banimento permanente do número do telefone.
- Use apenas um número de teste ou secundário.

## Licença

MIT, mantido pela comunidade. Use por sua conta e risco.

## Disclosure

**Este projeto é open source e precisa de mantenedores.** Issues, traduções, revisões, documentação e pull requests são todos bem-vindos, e também qualquer pessoa que queira ajudar a fazer o projeto crescer.

**O autor não aceita responsabilidade pela conta do WhatsApp usada para entrar.** Vincular este cliente significa conectar um cliente não oficial ao WhatsApp, o que vai contra os Termos de Serviço do WhatsApp, e a conta pode ser banida permanentemente. Use um número de teste ou secundário e apenas se aceitar esse risco.
