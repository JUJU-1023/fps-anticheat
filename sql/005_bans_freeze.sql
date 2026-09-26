-- 005_bans_freeze.sql — schema v1.4
-- W9 Day 6 E2E 통과: 킥 2.0s / 밴 1.7s, 킥 1회 집행, 밴 재접속 거부,
-- enforced_at 첫 시각 보존, VM 재부팅 후 ban_sync 자동 복구 확인
USE anticheat;
INSERT IGNORE INTO schema_meta (version, applied_at, frozen, note) VALUES
('v1.4', NOW(3), 1, 'W9 Day6 E2E 통과. FREEZE: bans. DRAFT 잔여: l1_reports');
