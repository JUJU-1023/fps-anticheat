-- =====================================================================
--  anticheat 텔레메트리 스키마 v1.0
--  파일   : sql/001_init.sql
--  대상   : MariaDB @ VM102 (100.64.82.15)
--  적용   : sudo mysql < 001_init.sql
--
--  FREEZE 범위
--    확정  : matches / players / movement_samples / violations / session_summary
--    미확정: combat_events / l1_reports  (사격 시스템, L1 Probe 구현 후 확정)
--
--  주의: 계정 생성/GRANT는 이 파일에 넣지 않는다. 비밀번호가 git에 들어간다.
-- =====================================================================

SET NAMES utf8mb4;

CREATE DATABASE IF NOT EXISTS anticheat
  DEFAULT CHARACTER SET utf8mb4
  COLLATE utf8mb4_unicode_ci;

USE anticheat;


-- ---------------------------------------------------------------------
--  schema_meta — freeze 이력. 캡스톤 문서의 근거 자료가 된다.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS schema_meta (
  version     VARCHAR(16)  NOT NULL,
  applied_at  DATETIME(3)  NOT NULL,
  frozen      BOOLEAN      NOT NULL DEFAULT FALSE,
  note        VARCHAR(255) NULL,
  PRIMARY KEY (version)
) ENGINE=InnoDB;


-- ---------------------------------------------------------------------
--  matches — 한 판(세션)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS matches (
  id                BIGINT       NOT NULL AUTO_INCREMENT,
  match_uid         CHAR(36)     NOT NULL,          -- 서버가 생성하는 GUID
  started_at        DATETIME(3)  NOT NULL,
  ended_at          DATETIME(3)  NULL,
  map_name          VARCHAR(64)  NULL,
  tick_rate         SMALLINT     NOT NULL DEFAULT 60,
  server_build_hash CHAR(64)     NULL,

  -- ★ 평가 라벨. W13 TPR/FP 계산의 정답지다. 이게 없으면 측정 자체가 불가능.
  --   clean : 치트 없는 세션
  --   cheat : 치트 하네스가 켜진 세션
  --   mixed : 일부 플레이어만 치트 (개별 라벨은 session_summary.label 참조)
  label             ENUM('clean','cheat','mixed','unknown')
                    NOT NULL DEFAULT 'unknown',

  -- 어떤 치트를 어떤 파라미터로 켰는지 (W11 하네스 설정 스냅샷)
  cheat_config_json TEXT         NULL,
  notes             VARCHAR(255) NULL,

  PRIMARY KEY (id),
  UNIQUE KEY uk_match_uid (match_uid),
  KEY idx_started (started_at),
  KEY idx_label (label)
) ENGINE=InnoDB;


-- ---------------------------------------------------------------------
--  players — 플레이어 식별
--  player_uid: W9 이전에는 클라이언트가 생성해 config에 저장하는 UUID,
--              W9 이후에는 HWID 해시로 대체한다.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS players (
  id           BIGINT      NOT NULL AUTO_INCREMENT,
  player_uid   VARCHAR(64) NOT NULL,
  display_name VARCHAR(32) NULL,
  is_bot       BOOLEAN     NOT NULL DEFAULT FALSE,
  first_seen   DATETIME(3) NOT NULL,
  last_seen    DATETIME(3) NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uk_player_uid (player_uid),
  KEY idx_is_bot (is_bot)
) ENGINE=InnoDB;


-- ---------------------------------------------------------------------
--  movement_samples — 이동 텔레메트리 (10Hz 다운샘플)
--
--  설계 노트
--  1) 저장은 10Hz(6틱마다 1행)지만, 관측은 60Hz다. 직전 샘플 이후 6틱에서
--     계산한 구간 집계(*_max, input_count)를 같은 행에 넣는다.
--     에임 스냅은 50~80ms 안에 끝나므로 10Hz 원시값만으로는 사라진다.
--  2) rtt_ms는 반드시 서버 측정값을 넣는다. 클라이언트 보고값을 넣으면
--     치터가 RTT를 부풀려 관용 범위를 넓힐 수 있다.
--  3) FK를 걸지 않는다. 대량 배치 INSERT에서 FK 검사 비용이 크고,
--     이 테이블은 append-only라 참조 무결성 위험이 낮다.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS movement_samples (
  id           BIGINT      NOT NULL AUTO_INCREMENT,
  match_id     BIGINT      NOT NULL,
  player_id    BIGINT      NOT NULL,
  is_bot       BOOLEAN     NOT NULL DEFAULT FALSE,

  -- 서버 틱과 클라 틱은 0 시점이 달라 서로 다른 시간축이다. 둘 다 남긴다.
  server_tick  INT         NOT NULL,
  client_tick  INT         NOT NULL,
  server_time  DATETIME(3) NOT NULL,

  pos_x        FLOAT       NOT NULL,
  pos_y        FLOAT       NOT NULL,
  pos_z        FLOAT       NOT NULL,
  vel_x        FLOAT       NOT NULL,
  vel_y        FLOAT       NOT NULL,
  vel_z        FLOAT       NOT NULL,
  yaw          FLOAT       NOT NULL,
  pitch        FLOAT       NOT NULL,

  -- InputPayload.buttons 비트: 0=JUMP 1=FIRE 2=CROUCH 3=SPRINT
  buttons      TINYINT UNSIGNED NOT NULL DEFAULT 0,
  grounded     BOOLEAN     NOT NULL DEFAULT TRUE,

  -- --- 60Hz 원본에서 파생한 구간 집계 (L3 feature의 원재료) ---
  yaw_rate_max   FLOAT NULL,             -- deg/s. 에임 스냅 탐지의 핵심
  pitch_rate_max FLOAT NULL,             -- deg/s
  speed_max      FLOAT NULL,             -- m/s. 수평 속도
  input_count    TINYINT UNSIGNED NULL,  -- 구간 내 서버 수신 입력 수.
                                         -- 정상 6, 스피드핵이면 그 이상.

  rtt_ms       SMALLINT UNSIGNED NULL,   -- ★ 서버 측정값만 넣을 것

  PRIMARY KEY (id),
  KEY idx_match_player_tick (match_id, player_id, server_tick),
  KEY idx_time (server_time)
) ENGINE=InnoDB;


-- ---------------------------------------------------------------------
--  violations — L2/L1 검증 위반
--
--  1초 창(window) 단위로 집계해 넣는다. 집계 없이 매 위반을 기록하면
--  스피드핵 하나에 초당 60행이 쌓여 테이블과 Grafana가 모두 무너진다.
--  first_tick~last_tick이 창의 범위, occurrences가 창 안의 발생 횟수다.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS violations (
  id          BIGINT      NOT NULL AUTO_INCREMENT,
  match_id    BIGINT      NOT NULL,
  player_id   BIGINT      NOT NULL,

  code        VARCHAR(16) NOT NULL,      -- V-MOVE-01, V-FIRE-01, ...
  layer       ENUM('L1','L2','L3') NOT NULL DEFAULT 'L2',
  severity    TINYINT     NOT NULL DEFAULT 1,   -- 1=관찰 2=경고 3=차단

  first_tick  INT         NOT NULL,
  last_tick   INT         NOT NULL,
  occurrences INT         NOT NULL DEFAULT 1,
  server_time DATETIME(3) NOT NULL,

  rtt_ms      SMALLINT UNSIGNED NULL,    -- 오탐 분석 시 지연 상관관계 확인용
  detail_json TEXT        NULL,          -- {"actual":..,"max":..} 등

  PRIMARY KEY (id),
  KEY idx_match_code (match_id, code),
  KEY idx_player (player_id),
  KEY idx_time (server_time)
) ENGINE=InnoDB;


-- ---------------------------------------------------------------------
--  combat_events — 전투 이벤트  [FREEZE 대상 아님 / 골격만]
--  사격 시스템 구현 전이라 컬럼이 바뀔 수 있다. 확정은 W7.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS combat_events (
  id              BIGINT      NOT NULL AUTO_INCREMENT,
  match_id        BIGINT      NOT NULL,
  player_id       BIGINT      NOT NULL,
  target_id       BIGINT      NULL,

  event_type      ENUM('FIRE','HIT','KILL','DEATH','SPOT') NOT NULL,
  weapon_id       VARCHAR(16) NULL,

  server_tick     INT         NOT NULL,
  server_time     DATETIME(3) NOT NULL,

  target_dist     FLOAT       NULL,
  is_headshot     BOOLEAN     NULL,
  aim_error_deg   FLOAT       NULL,      -- 발사 시점 조준-표적 각도차
  rewind_tick     INT         NULL,      -- lag compensation 되감기 틱
  time_to_kill_ms INT         NULL,
  spot_event_id   BIGINT      NULL,      -- SPOT -> FIRE 연결 (V-TIME-01)

  rtt_ms          SMALLINT UNSIGNED NULL,

  PRIMARY KEY (id),
  KEY idx_match_player (match_id, player_id, server_tick),
  KEY idx_spot (spot_event_id),
  KEY idx_type (event_type)
) ENGINE=InnoDB;


-- ---------------------------------------------------------------------
--  l1_reports — 클라이언트 Probe heartbeat  [FREEZE 대상 아님 / 골격만]
--  5초 주기. 확정은 W9.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS l1_reports (
  id           BIGINT      NOT NULL AUTO_INCREMENT,
  match_id     BIGINT      NOT NULL,
  player_id    BIGINT      NOT NULL,
  server_time  DATETIME(3) NOT NULL,

  -- 비트마스크: 0=debugger 1=susp_dll 2=harmony_patch 3=hash_mismatch ...
  flags        INT UNSIGNED NOT NULL DEFAULT 0,
  client_build_hash CHAR(64) NULL,
  detail_json  TEXT        NULL,

  PRIMARY KEY (id),
  KEY idx_match_player_time (match_id, player_id, server_time)
) ENGINE=InnoDB;


-- ---------------------------------------------------------------------
--  session_summary — 세션×플레이어 집계 (L3 학습 입력)
--  세션 종료 시 1행. 전투 컬럼은 사격 구현 전까지 NULL로 남는다.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS session_summary (
  id           BIGINT  NOT NULL AUTO_INCREMENT,
  match_id     BIGINT  NOT NULL,
  player_id    BIGINT  NOT NULL,
  is_bot       BOOLEAN NOT NULL DEFAULT FALSE,

  -- 플레이어 단위 라벨. matches.label='mixed'일 때 여기서 갈린다.
  label        ENUM('clean','cheat','unknown') NOT NULL DEFAULT 'unknown',
  duration_sec INT     NULL,

  -- --- 이동 / 네트워크 (지금 채워짐) ---
  mean_rtt_ms    INT   NULL,
  p95_rtt_ms     INT   NULL,
  reconcile_rate FLOAT NULL,   -- %. 정상 시뮬레이션에서는 0에 수렴
  mean_speed     FLOAT NULL,
  p95_yaw_rate   FLOAT NULL,   -- deg/s
  max_yaw_rate   FLOAT NULL,
  jump_count     INT   NULL,
  sample_count   INT   NULL,

  -- --- 전투 (사격 구현 후 채워짐) ---
  shots                INT   NULL,
  hits                 INT   NULL,
  kills                INT   NULL,
  deaths               INT   NULL,
  accuracy             FLOAT NULL,
  headshot_ratio       FLOAT NULL,
  median_reaction_ms   INT   NULL,
  median_aim_error_deg FLOAT NULL,

  -- --- 판정 ---
  violation_count INT NOT NULL DEFAULT 0,
  l1_flag_count   INT NOT NULL DEFAULT 0,
  trust_score     FLOAT NULL,   -- W13에서 채움

  PRIMARY KEY (id),
  UNIQUE KEY uk_match_player (match_id, player_id),
  KEY idx_label (label),
  KEY idx_is_bot (is_bot)
) ENGINE=InnoDB;


-- ---------------------------------------------------------------------
--  freeze 기록
-- ---------------------------------------------------------------------
INSERT INTO schema_meta (version, applied_at, frozen, note)
VALUES ('v1.0', NOW(3), TRUE,
        'W6 Day1. FREEZE: matches/players/movement_samples/violations/session_summary. DRAFT: combat_events/l1_reports')
ON DUPLICATE KEY UPDATE applied_at = NOW(3);
