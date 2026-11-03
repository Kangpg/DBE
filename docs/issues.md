# 이슈 추적

현재 프로젝트의 반복 전체 빌드 비교를 마쳤다. [벤치마크 보고서](benchmark.md)에 조건과 회차별 시간을 기록했다.

## 열린 작업

- [DBE-002](https://github.com/Kangpg/DBE/issues/2)
- [DBE-003](https://github.com/Kangpg/DBE/issues/3)
- [DBE-004](https://github.com/Kangpg/DBE/issues/4)
- [DBE-005](https://github.com/Kangpg/DBE/issues/5)
- [DBE-006](https://github.com/Kangpg/DBE/issues/6)
- [DBE-007](https://github.com/Kangpg/DBE/issues/7)
- [DBE-008](https://github.com/Kangpg/DBE/issues/8)

### DBE-002 · P1 · native 로컬 처리량 동등성

동일 PCH/MP/옵션의 native 대 DBE Local 전용 비교로 batch 분할과 입장 제어 비용을 분리한다. 완료 조건: 일반 앱 유지, 반복 성공 시간·CPU·peak commit·no-op 비교.

Local 전용은 원래 PCH/MP를 보존한다. 현재 프로젝트 전체 빌드 평균 시간은 순정 대비 +0.9%였다. 같은 프로젝트 Build 진입점에서 무변경 빌드 시간 비교가 남아 있다.

### DBE-003 · P1 · 원격 입력 비용과 단계별 계측

단계별 계측과 원격 슬롯·로컬 입장 제어 분리를 추가했다. 명시 입력 v4에서 Worker가 원본 소스로 PCH를 생성하고 사용하는 합성 검증을 두 물리 Worker에서 통과했다. 실제 프로젝트의 절대 경로·SDK 입력 제공과 일반 빌드 연결이 남아 있다. [원격 PCH 검증 범위](native-pch.md)를 따른다.

v5 선택 경로에 원래 경로·소스·PCH 명령 전달, 명시적인 입력 루트 범위, Worker 입력 캐시와 필요한 추가 include 전송을 구현했다. 로컬 TLS·MSVC 회귀 66개와 합성 프로필 8개를 통과했다. 실제 입력 열거에서 파일 수 제한과 오래된 산출물 포함 문제를 발견해 고유 데이터 예산과 목록 메모리 제한을 분리했다. 형제 헤더 누락과 미사용 메타데이터 변경 오판을 수정하고 최초 MSBuild membership 범위를 맞춰 합성 무변경 재빌드도 방지했다. SDK 전체 복제 중 수신 준비 제한에 걸린 사례를 확인해 프로젝트 inventory와 toolchain 허용 범위를 구분했다. 현재 프로젝트 일부의 원격 객체 생성을 확인했으며 전체 링크, 두 물리 Worker 및 속도 개선 검증은 진행 중이다. `/GL`+PCH는 LTCG 호환 문제로 원래 로컬 경로를 유지한다.

v6에서 입력 트리와 PCH를 세대별로 재사용하고 소비자별 PDB를 기준 PDB에서 복사한다. warm 요청은 컨텍스트 참조를 보내며 입력·기준 PDB 재전송을 생략한다. 실제 MSVC·TLS 합성 검증으로 병렬 소비자, 무효화, 오류·취소 복구, 원래 로컬 producer와의 링크·실행 및 DIA 타입 검증을 추가했다. 물리 Worker 두 대에서도 각각 30개 검증을 통과했다. 현재 프로젝트 전체 처리량은 별도 검증이 필요하다. 준비/대기/전송/Worker compile/반환과 cache hit·초기화 시간을 구분한다.

현재 프로젝트에서 원격 완료와 cache hit를 확인했으나 다른 빌드 시작으로 측정을 중단했다. 소스 전후 해시는 일치했다. 최종 링크가 끝나지 않은 시간을 성능 개선 근거로 사용하지 않는다. 기존 VS 기본 연결도 이 검증 결과만으로 전환하지 않았다.

실제 v5 검증에서 `/I../`를 파일 경로로 검증해 다수 TU가 로컬로 돌아가는 문제를 수정했다. 캐시 갱신 잠금을 작업별 트리 구성에서 해제하고 디렉터리 검증을 공유해 두 슬롯의 준비 직렬화도 줄였다. 응답 뒤 workspace 정리 중의 `busy`가 여전히 로컬 전환을 일으킨다. 작업별 입력 트리 재구성과 PCH 재생성, 슬롯 정리 시간 및 보수적인 병렬 예산을 후속 성능 항목으로 분리한다.

### DBE-004 · P0 · 수신 PC 사용자 우선 보호

개인용/전용 정책, local/RDP 활동과 자원 압력을 수신 PC가 판단한다. 자신의 빌드와 외부 요청 budget을 분리한다. 완료 조건: 사용자 활동 시 수신 감소/거절, 요청자 우회 불가, 반응성 검증.

### DBE-005 · P1 · 양방향 유휴 용량 공유

유효기간 있는 capacity 광고, 수신 재검사, 감소 우선/증가 hysteresis, 머신 budget과 다중 요청자 공정 큐가 필요하다. 동시 요청/사용자 복귀/연결 단절에서도 과할당이나 독점이 없어야 한다.

### DBE-006 · P1 · 전체 및 증분 성능 검증

전체 빌드는 세 모드에서 각각 두 번 성공했다. 남은 비교 항목은 무변경, 소스 변경, 헤더 변경이다.

위 성공 시간은 기존 v3 경로다. v6 입력/PCH 재사용의 합성 성공을 현재 프로젝트 가속으로 해석하지 않는다. 런타임 별도 설치형 Adapter가 .NET 파일을 새 입력으로 발견해 무변경 재빌드하는 문제도 발견했다. Adapter가 실행한 Worker에서 실제 런타임 경로를 받아 최초 입력 검사에 반영하도록 수정했고, 실제 MSBuild의 무변경·오류 복구·링크 검증을 통과했다.

### DBE-007 · P2 · Worker 실행 대시보드

compiler 버전/identity와 실제 queue/receive/compile/return 상태를 표시한다. Worker가 소유한 상태를 사용하고 단절/오래된 상태를 명확히 구분한다.

### DBE-008 · P2 · 새 머신 설치 재현

도구 선택과 preflight를 정리하고 새 환경에서 문서만으로 build→pair→Worker→VSIX→합성 fixture를 검증한다. 사용자 목표는 프로젝트 파일·컴파일 옵션·PCH를 변경하지 않고 설치 후 빌드 명령을 사용하는 것이다. compiler/configuration 자동 탐색과 설치 과정의 Worker 연결을 통해 반복적인 수동 경로 설정을 없앤다. 현재 문서/구문 검사만으로 새 머신 설치 성공이나 이 사용 흐름의 완성을 주장하지 않는다.

일반 VS Build는 사용자별 솔루션 등록으로 연결할 수 있다. VS 2026 기본 Build에서 합성 PCH/MP 프로젝트의 Local 빌드, 무변경, 컴파일 오류, 기본 취소와 복구를 확인했다. 같은 명령에서 동일 compiler의 루프백 TLS Worker와 로컬 분배도 확인했다. 새 머신 자동 설치와 Worker 페어링은 후속 작업이다.

## 해결 이력

- [DBE-001](https://github.com/Kangpg/DBE/issues/1) 긴 MP 출력 경로의 C1083/D8040을 수정했다. 합성 오류·취소 회귀 후 현재 프로젝트 전체 Hybrid 빌드와 최종 링크를 두 회차에서 확인했다.

- wrapper의 caller handle 상속으로 MSVC 진단 보존.
- Schannel 인증서 로딩과 lifetime 수정.
- 명시 compiler 옵션 프로필, PCH/PDB/JMC 및 인코딩 처리.
- VSIX menu resource/등록 수정, UTF-8 한글 출력, 프로젝트 선택 메뉴.
- local fallback 전 원격 lease 해제, Worker별 준비/실행 분리.
- runtime parent directory 제외와 include membership 검사로 합성 no-op 검증.
- 하나의 native MP Job/PDB owner, required-local waiter 우선 처리.

이 이력은 해당 경계의 검증이며 최신 엔진의 전체 지원이나 가속 달성을 뜻하지 않는다.
