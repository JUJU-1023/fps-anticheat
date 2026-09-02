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


def apply_batch(db, records):
    """레코드 묶음을 하나의 트랜잭션으로 적재한다. 실패하면 예외."""
    moves = []
    viols = []

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

            # 미지의 타입은 조용히 건너뛴다. 스키마가 앞서 나가도 깨지지 않는다.

        if moves:
            cur.executemany(MOVE_SQL, moves)
        if viols:
            cur.executemany(VIOL_SQL, viols)

    db.conn.commit()
    return len(moves), len(viols)


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

    total_m = total_v = 0
    try:
        for i in range(0, len(records), batch_size):
            m, v = apply_batch(db, records[i:i + batch_size])
            total_m += m
            total_v += v
    except Exception as e:
        try:
            db.conn.rollback()
        except Exception:
            pass
        log("error", f"{name}: 적재 실패, 오프셋 유지 ({e})")
        return False

    ckpt.set(name, start + consumed)
    log("info", f"{name}: move={total_m} violation={total_v} offset={start + consumed}")
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
