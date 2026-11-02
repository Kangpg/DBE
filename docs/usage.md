# 사용법

[설치](installation.md)와 [연결](connection.md) 후 일반 VS Build 또는 확장의 DBE 명령을 사용할 수 있다.

### 일반 VS 빌드

Windows 사용자 계정의 일반 PowerShell 7에서 대상 솔루션을 한 번 등록한다. 해당 VS 버전에서 만든 새 Local 또는 Hybrid adapter를 사용한다. VS 2026에서 v143을 사용하는 경우 adapter 생성 시 `-VisualStudio 2026`을 지정한다.

```powershell
$solution = Read-Host '솔루션 전체 경로'
.\scripts\Set-NormalBuild.ps1 -Solution $solution -AdapterDirectory $adapter.AdapterDirectory
```

등록 후 솔루션을 다시 열고 **빌드 → 솔루션 빌드**(`Ctrl+Shift+B`) 또는 프로젝트의 **빌드**를 사용한다. 출력은 기본 **빌드** 창에서 확인하고 **빌드 → 취소**로 중단한다. Local은 요청 PC의 MSVC를 실행한다. Hybrid는 컴파일을 분배하고 요청 PC에서 링크한다. Worker의 compiler identity가 다르면 로그에 사유가 표시된다.

등록은 사용자 MSBuild 폴더에 저장된다. 일반 IDE 빌드에서 등록한 솔루션의 x64/v143 C++ 구성과 adapter의 Hostx64 compiler 경로가 일치할 때 적용된다. 별도 compiler나 C++ targets를 선택한 구성, 명령줄 MSBuild 및 디자인 타임 평가는 등록 대상에서 제외한다. 외부 프로세스의 사용자 폴더를 가상화하는 셸에서는 실제 IDE 계정의 일반 PowerShell을 사용한다.

같은 명령에 다른 adapter를 지정해 모드를 전환하거나 아래 명령으로 등록을 해제한다. 변경 후 솔루션을 다시 연다.

```powershell
.\scripts\Set-NormalBuild.ps1 -Solution $solution -Disable
```

### 확장 전용 명령

| 작업 | 메뉴 |
|---|---|
| 솔루션 Build/Rebuild | 확장 → DBE → DBE 솔루션 빌드/다시 빌드 |
| 프로젝트 | 선택한 C++ 프로젝트 우클릭 → DBE 프로젝트 빌드 |
| 상단 프로젝트 명령 | 빌드 → DBE 프로젝트 빌드/다시 빌드 |
| 중단 | 확장 → DBE → DBE 빌드 취소 |

현재 x64 구성과 솔루션 dependency/mapping을 따른다. PCH 생성과 링크는 요청 PC에서 실행한다. 기본 VS 취소와 DBE 취소는 별개다. 일상 빌드는 원래 codegen/배포 이벤트를 실행하므로 프로젝트의 부수 효과를 확인한다.

출력 창에서 `DBE Build`를 선택한다. UTF-8 로그는 `%LOCALAPPDATA%\DBE\builds\<id>\build.log`에 남으며 Git에 올리지 않는다.

| 이벤트 | 의미 |
|---|---|
| local-running / local-batch-running | 요청 PC 실행 시작 이벤트 |
| preparing | 원격 입력 전처리 |
| remote-ready | 준비 완료, 원격 슬롯 대기 가능 |
| remote-requested | 연결/업로드 시도, Worker compiler 시작 증거는 아님 |
| remote-completed | 반환 artifact 검증·반영 완료 |
| fallback-completed | 원격 배정 불가 후 로컬 완료 |
| admission-wait | CPU/메모리/PDB/슬롯 입장 대기 |

진짜 no-op은 컴파일·전처리·원격 요청이 없어야 한다. object hash 유지 하나만으로 판단하지 않는다. 원격 compiler 오류를 local retry로 숨기지 않는다.

## 합성 검증

일반 IDE 경로는 평소 사용하는 Windows 계정에서 아래 명령으로 확인한다. 테스트가 전용 솔루션을 등록하고 완료 후 등록을 해제한다. Hybrid 검사에는 compiler identity가 일치하고 가용 메모리가 정책을 충족하는 Worker가 필요하다.

```powershell
.\scripts\Test-NormalBuild.ps1 -LocalAdapter $local.AdapterDirectory -HybridAdapter $hybrid.AdapterDirectory
```

```powershell
.\scripts\Test-Worker.ps1 -PublishDirectory $build.Directory
.\scripts\Test-HybridIncremental.ps1 -AdapterDirectory $adapter.AdapterDirectory
.\scripts\Test-CompilerProfiles.ps1 -PublishDirectory $build.Directory `
  -RemoteClientConfig $node.ClientConfig -HybridPolicy $policy
```

Build host 취소 검사는 먼저 `Build-VisualStudioExtension.ps1`과 `Test-Worker.ps1`로 host/child를 생성한 뒤 `Test-BuildHost.ps1 -AdapterDirectory $adapter.AdapterDirectory`를 실행한다. fixture 통과가 외부 프로젝트 전체 지원을 보장하지 않는다.
