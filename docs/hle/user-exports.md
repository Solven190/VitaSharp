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
- Situação no VitaSharp: `VitaSharp.Desktop hle-implemented` (os `[Hle]` e o módulo HLE de cada um) — dele sai quais
  bibliotecas o HLE serve.
- Tabela por função: [`data/user-exports.tsv`](data/user-exports.tsv) (os buracos: biblioteca, NID, nome, tipo,
  importadores, se está no VitaSDK).

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
