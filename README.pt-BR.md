# Pavise — guia inicial em português brasileiro

> Este guia acompanha a versão 3.3.1.6 do código-fonte. Selecione **PT-BR** em **Configurações → Geral**. A interface e a visualização de ações podem ser exibidas em português. Os registros de diagnóstico e os arquivos de ações exportados mantêm o formato canônico. Consulte o [README em inglês](README.md) para a documentação completa e possíveis atualizações.

O Pavise é um aplicativo gratuito e de código aberto para Windows que gerencia recursos durante jogos. De acordo com as configurações escolhidas, ele pode reduzir a competição de aplicativos em segundo plano por CPU, disco e GPU, e restaurar as alterações temporárias ao final da partida.

## Antes de começar

- Requisitos: Windows 10 versão 2004 (compilação 19041) ou superior, .NET Framework 4.7.2 ou superior e privilégios de administrador.
- A maioria das funções é aplicada durante a partida e restaurada ao sair. As opções persistentes são identificadas separadamente; algumas exigem reinicialização. Arquivos excluídos e caches apagados não são restaurados.
- Evite usar o Pavise junto com outras ferramentas que alteram prioridade ou afinidade de processos: elas podem sobrescrever as configurações umas das outras.
- Dependendo das funções selecionadas, o aplicativo pode alterar serviços, configurações de energia e valores do Registro. Confira as opções antes de ativá-las.
- Os resultados variam conforme o jogo, o hardware, os drivers e a atividade em segundo plano.

## Instalação e uso

1. Baixe a versão no [site oficial](https://pavise.club/en/changelog/#latest) ou na [página de lançamentos do GitHub](https://github.com/dulaiduwang003/Pavise-Game/releases/latest). Extraia todos os arquivos do ZIP em uma pasta permanente.
2. Execute o programa como administrador e adicione o arquivo executável do jogo à biblioteca.
3. Revise a lista de aplicativos protegidos (*whitelist*) e as configurações do jogo. Inclua nela programas que precisam de recursos, como aplicativos de transmissão e de voz.
4. Escolha os recursos, o modo **Inteligente** ou **Competitivo** e o nível de controle de processos em segundo plano. Ative a proteção e inicie o jogo.
5. Ao terminar, confira o estado de restauração. Se necessário, exporte os registros da sessão para investigar problemas.

O modo **Inteligente** se adapta ao aplicativo em primeiro plano e à carga sustentada. Ao sair da janela do jogo, ele libera o controle em segundo plano. O modo **Competitivo** mantém esse controle, o plano de energia, a afinidade e o agendamento de threads durante a partida, inclusive quando você muda de janela. Também mantém as pausas já ativas de serviços do Windows Update e downloads em segundo plano, além do bloqueio de manutenção automática.

## Controle em segundo plano

**Normal** é o padrão. Os dois níveis verificam o EcoQoS, a prioridade de E/S 0 e a prioridade de memória 1. Na primeira aplicação por processo em cada partida, o Pavise define a prioridade de CPU como ociosa e ignora pedidos de resolução do temporizador. Alterações posteriores do aplicativo são preservadas. Ao voltar no modo Inteligente, só são reaplicados os ajustes restaurados ao sair da janela.

**Agressivo** limita cada processo comum em segundo plano a três núcleos lógicos e 10 MiB/s de disco. Em máquinas com até quatro núcleos lógicos, só o limite de disco é aplicado. Normal funciona sem objeto Job e sem limites de CPU ou disco.

Na **Lista de permissões → Controle reforçado**, o nível Agressivo compartilha o equivalente a um núcleo lógico e 10 MiB/s entre os processos agrupados de cada aplicativo selecionado e fixa sua prioridade como ociosa. Processos que não entrarem no grupo recebem os mesmos limites individualmente, e a entrada mostra Limite por processo. A lista começa vazia, usa o nome e o caminho do executável e continua valendo quando ele é reiniciado. Em máquinas com até dois núcleos lógicos, o limite de CPU não é aplicado. Aplicativos protegidos não podem ser adicionados e entradas que ganham proteção aparecem como inativas. Remover o aplicativo, mudar para Normal, desativar o controle, sair do jogo ou mudar de janela no modo Inteligente libera os limites. Reprodutores de música e aplicativos semelhantes podem engasgar.

## Supressão do anti-cheat ACE

Na Biblioteca, o **Ajuste avançado** oferece a supressão do anti-cheat ACE por jogo, desativada por padrão. Durante a partida, ela se aplica somente aos serviços ACE qualificados e seus clientes verificados instalados em Program Files\AntiCheatExpert. Usa as menores prioridades de CPU e E/S, prioridade de memória 1 e modo de eficiência, e ignora pedidos de resolução do temporizador. Um objeto Job por processo fixa a prioridade ociosa e o último processador lógico disponível. Se o processo não puder entrar no Job, são usados ajustes individuais. As configurações são restauradas ao encerrar a partida, sair do jogo ou desativar a opção, e na próxima inicialização após um encerramento inesperado. EasyAntiCheat, BattlEye, Vanguard e componentes incluídos na pasta do jogo recebem apenas proteção. Os anti-cheats ficam fora do controle comum em segundo plano.

## Recursos

| Recurso | Descrição |
| --- | --- |
| **Aumento de prioridade das threads do jogo** | Controle por jogo no Ajuste avançado da Biblioteca, ativado por padrão. O Pavise reserva uma sessão de rastreamento do sistema Windows ao iniciar quando algum jogo pode usar o recurso. |
| **Afinidade da thread de renderização** | Controle por jogo no Ajuste avançado da Biblioteca, desativado por padrão. Ao ativar, o Pavise pede confirmação e usa o modo salvo. Se não houver um modo salvo compatível, seleciona Um núcleo de renderização. Os dois modos permitem preservar o núcleo físico inteiro. |
| **Disfarce da GPU** | Na página Ambiente, escolha o modelo da GPU na lista para alterar no Windows os nomes das GPUs PCI dedicadas compatíveis. Os modelos dependem do fabricante da GPU e do tipo de dispositivo. GPUs Intel dedicadas usam nomes da NVIDIA. Notebooks e consoles portáteis usam modelos de notebook. Desktops e dispositivos de tipo desconhecido usam modelos de desktop. O cartão mostra o nome original e o nome visível de cada GPU. Todos os aplicativos veem os novos nomes. Confirme para reiniciar os drivers de vídeo ao trocar de modelo ou escolher Sem disfarce para restaurar os nomes originais. A seleção fica desativada durante a preparação ou execução de um jogo. Se a alteração estiver pendente, use Tentar novamente, exceto quando o Windows exigir a reinicialização do PC. Uma atualização do driver pode restaurar os nomes originais. |

## Afinidade da thread de renderização

| Modo | O que faz |
| --- | --- |
| **Um núcleo de renderização** | Fixa a thread de renderização no núcleo físico mais adequado. |
| **Manual** | Fixa a thread de renderização em um a quatro processadores lógicos escolhidos na grade de CPUs. Se a topologia ou a faixa de CPUs do jogo mudar, refaça a seleção. |

Os dois modos identificam a thread de renderização com amostragem leve dos eventos de apresentação de quadros. O modo Um núcleo de renderização dá prioridade aos núcleos de desempenho e depois aos preferidos pelo Windows (nos processadores X3D, ao grupo com mais cache L3). Em caso de empate, evita a CPU 0 e escolhe o núcleo de menor número.

Cada etapa de uma sessão de jogo gerenciada permite até duas coletas de cerca de 3,2 segundos. Se a sessão de coleta falhar ao iniciar antes de registrar qualquer evento, o Pavise tenta novamente até três vezes, aguardando 30, 60 e 120 segundos entre as tentativas, sem usar o limite de coletas. Alternar para fora da janela do jogo, sair da partida ou desativar a afinidade cancela as novas tentativas pendentes.

As configurações valem na próxima partida. Desativar a opção ou sair do jogo restaura os ajustes. As configurações salvas como Automático ou Coordenação multicore são tratadas como desativadas.

## Alterações na versão 3.3.1.6

- Ao relatar problemas, seja educado. O Pavise é gratuito e de código aberto, e o autor não deve nada a ninguém.
- Após a atualização, o controle em segundo plano volta ao nível Normal.
- O nível Insano foi removido; o controle em segundo plano mantém Normal e Agressivo.
- Normal aplica ajustes por processo, sem limites de CPU ou disco.
- Os dois níveis continuam verificando o modo de eficiência, a prioridade de E/S 0 e a prioridade de memória 1.
- A prioridade de CPU e a supressão de pedidos do temporizador em processos comuns são definidas na primeira aplicação de cada partida. No modo Inteligente, só são reaplicados os ajustes restaurados ao sair da janela.
- Aplicativos comuns em segundo plano mantêm os aumentos dinâmicos de prioridade do Windows e seus núcleos originais.
- Agressivo limita cada processo comum em segundo plano a três núcleos lógicos e 10 MiB/s de disco. Em máquinas com até quatro núcleos lógicos, só o limite de disco é aplicado.
- A lista de permissões ganhou o controle reforçado. No nível Agressivo, cada grupo divide um núcleo lógico e 10 MiB/s com prioridade ociosa fixa. Processos que não entrarem no grupo recebem limites individuais. Em máquinas com até dois núcleos lógicos, só o limite de disco e a prioridade fixa são aplicados.
- Os processos filhos do jogo continuam protegidos depois que o processo pai é encerrado.
- Os drivers de periféricos agora são reconhecidos por palavras-chave do processo e do programa, e não mais pela conexão HID.
- A proteção de periféricos adiciona palavras-chave para Incott, GearLink, ATK Hub, Attack Shark, G-Wolves, Endgame Gear, INZONE, 3Dconnexion e outros.
- As palavras-chave de periféricos e aceleradores ficaram mais específicas para reduzir isenções indevidas de aplicativos comuns.
- Adicionar processo na lista de permissões mostra processos sem janela, com os controlados no topo.
- A página Ambiente ganhou o disfarce da GPU, que permite exibir os nomes das GPUs dedicadas como modelos de entrada, de alto desempenho ou topo de linha.
- O Ajuste avançado ganhou a opção Baixa latência NVIDIA por jogo, desativada por padrão.
- O Ajuste avançado da Biblioteca agora usa um layout de duas colunas.
- A supressão de anti-cheat se aplica somente ao ACE e usa objetos Job para fixar a prioridade e a afinidade da CPU.
- Melhorias do sistema agora inclui o agendamento multimídia, que exige reiniciar o PC.
- As confirmações com contagem regressiva podem ser canceladas a qualquer momento.
- Corrigido o caminho rápido de início de processos, que nunca era acionado.
- O modo Automático foi removido da afinidade da thread de renderização. Permanecem os modos Um núcleo de renderização e Manual.
- O modo Um núcleo de renderização agora usa amostragem leve para identificar a thread de renderização mesmo quando há poucas sessões de coleta do sistema disponíveis.
- O aumento de prioridade das threads do jogo reserva uma sessão de rastreamento do sistema para reduzir falhas de identificação quando há poucas sessões disponíveis.
- Redução de coletas desnecessárias durante as partidas, ao sair da janela e na interface.
- Corrigida a contagem imprecisa de processos que usam a camada adicional do League of Legends.
- A gravação dos registros de recuperação é repetida quando o arquivo está em uso.
- Os registros pós-partida incluem o resultado da restauração do ReBAR.

## Compilação e testes

A compilação usa o compilador C# do .NET Framework e o Windows PowerShell incluídos no Windows. Instale também o Visual Studio Build Tools com as ferramentas MSVC x64 e um Windows SDK. O script localiza a instalação com `vswhere` e aceita um script de ambiente em `PAVISE_MSVC_ENV`.

```bat
build.cmd -b prod
```

O executável gerado fica em `build\Pavise.exe`. Para executar os testes públicos, incluindo as verificações de idioma e da apresentação dos registros:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Run-PublicTests.ps1
```

Consulte o [guia de testes](tests/README.md) e o [guia do código-fonte](docs/SOURCE-GUIDE.md) para detalhes e limitações.

## Informar um problema

Ao abrir uma [issue](https://github.com/dulaiduwang003/Pavise-Game/issues), inclua as versões do Pavise e do Windows, CPU e GPU, nome do jogo, configurações relevantes e etapas para reproduzir o problema. Os registros de ações mostram o que ocorreu na sessão; os registros de diagnóstico contêm detalhes técnicos.

Criado e mantido por [bdth](https://github.com/dulaiduwang003). O projeto é licenciado sob [GNU GPL versão 3 somente](LICENSE); consulte também [NOTICE](NOTICE).

A tradução para o português brasileiro é de **Psyducks2**. [Perfil no GitHub](https://github.com/Psyducks2) · [PR #35](https://github.com/dulaiduwang003/Pavise-Game/pull/35)
