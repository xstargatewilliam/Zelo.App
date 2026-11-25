![Zelo — vigia o seu PC enquanto ele descansa](docs/banner.png)

# Zelo — vigia o seu PC enquanto ele descansa

Serviço do Windows + painel que cuida da manutenção do PC sozinho, quando você não está usando: **vistoria diária**, **revisão semanal** e **check-up mensal**. Ele mostra uma **nota de saúde** de 0 a 100 e só chama você quando algo realmente precisa de atenção.

## Instalação (uma vez)

**Jeito mais fácil:** na página **Releases** do repositório, baixe o `Zelo-win-x64.zip` mais recente. Ele já vem compilado: extraia, dê dois cliques em **`INSTALAR.bat`** e pronto, sem precisar do .NET.

**A partir do código-fonte:**

1. Extraia o zip numa pasta qualquer (ex.: `Documentos\Zelo`).
2. Dê dois cliques em **`INSTALAR.bat`** e aceite o pedido de administrador.
3. O script faz tudo:
   - instala o .NET 10 SDK pelo winget, se faltar;
   - compila o projeto;
   - instala em `C:\Program Files\Zelo`;
   - registra o serviço e abre o painel.

Na primeira vez leva uns 5 minutos, por causa dos downloads.

Se a compilação der erro, copie as mensagens vermelhas da janela e envie para correção.

Para remover: **`DESINSTALAR.bat`**. Ele pergunta se você quer voltar os ajustes da aba Otimizar antes de remover. Seus arquivos de backup nunca são apagados.

## Depois de instalar

- Na primeira vez, o Zelo abre um assistente de 3 passos: quando ele pode trabalhar (casa, trabalho com o **seu** expediente — inclusive noturno — ou jogos), o que copiar no backup, o que o Zelo pode instalar sozinho (você decide) e uma primeira vistoria em simulação.
- Depois, abra o painel pelo Menu Iniciar (**Zelo**) ou pelo ícone da lua perto do relógio. O pontinho do ícone fica verde, âmbar ou coral conforme a saúde do PC.
- **Simular antes** mostra o que cada verificação faria, sem apagar, instalar ou alterar nada. A seta ao lado de **Cuidar agora** roda ou simula a revisão semanal e o check-up mensal.
- No topo do Início, uma faixa mostra quando é a próxima vistoria e se há versão nova esperando (com **Instalar agora**).
- Em **Ajustes**, as opções marcadas com o selo **pede confirmação** abrem o aviso de administrador do Windows (UAC) quando você muda. Assim você não é pego de surpresa.
- Confirmações e resultados aparecem dentro do próprio Zelo (não em janelas do Windows), com o que deu certo, o que falhou e, quando dá, um botão para fazer à mão.
- No menu do ícone da bandeja dá para **pausar por 1 hora**.

## Atualização automática

Uma vez por dia o serviço consulta a página **Releases** deste repositório. Quando sai uma versão nova, ele:

1. baixa o `Zelo-win-x64.zip` direto do GitHub, por HTTPS;
2. confere o pacote com o SHA-256 publicado junto (se não bater, cancela);
3. guarda uma cópia da versão atual (se não conseguir, não troca nada);
4. para o serviço, troca os arquivos e o inicia de novo;
5. confere se a versão nova continua rodando por 30 segundos; se não, volta para a anterior e não tenta mais aquela versão sozinho;
6. reabre o painel na bandeja.

Nunca atualiza no meio de uma vistoria ou otimização, e a instalação automática só acontece no horário livre do seu perfil (e na tomada, se essa opção estiver ligada); fora dele, o Zelo só avisa que há versão nova. Para desligar, desligue **O próprio Zelo** em Ajustes → Atualizações; o painel continua avisando quando houver versão nova, e o botão **Procurar atualização** (Ajustes → Sobre) instala na hora. Qualquer pessoa do PC pode pedir a atualização: o Zelo só instala o pacote oficial publicado aqui, conferido pelo SHA-256.

**Para quem mantém o projeto:** suba o número em `<Version>` no `Directory.Build.props` e envie para o GitHub. O GitHub Actions compila e publica a versão em Releases sozinho.

## O que cada rotina faz

| Rotina | Tarefas |
|---|---|
| **Vistoria diária** | Atualização do antivírus + verificação rápida · saúde dos discos (SMART: desgaste, temperatura, erros) · espaço livre · erros graves no Visualizador de Eventos (telas azuis, desligamentos inesperados, falhas de disco) · reinicialização pendente · limpeza de temporários antigos · limpeza da pasta Downloads (opcional, só arquivos antigos) · backup incremental (guarda a versão anterior de cada arquivo alterado por 30 dias) |
| **Revisão semanal** | Ponto de restauração · Windows Update (sem upgrades de versão) · drivers pelo Windows Update (opcional, sem os de vídeo) · placa de vídeo: idade do driver e software do fabricante para atualizar · atualização de programas (winget, um por vez, até 15 min cada) · TRIM nos SSDs / desfragmentação só em HDs · Lixeira antiga (desligado por padrão) e cache de navegadores (Chrome, Edge, Firefox, Opera/Opera GX — opcional) · programas novos na inicialização |
| **Check-up mensal** | DISM + SFC · limpeza segura do WinSxS · verificação do sistema de arquivos (sem reiniciar) · auditoria de segurança (firewall, UAC, BitLocker, SMBv1, administradores novos) · verificação completa do antivírus · **teste do backup** (sorteia 25 arquivos e compara por SHA-256; é uma amostra, não a conferência de todos) · saúde da bateria · maiores arquivos · drivers muito antigos |

### Quando as rotinas rodam

- O serviço confere a cada 5 minutos se alguma rotina venceu.
- Ele só começa quando:
  - está no horário do perfil: **Casa** (qualquer hora), **Trabalho** (fora do expediente que você define, em horas e dias da semana — funciona para turno da noite, como 22h às 6h) ou **Jogos** (2h às 8h);
  - o notebook está na tomada (configurável);
  - o PC está ocioso (CPU baixa).
- Se o PC ficou desligado, a rotina roda na próxima oportunidade.
- O horário do perfil (expediente, madrugada) é uma regra rígida: nunca é ignorado, por mais atrasada que a rotina esteja. Se o horário livre acabar no meio de uma rotina (por exemplo, você estendeu o expediente), ela é interrompida e volta no próximo horário livre.
- O Zelo roda com prioridade baixa, para não disputar o processador com o que você está fazendo.

### O que o Zelo **não** faz (de propósito)

Ele não tem limpador de registro, "liberador de RAM", desfragmentação de SSD ou desativação de serviços. Essas coisas não trazem ganho real e podem quebrar o Windows. Programas na inicialização e drivers antigos são só **relatados**, nunca alterados.

## Otimizar o Windows

Na aba **Otimizar**, você escolhe como usa o PC (Jogos, Trabalho, Estudos, Edição e imagem, Programação e Dev — dá para marcar vários) e o Zelo já deixa marcados os ajustes recomendados para esses perfis. Você revisa, desmarca o que não quiser e aplica.

- **Antes de mudar qualquer coisa**, o Zelo cria um ponto de restauração e anota como cada item estava. Cada ajuste tem o próprio botão **Desfazer**, e há um **Desfazer tudo**.
- **O que ele faz**: remove apps de propaganda que vieram com o Windows (voltam pela Microsoft Store), tira sugestões e anúncios, reduz a telemetria ao obrigatório, ajusta desempenho e jogos, liga recursos úteis para quem programa e instala programas do perfil pelo winget.
- **O que ele nunca faz**: desligar o Windows Update, o antivírus, o Firewall ou o Controle de Conta de Usuário; remover a Microsoft Store, o Edge ou partes do sistema; usar scripts de terceiros.
- **Apps que vieram com o Windows** são removidos na **sua** conta (não pelo serviço), porque é lá que eles ficam instalados. Ligado = remover.
- Dá para **buscar**, ver só os **Marcados** ou **Já aplicados**, abrir os ajustes de **Outros perfis** e fechar os grupos que não interessam. O selo **pede confirmação** indica os grupos que abrem o aviso do Windows.
- Antes de aplicar, o Zelo mostra a lista do que vai mudar. No fim, mostra item por item o que deu certo e o que não deu, e por quê. Quando o próprio Windows bloqueia uma configuração (como os Widgets em algumas versões), o Zelo não força: mostra o caminho para fazer à mão.
- A seção **Avançado** mexe em áreas sensíveis sem desligar proteções (ex.: o Windows Update não reiniciar sozinho, verificação do antivírus mais leve, desligar a hibernação). Nada ali vem marcado.

## Registro (log)

A página **Registro**, no menu do Zelo, mostra ao vivo tudo o que o serviço fez no dia: cada vistoria, cada tarefa e, quando algo não dá certo, os detalhes do motivo (por exemplo, o código de erro do winget e o que ele significa). Cada rotina vira um bloco que abre e fecha, com selos coloridos de quantas coisas deram certo, avisos e problemas (os blocos com problema já abrem sozinhos). Clique com o botão direito numa linha para copiá-la. Dá para escolher o dia, filtrar por texto, ver **Só problemas**, **Copiar** para mandar a quem for ajudar e **Abrir pasta**. Os arquivos ficam em `C:\ProgramData\Zelo\logs`, um por dia, por 30 dias.

## Onde ficam as coisas

| O quê | Onde |
|---|---|
| Programa | `C:\Program Files\Zelo` |
| Configuração, histórico, logs, relatórios HTML | `C:\ProgramData\Zelo` |
| Backup | `<destino>\Zelo-Backup\` (versões anteriores em `_versoes\aaaa-MM-dd_HHmm`) |

## Testar pelo terminal (opcional)

Num Terminal **como administrador**:

```
"C:\Program Files\Zelo\Servico\Zelo.Service.exe" --rodar diaria --simulacao
```

Troque `diaria` por `semanal` ou `mensal`, e tire `--simulacao` para executar de verdade.

## Autor

Criado por **William Stargate** ([@stargaterequiem no X](https://x.com/stargaterequiem) · [@xstargatewilliam no GitHub](https://github.com/xstargatewilliam)). Distribuído sob a [licença MIT](LICENSE).

## Estrutura do código

```
src/
  Zelo.Core/      tarefas, agendamento, score de saúde, histórico, relatórios
    Tarefas/      uma classe por tarefa (TarefasDiarias/Semanais/Mensais.cs)
  Zelo.Service/   serviço do Windows (agendador + canal com o painel)
  Zelo.App/       painel WPF com ícone na bandeja e notificações
scripts/          instalar.ps1 / desinstalar.ps1
```

### Como adicionar uma tarefa

1. Crie uma classe herdando de `Tarefa`.
2. Registre a classe em `Catalogo.Todas`, em `Motor.cs`.

O resto acontece sozinho:

- painel;
- histórico;
- score;
- relatório;
- opção de desativar.

### Segurança

- O serviço roda como SYSTEM; o painel roda como o usuário, sem elevação, e conversa com o serviço por um canal local (named pipe) que recusa conexões pela rede.
- **Livre para qualquer pessoa do PC**: ver o painel, rodar e parar vistorias, pausar, silenciar avisos, atualizar o Zelo e mudar horários, perfil, limpeza comum e avisos de disco.
- **Pede a confirmação de administrador do Windows a cada vez** (o aviso do UAC): otimizar ou desfazer ajustes, mudar as pastas ou as versões do backup, ligar ou apertar a limpeza de Downloads, ligar ou apertar o esvaziamento da Lixeira (ele vale para todas as contas), ligar ou desligar instalações automáticas, desligar verificações de segurança ou de backup e confirmar alertas de segurança (como "esses administradores são meus"). Uma conta de administrador com o painel comum não basta: o Windows sempre pergunta. Desligar o que apaga é livre.
- **O serviço nunca apaga nem escreve fora do lugar certo**: as limpezas (Temp, Downloads, cache, Lixeira) e o backup trabalham em pastas que outras contas controlam. Antes de apagar, o Zelo abre cada arquivo e pergunta ao Windows onde ele está de verdade; se uma pasta foi trocada por um atalho (junção ou link) que leva para outro lugar, nada sai de lá. O destino do backup também é conferido, e num disco NTFS fora das pastas de perfil a pasta `Zelo-Backup` fica com escrita só para o sistema e administradores (as outras contas leem, para recuperar arquivos).
- **A pasta de dados (`C:\ProgramData\Zelo`) é protegida** pelo instalador e de novo pelo serviço sempre que ele inicia: só o sistema e administradores escrevem nela.
- **Silenciar não é resolver**: um aviso silenciado sai da lista, mas continua contando na nota até uma nova verificação não encontrar mais o problema. Já quando você explica o que aconteceu ("foi queda de energia"), o achado conta como resolvido até a próxima verificação.
- **Otimizar é transacional**: o Zelo anota como cada item estava antes de mudar, confere se a mudança pegou e, se o PC desligar no meio, desfaz o item incompleto quando o serviço volta. Se o ponto de restauração falhar, ele para e pergunta. Apps da Loja são removidos só da sua conta e voltam pela Microsoft Store (não pelo botão Desfazer).
- Uma operação pesada por vez: vistoria, otimização e atualização do Zelo nunca rodam juntas.

### Limitações conhecidas

- O programa ainda não tem assinatura digital: o Windows mostra "Fornecedor desconhecido" ao abrir o que foi baixado. As atualizações são conferidas pelo SHA-256 publicado no próprio GitHub, que garante que o pacote chegou inteiro, mas não é uma prova independente de quem o publicou.
- Durante o backup, o robocopy escreve no destino como SYSTEM. O Zelo confere o destino antes de cada cópia; num destino dentro da pasta de perfil de outra conta (onde essa conta pode mexer), um atalho criado no meio da cópia ainda poderia desviar a escrita. Prefira um disco externo ou uma pasta sua.
- O aviso do UAC mostra "Zelo", mas não qual ação será feita: confirme só quando você mesmo clicou em algo no Zelo.

- Programas instalados só para um usuário podem não ser atualizados pelo winget, porque o serviço roda como SYSTEM.
- O Windows cria no máximo um ponto de restauração a cada 24h.
- A tarefa avisa se a Restauração do Sistema estiver desligada.
- BitLocker não existe no Windows Home. Nesse caso a auditoria mostra "não disponível".
