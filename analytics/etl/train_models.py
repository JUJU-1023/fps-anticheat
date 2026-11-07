# =====================================================================
#  train_models.py  —  W13 L3 Isolation Forest (ESP · 에임핵 2개 모델)
#  clean 으로만 학습 → 전 세션 이상치 점수 → cheat 가 이상치로 뜨는지 확인
# =====================================================================
import pandas as pd
import numpy as np
from sklearn.ensemble import IsolationForest
from sklearn.preprocessing import RobustScaler
import joblib

df = pd.read_parquet("analytics/etl/features_v1.parquet")

# score_samples: 낮을수록 이상치. 보기 쉽게 부호 뒤집어 "이상치 점수"로.
def train_and_score(sub, feat_cols, name):
    sub = sub.dropna(subset=feat_cols).copy()
    clean = sub[sub.label == "clean"]
    print(f"\n{'='*60}\n[{name}]  feature={feat_cols}")
    print(f"학습 clean {len(clean)}행 / 전체 {len(sub)}행")

    X = sub[feat_cols].values
    Xc = clean[feat_cols].values

    scaler = RobustScaler().fit(Xc)
    model = IsolationForest(n_estimators=200, contamination=0.1,
                            random_state=42).fit(scaler.transform(Xc))

    sub["anomaly"] = -model.score_samples(scaler.transform(X))  # 높을수록 이상
    sub = sub.sort_values("anomaly", ascending=False)

    with pd.option_context("display.width", 160,
                           "display.float_format", lambda x: f"{x:.3f}"):
        print(sub[["match_id","player_id","label","anomaly"]+feat_cols].to_string(index=False))

    joblib.dump({"scaler": scaler, "model": model, "features": feat_cols},
                f"analytics/etl/iforest_{name}.joblib")
    return sub

# ── ESP 탐지기 ──
esp = train_and_score(
    df[df.occ_windows.notna()],
    ["occ_aim_mean","occ_aim_max","occ_aim_hi_frac"],
    "esp")

# ── 에임핵 탐지기 ──
aim = train_and_score(
    df[df.aim_hits.notna()],
    ["aim_err_std","aim_err_mean","aim_err_min"],
    "aimbot")

print(f"\n{'='*60}\n저장: iforest_esp.joblib / iforest_aimbot.joblib")
