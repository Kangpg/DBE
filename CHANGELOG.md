# 릴리스 노트

## 0.3.5 · 2026-11-04

병렬 컴파일의 PDB 서비스 소유권을 분리한다.

- 별도 compiler Job이 같은 MSPDBSRV에 연결하면 한 Job의 정상 종료·취소 정리가 다른 Job의 PDB 연결을 끊을 수 있었다. 현재 프로젝트 Hybrid에서 C1090 / RPC 0x6BA 오류를 관측했고, 이전 DLL의 실제 `/Zi /FS /MP` 두 세션이 같은 서비스를 사용하는 것을 재현했다.
- native wrapper와 managed native 감독 경로가 공통 환경 생산자를 사용한다. 실행마다 GUID 기반 `_MSPDBSRV_ENDPOINT_`를 부여하고 MP 자식은 같은 연결점을 상속한다. 나머지 Unicode 환경과 Windows 숨은 드라이브 항목은 보존하며 ambient 환경을 저장하지 않는다. 원래 PCH·`/Zi`·`/Fd`와 소유 Job 정리 규칙을 유지한다.
- 실제 MSVC로 별도 서비스 생성, 한 Job 취소 후 그 서비스 종료, 다른 서비스 유지·후속 병렬 컴파일·PDB 링크와 합성 프로그램 실행을 검증한다. 서비스 관측은 명시 Job 멤버십과 캡처한 프로세스 핸들·생성 시각으로 확인한다.
- 검증: Worker 73개, PCH 재사용·v7 배치·링크/실행·DIA 52개, native wrapper 계약 10개를 통과했다. 같은 새 회귀가 이전 DLL에서는 실패함을 확인했다. 0.3.5 VSIX의 assembly/manifest/menu 리소스와 설치 ZIP도 검증했다. 실제 사용자 IDE에 새 runtime/VSIX를 설치한 결과는 포함하지 않는다.
- 두 물리 Worker의 0.3.4 업데이트·등록 후 각각 49개 실제 TLS·v7 배치·PCH/PDB 재사용·오류·취소·링크·DIA 검증을 통과했다. 물리 Worker 종료 시 캐시 정리와 새 머신 자동 설치 전체를 검증한 것은 아니다.
- 실제 현재 프로젝트는 같은 입력·출력 볼륨에서 순정 367.69초, 수정 Local 342.84초, 두 Worker Hybrid 412.88초에 전체 링크를 완료했다. 세 모드 모두 컴파일·링크 없는 무변경 Build를 확인했다. Hybrid의 이번 관측은 순정보다 약 12.3% 느리다. 일반 앱·cache를 유지한 각 1회 결과이며 가속을 주장하지 않는다. 실패·중단 실행은 제외하고 [보고서](docs/benchmark.md)에 조건·전송량·중첩 대기를 기록한다.

## 0.3.4 · 2026-11-04

배포 등록 CMD의 기본 경로 해석을 수정한다.

- Worker에서 `Register-Requester.cmd`를 실행하면 Windows PowerShell 5.1의 parameter 기본값 평가 시 `$PSScriptRoot`가 빈 값이어서 requester 경로를 찾지 못했다. 경로 인자를 생략한 경우에만 script 본문에서 기본 위치를 해석하며 명시 입력은 보존한다.
- 기존 검증은 두 경로를 명시해 실제 배포 진입점 오류를 놓쳤다. 공개 bundle을 설치본에 풀고 다른 작업 폴더에서 CMD와 인자 없는 PowerShell script를 실행하는 회귀를 추가했다. 기존 설정의 멱등 보존을 함께 확인한다.
- 패키지 검증기가 절대 인증서 경로 앞에 다른 드라이브 경로를 붙이던 문제도 경로 소유 경계의 .NET 결합으로 수정했다. 원래 client 설정을 보존하면서 실제 TLS/컴파일로 확인했다.
- 0.3.4 framework/self-contained 빌드로 35개 회귀를 통과했다. 공개 등록 파일과 runtime을 묶은 ZIP을 설치본 사본에 덮고 실제 CMD 등록·mutual TLS·MSVC 컴파일도 확인했다. 0.3.3의 self-contained 배포 검사와 Worker 인증서·설정·toolchain 보존 규칙을 유지한다. 물리 Worker 등록·업데이트와 현재 프로젝트 성능 비교는 별도다.

## 0.3.3 · 2026-11-04

0.3.2 배포 전 검토에서 재현된 업그레이드 결함을 수정한다. 속도 측정 결과는 추가하지 않는다.

- `Package-WorkerUpgrade.ps1`이 framework-dependent runtime으로도 업그레이드 ZIP을 만들던 문제를 수정했다. 기존 Worker 사본에 적용하면 .NET 10을 찾지 못해 시작하지 못했다. 0.3.2에서 업그레이드 생산자를 다시 작성하며 초기 패키지의 self-contained 검사를 옮기지 않은 것이 원인이다.
- 배포 가능한 Worker runtime 판단을 `WorkerRuntime.ps1`의 공용 검사 하나로 옮겼다. `dbe-worker.runtimeconfig.json`을 해석해 `includedFrameworks`만 있는 self-contained 구성을 요구하고 `coreclr.dll`·`dbe-process.dll`도 확인한다. 초기 패키지와 업그레이드 모두 연결 데이터·산출물을 쓰기 전에 거부한다. 기존 초기 패키지의 `coreclr.dll` 존재 검사는 이 검사로 대체했다.
- `Test-WorkerPackage.ps1`은 ZIP 자체에 runtime이 있는지 확인한다. 설치된 Worker 사본 위 검증에서 기존 runtime 파일이 누락을 가리지 않는다.
- 검증: 0.3.3 framework/self-contained 빌드와 PowerShell 7.4.6으로 `Test-ConnectionRecovery.ps1` 33개 검사를 통과했다. 두 생산자가 실제 framework-dependent 빌드를 쓰기 전에 거부하는 검사를 추가했으며 `-PublishDirectory`는 framework-dependent 빌드여야 한다. 물리 Worker 업그레이드와 현재 프로젝트 성능 비교는 남아 있다.

## 0.3.2 · 2026-11-04

0.3.1 연결 복구 검토에서 찾은 두 생산 경계를 수정한다. 속도 측정 결과는 추가하지 않는다.

- Worker 업그레이드는 runtime-only다. ZIP에 `identity.pfx`·`worker.json`·`start-worker.cmd`를 넣지 않고 기존 Worker 폴더에 덮어 푼다. 이전에는 요청 PC에 남은 설치 시점 `worker.json`을 다시 배포해 `Register-Requester`로 교체한 pin을 이전 값으로 되돌릴 수 있었고, 복구한 Worker는 deployment가 없어 업그레이드할 수 없었다. `-ClientConfig`로 복구한 연결을 업그레이드하며 결과 deployment로 반복 업그레이드할 수 있다. bundled toolchain은 Worker 폴더에 유지된다.
- `-Compiler`는 설치 기록상 Worker가 이미 `toolchain\cl.exe`를 쓰는 경우에만 toolchain을 교체한다. compiler 선택 변경과 설정을 모르는 복구 Worker는 파일을 쓰기 전에 거부한다. 기존에 업그레이드로 compiler 출처를 바꾸던 흐름은 더 이상 지원하지 않는다.
- `Register-Requester`가 Worker 설정을 바꾼 뒤 기존 공개 `connection-<id>.json` 내용이 다르면 실패하던 문제를 수정했다. 파생 파일은 백업 후 다시 만들고 `ConnectionBackup`으로 보고한다. 요청 PC의 `client.json` 등 사용자 설정은 계속 덮어쓰지 않는다.
- `Test-WorkerPackage.ps1 -InstalledWorkerDirectory`로 설치된 Worker 사본 위 업그레이드를 검증한다. `Test-ConnectionRecovery.ps1`에 오래된 export 재생성, compiler 변경 거부, 복구 Worker 업그레이드 후 TLS·MSVC와 pin 유지, 불일치 requester의 실패 이유·설정 불변 확인을 추가했고 Windows PowerShell 5.1 host에서도 예상된 native 실패를 종료 코드로 판단한다.
- 검증: PowerShell 7.4.6에서 합성 loopback Worker 두 대와 self-contained 패키지로 31개 검사를 통과했다(실제 mutual TLS·MSVC, 등록은 Windows PowerShell 5.1). 물리 Worker 업그레이드·재등록, 기존 0.3.1 이하 Worker 폴더 위 실제 적용과 현재 프로젝트 성능 비교는 남아 있다. 남은 검토 항목: client 호스트 변경 시 import 갱신 경로, 업그레이드마다 장기 데이터 폴더에 쌓이는 client runtime 사본 정리 정책, 테스트가 `%TEMP%`에 남기는 합성 개인 키.

## 0.3.1 · 2026-11-03

연결 데이터의 수명을 빌드 산출물과 분리한다. `out` 정리로 요청 PC의 인증키가 없어지면 Worker가 켜져 있어도 TLS 연결이 끊기는 생성 경로를 수정했다.

- 초기 Worker 패키지와 업그레이드의 client 인증서·설정·실행 runtime·deployment 기록은 기본 `%LOCALAPPDATA%\DBE\connections`에 보관한다. 전달 ZIP과 Worker runtime/compiler payload만 `out`에 만든다. 데이터와 산출물 트리의 중첩 및 `out` 아래 영구 데이터 위치를 거부한다. 업그레이드는 기존 pair를 보존한다.
- 요청 PC 키 소실 시 사용하는 공개 인증서 기반 복구 도구를 추가한다. 개인 키는 요청 PC에 두고 Worker에는 등록 script·공개 인증서만 전달한다. 기존 Worker 인증서·compiler·state·수신 한도를 보존하고 `worker.json` 백업 후 허용 requester pin만 교체한다. 실행 중 또는 잠긴 Worker는 거부하며 반복 등록은 멱등적이다.
- Worker가 돌려준 공개 연결 정보는 requester ID·인증서 지문을 검증한 뒤 영구 client 설정으로 등록한다. 새로운 server 인증서를 자동 신뢰하지 않는다. 삭제된 개인 키를 복원했다고 주장하지 않으며, 새 키 등록 후 이전 요청자는 인증할 수 없다.
- Windows PowerShell 등록과 실제 두 합성 Worker의 mutual TLS·MSVC, 기존 설정/상태 보존·오류 거부·멱등성 및 산출물 경로 제거 뒤 설치/업그레이드 연결 등 26개 검증을 통과했다. 물리 Worker의 새 키 등록과 현재 프로젝트 성능 비교는 남아 있다. 이번 변경은 속도 개선 측정 결과를 추가하지 않는다.
- 기존 기본 `deployment.json` 위치가 영구 데이터 폴더로 이동한다. 반환된 `DeploymentFile`을 사용한다. WorkerConfig는 설치 설정 원본이며, 실행 Worker는 전달 ZIP에서 배치한다. 기본 인증서 유효기간 30일과 단일 허용 requester 정책은 유지하며 자동 갱신/다중 사용자 등록은 별도 작업이다.

## 0.3.0 · 2026-11-03

로컬 native 처리량 보존과 선택형 원격 공유 PDB 배치를 추가한다. **현재 프로젝트 속도 개선은 아직 재측정하지 않았다.**

- 요청 PC의 원래 컴파일·전처리·링크를 별도 native 감독 경로로 실행한다. NORMAL 우선순위와 원래 메모리 할당을 유지하며 incoming Worker의 8-process 제한을 자신의 `/MP` 작업에 적용하지 않는다. Job 소유권·취소·자식 정리 검증은 유지한다. 수신 Worker의 슬롯·메모리·우선순위 제한은 그대로다.
- 선택형 `preserveLocalParallelism`은 native Job 수(`nativeLocalJobs`, 기본 4)와 CL의 내부 `/MP` 병렬 수를 분리한다. 다른 프로젝트의 PCH 한 작업 때문에 전체 코어 예약을 기다리는 문제를 피한다. 이 모드의 자체 컴파일 메모리는 Windows가 관리하며 준비 작업은 별도 spare-commit 예산으로 제한한다. 기존 보수적 정책은 유지한다.
- 원격 v7은 같은 입력·옵션·PCH 컨텍스트의 2~4개 소스를 하나의 순차 CL 실행과 하나의 writable PDB로 처리한다. 객체별 검증 뒤 결과를 반환하고 공통 PDB는 한 번 전송·게시한다. `-NativeBatchFiles` 기본값 1은 v6를 유지하며 새 경로에는 업데이트된 Worker가 필요하다. Agent 전체의 영구 공유 PDB나 PDB delta 전송을 구현한 것은 아니다.
- 배치 전체 소스의 소속·원래 인수·객체 이름 충돌을 검증한다. 원격 컴파일 오류는 로컬 재시도로 숨기지 않고 부분 객체를 게시하지 않는다. 인프라 실패 시 같은 객체 소유권으로 원래 로컬 실행에 전환한다.
- 실제 MSVC/TLS의 재사용·무효화·오류·취소·4파일 링크/실행·DIA 타입 검증 52개, Worker 68개, 두 Worker identity 스케줄러 25개, MSBuild 프로필 10개와 local MP 취소를 통과했다. 합성 입력 검증이며 물리 Worker 두 대의 새 버전·현재 프로젝트 전체 성능 검증은 남아 있다.
- 다른 빌드와 겹친 예비 측정은 중단하고 성공 시간에서 제외했다. 일반 앱은 유지한다. Incredibuild 문서와 DBE의 실제 경계를 [별도 문서](docs/process-virtualization.md)에 기록했다. 전체 파일 접근·프로세스·레지스트리 가상화를 구현했다고 주장하지 않는다.

## 0.2.4 · 2026-11-03

참조 입력 추적 수정과 실제 검증 보고를 포함한다. Hybrid 측정은 엔진 0.2.3이며 수신 Worker 실행 코드·자원 한도는 변경하지 않았다.

- 평가된 ProjectReference의 소스 폴더를 최초 입력 검사에 포함한다. 상대 경로로 읽는 참조 헤더를 첫 컴파일 뒤에야 발견해 다음 Build를 재컴파일하던 Local 문제를 수정했다. 프로젝트 파일·PCH·compiler 옵션은 변경하지 않는다.
- 실제 MSBuild에서 참조 헤더 범위·없는 헤더의 추가/삭제·첫 무변경·일반 증분·오류 복구·명시 Rebuild 검증 15개를 통과했다. 현재 프로젝트 수정 Local 전체 354.69초와 첫 반복 Build 6.10초를 확인했다. 반복 실행은 compiler·linker·librarian·RPC 없이 객체 시각과 측정 입력을 보존했다. D:의 순정 참조는 전체 334.83초·반복 1.91초이며, 이번 단일 회차의 약 +5.9%를 처리량 동등성으로 해석하지 않는다.

- 현재 프로젝트 Build와 명시 참조에서 기존 PCH·옵션을 보존한 전체 링크를 확인했다. 로컬 656개와 두 물리 Worker의 원격 426개가 완료됐고 fallback은 없었다. 입력 전후 해시가 일치하고 링크 경고도 없었다.
- 컴파일·전처리·원격 요청 없는 반복 Build를 확인했다. 추가 링크는 실행되므로 전체 작업이 없는 no-op으로 표현하지 않는다.
- 이번 관측의 순정 로컬 326.54초와 Hybrid 1,014.07초를 기록했다. 일반 앱·VS 분석·cache 상태와 단일 회차 한계를 보존한다. 가속 달성이나 통제된 반복 성능으로 주장하지 않는다.
- 입력/PCH 재사용 및 실제 전송량을 집계했다. 원격 소비자 PDB 약 14.4GiB가 남은 비용이며, 예약·PDB·슬롯 대기의 겹치는 시간을 합산하지 않는다.
- 백그라운드 분석과 조회 프로세스를 구분할 출처 기록, 반복 빌드의 링크 확인, 출력 볼륨·용량 점검을 개발 스킬에 반영했다. Python bytecode를 Git에서 제외한다.

## 0.2.3 · 2026-11-03

### 수정

- OS NLS 데이터를 바이트로 복사해 읽기 전용 속성을 캐시에 상속하지 않는다. 이전 runtime이 남긴 캐시 복사본은 경로·내용을 검증한 뒤 해당 속성만 해제한다. 호스트 원본 파일은 변경하지 않는다.
- 요청 PC의 로컬 컴파일·전처리·링크에서 스케줄러 예약 추정치를 compiler의 강제 힙 한도로 사용하지 않는다. 입장 제어·취소·프로세스 소유권은 유지하며 수신 Worker의 메모리 제한도 유지한다.

### 검증과 남은 작업

- Worker 회귀 67개, 입력/PCH 재사용 36개, 프로세스 간 예약 24개를 통과했다. 읽기 전용 runtime 캐시의 정규화·축출·종료와 호스트 파일 속성 보존을 확인했다. 메모리 제한이 있는 실행의 거절과 로컬 실행의 정상 할당도 구분했다. 최종 런타임으로 실제 MSBuild Hybrid 프로필 9개도 통과했다(PCH/PDB/MP·링크·실행·무변경·오류 복구).
- 현재 프로젝트에서 C1060이 발생했던 로컬 fallback 단일 파일을 원래 PCH·옵션으로 다시 컴파일해 성공했다. 실행 98.239초, CPU 116.125초, 프로세스군 최대 commit 약 4.70GiB였다. 단일 파일 진단이며 전체 빌드 성능 비교가 아니다.
- 두 물리 Worker를 사용하는 전체 링크·무변경 검증은 남아 있다. 이전 실패 실행의 시간을 가속률로 해석하지 않는다. 기존 VS 기본 연결은 자동 전환하지 않는다.

## 0.2.2 · 2026-11-03

### 수정

- 정상 종료 시 공유 입력·runtime 파일 핀을 먼저 닫고 컨텍스트 트리를 제거한다. 종료 전 불확실한 정리나 컨텍스트 삭제 실패가 있으면 Worker 전체 소유자가 compiler·상태 잠금 등 남은 자원을 보존한다.
- `Repair-WorkerState.cmd`를 추가했다. 실행 중 Worker와 잠금을 확인하고, 설정을 원자적으로 백업·교체해 새 상태 폴더를 선택한다. 이전 컨텍스트·시도 폴더는 삭제하거나 이동하지 않는다. 인증서·주소·compiler 설정을 보존한다.
- 복구 도구의 Windows PowerShell 검증에서 증거·백업 보존, 변경 범위, 반복 실행, 잠금·잘못된 설정·실행 중 거부, 기존 pair의 실제 TLS 재시작을 확인했다.

### 범위

정리 실패 시 상태 소유권을 유지하는 검증을 추가했다. 복구는 남은 상태의 안전한 삭제를 판정하는 기능이 아니다. 두 물리 Worker의 수정 후 전체 프로젝트 링크·무변경 검증은 이어서 진행한다.

## 0.2.1 · 2026-11-03

### 수정

- 원래 설정에서 약 1GiB가 되는 PCH를 수용하도록 캐시 PCH 디스크 한도를 512MiB에서 1.5GiB로 조정했다. 컨텍스트·전체 캐시 예산과 각 MSVC의 메모리·시간·슬롯 제한은 유지한다.
- 확정된 산출물 크기 제한 실패를 컨텍스트에 기록해 같은 PCH를 파일마다 다시 생성하지 않는다. 산출물 종류·실제 크기·한도를 진단에 표시한다.
- v6 RPC 예산에 최초 입력 준비와 두 MSVC 실행을 반영한다. compiler 제한이 120초이면 RPC 전체 예산은 420초이며 각 compiler의 120초·2GiB 제한은 유지한다.
- 검증 산출물의 `-OutputRoot` 선택을 지원한다. C:에서 드러난 합성 PCH 검증의 TEMP 경로 중복 구현을 공통 mapped compiler 경로로 통합했다.

### 검증과 남은 작업

- 현재 프로젝트의 큰 PCH 두 사례에서 실제 MSVC·TLS 생성과 반복 컴파일을 통과했다. 반복 요청은 PCH를 재사용했다. 검증 프로세스군의 최대 commit은 약 1.73GiB였다.
- 재사용·무효화·정책 실패 반복 방지 검증 34개와 기존 Worker 회귀 66개를 통과했다.
- 전체 프로젝트 실행에서 512MiB 한도에 따른 로컬 전환과 PCH 재생성을 확인해 소유한 검증만 중단했다. 원본 소스 전후 해시는 일치했다. 수정 후 두 물리 Worker의 전체 링크·무변경·성능 비교는 추가 검증이 필요하다.
- Worker 업데이트는 창에서 Ctrl+C로 정상 종료하고 캐시 정리가 끝난 뒤 적용한다. 인증서·설정·toolchain을 보존한다.

## 0.2.0 · 2026-11-03

실험 기능을 포함한 개발 버전이다. 제품 버전 `0.2.0`과 통신 프로토콜 `v6`는 별개이며 기존 VS 기본 빌드 연결은 자동 전환하지 않는다.

### 변경

- 선택형 NativeInputs Hybrid에서 원본 소스·경로와 기존 PCH 설정·compiler 옵션을 보존한다.
- Worker가 동일 생성 명령으로 만든 입력 트리와 PCH를 생성 세대별로 재사용한다. 로컬 PCH 바이너리를 그대로 전송하는 방식은 아니다.
- 병렬 소비자는 기준 PDB의 독립적인 사본을 사용한다. 기준 PDB와 입력은 첫 전송 후 재사용하며 후속 요청은 작은 컨텍스트 참조를 보낸다.
- 헤더 내용·생성 옵션·로컬 PCH 생성 세대 변경 시 컨텍스트를 무효화한다. 캐시 용량·축출·소유권과 오류·취소 처리를 명시한다.
- 런타임 별도 설치형 Adapter의 .NET 입력 오인으로 발생한 무변경 재빌드를 수정했다.
- Worker에 `--version` 명령을 추가하고 Worker·VSIX 버전을 일치시켰다.

### 검증

- 실제 MSVC·TLS Worker 회귀 66개 통과.
- 로컬 재사용 검증 31개, 물리 Worker 두 대에서 각각 30개 통과.
- 원래 로컬 producer·원격 producer와 서로 다른 소비자 객체를 링크·실행하고 DIA로 최종 PDB 타입·멤버 정보를 확인했다.
- 실제 MSBuild PCH/PDB/MP·JMC·인코딩·오류 복구·무변경 검증 8개 및 실제 MP 자식 취소 검증 5개 통과.

### 남은 검증

현재 프로젝트 전체 검증은 다른 빌드 시작을 감지해 중단했다. 최종 링크·전체 성공·실제 프로젝트의 무변경 빌드·순정 대비 성능 비교는 남아 있다. 합성 결과를 현재 프로젝트의 가속률로 해석하지 않는다. `/GL`과 PCH를 함께 사용하는 프로필 등 미지원 항목은 원래 로컬 경로를 유지한다.

자세한 범위는 [원본 입력/PCH 설계](docs/native-pch.md), [벤치마크](docs/benchmark.md), [이슈 추적](docs/issues.md)을 따른다. 이전 변경은 Git 이력과 해당 문서에 기록돼 있으며 소급해 릴리스 버전을 부여하지 않는다.
