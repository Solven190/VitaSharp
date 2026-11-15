# Plugins (de kernel `.skprx` e de usuário `.suprx`)

*English: [plugins.md](plugins.md).*

Os plugins são uma opção do emulador, **Ativar Plugins do Kernel**, desligada por padrão (o Vita não tem essa
configuração, como pausar ou gravar a tela). Desligada, roda o firmware puro e nenhum plugin carrega. Ligada, o
VitaSharp carrega os plugins que um Vita com homebrew carrega, pela mesma configuração de plugins, roda o código
original deles e oferece a eles as bibliotecas do framework de plugins que eles importam. Nada no emulador é escrito
para um plugin ou homebrew específico.

Ligue em Configurações > Avançado (Android: Configurações > Depuração), com `--kernel-plugins=on` na linha de comando
ou `VITASHARP_KERNEL_PLUGINS=1`; a chave do launcher e a do app valem sobre a variável.

## Como configurar

Copie os plugins do seu próprio Vita para a pasta de dados (`ur0:tai` é `fs/ur0/tai` nela) e liste-os em
`fs/ur0/tai/config.txt` (ou `fs/ux0/tai/config.txt`, que vale quando os dois existem), o arquivo que o ambiente homebrew lê no
Vita:

```
# plugins de kernel: carregados no kernel e iniciados quando um programa inicia
*KERNEL
ur0:tai/algum_plugin_de_kernel.skprx
# plugins de usuário para todo programa
*ALL
# plugins de usuário para um título
*PCSE00000
ux0:tai/algum_plugin.suprx
```

Sem `config.txt`, ou com a opção desligada, nenhum plugin é carregado.

A configuração fica na pasta do próprio emulador, `ur0:kernel_plugins/` ao lado de `ur0:tai` (no computador
`fs/ur0/kernel_plugins/`; Windows, Linux, macOS e Android igual): `ur0:kernel_plugins/config.txt`, lido com as mesmas
regras. As linhas dele são caminhos do Vita (`ur0:tai/kubridge.skprx`): os plugins ficam em `ur0:tai` / `ux0:tai`, onde
os homebrews conferem os arquivos; uma linha sem dispositivo (`meu.skprx`) é um arquivo de `ur0:kernel_plugins/`
(caminho relativo à configuração); um arquivo que não está lá é pulado com um aviso no log. Sem o
`ur0:kernel_plugins/config.txt` é lida a configuração do Vita acima. Só o carregador do emulador lê a configuração. Na
primeira vez com a opção ligada e sem configuração na pasta, o `config.txt` do Vita vai para `ur0:kernel_plugins/` sem
mudanças (uma cópia fica como `ur0:kernel_plugins/config.txt.bak`; o log avisa); um plugin que uma versão anterior tinha
movido para a pasta volta para o caminho do Vita que o `.bak` indica. A pasta das versões anteriores, `kernel_plugins/`
ao lado de `fs/` na pasta de dados, vai uma vez para `ur0:kernel_plugins/` (cada item que ainda não está lá; a pasta
antiga vazia é apagada). Os plugins, o arquivo do próprio framework e os arquivos de dados dos plugins nunca são
movidos. Nada mais é apagado.

O arquivo é lido com as regras do parser e do carregador de plugins do próprio ambiente homebrew: só
um arquivo vale (`ux0:tai/config.txt`, senão `ur0:tai/config.txt`); `#` começa um comentário, `*` uma seção, e
qualquer outra linha é o caminho de um módulo (sem os espaços do começo e do fim); os nomes de seção são comparados
exatos, com maiúsculas e minúsculas (`*main` é o shell, os outros nomes são IDs de título); `*ALL` vale para todo
programa, mas não para `*KERNEL`; uma seção escrita `*!NOME` que bate termina a leitura na próxima seção; uma linha de
256 caracteres ou mais, um caminho antes da primeira seção ou uma seção sem nome faz o carregador de plugins recusar o arquivo
inteiro, e aí nenhum plugin carrega. Os módulos de uma seção carregam na ordem do arquivo, e um que falha não impede
os outros.

## O que acontece

- Os módulos de `*KERNEL` são carregados na região do kernel (uma área que todo processo mapeia, do processo do kernel)
  e o `module_start` deles roda na hora numa thread de kernel; um módulo cujo início não informa sucesso é descarregado
  de novo, como faz o gerenciador de módulos do firmware. O log diz o que aconteceu com cada um
  (`kernel plugin ...: loaded and started`, `... module_start of X returned 0x2, unloaded`).
- Os módulos de `*KERNEL` carregam uma vez, com o primeiro processo da sessão (o shell quando o sistema inicia, como o
  carregador de plugins os carrega no boot); um processo depois os encontra carregados.
- Os módulos de `*ALL` e `*TITLEID` são carregados no processo de todo programa, também nos do próprio sistema (o carregador
  de plugins os carrega depois dos módulos pré-carregados do processo), e iniciam
  antes da thread principal. A seção é o ID de título do processo no kernel: `main` para o shell, o NPXS de um app do
  sistema.
- Um módulo que importa uma biblioteca que ninguém fornece (nenhum módulo carregado e nenhuma biblioteca do firmware que
  o VitaSharp implementa) não é iniciado; o log diz quais bibliotecas (`not started: no module provides ...`).
- Os módulos de kernel chamam as bibliotecas de kernel do firmware (SceSysmemForDriver, SceThreadmgrForDriver,
  SceModulemgrForKernel, SceCpuForDriver...): o VitaSharp as implementa com o comportamento e os códigos de erro do
  firmware. Programas de usuário só chegam a um módulo de kernel pelas bibliotecas exportadas como syscall; módulos de
  kernel nunca se ligam a código de usuário.
- Os módulos de kernel do próprio firmware (todos os `os0:kd/*.skprx` e os módulos dentro do `bootimage.skprx`)
  estão na lista de módulos do processo do kernel antes do primeiro outro módulo de kernel carregar, com nome, NID de
  módulo e tabelas de exportação e importação (`ksceKernelGetModuleList`, `ksceKernelGetModuleCB`). As importações de
  um módulo de kernel se ligam às entradas de exportação deles, que são código ARM de verdade (um `svc` para a função
  do HLE): código que um módulo de kernel escreve por cima de uma entrada roda para quem chama por ela no kernel. Os
  programas de usuário continuam chegando às exportações de syscall pelos stubs do HLE. Precisa do firmware instalado
  (`os0` do PSVUPDAT.PUP); sem ele nenhum módulo do firmware aparece.
- A CPU emulada não tem níveis de privilégio, então a checagem de permissão das cópias usuário/kernel é feita pela
  biblioteca do kernel: o intervalo do usuário tem de ser memória do próprio chamador, não memória do kernel.

## As bibliotecas do framework de plugins

No Vita o módulo do framework do ambiente homebrew carrega primeiro e fornece o carregador de plugins e as bibliotecas
que os outros plugins e os homebrews importam. Com a opção ligada, o VitaSharp oferece ele mesmo essas bibliotecas
(declaradas só na tabela de NIDs): informação de módulo por nome ou NID de módulo (o registro de 0x38 bytes, do módulo
mais novo para o mais antigo), deslocamentos nos segmentos de um módulo, as entradas de exportação e importação dele,
injeção de dados (os bytes originais guardados e escritos de volta na liberação) e os códigos de erro do framework
(0x9001xxxx). Um módulo listado que exporta essas bibliotecas (a cópia do framework do usuário) não é carregado: o log
diz `not loaded (the emulator provides its libraries)`. Com a opção desligada as funções não fazem nada, como
importações que ninguém fornece. Os hooks de função são o próximo passo.

## Limites

- Os módulos de kernel vivem com o processo do programa para o qual foram carregados (o kernel ainda não é um processo
  único do sistema); são descarregados quando esse processo termina.
- Um plugin que corrige por deslocamento o código do próprio firmware não acha código do firmware ali (o VitaSharp
  implementa ele mesmo as bibliotecas do firmware).
- Exceções não são entregues a handlers de kernel (a CPU emulada relata uma falha do guest como queda); o mascaramento
  de interrupções é só estado (não há controlador de interrupções).
