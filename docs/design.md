# 구조와 구현 한계

VSIX → 원래 MSBuild CL Task/추적 → Adapter → local MSVC 또는 TLS Worker → artifact 검증/publication → local link 순서다. 별도 compiler wrapper는 caller 인자·환경·진단을 보존한다.

- 프로세스: Job Object를 creation 시점에 할당하고 소유 handle을 유지한다. PID/경로만으로 종료 대상을 결정하지 않는다.
- PDB 서비스: 0.3.5부터 각 소유 실행에 고유한 `_MSPDBSRV_ENDPOINT_`를 부여한다. `/MP` 자식은 같은 연결점을 상속한다. 별도 Job의 CL/link가 MSPDBSRV를 공유하면 먼저 끝난 Job의 정리가 다른 컴파일의 PDB 연결을 끊을 수 있다. native wrapper와 managed 실행의 공통 native 환경 생산 경계에서 분리하며 기존 Unicode 환경과 숨은 드라이브 항목을 보존한다. `/Zi`·PCH·`/Fd`를 변경하거나 자식 정리를 해제하지 않는다. 같은 서비스 격리 수단은 [Microsoft BuildXL 구현](https://github.com/microsoft/BuildXL/blob/main/Public/Sdk/Experimental/Msvc/Native/Tools/Link/Link.dsc)에서도 확인할 수 있다.
- PDB 식별: 서비스가 분리되었으므로 같은 PDB의 동시 쓰기는 로컬 PDB 독점 lease로만 막는다. 다른 연결점의 두 compiler가 같은 PDB를 쓰면 C1041로 실패한다(실제 MSVC 3/3). lease 키와 `/Fo`·`/Fp` 출력은 `MsvcPath`가 MSVC의 실제 해석(콜론 형식, 마지막 지정, 끝 구분자, 확장자 추가, 드라이브별 현재 폴더)으로 정한다. 호출자가 지정한 `_MSPDBSRV_ENDPOINT_`는 소유 실행의 값으로 의도적으로 대체한다. 설치된 VS 2022 MSBuild/VC targets는 이 변수를 설정하지 않는다. 실행별 새 서비스는 Job마다 약 277ms(작은 TU 중앙값)를 더한다.
- Local 전용: native wrapper로 원래 MSVC PCH/MP를 보존한다. 자신의 빌드에는 Worker용 고정 제한을 적용하지 않는다. 수신 요청의 보호 정책과 구분한다.
- 파일: 기본 경로는 준비된 TU와 명시 입력을 TLS로 보내고 object/PDB를 검증 후 원자적으로 반영한다. v6 선택 경로는 허용 범위의 원본 입력, Worker별 hash·mtime blob 캐시와 재사용 입력 트리를 사용한다. 소비자 object를 저장하는 빌드 결과 캐시는 아니다.
- Local staging: object별 hash/attempt 중첩과 compiler 출력 폴더를 분리한다. 짧은 Job 전용 폴더를 사용하고, 확인된 프로세스 정리 후에만 지운다. 최종 목적지 볼륨에서 rename하여 publication한다.
- PCH: 기본 Hybrid는 생성이 local이고 원격 사용자는 `/P /Y-`로 펼친다. 선택적인 [v6 원본 입력 경로](native-pch.md)는 원래 `/Yc`·`/Yu`를 실행하고 입력·생성 옵션·로컬 생성 세대가 같은 Worker PCH를 재사용한다. 기준 PDB를 고정하고 소비자별 사본을 사용한다. 같은 local PDB는 하나의 MP Job 안에서 사용한다.
- Compile-ahead: Hybrid 명령줄 빌드에서 참조를 빌드하는 동안 자기 컴파일을 같은 병렬 MSBuild 호출로 시작한다. 인스턴스는 ProjectReference 없이 PrepareForBuild와 ClCompile만 실행한다. 실패 진단은 버리고 객체를 남기지 않아, 일반 Build가 의존 순서대로 다시 컴파일하고 진짜 오류를 보고한다. 일반 VS 솔루션 빌드는 VS가 순서를 정하므로 적용되지 않는다.
- 자동 입력 범위: 선택형이며 프로젝트가 선언한 경로를 신뢰한다. 선언되지 않은 경로의 파일을 `__has_include`로 찾는 소스는 원격과 로컬 결과가 다를 수 있다. 일반 include 누락은 `C1083`로 로컬에서 다시 판정한다.
- 실패 처리: 원격 실패가 이어지면 해당 CL Task는 원래 fan-out의 로컬 Job으로 돌아간다. DBE 상태나 스케줄러를 쓸 수 없으면 원래 도구를 원래 명령으로 실행하고 lease는 보존한다. 입력 해시 인덱스는 MSBuild 추적과 같이 경로·크기·수정 시각을 신뢰하므로, 시각과 크기를 유지한 채 내용만 바뀐 파일은 빌드 사이에 감지하지 못한다.
- 추적: native MSBuild 추적을 보존하고 include membership 변화를 추가로 확인한다. include가 될 수 있는 확장자의 파일만 세고, 솔루션 폴더는 감시 범위에 넣지 않는다. tlog로 처음 관찰한 폴더는 기록만 하고 이후 변화를 감지한다. runtime 파일은 실제 parent directory 기준으로 제외한다.
- 예산: Worker별 준비 티켓과 실제 요청 제한을 분리한다. fallback은 local PDB 대기 전에 원격 lease를 놓는다. 동일 scheduler에서 required-local waiter가 새 MP group에 밀리지 않게 한다.
- 보호: 수신 PC 사용자가 우선이다. 현재 fixed slots는 개인용/전용 정책, 활동 감지, 양방향 capacity 광고, 머신 전체 budget, 여러 요청자 공정성을 완성하지 못했다.
- UI: VSIX 별도 명령과 UTF-8 출력이 있다. 웹 대시보드는 없다. 요청 이벤트와 실제 Worker 실행 상태를 구분해야 한다.

성능은 동일 compiler 사용만으로 보장되지 않는다. batch 분할, PDB 입장, 전처리·전송·프로세스 시작 비용을 계측해야 한다. [벤치마크](benchmark.md)와 [이슈](issues.md)를 따른다.
