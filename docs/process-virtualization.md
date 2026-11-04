# Process virtualization: 문서와 현재 구현

Incredibuild의 [Process Virtualization Flow](https://docs.incredibuild.com/windows/process-virtualization-flow)는 15단계의 개념 설명이다. 문서 자체도 내부 세부 사항을 생략한 설명이라고 명시한다. 아래는 공개 설명과 DBE 소스를 대조한 범위이며, 비공개 알고리즘을 재현했다는 의미가 아니다.

| 문서 흐름 | 공개 설명 | DBE 0.3.5 |
|---|---|---|
| 1~2 | 빌드 프로세스와 OS 사이에서 컴파일 프로세스 생성 중재 | MSBuild CL wrapper로 진입한다. 일반 CreateProcess 가로채기와 가상 프로세스 handle은 없다. |
| 3~6 | Coordinator가 가용 Helper를 배정하고 요청자와 Helper가 직접 통신 | 명시 등록한 Worker를 TLS로 직접 연결한다. 슬롯·준비 pipeline을 제한하지만 동적 Coordinator와 다중 사용자 공정 배정은 미구현이다. |
| 7~9 | compiler를 보내 Helper에서 실행하고 가상화 계층 적용 | 선택 compiler identity를 검증하고 미리 배치한 compiler를 사용한다. 전용 launcher의 private device map으로 원래 드라이브 경로를 투영한다. 일반 프로세스 주입은 없다. |
| 10~14 | compiler의 파일 요청에 따라 입력을 가져와 cache로 redirect; registry·DLL·프로세스 I/O도 가상화 | 허용된 입력을 사전 inventory·hash·전송하고 immutable 입력/PCH를 재사용한다. 일부 누락 include는 DBE 진단으로 추가 요청한다. 모든 파일 open·directory query·registry·DLL load를 가로채는 계층은 없다. |
| 15 | 여러 Helper를 함께 사용 | 명시 등록한 여러 Worker에 분배한다. 0.3.4 두 물리 Worker의 v7 배치와 수정 요청자 0.3.5의 현재 프로젝트 전체 링크·분배를 확인했다. |

DBE의 원래 경로 투영은 namespace를 제공한다. compiler가 처음 여는 파일을 즉시 요청하거나 디렉터리 열거·파일 부재·registry를 완전히 재현하는 기능을 뜻하지 않는다. 이 차이가 사전 입력 조사와 준비 비용을 남긴다.

이번 튜닝은 측정한 비용 두 가지를 다룬다. 자신의 빌드에는 native 우선순위와 원래 `/MP` fan-out을 허용하고, 원격에서는 최대 4소스를 한 writable PDB로 처리해 소비자별 PDB 반환 횟수를 줄인다. 수신 Worker의 자원 한도는 유지한다. Agent 전체에 걸친 mutable PDB 재사용·delta 반환은 구현하지 않았으며, 이 문서만으로 Incredibuild의 PDB 동기화 알고리즘도 알 수 없다.

실제 현재 프로젝트의 수정 Hybrid는 412.88초로 전체 링크에 성공했지만 이번 순정 367.69초보다 느렸다. warm 입력 캐시·일반 앱을 유지한 1회 관측이며 조건과 단계 시간은 [측정 보고서](benchmark.md)에 기록한다. 다음 대상은 남은 입력 준비·원격 용량 대기와 로컬 대형 그룹 배정이다. 이를 근거로 요청 기반 입력 계층의 범위와 정확성 계약을 설계한다. 합성 검증이나 일부 실제 성공만으로 투명한 모든 프로젝트 지원이나 가속을 주장하지 않는다.
