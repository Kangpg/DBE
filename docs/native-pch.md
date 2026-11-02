# 원본 입력과 원격 PCH 검증

프로토콜 v4의 명시 입력 프로필을 추가했다. Client는 원본 소스와 헤더의 바이트 및 SHA-256을 보내고 Worker는 같은 작업 디렉터리에서 MSVC의 `/Yc`와 `/Yu`를 차례로 실행한다. 같은 Worker가 PCH와 compiler PDB를 소유하며 두 객체와 PDB를 반환한다. 전체 작업이 성공한 경우에만 결과물을 반환한다.

현재 지원 범위는 상대 경로로 구성한 명시 입력 트리, 공유 compiler 옵션, 순서 있는 `/I`, `/FI`, 제한된 `/D`·`/U`와 UTF-8이다. 입력은 최대 64개, 총 32 MiB이다. 절대 경로, SDK 파일 자동 제공, 파일 열기 요청의 중계와 캐시, 일반 MSBuild에서 PCH producer를 수집하는 연결은 후속 작업이다. 일반 Hybrid 진입점은 기존 v3 경로를 사용한다. v4는 별도 `compile` 명령과 검증 명령으로 실행한다.

합성 fixture에서 생성 헤더 값, include 검색 순서, 강제 include, 매크로를 확인했다. 헤더 변경과 오류 후 복구를 검사하고, 반환된 producer·consumer 객체를 PDB와 함께 링크해 실행 값을 확인했다. 로컬 TLS와 실제 MSVC 검증을 통과했다. 두 물리 Worker에서도 각 사례의 반환 객체와 PDB를 링크·실행했다. producer 오류와 consumer 오류 모두 결과물 반환을 차단했다. Worker 회귀 49개, 스케줄러 회귀 24개 및 컴파일러 프로필 검증을 통과했다. 현재 프로젝트의 원본 입력 경로 연결과 성능 비교가 남아 있다.

검증 실행 예시는 다음과 같다.

```powershell
.\scripts\Test-NativePch.ps1 -PublishDirectory <runtime-directory> -Compiler <cl.exe> -ClientConfigs <client.json>
```

Worker v4 런타임을 기존 Worker 폴더에 덮어쓰는 방식으로 업데이트할 수 있다. Worker가 종료된 상태에서 적용한 뒤 기존 실행 스크립트로 시작한다. Client와 Worker는 같은 바이너리 디렉터리 identity의 compiler를 사용해야 한다. 런타임 ZIP과 페어링 인증서는 Git에 포함하지 않는다.
