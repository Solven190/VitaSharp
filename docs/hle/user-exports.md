# Censo das exportações de usuário (FW 3.74)

Todas as funções que o firmware 3.74 exporta para código de usuário, quais chegam ao HLE do VitaSharp e, dessas, quais
ainda não têm implementação. É o par do censo de kernel (`kernel-exports.md`): lá ficam as bibliotecas
`*ForDriver`/`*ForKernel`; aqui, as syscalls dos módulos de kernel e as bibliotecas dos módulos de usuário.

## Como o console faz

- Código de usuário chama o firmware por dois caminhos: as **syscalls** que os módulos de kernel (`os0:kd/*.skprx`,
  inclusive os 57 embutidos no `bootimage.skprx`) exportam com o atributo 0x4000, e as bibliotecas dos **módulos de
  usuário** do firmware (`os0:us/*.suprx`: libkernel, driver_us, libgxm_es4, libgpu_es4, avcodec_us; e os de `vs0:`:
  `sys/external`, `vsh/common`, `vsh/shell`, apps de `vs0:app`). O modulemgr liga cada importação pelo NID ao módulo
  carregado que exporta a biblioteca; o sysmodule carrega os de `vs0:` sob pedido.
- No VitaSharp os módulos de kernel e os de `os0:us` não rodam: o HLE faz o papel deles, então **toda** função dessas
  bibliotecas que um programa chama precisa existir em HLE.
- Os módulos de usuário de `vs0:` rodam de verdade (LLE) quando o HLE não serve a biblioteca: o carregador
  (`Emulator.LoadFirmwareDependencies`) mantém em HLE uma biblioteca que tenha ao menos uma função HLE fora dos módulos
  marcados `FirmwareFallbackOnly` (libc, libpaf, sqlite, dbutil...: esses só valem quando o módulo real falta).
- Exceção (14/11/2026): software do sistema (um módulo de `vs0:` ou qualquer módulo de um programa do sistema) que
  importa de uma biblioteca servida pelo HLE uma função que o HLE não tem (as internas sem nome) recebe o módulo real
  para a biblioteca inteira, públicas incluídas, como o console faz (teste `FirmwareInternalsTests`). Um jogo continua
  no HLE. Com isso as internas de SceNpUtility, SceNpCommon, SceSslInternal, SceNetInternal, SceCommonDialog... que os
  apps do sistema chamam vêm do np_utility/np_common/libssl/libnet/libcdlg do firmware.
- Na contagem do `hle-coverage`, as funções que o HLE só tem como substituto do módulo do firmware
  (`FirmwareFallbackOnly`) contam como LLE quando o firmware instalado as exporta (`CoverageStatus`, teste
  `CoverageStatusTests`): antes contavam como HLE.

## Fonte e método

- Exportações e importações: `VitaSharp.Desktop hle-coverage --out=<tsv>` sobre o firmware instalado (359 módulos de
  `os0`/`vs0`, os do `bootimage.skprx` lidos um a um) e os 55 jogos instalados; nomes do `Hle/Data/nids.txt`
  (vita-headers; `NID_xxxxxxxx` = sem nome público). A saída agora traz quantos jogos e quantos módulos do sistema
  importam cada função (cada importador contado uma vez; teste `FunctionCensusTests`).
- Módulos reais (LLE) do firmware: todo `.suprx` de `vs0:` (`FirmwareLibraries`: primeiro `sys/external`, `vsh/common`,
  `vsh/shell`; depois as outras pastas, como `vsh/common/mms`, `app/NPXS*/` e `data/external/webcore`, que os programas
  carregam por caminho).
- Situação no VitaSharp: `VitaSharp.Desktop hle-implemented` (os `[Hle]` e o módulo HLE de cada um) — dele sai quais
  bibliotecas o HLE serve.
- Tabela por função: [`data/user-exports.tsv`](data/user-exports.tsv), gerada junto com o status por
  `build/hle-status.ps1` (`hle-coverage --status`, `HleCensusTables`; os buracos: biblioteca, NID, nome, tipo,
  importadores, se está no VitaSDK).

## Lote 15 (15/11/2026, feat/user-hle-15)

- SceMgVideo (29) e SceMgVideoForMiniApp (4), lidos no `os0:kd/mgvideo.skprx` 3.74 (e no `magicgate.skprx`): toda
  entrada pede o bit 0x81 da capacidade (SceSblACMgrForDriver_A27E47A7, senão 0x80010058); as que copiam um bloco de
  parâmetros da memória do usuário fazem a cópia (e os limites 0x400/0x40, 0x8002710B) antes disso. A abertura
  (`NID_C62CA5A7`, `NID_E8617A7F`, `NID_3EBD4502`) aceita só três zeros (senão 0x8050C002) e liga a emulação de
  MagicGate do `magicgate.skprx` (chaves do SceSblMgKeyMgr, identidade do cartão pelo SceMsif, licenças em
  `tm0:/drm/magicgate`). O VitaSharp não tem essas chaves nem o MagicGate do cartão: a abertura responde 0x8050C021 (o
  que o módulo dá quando o armazenamento do MagicGate não sobe) e o resto responde como sem sessão (0x8050C011; as duas
  liberações, `NID_8A803FB4`/`NID_553C65CB`, 0). Marcadas Default.
- SceRegMgr: `sceRegMgrResetRegistryLv`, `sceRegMgrDbBackup`, `sceRegMgrDbRestore` (SceRegistryMgr 3.74). O registro
  agora lê do template os campos 4 (nível de reset e flags 0x2000/0x4000; 0x80 vira 0x1000), 5 (tipos de aparelho) e 7
  (nível de backup) de cada chave. Reset: níveis 0/0x10/0x20/0x30/0x40/0x100/0x1000 (senão 0x800D000D), categoria sem
  chaves 0x800D0017, caminho de 0x100+ 0x800D000A. Backup: níveis 0/10/20/30/40/99 (senão 0x800D0020); as chaves de
  nível de reset >= 5 e nível de backup abaixo do pedido vão com o padrão de fábrica. Restore: chaves de nível de
  backup 5 ficam com o valor do aparelho, as outras recebem o do backup ou o padrão. O arquivo de backup usa o formato
  do armazenamento do VitaSharp (o registro não é o `system.dreg` do firmware).
## Lote 15b (15/11/2026, feat/marlin-hci)

- SceMarlinHci (44 exportações, as 20 importadas e o segundo conjunto de 24 que chama o primeiro com buffers próprios),
  lido no `os0:kd/marlin_hci.skprx` 3.74 (descompilado e desmontado, só estudo): toda entrada pede o bit 0x80 da
  capacidade (SceSblACMgrForDriver_F5AE24AC, senão 0x80010058); os blocos de parâmetros e os pares (tamanho, ponteiro)
  de 1 a 0xFFFF bytes são copiados do usuário; o motor roda numa pilha de 0x4000 e os erros dele são positivos (3
  argumento ou handle desconhecido, 2 sem memória, 0x1106xxxx arquivo, 0x2311xxxx motor), então a entrada grava 0 na
  palavra de saída e devolve o resultado dessa cópia. Todo objeto do motor sai das chaves do aparelho em
  `tm0:nphome/sdata/P0000000/P0000001.DAT` (P0000004.DAT para o segundo tipo), decifradas com a chave do aparelho;
  handles numa tabela de 32 (índice | 0x1000). Reais: início e parada do módulo (mutex novo / mutex apagado), lock e
  unlock do `SceMarlinHciMutex` (uma thread espera a outra), a checagem dos tipos 0/1 e a remoção do arquivo de chaves.
  O VitaSharp não tem a chave do aparelho: a leitura do arquivo responde como sem arquivo (0x11060002), então nenhum
  handle é criado, e a gravação de chaves responde 0x11060001 (arquivo que não pode ser gravado). Marcadas Default.
  Teste: `MarlinHciTests`. Sem caso no TestGame: o VitaSDK não tem stub do SceMarlinHci.

## Jogos de volta a 100% e LLE das bibliotecas que o HLE não emula (15/11/2026, docs/hle-status-15)

- Por que a coluna "jogos" caiu de 100% (2.049/2.049) para 99,1% (2.105/2.124): o status anterior (eef84396, 11/11) foi
  gerado com 52 jogos; depois entraram o runtime do PSM (PCSI00011), o TestGame (VSTEST001) e o vkQuake (VKQUAKE02), que
  importam 75 funções que nenhum outro jogo importa (66 do PCSI00011). O 908e583c só regenerou o status já com eles. As
  19 não cobertas eram todas do PCSI00011: SceFt2 (14, o FreeType do libSceFt2, chamadas pelo libpsm), SceDrmPsmKdc (4,
  o psmkdc que o runtime carrega pelo sysmodule) e `IPMI::Client::create` (SceIpmi do libshellsvc, chamado pelo eboot).
- Como o console faz: o modulemgr 3.74 pré-carrega em todo processo o libSceFt2, o libpvf, o libshellsvc, o libcdlg e o
  apputil só de `vs0:sys/external` (só libc e libfios2 têm caminho em `app0:sce_module`); o sysmodule carrega o psmkdc
  do firmware.
- Carregador: um jogo recebe o módulo do firmware de um pré-carregamento quando o HLE não conhece nenhuma das bibliotecas
  dele (libSceFt2) ou quando o programa importa, de uma biblioteca que o HLE só tem como substituto do firmware, uma
  função que o HLE não tem (libshellsvc, pelo `IPMI::Client::create`); `GameFirmwarePreloadTests`.
- Contagem: as funções de bibliotecas que o HLE não emula contam como LLE (o software do sistema recebe o módulo pelas
  dependências, o jogo pelos pré-carregamentos e pelas dependências do sysmodule: `HleRegistry.FirmwareServesImporters`).
  Antes elas contavam como faltando, mesmo rodando do módulo real.
- Resultado: jogos 2.124/2.124 (100%; real 1.654, default 6, LLE 464); sistema 6.080/7.327; geral 8.178/9.955; total
  16.382/19.406 (real 4.674, default 375, LLE 11.333). Os buracos que chegam ao HLE (a tabela deste censo) não mudam.

## Lote 14c (14/11/2026, feat/user-hle-14c)

- Cobertas 9.452 de 19.406 (HLE de verdade 4.674, default 375, LLE 4.403): +12 funções, todas lidas no módulo do
  FW 3.74 (SceAppMgr e SceCamera/SceUdcd do `bootimage.skprx`, SceMotion do `driver_us.suprx`).
- SceAppMgr: `sceAppMgrSuspendUntilActivated`, `sceAppMgrEnableCoredumpForTest`. SceCamera `NID_99B5FB62` (conjunto de
  buffers da câmera do processo), SceUdcd `NID_FCD876FB` (modo de energia do USB). SceMotion: `sceMotionGetStateExt`
  (forma empacotada de 10 bits), `sceMotionGetDeadbandExt`, `sceMotionGetTiltCorrectionExt`, os três limiares da
  orientação básica (`NID_CA0A798B`/`NID_F4E9D62E`), o zero do acelerômetro (`NID_02AC0F4B`) e as leituras dos outros
  dois clientes de movimento (`NID_D23686B0`, `NID_D66D5192`).
- Revalidado no SceMotion: `sceMotionGetState`/`GetSensorState` só respondem com a amostragem ligada (0x80360206) e
  NULL é 0x80360209; `sceMotionGetStateInternal` é a leitura do cliente de localização; `sceMotionSetAngleThreshold`
  aceita só 0 < ângulo <= 45; deadband e correção de inclinação guardam o valor recebido; porta Ext acima de 4 é
  0x8036020B.
- Ainda chegam ao HLE sem implementação e são importadas: 95 funções — SceGpuEs4User (41), SceMarlinHci (20),
  SceMgVideo (20), SceGpuEs4UserForVsh (5), SceRegMgr (3), SceGpuEs4ForUser (3), SceThreadmgr
  (`_sceKernelExitCallback`), SceDrmBridge e SceDrmBridgeUser (1 cada).

## Depois da regra do carregador (14/11/2026, release 82eec041 + esta branch)

- Cobertas 9.440 de 19.406: HLE de verdade 4.662, default 375, LLE 4.403 (com os substitutos `FirmwareFallbackOnly`
  agora contados como LLE).
- O que ainda chega ao HLE sem implementação e é importado: só syscalls e `os0:us`, 98 funções — SceGpuEs4User (41),
  SceMarlinHci (20), SceMgVideo (20), SceGpuEs4UserForVsh (5), SceRegMgr (3), SceGpuEs4ForUser (3), SceMotion,
  SceThreadmgr (`_sceKernelExitCallback`), SceUdcd, SceCamera, SceDrmBridge e SceDrmBridgeUser (1 cada).

## Resultado (14/11/2026, main b8cc636c)

- 19.406 funções exportadas a usuário; 9.435 cobertas (HLE, default ou LLE contado pelo `hle-coverage`).
- Das 9.971 sem `[Hle]`, 9.103 são de bibliotecas de `vs0:` que o HLE não serve: rodam do módulo real.
- **Buracos que chegam ao HLE: 868 funções, 387 delas importadas** por algum jogo ou módulo do sistema.
- Jogos: as funções de firmware que faltam aos jogos instalados são todas do runtime do PlayStation Mobile (PCSI00011:
  SceFt2, SceIpmi, SceDrmPsmKdc), que é a frente do PSM-B; os 11 `sceShaccCg*` do VitaSDK são do `libshacccg.suprx`, um
  módulo que não é do firmware e roda pelo código dele.
- VitaSDK: 48 das 7.006 funções de usuário sem cobertura; fora do PSM e do `libshacccg`, são sceRegMgr (4),
  sceAppMgr (2), PVRSRV (3), SceGxmInternal (2), `_sceKernelExitCallback`, `sceKernelAllocUnmapMemBlock` (já na release)
  e duas do libpaf (rodam do módulo real).

| Biblioteca | Tipo | Faltam | Importadas | Importadores (soma) |
| --- | --- | ---: | ---: | ---: |
| SceNpUtility | vs0 (np_utility.suprx) | 96 | 36 | 622 |
| SceNpCommon | vs0 (np_common.suprx) | 23 | 17 | 187 |
| SceSslInternal | vs0 (libssl.suprx) | 92 | 67 | 132 |
| SceNetInternal | vs0 (libnet.suprx) | 51 | 39 | 82 |
| SceMarlinHci | syscall (marlin_hci.skprx) | 44 | 20 | 60 |
| SceCommonDialog | vs0 (libcdlg.suprx) | 39 | 33 | 55 |
| SceNpWebApi | vs0 (np_webapi.suprx) | 16 | 13 | 55 |
| SceGpuEs4User | os0:us (libgpu_es4.suprx) | 48 | 41 | 41 |
| SceMgVideo | syscall (mgvideo.skprx) | 29 | 20 | 26 |
| SceNpSnsFacebook | vs0 (np_sns_facebook.suprx) | 54 | 23 | 25 |
| SceNpTrophy | vs0 (np_trophy.suprx) | 44 | 16 | 18 |
| SceLibLocation | vs0 (liblocation.suprx) | 11 | 11 | 16 |
| ScePhotoExport | vs0 (libScePhotoExport.suprx) | 6 | 6 | 15 |
| SceNpManager | vs0 (np_manager.suprx) | 12 | 2 | 14 |
| SceTriggerUtil | vs0 (trigger_util.suprx) | 7 | 3 | 11 |
| SceAppUtil | vs0 (apputil.suprx) | 10 | 8 | 8 |
| SceRegMgr | syscall (SceRegistryMgr) | 4 | 4 | 7 |
| SceGpuEs4UserForVsh / SceGpuEs4ForUser | os0:us / syscall | 8 | 8 | 8 |
| outras 32 bibliotecas | | 274 | 20 | 33 |

## Ordem dos lotes

1. Syscalls e `os0:us` pequenas (sem módulo real que possa rodar no lugar): SceRegMgr, SceThreadmgr, SceAppMgr,
   SceCamera, SceUdcd, SceDrmBridge(User), SceMotion, SceGxmInternal*, SceLibKernel.
2. SceMgVideo, SceMarlinHci, SceKrm, USB (SceUsbSerial, SceUsbDevSerial, SceUsbstor, SceUsbAudioIn).
3. SceGpuEs4User/ForVsh/ForUser: o cliente dos serviços do driver PowerVR (PVRSRV), que pede um modelo do driver.
4. As bibliotecas de `vs0:` servidas pelo HLE (SceNpUtility, SceNpCommon, SceSslInternal, SceNetInternal,
   SceCommonDialog...): feito pela regra do carregador acima (o software do sistema que chama internas recebe o módulo
   real); falta só o que um jogo chamar e o HLE não tiver (nenhum dos jogos instalados).

Cada lote: funções fiéis ao módulo do FW 3.74 (estudado no Ghidra), testes que falham sem elas e este censo refeito.
