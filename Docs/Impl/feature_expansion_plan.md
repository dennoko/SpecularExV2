# SpecularExV2 機能拡張 実装計画 (Feature Expansion Plan)

[overview.md](overview.md) で定義した既存4機能（スペキュラー 2nd/3rd・MatCap・ノーマル 3rd・リム 2nd）に対し、表現の幅を広げる追加設定を実装するための計画書です。

## 0. 基本方針

- **テクスチャ宣言を増やさない**。むしろ MatCap の Back テクスチャを廃止し、宣言数を **5 → 4** に減らす。
- 新規 SamplerState は 0（既存どおり `sampler_linear_repeat` / `lil_sampler_linear_clamp` を共有）。
- 追加するのはすべて ALU のみ、または既存テクスチャの追加サンプルのみ（MatCap の Back 半球は同一テクスチャの2回目のサンプル）。
- **既定値は「現状と同じ見た目」になる値にする**（例外: リム 2nd のライティング反映。§4.1 参照）。
- **暗い空間でエフェクトが浮かない**ことを優先する。スペキュラー／リムの強さはライティングで制限し、最低輝度を持ち上げる方向の機能は入れない。

### 対象項目一覧

| 機能 | 項目 | 節 |
|---|---|---|
| スペキュラー 2nd/3rd | 光源方向の補正（フェイクライト）＋ライティング反映 | §1.1 |
| スペキュラー 2nd/3rd | クリアコートモード | §1.2 |
| スペキュラー 2nd/3rd | フレネル強度 | §1.3 |
| MatCap | 空間モード View / World / Object | §2.1 |
| MatCap | 1枚テクスチャ化（Back テクスチャ廃止・左右分割レイアウト） | §2.2 |
| MatCap | ~~フレネル強度~~（オミット） | §2.3 |
| MatCap | HSV 調整・メインカラー乗算 | §2.4 |
| ノーマル 3rd | 距離フェード | §3.1 |
| ノーマル 3rd | UV スクロール・回転 | §3.2 |
| ノーマル 4th | 3rd と同じ構成の第4レイヤーを追加 | §3.3 |
| リム 2nd | ライティング反映 | §4.1 |
| リム 2nd | 上下方向の制限 | §4.2 |
| リム 2nd | 逆光ブースト | §4.3 |
| リム 2nd | 境界（Border） | §4.4 |

---

## 1. スペキュラー 2nd / 3rd

2nd と 3rd は同一ロジックです。以下のプロパティ名は `Refl2nd` で記載していますが、`Refl3rd` にも同じものを追加します。

### 1.1 光源方向の補正（フェイクライト）とライティング反映

**目的**: ディレクショナルライトのないワールドでは、`fd.L` が SH の主方向（多くは真上）になり、ハイライトが視界に入らない。そこで光源方向だけをカメラ基準の方向に寄せる。その一方で、**明るさは常にシーンのライティングに従わせ**、暗所で浮かないようにする。

**プロパティ**

| プロパティ | 型 / 範囲 | 既定値 | 説明 |
|---|---|---|---|
| `_CustomRefl2ndFakeLightBlend` | Range(0, 1) | 0 | 実ライト方向 → フェイクライト方向へのブレンド率 |
| `_CustomRefl2ndFakeLightDir` | Vector | (0.3, 0.5, 1, 0) | カメラ基準の光源方向（x=右, y=上, z=視点側） |
| `_CustomRefl2ndEnableLighting` | Range(0, 1) | 1 | ライト色を乗算する度合い（1 = 現状の挙動） |

**計算**（ForwardBase のみ。ForwardAdd は実在の追加ライトなので補正しない）

```hlsl
// カメラ基準 → ワールド。headV（左右の目の中点 → 表面の逆向き）を z 軸にすると VR の左右の目で一致する
float3 fakeL = normalize(dir.x * fd.cameraRight + dir.y * fd.cameraUp + dir.z * fd.headV);
float3 L     = normalize(lerp(fd.L, fakeL, fakeBlend));   // ForwardAdd では fd.L のまま
```

- 明るさは補正前と同じ `fd.lightColor` を使う。**フェイクライトは方向だけを変え、光量は作らない**。
- ライト色の反映:
  `lightFactor = lerp(1, fd.lightColor, EnableLighting)`
- ~~明るさの上限（ライト輝度に対する倍率で輝度を制限する `LightLimit`）~~ — 実装後に**オミット**（ライティング反映のみで扱う）。
- ForwardAdd ではライト色の反映を `lightColor * EnableLighting` とする（加算パスなので MatCap と同じ扱い）。

**実装箇所**: `custom_insert.hlsl` に `DNKW_SpecularLightDir`（パスで分岐）・`DNKW_SpecularLighting` と、2nd/3rd 共通の `DNKW_ApplySpecularLayer` を追加し、`BEFORE_REFLECTION` はそれを呼ぶだけにする。

### 1.2 クリアコートモード

**目的**: 下地の上にコート層がある材質（塗装・ネイル・レジン）を、エネルギー保存に近い形で表現する。

| プロパティ | 型 | 既定値 | 説明 |
|---|---|---|---|
| `_CustomRefl2ndClearCoat` | Float (Toggle) | 0 | クリアコートモード |

**挙動（ON 時）**
- F0 を 0.04 に固定する（Metallic / Reflectance は無視。インスペクターでは非表示にする）。
- 下地を減衰する: `fd.col.rgb *= 1 - F_schlick(0.04, nv) * saturate(strength) * mask`
  - `nv` はスペキュラーに使うのと同じ法線 `_s2N` から計算する。
- その後、スペキュラーを加算する（従来どおり）。
- ForwardAdd でも同じ減衰を掛ける（加算寄与も「コートの下」として扱うため）。

**制限事項**: `BEFORE_REFLECTION` の時点で減衰するので、この後に加算される lilToon 本体の反射・MatCap・リム・エミッションは減衰されない。実用上はこれで十分と判断する。コート層をより厳密に扱う必要が出たら、減衰を `BEFORE_RIMLIGHT` に移すことを検討する。

### 1.3 フレネル強度

| プロパティ | 型 / 範囲 | 既定値 | 説明 |
|---|---|---|---|
| `_CustomRefl2ndFresnelStrength` | Range(0, 1) | 0 | グレージング角（斜めから見たとき）への偏りの強さ |
| `_CustomRefl2ndFresnelPower` | Range(0.5, 10) | 5 | フレネルの鋭さ |

```hlsl
float fres = pow(1.0 - saturate(dot(_s2N, fd.V)), FresnelPower);
contrib *= lerp(1.0, fres, FresnelStrength);
```
§1.1 の上限処理の**前**に掛ける（上限の判定は最終的な寄与に対して行う）。

---

## 2. MatCap

### 2.1 空間モード View / World / Object

> **改訂:** §2.5 で「ワールド固定」0〜1 のブレンドに置き換え、Object モードは廃止した。

既存の `_CustomMatcapWorldFixed`（0/1）を **プロパティ名そのまま** 3値の enum に拡張します。既存マテリアルとアニメーションの互換を保つため、名前は変えません。インスペクター上の表示名は「空間モード」にします。

| 値 | モード | サンプリング方向 |
|---|---|---|
| 0 | View（従来） | `mul(fd.cameraMatrix, N).xy` |
| 1 | World（従来） | `R = reflect(-V, N)`（ワールド） |
| 2 | **Object（新規）** | `R_os = normalize(mul((float3x3)LIL_MATRIX_I_M, R))` |

- Object モードでは、反射がアバターの回転には追従し、カメラの移動には追従しない（オブジェクトに固定された周囲環境の映り込み）。
- Yaw 回転（`_CustomMatcapWorldRotation`）は World / Object の両方で有効にする。Object モードではオブジェクトのローカル Y 軸まわりの回転になる。
- 非一様スケールがあるので `normalize` は必須。

### 2.2 1枚テクスチャ化（Back テクスチャ廃止）

> **改訂:** §2.5 でレイアウト（左右分割）と旧 Back テクスチャの結合機能（`SpecularExMatcapAtlasBaker`）を廃止し、「1枚（後半球は鏡像）」だけにした。

**変更内容**
- `_CustomMatcapBackTex` と `_CustomMatcapBackEnabled` を**削除**し、HLSL の `TEXTURE2D(_CustomMatcapBackTex)` も削除する。**テクスチャ宣言は 5 → 4 になる**。
- `_CustomMatcapFrontTex` は**プロパティ名を維持**する（既存の割り当てを保つため）。表示名は「MatCap テクスチャ」にする。
- 新しいプロパティ:

| プロパティ | 型 | 既定値 | 説明 |
|---|---|---|---|
| `_CustomMatcapLayout` | Float (enum) | 0 | 0 = 単一（後半球は前半球の鏡像）/ 1 = 左右分割（左半分: 前 +Z、右半分: 後 −Z） |

**サンプリング（`DNKW_SampleWorldMatcap` の改修）**
```hlsl
// halfUV: 各半球の 0..1 UV。offset: 前 = 0, 後 = 0.5
float2 DNKW_MatcapAtlasUV(float2 halfUV, float offset, float lod)
{
    // 両半球の境界から滲まないよう、mip レベルに応じて内側へクランプする
    float pad = _CustomMatcapFrontTex_TexelSize.x * exp2(lod) * 2.0;   // 半分領域の UV 単位
    halfUV.x = clamp(halfUV.x, pad, 1.0 - pad);
    return float2(halfUV.x * 0.5 + offset, halfUV.y);
}
```
- 必要な変数は `LIL_CUSTOM_PROPERTIES` に `float4 _CustomMatcapFrontTex_TexelSize;` として追加する（値は Unity が自動で設定する）。
- 前後のクロスフェード（`DNKW_MATCAP_SEAM_WIDTH`）は従来どおり。2回のサンプルが同じテクスチャになるだけ。
- View モードかつ Layout = 1 の場合は、左半分（前半球）だけを使う。
- Layout = 0 の場合は現状の「前半球を鏡像にする」挙動と同一で、サンプルは1回。

**旧マテリアルの移行**
- Unity は、シェーダーから消えたプロパティも `m_SavedProperties.m_TexEnvs` に残す。インスペクターはここを `SerializedObject` で読み、旧 `_CustomMatcapBackTex` にテクスチャが残っていれば HelpBox と「前後を1枚に結合」ボタンを表示する。
- 結合処理: `SpecularExMatcapAtlasBaker`（新規エディタクラス）が GPU Blit で Front と Back を左右に並べる。
  - 高さは大きい方に合わせ、幅は高さ × 2 にする。
  - 出力先: `SpecularExV2_Generated/MatcapAtlas/<hash>.png`（ハッシュ化は `SpecularExPackedMaskStore` と同じ方式）。
  - インポート設定: **sRGB = ON**、Wrap = Clamp、Mipmap = ON。
  - 結合後に `_CustomMatcapFrontTex` へ代入し、`_CustomMatcapLayout = 1` にして、保存済みプロパティから旧エントリを削除する。
- `SyncAllEffective()` から `_CustomMatcapBackEnabled` の同期を削除する。
- ビルドフック（`SpecularExPackedMaskBuildHook`）で、未移行の旧 Back テクスチャが見つかったら警告ログを出す（自動変換はしない。見た目が変わるため、ユーザーの操作で変換してもらう）。

### 2.5 改訂: ワールド固定ブレンド（Layout・Object 廃止）

**方針**
- テクスチャは1枚だけ。ワールド側の後半球は常に前半球の鏡像にする。`_CustomMatcapLayout`、`_CustomMatcapFrontTex_TexelSize`、`DNKW_SampleMatcapHalf` / `DNKW_SampleWorldMatcap` / `DNKW_SampleViewMatcap`、`SpecularEx_MatcapAtlas.shader`、`SpecularExMatcapAtlasBaker`、移行 UI、ビルドフックの警告を削除する。
- 空間モードの選択をやめ、`_CustomMatcapWorldFixed` を **Range(0, 1) のスライダー「ワールド固定」** にする（名前は互換のため維持）。0 = 完全にビュー、1 = 完全にワールド固定。
- Object モードは用途が少ないので廃止。旧マテリアルの値 2 はシェーダーの `saturate` で 1（ワールド固定）になる。

**ブレンド方法（案 B: 参照方向のブレンド）**

検討した案:
- 案 A 色のクロスフェード: 2回サンプリングし、中間値でハイライトが二重に見える（ゴースト）。不採用。
- **案 B 参照方向のブレンド（採用）**: テクスチャを引く前に方向を混ぜ、1回だけサンプリングする。
- 案 C 案 B ＋ グレージング角でワールド寄りにする補正: 必要になったら追加する。

```hlsl
float2 DNKW_MatcapUV(float3 dView, float3 dWorld, float t)
{
    dView.z  = abs(dView.z);            // 前半球に折り返す（鏡像）
    dWorld.z = abs(dWorld.z);
    float3 d = DNKW_SafeNormalize(lerp(dView, dWorld, t), float3(0, 0, 1));
    return d.xy * 0.5 + 0.5;
}
// dView  = mul(fd.cameraMatrix, N)                         （従来のビュー MatCap と同じ）
// dWorld = DNKW_RotateYaw(reflect(-fd.V, N), yaw)          （従来のワールド固定と同じ）
```
- t = 0 / 1 はそれぞれ従来のビュー / ワールド固定（Layout = 0）と一致する。
- 中間値ではハイライトが1つのまま、カメラに遅れて追従する。
- 両方向とも z ≥ 0 に折り返すので、正反対になって打ち消し合うことはほぼない（ゼロ長は `DNKW_SafeNormalize` で回避）。
- `normalize(lerp)` なので t に対する変化は等速ではない。気になる場合は slerp か t のカーブ補正を検討する。

### 2.3 フレネル強度

> **改訂:** 実装後に**オミット**した（`_CustomMatcapFresnelStrength` / `_CustomMatcapFresnelPower` を削除）。

| プロパティ | 型 / 範囲 | 既定値 |
|---|---|---|
| `_CustomMatcapFresnelStrength` | Range(0, 1) | 0 |
| `_CustomMatcapFresnelPower` | Range(0.5, 10) | 3 |

```hlsl
_wmA *= lerp(1.0, pow(1.0 - saturate(dot(_wmN, fd.V)), FresnelPower), FresnelStrength);
```

### 2.4 HSV 調整・メインカラー乗算

| プロパティ | 型 | 既定値 | 説明 |
|---|---|---|---|
| `_CustomMatcapHSVG` | Vector | (0, 1, 1, 1) | 色相・彩度・明度・ガンマ（lilToon の `_MainTexHSVG` と同じ形式） |
| `_CustomMatcapMainColorStrength` | Range(0, 1) | 0 | メインカラー（`fd.albedo`）の乗算度 |

```hlsl
float3 mc = lilToneCorrection(_wmTex.rgb, _CustomMatcapHSVG);
mc *= _CustomMatcapColor.rgb * lerp(1.0.xxx, fd.albedo, MainColorStrength);
```
インスペクターの HSVG は、lilToon 本体と同様に4本のスライダー（Hue −0.5〜0.5 / Saturation 0〜2 / Value 0〜2 / Gamma 0.01〜2）に分けて表示する。

---

## 3. ノーマルマップ 3rd

### 3.1 距離フェード

**目的**: 遠くで細かい法線が生むモアレやちらつき（特に VR）を抑える。

| プロパティ | 型 | 既定値 | 説明 |
|---|---|---|---|
| `_CustomNormal3rdDistanceFade` | Vector | (2, 10, 0, 0) | x = フェード開始距離[m], y = 終了距離[m], z = フェード強度（0 で無効） |

```hlsl
float fade = 1.0 - saturate((fd.depth - df.x) / max(df.y - df.x, 1e-4));
strength *= lerp(1.0, fade, df.z);
```
- `fd.depth`（頭の位置からの距離）は `LIL_GET_POSITION_WS_DATA` で計算され、`BEFORE_AUDIOLINK` より前に確定している。**実装時にパス別に確認する**。
- フェードしきっても、サンプリングは分岐でスキップしない（暗黙微分を使うサンプルを動的分岐の中に入れないため）。

### 3.2 UV スクロール・回転

| プロパティ | 型 | 既定値 | 説明 |
|---|---|---|---|
| `_CustomNormal3rdTex_ScrollRotate` | Vector | (0, 0, 0, 0) | x, y = スクロール速度, z = 角度[rad], w = 回転速度（lilToon の `_MainTex_ScrollRotate` と同じ形式） |

```hlsl
_n3UV = lilCalcUV(_n3UV, _CustomNormal3rdTex_ST, _CustomNormal3rdTex_ScrollRotate);
```
- 既存の `_n3UV * ST.xy + ST.zw` を置き換える。
- マスク（パック済み .b）はスクロールさせない（マスクは固定した領域の指定として使う想定）。
- インスペクターでは角度を度単位で表示し、ラジアンで保存する。lilToon の UV 設定 GUI（`lilEditorGUI` の UV 設定描画）が流用できれば流用する。できなければ自前で描画する。

### 3.3 ノーマルマップ 4th（追加）

3rd と同じ構成のレイヤーを追加し、3rd の結果の上に合成する。

| プロパティ | 内容 |
|---|---|
| `_CustomNormal4thUIEnabled` / `_CustomNormal4thEnabled` | 有効化（テクスチャ未設定なら実効フラグは 0） |
| `_CustomNormal4thTex` | ノーマルマップ（Tiling/Offset 対応） |
| `_CustomNormal4thStrength` | Range(-2, 2) = 1 |
| `_CustomNormal4thTex_UVMode` | UV0〜UV3 |
| `_CustomNormal4thTex_ScrollRotate` | lilToon の ScrollRotate 形式 |
| `_CustomNormal4thDistanceFade` | x = 開始 [m], y = 終了 [m], z = 強度 |
| `_CustomNormal4thMaskTex` | マスク。**パックマスク2の B チャンネル**に格納（マスク用テクスチャは増えない） |

- シェーダー: 3rd の処理を `DNKW_NORMAL_LAYER(tex, st, uvMode, scrollRotate, fade, strength, maskValue)` にまとめ、`BEFORE_AUDIOLINK` で 3rd → 4th の順に呼ぶ。法線の派生値の更新（`DNKW_REFRESH_NORMAL_DERIVED`）はどちらかが有効なときに1回だけ行う。fxc はトークン連結（`##`）で `3rd` を扱えないため、uniform は引数で渡す。
- 負荷: テクスチャ宣言 +1（`_CustomNormal4thTex`）。4th 有効時のみサンプル +2（法線＋マスク）。サンプラーは共有のため増えない。
- インスペクター: 3rd / 4th を共通の `DrawNormalLayer` で描画する。未設定時のヘルプは共通キー `help_normal_missing`（旧 `help_normal3rd_missing`）。

---

## 4. リムライト 2nd

### 4.1 ライティング反映

| プロパティ | 型 / 範囲 | 既定値 |
|---|---|---|
| `_CustomRim2ndEnableLighting` | Range(0, 1) | **1** |

```hlsl
if (_CustomRim2ndBlendMode != 3)   // 乗算（リムシェード）には掛けない
    _r2Color = lerp(_r2Color, _r2Color * fd.lightColor, _CustomRim2ndEnableLighting);
```
- **既定値を 1 にする**（lilToon 本体の `_RimEnableLighting` と同じで、暗所で浮かない側）。既存マテリアルは更新後に暗所でリムが暗くなる。この点を**リリースノートに明記する**。発光リムを維持したい場合は 0 にしてもらう。

### 4.2 上下方向の制限

光源方向に依存しない軽量設計のまま、「上からの光」「下からの照り返し」のような方向性を付けます。

| プロパティ | 型 / 範囲 | 既定値 | 説明 |
|---|---|---|---|
| `_CustomRim2ndVerticalBias` | Range(-1, 1) | 0 | +: 上向きの面だけ / −: 下向きの面だけ / 0: 制限なし |

```hlsl
float d   = dot(_r2N, float3(0, 1, 0));                  // ワールドの上方向
float t   = (_CustomRim2ndVerticalBias >= 0 ? d : -d) * 0.5 + 0.5;
_r2Amt   *= lerp(1.0, t, abs(_CustomRim2ndVerticalBias));
```
- 基準はワールドの上方向にする（アバターが寝転んでも「空の方向」が基準になる）。オブジェクト空間を選べるようにするのは今回の対象外とし、将来の拡張候補とする。

### 4.3 逆光ブースト

| プロパティ | 型 / 範囲 | 既定値 |
|---|---|---|
| `_CustomRim2ndBacklight` | Range(0, 4) | 0 |

```hlsl
float back = saturate(-fd.vl);                             // V と L が逆向き（逆光）で 1
_r2Amt = saturate(_r2Amt * (1.0 + _CustomRim2ndBacklight * back * back));
```
- ディレクショナルライトがない環境では、`fd.L` は SH の主方向になる。明るさは §4.1 のライティング反映で制限されるので、暗所で浮くことはない。

### 4.4 境界（Border）

| プロパティ | 型 / 範囲 | 既定値 |
|---|---|---|
| `_CustomRim2ndBorder` | Range(0, 1) | 0.5 |

既存コードで固定されている `0.5` を `_CustomRim2ndBorder` に置き換えます（既定値 0.5 なので見た目は変わりません）。
```hlsl
_r2Val = saturate((_r2Val - (_CustomRim2ndBorder - _r2Half)) / max(_r2Half * 2.0, fwidth(_r2Val) + 1e-4));
```

---

## 5. 変更ファイルと作業項目

| ファイル | 変更内容 |
|---|---|
| `Shaders/lilCustomShaderProperties.lilblock` | §1〜4 のプロパティを追加。`_CustomMatcapBackTex` と `_CustomMatcapBackEnabled` を削除。`_CustomMatcapWorldFixed` のコメントを3値に更新 |
| `Shaders/custom.hlsl` | `LIL_CUSTOM_PROPERTIES` に追加し、`_CustomMatcapFrontTex_TexelSize` も追加。`LIL_CUSTOM_TEXTURES` から Back を削除。各フックマクロを改修 |
| `Shaders/custom_insert.hlsl` | `DNKW_SpecularLightDir`、`DNKW_SpecularLighting`、`DNKW_ApplySpecularLayer`、`DNKW_FresnelWeight`、`DNKW_ToneCorrection`、`DNKW_MatcapAtlasUV` を追加。`DNKW_SampleWorldMatcap` を1枚テクスチャ対応に改修 |
| `Editor/SpecularExV2Inspector.cs` | プロパティ参照、描画、コピー／ペースト対象リスト、クリアコート時の Metallic/Reflectance 非表示、MatCap 空間モードの Popup 化、旧 Back テクスチャの移行 UI |
| `Editor/SpecularExMatcapAtlasBaker.cs`（新規） | Front+Back の左右結合ベイク（GPU Blit と PNG 保存） |
| `Editor/VRCSDK/SpecularExPackedMaskBuildHook.cs` | 未移行の Back テクスチャがあれば警告 |
| `Editor/Language/ja-JP.json` / `en-US.json` | 新規ラベルキー（下記） |
| `Docs/Impl/overview.md` | パラメーター一覧、§3 テクスチャ構成（宣言数 4）、§5.2 インスペクター構成を更新 |

### 5.1 追加ローカライズキー（案）

`label_fake_light_blend`, `label_fake_light_dir`, `label_enable_lighting`（既存を流用）, `label_clear_coat`, `label_fresnel_strength`, `label_fresnel_power`, `label_matcap_space`, `space_view`, `space_world`, `space_object`, `label_matcap_layout`, `layout_single`, `layout_side_by_side`, `label_hue`, `label_saturation`, `label_value`, `label_gamma`, `label_distance_fade_start`, `label_distance_fade_end`, `label_distance_fade_strength`, `label_uv_scroll`, `label_uv_angle`, `label_uv_rotate_speed`, `label_vertical_bias`, `label_backlight`, `label_border`, `help_matcap_legacy_back`, `button_matcap_bake_atlas`, `help_clear_coat`

### 5.2 インスペクター配置

- **スペキュラー**: 色・強度 ／ タイプ・滑らかさ・金属度・反射率（クリアコート ON 時は金属度と反射率を隠す）・クリアコート ／ フレネル強度・鋭さ ／ **ライティング**（ライティング反映・フェイクライトのブレンドと方向）／ 法線・影・メインカラー・ForwardAdd ／ マスク
- **MatCap**: 空間モード・テクスチャ・レイアウト（World/Object のときだけ表示）／ 色・強度・ブレンド・HSV・メインカラー ／ ぼかし・回転・法線 ／ ライティング・影・裏面 ／ マスク
- **ノーマル 3rd**: ノーマルマップ・強度・UV・スクロール／回転 ／ 距離フェード ／ マスク
- **リム 2nd**: 色・強度・ブレンド・ライティング反映 ／ Power・境界・ぼかし ／ 上下制限・逆光 ／ 法線・影・メインカラー ／ マスク

---

## 6. 実装フェーズ

- [x] **Phase A: 互換に影響しない追加（ALU のみ）**
  - スペキュラー §1.1〜1.3、MatCap §2.1・§2.3・§2.4、ノーマル §3.1・§3.2、リム §4.2〜4.4
  - プロパティ、HLSL、インスペクター、ローカライズを一括で
- [x] **Phase B: 見た目が変わる変更**
  - リム §4.1（既定値 1）
  - リリースノートの文案作成（[release_notes_draft.md](release_notes_draft.md)）
- [x] **Phase C: MatCap 1枚テクスチャ化と移行**
  - シェーダー側の Back 削除と Layout 実装
  - `SpecularExMatcapAtlasBaker` と移行 UI、ビルドフックでの警告
- [x] **Phase E: MatCap のレイアウト・Object 廃止とワールド固定ブレンド**（§2.5）
  - シェーダー、プロパティ、インスペクター、ローカライズ、ビルドフック、ドキュメント
  - fxc 全バリアントと Roslyn（Editor / VRCSDK.Editor）のコンパイルを再確認
- [x] **Phase F: ノーマルマップ 4th の追加**（§3.3）
  - シェーダー、プロパティ、マスクパッカー（Pack 2 B）、インスペクター、ローカライズ、ドキュメント
  - fxc 全 32 バリアント、テクスチャ宣言数 5（リフレクションで確認）、Roslyn（Editor / VRCSDK.Editor）
- [ ] **Phase D: 検証**（自動検証は完了。Unity 上の目視確認が残っている）

  **自動検証（完了）**
  - [x] シェーダーのコンパイル: fxc (ps_5_0 / vs_5_0) で lilToon の生成シェーダーと同じインクルード順・全 `LIL_FEATURE_*` 有効の状態を再現し、以下 32 通りがエラー・警告なしでコンパイルできることを確認
    - Forward / ForwardAdd / Meta / ShadowCaster、Opaque / Cutout / Transparent、Outline、Lite、Multi（キーワードなし）、Gem、Fur、Refraction
    - この過程で、Multi のノーマルマップ系キーワードなしバリアントの既存コンパイルエラーを見つけて修正（`LIL_REQUIRE_APP_TANGENT`）
  - [x] テクスチャ宣言数: 全機能 Forward / ForwardAdd の PS がバインドするカスタムテクスチャが 5 → 4 であることを、fxc のリフレクション出力で確認（`_CustomMatcapBackTex` の削除）
  - [x] C# のコンパイル: `SpecularExV2.Editor` と `SpecularExV2.VRCSDK.Editor` を Unity 同梱の Roslyn と Unity 生成 csproj の参照でコンパイルし、エラーがないことを確認
  - [x] ローカライズ: インスペクターが参照するキーが ja-JP / en-US の両方に存在することを確認
  - [x] 既定値での互換性（コードレビュー）: 新しいプロパティはすべて、既定値で従来と同じ計算になる（例外は意図した変更であるリム 2nd のライティング反映のみ）
    - スペキュラー: `FakeLightBlend=0` で `fd.L`、`EnableLighting=1` で `fd.lightColor`、`FresnelStrength=0` で ×1、`ClearCoat=0`
    - MatCap: HSVG は既定値で処理を省略、`MainColorStrength=0` で ×1、`Layout=0` は旧「Back 未設定」時と同一
    - ノーマル 3rd: `ScrollRotate=0` で `lilCalcUV` は従来の `uv*ST.xy+ST.zw` と同一、`DistanceFade.z=0` で ×1
    - リム 2nd: `Border=0.5` で従来の定数と同一、`VerticalBias=0`・`Backlight=0` で ×1

  **Unity 上での目視確認（未実施：実行中の Unity でのアセット再インポートと描画が必要）**
  - [ ] ライティング環境: ディレクショナルライトあり ／ なし（SH のみ）／ 真っ暗（`_LightMinLimit` のみ）／ ポイントライト（ForwardAdd）／ VRCLV
  - [ ] フェイクライト: VR の左右の目でハイライト位置が一致すること、ForwardAdd で補正されないこと
  - [ ] クリアコート: グレージング角で下地が減衰すること、ForwardAdd 加算との整合
  - [ ] MatCap ワールド固定: 0 / 1 が従来のビュー / ワールド固定と一致すること、0.3〜0.7 でハイライトが二重にならず自然に追従すること、R.z = 0 付近で継ぎ目が出ないこと（ぼかし 0 / 5 / 10）
  - [ ] 旧マテリアル: 空間モード 2（Object）のマテリアルがワールド固定として描画されること、アニメーションクリップ内の `_CustomMatcapWorldFixed` の互換
  - [ ] ノーマル 3rd: 距離フェードの境界、スクロール時にマスクが固定されていること
  - [ ] ノーマル 4th: 3rd の上に合成されること、4th だけ有効でも動作すること、4th のマスク（Pack 2 B）が反映されること
  - [ ] リム: 暗所でリムが浮かないこと（ライティング反映）、逆光ブーストで飽和しないこと
  - [ ] VRChat の Build & Publish（Cutout / Transparent で消えないこと）

## 7. 負荷見積り

| 項目 | テクスチャ宣言 | サンプル数（最大） | ALU |
|---|---|---|---|
| 現状 | 5（パックマスク2枚を含む） | MatCap 2 | — |
| 拡張後 | **5**（パックマスク2枚・ノーマル 4th を含む） | MatCap 1、ノーマル 4th 有効時 +2 | 各機能 +10〜30 命令程度。機能 OFF 時は既存の Enabled 分岐でスキップ |
