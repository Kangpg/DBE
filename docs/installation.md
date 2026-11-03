# 설치와 소스 빌드

실험용 소스다. 성능 보장과 수신 PC의 자동 사용자 보호는 아직 제공하지 않는다.

## 요청 PC

Windows x64, PowerShell 7, VS 2022 또는 Build Tools의 C++ v143/Windows SDK, .NET SDK 10.0.402가 필요하다. SDK는 `worker/global.json`에 고정돼 있다. 현재 build script는 표준 `C:\Program Files\dotnet\dotnet.exe`를 사용한다. VSIX 빌드에는 Visual Studio SDK와 .NET Framework 4.7.2 targeting pack도 필요하다.

```powershell
# 저장소 루트, PowerShell 7
Set-ExecutionPolicy -Scope Process Bypass
$build = .\scripts\Build-Worker.ps1 -SelfContained
.\scripts\Build-Wrapper.ps1 -VisualStudio 2022

# 실제 Worker 주소를 직접 입력한다.
$workerAddress = Read-Host 'Worker IPv4'
$package = .\scripts\Package-Worker.ps1 -PublishDirectory $build.Directory `
  -WorkerAddress $workerAddress -Compiler $build.Compiler -BundleCompiler
$package
```

Worker마다 새 pair를 만든다. `WorkerZip`만 해당 Worker에 전달한다. `ClientConfig`, `ClientExecutable`와 `deployment.json`은 기본 `%LOCALAPPDATA%\DBE\connections`의 장기 보관 폴더에 생성된다. `-ConnectionDirectory`로 다른 위치를 지정할 수 있지만 `out` 아래는 거부한다. ZIP·compiler payload 등 전달 산출물만 `out`에 생성한다. WorkerConfig는 장기 보관한 설치 설정 원본이며 실제 Worker 실행 폴더는 전달 ZIP에서 만든다. 인증서/개인 키/config와 compiler 사본을 Git에 넣지 않는다. Compiler 재배포 권한은 이 프로젝트가 부여하지 않는다.

기존 연결 파일을 잃었다면 [요청 PC 인증 연결 복구](connection.md#요청-pc-인증파일을-잃었을-때)를 따른다. 상태 정리용 Repair는 요청 PC의 삭제된 개인 키를 복원하지 않는다.

## Worker PC

ZIP을 전부 풀고 `start-worker.cmd`를 실행한다. 기본 TCP port는 43127이다. Self-contained runtime과 compiler를 묶었다면 Worker에 .NET SDK나 VSIX는 필요하지 않다. 현재 console 실행이며 서비스 자동 시작은 지원하지 않는다. Ctrl+C로 종료한다.

남은 컨텍스트·attempt 때문에 재시작이 거부되면 [상태 복구 절차](worker-install.md)를 따른다. `Repair-WorkerState.cmd`는 종료 상태를 확인하고 설정 백업과 새 상태 경로만 만든다. 기존 상태를 삭제하거나 이동하지 않는다.

연결이 방화벽에 막힐 때에만 Worker 폴더의 관리자 PowerShell에서 실행한다:

```powershell
$initiatorAddress = Read-Host '허용할 요청 PC IPv4'
.\Allow-WorkerFirewall.ps1 -InitiatorAddress $initiatorAddress
```

현재 Worker의 허용 동시는 1–2개이고 기본값은 2개다. 작업당 2GiB, compiler 120초 한도다. 코어 수를 그대로 슬롯으로 설정하지 않는다. 사용자 활동에 따른 자동 양보는 미완성이므로 Worker 사용자가 영향을 받으면 직접 중지한다.

## VSIX

```powershell
$extension = .\scripts\Package-VisualStudioExtension.ps1
$extension.Zip
```

생성 ZIP을 풀고 작업 저장 후 설치 대상 VS 창을 닫아 `Install-DBE.cmd`를 실행한다. `-InstanceId <ID>`로 인스턴스 선택, `-CheckOnly`로 대상 확인이 가능하다. 설치기는 IDE를 강제 종료하지 않는다. Worker 연결은 [연결 가이드](connection.md)를 따른다.
