# =====================================================================
#  build_features.py  —  W13 L3 feature 추출
#  (match, player) 단위로 ESP·에임핵 feature 를 뽑아 parquet 으로 저장.
# =====================================================================
import pandas as pd
import pymysql

DB = dict(host="100.64.82.15", user="gs", password="1234",
          database="anticheat", charset="utf8mb4")

EXCLUDE_PLAYERS = [13, 14, 15, 16, 17, 18, 21, 22]  # uid 버그 중복 행
OUT = "analytics/etl/features_v1.parquet"


def main():
    conn = pymysql.connect(**DB)
    try:
        esp = pd.read_sql("""
            SELECT match_id, player_id,
                   AVG(occluded_aim_samples/aim_samples)                 AS occ_aim_mean,
                   MAX(occluded_aim_samples/aim_samples)                 AS occ_aim_max,
                   SUM(occluded_aim_samples/aim_samples >= 0.4)/COUNT(*) AS occ_aim_hi_frac,
                   COUNT(*)                                              AS occ_windows
            FROM los_aim_samples
            WHERE aim_samples >= 50
            GROUP BY match_id, player_id
            HAVING occ_windows >= 5
        """, conn)

        aim = pd.read_sql("""
            SELECT match_id, player_id,
                   STDDEV(aim_error_deg) AS aim_err_std,
                   AVG(aim_error_deg)    AS aim_err_mean,
                   MIN(aim_error_deg)    AS aim_err_min,
                   COUNT(*)              AS aim_hits
            FROM combat_events
            WHERE event_type IN ('HIT','KILL') AND aim_error_deg IS NOT NULL
            GROUP BY match_id, player_id
            HAVING aim_hits >= 20
        """, conn)

        labels = pd.read_sql("""
            SELECT id AS match_id, label
            FROM matches
            WHERE label IN ('clean','cheat')
        """, conn)
    finally:
        conn.close()

    feats = pd.merge(esp, aim, on=["match_id","player_id"], how="outer")
    df = labels.merge(feats, on="match_id", how="inner")

    df = df[~df["player_id"].isin(EXCLUDE_PLAYERS)]
    df = df[df["occ_windows"].notna() | df["aim_hits"].notna()].copy()

    df = df.sort_values(["label","match_id","player_id"]).reset_index(drop=True)
    cols = ["match_id","player_id","label",
            "occ_aim_mean","occ_aim_max","occ_aim_hi_frac","occ_windows",
            "aim_err_std","aim_err_mean","aim_err_min","aim_hits"]
    df = df[cols]
    df.to_parquet(OUT, index=False)

    print(f"\n저장: {OUT}  ({len(df)} 행)\n")
    print("라벨 분포:")
    print(df["label"].value_counts().to_string())
    print("\nESP feature 행:", df["occ_windows"].notna().sum(),
          "/ 에임핵 feature 행:", df["aim_hits"].notna().sum())
    print("\n--- 전체 ---")
    with pd.option_context("display.max_rows", None, "display.width", 160,
                           "display.float_format", lambda x: f"{x:.3f}"):
        print(df.to_string(index=False))


if __name__ == "__main__":
    main()
