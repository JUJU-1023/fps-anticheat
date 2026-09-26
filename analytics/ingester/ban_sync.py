#!/usr/bin/env python3
# =====================================================================
#  ban_sync.py
#  경로: analytics/ingester/ban_sync.py  (VM101에 배포, ingester 옆)
#
#  bans 테이블에서 "지금 집행해야 할 것"만 골라 게임 서버가 읽을
#  JSON 파일로 내보낸다. 게임 서버는 DB 에 직접 붙지 않으므로
#  (Unity DLL 충돌) 텔레메트리와 같은 파일 중계 구조를 거꾸로 쓴다.
#
#  설계
#   - 유효 판정은 전부 DB 시계(UTC) NOW(3) 로 한다. 이 VM 의 시계를 쓰지 않는다.
#   - autocommit=True 필수. 트랜잭션을 열어둔 채 SELECT 를 반복하면
#     REPEATABLE READ 스냅샷에 갇혀 새 밴이 영원히 안 보인다.
#   - tmp 에 쓰고 os.replace 로 교체한다. 게임 서버가 반쯤 쓰인 파일을 읽지 않는다.
#   - DB 가 죽으면 파일을 건드리지 않는다. 게임 서버는 마지막 목록으로 계속
#     집행하고, 30초 넘게 갱신이 없으면 경고를 남긴다.
#   - 매 주기 다시 쓴다(내용이 같아도). 파일 mtime 이 곧 이 프로세스의 생존 신호다.
#
#  킥 규칙
#   발행 후 KICK_WINDOW_SEC 이내, 해제 안 됐고, 아직 집행 안 된 킥만 넣는다.
#   대시보드 BanRepository.KickWindowSeconds 와 반드시 같은 값.
#
#  계정: gs (SELECT 만 사용)
#
#  실행
#   DB_PASS=1234 python3 ban_sync.py --out /home/game/gameserver/bans/active_bans.json
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


KICK_WINDOW_SEC = 60   # ★ 대시보드 BanRepository.KickWindowSeconds 와 같아야 한다

SQL_NOW = "SELECT NOW(3)"

SQL_BANS = """
SELECT b.id, p.player_uid, b.expires_at
FROM bans b
JOIN players p ON p.id = b.player_id
WHERE b.action = 'BAN'
  AND b.revoked_at IS NULL
  AND (b.expires_at IS NULL OR b.expires_at > NOW(3))
ORDER BY b.id
"""

SQL_KICKS = f"""
SELECT b.id, p.player_uid
FROM bans b
JOIN players p ON p.id = b.player_id
WHERE b.action = 'KICK'
  AND b.revoked_at IS NULL
  AND b.enforced_at IS NULL
  AND b.created_at > NOW(3) - INTERVAL {KICK_WINDOW_SEC} SECOND
ORDER BY b.id
"""


def parse_args():
    p = argparse.ArgumentParser(description="bans -> active_bans.json")
    p.add_argument("--out", default="/home/game/gameserver/bans/active_bans.json",
                   help="게임 서버가 읽을 파일 경로")
    p.add_argument("--interval", type=float, default=2.0, help="폴링 주기(초)")
    p.add_argument("--db-host", default=os.environ.get("DB_HOST", "100.64.82.15"))
    p.add_argument("--db-port", type=int, default=int(os.environ.get("DB_PORT", "3306")))
    p.add_argument("--db-user", default=os.environ.get("DB_USER", "gs"))
    p.add_argument("--db-pass", default=os.environ.get("DB_PASS", ""))
    p.add_argument("--db-name", default=os.environ.get("DB_NAME", "anticheat"))
    p.add_argument("--once", action="store_true", help="한 번만 쓰고 종료 (테스트용)")
    return p.parse_args()


def log(level, msg):
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
    print(f'{{"ts":"{ts}","level":"{level}","src":"ban_sync","msg":{json.dumps(msg, ensure_ascii=False)}}}',
          flush=True)


def iso(dt):
    return dt.strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z" if dt else None


class Db:
    def __init__(self, args):
        self.args = args
        self.conn = None

    def connect(self):
        if self.conn is not None:
            try:
                self.conn.ping(reconnect=True)
                return True
            except Exception:
                self.conn = None
        try:
            self.conn = pymysql.connect(
                host=self.args.db_host, port=self.args.db_port,
                user=self.args.db_user, password=self.args.db_pass,
                database=self.args.db_name, charset="utf8mb4",
                autocommit=True,          # ★ 스냅샷 고착 방지
                connect_timeout=5, read_timeout=10,
            )
            log("info", "DB 연결 성공")
            return True
        except Exception as e:
            log("warn", f"DB 연결 실패: {e}")
            self.conn = None
            return False

    def snapshot(self):
        with self.conn.cursor() as cur:
            cur.execute(SQL_NOW)
            db_now = cur.fetchone()[0]
            cur.execute(SQL_BANS)
            bans = [{"id": r[0], "player_uid": r[1], "expires_at": iso(r[2])} for r in cur.fetchall()]
            cur.execute(SQL_KICKS)
            kicks = [{"id": r[0], "player_uid": r[1]} for r in cur.fetchall()]
        return {"db_now": iso(db_now), "bans": bans, "kicks": kicks}


def write_atomic(path, data):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)


_running = True


def _stop(signum, frame):
    global _running
    _running = False
    log("info", f"종료 신호 수신 ({signum})")


def main():
    args = parse_args()
    if not args.db_pass:
        log("warn", "DB 비밀번호가 비어 있습니다 (DB_PASS 환경변수 확인)")

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    signal.signal(signal.SIGINT, _stop)
    signal.signal(signal.SIGTERM, _stop)

    db = Db(args)
    log("info", f"시작: out={args.out} interval={args.interval}s kick_window={KICK_WINDOW_SEC}s")

    last_sig = None
    while _running:
        try:
            if db.connect():
                snap = db.snapshot()
                write_atomic(args.out, snap)

                # 목록이 바뀔 때만 로그
                sig = (tuple(b["id"] for b in snap["bans"]), tuple(k["id"] for k in snap["kicks"]))
                if sig != last_sig:
                    last_sig = sig
                    log("info", f"밴 {len(snap['bans'])}건 {list(sig[0])} / 킥 {len(snap['kicks'])}건 {list(sig[1])}")
        except Exception as e:
            log("error", f"동기화 실패, 기존 파일 유지: {e}")
            db.conn = None

        if args.once:
            break

        waited = 0.0
        while _running and waited < args.interval:
            time.sleep(0.2)
            waited += 0.2

    log("info", "종료")


if __name__ == "__main__":
    main()
