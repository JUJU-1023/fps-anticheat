-- W11: movement_samples.input_count TINYINT -> SMALLINT
-- 원인: 클라 정지 후 밀린 입력이 10Hz 창 하나에 몰려 276 관측 → ingester가
--       20261008-131229 파일에서 4시간 20분 막힘(오프셋 유지, 유실 없음)
-- movement_samples는 v1.0 FREEZE 테이블. 타입 확장만 하고 의미는 그대로.
-- 이미 2026-10-09 VM102에 적용됨. 재실행해도 무해.
ALTER TABLE movement_samples MODIFY input_count SMALLINT UNSIGNED NULL;
