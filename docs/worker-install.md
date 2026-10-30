# Worker 설치

요청 PC에서 `Package-Worker.ps1`로 해당 머신의 주소와 새 인증 pair를 생성한다. Worker-only ZIP만 전달하고 client 개인 키는 요청 PC에 남긴다. ZIP을 풀고 `start-worker.cmd`를 실행한다.

TCP port 43127을 사용한다. 필요한 경우 함께 제공된 `Allow-WorkerFirewall.ps1 -InitiatorAddress <요청PC주소>`를 관리자 PowerShell에서 실행한다. 기본 동시 2개/작업당 2GiB/120초는 사용자 활동 보호 정책을 대신하지 않는다. 영향을 받으면 Ctrl+C로 종료한다.

이 파일은 Worker ZIP에도 포함된다. 전체 소스 저장소에서는 [설치](installation.md), [연결](connection.md), [사용법](usage.md)을 참고한다. 인증서·설정·compiler·산출물을 Git에 올리지 않는다.
