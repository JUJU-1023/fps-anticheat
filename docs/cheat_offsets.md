# 치트 exe 대상 클라 바이너리

이 문서의 오프셋과 포인터 체인은 아래 해시의 클라 빌드에서만 유효하다.
클라를 재빌드하면 전부 무효가 되며, 치트 exe 는 해시가 다르면 실행을 거부한다.

기록일: 2026-09-26

| 파일 | SHA-256 |
|---|---|
| `Game_Data/Managed/Assembly-CSharp.dll` | `e579acf9c596d28f7df719e5340fb5efa11231edc698bcd6b2c9e772b6ae0a86` |
| `UnityPlayer.dll` | `b3da7eb4e7429f03123f9053079f37d90a302890e8a3e4b4f8f0bb494c0f2335` |
| `MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll` | `be655f08bd65d0b8e83bf29c47ce780f060a43f37e21e800f6c2937466c0c3a4` |

## 포인터 체인

(W10 Day 2부터 채움)

## 포인터 체인

### CurrentTick (스피드핵·연사핵 공통)
- 모듈: `mono-2.0-bdwgc.dll`
- 오프셋: `0x73E378` (단일, 포인터 체인 아님)
- 타입: int (4 bytes)
- 재시작 검증: 통과 (2026-09-27, 게임 2회 실행에서 동일 오프셋에 틱 존재)
- 용도: 값을 앞으로 밀면 input.tick 이 점프 → V-MOVE RateExceeded(스피드핵) / V-FIRE 버킷(연사핵)
