-- =====================================================================
--  anticheat 텔레메트리 스키마 v1.1
--  파일   : sql/002_combat_freeze.sql
--  대상   : MariaDB @ VM102 (100.64.82.15)
--  적용   : sudo mysql < 002_combat_freeze.sql
--
--  W6.5 에서 사격 시스템을 구현하면서 combat_events 를 확정한다.
--  v1.0 에서는 사격이 없어 초안 상태로 두었던 테이블이다.
--
--  주요 변경
--   1) rewind_tick -> rewind_ms
--      되감기를 틱이 아니라 실시간(ms)으로 계산한다.
--      클라 틱과 서버 틱이 독립된 시간축이라 틱 표현은 의미가 모호하다.
--      실측 rewind_ms = 101 (RTT 2ms/2 + 보간지연 100ms).
--
--   2) yaw / pitch / expected_recoil_pitch 추가
--      W8 노리코일 핵 탐지의 원재료다.
--      서버가 반동 패턴을 알고 있으므로 "이론 반동 대비 실제 조준각"의
--      차이가 곧 플레이어의 반동 상쇄량이 된다.
--      사람은 과보정/부족보정을 반복해 분산이 크지만
--      노리코일 핵은 오차가 0에 수렴한다.
--      실측 예시 (연사 2~4발):
--        shot 2: pitch 3.887 / 이론누적 0.888
--        shot 3: pitch 3.225 / 이론누적 1.463  -> 상쇄 1.237
--        shot 4: pitch 2.475 / 이론누적 2.125  -> 상쇄 1.413
--      이 컬럼이 없으면 W12 수집 데이터에서 신호가 통째로 사라진다.
--
--   3) client_tick, shot_index 추가
--      shot_index 는 연사 몇 번째 발인지. 반동 인덱스와 같다.
--
--  유지(미사용) 컬럼
--   target_id, aim_error_deg, time_to_kill_ms, spot_event_id
--   W7 에서 V-LOS / V-TIME 구현 시 채운다.
-- =====================================================================

USE anticheat;

-- ---------------------------------------------------------------------
--  combat_events 확정
-- ---------------------------------------------------------------------

ALTER TABLE combat_events
  -- 되감기 단위 변경
  DROP COLUMN rewind_tick,
  ADD COLUMN rewind_ms SMALLINT UNSIGNED NULL
      COMMENT '되감기 시간(ms). RTT/2 + 보간지연' AFTER target_dist,

  -- 클라이언트 시간축
  ADD COLUMN client_tick INT NULL AFTER server_tick,

  -- 연사 인덱스
  ADD COLUMN shot_index SMALLINT UNSIGNED NULL
      COMMENT '연사 중 몇 번째 발(0-based). 반동 인덱스' AFTER weapon_id,

  -- 조준각 (W8 노리코일 탐지용)
  ADD COLUMN yaw   FLOAT NULL COMMENT '발사 시점 조준 yaw',
  ADD COLUMN pitch FLOAT NULL COMMENT '발사 시점 조준 pitch',
  ADD COLUMN expected_recoil_pitch FLOAT NULL
      COMMENT '0~shot_index-1 반동 수직 누적. 실제 pitch 와의 차이가 상쇄량';

-- 연사 단위 조회를 위한 인덱스.
-- W8 에서 "한 연사 묶음의 pitch 궤적"을 뽑을 때 쓴다.
CREATE INDEX idx_burst ON combat_events (match_id, player_id, shot_index);

-- ---------------------------------------------------------------------
--  freeze 기록
-- ---------------------------------------------------------------------

INSERT INTO schema_meta (version, applied_at, frozen, note)
VALUES ('v1.1', NOW(3), TRUE,
        'W6.5. FREEZE: combat_events (사격 시스템 구현 완료). DRAFT 잔여: l1_reports')
ON DUPLICATE KEY UPDATE applied_at = NOW(3);
