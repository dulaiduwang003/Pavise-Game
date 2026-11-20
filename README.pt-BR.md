# Pavise — guia inicial em português brasileiro

> Este guia acompanha a versão 3.3.1.3 do código-fonte. Selecione **PT-BR** em **Configurações → Geral**. A interface e a visualização de ações podem ser exibidas em português. Os registros de diagnóstico e os arquivos de ações exportados mantêm o formato canônico. Consulte o [README em inglês](README.md) para a documentação completa e possíveis atualizações.

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

O modo **Inteligente** se adapta ao aplicativo em primeiro plano e à carga sustentada. O modo **Competitivo** mantém o controle de atividades em segundo plano durante a sessão, inclusive quando você muda de janela.

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
