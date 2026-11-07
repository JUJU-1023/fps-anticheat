# 플레이어 단위 라벨 보정: 치트 세션이라도 치트 안 쓴 상대(주로 p19)는 clean
import pandas as pd
df = pd.read_parquet("analytics/etl/features_v1.parquet")

# 치트 세션에서 치트 행위자는 거의 player 1. 피해자(그 외)는 clean 행동.
df["player_label"] = df["label"]
mask = (df["label"]=="cheat") & (df["player_id"]!=1)
df.loc[mask, "player_label"] = "clean"

df.to_parquet("analytics/etl/features_v1.parquet", index=False)
print("보정된 행:", mask.sum())
print(df[df.label=="cheat"][["match_id","player_id","label","player_label"]].to_string(index=False))
