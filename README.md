# Broken Arrow Player Tracker

Broken Arrow 멀티플레이 로비의 플레이어 이름을 감지하고, BATrace 공개 데이터에서 이름이 정확히 일치하는 플레이어의 통계를 표시하는 Windows 데스크톱 앱입니다.

## 주요 기능

- 로비 입장과 퇴장 자동 감지
- 플레이어 ELO, 세계 순위, 레벨, 랭크 게임 수, K/D, Turtle Index 표시
- Steam 프로필 이미지와 프로필 링크 표시
- 영문·숫자·한글·중국어·일본어·러시아어 닉네임 처리
- 현재 표를 유지하면서 플레이어 행 갱신

## 실행 방법

1. [Releases](../../releases)에서 `BrokenArrowSquadTracker.exe`를 받습니다.
2. EXE를 실행하고 Windows 관리자 권한 요청을 허용합니다.
3. Broken Arrow를 실행하고 멀티플레이 로비에 입장합니다.

패킷 감지에는 Windows 기본 도구인 `pktmon`을 사용하므로 관리자 권한이 필요합니다. 별도의 패킷 드라이버는 설치하지 않습니다.

## 요구사항

- Windows x64
- .NET 10 Windows Desktop Runtime x64

.NET 런타임이 없으면 Windows의 Microsoft .NET AppHost 안내 창을 통해 설치할 수 있습니다.

## 데이터

플레이어 통계는 BATrace 공개 데이터에서 조회합니다. 패킷에서 감지된 이름과 BATrace 검색 결과가 정확히 일치할 때만 표에 표시합니다.
