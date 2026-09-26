-- =====================================================================
-- 003_combat_fire_gap.sql  —  schema v1.2
-- W8 Day 1: combat_events에 발사 간격 컬럼 추가
--   fire_gap_ticks : 반동 인덱스 리셋 판정의 클라 틱 축 간격
--   fire_gap_ms    : 같은 판정의 서버 실시간 축 간격
--   첫 발은 둘 다 NULL
--
-- 실제 ALTER는 W8 Day 1(2026-09)에 DB에 직접 적용됐고, 파일과
-- schema_meta 기록은 W9 Day 1(2026-09-26)에 사후 작성했다.
-- 전 구문이 재실행 안전(IF NOT EXISTS / INSERT IGNORE).
-- 실행: sudo mariadb anticheat < 003_combat_fire_gap.sql
-- =====================================================================

USE anticheat;

ALTER TABLE combat_events
  ADD COLUMN IF NOT EXISTS fire_gap_ticks INT NULL AFTER expected_recoil_pitch,
  ADD COLUMN IF NOT EXISTS fire_gap_ms    INT NULL AFTER fire_gap_ticks;

INSERT IGNORE INTO schema_meta (version, applied_at, frozen, note) VALUES
('v1.2', NOW(3), 1, 'W8 Day1 combat_events에 fire_gap_ticks/fire_gap_ms 추가. W9에 사후 기록');
