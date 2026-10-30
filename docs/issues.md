# 이슈 추적

개발은 일시 중단 상태다. 기록은 DBE의 일반적인 현상과 완료 조건으로 제한한다.

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

C1083/D8040 실패를 합성 입력으로 재현한다. 경로 길이, Job 메모리/프로세스 한도를 구분하고 원자적 publication과 취소 정리를 유지한다. 완료 조건: 긴/한글 경로, 오류/취소 회귀와 현재 프로젝트 전체 성공. 경로 원인은 아직 확정하지 않았다.

### DBE-002 · P1 · native 로컬 처리량 동등성

동일 PCH/MP/옵션의 native 대 DBE Local 전용 비교로 batch 분할과 입장 제어 비용을 분리한다. 완료 조건: 일반 앱 유지, 반복 성공 시간·CPU·peak commit·no-op 비교.

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

도구 선택과 preflight를 정리하고 새 환경에서 문서만으로 build→pair→Worker→VSIX→합성 fixture를 검증한다. 현재 문서/구문 검사만으로 새 머신 설치 성공을 주장하지 않는다.

## 해결 이력

- wrapper의 caller handle 상속으로 MSVC 진단 보존.
- Schannel 인증서 로딩과 lifetime 수정.
- 명시 compiler 옵션 프로필, PCH/PDB/JMC 및 인코딩 처리.
- VSIX menu resource/등록 수정, UTF-8 한글 출력, 프로젝트 선택 메뉴.
- local fallback 전 원격 lease 해제, Worker별 준비/실행 분리.
- runtime parent directory 제외와 include membership 검사로 합성 no-op 검증.
- 하나의 native MP Job/PDB owner, required-local waiter 우선 처리.

이 이력은 해당 경계의 검증이며 최신 엔진의 전체 지원이나 가속 달성을 뜻하지 않는다.
