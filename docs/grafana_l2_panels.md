# Grafana L2 패널

**대상**: mon-server (VM103) Grafana → MariaDB (VM102, `100.64.82.15`)
**작성**: W7 Day 6~7

W8 이 대시보드 전용 주차이므로 여기서는 **W7 DoD 를 채우는 최소 4패널** 만 만든다.
레이아웃과 시연용 구성은 W8 에서 다듬는다.

---

## 1. 데이터소스 추가

Grafana 에는 지금 Prometheus / Loki 만 있다.
`mysqld-exporter` 는 MariaDB **서버 상태 메트릭** 용이라 테이블을 조회할 수 없다.
MySQL 데이터소스를 따로 붙여야 한다.

### 1-1. 읽기 전용 계정 생성 (VM102)

`gs` 계정에는 INSERT / UPDATE 권한이 있다. 대시보드에 그 권한을 줄 이유가 없다.

```sql
CREATE USER 'grafana'@'%' IDENTIFIED BY '<비밀번호>';
GRANT SELECT ON anticheat.* TO 'grafana'@'%';
FLUSH PRIVILEGES;
```

MariaDB 가 tailnet 에만 바인드돼 있는지 확인할 것.

```bash
ssh db@100.64.82.15 'sudo ss -lntp | grep 3306'
```

### 1-2. Grafana 설정

Connections → Data sources → Add → MySQL

| 항목 | 값 |
|---|---|
| Host | `100.64.82.15:3306` |
| Database | `anticheat` |
| User | `grafana` |
| Session timezone | `+00:00` |

**호스트는 반드시 Tailscale IP 로 넣는다.** Grafana 가 Docker 컨테이너 안이면
MagicDNS 이름을 해석하지 못한다. `extra_hosts` 를 쓰고 있어도 IP 가 안전하다.

**Session timezone 은 UTC 다.** DB 에 UTC 로 저장되므로 여기서 KST 를 넣으면
9시간 어긋난다. 표시 시간대는 대시보드 설정(Dashboard settings → Timezone)에서
`Asia/Seoul` 로 바꾼다. 저장은 UTC, 표시만 KST 다.

---

## 2. 패널

### 패널 1 · 위반 타임라인

**시각화**: Time series (Bar / Stacked)
**용도**: 시연의 중심 화면. 치트를 켜면 막대가 즉시 올라온다.

```sql
SELECT
  $__timeGroupAlias(v.server_time, '10s'),
  v.code AS metric,
  SUM(v.occurrences) AS value
FROM violations v
WHERE $__timeFilter(v.server_time)
GROUP BY 1, 2
ORDER BY 1
```

`occurrences` 를 합산하는 이유는 `ViolationLogger` 가 1초 창으로 집계하기 때문이다.
행 수를 세면 실제 발생량을 크게 과소평가한다.

---

### 패널 2 · 위반 상세

**시각화**: Table
**용도**: "무엇이 왜 걸렸는가"를 한 줄로 보여준다.

```sql
SELECT
  v.server_time                                          AS "시각",
  p.player_uid                                           AS "플레이어",
  v.code                                                 AS "코드",
  JSON_UNQUOTE(JSON_EXTRACT(v.detail_json, '$.reason'))  AS "사유",
  v.severity                                             AS "심각도",
  v.occurrences                                          AS "횟수",
  v.first_tick                                           AS "틱",
  v.rtt_ms                                               AS "RTT"
FROM violations v
JOIN players p ON p.id = v.player_id
WHERE $__timeFilter(v.server_time)
ORDER BY v.server_time DESC
LIMIT 100
```

`players` 의 기본키는 `id` 다 (`player_id` 가 아니다).
계정명 컬럼은 없고 `player_uid` 가 식별자다.

`심각도` 컬럼에 Value mappings 로 색을 넣으면 읽기 쉽다 (1 회색 / 2 주황 / 3 빨강).

---

### 패널 3 · 반응시간 분포 ★

**시각화**: Histogram (Bucket size 25)
**용도**: 이번 주 결과 중 가장 설득력 있는 그림.
봇과 사람이 임계를 사이에 두고 갈린다.

```sql
SELECT
  TIMESTAMPDIFF(MICROSECOND, s.server_time, f.server_time) / 1000
    - f.rtt_ms / 2                                       AS reaction_ms
FROM combat_events s
JOIN combat_events f
  ON  f.match_id      = s.match_id
  AND f.spot_event_id = s.spot_event_id
  AND f.event_type IN ('FIRE','HIT','KILL')
  AND f.server_time = (
        SELECT MIN(x.server_time) FROM combat_events x
        WHERE x.match_id      = s.match_id
          AND x.spot_event_id = s.spot_event_id
          AND x.event_type IN ('FIRE','HIT','KILL'))
WHERE s.event_type = 'SPOT'
  AND $__timeFilter(s.server_time)
HAVING reaction_ms BETWEEN -200 AND 2000
```

**`match_id` 를 조인 키에 반드시 포함한다.**
`spot_event_id` 는 서버 프로세스 스코프라 재시작마다 1부터 재발급된다.
빼먹으면 다른 세션의 SPOT 과 FIRE 가 붙어 반응시간이 −24분으로 나온다.

`HAVING` 은 SPOT 후 한참 뒤의 발사(수 초 단위)를 잘라낸다. 그건 반응이 아니다.

임계선 표시: Panel options → Thresholds 에 60 (실효 임계) 을 넣는다.

---

### 패널 4 · 조준 오차 ★

**시각화**: Table
**용도**: W13 에임봇 feature 의 근거. `sd_m` 이 핵심이다.

```sql
SELECT
  event_type                                                     AS "이벤트",
  COUNT(*)                                                       AS "표본",
  ROUND(AVG(target_dist), 1)                                     AS "평균 거리(m)",
  ROUND(AVG(aim_error_deg), 2)                                   AS "조준 오차(도)",
  ROUND(AVG(aim_error_deg * target_dist * PI() / 180), 3)        AS "오차(m)",
  ROUND(STDDEV(aim_error_deg * target_dist * PI() / 180), 3)     AS "오차 표준편차(m)"
FROM combat_events
WHERE aim_error_deg IS NOT NULL
  AND target_dist   IS NOT NULL
  AND event_type IN ('FIRE','HIT','KILL')
  AND $__timeFilter(server_time)
GROUP BY event_type
ORDER BY FIELD(event_type, 'KILL', 'HIT', 'FIRE')
```

각도만 보면 원거리 사격이 정밀해 보인다. 1도는 5m 에서 8.7cm, 50m 에서 87cm 다.
거리를 곱해 미터로 환산해야 비교가 된다.

**표준편차가 에임봇의 신호다.** 사람은 손으로 0.3m 편차를 없앨 수 없다.
평균이 아니라 분산이 무너지는 것을 본다.

---

## 3. 확인

패널 4개를 만든 뒤 하네스로 한 번 훑는다.

```
F10 연사핵 10초  →  패널 1 에 V-FIRE-01 막대
F11 트리거봇     →  패널 3 히스토그램 왼쪽 끝에 봇 구간
정상 교전        →  패널 4 의 HIT 오차가 0.5m 안쪽
```

패널 1 에 아무것도 안 뜨면 순서대로 확인한다.

1. ingester 가 살아 있는가 — `systemctl is-active ingester`
2. 대시보드 시간 범위가 UTC 기준으로 맞는가
3. Session timezone 이 `+00:00` 인가

---

## 4. W8 로 넘길 것

- 실시간 갱신 주기(5s) 와 시연용 레이아웃
- 플레이어별 Trust Score 패널 (W13 이후)
- Prometheus 메트릭 노출 (서버 틱 지연, 검증기 처리량)
- Loki 로그 패널과 위반 타임라인 시각 동기화
