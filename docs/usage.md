# 사용법

[설치](installation.md)와 [연결](connection.md) 후 별도 DBE 명령을 사용한다. 수동 import가 없는 일반 VS Build는 기존 동작을 유지한다.

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

```powershell
.\scripts\Test-Worker.ps1 -PublishDirectory $build.Directory
.\scripts\Test-HybridIncremental.ps1 -AdapterDirectory $adapter.AdapterDirectory
.\scripts\Test-CompilerProfiles.ps1 -PublishDirectory $build.Directory `
  -RemoteClientConfig $node.ClientConfig -HybridPolicy $policy
```

Build host 취소 검사는 먼저 `Build-VisualStudioExtension.ps1`과 `Test-Worker.ps1`로 host/child를 생성한 뒤 `Test-BuildHost.ps1 -AdapterDirectory $adapter.AdapterDirectory`를 실행한다. fixture 통과가 외부 프로젝트 전체 지원을 보장하지 않는다.
