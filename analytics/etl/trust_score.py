# =====================================================================
#  trust_score.py — W13 L3 Trust Score 통합
#    에임핵 z + ESP z → 플레이어별 Trust(0~100) + 4단계
#    clean 기준(median/MAD)과 임계를 l3_model.json 으로 저장(FastAPI 재사용)
# =====================================================================
import json
import numpy as np
import pandas as pd

df = pd.read_parquet("analytics/etl/features_v1.parquet")
LAB = "player_label"

AIM_FEATS = [("aim_err_std",-1), ("aim_err_mean",-1), ("aim_err_min",-1)]
ESP_FEATS = [("occ_aim_mean",+1), ("occ_aim_max",+1)]
AIM_TIER = {"watch":1.3, "shadow":1.6, "banned":2.0}
ESP_TIER = {"watch":2.0, "shadow":4.0, "banned":6.0}
TIERS = ["normal","watch","shadow","banned"]

clean = df[df[LAB]=="clean"]

def fit(feats):
    p = {}
    for col,_ in feats:
        v = clean[col].dropna().values
        med = float(np.median(v))
        mad = float(np.median(np.abs(v-med))*1.4826)
        if mad < 1e-6:
            sd = float(np.std(v)); mad = sd if sd>1e-6 else 1.0
        p[col] = {"median":med, "mad":mad}
    return p

def dscore(row, feats, params):
    zs=[]
    for col,d in feats:
        x=row[col]
        if pd.isna(x): continue
        zs.append((x-params[col]["median"])/params[col]["mad"]*d)
    return max(zs) if zs else np.nan

def tier(s, cut):
    if pd.isna(s): return 0
    if s>=cut["banned"]: return 3
    if s>=cut["shadow"]: return 2
    if s>=cut["watch"]:  return 1
    return 0

aim_p, esp_p = fit(AIM_FEATS), fit(ESP_FEATS)
df["aim_z"] = df.apply(lambda r: dscore(r,AIM_FEATS,aim_p), axis=1)
df["esp_z"] = df.apply(lambda r: dscore(r,ESP_FEATS,esp_p), axis=1)
df["tier_i"] = df.apply(lambda r: max(tier(r["aim_z"],AIM_TIER),
                                      tier(r["esp_z"],ESP_TIER)), axis=1)
df["tier"] = df["tier_i"].map(lambda i: TIERS[i])

def trust(r):
    na = max(0,r["aim_z"])/AIM_TIER["banned"] if not pd.isna(r["aim_z"]) else 0
    ne = max(0,r["esp_z"])/ESP_TIER["banned"] if not pd.isna(r["esp_z"]) else 0
    return round(100*(1-min(1,max(na,ne))))
df["trust"] = df.apply(trust, axis=1)

out = df.sort_values(["tier_i","trust"], ascending=[False,True])
cols=["match_id","player_id","label",LAB,"trust","tier","aim_z","esp_z"]
with pd.option_context("display.width",180,"display.float_format",lambda x:f"{x:.2f}"):
    print(out[cols].to_string(index=False))

json.dump({"aim":{"feats":AIM_FEATS,"params":aim_p,"tier":AIM_TIER},
           "esp":{"feats":ESP_FEATS,"params":esp_p,"tier":ESP_TIER}},
          open("analytics/etl/l3_model.json","w"), ensure_ascii=False, indent=2)
print("\n저장: analytics/etl/l3_model.json")
