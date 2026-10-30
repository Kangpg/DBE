# 구조와 구현 한계

VSIX → 원래 MSBuild CL Task/추적 → Adapter → local MSVC 또는 TLS Worker → artifact 검증/publication → local link 순서다. 별도 compiler wrapper는 caller 인자·환경·진단을 보존한다.

- 프로세스: Job Object를 creation 시점에 할당하고 소유 handle을 유지한다. PID/경로만으로 종료 대상을 결정하지 않는다.
- 파일: 준비된 TU와 명시 입력을 TLS로 보내고 object/PDB를 검증 후 원자적으로 반영한다. 공유 CAS가 아니다.
- PCH: 생성은 local, 원격 사용자는 `/P /Y-`로 펼친다. 원격 PCH reuse는 없다. 같은 local PDB는 하나의 MP Job 안에서 사용한다.
- 추적: native MSBuild 추적을 보존하고 include membership 변화를 추가로 확인한다. runtime 파일은 실제 parent directory 기준으로 제외한다.
- 예산: Worker별 준비 티켓과 실제 요청 제한을 분리한다. fallback은 local PDB 대기 전에 원격 lease를 놓는다. 동일 scheduler에서 required-local waiter가 새 MP group에 밀리지 않게 한다.
- 보호: 수신 PC 사용자가 우선이다. 현재 fixed slots는 개인용/전용 정책, 활동 감지, 양방향 capacity 광고, 머신 전체 budget, 여러 요청자 공정성을 완성하지 못했다.
- UI: VSIX 별도 명령과 UTF-8 출력이 있다. 웹 대시보드는 없다. 요청 이벤트와 실제 Worker 실행 상태를 구분해야 한다.

성능은 동일 compiler 사용만으로 보장되지 않는다. batch 분할, PDB 입장, 전처리·전송·프로세스 시작 비용을 계측해야 한다. [벤치마크](benchmark.md)와 [이슈](issues.md)를 따른다.
