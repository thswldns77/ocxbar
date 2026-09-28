# Usage Tray for Windows

Windows 작업표시줄 알림 영역에 **Codex**와 **Claude Code**의 남은 사용량을 각각 숫자 아이콘으로 표시하고, 알림 영역 왼쪽의 작업표시줄 빈 공간에는 더 읽기 쉬운 가로 요약창을 겹쳐 띄웁니다. 아이콘이나 요약창을 클릭하면 `ocxbar`처럼 서비스별 한도 막대, 남은 비율, 초기화까지 남은 시간을 볼 수 있습니다. 숫자 아이콘은 Codex 공통 한도 중 가장 적게 남은 비율과 Claude Code **주간 한도**를 표시합니다. 가로 요약은 각 서비스의 5시간과 주간 한도, Claude의 Fable 한도가 제공되면 함께 표시합니다. 5분마다 자동으로 새로고침합니다. 일시적 조회 실패 시 최근 30분의 마지막 정상 값과 확인 시각을 표시합니다.

## 실행

`UsageTray.exe`를 더블클릭하세요. 아이콘이 보이지 않으면 작업표시줄의 `^` 숨겨진 아이콘 영역에서 Codex와 Claude 아이콘을 찾아 작업표시줄로 각각 끌어 놓으세요.

- 왼쪽 클릭: 상세 창
- 오른쪽 클릭: 새로고침, Codex/Claude 로그인, 가로 사용량 표시 켜기/끄기, 작업표시줄 안쪽/바로 위 위치 변경, **표시 설정**, Windows 시작 시 실행, 종료
- 녹색: 25% 이상 남음 / 주황색: 10–24% / 빨간색: 10% 미만 / 회색 `?`: 조회 불가

Windows 시작 시 실행은 오른쪽 클릭 메뉴에서 켤 수 있습니다. 실행 파일을 옮기면 해당 옵션을 다시 켜 주세요.

가로 요약창은 Windows 작업표시줄의 공식 확장 요소가 아닌 별도 창입니다. 작업표시줄 버튼이 많아 겹치면 오른쪽 클릭 메뉴에서 **작업표시줄 안쪽에 표시**를 끄면 작업표시줄 바로 위로 이동합니다.
작업표시줄 안쪽에 표시 중일 때는 가로 요약창을 마우스 왼쪽 버튼으로 잡아 좌우로 끌 수 있습니다. 놓은 위치는 앱을 다시 실행해도 유지됩니다. 짧게 클릭하면 상세 창이 열립니다.
작업표시줄 안쪽의 가로 요약은 두 줄로 표시합니다. 예: `Codex 주간 96%`와 `Claude 5h 76% / 주간 64% / Fable 95%`. 5시간 한도가 없으면 주간 값만 표시합니다. 주변 작업표시줄 색을 읽어 배경에 반영하고, 밝기에 따라 글자색을 조정합니다.

트레이 아이콘을 오른쪽 클릭하고 **표시 설정...**을 열면 가로 요약의 글자 크기(8–12pt), 굵기, 글자색을 미리 보며 조절할 수 있습니다. 글자색 자동 선택을 끄면 원하는 색을 지정할 수 있습니다. 저장한 설정은 앱을 다시 실행해도 유지됩니다.

## 로그인

상세 창의 **로그인/계정 변경** 버튼에서 해당 서비스의 공식 CLI 로그인 절차를 시작할 수 있습니다. 새 콘솔과 브라우저가 열리고 인증이 끝나면 **새로고침**을 누르세요. 이 앱은 비밀번호를 받거나 저장하지 않습니다.

- **Codex:** 이미 Codex CLI에서 ChatGPT 계정으로 로그인했다면 추가 로그인은 필요 없습니다. 새로 로그인하려면 버튼을 누르거나 PowerShell에서 `codex login`을 실행하세요.
- **Claude Code:** 이 앱은 일반 설치판과 Microsoft Store판 Claude 앱에 포함된 Claude Code 실행 파일을 찾습니다. 상세 창의 **로그인** 버튼을 누르면 `claude auth login`이 열립니다. Claude 앱에 로그인되어 있어도 CLI의 사용량 조회 인증이 별도로 필요할 수 있습니다. 브라우저에서 같은 Claude 구독 계정으로 인증한 뒤 **새로고침**을 누르세요. 실행 파일을 찾지 못할 때만 [Windows용 Claude Code 설치 안내](https://code.claude.com/docs/en/setup)를 따르세요.

Claude 로그인 때 다른 Chrome 프로필이 열리면 그 창에서는 인증하지 말고, 로그인 콘솔의 URL을 복사해 원하는 프로필의 주소창에 붙여넣으세요. 콘솔에 `c`로 URL 복사 안내가 보이면 **Ctrl+C가 아닌 `c` 키만** 누르세요. 인증을 마친 뒤 **새로고침**을 누르세요.

`ocxbar`는 OpenCodex 서버의 계정 풀을 읽지만 이 Windows 앱은 **각 CLI에 직접 로그인한 계정**을 읽습니다. OpenCodex 설치는 필요하지 않습니다. 여러 계정의 사용량을 동시에 보여주는 기능은 현재 포함하지 않습니다.

## 사용량 데이터

- **Codex:** 이 PC의 Codex CLI 로그인 상태를 사용하고 `codex app-server`의 `account/rateLimits/read`를 호출합니다. CLI가 설치되고 ChatGPT 계정으로 로그인되어 있어야 합니다. API 키 사용량과는 별개입니다.
- **Claude Code:** `CLAUDE_CODE_OAUTH_TOKEN` 또는 `%USERPROFILE%\.claude\.credentials.json`의 Claude Code 로그인 토큰으로 사용량을 조회합니다. 5시간, 주간, 모델별 주간 한도가 제공되면 모두 표시합니다. Claude의 OAuth 사용량 URL은 공개 API 계약이 아니어서 변경될 수 있습니다. 토큰은 디스크에 복사하거나 로그에 남기지 않습니다.
- Claude OAuth 조회가 불가능할 때는 Claude Code의 `statusLine` 입력을 저장한 최근 15분 데이터도 사용할 수 있습니다. 이 방식은 Claude Code를 실제로 사용한 뒤에 갱신되고, 모델별 주간 한도는 제공하지 않습니다.

Claude Code CLI의 사용량 조회 인증이 없으면 Claude 아이콘은 `?`로 보입니다. Claude 데스크톱 앱에 로그인되어 있어도 이 상태가 될 수 있습니다. 카드의 **로그인**을 누른 뒤 **지금 새로고침**을 누르세요.

### Claude 상태줄 연동 (선택)

Claude OAuth 사용량 조회가 막히는 경우, Claude Code의 `%USERPROFILE%\.claude\settings.json`에 아래 `statusLine`을 설정할 수 있습니다. `C:\path\to\bar`를 이 폴더의 실제 경로로 바꾸고 JSON 백슬래시를 두 번 써 주세요. 이미 `statusLine`을 쓰고 있다면 기존 설정을 덮어쓰지 말고 기존 명령에서 `UsageTrayCli.exe --claude-status`로 같은 입력을 전달해야 합니다.

```json
{
  "statusLine": {
    "type": "command",
    "command": "C:\\path\\to\\bar\\UsageTrayCli.exe --claude-status"
  }
}
```

`UsageTrayCli.exe --claude-status`는 입력에서 사용량 수치만 `%LOCALAPPDATA%\UsageTray\claude-status.json`에 저장하며, Claude 터미널 상태줄에도 남은 비율을 출력합니다.

## 소스 빌드

Windows PowerShell에서 `powershell -ExecutionPolicy Bypass -File .\build.ps1`을 실행하세요. Windows에 포함된 .NET Framework C# 컴파일러를 사용하므로 별도의 .NET SDK가 필요하지 않습니다. `UsageTrayCli.exe --self-test`는 응답 형식 파서를 검증하고, `UsageTrayCli.exe --once`는 현재 조회 결과를 한 번 출력합니다.

## 참고

- [OpenAI Codex app-server rate limit protocol](https://github.com/openai/codex/blob/main/codex-rs/app-server-protocol/src/protocol/v2/account.rs)
- [Claude Code status line 데이터 형식](https://code.claude.com/docs/en/statusline)
- [CodexBar Claude OAuth 사용량 경로 설명](https://github.com/steipete/CodexBar/blob/main/docs/claude.md)
