# W7 Day 5 · 정상 플레이 오탐 측정 프로토콜

**목적 두 가지**

1. DoD 판정 — 정상 30분에 V-MOVE 0, V-FIRE 0, V-LOS ≤2
2. 잠정 임계값 확정 — `TrackConeDeg`, `TrackViolationSec`, `ThresholdMs`

**소요**: 10분 × 3세션 + 준비/분석 20분
**인원**: 2명 (혼자 MPPM 2창도 가능하나 교전 표본이 안 쌓인다)

W12 에서 같은 절차를 반복하므로 이 문서를 그대로 재사용한다.

---

## 세션을 왜 3개로 나누는가

한 번에 30분을 돌리면 **위반이 나왔을 때 무엇 때문인지 모른다.**
지금 V-LOS-01 의 가장 유력한 오탐 요인은 반차폐(적의 일부만 보이는 상황)인데,
개활지와 엄폐물 구간을 섞어 놓으면 그 가설을 검증할 수 없다.

세 세션 모두 **정상 플레이** 다. 인위적인 스트레스 테스트가 아니라
실제 플레이에서 나타나는 서로 다른 국면을 나눠 담는다.
그래야 합계를 오탐률로 쓰면서 동시에 원인을 귀속시킬 수 있다.

| 세션 | 성격 | 주로 검증하는 것 |
|---|---|---|
| S1 | 개활지 교전 | 기준선. 차폐가 없으니 V-LOS 는 구조적으로 0이어야 한다 |
| S2 | 엄폐물 교전 | 반차폐 오탐 가설. V-LOS 오탐이 나온다면 여기서 나온다 |
| S3 | 혼합 자유 플레이 | 일반화. 실제 플레이 비율에 가까운 조건 |

S1 에서 V-LOS 가 발화하면 **튜닝 문제가 아니라 버그** 다. 차폐가 없는데 차폐 추적이
쌓일 수는 없다. 그 경우 임계값을 만지지 말고 원인부터 찾는다.

---

## 준비 (측정 전 1회)

```bash
# 1. ingester 가 살아 있는지
ssh game@100.116.18.41 'systemctl is-active ingester; systemctl is-enabled ingester'

# 2. 최신 빌드가 올라가 있는지
ssh game@100.116.18.41 'find ~/gameserver -name "Assembly-CSharp.dll" -printf "%TY-%Tm-%Td %TH:%TM\n"'

# 3. 로그 창 (별도 터미널)
ssh game@100.116.18.41 'tail -f ~/gameserver/logs/server.log | grep --line-buffered -E "\[LOS\]|VIOLATION"'
```

**기존 데이터는 지우지 않는다.** `match_id` 로 분리되므로 TRUNCATE 할 이유가 없다.
지우려면 반드시 `infra/scripts/reset_telemetry.sh` 로 오프셋과 함께 지운다.

---

## 공통 규칙

- **치트 키를 한 번도 누르지 않는다** (F5 ~ F11)
- 세션 시작 전 게임 서버를 재시작한다 — `match` 분리 + `peakTrack` 초기화
- 10분을 채운다. 중간에 끊으면 그 세션은 버린다
- 가만히 서 있지 않는다. 계속 이동하고 교전한다
- 세션 종료 후 20초 기다렸다가 `match_id` 를 기록한다

```bash
ssh game@100.116.18.41 'sudo systemctl restart gameserver'
```

---

## S1 · 개활지 교전 (10분)

**장소**: 벽·큐브에서 최대한 떨어진 열린 공간

**행동**

- 서로 보이는 상태로 계속 이동하며 교전
- 사거리를 의도적으로 섞는다 — 5m 근접, 20m 중거리, 40m 이상 원거리
- 맞히기도 하고 일부러 빗나가기도 한다
- 죽으면 리스폰 후 바로 재교전

**여기서 나오는 데이터**

| | |
|---|---|
| `aim_error_deg` 분포 | 차폐 간섭 없는 순수 조준 정밀도 |
| V-LOS 기준선 | **0건이어야 한다** |
| V-MOVE / V-FIRE 기준선 | 리스폰 직후 유예가 제대로 도는지 |

**빗나간 사격이 중요하다.** 명중분만 모으면 오차 분포의 한쪽 꼬리만 남는다.

---

## S2 · 엄폐물 교전 (10분)

**장소**: 큐브·벽이 있는 구간

**행동**

- 엄폐물을 사이에 두고 교전
- 코너 피킹을 반복한다 — 나왔다 들어갔다
- **적이 완전히 가려진 동안 그쪽을 겨누고 기다리는 것도 한다.** 실제 플레이에서 흔하다
- 적의 일부만 보이는 상황(머리만, 어깨만)에서도 쏜다
- 엄폐물을 피하지 않는다

**여기서 나오는 데이터**

| | |
|---|---|
| V-LOS 오탐 | 이번 측정의 핵심 질문 |
| SPOT / 반응시간 | 코너 피킹이 SPOT 을 만든다 |
| `peakTrack` | 정상 플레이가 어디까지 올라가는가 |

**로그 창의 `peakTrack` 을 눈으로 보세요.** 2.0s(현재 임계)를 넘는 순간을 기억해두면
나중에 그게 어떤 상황이었는지 설명할 수 있다.

재시도할 때는 `ForgetSec` 0.5초보다 오래 가려져야 SPOT 이 새로 발생한다.

---

## S3 · 혼합 자유 플레이 (10분)

**장소**: 맵 전체

**행동**

- 특별한 규칙 없이 평소처럼 플레이
- 개활지와 엄폐물을 오간다
- 이동 위주 구간과 교전 위주 구간을 섞는다

**여기서 나오는 데이터**

S1 과 S2 의 결과가 실제 플레이 비율에서도 유지되는지 본다.
S1·S2 에서 안 보이던 위반이 여기서 나오면 두 세션이 놓친 조건이 있다는 뜻이다.

---

## 세션 라벨 기록

`matches` 테이블의 `label` 컬럼이 이 용도다. 기본값이 `unknown` 이라
나중에 어느 match 가 어느 조건이었는지 알 수 없다.

```sql
SELECT id, match_uid, started_at, label FROM matches ORDER BY id DESC LIMIT 5;
```

```sql
UPDATE matches SET label='normal_open'  WHERE id = <S1>;
UPDATE matches SET label='normal_cover' WHERE id = <S2>;
UPDATE matches SET label='normal_mixed' WHERE id = <S3>;
```

W12 에서 정상/치트 라벨링에 같은 컬럼을 쓴다. 지금부터 습관을 들이는 게 낫다.

---

## 분석

`<S1>,<S2>,<S3>` 를 실제 id 로 바꿔 실행한다.

### 1. 세션별 위반 (DoD 판정)

```sql
SELECT m.label,
       v.code,
       JSON_UNQUOTE(JSON_EXTRACT(v.detail_json,'$.reason')) AS reason,
       COUNT(*) AS windows, SUM(v.occurrences) AS total
FROM violations v
JOIN matches m ON m.id = v.match_id
WHERE v.match_id IN (<S1>,<S2>,<S3>)
GROUP BY m.label, v.code, reason
ORDER BY m.id, v.code;
```

| 코드 | 허용 (30분 합계) | 초과 시 |
|---|---|---|
| V-MOVE-01 | 0 | 즉시 원인 조사 |
| V-FIRE-01 | 0 | 버킷 용량 4 재검토 |
| V-LOS-01 | ≤ 2 | 아래 "임계 재조정" 참조 |
| V-TIME-01 sev 1 | ≤ 10 | 정상 (확률적) |
| V-TIME-01 sev 3 | 0 | `ThresholdMs` 재검토 |

### 2. 반응시간 분포 (`ThresholdMs` 확정용)

```sql
SELECT ROUND(TIMESTAMPDIFF(MICROSECOND, s.server_time, f.server_time)/1000
             - f.rtt_ms/2) AS reaction_ms
FROM combat_events s
JOIN combat_events f
  ON  f.match_id      = s.match_id
  AND f.spot_event_id = s.spot_event_id
  AND f.event_type IN ('FIRE','HIT','KILL')
  AND f.server_time = (SELECT MIN(x.server_time) FROM combat_events x
                       WHERE x.match_id      = s.match_id
                         AND x.spot_event_id = s.spot_event_id
                         AND x.event_type IN ('FIRE','HIT','KILL'))
WHERE s.event_type='SPOT' AND s.match_id IN (<S1>,<S2>,<S3>)
HAVING reaction_ms BETWEEN -200 AND 2000
ORDER BY reaction_ms;
```

**사람 쪽 최솟값** 을 본다. 지금까지 관측된 최솟값은 88ms 다.
30분 표본에서 이보다 낮은 값이 여럿 나오면 실효 임계 60ms 가 위험하다.

기존 봇 값은 14ms, 30ms 였다. 사람 하한과의 간격이 좁아지면 임계를 낮춘다.

### 3. 조준 오차 분포 (W13 기준선)

```sql
SELECT m.label, c.event_type,
       COUNT(*) n,
       ROUND(AVG(c.aim_error_deg * c.target_dist * PI()/180), 3) avg_m,
       ROUND(STDDEV(c.aim_error_deg * c.target_dist * PI()/180), 3) sd_m
FROM combat_events c JOIN matches m ON m.id = c.match_id
WHERE c.match_id IN (<S1>,<S2>,<S3>)
  AND c.aim_error_deg IS NOT NULL AND c.target_dist IS NOT NULL
  AND c.event_type IN ('FIRE','HIT','KILL')
GROUP BY m.label, c.event_type;
```

`sd_m` 이 W13 에임봇 탐지의 기준선이다. 사람의 편차가 얼마인지 알아야
"에임봇은 이 편차가 사라진다"고 말할 수 있다.

### 4. 표본 수 확인

```sql
SELECT m.label,
       SUM(c.event_type='SPOT') AS spots,
       SUM(c.event_type IN ('FIRE','HIT','KILL')) AS shots,
       (SELECT COUNT(*) FROM movement_samples ms WHERE ms.match_id=m.id) AS moves
FROM combat_events c JOIN matches m ON m.id = c.match_id
WHERE c.match_id IN (<S1>,<S2>,<S3>)
GROUP BY m.label;
```

세션당 발사 300건 미만이면 교전이 부족했던 것이다. 그 세션은 다시 한다.

---

## 결과에 따른 조치

### V-LOS 오탐이 3건 이상 (S2 집중)

반차폐 가설이 맞은 것이다. 순서대로 시도한다.

1. **머리·몸통 두 점 판정** — 둘 다 가려질 때만 차폐로 센다.
   `PlayerRewind.TryHeadCenterAt` 이 이미 있으므로 Raycast 한 번만 추가하면 된다.
   가장 정확하고 근본적이다.
2. `TrackConeDeg` 를 좁힌다 (5° → 2°)
3. `TrackViolationSec` 을 올린다

**1번부터 한다.** 2·3번은 탐지율도 함께 떨어뜨린다.

### V-LOS 오탐이 S1 에서 발생

튜닝이 아니라 버그다. 개활지에는 차폐가 없으므로 `blocked=true` 가 될 수 없다.
지형(바닥 Plane)이 레이에 걸리는지부터 확인한다.
눈 높이에서 몸통 중심을 향하면 레이가 아래로 기울어 근거리에서 바닥을 스칠 수 있다.

### V-FIRE 오탐 발생

버킷 용량 4가 부족한 것이다. 지터로 몰려 도착한 입력이 400ms 분량을 넘었다는 뜻이다.
실측 버스트 크기를 보고 용량을 정한다. W6 에서 `input_count` 분포로
V-MOVE 용량 20을 정한 것과 같은 방식이다.

### 사람 반응시간 최솟값이 60ms 에 근접

`ThresholdMs` 를 낮추거나 `RepeatLimit` 에 더 의존한다.
단발 severity 1 은 어차피 차단하지 않으므로, 반복 조건만 엄격히 해도 된다.

---

## 완료 후

- `docs/l2_rules.md` 의 **잠정** 표시와 오탐 표 빈칸을 채운다
- 임계값을 바꿨으면 그 근거(실측 분포)를 같은 문서에 적는다
- Grafana 패널 3·4 를 세션 시간 범위로 맞춰 스크린샷 — 발표자료용
- 커밋
