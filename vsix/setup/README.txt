DBE Visual Studio 확장 설치

1. ZIP을 폴더에 모두 풉니다.
2. 작업을 저장하고 설치 대상 Visual Studio의 창을 닫습니다.
3. Install-DBE.cmd를 실행합니다.
4. 설치 완료 후 Visual Studio를 열고 확장 > DBE > DBE 솔루션 빌드를 선택합니다.

프로젝트만 빌드하려면 솔루션 탐색기에서 C++ 프로젝트 하나를 선택한 뒤
우클릭 > DBE 프로젝트 빌드 또는 상단 빌드 > DBE 프로젝트 빌드를 선택합니다.
DBE 프로젝트 다시 빌드도 같은 위치에 있습니다. 상단 DBE 메뉴/도구 모음에서도 사용할 수 있습니다.
선택한 프로젝트와 필요한 의존 프로젝트가 대상이며 솔루션의 구성 매핑을 유지합니다.
다중 선택·솔루션 폴더·언로드된 프로젝트에서는 프로젝트 명령을 실행하지 않습니다.

설치 프로그램은 설치된 Visual Studio 2022/2026 중 최신 IDE를 선택합니다.
특정 인스턴스 지정: Install-DBE.cmd -InstanceId <vswhere에서 확인한 ID>
설치 없이 대상 확인: Install-DBE.cmd -CheckOnly

Dbe.VisualStudio.vsix를 직접 더블클릭하여 표준 VSIX 설치 관리자로 설치할 수도 있습니다.
Install-DBE.cmd는 메뉴 등록을 위한 설정 갱신까지 수행하고, VS 창을 강제로 닫지 않습니다.

분산 빌드는 솔루션 옆 <솔루션 이름>.dbe.json에 설정된 기존 Adapter와 Worker 연결을 사용합니다.
이 패키지는 VS 확장만 설치합니다. 기존 인증서, Worker 주소, 일반 VS Build 설정을 변경하지 않습니다.
DBE 빌드에서는 프로젝트의 원래 코드 생성·배포·빌드 이벤트가 실행됩니다.
취소: 확장 > DBE > DBE 빌드 취소.
로그: %LOCALAPPDATA%\DBE\builds\<빌드 ID>\build.log (UTF-8)
제거: Visual Studio의 확장 관리에서 DBE Distributed Build를 제거합니다.
