#!/usr/bin/env python3
# =====================================================================
#  ingester.py
#  경로: analytics/ingester/ingester.py  (VM101에 배포)
#
#  게임 서버가 쓴 JSONL 텔레메트리를 읽어 MariaDB(VM102)에 적재한다.
#
#  설계
#   - 파일별 오프셋을 체크포인트에 기록한다. 재시작하면 이어서 읽는다.
#   - DB 쓰기에 실패하면 오프셋을 갱신하지 않는다.
#     DB가 복구되면 같은 구간을 다시 읽어 자동 재전송된다.
#   - 개행으로 끝나지 않는 마지막 조각은 버린다.
#     Unity가 1초마다 flush하므로 읽는 순간 줄이 잘려 있을 수 있다.
#   - match_uid / player_uid를 DB의 BIGINT id로 해석하고 캐시한다.
#
#  처리 타입: match_start / match_end / move / violation / combat
#
#  실행
#   python3 ingester.py --dir /home/game/gameserver/telemetry
# =====================================================================

import argparse
import json
import os
import signal
import sys
import time
from datetime import datetime, timezone

try:
    import pymysql
except ImportError:
    sys.exit("pymysql이 필요합니다: pip3 install pymysql")


# ---------------------------------------------------------------------
#  설정
# ---------------------------------------------------------------------

def parse_args():
    p = argparse.ArgumentParser(description="텔레메트리 JSONL -> MariaDB")
    p.add_argument("--dir", default="/home/game/gameserver/telemetry",
                   help="JSONL 디렉터리")
    p.add_argument("--checkpoint", default=None,
                   help="오프셋 체크포인트 경로 (기본: <dir>/.ingest_offset.json)")
    p.add_argument("--interval", type=float, default=5.0,
                   help="폴링 주기(초)")
    p.add_argument("--batch", type=int, default=1000,
                   help="INSERT 배치 크기")
    p.add_argument("--db-host", default=os.environ.get("DB_HOST", "100.64.82.15"))
    p.add_argument("--db-port", type=int, default=int(os.environ.get("DB_PORT", "3306")))
    p.add_argument("--db-user", default=os.environ.get("DB_USER", "gs"))
    p.add_argument("--db-pass", default=os.environ.get("DB_PASS", ""))
    p.add_argument("--db-name", default=os.environ.get("DB_NAME", "anticheat"))
    p.add_argument("--once", action="store_true",
                   help="한 번만 처리하고 종료 (테스트용)")
    return p.parse_args()


def log(level, msg):
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
    print(f'{{"ts":"{ts}","level":"{level}","src":"ingester","msg":{json.dumps(msg, ensure_ascii=False)}}}',
          flush=True)


# ---------------------------------------------------------------------
#  체크포인트
# ---------------------------------------------------------------------

class Checkpoint:
    """파일명 -> 읽은 바이트 수."""

    def __init__(self, path):
        self.path = path
        self.data = {}
        self._load()

    def _load(self):
        try:
            with open(self.path, "r", encoding="utf-8") as f:
                self.data = json.load(f)
            log("info", f"체크포인트 로드: {len(self.data)}개 파일")
        except FileNotFoundError:
            self.data = {}
        except Exception as e:
            log("error", f"체크포인트 손상, 처음부터 시작: {e}")
            self.data = {}

    def get(self, name):
        return self.data.get(name, 0)

    def set(self, name, offset):
        self.data[name] = offset

    def save(self):
        # 원자적 교체. 쓰는 도중 죽어도 기존 파일이 남는다.
        tmp = self.path + ".tmp"
        try:
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(self.data, f)
            os.replace(tmp, self.path)
        except Exception as e:
            log("error", f"체크포인트 저장 실패: {e}")


# ---------------------------------------------------------------------
#  DB
# ---------------------------------------------------------------------

class Db:
    def __init__(self, args):
        self.args = args
        self.conn = None
        self._match_cache = {}    # match_uid -> id
        self._player_cache = {}   # player_uid -> id

    def connect(self):
        if self.conn is not None:
            try:
                self.conn.ping(reconnect=True)
                return True
            except Exception:
                self.conn = None

        try:
            self.conn = pymysql.connect(
                host=self.args.db_host,
                port=self.args.db_port,
                user=self.args.db_user,
                password=self.args.db_pass,
                database=self.args.db_name,
                charset="utf8mb4",
                autocommit=False,
                connect_timeout=5,
            )
            # 재연결 시 id가 바뀌었을 수 있으니 캐시를 비운다.
            self._match_cache.clear()
            self._player_cache.clear()
            log("info", "DB 연결 성공")
            return True
        except Exception as e:
            log("warn", f"DB 연결 실패: {e}")
            self.conn = None
            return False

    # --- uid -> id 해석 ---

    def match_id(self, uid, started_at=None, map_name=None, tick_rate=60):
        if uid in self._match_cache:
            return self._match_cache[uid]

        with self.conn.cursor() as cur:
            cur.execute("SELECT id FROM matches WHERE match_uid=%s", (uid,))
            row = cur.fetchone()
            if row:
                self._match_cache[uid] = row[0]
                return row[0]

            # match_start를 못 본 채 move가 먼저 온 경우에도 행을 만든다.
            cur.execute(
                "INSERT INTO matches (match_uid, started_at, map_name, tick_rate, label) "
                "VALUES (%s, %s, %s, %s, 'unknown')",
                (uid, started_at or datetime.now(timezone.utc), map_name, tick_rate))
            mid = cur.lastrowid
            self._match_cache[uid] = mid
            return mid

    def player_id(self, uid, is_bot=False):
        if uid in self._player_cache:
            return self._player_cache[uid]

        with self.conn.cursor() as cur:
            cur.execute("SELECT id FROM players WHERE player_uid=%s", (uid,))
            row = cur.fetchone()
            if row:
                self._player_cache[uid] = row[0]
                return row[0]

            cur.execute(
                "INSERT INTO players (player_uid, is_bot, first_seen) VALUES (%s, %s, %s)",
                (uid, is_bot, datetime.now(timezone.utc)))
            pid = cur.lastrowid
            self._player_cache[uid] = pid
            return pid


# ---------------------------------------------------------------------
#  레코드 처리
# ---------------------------------------------------------------------

def parse_ts(s):
    """ISO-8601 'Z' 형식을 datetime으로."""
    if not s:
        return datetime.now(timezone.utc)
    try:
        return datetime.strptime(s, "%Y-%m-%dT%H:%M:%S.%fZ")
    except ValueError:
        try:
            return datetime.strptime(s, "%Y-%m-%dT%H:%M:%SZ")
        except ValueError:
            return datetime.now(timezone.utc)


MOVE_SQL = """
INSERT INTO movement_samples
 (match_id, player_id, is_bot, server_tick, client_tick, server_time,
  pos_x, pos_y, pos_z, vel_x, vel_y, vel_z, yaw, pitch,
  buttons, grounded,
  yaw_rate_max, pitch_rate_max, speed_max, input_count, rtt_ms)
VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)
"""

VIOL_SQL = """
INSERT INTO violations
 (match_id, player_id, code, layer, severity,
  first_tick, last_tick, occurrences, server_time, rtt_ms, detail_json)
VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)
"""

COMBAT_SQL = """
INSERT INTO combat_events
 (match_id, player_id, target_id, event_type, weapon_id, shot_index,
  server_tick, client_tick, server_time,
  target_dist, aim_error_deg, rewind_ms, is_headshot,
  yaw, pitch, expected_recoil_pitch, rtt_ms, spot_event_id,
  fire_gap_ticks, fire_gap_ms)
VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)
"""



def apply_batch(db, records):
    """레코드 묶음을 하나의 트랜잭션으로 적재한다. 실패하면 예외."""
    moves = []
    viols = []
    combats = []

    with db.conn.cursor() as cur:
        for r in records:
            t = r.get("t")

            if t == "match_start":
                # 행을 미리 만들어 두고 메타데이터를 채운다.
                mid = db.match_id(r["match_uid"],
                                  parse_ts(r.get("started_at")),
                                  r.get("map"),
                                  r.get("tick_rate", 60))
                cur.execute(
                    "UPDATE matches SET started_at=%s, map_name=%s, tick_rate=%s "
                    "WHERE id=%s",
                    (parse_ts(r.get("started_at")), r.get("map"),
                     r.get("tick_rate", 60), mid))

            elif t == "match_end":
                mid = db.match_id(r["match_uid"])
                cur.execute("UPDATE matches SET ended_at=%s WHERE id=%s",
                            (parse_ts(r.get("ended_at")), mid))

            elif t == "move":
                mid = db.match_id(r["match_uid"])
                pid = db.player_id(r["player_uid"], r.get("is_bot", False))
                pos = r.get("pos", [0, 0, 0])
                vel = r.get("vel", [0, 0, 0])
                moves.append((
                    mid, pid, r.get("is_bot", False),
                    r.get("server_tick", 0), r.get("client_tick", 0),
                    parse_ts(r.get("ts")),
                    pos[0], pos[1], pos[2],
                    vel[0], vel[1], vel[2],
                    r.get("yaw", 0.0), r.get("pitch", 0.0),
                    r.get("buttons", 0), r.get("grounded", True),
                    r.get("yaw_rate_max"), r.get("pitch_rate_max"),
                    r.get("speed_max"), r.get("input_count"),
                    r.get("rtt_ms"),
                ))

            elif t == "violation":
                mid = db.match_id(r["match_uid"])
                pid = db.player_id(r["player_uid"], r.get("is_bot", False))
                detail = r.get("detail")
                viols.append((
                    mid, pid, r.get("code", "UNKNOWN"),
                    r.get("layer", "L2"), r.get("severity", 1),
                    r.get("first_tick", 0), r.get("last_tick", 0),
                    r.get("occurrences", 1),
                    parse_ts(r.get("ts")), r.get("rtt_ms"),
                    json.dumps(detail, ensure_ascii=False) if detail else None,
                ))

            elif t == "combat":
                mid = db.match_id(r["match_uid"])
                pid = db.player_id(r["player_uid"], r.get("is_bot", False))

                # 표적은 이미 등록된 플레이어다. is_bot 은 본인 이벤트에서 정해진다.
                tuid = r.get("target_uid")
                tid  = db.player_id(tuid, False) if tuid else None

                combats.append((
                    mid, pid, tid,
                    r.get("event_type", "FIRE"), r.get("weapon_id"),
                    r.get("shot_index"),
                    r.get("server_tick", 0), r.get("client_tick"),
                    parse_ts(r.get("ts")),
                    r.get("target_dist"), r.get("aim_error_deg"),
                    r.get("rewind_ms"), r.get("is_headshot"),
                    r.get("yaw"), r.get("pitch"),
                    r.get("expected_recoil_pitch"),
                    r.get("rtt_ms"),
                    r.get("spot_event_id"),
                    # 반동 인덱스 리셋 판정의 두 축. 첫 발은 null 로 들어온다.
                    r.get("fire_gap_ticks"),
                    r.get("fire_gap_ms"),
                ))

            # 미지의 타입은 조용히 건너뛴다. 스키마가 앞서 나가도 깨지지 않는다.

        if moves:
            cur.executemany(MOVE_SQL, moves)
        if viols:
            cur.executemany(VIOL_SQL, viols)
        if combats:
            cur.executemany(COMBAT_SQL, combats)

    db.conn.commit()
    return len(moves), len(viols), len(combats)


# ---------------------------------------------------------------------
#  파일 스캔
# ---------------------------------------------------------------------

def process_file(db, path, name, ckpt, batch_size):
    """
    한 파일을 오프셋부터 읽어 적재한다.
    성공한 만큼만 오프셋을 올린다. 실패하면 오프셋을 그대로 두고 False.
    """
    start = ckpt.get(name)
    size = os.path.getsize(path)

    if size < start:
        # 파일이 줄었다 = 다른 파일로 교체됨. 처음부터 다시.
        log("warn", f"{name}: 크기가 줄어 오프셋 리셋 ({start} -> 0)")
        start = 0

    if size == start:
        return True

    with open(path, "rb") as f:
        f.seek(start)
        chunk = f.read(size - start)

    # 개행으로 끝나지 않으면 마지막 조각은 미완성이다.
    last_nl = chunk.rfind(b"\n")
    if last_nl < 0:
        return True          # 완성된 줄이 하나도 없음
    usable = chunk[:last_nl + 1]
    consumed = len(usable)

    records = []
    bad = 0
    for line in usable.decode("utf-8", errors="replace").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            records.append(json.loads(line))
        except json.JSONDecodeError:
            bad += 1

    if bad:
        log("warn", f"{name}: JSON 파싱 실패 {bad}줄")

    if not records:
        ckpt.set(name, start + consumed)
        return True

    total_m = total_v = total_c = 0
    try:
        for i in range(0, len(records), batch_size):
            m, v, c = apply_batch(db, records[i:i + batch_size])
            total_m += m
            total_v += v
            total_c += c
    except Exception as e:
        try:
            db.conn.rollback()
        except Exception:
            pass
        log("error", f"{name}: 적재 실패, 오프셋 유지 ({e})")
        return False

    ckpt.set(name, start + consumed)
    log("info", f"{name}: move={total_m} violation={total_v} combat={total_c} "
                f"offset={start + consumed}")
    return True


def scan_once(db, args, ckpt):
    try:
        names = sorted(n for n in os.listdir(args.dir) if n.endswith(".jsonl"))
    except FileNotFoundError:
        log("warn", f"디렉터리 없음: {args.dir}")
        return

    if not names:
        return

    if not db.connect():
        return   # DB가 죽어 있으면 이번 주기는 건너뛴다. 파일은 계속 쌓인다.

    dirty = False
    for name in names:
        path = os.path.join(args.dir, name)
        before = ckpt.get(name)
        ok = process_file(db, path, name, ckpt, args.batch)
        if not ok:
            break                     # DB 문제. 이후 파일도 어차피 실패한다.
        if ckpt.get(name) != before:
            dirty = True

    if dirty:
        ckpt.save()


# ---------------------------------------------------------------------
#  main
# ---------------------------------------------------------------------

_running = True


def _stop(signum, frame):
    global _running
    _running = False
    log("info", f"종료 신호 수신 ({signum})")


def main():
    args = parse_args()
    if args.checkpoint is None:
        args.checkpoint = os.path.join(args.dir, ".ingest_offset.json")

    if not args.db_pass:
        log("warn", "DB 비밀번호가 비어 있습니다 (DB_PASS 환경변수 확인)")

    signal.signal(signal.SIGINT, _stop)
    signal.signal(signal.SIGTERM, _stop)

    ckpt = Checkpoint(args.checkpoint)
    db = Db(args)

    log("info", f"시작: dir={args.dir} interval={args.interval}s")

    if args.once:
        scan_once(db, args, ckpt)
        return

    while _running:
        try:
            scan_once(db, args, ckpt)
        except Exception as e:
            log("error", f"스캔 중 예외: {e}")
        # 종료 신호에 빠르게 반응하도록 잘게 쪼개 대기
        waited = 0.0
        while _running and waited < args.interval:
            time.sleep(0.2)
            waited += 0.2

    ckpt.save()
    log("info", "종료")


if __name__ == "__main__":
    main()

V-RECOIL-01 노리코일 탐지 (W8 Day 2 확정)

지표   |pitch[n] - pitch[n-1]| < 0.05 인 발의 비율
표본   shot_index >= 8 (램프 이후, 반동 1.1 고정 구간)
       버스트 내 연속 발사만 (fire_gap_ticks <= 21)
판정   창 40발 중 24발(60%) 이상 고정 → 위반

실측
  clean  match 64  4.6%   match 71  9.0%
         match 75  6.3%   match 63  7.6%   match 68  8.7%
  cheat  match 72 64.8%   match 76 70.1%   match 65 93.3%

  정상 상한 9.0% / 핵 하한 64.8%. 임계 60%는 정상 대비 6.7배 여유.

서버 구현 검증 (match 76)
  서버 로그 samples=515 frozen=361 (70.1%)
  SQL      n=515       frozen=361  70.1%
  → 완전 일치. 버스트 경계·램프 제외·클램프 가드 정상 동작.
  violations=1 (히스테리시스로 531발에 1건), maxRatio=1.000

미확인
  정상 세션의 창 단위 maxRatio. 0.5 를 넘으면 임계 상향 필요.
  실측 표본이 동일 플레이어 1명. 개인차 검증 필요.

## 발사 게이트 순서 (W8 Day 3)

1. 게임플레이 간격 게이트 (클라 틱)
2. 재장전 중          ← 탄약 게이트
3. 탄약 0             ← 탄약 게이트
4. V-FIRE-01 토큰 버킷
5. 승인

★ 탄약 게이트를 V-FIRE 뒤로 옮기면 안 된다 ★

GatherInput 은 마우스를 누르는 동안 매 틱 BTN_FIRE 를 보낸다.
탄약 0 이거나 재장전 중이면 _lastFireTick 이 전진하지 않으므로
gapTicks 가 계속 커져 간격 게이트를 매번 통과한다. 그 요청이
V-FIRE 까지 도달하면 초당 60개씩 토큰을 태운다. 충전은 10/s 라
즉시 위반이 난다.

실측 검증 (match 79)
  정상 세션  noAmmoRejected=334  violationRejected=0
             reloadRejected=113  reloads=5  reloadIgnored=59
             → 탄약 0 에서 마우스 5초 유지해도 V-FIRE 오탐 0건

  F10 연사핵  violationRejected=2104  (38행, 초당 61건)
             → 탄약 게이트가 앞에 있어도 연사핵 탐지는 유지된다

W7 Day 5 의 V-MOVE 183건 연쇄와 같은 구조다.
거부 경로가 상태를 전진시키지 않으면 정상 입력이 무한히 재시도되어
다음 검증기를 폭격한다.

players id 13~18(pending-N), 21(#2), 22(#5)는 W8 Day4 이전 uid 버그로
생긴 중복 행. W13 플레이어 단위 집계에서 제외한다.
유효 테스터는 ...8ea4(#1 포함) 와 ...dda7 두 명.

session_summary 테이블이 비어 있다. PlayerHealth.Kills/Deaths 를
집계해 넣는 코드가 없다. combat_events 의 KILL 행으로 유도 가능하므로
급하지 않음. W13 에서 판단.

pitch 는 0.05 도 격자 위에만 존재한다 (마우스 1카운트 = 0.05도).
반동 1.1도 = 정확히 22카운트.
따라서 |Δpitch| < 0.05 는 사실상 Δpitch == 0 과 같고,
epsilon 을 0.005 까지 조여도 걸러지는 표본이 없다 (e050 == e005).

반동 제어를 잘하는 사람은 22카운트가 최빈값이라 frozen 비율이
구조적으로 높다. 비율로는 정상 22.0% / 핵 64.8% 까지 좁혀진다.
갈리는 것은 연속 길이다. 정상은 avg_run 1.05 (단발 사고),
핵은 5~33 (지속 상태).

V-RECOIL-01 이 잡아낸 게임 로직 버그 (W8.5)

정상 플레이에서 max_run 이 2 → 14 로 뛰어 조사한 결과,
FinishClientReload 가 서버의 이미 차감된 _reserve 를 읽어
마지막 재장전에서 클라 탄약이 0 으로 남는 버그를 발견했다.

서버는 탄창 30 발로 발사를 승인하고 기록했지만 클라는
반동을 적용하지 않았다. 그 구간이 노리코일과 구분되지 않는다.

탐지 지표가 없었다면 발견하지 못했을 버그다.
클라 예측을 제거하고 서버 NetworkVariable 을 직접 읽도록 수정.

V-RECOIL-01 (확정)
  지표   |Δpitch| < 0.05 인 발사의 연속 길이
  표본   shot_index >= 8, 버스트 내 연속 발사
  카운터 frozen → +1,  not frozen → max(0, -1)
  임계   25 이상 → 위반

  정상 max_run  2 / 2 / 2 / 2 / 2 / 3 / 4   (7세션, 2명)
  핵   max_run  45 / 57 / 290               (3세션)

## V-RECOIL-01 노리코일 탐지 (W8.5 확정)

지표   |pitch[n] - pitch[n-1]| < 0.05 인 발사의 연속 길이
표본   shot_index >= 8 (램프 이후), 버스트 내 연속 발사
카운터 frozen → +1,  not frozen → max(0, -1)
임계   25 이상 → 위반

### 실측

| 라벨 | 매치 | max_run |
|---|---|---|
| clean | 63, 64, 71, 99(x2), 101 | 2 ~ 4 |
| cheat | 72 | 45 |
| cheat | 76 | 57 |
| cheat | 65 | 290 |

정상 상한 4 / 핵 하한 45. 11배.
임계 25 는 정상 쪽 6배, 핵 쪽 1.8배 여유.

### 왜 비율이 아니라 연속 길이인가

pitch 는 0.05도 격자 위에만 존재한다 (마우스 1카운트 = 0.05도).
반동 1.1도 = 정확히 22카운트. 따라서 |Δpitch| < 0.05 는 사실상
Δpitch == 0 과 같고, epsilon 을 0.005 까지 조여도 걸러지는 표본이
없다 (8세션 전부에서 e050 == e005).

반동 제어 중에는 22카운트가 최빈값이라 frozen 비율이 구조적으로
높아진다. 비율 지표로는 정상 22.0% / 핵 64.8% 까지 좁혀졌다.

갈리는 것은 연속 길이다.
  정상  avg_run 1.05~1.36 — 우연히 22카운트를 맞춘 단발 사고
  핵    avg_run 5~33      — 마우스를 안 만지는 지속 상태

### 지표 변경 이력

1차  창 20발 표본 표준편차, 임계 0.30
     → match 68 오탐. 정상 최저 0.228 이 핵 값 0.250~0.290 보다 낮아
       분포가 겹쳤다. 매치 전체 편차(0.850)로 임계를 정하고
       창 20발 편차(0.228)로 판정한 것이 원인.
       교훈: 임계는 판정과 같은 단위에서 측정해야 한다.

2차  창 40발의 frozen 비율, 임계 60%
     → 조준을 유지한 노리코일(match 72)에서 64.8% 까지 내려오고,
       반동 제어가 좋은 정상 플레이(match 97)가 22.0% 까지 올라와
       여유가 1.5배로 좁아졌다.

3차  연속 길이 (현재)
     → 11배 분리. 창 큐가 필요 없어 구현도 단순하다.

### 감쇠를 쓰는 이유

단순 리셋이면 20발마다 마우스를 한 번 툭 치는 것으로 빠져나간다.
-1 감쇠면 그래도 누적된다. 실측 8세션 전부에서 리셋 방식과
같은 결과가 나오므로 손해가 없다.

### 한계

표본 2명, 세션당 판정 110~740 발. 더 쌓을수록 정상 상한이
올라갈 수 있다. 다만 정상 쪽 여유가 6배라 10 까지 올라가도 안전하다.

반동과 정확히 같은 양을 매번 움직이는 역보정 매크로는 통과한다.
그 경우 pitch 가 계속 변하므로 frozen 으로 세지 않는다.
W13 Trust Score 에서 Δpitch 분포의 결정론성으로 잡는 편이 낫다.

## V-RECOIL-01 이 잡아낸 게임 로직 버그 (W8.5)

정상 플레이의 max_run 이 2 에서 14 로 뛰어 조사한 결과,
WeaponSystem.FinishClientReload 가 서버의 이미 차감된 _reserve 를
읽어 마지막 재장전에서 클라 탄약이 0 으로 남는 버그를 발견했다.

  서버  탄창 30발 보유 → 발사 승인 → combat_events 에 기록
  클라  _clientAmmo = 0 → 반동 미적용

마지막 탄창 전체가 "반동 없는 발사"로 기록됐고, 이는 노리코일과
구분되지 않는다.

  match 98 p19  accepted=150 ammo=30/0  마지막 탄창 미사용  max_run 1
  match 98 p1   accepted=172 ammo=8/0   마지막 탄창 22발    max_run 13
  match 99      서로 사살 반복 (리스폰마다 재동기)          max_run 2
  match 101     수정 후, 180발 전량 소진                    max_run 4

수정: 클라 탄약 예측을 제거하고 서버 NetworkVariable 을 직접 읽도록.
      (matches 97, 98 은 label=unknown, 임계 산정에서 제외)

탐지 지표가 없었다면 발견하지 못했을 버그다.

V-RECOIL-01 (W8.5 확정)

지표   |Δpitch| < 0.05 인 발사의 연속 길이
       frozen → +1,  not frozen → max(0, -1)
임계   25

서버 구현 실측
  정상  samples=132  frozen=14 (10.6%)  maxRun=1   violations=0
  핵    samples=44   frozen=36 (81.8%)  maxRun=28  violations=1
  핵    samples=88   frozen=46 (52.3%)  maxRun=44  violations=1

SQL 교차검증 (버그 수정 후)
  정상  match 99, 101   max_run 2 ~ 4
  핵    match 65,72,76  max_run 45 / 57 / 290

정상 상한 4, 핵 하한 28. 임계 25 는 정상 쪽 6배 여유.

비율로는 정상 10.6~22.0% / 핵 52.3~93.3% 로 최소 2.4배까지 좁혀진다.
연속 길이로는 4 대 28 이상. 같은 데이터에서 분리도가 다르다.

StatePayload.tick은 소유 클라이언트의 로컬 틱이다. 플레이어마다 원점이 다르므로 서로 다른 플레이어의 틱을 같은 축에서 비교하면 안 된다
NGO가 (Clone)을 재사용하면 Awake가 다시 안 불려 latestTick·버퍼에 이전 세션 값이 남는다. 단조 증가 비교(state.tick > latestTick)와 만나면 보간이 영구히 멈춘다
캐릭터 모델은 원인이 아니라 접속 지연을 키워 기존 버그를 드러낸 계기였다
일반화하면 "초기화 없는 오브젝트 재사용 + 단조 증가 비교"는 영구 정지를 만든다. V-MOVE의 lastProcessedTick, V-FIRE의 _lastFireTick과 같은 계열



StatePayload.tick에는 축이 두 개다. BroadcastStateClientRpc는 클라 로컬 틱, ForceStateClientRpc는 서버 틱. 실측 차이 7,870
같은 변수에 두 축의 틱을 넣으면 조용히 깨진다. 역행 폭이 매번 같은 값이면 지터가 아니라 축 혼선이다 — 이게 판별 기준
초기화 없는 오브젝트 재사용 + 단조 증가 비교는 영구 정지를 만든다. V-MOVE lastProcessedTick, V-FIRE _lastFireTick과 같은 계열

StatePayload.tick에는 축이 두 개다. BroadcastStateClientRpc는 소유 클라이언트의 로컬 틱, ForceStateClientRpc(ServerTeleport)는 서버 틱. 실측 차이 7,870틱. 같은 변수에 두 축을 넣으면 조용히 깨진다
역행 폭이 매번 같은 상수면 지터가 아니라 축 혼선이다. 이게 판별 기준
초기화 없는 오브젝트 재사용 + 단조 증가 비교 = 영구 정지. NGO가 (Clone)을 재사용하면 Awake가 다시 안 불린다. V-MOVE lastProcessedTick, V-FIRE _lastFireTick과 같은 계열로 이 프로젝트에서 세 번째
