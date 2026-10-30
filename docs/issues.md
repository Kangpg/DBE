# 이슈 추적

오류 수정과 합성 검증을 마쳤다. 외부 빌드가 진행 중이므로 사용자 요청으로 성능 측정은 보류한다. 기록은 DBE의 일반적인 현상과 완료 조건으로 제한한다.

## 열린 작업

- [DBE-001](https://github.com/Kangpg/DBE/issues/1)
- [DBE-002](https://github.com/Kangpg/DBE/issues/2)
- [DBE-003](https://github.com/Kangpg/DBE/issues/3)
- [DBE-004](https://github.com/Kangpg/DBE/issues/4)
- [DBE-005](https://github.com/Kangpg/DBE/issues/5)
- [DBE-006](https://github.com/Kangpg/DBE/issues/6)
- [DBE-007](https://github.com/Kangpg/DBE/issues/7)
- [DBE-008](https://github.com/Kangpg/DBE/issues/8)

### DBE-001 · P0 · MP staging 오류 재현

C1083/D8040을 실제 MSVC의 긴 object 출력 경로로 재현했다. hash/attempt metadata와 compiler 출력 폴더를 분리하고, 짧은 staging에서 목적지 볼륨의 원자적 publication으로 전환했다. 최신 합성 PCH/PDB·긴 파일명 검사와 실제 MP 자식 취소·정리가 통과했다. 현재 프로젝트 전체 Hybrid 성공을 확인한 뒤 닫는다.

### DBE-002 · P1 · native 로컬 처리량 동등성

동일 PCH/MP/옵션의 native 대 DBE Local 전용 비교로 batch 분할과 입장 제어 비용을 분리한다. 완료 조건: 일반 앱 유지, 반복 성공 시간·CPU·peak commit·no-op 비교.

Local 전용을 native wrapper로 전환해 원래 PCH/MP를 보존하고 Worker용 고정 한도를 제거했다. MSBuild 실패 object 정리와 증분 빌드도 검증했다. 입력 변경 및 외부 빌드 중첩이 발견된 측정은 무효 처리했다. 고정 입력과 빌드 중첩이 없는 구간에서 반복 비교가 남아 있다.

순정 솔루션 진입점의 링크 실패와 표준 프로젝트 Build의 성공을 구분했다. 다음 측정은 동일한 프로젝트 Build 진입점으로 수행하며, 실패 시간이나 기존 출력 재사용 진단을 fresh-build 결과로 합산하지 않는다. 현재 사용자 요청으로 성능 측정은 보류 상태다.

### DBE-003 · P1 · 원격 입력 비용과 단계별 계측

준비/대기/전송/Worker compile/반환을 계측하고 PCH reuse와 입력 cache를 설계한다. 옵션·새 include·toolchain identity·오류 정확성을 보존하며 실제 전송량도 측정한다.

### DBE-004 · P0 · 수신 PC 사용자 우선 보호

개인용/전용 정책, local/RDP 활동과 자원 압력을 수신 PC가 판단한다. 자신의 빌드와 외부 요청 budget을 분리한다. 완료 조건: 사용자 활동 시 수신 감소/거절, 요청자 우회 불가, 반응성 검증.

### DBE-005 · P1 · 양방향 유휴 용량 공유

유효기간 있는 capacity 광고, 수신 재검사, 감소 우선/증가 hysteresis, 머신 budget과 다중 요청자 공정 큐가 필요하다. 동시 요청/사용자 복귀/연결 단절에서도 과할당이나 독점이 없어야 한다.

### DBE-006 · P1 · 전체 및 증분 성능 검증

현재 프로젝트 전체 성공 후 전체/무변경/소스 변경/헤더 변경을 반복 비교한다. cache·앱·build event 조건, 실패와 미측정 비용을 기록한다. 보고서에는 현재 프로젝트라는 표현과 승인된 결과만 남긴다.

### DBE-007 · P2 · Worker 실행 대시보드

compiler 버전/identity와 실제 queue/receive/compile/return 상태를 표시한다. Worker가 소유한 상태를 사용하고 단절/오래된 상태를 명확히 구분한다.

### DBE-008 · P2 · 새 머신 설치 재현

도구 선택과 preflight를 정리하고 새 환경에서 문서만으로 build→pair→Worker→VSIX→합성 fixture를 검증한다. 사용자 목표는 프로젝트 파일·컴파일 옵션·PCH를 변경하지 않고 설치 후 빌드 명령을 사용하는 것이다. compiler/configuration 자동 탐색과 설치 과정의 Worker 연결을 통해 반복적인 수동 경로 설정을 없앤다. 현재 문서/구문 검사만으로 새 머신 설치 성공이나 이 사용 흐름의 완성을 주장하지 않는다.

## 해결 이력

- wrapper의 caller handle 상속으로 MSVC 진단 보존.
- Schannel 인증서 로딩과 lifetime 수정.
- 명시 compiler 옵션 프로필, PCH/PDB/JMC 및 인코딩 처리.
- VSIX menu resource/등록 수정, UTF-8 한글 출력, 프로젝트 선택 메뉴.
- local fallback 전 원격 lease 해제, Worker별 준비/실행 분리.
- runtime parent directory 제외와 include membership 검사로 합성 no-op 검증.
- 하나의 native MP Job/PDB owner, required-local waiter 우선 처리.

이 이력은 해당 경계의 검증이며 최신 엔진의 전체 지원이나 가속 달성을 뜻하지 않는다.
