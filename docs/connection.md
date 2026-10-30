# 요청 PC와 Worker 연결

설치 시 생성된 pair를 유지한다. 테스트를 위해 인증서를 새로 만들지 않는다.

```powershell
$deploymentFile = Read-Host 'deployment.json 전체 경로'
$node = Get-Content $deploymentFile -Raw | ConvertFrom-Json
$client = Get-Content $node.ClientConfig -Raw | ConvertFrom-Json
Test-NetConnection $client.host -Port 43127
& $node.ClientExecutable info $node.ClientConfig
.\scripts\Test-RemoteWorker.ps1 -DeploymentFile $deploymentFile
```

TCP 성공만으로 TLS/컴파일 성공을 판단하지 않는다. 실제 반환 object의 링크·실행까지 확인한다. 기본적으로 compiler binary-directory identity 일치를 요구한다. 이 identity는 전체 SDK 일치 증명과 다르다.

## Hybrid Adapter

```powershell
$root = (Get-Location).Path
$policyDir = Join-Path $root ('out\policy-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $policyDir | Out-Null
$policy = Join-Path $policyDir 'host.json'
@{
  localSlots=4; remoteSlots=2; localMemoryMiB=4096
  reserveMemoryMiB=4096; waitSeconds=1200; cpuBusyPercent=85
  localBatchSlots=1; preprocessMemoryMiB=2048
} | ConvertTo-Json | Set-Content $policy
$adapter = .\scripts\New-MSBuildAdapter.ps1 `
  -Destination (Join-Path $root ('out\adapter-' + [Guid]::NewGuid().ToString('N'))) `
  -PublishDirectory (Join-Path $root 'out\worker-build\portable') `
  -ClientConfig $node.ClientConfig -Mode Hybrid -HostPolicy $policy
$adapter.AdapterDirectory
```

예시 예산은 최적값이 아니다. localSlots는 논리 CPU 수보다 작게 하고 실제 workload와 메모리에 맞춰 결정한다. 같은 policy 디렉터리의 Adapter가 예약을 공유한다. 모든 policy/수신 Worker를 통합한 머신 서비스는 아니다. 새 Adapter 경로를 사용해 실행 중 runtime을 덮어쓰지 않는다.

설치 경로가 깊으면 `-StateDirectory C:\DBE\state\<새-id>`처럼 짧은 절대 경로를 지정한다. 상대 경로는 Adapter 디렉터리를 기준으로 해석한다. MSVC와 MSBuild Tracker에는 긴 경로 제약이 있으므로 Windows long-path 설정만으로 해결되지 않는다. local object는 state 아래 짧은 전용 폴더에서 생성한 뒤 최종 위치 옆 임시 파일을 거쳐 원자적으로 반영한다. 취소 후 프로세스 정리가 확인되지 않은 staging은 보존한다.

## Local 전용 비교

같은 생성 스크립트에 `-Mode Local`을 지정하면 native wrapper가 선택된 MSVC의 인자·PCH·MP·진단을 그대로 전달한다. 이 모드는 Worker에 요청하지 않고, Hybrid의 그룹 분할이나 Worker용 프로세스·메모리·시간 한도를 적용하지 않는다. 생성 API는 현재 기존 `-ClientConfig` 경로를 요구하지만 Local 실행에는 사용하지 않는다.

새 Adapter를 생성해서 사용한다. `msbuild.json`의 mode만 바꾸면 실행 파일과 추적 설정이 맞지 않는다. 선택된 compiler는 UTF-16LE `.compiler` sidecar에 기록되며, 잘못된 sidecar를 환경 변수로 우회하지 않는다. Local 출력에는 Hybrid의 `local-running` 이벤트 대신 원래 compiler 진단이 나온다.

다른 Worker는 별도 package/pair를 만든 뒤 새 Adapter 생성에 `-AdditionalClients @($otherNode.ClientConfig)`로 추가한다. 같은 인증서의 중복 등록은 거부한다. Worker별 실행 2개, 준비 티켓은 실행 중인 항목을 포함해 최대 4개다. 양방향 enrollment와 공정성은 미완성이다.

솔루션 옆 `<solution>.dbe.json`에 아래 내용을 저장한다. 기존 설정은 백업하고 필요한 경로만 변경한다:

```json
{"adapterDirectory":"C:\\DBE\\out\\adapter-example"}
```

실제 `$adapter.AdapterDirectory`를 사용한다. VSIX와 수동 targets import를 중복 적용하지 않는다. 경로 오류는 현재 폴더와 `scripts` 철자를 확인하고, 실행 정책은 현재 PowerShell 범위에서 처리한다. lease 파일을 삭제해 입장 제어를 우회하지 않는다.
