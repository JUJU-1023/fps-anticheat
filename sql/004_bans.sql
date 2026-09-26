-- =====================================================================
-- 004_bans.sql  —  schema v1.3 (DRAFT)
-- W9 Day 1: 대시보드 킥/밴 기능용 bans 테이블 + 대시보드 계정 dash
--
-- 규칙
--   - 해제는 DELETE가 아니라 revoked_at 기록 (이력 보존)
--   - BAN: expires_at NULL = 영구 / KICK: expires_at 항상 NULL
--   - 모든 시각은 DB 시계(UTC) 기준. expires_at은 클라이언트가 계산하지 않고
--     INSERT 시 NOW(3) + INTERVAL ... 으로 넣는다
--   - enforced_at은 게임 서버 집행 로그를 ingester(gs 계정)가 채운다
--
-- dash 권한: 전체 SELECT + bans INSERT + bans.revoked_at UPDATE 만
-- W9 E2E 검증 후 v1.4에서 bans FREEZE 예정
--
-- 적용: 2026-09-26 (W9 Day 1). 전 구문 재실행 안전.
-- 실행: sudo mariadb anticheat < 004_bans.sql
-- =====================================================================

USE anticheat;

CREATE TABLE IF NOT EXISTS bans (
  id           BIGINT AUTO_INCREMENT PRIMARY KEY,
  player_id    BIGINT       NOT NULL,
  action       ENUM('KICK','BAN') NOT NULL,
  reason       VARCHAR(255) NOT NULL,
  violation_id BIGINT       NULL,
  created_at   DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  expires_at   DATETIME(3)  NULL,
  revoked_at   DATETIME(3)  NULL,
  enforced_at  DATETIME(3)  NULL,
  created_by   VARCHAR(32)  NOT NULL DEFAULT 'dashboard',
  KEY idx_player (player_id),
  KEY idx_created (created_at)
);

CREATE USER IF NOT EXISTS 'dash'@'100.%' IDENTIFIED BY '1234';
GRANT SELECT ON anticheat.* TO 'dash'@'100.%';
GRANT INSERT ON anticheat.bans TO 'dash'@'100.%';
GRANT UPDATE (revoked_at) ON anticheat.bans TO 'dash'@'100.%';

INSERT IGNORE INTO schema_meta (version, applied_at, frozen, note) VALUES
('v1.3', NOW(3), 0, 'W9 Day1. 신규 bans(킥/밴). DRAFT: bans, l1_reports');
