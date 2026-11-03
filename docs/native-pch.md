# 원본 입력과 원격 PCH 검증

2026-11-03 최신 검증: 엔진 0.2.3으로 현재 프로젝트 Build와 명시 참조의 전체 컴파일·링크를 통과했다. 두 물리 Worker에서 총 426개 원격 완료와 398개 PCH hit를 확인했다. 원본 입력 전후 해시는 일치하고 반복 Build에서 compiler·RPC가 없었다. 반복 링크는 남아 있으며 속도 가속은 달성하지 못했다. [전체 측정과 한계](benchmark.md)를 따른다. 아래 이전 버전의 미완료 기록은 해당 시점의 범위다.

## v6: 입력 트리와 PCH 재사용

선택형 `Hybrid -NativeInputs` 경로에 재사용 컨텍스트를 추가했다. 프로젝트·솔루션 파일이나 PCH 설정, compiler 옵션을 바꾸지 않는다. 원래 `/Yc`가 로컬에서 성공한 세대를 기록하고, Worker가 같은 생성 명령과 논리 경로로 PCH를 한 번 만든 뒤 해당 세대의 `/Yu` 작업에서 재사용한다. **로컬 PCH 바이너리를 그대로 전송하는 방식은 아니다.** 소스 전처리본 전송도 하지 않는다. 최종 링크는 요청 PC에서 실행한다.

컨텍스트 identity는 compiler identity, 입력 목록의 경로·SHA-256·길이·mtime·deferred 상태, 작업 디렉터리, `INCLUDE`, 원래 PCH 생성 옵션·소스·`Fp`·`Fo`·생성 세대를 포함한다. 헤더 내용, 생성 옵션이나 로컬 `/Yc` 세대가 바뀌면 새 컨텍스트를 만든다. 파싱된 정의가 입력 배열을 소유하므로 호출자의 배열 변경으로 이전 검증을 우회할 수 없다. 소비자는 선언된 소스와 허용된 compiler 옵션만 선택할 수 있다.

Worker는 입력 트리를 한 번 구성하고 입력과 완료된 PCH·producer 객체·기준 PDB를 읽기 전용 handle로 고정한다. 같은 컨텍스트의 초기화만 직렬화하며, 준비된 컨텍스트의 소비자는 서로 다른 출력·임시 디렉터리에서 병렬 실행한다. PCH를 사용하면서 빈 PDB를 주면 C2859가 발생하므로, 각 소비자에 기준 PDB의 쓰기 가능한 사본을 준다. 반환된 소비자 PDB와 기준 PDB는 요청 PC에서 링크까지 보존한다. 기준 PDB는 첫 수신 후 client 캐시에서 복사하며 후속 요청마다 전송하지 않는다.

후속 요청은 파일 목록 전체 대신 검증된 컨텍스트 key와 소비자 명령을 보낸다. Worker 재시작이나 축출로 key를 찾지 못하면 전체 정의로 한 번 재협상한다. 필요한 deferred 입력은 새 정의로 추가한다. 반복 요청의 원본 정의는 상태 폴더에 공통 증거로 한 번 저장하고 개별 영수증에서 참조한다. 공통 증거 한도는 64개·1 GiB이며 한도 초과 시 영수증에 미보존 상태를 명시한다.

재사용 컨텍스트는 최대 4개·논리 크기 4 GiB로 제한하고 활성 사용자가 없는 오래된 컨텍스트를 축출한다. 컨텍스트 하나는 2 GiB, PCH 1.5 GiB, 기준 PDB 64 MiB 한도다. 기존 원본 blob 캐시는 별도 2 GiB 한도다. 프로세스 정리가 불확실하면 컨텍스트와 소유권을 보존하고 Worker를 unhealthy로 전환한다. 정상 종료 때 컨텍스트를 삭제하며, 재시작 시 남은 컨텍스트를 임의로 재사용하거나 삭제하지 않는다. Worker의 실행 슬롯·메모리·각 MSVC 시간 한도는 늘리지 않았다.

0.2.1에서 큰 PCH의 디스크 한도를 조정했다. 크기 정책에 맞지 않는 컨텍스트는 실패 이유를 보존하고 동일 생성의 반복 실행을 생략한다. 일반 compiler 오류나 취소를 영구 정책 실패로 저장하지 않는다. v6 RPC 전체 예산은 입력 준비 180초와 두 MSVC 시간 예산의 합으로 계산하며, compiler 제한 120초일 때 420초다. 각 compiler는 계속 개별 제한을 받는다. 최초 입력 전송만으로 기존 RPC 180초가 소모되는 사례를 재현하고 수정 경로에서 실제 큰 PCH 생성·재사용을 확인했다.

실제 MSVC·TLS 합성 검증에서 병렬 소비자, warm 입력/PCH hit, 입력·기준 PDB 재전송 없음, 작은 참조 헤더, 잘못된 참조 거부, 헤더·옵션·생성 세대 무효화, 오류·취소 후 복구를 확인했다. 서로 다른 사용자 타입을 가진 두 소비자를 원래 로컬 producer 및 원격 producer 각각과 링크·실행하고 DIA로 최종 PDB의 타입·멤버를 확인한다. 루프백 검증에서는 Worker 종료와 캐시 삭제 후 링크하므로 Worker에 남은 기준 PDB에 우연히 의존하는 성공을 배제한다. 물리 Worker 두 대에서도 동일 compiler identity로 각각 30개 검증을 통과했다. 현재 프로젝트 전체 처리량 검증은 별도 단계다. `/GL`+PCH 등 v5의 미지원 프로필은 원래 로컬 경로를 유지한다.

```powershell
.\scripts\Test-NativeReuse.ps1 -PublishDirectory <runtime-directory>
# 여유 있는 검증 볼륨 선택
.\scripts\Test-NativeReuse.ps1 -PublishDirectory <runtime-directory> -OutputRoot <verification-root>
# 동일 compiler의 페어링된 물리 Worker 검증
.\scripts\Test-NativeReuse.ps1 -PublishDirectory <runtime-directory> -Compiler <cl.exe> -RemoteClientConfig <client.json>
```

Client와 Worker 모두 v6 런타임이 필요하다. 기존 VS 연결은 자동 전환하지 않는다. 소스·PCH·compiler 옵션 보존과 실제 원격 완료·최종 링크를 확인한 다음 전환한다.

현재 프로젝트 검증에서는 로컬 40개·원격 19개 완료와 cache hit를 확인했지만 다른 빌드가 시작돼 소유한 검증만 중단했다. 원본 입력은 바뀌지 않았다. 최종 링크·전체 성공·무변경 빌드와 실제 처리량은 아직 미검증이다. [검증 보고서](benchmark.md)를 따른다.

재개한 실행에서는 약 1GiB PCH들이 512MiB 정책에 걸려 로컬로 전환되는 원인을 확인하고 소유한 검증을 중단했다. 실제 큰 PCH 두 사례는 수정 후 별도의 MSVC·TLS 콜드/웜 검증을 통과했다. 전체 최종 링크 성공으로 해석하지 않는다. Worker 업데이트 때는 Ctrl+C로 정상 종료하고 캐시 정리가 끝날 때까지 기다린 뒤 파일을 교체한다.

## v5: 원래 경로를 사용하는 Hybrid 실험

`New-MSBuildAdapter.ps1 -Mode Hybrid -NativeInputs -NativeInputRoots <프로젝트·생성 입력 루트들> -NativeToolchainRoots <MSVC·SDK 허용 루트들>`로 별도의 Adapter를 만든다. 기본 Hybrid의 v3 전처리본 전송 경로와 구분한다. 기존 솔루션 등록은 자동 변경하지 않는다. 모든 Worker에 v5 런타임과 동일 compiler identity가 필요하다. 현재 검증한 compiler 계열은 MSVC 19.44다.

이 경로는 요청 PC에서 `/P /Y-`를 실행하지 않는다. 원본 소스 바이트, 소스 인자의 표기, 작업 디렉터리, 순서 있는 include 옵션과 `INCLUDE`를 전달한다. 로컬 `/Yc` 성공 후 기록한 생성 명령을 사용해 Worker에서도 `/Yc`와 `/Yu`를 실행한다. PCH 자체를 머신 사이에 공유하거나 요청마다 재생성을 생략하는 기능은 아직 없다. 최종 링크는 요청 PC에서 실행한다.

Worker의 전용 launcher와 소유 compiler에만 비공개 NT device map을 적용한다. 원래 드라이브 경로는 작업별 입력 트리를 가리키고 결과물과 임시 파일은 Worker의 실제 볼륨에 기록한다. 전역 드라이브나 디렉터리 junction은 만들지 않는다. receiving OS의 compiler DLL 의존성과 NLS·정렬 데이터를 별도 캐시에 복사한다. 이 구조는 임의 프로그램의 파일 접근을 중계하는 범용 가상화나 보안 sandbox를 의미하지 않는다.

프로젝트·생성 입력 루트는 전체를 열거한다. 소스 폴더 밖의 상대 include도 이 범위 안에 있어야 한다. 같은 루트를 최초 MSBuild membership 검사에도 전달한다. MSVC·SDK 허용 루트는 접근 범위이며 전체를 복제하지 않는다. 그 안에서 실제 `/I`·`/external:I`·`/FI`·`INCLUDE`가 선택한 입력 트리만 열거한다. SDK의 다른 버전까지 함께 복제하는 준비 비용을 피한다. 전체 드라이브와 reparse 경로는 지원하지 않는다. C++ 소스·헤더와 선택된 표준 라이브러리 헤더를 미리 전송하고, 나머지는 메타데이터와 작업별 오류 표식을 둔다. compiler가 해당 표식의 파일을 include한 경우에만 원본을 추가로 전송한다. 사용하지 않은 deferred 파일의 변경을 소비한 입력의 변경으로 취급하지 않는다. 일반 컴파일 오류는 로컬 재시도로 숨기지 않는다. 추가 입력은 최대 32회 요청한다.

include 디렉터리의 끝 구분자는 입력 수집 시 정규화한다. `/I../`와 끝에 역슬래시가 있는 `INCLUDE`·작업 디렉터리를 파일 경로 규칙으로 거부하지 않는다. MSVC에 전달하는 원래 옵션은 보존한다.

v5는 입력 65,536개, JSON frame 32 MiB, 파일 하나 16 MiB, SHA-256·mtime 조합별 고유 입력 1 GiB로 제한한다. Worker는 큰 요청 헤더를 동시에 두 개까지만 읽는다. 입력 캐시는 hash·mtime·길이를 검증하고 쓰기 금지 handle로 고정하며, 작업별 hard link를 만든다. 캐시는 2 GiB·65,536개 한도이고 자동 축출은 구현하지 않았다. 캐시 용량이나 디스크 여유가 부족하면 원래 로컬 compiler로 돌아간다. 첫 전송과 작업별 입력 트리 구성 비용은 남아 있다.

캐시 갱신만 직렬화하고 작업별 입력 트리 구성은 잠금 밖에서 실행한다. 각 트리의 디렉터리는 생성 시 한 번 검증하며 65,536개로 제한한다. 준비 도중에도 취소를 확인한다. 동시 원본 PCH 요청 두 개의 객체·PDB를 각각 검증하고 로컬 producer와의 링크·실행을 확인했다. 응답 후 workspace 정리가 끝나기 전까지 슬롯이 사용 중일 수 있으므로 `busy`는 컴파일 오류와 구분한다.

`/GL`과 PCH를 함께 쓰는 소비자는 원래 로컬 compiler로 실행한다. 재생성한 원격 PCH와 로컬 PCH 객체를 LTCG 링크할 때 C1382가 발생하는 사례를 확인했다. deferred 입력이 있는 `/std:c++latest`도 로컬로 처리한다. 지원하지 않는 프로필·경로·Worker 버전은 로컬로 처리하며 compiler 옵션을 임의로 바꾸지 않는다.

실제 로컬 TLS·MSVC 회귀 66개를 통과했다. 합성 compiler 프로필 8개에서는 형제 헤더를 읽는 Debug PCH/PDB/MP, JMC, 혼합 인코딩, 오류·복구 및 무변경 빌드를 확인했다. 무변경 검증은 compiler·원격 요청 없이 완료됐다. 로컬 producer 객체와 원격 consumer 객체의 PDB를 함께 링크해 실행 값도 확인했다. 현재 프로젝트 전체의 v5 원격 완료 및 두 물리 Worker 성능 비교는 아직 검증 중이다. 기존 v3 벤치마크를 v5 결과로 해석하지 않는다.

## v4: 명시적인 상대 입력 트리

프로토콜 v4의 명시 입력 프로필을 추가했다. Client는 원본 소스와 헤더의 바이트 및 SHA-256을 보내고 Worker는 같은 작업 디렉터리에서 MSVC의 `/Yc`와 `/Yu`를 차례로 실행한다. 같은 Worker가 PCH와 compiler PDB를 소유하며 두 객체와 PDB를 반환한다. 전체 작업이 성공한 경우에만 결과물을 반환한다.

현재 지원 범위는 상대 경로로 구성한 명시 입력 트리, 공유 compiler 옵션, 순서 있는 `/I`, `/FI`, 제한된 `/D`·`/U`와 UTF-8이다. 입력은 최대 64개, 총 32 MiB이다. 절대 경로, SDK 파일 자동 제공, 파일 열기 요청의 중계와 캐시, 일반 MSBuild에서 PCH producer를 수집하는 연결은 후속 작업이다. 일반 Hybrid 진입점은 기존 v3 경로를 사용한다. v4는 별도 `compile` 명령과 검증 명령으로 실행한다.

합성 fixture에서 생성 헤더 값, include 검색 순서, 강제 include, 매크로를 확인했다. 헤더 변경과 오류 후 복구를 검사하고, 반환된 producer·consumer 객체를 PDB와 함께 링크해 실행 값을 확인했다. 로컬 TLS와 실제 MSVC 검증을 통과했다. 두 물리 Worker에서도 각 사례의 반환 객체와 PDB를 링크·실행했다. producer 오류와 consumer 오류 모두 결과물 반환을 차단했다. Worker 회귀 49개, 스케줄러 회귀 24개 및 컴파일러 프로필 검증을 통과했다. 현재 프로젝트의 원본 입력 경로 연결과 성능 비교가 남아 있다.

검증 실행 예시는 다음과 같다.

```powershell
.\scripts\Test-NativePch.ps1 -PublishDirectory <runtime-directory> -Compiler <cl.exe> -ClientConfigs <client.json>
```

Worker v4 런타임을 기존 Worker 폴더에 덮어쓰는 방식으로 업데이트할 수 있다. Worker가 종료된 상태에서 적용한 뒤 기존 실행 스크립트로 시작한다. Client와 Worker는 같은 바이너리 디렉터리 identity의 compiler를 사용해야 한다. 런타임 ZIP과 페어링 인증서는 Git에 포함하지 않는다.

## 0.2.3의 정리 및 로컬 메모리 수정

OS NLS 파일은 내용만 캐시에 복사한다. 이전 버전이 상속한 읽기 전용 속성은 경로·내용을 검증한 캐시 복사본에서만 해제한다. 원본 OS 파일과 캐시의 불변 핀을 보존하며, 읽기 전용 캐시를 미리 만든 실제 재사용 검증에서 축출·정상 종료까지 확인했다.

로컬 실행의 예약 추정치는 스케줄러 입장 판단에만 사용한다. 요청 PC의 컴파일·전처리·링크에는 그 값을 Job 힙 한도로 적용하지 않는다. 취소·소유권·입장 제어와 수신 Worker의 제한은 유지한다. 이 수정만으로 수신 사용자 활동 보호나 현재 프로젝트 전체 성공·가속을 주장하지 않는다.


## 0.3.0 선택형 v7 배치

`New-MSBuildAdapter.ps1 -NativeInputs -NativeBatchFiles 4`로 같은 CL Task에서 최대 4개 소스를 묶는다. 기본 1은 기존 v6다. 모든 Worker가 v7을 지원해야 하며 지원하지 않는 Worker의 배치는 원래 로컬 실행으로 전환되므로, 성공한 빌드만으로 원격 검증을 주장하지 않는다. 실제 v7 receipt와 원격 완료를 확인한다.

각 배치는 기존 incoming 슬롯 하나에서 순차 CL 한 번을 실행한다. 원래 `/Zi`, `/Yu`, 입력·include 경로·producer Fo/Fp 의미는 유지하며 객체별 출력과 배치별 PDB를 반환한다. 캐시 producer seed는 읽기 전용이고 배치가 writable clone 하나를 소유한다. 공통 PDB는 객체 게시가 끝날 때까지 pin하며 해시·길이가 바뀐 동일 경로는 거부한다. 배치 사이의 영구 mutable PDB 공유나 delta 동기화는 지원하지 않는다. 긴 배치가 기존 요청 timeout을 넘으면 이득이 없어질 수 있으므로 최적 크기는 실제 재측정 대상이다.

요청 PC의 원래 `/MP` 처리량 보존은 새 host policy의 `preserveLocalParallelism: true`, `nativeLocalJobs`로 선택한다. `localBatchSlots`는 원래 `/MP` 이하의 내부 fan-out 상한이고 `nativeLocalJobs`는 서로 다른 PDB를 소유한 native Job의 동시 수다. 후자는 MSBuild의 프로젝트 병렬 수와 맞춰 제한한다. 자체 native Job에는 보수적 CPU/physical-memory admission이나 incoming hard limit를 적용하지 않는다. 준비 작업은 두 개 이하이며 spare commit과 headroom을 검사한다. 기존 기본 정책을 자동 변경하지 않는다.

로컬 TLS 합성에서 4개 소비자·하나의 PDB를 원래 로컬 producer 및 원격 producer와 각각 링크/실행하고 DIA로 타입·멤버를 확인했다. Worker cache 제거 뒤에도 링크가 성공했다. 실제 MSBuild Debug/Release, JMC, 혼합 인코딩, 무변경 및 MP 취소도 확인했다. 현재 프로젝트·물리 Worker의 새 경로 검증과 속도 비교는 별도다.
