import pandas as pd
import numpy as np

df = pd.read_parquet("analytics/etl/features_v1.parquet")
LAB = "player_label"

def robust_z(series, clean_vals, direction):
    med = np.median(clean_vals)
    mad = np.median(np.abs(clean_vals - med)) * 1.4826
    # MAD 가 너무 작으면(거의 상수인 feature) clean 표준편차로 대체, 그것도 0이면 하한
    if mad < 1e-6:
        sd = np.std(clean_vals)
        mad = sd if sd > 1e-6 else 1.0
    z = (series - med) / mad
    return z * direction

def score(sub, specs, name):
    sub = sub.dropna(subset=[c for c,_ in specs]).copy()
    clean = sub[sub[LAB]=="clean"]
    print(f"\n{'='*64}\n[{name}]  clean {len(clean)} / 전체 {len(sub)}")
    zs = []
    for col, direction in specs:
        z = robust_z(sub[col], clean[col].values, direction)
        sub[f"z_{col}"] = z
        zs.append(z)
    sub["score"] = pd.concat(zs, axis=1).max(axis=1)
    sub = sub.sort_values("score", ascending=False)
    cols = ["match_id","player_id","label",LAB,"score"] + [c for c,_ in specs]
    with pd.option_context("display.width",180,"display.float_format",lambda x:f"{x:.2f}"):
        print(sub[cols].to_string(index=False))
    return sub

score(df[df.aim_hits.notna()],
      [("aim_err_std",-1),("aim_err_mean",-1),("aim_err_min",-1)],
      "에임핵")

# ESP: hi_frac 은 clean 이 거의 0 이라 MAD=0 → mean 위주로. hi_frac 은 보조
score(df[df.occ_windows.notna()],
      [("occ_aim_mean",+1),("occ_aim_max",+1)],
      "ESP")
