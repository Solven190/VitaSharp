# Censo das exportações de kernel (FW 3.74)

Todas as funções que os módulos de kernel do firmware 3.74 exportam para outros módulos de kernel (bibliotecas sem o
atributo de syscall 0x4000: `*ForDriver`, `*ForKernel`, `*ForDebugger`, `*ForTool`), quais o VitaSharp implementa em
HLE, quais são "default" (sem hardware por trás, devolvem o valor documentado) e quais faltam. As exportações de
syscall (o que o código de usuário chama) ficam no censo do usuário (`progress.md`).

## Como o console faz

- O kernel do Vita é um conjunto de módulos `.skprx` em `os0:kd`; o `bootimage.skprx` carrega mais 57 deles embutidos
  (SceCtrl, ScePower, SceAppMgr, SceNpDrm, SceVshBridge...). Cada módulo exporta bibliotecas de kernel; um módulo de
  kernel (do firmware ou um plugin) se liga a elas pelo NID, pelo mesmo gerenciador de módulos (SceKernelModulemgr).
- `*ForDriver` são as bibliotecas estáveis entre versões; `*ForKernel` mudaram de NID no 3.63 (ex.:
  SceSysmemForKernel 0x63A519E5 no 3.60 -> 0x02451F0F no 3.63+, com as funções renumeradas; vita-headers `db/363`).
  Os plugins feitos para o 3.60 que usam `*ForKernel` não se ligam no 3.65+ sem tradução; os que usam só `*ForDriver`
  funcionam nos dois.
- No VitaSharp os módulos de kernel do firmware não rodam: o HLE faz o papel deles, para o código de usuário e para os
  plugins de kernel que o usuário instala (`docs/notes/plugins.md`). Então tudo o que um plugin pode importar precisa
  existir aqui, com o comportamento e os códigos de erro do firmware.

## Fonte e método

- Exportações e importações: as tabelas de exportação/importação dos `os0:kd/*.skprx` instalados (decifrados; os
  módulos embutidos no `bootimage.skprx` lidos um a um), 106 módulos, 3.918 funções em 115 bibliotecas.
- Nomes: vita-headers `db/360` + `db/363` (`NID_xxxxxxxx` = sem nome público; 2.037 funções).
- Situação no VitaSharp: os atributos `[Hle]` do código e os NIDs de `Hle/Data/nids.txt`.
- "Importadas" = pelo menos um módulo de kernel do firmware (ou plugin instalado) importa a função: é a ordem de
  implementação (o que o kernel de verdade usa primeiro).
- Tabela por função: [`data/kernel-exports.tsv`](data/kernel-exports.tsv) (biblioteca, NID, nome, situação, método do
  VitaSharp, quem importa).

## Plugins instalados (ur0:tai)

- `kubridge.skprx`: 33 importações; todas as de firmware em HLE; 5 de `taihenForKernel`/`taihenModuleUtils`
  (dependem do taiHEN rodar).
- `fd_fix.skprx`: 2 importações, ambas do taiHEN.
- `taihen.skprx`: 60 importações; todas as de firmware em HLE (o `module_start` dele falha sozinho, ver
  `docs/notes/plugins.md`).
- 12 funções já implementadas só tinham o NID do 3.60 (SceModulemgrForKernel, SceSysmemForKernel,
  SceCpuForKernel, SceExcpmgrForKernel): o firmware 3.74 as exporta com o NID novo.

## Feito no k2 (12/11/2026)

- `Hle/Data/nids.txt`: os 3.397 NIDs de kernel do 3.74 que faltavam (nomes do vita-headers; nomes de C com prefixo `k`;
  sem nome público, `<Biblioteca>_<NID>`). Com isso as funções `*ForKernel` já implementadas ligam também o NID novo
  do 3.63+ (GetModuleInfo/List/CB, Stop/UnloadModuleForPid, ksceGUIDKernelCreateWithOpt).
- `Hle/Modules/KernelTwins.cs`: 401 entradas de kernel (`*ForDriver`/`*ForKernel`) que no firmware são a mesma função
  da syscall de usuário com os mesmos argumentos (protótipos psp2kern x psp2 conferidos) rodam o HLE de usuário.
  Fora: SceAppMgrForDriver (aguarda coordenação) e as de argumentos diferentes (implementar à parte).
- Teste: `KernelFw374ExportsTests` (módulo de kernel do `SceModuleBuilder` chamando os NIDs do 3.74 pelos stubs;
  falha na main).

## Por biblioteca (depois do k2, 12/11/2026; a coluna "antes" é a main)

| Biblioteca | Módulo (NID da biblioteca) | Funções | HLE+default antes | HLE | default | faltam | faltam e são importadas | sem nome |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| SceAVConfigForDriver | SceAVConfig (0xECC77DDB) | 6 | 0 | 0 | 0 | 6 | 2 | 6 |
| SceAppMgrForDriver | SceAppMgr (0xDCE180F8) | 76 | 3 | 3 | 0 | 73 | 24 | 22 |
| SceAudioForDriver | SceAudio (0x15D711C1) | 20 | 0 | 3 | 0 | 17 | 10 | 0 |
| SceAudioInForDriver | SceAudioin (0x60928C22) | 10 | 0 | 4 | 0 | 6 | 5 | 0 |
| SceAvcodecForDriver | SceAvcodec (0x66845469) | 105 | 0 | 23 | 0 | 82 | 1 | 82 |
| SceBacktraceForDriver | SceKernelModulemgr (0x77CB3DD6) | 3 | 0 | 1 | 0 | 2 | 0 | 2 |
| SceBbmcForDriver | SceBbmc (0x2907BCF0) | 63 | 0 | 0 | 0 | 63 | 14 | 63 |
| SceBtForDriver | SceBt (0xD48CA62D) | 54 | 0 | 7 | 45 | 2 | 1 | 1 |
| SceBusErrorForKernel | SceKernelBusError (0x193156B1) | 1 | 0 | 0 | 0 | 1 | 0 | 1 |
| SceCameraForDriver | SceCamera (0xBCBC1F4A) | 25 | 0 | 0 | 0 | 25 | 2 | 24 |
| SceCdramForDriver | SceLowio (0xC8CD941E) | 3 | 0 | 0 | 0 | 3 | 0 | 3 |
| SceClockgenForDriver | SceClockgen (0xFF160234) | 14 | 0 | 0 | 0 | 14 | 8 | 13 |
| SceCodecEngineWrapperForDebugger | SceCodecEngineWrapper (0x615771E2) | 1 | 0 | 0 | 0 | 1 | 0 | 1 |
| SceCodecEngineWrapperForDriver | SceCodecEngineWrapper (0x51593F20) | 50 | 0 | 0 | 0 | 50 | 47 | 50 |
| SceCodecForDriver | SceCodec (0x7697DFB4) | 9 | 0 | 0 | 0 | 9 | 7 | 0 |
| SceCompatForDriver | SceCompat (0x7CFA62CD) | 1 | 0 | 0 | 0 | 1 | 1 | 1 |
| SceCoredumpForDriver | SceCoredump (0xA351714A) | 21 | 0 | 0 | 0 | 21 | 19 | 8 |
| SceCpuForDriver | SceSysmem (0x40ECDB0E) | 104 | 8 | 80 | 0 | 24 | 13 | 1 |
| SceCpuForKernel | SceSysmem (0xA5195D20) | 39 | 3 | 3 | 0 | 36 | 6 | 5 |
| SceCsiForDriver | SceLowio (0xD85C8E44) | 6 | 0 | 0 | 0 | 6 | 6 | 6 |
| SceCtrlForDriver | SceCtrl (0x7823A5D1) | 40 | 1 | 11 | 0 | 29 | 21 | 8 |
| SceDebugForDriver | SceSysmem (0x88758561) | 16 | 1 | 1 | 0 | 15 | 7 | 0 |
| SceDebugForKernel | SceSysmem (0x13D793B7) | 17 | 0 | 0 | 0 | 17 | 6 | 11 |
| SceDebugLedForDriver | SceSysmem (0x7BC05EAD) | 9 | 4 | 4 | 0 | 5 | 1 | 0 |
| SceDipswForDriver | SceSysmem (0xC9E26388) | 4 | 0 | 3 | 0 | 1 | 1 | 0 |
| SceDisplayForDriver | SceDisplay (0x9FED47AC) | 53 | 0 | 18 | 0 | 35 | 19 | 4 |
| SceDmacmgrForDriver | SceKernelDmacMgr (0xEB4E3738) | 15 | 0 | 2 | 0 | 13 | 10 | 2 |
| SceDsiForDriver | SceLowio (0xEC897883) | 15 | 0 | 0 | 0 | 15 | 10 | 3 |
| SceExcpmgrForKernel | SceExcpmgr (0x1496A5B5) | 17 | 1 | 1 | 0 | 16 | 5 | 15 |
| SceFios2KernelForDriver | SceFios2Kernel (0x54D6B9EB) | 20 | 0 | 0 | 0 | 20 | 9 | 6 |
| SceGpioForDriver | SceLowio (0xF0EF5743) | 10 | 0 | 0 | 0 | 10 | 7 | 0 |
| SceGpsForDriver | SceGps (0x4E75DFF2) | 7 | 0 | 0 | 0 | 7 | 6 | 7 |
| SceGpuEs4ForDriver | SceGpuEs4 (0x54802381) | 31 | 0 | 0 | 0 | 31 | 28 | 8 |
| SceGrabForDriver | SceLowio (0x81C54BED) | 8 | 0 | 0 | 0 | 8 | 5 | 0 |
| SceHdmiForDriver | SceHdmi (0x58A1A1C5) | 25 | 0 | 0 | 0 | 25 | 19 | 25 |
| SceHidForDriver | SceHid (0xB6000991) | 30 | 0 | 28 | 0 | 2 | 1 | 2 |
| SceHpremoteForDriver | SceHpremote (0xFD0B0E27) | 11 | 0 | 0 | 0 | 11 | 6 | 0 |
| SceI2cForDriver | SceLowio (0xE14BEF6E) | 8 | 0 | 0 | 0 | 8 | 3 | 2 |
| SceIdStorageForDriver | SceIdStorage (0xE1AC6D7C) | 17 | 0 | 0 | 0 | 17 | 16 | 0 |
| SceIftuForDriver | SceLowio (0xCAFCFE50) | 9 | 0 | 0 | 0 | 9 | 9 | 3 |
| SceIntrmgrForDriver | SceKernelIntrMgr (0x9DF04041) | 23 | 0 | 0 | 0 | 23 | 13 | 1 |
| SceIntrmgrForKernel | SceKernelIntrMgr (0xDF44892E) | 21 | 0 | 0 | 0 | 21 | 12 | 20 |
| SceIofilemgrForDriver | SceIofilemgr (0x40FD29C7) | 148 | 4 | 47 | 0 | 101 | 75 | 18 |
| SceKernelSuspendForDriver | SceSysmem (0x7290B21C) | 28 | 0 | 1 | 0 | 27 | 12 | 24 |
| SceKernelUtilsForDriver | SceSysmem (0x496AD8B4) | 57 | 0 | 1 | 0 | 56 | 33 | 14 |
| SceLcdForDriver | SceLcd (0xFA916D71) | 23 | 0 | 0 | 0 | 23 | 13 | 15 |
| SceLedForDriver | ScePower (0x282C1323) | 1 | 0 | 0 | 0 | 1 | 1 | 0 |
| SceMagicGateForDriver | SceMagicGate (0x4EA9847A) | 48 | 0 | 0 | 0 | 48 | 37 | 48 |
| SceModulemgrForDriver | SceKernelModulemgr (0xD4A60A52) | 15 | 9 | 9 | 0 | 6 | 4 | 0 |
| SceModulemgrForKernel | SceKernelModulemgr (0x92C9FFC2) | 48 | 0 | 5 | 0 | 43 | 23 | 5 |
| SceMotionDevForDriver | SceMotionDev (0xA501409A) | 23 | 0 | 6 | 3 | 14 | 7 | 12 |
| SceMsifForDriver | SceMsif (0xB706084A) | 12 | 0 | 0 | 0 | 12 | 8 | 6 |
| SceMtpIfForDriver | SceMtpIfDriver (0x73309DFE) | 17 | 0 | 0 | 0 | 17 | 11 | 17 |
| SceNetPsForDriver | SceNetPs (0xB2A5C920) | 20 | 0 | 12 | 0 | 8 | 3 | 7 |
| SceNpDrmForDriver | SceNpDrm (0xD84DC44A) | 26 | 0 | 0 | 0 | 26 | 20 | 0 |
| SceOledForDriver | SceOled (0x60C7478A) | 21 | 0 | 0 | 0 | 21 | 12 | 13 |
| ScePervasiveForDriver | SceLowio (0xE692C727) | 156 | 0 | 0 | 0 | 156 | 88 | 131 |
| ScePfsFacadeForKernel | ScePfsMgr (0xC26DC7BD) | 6 | 0 | 0 | 0 | 6 | 6 | 6 |
| ScePfsMgrForKernel | ScePfsMgr (0xA067B56F) | 14 | 0 | 0 | 0 | 14 | 11 | 5 |
| ScePmMgrForDriver | SceSysmem (0xF13F32F9) | 2 | 0 | 0 | 0 | 2 | 1 | 0 |
| ScePowerForDriver | ScePower (0x1590166F) | 109 | 36 | 36 | 0 | 73 | 36 | 23 |
| SceProcEventForDriver | SceSysmem (0x887F19D0) | 3 | 2 | 2 | 0 | 1 | 1 | 0 |
| SceProcessmgrForDriver | SceProcessmgr (0x746EC971) | 46 | 4 | 10 | 0 | 36 | 25 | 35 |
| SceProcessmgrForKernel | SceProcessmgr (0xEB1F8EF7) | 60 | 0 | 3 | 0 | 57 | 30 | 49 |
| ScePsmDrmForDriver | SceNpDrm (0x9F4924F2) | 6 | 0 | 0 | 1 | 5 | 2 | 0 |
| ScePwmForDriver | SceLowio (0xECEAE2D0) | 5 | 0 | 0 | 0 | 5 | 3 | 5 |
| SceQafMgrForDriver | SceSysmem (0x4E29D3B6) | 25 | 0 | 0 | 0 | 25 | 23 | 14 |
| SceRegMgrForDebugger | SceRegistryMgr (0xCC88EAB1) | 9 | 0 | 0 | 0 | 9 | 0 | 9 |
| SceRegMgrForDriver | SceRegistryMgr (0xB2223AEB) | 25 | 21 | 21 | 0 | 4 | 0 | 0 |
| SceRegMgrForTool | SceRegistryMgr (0x1F121C9E) | 13 | 0 | 0 | 0 | 13 | 0 | 13 |
| SceRegMgrServiceForDriver | SceRegistryMgr (0x12431F66) | 5 | 5 | 5 | 0 | 0 | 0 | 0 |
| SceRtcForDriver | SceRtc (0x0351D827) | 65 | 0 | 16 | 0 | 49 | 25 | 30 |
| SceSblACMgrForDriver | SceSblACMgr (0x9AD8E213) | 45 | 1 | 1 | 0 | 44 | 39 | 22 |
| SceSblACMgrForKernel | SceSblACMgr (0x11F9B314) | 47 | 0 | 0 | 0 | 47 | 12 | 24 |
| SceSblAIMgrForDriver | SceSysmem (0xFD00C69A) | 17 | 0 | 0 | 0 | 17 | 17 | 0 |
| SceSblAuthMgrForDriver | SceSblAuthMgr (0x4EB2B1BB) | 3 | 0 | 0 | 0 | 3 | 3 | 0 |
| SceSblAuthMgrForKernel | SceSblAuthMgr (0x7ABF5135) | 9 | 0 | 0 | 0 | 9 | 7 | 1 |
| SceSblFwLoaderForDriver | SceSblPostSsMgr (0x6FE424E4) | 3 | 0 | 0 | 0 | 3 | 3 | 0 |
| SceSblGcAuthMgrDrmBBForDriver | SceSblGcAuthMgr (0x1926B182) | 20 | 0 | 0 | 0 | 20 | 7 | 17 |
| SceSblGcAuthMgrGcAuthForDriver | SceSblGcAuthMgr (0xC6627F5E) | 1 | 0 | 0 | 0 | 1 | 1 | 0 |
| SceSblGcAuthMgrMlnpsnlForDriver | SceSblGcAuthMgr (0x29ED0109) | 2 | 0 | 0 | 0 | 2 | 2 | 1 |
| SceSblGcAuthMgrMsSaveBBForDriver | SceSblGcAuthMgr (0x5032E8D4) | 6 | 0 | 0 | 0 | 6 | 6 | 6 |
| SceSblGcAuthMgrPkgForDriver | SceSblGcAuthMgr (0x082FBA7D) | 1 | 0 | 0 | 1 | 0 | 0 | 0 |
| SceSblGcAuthMgrPsmactForDriver | SceSblGcAuthMgr (0x1C53F37D) | 1 | 0 | 0 | 0 | 1 | 1 | 1 |
| SceSblGcAuthMgrSclkForDriver | SceSblGcAuthMgr (0xF24F760D) | 2 | 0 | 0 | 0 | 2 | 0 | 2 |
| SceSblMgKeyMgrForDriver | SceSblMgKeyMgr (0xE1856C6D) | 14 | 0 | 0 | 0 | 14 | 2 | 14 |
| SceSblPostSsMgrForDriver | SceSblPostSsMgr (0x2254E1B2) | 44 | 0 | 0 | 5 | 39 | 25 | 0 |
| SceSblSmCommForKernel | SceSblSsSmComm (0xCD3C89B6) | 4 | 0 | 0 | 0 | 4 | 4 | 0 |
| SceSblSmSchedProxyForKernel | SceSblSmschedProxy (0x15F25C84) | 16 | 0 | 0 | 0 | 16 | 9 | 2 |
| SceSblSsMgrForDriver | SceSblSsMgr (0x61E9428D) | 40 | 0 | 0 | 0 | 40 | 33 | 3 |
| SceSblSsMgrForKernel | SceSblSsMgr (0x74580D9F) | 5 | 0 | 0 | 0 | 5 | 5 | 1 |
| SceSdifForDriver | SceSdif (0x96D306FA) | 59 | 0 | 0 | 0 | 59 | 31 | 42 |
| SceSysclibForDriver | SceSysmem (0x7EE45391) | 51 | 4 | 4 | 0 | 47 | 27 | 0 |
| SceSysconForDriver | SceSyscon (0x60A35F64) | 311 | 0 | 0 | 0 | 311 | 198 | 239 |
| SceSysmemForDriver | SceSysmem (0x6F25E18A) | 114 | 22 | 22 | 0 | 92 | 57 | 14 |
| SceSysmemForKernel | SceSysmem (0x02451F0F) | 169 | 1 | 2 | 0 | 167 | 75 | 140 |
| SceSysrootForDriver | SceSysmem (0x2ED7F97A) | 181 | 0 | 0 | 0 | 181 | 111 | 156 |
| SceSysrootForKernel | SceSysmem (0x3691DA45) | 143 | 2 | 2 | 0 | 141 | 65 | 105 |
| SceSystimerForDriver | SceSystimer (0xA47EB09A) | 14 | 0 | 0 | 0 | 14 | 8 | 4 |
| SceThreadmgrForDriver | SceKernelThreadMgr (0xE2C40624) | 248 | 22 | 89 | 0 | 159 | 72 | 136 |
| SceThreadmgrForKernel | SceKernelThreadMgr (0x7F8593BA) | 64 | 0 | 0 | 0 | 64 | 15 | 63 |
| SceTouchForDriver | SceTouch (0xA4A7DAFC) | 29 | 0 | 15 | 0 | 14 | 8 | 10 |
| SceUartForKernel | SceSysmem (0x1CCD9BA3) | 4 | 0 | 0 | 0 | 4 | 1 | 0 |
| SceUdcdForDriver | SceUdcd (0xBC05A8FB) | 47 | 0 | 3 | 0 | 44 | 31 | 15 |
| SceUlobjMgrForDriver | SceUlobjMgr (0xD6A45C93) | 19 | 0 | 0 | 0 | 19 | 12 | 19 |
| SceUsbAudioForDriver | SceUsbAudio (0x066BBE8B) | 5 | 0 | 0 | 0 | 5 | 0 | 0 |
| SceUsbEtherRtlForDriver | SceUsbEtherRtl (0x6AC379A7) | 3 | 0 | 0 | 0 | 3 | 0 | 3 |
| SceUsbMassForDriver | SceUsbMass (0x15243EC5) | 6 | 0 | 0 | 0 | 6 | 6 | 6 |
| SceUsbMtpForDriver | SceUsbMtp (0xF0122DB1) | 12 | 0 | 0 | 0 | 12 | 12 | 12 |
| SceUsbServForDriver | SceUsbServ (0xA75BBDF2) | 7 | 0 | 0 | 2 | 5 | 4 | 0 |
| SceUsbdForDriver | SceUsbd (0xA0EBCA41) | 21 | 0 | 0 | 0 | 21 | 20 | 0 |
| SceUsbstorForDriver | SceUsbstorDriver (0x5D01DDE4) | 9 | 0 | 0 | 0 | 9 | 7 | 9 |
| SceWlanBtForDriver | SceWlanBt (0xFD94FCE9) | 4 | 0 | 0 | 0 | 4 | 2 | 0 |
| SceZlibForDriver | SceSblPostSsMgr (0xE241534E) | 65 | 0 | 0 | 0 | 65 | 6 | 65 |
| **Total** | | **3918** | **154** | **504** | **57** | **3357** | **1834** | **2037** |
