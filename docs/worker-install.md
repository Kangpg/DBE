# Worker 설치

요청 PC에서 `Package-Worker.ps1`로 해당 머신의 주소와 새 인증 pair를 생성한다. Worker-only ZIP만 전달하고 client 개인 키는 요청 PC에 남긴다. ZIP을 풀고 `start-worker.cmd`를 실행한다.

TCP port 43127을 사용한다. 필요한 경우 함께 제공된 `Allow-WorkerFirewall.ps1 -InitiatorAddress <요청PC주소>`를 관리자 PowerShell에서 실행한다. 기본 동시 2개/작업당 2GiB/120초는 사용자 활동 보호 정책을 대신하지 않는다. 영향을 받으면 Ctrl+C로 종료한다.

업데이트는 Ctrl+C로 정상 종료하고 캐시 정리가 끝난 뒤 업그레이드 ZIP을 기존 Worker 폴더에 덮어 푼다. 업그레이드 ZIP에는 runtime·방화벽 도구·README(선택 시 bundled toolchain)만 있고 `identity.pfx`·`worker.json`·`start-worker.cmd`가 없다. 따라서 server 인증서, 허용 requester pin, compiler 선택, state, 수신 한도는 그대로 유지된다. 빈 폴더에 풀면 실행할 수 없다. `Retained native contexts need inspection before restart` 또는 남은 attempt 오류로 재시작이 막히면 Worker가 종료된 상태에서 `Repair-WorkerState.cmd`를 실행한다. 도구는 `worker.json`을 백업하고 새 상태 폴더로 전환한 뒤 기존 시작 스크립트를 호출한다. 기존 컨텍스트·시도 폴더와 인증서·주소·toolchain은 보존한다. 실행 중이거나 잠겨 있는 Worker는 변경하지 않는다. 남은 폴더를 직접 삭제하거나 이름을 바꿀 필요가 없다.

이 파일은 Worker ZIP에도 포함된다. 전체 소스 저장소에서는 [설치](installation.md), [연결](connection.md), [사용법](usage.md)을 참고한다. 인증서·설정·compiler·산출물을 Git에 올리지 않는다.


요청 PC의 client 키를 잃었을 때 받는 복구 ZIP에는 공개 인증서와 등록 도구만 있다. Worker를 Ctrl+C로 정상 종료하고 ZIP을 기존 Worker 폴더에 풀어 `Register-Requester.cmd`를 실행한 뒤 `start-worker.cmd`로 시작한다. 설정 백업 후 허용 requester pin만 바꾸며 server 인증서·toolchain·state·수신 한도는 유지한다. 같은 키로 재실행하면 수정하지 않는다. 출력한 공개 `connection-<id>.json`은 요청 PC 등록에 사용한다. 이전 requester 키는 더 이상 허용되지 않는다. `Repair-WorkerState.cmd`는 상태 복구용이므로 이 등록을 대신하지 않는다.
