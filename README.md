# DBE — Distributed Build Engine

Windows MSVC C++ 빌드를 요청 PC와 여러 Worker에 배정하는 실험 프로젝트. 네이티브 wrapper, MSBuild Adapter, TLS Worker와 Visual Studio 확장 소스를 포함한다.

**상태: 오류 수정과 Local 전용 성능 비교를 재개했다.** 긴 임시 object 경로를 수정하고 Local 전용 실행에 원래 MSVC PCH/MP를 보존했다. 합성 회귀 35개가 통과했다. 현재 프로젝트의 유효한 반복 성능 비교와 Hybrid 전체 성공은 아직 확인 중이며, 가속 달성을 주장하지 않는다. [벤치마크](docs/benchmark.md)를 참고한다.

- [설치](docs/installation.md): 소스 빌드, Worker 패키지, VSIX.
- [연결](docs/connection.md): 인증 pair, 여러 Worker, 솔루션 설정.
- [사용법](docs/usage.md): 프로젝트/솔루션 빌드, 취소, 로그.
- [구조와 한계](docs/design.md), [이슈 추적](docs/issues.md), [개발 지침](AGENTS.md).

Windows x64, PowerShell 7, VS 2022 C++ v143/Windows SDK, `worker/global.json`의 .NET SDK를 사용한다. Incredibuild나 별도 역공학 도구는 필요하지 않다.

```powershell
$build = .\scripts\Build-Worker.ps1 -SelfContained
.\scripts\Build-Wrapper.ps1 -VisualStudio 2022
```

`native/`는 프로세스 감독과 wrapper, `worker/`는 TLS/옵션/스케줄러, `msbuild/`는 빌드 추적, `vsix/`는 UI 명령, `scripts/`와 `fixture*/`는 패키징과 합성 검증이다. 실행 산출물은 `out/`에만 생성한다.

Git에는 DBE 자체 소스·합성 fixture·일반화한 문서만 포함한다. 외부 프로젝트 이름/경로/소스 파일명/내부 구조/원본 코드/상세 로그, 실제 머신 주소와 인증 정보는 포함하지 않는다. 벤치마크 대상은 **현재 프로젝트**로만 표시한다.
