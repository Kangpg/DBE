# DBE — Distributed Build Engine

Windows MSVC C++ 빌드를 요청 PC와 여러 Worker에 배정하는 실험 프로젝트. 네이티브 wrapper, MSBuild Adapter, TLS Worker와 Visual Studio 확장 소스를 포함한다.

현재 개발 버전은 **0.2.1**이다. [릴리스 노트](CHANGELOG.md)에 변경·검증·남은 항목을 기록한다. 제품 버전은 `VERSION`이 기준이며 통신 프로토콜 번호와 구분한다.

**현재 프로젝트 전체 빌드가 세 모드에서 각각 두 번 성공했다.** 평균은 순정 로컬 297.57초, DBE Local 300.20초, 두 Worker Hybrid 673.36초였다. [벤치마크 조건과 결과](docs/benchmark.md)

- [설치](docs/installation.md): 소스 빌드, Worker 패키지, VSIX.
- [연결](docs/connection.md): 인증 pair, 여러 Worker, 솔루션 설정.
- [사용법](docs/usage.md): 프로젝트 파일 수정 없이 일반 VS Build에 연결, 프로젝트/솔루션 빌드, 취소, 로그.
- [구조와 한계](docs/design.md), [이슈 추적](docs/issues.md), [개발 지침](AGENTS.md).
- [원본 소스·경로·원격 PCH 실험](docs/native-pch.md): v6 입력/PCH 재사용과 v5 경로 보존의 검증 범위. 위 전체 빌드 시간은 기존 v3 경로의 결과다.

Windows x64, PowerShell 7, VS 2022 C++ v143/Windows SDK, `worker/global.json`의 .NET SDK를 사용한다. Incredibuild나 별도 역공학 도구는 필요하지 않다.

```powershell
$build = .\scripts\Build-Worker.ps1 -SelfContained
.\scripts\Build-Wrapper.ps1 -VisualStudio 2022
```

`native/`는 프로세스 감독과 wrapper, `worker/`는 TLS/옵션/스케줄러, `msbuild/`는 빌드 추적, `vsix/`는 UI 명령, `scripts/`와 `fixture*/`는 패키징과 합성 검증이다. 실행 산출물은 `out/`에만 생성한다.

Git에는 DBE 자체 소스·합성 fixture·일반화한 문서만 포함한다. 외부 프로젝트 이름/경로/소스 파일명/내부 구조/원본 코드/상세 로그, 실제 머신 주소와 인증 정보는 포함하지 않는다. 벤치마크 대상은 **현재 프로젝트**로만 표시한다.
